using FreePartition.Core.Ntfs;

namespace FreePartition.Core.Disks;

public enum CellState : byte
{
    /// <summary>Every sample of the cell is zero.</summary>
    Zero,

    /// <summary>At least one sample holds a byte that is not zero.</summary>
    Data,

    /// <summary>A read of the cell failed or came back short.</summary>
    Unreadable
}

/// <summary>
/// A volume cut into equal cells, each classified from a few samples, so a whole disk can be drawn in seconds.
/// A cell marked <see cref="CellState.Zero"/> is only zero where it was sampled; <see cref="WipeVerifier"/> reads everything.
/// </summary>
public sealed class ClusterMap
{
    public const int SampleBytes = 4096;

    public long Length { get; }
    public long BytesPerCell { get; }
    public CellState[] States { get; }

    /// <summary>Offset of the first non-zero byte seen in each cell, or -1.</summary>
    public long[] FirstDataOffset { get; }

    private ClusterMap(long length, long bytesPerCell, CellState[] states, long[] firstData)
    {
        Length = length;
        BytesPerCell = bytesPerCell;
        States = states;
        FirstDataOffset = firstData;
    }

    public int Cells => States.Length;

    public long CellStart(int cell) => cell * BytesPerCell;

    public long CellEnd(int cell) => Math.Min(Length, (cell + 1L) * BytesPerCell);

    public int CountOf(CellState state) => States.Count(s => s == state);

    /// <param name="maxCells">Upper bound on the number of cells; cells are a multiple of 4 KB each.</param>
    public static ClusterMap Build(ReadAt read, long length, int maxCells = 4096, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCells, 1);

        long perCell = (length + maxCells - 1) / maxCells;
        perCell = (perCell + SampleBytes - 1) / SampleBytes * SampleBytes;
        int cells = (int)((length + perCell - 1) / perCell);

        var states = new CellState[cells];
        var firstData = new long[cells];
        var buffer = new byte[SampleBytes];

        for (int i = 0; i < cells; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long start = i * perCell, end = Math.Min(length, start + perCell);
            firstData[i] = -1;

            foreach (long at in SampleOffsets(start, end))
            {
                int want = (int)Math.Min(SampleBytes, end - at);
                int got = read(at, buffer.AsSpan(0, want));
                if (got < want)
                {
                    states[i] = CellState.Unreadable;
                    break;
                }

                int found = buffer.AsSpan(0, want).IndexOfAnyExcept((byte)0);
                if (found >= 0 && states[i] != CellState.Data)
                {
                    states[i] = CellState.Data;
                    firstData[i] = at + found;
                }
            }

            if (i % 64 == 0 || i == cells - 1) progress?.Report((i + 1.0) / cells);
        }

        return new ClusterMap(length, perCell, states, firstData);
    }

    // Start, middle and end of the cell (each 4 KB), without repeating a spot in a small cell.
    private static IEnumerable<long> SampleOffsets(long start, long end)
    {
        long size = end - start;
        yield return start;
        if (size <= SampleBytes) yield break;

        long last = Math.Max(start, end - SampleBytes);
        long middle = Math.Max(start, (start + size / 2) / SampleBytes * SampleBytes);
        if (middle > start && middle < last) yield return middle;
        if (last > start) yield return last;
    }
}
