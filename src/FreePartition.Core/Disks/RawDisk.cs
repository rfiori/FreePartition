using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace FreePartition.Core.Disks;

/// <summary>
/// READ-ONLY access to a whole physical disk (\\.\PhysicalDriveN). The disk only accepts reads that start and end on
/// its logical sector, so this type does the alignment. Requires running as administrator.
/// </summary>
public sealed class RawDisk : IDisposable
{
    private readonly SafeFileHandle _handle;

    public int DiskNumber { get; }
    public long Length { get; }
    public int SectorSize { get; }

    private RawDisk(SafeFileHandle handle, int diskNumber, long length, int sectorSize)
    {
        _handle = handle;
        DiskNumber = diskNumber;
        Length = length;
        SectorSize = sectorSize;
    }

    public static RawDisk Open(int diskNumber)
    {
        if (diskNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(diskNumber), "Invalid disk number.");

        var handle = DiskIo.OpenDevice($@"\\.\PhysicalDrive{diskNumber}", DiskIo.GenericRead);
        try
        {
            return new RawDisk(handle, diskNumber, DiskIo.ReadLength(handle), DiskIo.ReadSectorSize(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads bytes from any offset. Returns how many bytes were read: fewer than requested only at the end of the disk.
    /// </summary>
    public int Read(long offset, Span<byte> destination)
    {
        if (destination.IsEmpty || offset < 0 || offset >= Length) return 0;

        long start = offset / SectorSize * SectorSize;
        long end = Math.Min(Length, (offset + destination.Length + SectorSize - 1) / SectorSize * SectorSize);
        int size = checked((int)(end - start));

        byte[] temp = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int total = 0;
            while (total < size)
            {
                int n = RandomAccess.Read(_handle, temp.AsSpan(total, size - total), start + total);
                if (n == 0) break;
                total += n;
            }

            int skip = (int)(offset - start);
            int copy = Math.Min(Math.Max(0, total - skip), destination.Length);
            temp.AsSpan(skip, copy).CopyTo(destination);
            return copy;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    public void Dispose() => _handle.Dispose();
}
