using FreePartition.Core.Util;

namespace FreePartition.Tests;

public class ScanStatusFormatterTests
{
    private const long Gb = 1L << 30;

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(65, "0:01:05")]
    [InlineData(3725, "1:02:05")]
    [InlineData(90000, "25:00:00")]
    public void FormatDuration_ShowsHoursMinutesSeconds(int seconds, string expected) =>
        Assert.Equal(expected, ScanStatusFormatter.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Carving_ShowsBytesPercentSpeedAndEstimate()
    {
        // 10 GB of 100 GB in 100 s: about 102 MB/s, so 900 s (15 min) to go.
        string text = ScanStatusFormatter.Carving(10 * Gb, 100 * Gb, TimeSpan.FromSeconds(100), 2256);

        Assert.Contains($"{10.0:0.0}%", text);   // formatted like the code, so the test holds in any culture
        Assert.Contains("0:01:40 elapsed", text);
        Assert.Contains("about 0:15:00 left", text);
        Assert.Contains("/s", text);
        Assert.Contains($"{2256:N0} file(s) found", text);
    }

    [Fact]
    public void Carving_HidesTheEstimateDuringTheFirstSeconds()
    {
        string text = ScanStatusFormatter.Carving(Gb, 100 * Gb, TimeSpan.FromSeconds(2), 0);

        Assert.DoesNotContain("left", text);
        Assert.Contains("0:00:02 elapsed", text);
    }

    [Fact]
    public void Carving_WithoutAKnownTotal_ShowsOnlyWhatWasRead()
    {
        string text = ScanStatusFormatter.Carving(5 * Gb, 0, TimeSpan.FromSeconds(30), 3);

        Assert.DoesNotContain("%", text);
        Assert.Contains("read", text);
    }

    [Fact]
    public void Carving_NeverPassesOneHundredPercent()
    {
        string text = ScanStatusFormatter.Carving(110 * Gb, 100 * Gb, TimeSpan.FromSeconds(60), 0);

        Assert.Contains($"{100.0:0.0}%", text);
    }
}
