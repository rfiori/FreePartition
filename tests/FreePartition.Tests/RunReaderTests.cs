using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

/// <summary>
/// RawVolume needs a real disk, so these tests only use sparse runs and boundary cases,
/// which never touch the volume. Reading real clusters is covered by manual VHD testing.
/// </summary>
public class RunReaderTests
{
    private const int Cluster = 4096;

    private static RunReader Reader(long size, params DataRun[] runs) => new(null!, runs, Cluster, size);

    private static byte[] Filled(int length) => Enumerable.Repeat((byte)0xFF, length).ToArray();

    [Fact]
    public void Read_SparseRun_FillsTheBufferWithZeros()
    {
        var reader = Reader(Cluster, new DataRun(-1, 1));
        byte[] buffer = Filled(100);

        int read = reader.Read(0, buffer);

        Assert.Equal(100, read);
        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Read_SpanningTwoSparseRuns_FillsAcrossTheBoundary()
    {
        var reader = Reader(2 * Cluster, new DataRun(-1, 1), new DataRun(-1, 1));
        byte[] buffer = Filled(200);

        int read = reader.Read(Cluster - 100, buffer);

        Assert.Equal(200, read);
        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Read_StartingInsideALaterRun_Works()
    {
        var reader = Reader(3 * Cluster, new DataRun(-1, 1), new DataRun(-1, 2));
        byte[] buffer = Filled(50);

        int read = reader.Read(2 * Cluster + 10, buffer);

        Assert.Equal(50, read);
        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Read_IsClampedToTheLogicalSize_EvenWhenTheRunIsLarger()
    {
        var reader = Reader(100, new DataRun(-1, 1));
        byte[] buffer = Filled(500);

        int read = reader.Read(0, buffer);

        Assert.Equal(100, read);
        Assert.All(buffer.Take(100), b => Assert.Equal(0, b));
        Assert.All(buffer.Skip(100), b => Assert.Equal(0xFF, b));    // untouched past the end
    }

    [Theory]
    [InlineData(100)]    // exactly at the end
    [InlineData(101)]    // past the end
    [InlineData(1_000_000)]
    public void Read_AtOrPastTheEnd_ReturnsZero(long offset)
    {
        var reader = Reader(100, new DataRun(-1, 1));

        Assert.Equal(0, reader.Read(offset, new byte[10]));
    }

    [Fact]
    public void Read_IntoAnEmptyBuffer_ReturnsZero()
    {
        var reader = Reader(100, new DataRun(-1, 1));

        Assert.Equal(0, reader.Read(0, Span<byte>.Empty));
    }

    [Fact]
    public void Read_WithNoRuns_ReturnsZero()
    {
        var reader = Reader(100);

        Assert.Equal(0, reader.Read(0, new byte[10]));
    }

    [Fact]
    public void Read_WhenRunsEndBeforeTheLogicalSize_ReturnsOnlyWhatTheRunsCover()
    {
        // Size says 3 clusters but only 1 is mapped (damaged or incomplete run list).
        var reader = Reader(3 * Cluster, new DataRun(-1, 1));
        byte[] buffer = Filled(2 * Cluster);

        int read = reader.Read(0, buffer);

        Assert.Equal(Cluster, read);
    }

    [Fact]
    public void Size_ReportsTheLogicalSize()
    {
        Assert.Equal(12345, Reader(12345).Size);
    }
}
