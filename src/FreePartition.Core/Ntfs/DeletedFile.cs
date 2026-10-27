using FreePartition.Core.Util;

namespace FreePartition.Core.Ntfs;

public enum RecoveryChance
{
    Unavailable,
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
        _ => "Unavailable"
    };

    public bool IsRecoverable => Chance != RecoveryChance.Unavailable;
}

public sealed record ScanProgress(long RecordsAnalyzed, long TotalRecords, int DeletedFound)
{
    public double Percent => TotalRecords == 0 ? 0 : RecordsAnalyzed * 100.0 / TotalRecords;
}

public sealed record ScanResult(
    string Drive,
    int BytesPerCluster,
    long RecordsAnalyzed,
    TimeSpan Duration,
    IReadOnlyList<DeletedFile> Files);
