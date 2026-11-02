using FreePartition.Core.Operations;
using static FreePartition.Core.Disks.DiskIo;

namespace FreePartition.Core.Disks;

public sealed record PartitionAddRequest(int DiskNumber, long Offset, long Length, FileSystemFormat FileSystem);

/// <summary>Adds an entry to a partition table without touching the partition. Behind an interface so tests never touch a real disk.</summary>
public interface IPartitionAdder
{
    /// <exception cref="InvalidOperationException">The entry was refused before anything was written.</exception>
    void Add(PartitionAddRequest request);
}

/// <summary>
/// Writes one entry into the partition table of a physical disk with IOCTL_DISK_SET_DRIVE_LAYOUT_EX, which is the only
/// thing it writes. New-Partition cannot be used for this: it zeroes the start of the new partition, which destroys the
/// boot sector of the very volume being brought back.
/// </summary>
public sealed class PhysicalPartitionAdder : IPartitionAdder
{
    /// <summary>MBR partition type: 0x07 for NTFS and exFAT, 0x0C for FAT32 (LBA).</summary>
    private static int TypeCode(FileSystemFormat fileSystem) => fileSystem == FileSystemFormat.FAT32 ? 0x0C : 0x07;

    public void Add(PartitionAddRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var disk = OpenDevice($@"\\.\PhysicalDrive{request.DiskNumber}", GenericRead | GenericWrite);
        DriveLayout layout;
        try
        {
            layout = DriveLayout.Parse(PhysicalDiskMover.ReadLayout(disk), ReadLength(disk));
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                $"Disk {request.DiskNumber} has no usable partition table ({ex.Message}). Initialize it first (as MBR or GPT), then restore the partitions.", ex);
        }
        byte[] updated = layout.WithAddedPartition(request.Offset, request.Length, TypeCode(request.FileSystem), ReadSectorSize(disk));

        try
        {
            PhysicalDiskMover.SetLayout(disk, updated);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Windows blocked FreePartition from writing to the disk, most likely Controlled folder access " +
                "(ransomware protection) or an antivirus. Nothing was changed. Allow FreePartition.exe in Windows " +
                "Security > Virus & threat protection > Ransomware protection > Allow an app through Controlled " +
                "folder access, then queue the restore again.");
        }
        Control(disk, IoctlDiskUpdateProperties);
    }
}
