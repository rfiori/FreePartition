using System.Text;
using FreePartition.Core.Disks;
using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class WipeVerifierTests
{
    private const int Chunk = WipeVerifier.ChunkBytes;

    private static ReadAt Over(byte[] data) => (offset, destination) =>
    {
        long remaining = data.Length - offset;
        if (remaining <= 0) return 0;
        int n = (int)Math.Min(destination.Length, remaining);
        data.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    };

    private static WipeVerification Scan(byte[] data, long? length = null) =>
        WipeVerifier.Scan(Over(data), length ?? data.Length);

    [Theory]
    [InlineData(1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(10 * 1024 * 1024)]   // more than one read buffer
    public void AllZeros_IsAllZeros(int size)
    {
        var result = Scan(new byte[size]);

        Assert.Equal(WipeVerdict.AllZeros, result.Verdict);
        Assert.Equal(size, result.BytesScanned);
        Assert.Equal(0, result.NonZeroBytes);
        Assert.Equal(-1, result.FirstNonZeroOffset);
        Assert.Equal(result.Chunks, result.ZeroChunks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Chunk * 3 + 17)]
    [InlineData(5 * 1024 * 1024 + 1)]   // in the second buffer
    [InlineData(9_999_999)]             // last byte
    public void OneStrayByte_IsFoundWhereItIs(int at)
    {
        var data = new byte[10_000_000];
        data[at] = 0xAB;

        var result = Scan(data);

        Assert.Equal(WipeVerdict.ResidualData, result.Verdict);
        Assert.Equal(1, result.NonZeroBytes);
        Assert.Equal(at, result.FirstNonZeroOffset);
        Assert.Equal(1, result.Chunks - result.ZeroChunks);
    }

    [Fact]
    public void RandomData_LooksRandom()
    {
        var data = new byte[2 * 1024 * 1024];
        new Random(7).NextBytes(data);

        var result = Scan(data);

        Assert.Equal(WipeVerdict.LooksRandom, result.Verdict);
        Assert.Equal(0, result.ZeroChunks);
    }

    [Fact]
    public void MostlyZerosWithSomeData_IsResidual()
    {
        var data = new byte[2 * 1024 * 1024];
        new Random(7).NextBytes(data.AsSpan(0, 64 * 1024));

        var result = Scan(data);

        Assert.Equal(WipeVerdict.ResidualData, result.Verdict);
        Assert.True(result.ZeroChunkFraction > 0.9);
    }

    [Fact]
    public void ARead_ThatStopsEarly_IsIncomplete()
    {
        var result = WipeVerifier.Scan(Over(new byte[Chunk * 4]), Chunk * 10);

        Assert.Equal(WipeVerdict.Incomplete, result.Verdict);
        Assert.Equal(Chunk * 4, result.BytesScanned);
        Assert.False(result.Complete);
    }

    [Fact]
    public void ARead_ThatReturnsNothing_IsIncomplete() =>
        Assert.Equal(WipeVerdict.Incomplete, WipeVerifier.Scan((_, _) => 0, 1_000_000).Verdict);

    [Fact]
    public void AnEmptyVolume_IsNotAllZeros() =>
        Assert.Equal(WipeVerdict.Incomplete, WipeVerifier.Scan(Over([]), 0).Verdict);

    [Fact]
    public void SurvivingFileSystemSignatures_AreNamedAndCounted()
    {
        var data = new byte[Chunk * 4];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(data, 3);                  // boot sector
        Encoding.ASCII.GetBytes("FILE0").CopyTo(data, Chunk + 1024);          // MFT records
        Encoding.ASCII.GetBytes("FILE0").CopyTo(data, Chunk + 2048);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(data, Chunk * 2 + 0x52);

        var result = Scan(data);

        Assert.Equal(WipeVerdict.ResidualData, result.Verdict);
        Assert.Equal(1, result.Signatures["NTFS boot sector"]);
        Assert.Equal(2, result.Signatures["NTFS MFT record"]);
        Assert.Equal(1, result.Signatures["FAT32 boot sector"]);
    }

    [Fact]
    public void Progress_ReachesOne()
    {
        var reports = new List<double>();
        WipeVerifier.Scan(Over(new byte[10_000_000]), 10_000_000, new SyncProgress(reports.Add));

        Assert.True(reports.Count >= 2);
        Assert.Equal(reports.Order(), reports);   // never goes backwards
        Assert.Equal(1.0, reports[^1]);
    }

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    [Fact]
    public void Cancel_StopsTheScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WipeVerifier.Scan(Over(new byte[Chunk]), Chunk, null, cts.Token));
    }

    [Fact]
    public void ReadsOnlyEverRequestWhatIsLeft()
    {
        long lastEnd = 0;
        ReadAt read = (offset, destination) =>
        {
            lastEnd = Math.Max(lastEnd, offset + destination.Length);
            destination.Clear();
            return destination.Length;
        };

        WipeVerifier.Scan(read, 6_000_000);

        Assert.Equal(6_000_000, lastEnd);   // never past the end of the volume
    }
}
