using FreePartition.Core.Disks;

namespace FreePartition.Core.Ntfs;

/// <summary>
/// Reads an NTFS data stream (the MFT itself, $Bitmap or a file) as if it were contiguous,
/// following the list of runs (clusters) on the volume.
/// </summary>
public sealed class RunReader
{
    private readonly RawVolume _volume;
    private readonly IReadOnlyList<DataRun> _runs;
    private readonly int _bytesPerCluster;

    /// <summary>Logical size of the stream in bytes.</summary>
    public long Size { get; }

    public RunReader(RawVolume volume, IReadOnlyList<DataRun> runs, int bytesPerCluster, long size)
    {
        _volume = volume;
        _runs = runs;
        _bytesPerCluster = bytesPerCluster;
        Size = size;
    }

    /// <summary>Reads from a logical offset. Returns how many bytes were filled.</summary>
    public int Read(long logicalOffset, Span<byte> destination)
    {
        long remaining = Size - logicalOffset;
        if (remaining <= 0 || destination.IsEmpty) return 0;

        int total = (int)Math.Min(destination.Length, remaining);
        int bytesRead = 0;
        long runStart = 0;

        foreach (var run in _runs)
        {
            long runSize = run.Clusters * _bytesPerCluster;
            long runEnd = runStart + runSize;

            while (bytesRead < total && logicalOffset + bytesRead >= runStart && logicalOffset + bytesRead < runEnd)
            {
                long insideRun = logicalOffset + bytesRead - runStart;
                int amount = (int)Math.Min(total - bytesRead, runSize - insideRun);
                var part = destination.Slice(bytesRead, amount);

                if (run.IsSparse)
                {
                    part.Clear();
                }
                else
                {
                    int n = _volume.Read(run.Lcn * _bytesPerCluster + insideRun, part);
                    if (n < amount) return bytesRead + n;    // end of volume / read error
                }

                bytesRead += amount;
            }

            runStart = runEnd;
            if (bytesRead >= total) break;
        }

        return bytesRead;
    }
}
