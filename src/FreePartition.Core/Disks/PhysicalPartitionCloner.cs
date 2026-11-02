using Microsoft.Win32.SafeHandles;
using FreePartition.Core.Operations;
using static FreePartition.Core.Disks.DiskIo;

namespace FreePartition.Core.Disks;

/// <param name="Drive">The source partition's drive letter: its volume is locked and dismounted while it is read.</param>
public sealed record PartitionCloneRequest(
    int SourceDisk, int SourcePartition, char Drive, long SourceOffset, long Length,
    int DestinationDisk, long DestinationOffset, FileSystemFormat FileSystem);

/// <summary>Does the raw part of "Clone partition". Behind an interface so tests never touch a real disk.</summary>
public interface IPartitionCloner
{
    /// <exception cref="InvalidOperationException">The clone was refused before anything was written.</exception>
    /// <exception cref="OperationCanceledException">Canceled while copying; nothing refers to the copy yet.</exception>
    void Clone(PartitionCloneRequest request, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// Copies a partition, sector by sector, into unallocated space of the same or another disk, then adds it to that
/// disk's partition table. The source is only read (through its own locked volume, so what is copied is consistent)
/// and the copy is written where no partition exists, so a failure at any point leaves the source as it was.
/// Order of events:
/// 1. check the source is the selected partition and that the destination fits in the destination table (nothing written);
/// 2. lock and dismount the source volume;
/// 3. copy the sectors into the free space (canceling here is safe);
/// 4. add the partition table entry for the copy.
/// </summary>
public sealed class PhysicalPartitionCloner : IPartitionCloner
{
    public void Clone(PartitionCloneRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        char letter = char.ToUpperInvariant(request.Drive);

        int sourceSectorSize;
        using (var sourceDisk = OpenDevice($@"\\.\PhysicalDrive{request.SourceDisk}", GenericRead))
            sourceSectorSize = ReadSectorSize(sourceDisk);

        using (var destination = OpenDevice($@"\\.\PhysicalDrive{request.DestinationDisk}", GenericRead | GenericWrite))
        {
            int destinationSectorSize = ReadSectorSize(destination);
            if (destinationSectorSize != sourceSectorSize)
                throw new InvalidOperationException(
                    $"The disks use different sector sizes ({sourceSectorSize} and {destinationSectorSize} bytes): " +
                    "a file system copied between them would not work. Nothing was changed.");
            SectorCopier.EnsureSectorAligned(request.SourceOffset, request.DestinationOffset, request.Length, sourceSectorSize);

            // Dry run on the destination table: refuses an overlap or a position outside the disk before any write.
            CheckDestination(destination, request, destinationSectorSize);

            using var volume = OpenDevice($@"\\.\{letter}:", GenericRead | GenericWrite);
            var extents = ReadVolumeExtents(volume, $"{letter}:");
            if (extents.Count != 1 || extents[0].Disk != request.SourceDisk
                || extents[0].Start != request.SourceOffset || extents[0].Length != request.Length)
                throw new InvalidOperationException(
                    $"{letter}: is not the partition that was selected (the disks changed). Refresh and queue the clone again.");

            progress?.Report($"Locking {letter}: ...");
            if (!Control(volume, FsctlLockVolume))
                throw new InvalidOperationException(
                    $"Could not lock {letter}: — something is using it. Close every program using the drive and try again.");
            if (!Control(volume, FsctlDismountVolume))
                throw new InvalidOperationException($"Could not dismount {letter}:.");
            Control(volume, FsctlAllowExtendedDasdIo);   // the volume's last sectors lie past what the file system reports
            Control(destination, FsctlAllowExtendedDasdIo);

            progress?.Report("Copying the partition. The original is not changed; you can cancel.");
            bool wroteSomething = false;
            int last = -1;
            var copyProgress = new PhysicalDiskMover.SyncProgress(fraction =>
            {
                wroteSomething = true;
                int percent = (int)(fraction * 100);
                if (percent / 5 == last / 5 && percent < 100) return;
                last = percent;
                progress?.Report($"    copied {percent}%");
            });
            try
            {
                SectorCopier.Copy(volume, 0, destination, request.DestinationOffset, request.Length, copyProgress, ct);
            }
            catch (UnauthorizedAccessException) when (!wroteSomething)
            {
                throw new InvalidOperationException(PhysicalDiskMover.WriteBlockedMessage("queue the clone again", letter));
            }
        }

        // The source volume is released here and Windows mounts it again.
        progress?.Report("Adding the copy to the partition table...");
        new PhysicalPartitionAdder().Add(
            new PartitionAddRequest(request.DestinationDisk, request.DestinationOffset, request.Length, request.FileSystem));
        progress?.Report("Partition table updated.");
    }

    private static void CheckDestination(SafeFileHandle destination, PartitionCloneRequest request, int sectorSize)
    {
        DriveLayout layout;
        try
        {
            layout = DriveLayout.Parse(PhysicalDiskMover.ReadLayout(destination), ReadLength(destination));
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                $"Disk {request.DestinationDisk} has no usable partition table ({ex.Message}). Initialize it first (as MBR or GPT), then clone.", ex);
        }
        layout.WithAddedPartition(request.DestinationOffset, request.Length, 0x07, sectorSize);
    }
}
