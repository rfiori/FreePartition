using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace FreePartition.Core.Disks;

/// <summary>
/// Moves a byte range inside one disk (or file), in chunks, correctly when the source and the destination overlap:
/// forward when moving to a lower offset, backward when moving to a higher one, so no byte is overwritten before it
/// has been copied. It does not stop half way: if the process or the power dies in the middle, the data is corrupted.
/// </summary>
public static class SectorCopier
{
    public const int DefaultChunkBytes = 4 << 20;

    /// <param name="progress">Fraction done, 0 to 1.</param>
    public static void Move(SafeFileHandle disk, long source, long destination, long length,
        IProgress<double>? progress = null, int chunkBytes = DefaultChunkBytes)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (source < 0 || destination < 0 || length < 0 || chunkBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Invalid range.");
        if (source == destination || length == 0) return;

        bool forward = destination < source;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkBytes);
        try
        {
            long done = 0;
            while (done < length)
            {
                int size = (int)Math.Min(chunkBytes, length - done);
                long at = forward ? done : length - done - size;

                ReadFully(disk, buffer.AsSpan(0, size), source + at);
                RandomAccess.Write(disk, buffer.AsSpan(0, size), destination + at);

                done += size;
                progress?.Report((double)done / length);
            }
            RandomAccess.FlushToDisk(disk);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadFully(SafeFileHandle disk, Span<byte> destination, long offset)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(disk, destination[total..], offset + total);
            if (n == 0) throw new IOException("Unexpected end of the disk while copying.");
            total += n;
        }
    }
}
