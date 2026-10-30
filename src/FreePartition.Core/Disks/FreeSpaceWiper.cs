using System.Buffers;
using System.Security.Cryptography;

namespace FreePartition.Core.Disks;

/// <summary>
/// Wipes the free space of a mounted volume by filling it with one temporary file and deleting it: the clusters
/// that held deleted files get overwritten while the files that exist stay untouched. It does not reach deleted
/// files small enough to live inside the MFT, nor slack space at the end of existing files.
/// </summary>
public static class FreeSpaceWiper
{
    public const string FileName = "FreePartition-wipe.tmp";

    private const int ChunkBytes = 1 << 20;
    private const int SmallestChunk = 4096;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;

    /// <param name="root">Root of the volume, e.g. "E:\".</param>
    /// <returns>How many bytes were written.</returns>
    public static long Wipe(string root, WipePattern pattern, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        long free = new DriveInfo(root).AvailableFreeSpace;
        string path = Path.Combine(root, FileName);

        // DeleteOnClose: the file goes away even when the fill is canceled or fails.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
            FileOptions.DeleteOnClose);
        File.SetAttributes(file.SafeFileHandle, FileAttributes.Hidden | FileAttributes.Temporary);
        if (pattern == WipePattern.Zeros && File.GetAttributes(file.SafeFileHandle).HasFlag(FileAttributes.Compressed))
            throw new InvalidOperationException(
                "The root folder of the drive is compressed, so zeros would take no space. Use random data instead.");

        return Fill(file, pattern, free, progress, ct);
    }

    /// <summary>Writes into <paramref name="stream"/> until the volume is full, then flushes it to the disk.</summary>
    /// <param name="expected">Free space at the start, only used for the progress.</param>
    public static long Fill(Stream stream, WipePattern pattern, long expected, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!Enum.IsDefined(pattern))
            throw new ArgumentOutOfRangeException(nameof(pattern));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            if (pattern == WipePattern.Zeros)
                buffer.AsSpan(0, ChunkBytes).Clear();

            long written = 0;
            int size = ChunkBytes;
            while (size >= SmallestChunk)
            {
                ct.ThrowIfCancellationRequested();
                if (pattern == WipePattern.Random)
                    RandomNumberGenerator.Fill(buffer.AsSpan(0, size));
                try
                {
                    stream.Write(buffer, 0, size);
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    size /= 2;   // fill the last few clusters with smaller writes
                    continue;
                }
                written += size;
                if (expected > 0)
                    progress?.Report(Math.Min(1.0, (double)written / expected));
            }

            if (stream is FileStream file) file.Flush(flushToDisk: true);
            else stream.Flush();
            progress?.Report(1.0);
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull;
}
