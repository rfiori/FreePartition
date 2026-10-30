namespace FreePartition.Core.Disks;

public enum AlignmentStatus
{
    /// <summary>Starts on a 1 MB boundary, like the partitions Windows creates.</summary>
    Aligned,

    /// <summary>Starts on a 4 KB boundary but not on a 1 MB one: fine for performance, just unusual.</summary>
    AlignedTo4K,

    /// <summary>Not on a 4 KB boundary: writes on SSDs and 4 KB-sector disks straddle two physical sectors.</summary>
    Misaligned
}

/// <summary>
/// Where a partition starts relative to the physical sectors (4 KB) and erase blocks (1 MB is the usual safe bound)
/// of modern disks. Read-only: it only classifies offsets.
/// </summary>
public static class PartitionAlignment
{
    /// <summary>The boundary new and realigned partitions use, the same as Windows.</summary>
    public const ulong Recommended = 1UL << 20;

    /// <summary>The physical sector size of SSDs and Advanced Format disks; below this, alignment hurts performance.</summary>
    public const ulong Minimum = 4096;

    public static AlignmentStatus Classify(ulong offset) =>
        offset % Recommended == 0 ? AlignmentStatus.Aligned
        : offset % Minimum == 0 ? AlignmentStatus.AlignedTo4K
        : AlignmentStatus.Misaligned;

    /// <summary>The first 1 MB boundary at or after <paramref name="offset"/>.</summary>
    public static ulong AlignUp(ulong offset) =>
        offset % Recommended == 0 ? offset : checked((offset / Recommended + 1) * Recommended);

    /// <summary>Short text for the UI.</summary>
    public static string Describe(AlignmentStatus status) => status switch
    {
        AlignmentStatus.Aligned => "Aligned to 1 MB",
        AlignmentStatus.AlignedTo4K => "Aligned to 4 KB (not to 1 MB)",
        _ => "Not aligned to 4 KB: this can slow down SSDs and 4 KB-sector disks"
    };
}
