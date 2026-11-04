namespace FreePartition.Core.Util;

/// <summary>Status line of a long scan: how much was read, how fast, and how long it should still take.</summary>
public static class ScanStatusFormatter
{
    /// <summary>The estimate is not shown before this: the first seconds give a wildly wrong speed.</summary>
    private static readonly TimeSpan MinElapsedForEstimate = TimeSpan.FromSeconds(5);

    public static string Carving(long bytesScanned, long totalBytes, TimeSpan elapsed, int filesFound)
    {
        string found = $"{filesFound:N0} file(s) found";
        string time = $"{FormatDuration(elapsed)} elapsed";

        if (totalBytes <= 0 || bytesScanned <= 0)
            return $"Searching for file signatures... {ByteFormatter.Format(bytesScanned)} read — {time} — {found}";

        double percent = Math.Min(100.0, bytesScanned * 100.0 / totalBytes);
        string text = $"Searching for file signatures... {ByteFormatter.Format(bytesScanned)} of {ByteFormatter.Format(totalBytes)} " +
                      $"({percent:0.0}%)";

        if (elapsed >= MinElapsedForEstimate)
        {
            double bytesPerSecond = bytesScanned / elapsed.TotalSeconds;
            var remaining = TimeSpan.FromSeconds(Math.Max(0, totalBytes - bytesScanned) / bytesPerSecond);
            text += $" — {ByteFormatter.Format((long)bytesPerSecond)}/s — {time}, about {FormatDuration(remaining)} left";
        }
        else
        {
            text += $" — {time}";
        }

        return $"{text} — {found}";
    }

    /// <summary>h:mm:ss, with the hours growing past 24 instead of wrapping into days.</summary>
    public static string FormatDuration(TimeSpan duration) =>
        $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
}
