using System.Globalization;

namespace FreePartition.Core.Operations;

/// <summary>Smallest and largest size Windows allows for a partition (accounts for unmovable files).</summary>
public sealed record SupportedSize(long MinBytes, long MaxBytes)
{
    /// <summary>Asks Windows for the limits of a partition. Returns null if the query fails.</summary>
    public static async Task<SupportedSize?> QueryAsync(char drive, ICommandExecutor executor, CancellationToken ct = default)
    {
        if (!char.IsAsciiLetter(drive)) return null;

        string script = $"$s = Get-PartitionSupportedSize -DriveLetter {char.ToUpperInvariant(drive)}; \"$($s.SizeMin) $($s.SizeMax)\"";
        try
        {
            var result = await executor.ExecuteAsync(script, ct);
            return result.Success ? Parse(result.Output) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Parses "min max" (two integers separated by whitespace); null if invalid.</summary>
    public static SupportedSize? Parse(string output)
    {
        var parts = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long min)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long max)
            && min > 0 && max >= min)
            return new SupportedSize(min, max);
        return null;
    }
}
