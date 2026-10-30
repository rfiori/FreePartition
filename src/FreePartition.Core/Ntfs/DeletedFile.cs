using FreePartition.Core.Util;

namespace FreePartition.Core.Ntfs;

public enum RecoveryChance
{
    Unavailable,

    /// <summary>The clusters are free, but what is in them is all zeros (a wipe overwrote them): nothing to recover.</summary>
    Overwritten,
    Low,
    Medium,
    High
}

public sealed record DeletedFile(
    long MftRecordNumber,
    string Name,
    string Path,
    long Size,
    DateTime? Modified,
    RecoveryChance Chance,
    double FreeClusterFraction,
    IReadOnlyList<DataRun> Runs,
    byte[]? ResidentData)
{
    public string SizeFormatted => ByteFormatter.Format(Size);

    public string ModifiedFormatted => Modified?.ToLocalTime().ToString("MM/dd/yyyy HH:mm") ?? "";

    public string ChanceText => Chance switch
    {
        RecoveryChance.High => "High",
        RecoveryChance.Medium => "Medium",
        RecoveryChance.Low => "Low",
        RecoveryChance.Overwritten => "Overwritten (zeros)",
        _ => "Unavailable"
    };

    /// <summary>There is data to read, so it can be previewed (even if it is only zeros).</summary>
    public bool IsRecoverable => Chance != RecoveryChance.Unavailable;

    /// <summary>Worth recovering: there is data and it was not wiped.</summary>
    public bool CanRecover => IsRecoverable && Chance != RecoveryChance.Overwritten;
}

/// <param name="Phase">What the scan is doing when it is not reading the MFT, to show instead of the MFT percentage.</param>
public sealed record ScanProgress(long RecordsAnalyzed, long TotalRecords, int DeletedFound, string? Phase = null)
{
    public double Percent => TotalRecords == 0 ? 0 : RecordsAnalyzed * 100.0 / TotalRecords;
}

public sealed record ScanResult(
    string Drive,
    int BytesPerCluster,
    long RecordsAnalyzed,
    TimeSpan Duration,
    IReadOnlyList<DeletedFile> Files);
