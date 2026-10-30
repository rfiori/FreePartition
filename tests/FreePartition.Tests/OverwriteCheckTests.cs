using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class OverwriteCheckTests
{
    private const int Sample = OverwriteCheck.SampleBytes;

    /// <summary>A reader over an in-memory file, like RunReader.Read.</summary>
    private static ReadAt Over(byte[] data, Action<long>? onRead = null) => (offset, destination) =>
    {
        onRead?.Invoke(offset);
        long remaining = data.Length - offset;
        if (remaining <= 0) return 0;
        int n = (int)Math.Min(destination.Length, remaining);
        data.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    };

    private static byte[] Zeros(int length) => new byte[length];

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(Sample)]
    [InlineData(Sample + 1)]
    [InlineData(3 * Sample)]
    [InlineData(1_000_000)]
    public void AllZeros_IsOverwritten(int size) =>
        Assert.True(OverwriteCheck.IsZeroFilled(Over(Zeros(size)), size));

    [Theory]
    [InlineData(0)]                  // start
    [InlineData(500_000)]            // middle
    [InlineData(999_999)]            // last byte
    public void AnyNonZeroByteInASample_IsNotOverwritten(int at)
    {
        var data = Zeros(1_000_000);
        data[at] = 1;

        Assert.False(OverwriteCheck.IsZeroFilled(Over(data), data.Length));
    }

    [Fact]
    public void RealDataEverywhere_IsNotOverwritten()
    {
        var data = new byte[200_000];
        new Random(5).NextBytes(data);

        Assert.False(OverwriteCheck.IsZeroFilled(Over(data), data.Length));
    }

    [Fact]
    public void NonZeroDataOutsideTheSamples_IsNotSeen()
    {
        // Documents the limit: only the start, the middle and the end are looked at.
        var data = Zeros(1_000_000);
        data[100_000] = 1;

        Assert.True(OverwriteCheck.IsZeroFilled(Over(data), data.Length));
    }

    [Fact]
    public void OnlyTheThreeSpotsAreRead()
    {
        var offsets = new List<long>();
        var data = Zeros(1_000_000);

        OverwriteCheck.IsZeroFilled(Over(data, offsets.Add), data.Length);

        Assert.Equal([0L, 500_000L, 1_000_000L - Sample], offsets);
    }

    [Fact]
    public void ASmallFile_IsReadOnce()
    {
        var offsets = new List<long>();
        var data = Zeros(100);

        Assert.True(OverwriteCheck.IsZeroFilled(Over(data, offsets.Add), data.Length));
        Assert.Equal([0L], offsets);
    }

    [Fact]
    public void AShortRead_IsNotProofOfAnything()
    {
        ReadAt failing = (_, destination) => destination.Length / 2;

        Assert.False(OverwriteCheck.IsZeroFilled(failing, 1_000_000));
    }

    [Fact]
    public void ARead_ThatReturnsNothing_IsNotOverwritten() =>
        Assert.False(OverwriteCheck.IsZeroFilled((_, _) => 0, 1_000_000));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AnEmptyFile_IsNotOverwritten(long size) =>
        Assert.False(OverwriteCheck.IsZeroFilled(Over(Zeros(10)), size));

    [Fact]
    public void ChanceText_SaysOverwrittenWithZeros()
    {
        var file = new DeletedFile(1, "a.zip", @"\", 10, null, RecoveryChance.Overwritten, 1.0, [], null);

        Assert.Equal("Overwritten (zeros)", file.ChanceText);
        Assert.True(file.IsRecoverable);   // it can still be previewed
        Assert.False(file.CanRecover);     // but there is nothing worth recovering
    }

    [Theory]
    [InlineData(RecoveryChance.High, true)]
    [InlineData(RecoveryChance.Medium, true)]
    [InlineData(RecoveryChance.Low, true)]
    [InlineData(RecoveryChance.Overwritten, false)]
    [InlineData(RecoveryChance.Unavailable, false)]
    public void CanRecover_FollowsTheChance(RecoveryChance chance, bool expected)
    {
        var file = new DeletedFile(1, "a.zip", @"\", 10, null, chance, 1.0, [], null);

        Assert.Equal(expected, file.CanRecover);
    }
}
