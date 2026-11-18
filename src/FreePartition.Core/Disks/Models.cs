namespace FreePartition.Core.Disks;

/// <summary>Logical volume (drive letter) inside a partition.</summary>
public sealed record LogicalVolumeInfo(
    string Letter,
    string? Label,
    string? FileSystem,
    ulong Size,
    ulong Free,
    string? VolumePath = null,   // "\\?\Volume{guid}\", set for a volume without a drive letter (what the raw volume is opened by)
    bool Unmounted = false);   // Windows has no volume for it (offline disk, automount off): usage read straight from the disk

public sealed record PartitionInfo(
    int Index,
    string Type,
    ulong Offset,
    ulong Size,
    bool Boot,
    IReadOnlyList<LogicalVolumeInfo> Volumes,
    int Number = 0,   // 1-based number the Storage cmdlets use (differs from Index when WMI hides a partition, e.g. MSR)
    IReadOnlyList<string>? Details = null);   // Boot, Page File, Crash Dump, Active, partition kind...

/// <summary>A Microsoft Reserved partition: the Storage stack reports it, but WMI's partition list leaves it out.</summary>
public sealed record ReservedRange(ulong Offset, ulong Size);

public sealed record DiskInfo(
    int Index,
    string Model,
    ulong Size,
    string? MediaType,
    string? Interface,
    string? Status,
    string PartitionStyle,
    IReadOnlyList<PartitionInfo> Partitions,
    bool IsSsd = false,
    IReadOnlyList<ReservedRange>? Reserved = null,   // from the Storage stack (Win32_DiskDrive.MediaType cannot tell SSDs from HDDs)
    bool IsOffline = false);   // Windows mounts none of its volumes and refuses changes until it is brought online
