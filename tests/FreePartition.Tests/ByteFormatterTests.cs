using System.Globalization;
using FreePartition.Core.Util;

namespace FreePartition.Tests;

public class ByteFormatterTests
{
    /// <summary>The formatter uses the current culture's decimal separator; pin it so the tests do not depend on the machine.</summary>
    private static string Invariant(Func<string> format)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return format(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(1UL, "1 B")]
    [InlineData(1023UL, "1023 B")]
    [InlineData(1024UL, "1 KB")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(1048576UL, "1 MB")]
    [InlineData(1073741824UL, "1 GB")]
    [InlineData(1099511627776UL, "1 TB")]
    [InlineData(1125899906842624UL, "1 PB")]
    [InlineData(500UL * 1024 * 1024 * 1024, "500 GB")]
    public void Format_Unsigned_PicksTheLargestFittingUnit(ulong bytes, string expected) =>
        Assert.Equal(expected, Invariant(() => ByteFormatter.Format(bytes)));

    [Fact]
    public void Format_RoundsToTwoDecimals() =>
        Assert.Equal("1.33 KB", Invariant(() => ByteFormatter.Format(1365UL)));

    [Fact]
    public void Format_AbovePetabytes_StaysInPetabytes() =>
        Assert.Equal("2048 PB", Invariant(() => ByteFormatter.Format(1UL << 61)));    // 2^61 bytes = 2048 PB

    [Fact]
    public void Format_MaxValue_DoesNotThrow()
    {
        string text = Invariant(() => ByteFormatter.Format(ulong.MaxValue));

        Assert.EndsWith(" PB", text);
    }

    [Theory]
    [InlineData(-1L, "0 B")]
    [InlineData(long.MinValue, "0 B")]
    [InlineData(0L, "0 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(long.MaxValue, "8192 PB")]    // 2^63 bytes = 8192 PB
    public void Format_Signed_ClampsNegativeValuesToZero(long bytes, string expected) =>
        Assert.Equal(expected, Invariant(() => ByteFormatter.Format(bytes)));
}
