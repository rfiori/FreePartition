using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static FreePartition.Core.Disks.DiskIo;

namespace FreePartition.Core.Disks;

/// <param name="Drive">The partition's drive letter: its volume is locked and dismounted during the move.</param>
public sealed record PartitionMoveRequest(
    int DiskNumber, int PartitionNumber, char Drive, long SourceOffset, long Length, long DestinationOffset);

/// <summary>Does the raw part of "Move partition". Behind an interface so tests never touch a real disk.</summary>
public interface IPartitionMover
{
    /// <exception cref="InvalidOperationException">The move was refused before anything was written.</exception>
    void Move(PartitionMoveRequest request, IProgress<string>? progress);
}

/// <summary>
/// Writes to a physical disk outside the PowerShell cmdlets to move a partition. Order of events, so that the risky
/// window is as short as possible:
/// 1. check the partition table and that the drive letter really is that partition (nothing written yet);
/// 2. lock and dismount the volume (fails if something is using it);
/// 3. copy the sectors (if the power dies here the data is corrupted);
/// 4. rewrite the start of the partition in the partition table; if that fails, copy everything back.
/// </summary>
public sealed class PhysicalDiskMover : IPartitionMover
{
    private const uint IoctlDiskGetDriveLayoutEx = 0x00070050;
    private const uint IoctlDiskSetDriveLayoutEx = 0x0007C054;
    private const int ErrorInsufficientBuffer = 122;

    public void Move(PartitionMoveRequest request, IProgress<string>? progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        char letter = char.ToUpperInvariant(request.Drive);

        using var disk = OpenDevice($@"\\.\PhysicalDrive{request.DiskNumber}", GenericRead | GenericWrite);
        long diskLength = ReadLength(disk);
        var layout = DriveLayout.Parse(ReadLayout(disk), diskLength);
        var partition = layout.CheckMove(request.PartitionNumber, request.SourceOffset, request.Length, request.DestinationOffset);
        SectorCopier.EnsureSectorAligned(request.SourceOffset, request.DestinationOffset, request.Length, ReadSectorSize(disk));

        using var volume = OpenDevice($@"\\.\{letter}:", GenericRead | GenericWrite);
        CheckVolumeIsThePartition(volume, letter, request);

        progress?.Report($"Locking {letter}: ...");
        if (!Control(volume, FsctlLockVolume))
            throw new InvalidOperationException(
                $"Could not lock {letter}: — something is using it. Close every program using the drive and try again.");
        if (!Control(volume, FsctlDismountVolume))
            throw new InvalidOperationException($"Could not dismount {letter}:.");

        Control(disk, FsctlAllowExtendedDasdIo);

        progress?.Report("Copying the data. Do NOT turn off the computer or unplug the disk.");
        int lastReported = -1;
        bool wroteSomething = false;
        var copyProgress = new SyncProgress(fraction =>
        {
            wroteSomething = true;
            int percent = (int)(fraction * 100);
            if (percent / 5 == lastReported / 5 && percent < 100) return;
            lastReported = percent;
            progress?.Report($"    copied {percent}%");
        });
        try
        {
            SectorCopier.Move(disk, request.SourceOffset, request.DestinationOffset, request.Length, copyProgress);
        }
        catch (UnauthorizedAccessException) when (!wroteSomething)
        {
            // Nothing was written yet: Windows (or an antivirus) refused the very first write to the disk.
            throw new InvalidOperationException(WriteBlockedMessage("queue the move again", letter));
        }
        catch (Exception ex) when (wroteSomething && ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"The copy stopped half way ({ex.Message}). The partition {letter}: may be damaged: run a file system " +
                "check on it before using it, and restore from your backup if it does not mount.", ex);
        }

        progress?.Report("Updating the partition table...");
        try
        {
            SetLayout(disk, layout.WithMovedPartition(partition, request.DestinationOffset));
        }
        catch (Exception ex)
        {
            // The table still points at the old position: put the data back so that position is valid again.
            progress?.Report("Could not update the partition table; restoring the original position...");
            SectorCopier.Move(disk, request.DestinationOffset, request.SourceOffset, request.Length);
            throw new IOException($"The partition table could not be updated ({ex.Message}). The original position was restored.", ex);
        }

        Control(disk, IoctlDiskUpdateProperties);
        progress?.Report("Partition table updated.");
    }

    /// <summary>What to tell the user when Windows refuses the very first raw write (nothing was changed).</summary>
    internal static string WriteBlockedMessage(string retry, char letter) =>
        "Windows blocked FreePartition from writing to the disk, most likely Controlled folder access " +
        "(ransomware protection) or an antivirus. Nothing was changed. Allow FreePartition.exe in Windows " +
        "Security > Virus & threat protection > Ransomware protection > Allow an app through Controlled " +
        $"folder access, then {retry}. {letter}: may need to be reopened to be mounted again.";

    internal sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    // ------------------------------------------------------------------ Windows calls

    /// <summary>The drive letter must be exactly the partition we are about to move (one extent, same start).</summary>
    private static void CheckVolumeIsThePartition(SafeFileHandle volume, char letter, PartitionMoveRequest request)
    {
        var extents = ReadVolumeExtents(volume, $"{letter}:");
        if (extents.Count != 1 || extents[0].Disk != request.DiskNumber || extents[0].Start != request.SourceOffset)
            throw new InvalidOperationException(
                $"{letter}: is not the partition that was selected (the disks changed). Refresh and queue the move again.");
    }

    /// <summary>
    /// Reads the partition table of a disk (read-only) the way a move would, so the UI can explain up front why a
    /// disk is not supported (for example MBR with extended partitions).
    /// </summary>
    /// <exception cref="InvalidDataException">The disk layout is not supported.</exception>
    public static DriveLayout LoadLayout(int diskNumber)
    {
        using var disk = OpenDevice($@"\\.\PhysicalDrive{diskNumber}", GenericRead);
        return DriveLayout.Parse(ReadLayout(disk), ReadLength(disk));
    }

    internal static byte[] ReadLayout(SafeFileHandle disk)
    {
        for (int size = 48 + 144 * 128; size <= 1 << 20; size *= 2)
        {
            var buffer = new byte[size];
            if (DeviceIoControl(disk, IoctlDiskGetDriveLayoutEx, default, 0, buffer, size, out int returned, IntPtr.Zero))
                return buffer.AsSpan(0, returned).ToArray();
            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the partition table.");
        }
        throw new InvalidDataException("The partition table is too large.");
    }

    internal static void SetLayout(SafeFileHandle disk, byte[] layout)
    {
        if (!DeviceIoControl(disk, IoctlDiskSetDriveLayoutEx, layout, layout.Length, default, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
}
