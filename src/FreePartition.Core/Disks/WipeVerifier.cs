using System.Text;
using FreePartition.Core.Ntfs;

namespace FreePartition.Core.Disks;

public enum WipeVerdict
{
    /// <summary>The whole volume was read and every byte is zero.</summary>
    AllZeros,

    /// <summary>No zero chunk and about 1 in 256 bytes is zero, as random data looks. Not proof that it is random.</summary>
    LooksRandom,

    /// <summary>Data that is neither zeros nor random-looking is still there.</summary>
    ResidualData,

    /// <summary>The read stopped before the end of the volume, so nothing can be concluded.</summary>
    Incomplete
}

/// <param name="Length">Bytes the volume is supposed to have.</param>
/// <param name="BytesScanned">Bytes actually read; less than <paramref name="Length"/> when a read came back short.</param>
/// <param name="NonZeroBytes">How many bytes are not zero.</param>
/// <param name="FirstNonZeroOffset">Offset of the first byte that is not zero, or -1.</param>
/// <param name="Chunks">Chunks of <see cref="WipeVerifier.ChunkBytes"/> that were read.</param>
/// <param name="ZeroChunks">Chunks that are entirely zeros.</param>
/// <param name="Signatures">File system signatures found at sector starts, with how many times each appeared.</param>
public sealed record WipeVerification(
    long Length, long BytesScanned, long NonZeroBytes, long FirstNonZeroOffset,
    long Chunks, long ZeroChunks, IReadOnlyDictionary<string, long> Signatures)
{
    public bool Complete => BytesScanned >= Length;

    public double ZeroChunkFraction => Chunks == 0 ? 0 : (double)ZeroChunks / Chunks;

    public WipeVerdict Verdict
    {
        get
        {
            if (!Complete || BytesScanned == 0) return WipeVerdict.Incomplete;
            if (NonZeroBytes == 0) return WipeVerdict.AllZeros;

            // Random bytes are zero 1 time in 256 (0.39%) and a 4 KB chunk is never entirely zero.
            double zeroBytes = 1.0 - (double)NonZeroBytes / BytesScanned;
            if (ZeroChunks == 0 && Signatures.Count == 0 && zeroBytes is > 0.002 and < 0.006)
                return WipeVerdict.LooksRandom;
            return WipeVerdict.ResidualData;
        }
    }
}

/// <summary>
/// Reads a whole volume and reports whether it was wiped: how many bytes are not zero, where the first one is, and
/// whether a file system signature (NTFS, exFAT, FAT32, an MFT record, an index block) survived. Read-only.
/// </summary>
public static class WipeVerifier
{
    public const int ChunkBytes = 4096;
    private const int SectorBytes = 512;
    private const int BufferBytes = 4 * 1024 * 1024;   // a multiple of ChunkBytes

    /// <param name="read">Reads from an offset of the volume; returns how many bytes were filled.</param>
    /// <param name="length">Size of the volume in bytes.</param>
    /// <param name="progress">Receives the fraction scanned, from 0 to 1.</param>
    public static WipeVerification Scan(ReadAt read, long length, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var signatures = new Dictionary<string, long>();
        long scanned = 0, nonZero = 0, first = -1, chunks = 0, zeroChunks = 0;
        var buffer = new byte[BufferBytes];

        while (scanned < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int want = (int)Math.Min(BufferBytes, length - scanned);
            int got = read(scanned, buffer.AsSpan(0, want));
            if (got <= 0) break;

            for (int at = 0; at < got; at += ChunkBytes)
            {
                var chunk = buffer.AsSpan(at, Math.Min(ChunkBytes, got - at));
                chunks++;

                int firstInChunk = chunk.IndexOfAnyExcept((byte)0);
                if (firstInChunk < 0)
                {
                    zeroChunks++;
                    continue;
                }

                if (first < 0) first = scanned + at + firstInChunk;
                nonZero += chunk.Length - chunk.Count((byte)0);
                FindSignatures(chunk, signatures);
            }

            scanned += got;
            progress?.Report((double)scanned / length);
            if (got < want) break;   // a short read means the end of the device
        }

        return new WipeVerification(length, scanned, nonZero, first, chunks, zeroChunks, signatures);
    }

    // Offset of each signature inside a sector. Random data matches an 8-byte one with odds of 1 in 2^64.
    private static readonly (string Name, int Offset, byte[] Bytes)[] SectorSignatures =
    [
        ("NTFS boot sector", 3, Encoding.ASCII.GetBytes("NTFS    ")),
        ("exFAT boot sector", 3, Encoding.ASCII.GetBytes("EXFAT   ")),
        ("FAT32 boot sector", 0x52, Encoding.ASCII.GetBytes("FAT32   ")),
        ("NTFS MFT record", 0, Encoding.ASCII.GetBytes("FILE0")),
        ("NTFS index block", 0, Encoding.ASCII.GetBytes("INDX("))
    ];

    private static void FindSignatures(ReadOnlySpan<byte> chunk, Dictionary<string, long> found)
    {
        for (int sector = 0; sector + SectorBytes <= chunk.Length; sector += SectorBytes)
        {
            var span = chunk.Slice(sector, SectorBytes);
            foreach (var (name, offset, bytes) in SectorSignatures)
            {
                if (span.Slice(offset, bytes.Length).SequenceEqual(bytes))
                    found[name] = found.GetValueOrDefault(name) + 1;
            }
        }
    }
}
