using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
/// The one place that WRITES to a physical disk outside the PowerShell cmdlets. Order of events, so that the risky
/// window is as short as possible:
/// 1. check the partition table and that the drive letter really is that partition (nothing written yet);
/// 2. lock and dismount the volume (fails if something is using it);
/// 3. copy the sectors (if the power dies here the data is corrupted);
/// 4. rewrite the start of the partition in the partition table; if that fails, copy everything back.
/// </summary>
public sealed partial class PhysicalDiskMover : IPartitionMover
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    private const uint IoctlDiskGetDriveLayoutEx = 0x00070050;
    private const uint IoctlDiskSetDriveLayoutEx = 0x0007C054;
    private const uint IoctlDiskUpdateProperties = 0x00070140;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint FsctlAllowExtendedDasdIo = 0x00090083;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;

    public void Move(PartitionMoveRequest request, IProgress<string>? progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        char letter = char.ToUpperInvariant(request.Drive);

        using var disk = OpenDevice($@"\\.\PhysicalDrive{request.DiskNumber}", GenericRead | GenericWrite);
        long diskLength = ReadDiskLength(disk);
        var layout = DriveLayout.Parse(ReadLayout(disk), diskLength);
        var partition = layout.CheckMove(request.PartitionNumber, request.SourceOffset, request.Length, request.DestinationOffset);

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
            throw new InvalidOperationException(
                "Windows blocked FreePartition from writing to the disk, most likely Controlled folder access " +
                "(ransomware protection) or an antivirus. Nothing was changed. Allow FreePartition.exe in Windows " +
                "Security > Virus & threat protection > Ransomware protection > Allow an app through Controlled " +
                $"folder access, then queue the move again. {letter}: may need to be reopened to be mounted again.");
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

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    // ------------------------------------------------------------------ Windows calls

    /// <summary>The drive letter must be exactly the partition we are about to move (one extent, same start).</summary>
    private static void CheckVolumeIsThePartition(SafeFileHandle volume, char letter, PartitionMoveRequest request)
    {
        var buffer = new byte[8 + 24 * 8];
        if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, default, 0, buffer, buffer.Length, out _, IntPtr.Zero)
            && Marshal.GetLastPInvokeError() != ErrorMoreData)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not identify the partition of {letter}:.");

        uint count = BitConverter.ToUInt32(buffer, 0);
        int disk = BitConverter.ToInt32(buffer, 8);
        long start = BitConverter.ToInt64(buffer, 16);
        if (count != 1 || disk != request.DiskNumber || start != request.SourceOffset)
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
        return DriveLayout.Parse(ReadLayout(disk), ReadDiskLength(disk));
    }

    private static byte[] ReadLayout(SafeFileHandle disk)
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

    private static void SetLayout(SafeFileHandle disk, byte[] layout)
    {
        if (!DeviceIoControl(disk, IoctlDiskSetDriveLayoutEx, layout, layout.Length, default, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static long ReadDiskLength(SafeFileHandle disk)
    {
        var buffer = new byte[8];
        if (!DeviceIoControl(disk, IoctlDiskGetLengthInfo, default, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the size of the disk.");
        return BitConverter.ToInt64(buffer, 0);
    }

    private static bool Control(SafeFileHandle handle, uint code) =>
        DeviceIoControl(handle, code, default, 0, default, 0, out _, IntPtr.Zero);

    private static SafeFileHandle OpenDevice(string path, uint access)
    {
        var handle = CreateFile(path, access, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not open {path} (run as administrator).");
        }
        return handle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ReadOnlySpan<byte> lpInBuffer, int nInBufferSize,
        Span<byte> lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);
}
