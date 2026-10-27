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
    IReadOnlyList<LogicalVolumeInfo> Volumes);

public sealed record DiskInfo(
    int Index,
    string Model,
    ulong Size,
    string? MediaType,
    string? Interface,
    string? Status,
    string PartitionStyle,
    IReadOnlyList<PartitionInfo> Partitions);
