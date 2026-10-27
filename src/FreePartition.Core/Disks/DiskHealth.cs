namespace FreePartition.Core.Disks;

public enum HealthLevel
{
    Unknown,
    Good,
    Warning,
    Critical,
}

/// <summary>One S.M.A.R.T. attribute of an ATA disk.</summary>
public sealed record SmartAttribute(
    byte Id,
    string Name,
    byte Current,
    byte Worst,
    byte Threshold,
    ulong Raw,
    bool PreFailure)
{
    /// <summary>The normalized value dropped to or below its threshold (a threshold of 0 means "none").</summary>
    public bool Failing => Threshold > 0 && Current <= Threshold;
}

/// <summary>Health report of one physical disk.</summary>
public sealed record DiskHealth(
    int DiskIndex,
    string Model,
    HealthLevel Level,
    string Summary,
    string? HealthStatus,
    bool? PredictFailure,
    int? TemperatureCelsius,
    long? PowerOnHours,
    int? WearPercent,
    IReadOnlyList<SmartAttribute> Attributes,
    IReadOnlyList<KeyValuePair<string, string>> Details,
    string? SmartUnavailable = null);

/// <summary>Parses the raw S.M.A.R.T. tables returned by the Windows ATA storage driver.</summary>
public static class SmartParser
{
    private const int EntryCount = 30;
    private const int EntrySize = 12;
    private const int FirstEntryOffset = 2; // 2-byte structure revision comes first

    public const int AttributeTableLength = FirstEntryOffset + EntryCount * EntrySize;

    private static readonly Dictionary<byte, string> Names = new()
    {
        [1] = "Read error rate",
        [3] = "Spin-up time",
        [4] = "Start/stop count",
        [5] = "Reallocated sector count",
        [7] = "Seek error rate",
        [9] = "Power-on hours",
        [10] = "Spin retry count",
        [12] = "Power cycle count",
        [177] = "Wear leveling count",
        [183] = "Runtime bad block count",
        [187] = "Reported uncorrectable errors",
        [190] = "Airflow temperature",
        [191] = "G-sense error rate",
        [192] = "Power-off retract count",
        [193] = "Load/unload cycle count",
        [194] = "Temperature",
        [196] = "Reallocation event count",
        [197] = "Current pending sector count",
        [198] = "Offline uncorrectable sector count",
        [199] = "UDMA CRC error count",
        [231] = "SSD life left",
        [233] = "Media wearout indicator",
        [241] = "Total LBAs written",
        [242] = "Total LBAs read",
    };

    /// <summary>
    /// Reads the attribute table (2-byte revision + 30 entries of 12 bytes) and merges the thresholds
    /// from the matching threshold table, which uses the same layout. Unused (id 0) entries are skipped.
    /// </summary>
    public static IReadOnlyList<SmartAttribute> ParseAttributes(ReadOnlySpan<byte> data, ReadOnlySpan<byte> thresholds)
    {
        var list = new List<SmartAttribute>();

        for (int i = 0; i < EntryCount; i++)
        {
            int offset = FirstEntryOffset + i * EntrySize;
            if (offset + EntrySize > data.Length) break;

            byte id = data[offset];
            if (id == 0) continue;

            ushort flags = (ushort)(data[offset + 1] | data[offset + 2] << 8);
            ulong raw = 0;
            for (int b = 5; b >= 0; b--)
                raw = raw << 8 | data[offset + 5 + b];

            list.Add(new SmartAttribute(
                Id: id,
                Name: Names.GetValueOrDefault(id) ?? $"Attribute {id}",
                Current: data[offset + 3],
                Worst: data[offset + 4],
                Threshold: FindThreshold(thresholds, id),
                Raw: raw,
                PreFailure: (flags & 0x01) != 0));
        }

        return list;
    }

    private static byte FindThreshold(ReadOnlySpan<byte> thresholds, byte id)
    {
        for (int i = 0; i < EntryCount; i++)
        {
            int offset = FirstEntryOffset + i * EntrySize;
            if (offset + 1 >= thresholds.Length) break;
            if (thresholds[offset] == id) return thresholds[offset + 1];
        }
        return 0;
    }

    /// <summary>Decides the overall level from everything that is known about the disk.</summary>
    public static (HealthLevel Level, string Summary) Evaluate(
        string? healthStatus,
        bool? predictFailure,
        int? temperatureCelsius,
        int? wearPercent,
        IReadOnlyList<SmartAttribute> attributes,
        NvmeHealth? nvme = null)
    {
        bool known = healthStatus is not null || predictFailure is not null || attributes.Count > 0 || nvme is not null;
        if (!known)
            return (HealthLevel.Unknown, "S.M.A.R.T. data is not available for this disk.");

        var critical = new List<string>();
        var warnings = new List<string>();

        if (predictFailure == true) critical.Add("the drive predicts an imminent failure");
        if (string.Equals(healthStatus, "Unhealthy", StringComparison.OrdinalIgnoreCase))
            critical.Add("Windows reports the disk as unhealthy");
        foreach (var a in attributes.Where(a => a.Failing))
            critical.Add($"{a.Name} is at or below its threshold");

        if (nvme is not null)
        {
            if ((nvme.CriticalWarning & 0x1F) != 0) critical.Add($"the drive reports a critical warning (0x{nvme.CriticalWarning:X2})");
            if (nvme.AvailableSparePercent < nvme.SpareThresholdPercent) critical.Add("spare capacity is below its threshold");
            if (nvme.MediaErrors > 0) warnings.Add($"media errors: {nvme.MediaErrors}");
        }

        if (string.Equals(healthStatus, "Warning", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Windows reports a health warning");
        foreach (byte id in new byte[] { 5, 197, 198 })
            if (attributes.FirstOrDefault(a => a.Id == id) is { Raw: > 0 } bad)
                warnings.Add($"{bad.Name}: {bad.Raw}");
        if (wearPercent >= 90) warnings.Add($"{wearPercent}% of the rated endurance is used");
        if (temperatureCelsius >= 60) warnings.Add($"temperature is {temperatureCelsius} °C");

        if (critical.Count > 0)
            return (HealthLevel.Critical, "Back up your data now: " + string.Join("; ", critical) + ".");
        if (warnings.Count > 0)
            return (HealthLevel.Warning, "Attention: " + string.Join("; ", warnings) + ".");
        return (HealthLevel.Good, "No problems detected.");
    }
}
