using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreePartition.Core.Disks;

/// <summary>The raw Windows calls shared by the code that writes to physical disks and volumes.</summary>
internal static partial class DiskIo
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    public const uint IoctlDiskGetLengthInfo = 0x0007405C;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlDiskUpdateProperties = 0x00070140;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    public const uint FsctlLockVolume = 0x00090018;
    public const uint FsctlDismountVolume = 0x00090020;
    public const uint FsctlAllowExtendedDasdIo = 0x00090083;
    private const int ErrorMoreData = 234;

    public static SafeFileHandle OpenDevice(string path, uint access)
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

    public static bool Control(SafeFileHandle handle, uint code) =>
        DeviceIoControl(handle, code, default, 0, default, 0, out _, IntPtr.Zero);

    /// <summary>Size in bytes of a disk or a volume.</summary>
    public static long ReadLength(SafeFileHandle device)
    {
        var buffer = new byte[8];
        if (!DeviceIoControl(device, IoctlDiskGetLengthInfo, default, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the size of the disk.");
        return BitConverter.ToInt64(buffer, 0);
    }

    /// <summary>Logical bytes per sector (DISK_GEOMETRY.BytesPerSector): 512 on most disks, 4096 on 4Kn ones.</summary>
    public static int ReadSectorSize(SafeFileHandle disk)
    {
        var buffer = new byte[256];
        if (!DeviceIoControl(disk, IoctlDiskGetDriveGeometryEx, default, 0, buffer, buffer.Length, out int returned, IntPtr.Zero)
            || returned < 24)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the sector size of the disk.");
        return BitConverter.ToInt32(buffer, 20);
    }

    /// <summary>The (disk number, starting offset, length) pieces a volume is made of; one for a plain partition.</summary>
    public static IReadOnlyList<(int Disk, long Start, long Length)> ReadVolumeExtents(SafeFileHandle volume, string name)
    {
        var buffer = new byte[8 + 24 * 32];
        if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, default, 0, buffer, buffer.Length, out _, IntPtr.Zero)
            && Marshal.GetLastPInvokeError() != ErrorMoreData)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not identify the partition of {name}.");

        int count = (int)Math.Min(BitConverter.ToUInt32(buffer, 0), 32);
        var extents = new List<(int, long, long)>(count);
        for (int i = 0; i < count; i++)
        {
            int at = 8 + i * 24;
            extents.Add((BitConverter.ToInt32(buffer, at), BitConverter.ToInt64(buffer, at + 8), BitConverter.ToInt64(buffer, at + 16)));
        }
        return extents;
    }

    /// <summary>The disks a drive letter lives on (read-only query).</summary>
    public static IReadOnlyList<int> DisksOf(char letter)
    {
        using var volume = OpenDevice($@"\\.\{char.ToUpperInvariant(letter)}:", 0);
        return ReadVolumeExtents(volume, $"{letter}:").Select(e => e.Disk).Distinct().ToList();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ReadOnlySpan<byte> lpInBuffer, int nInBufferSize,
        Span<byte> lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);
}
