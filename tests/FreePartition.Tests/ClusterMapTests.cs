using FreePartition.Core.Disks;
using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class ClusterMapTests
{
    private static ReadAt Over(byte[] data) => (offset, destination) =>
    {
        long remaining = data.Length - offset;
        if (remaining <= 0) return 0;
        int n = (int)Math.Min(destination.Length, remaining);
        data.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    };

    [Fact]
    public void AllZeros_EveryCellIsZero()
    {
        var map = ClusterMap.Build(Over(new byte[1_000_000]), 1_000_000, 100);

        Assert.All(map.States, s => Assert.Equal(CellState.Zero, s));
        Assert.All(map.FirstDataOffset, o => Assert.Equal(-1, o));
    }

    [Fact]
    public void CellsAreMultiplesOfFourKb_AndCoverTheVolume()
    {
        var map = ClusterMap.Build(Over(new byte[1_000_000]), 1_000_000, 100);

        Assert.Equal(0, map.BytesPerCell % ClusterMap.SampleBytes);
        Assert.True(map.Cells <= 100);
        Assert.Equal(1_000_000, map.CellEnd(map.Cells - 1));
        Assert.True(map.CellStart(map.Cells - 1) < 1_000_000);
    }

    [Fact]
    public void DataInASample_MarksOnlyThatCell()
    {
        var data = new byte[1_000_000];
        var map0 = ClusterMap.Build(Over(data), data.Length, 100);
        int cell = 40;
        long at = map0.CellStart(cell);
        data[at + 10] = 7;

        var map = ClusterMap.Build(Over(data), data.Length, 100);

        Assert.Equal(CellState.Data, map.States[cell]);
        Assert.Equal(at + 10, map.FirstDataOffset[cell]);
        Assert.Equal(1, map.CountOf(CellState.Data));
    }

    [Fact]
    public void DataAtTheVeryEnd_IsSeen()
    {
        var data = new byte[1_000_000];
        data[^1] = 1;

        var map = ClusterMap.Build(Over(data), data.Length, 100);

        Assert.Equal(CellState.Data, map.States[^1]);
    }

    [Fact]
    public void AShortRead_MakesTheCellUnreadable()
    {
        var map = ClusterMap.Build((_, destination) => destination.Length / 2, 100_000, 10);

        Assert.All(map.States, s => Assert.Equal(CellState.Unreadable, s));
    }

    [Fact]
    public void ASmallVolume_IsOneCell()
    {
        var map = ClusterMap.Build(Over(new byte[100]), 100, 4096);

        Assert.Equal(1, map.Cells);
        Assert.Equal(100, map.CellEnd(0));
    }

    [Fact]
    public void SamplingReadsAFewSpotsPerCell_NotTheWholeVolume()
    {
        long read = 0;
        ReadAt counting = (_, destination) => { read += destination.Length; destination.Clear(); return destination.Length; };

        ClusterMap.Build(counting, 10L * 1024 * 1024 * 1024, 4096);

        Assert.True(read <= 4096L * 3 * ClusterMap.SampleBytes);
    }

    [Fact]
    public void Cancel_StopsTheBuild()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ClusterMap.Build(Over(new byte[10_000]), 10_000, 10, null, cts.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveLength_IsRejected(long length) =>
        Assert.ThrowsAny<ArgumentException>(() => ClusterMap.Build((_, _) => 0, length));
}
