using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace FreePartition.Core.Disks;

public enum WipePattern
{
    /// <summary>Every byte becomes 0x00. Can be verified by reading it back.</summary>
    Zeros,

    /// <summary>Cryptographically random bytes, so it is not obvious the area was wiped. Cannot be verified.</summary>
    Random
}

/// <summary>
/// Overwrites a byte range of a disk, volume or file, in chunks. Every read and write starts and ends on the
/// device's sector, which a physical disk requires. Stopping half way is allowed: the area is then partly wiped,
/// which never damages anything outside it.
/// </summary>
public static class SectorWiper
{
    public const int DefaultChunkBytes = 4 << 20;

    /// <param name="verify">Read everything back and check it is zero (only with <see cref="WipePattern.Zeros"/>).</param>
    /// <param name="progress">Fraction done, 0 to 1 (writing and, if asked, verifying).</param>
    /// <exception cref="IOException">The verification found a byte that is not zero.</exception>
    public static void Wipe(SafeFileHandle handle, long start, long length, WipePattern pattern, bool verify, int sectorSize,
        IProgress<double>? progress = null, CancellationToken ct = default, int chunkBytes = DefaultChunkBytes)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!Enum.IsDefined(pattern))
            throw new ArgumentOutOfRangeException(nameof(pattern));
        if (verify && pattern != WipePattern.Zeros)
            throw new ArgumentException("Only a wipe with zeros can be verified.", nameof(verify));
        if (start < 0 || length < 0 || chunkBytes <= 0 || chunkBytes % Math.Max(sectorSize, 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Invalid range.");
        SectorCopier.EnsureSectorAligned(start, start, length, sectorSize);
        if (length == 0) return;

        long work = verify ? 2 * length : length, done = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkBytes);
        try
        {
            if (pattern == WipePattern.Zeros)
                buffer.AsSpan(0, chunkBytes).Clear();

            for (long at = 0; at < length;)
            {
                ct.ThrowIfCancellationRequested();
                int size = (int)Math.Min(chunkBytes, length - at);
                if (pattern == WipePattern.Random)
                    RandomNumberGenerator.Fill(buffer.AsSpan(0, size));

                RandomAccess.Write(handle, buffer.AsSpan(0, size), start + at);
                at += size;
                done += size;
                progress?.Report((double)done / work);
            }
            RandomAccess.FlushToDisk(handle);

            if (!verify) return;
            for (long at = 0; at < length;)
            {
                ct.ThrowIfCancellationRequested();
                int size = (int)Math.Min(chunkBytes, length - at);
                var chunk = buffer.AsSpan(0, size);
                ReadFully(handle, chunk, start + at);
                int bad = chunk.IndexOfAnyExcept((byte)0);
                if (bad >= 0)
                    throw new IOException($"Verification failed: the byte at offset {start + at + bad:N0} is not zero.");
                at += size;
                done += size;
                progress?.Report((double)done / work);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadFully(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(handle, destination[total..], offset + total);
            if (n == 0) throw new IOException("Unexpected end of the disk while verifying.");
            total += n;
        }
    }
}
