namespace FreePartition.Core.Disks;

/// <summary>Logical volume (drive letter) inside a partition.</summary>
public sealed record LogicalVolumeInfo(
    string Letter,
    string? Label,
    string? FileSystem,
    ulong Size,
    ulong Free);

public sealed record PartitionInfo(
    int Index,
    string Type,
    ulong Offset,
    ulong Size,
    bool Boot,
    IReadOnlyList<LogicalVolumeInfo> Volumes,
    int Number = 0,   // 1-based number the Storage cmdlets use (differs from Index when WMI hides a partition, e.g. MSR)
    IReadOnlyList<string>? Details = null);   // Boot, Page File, Crash Dump, Active, partition kind...

public sealed record DiskInfo(
    int Index,
    string Model,
    ulong Size,
    string? MediaType,
    string? Interface,
    string? Status,
    string PartitionStyle,
    IReadOnlyList<PartitionInfo> Partitions,
    bool IsSsd = false);   // from the Storage stack (Win32_DiskDrive.MediaType cannot tell SSDs from HDDs)
