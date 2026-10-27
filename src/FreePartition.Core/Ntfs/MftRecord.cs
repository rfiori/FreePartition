namespace FreePartition.Core.Ntfs;

/// <summary>Contiguous stretch of clusters. Lcn &lt; 0 indicates a sparse stretch (no data on disk).</summary>
public readonly record struct DataRun(long Lcn, long Clusters)
{
    public bool IsSparse => Lcn < 0;
}

/// <summary>Summary of an MFT record, with what matters for locating/recovering files.</summary>
public sealed class MftRecord
{
    public long Number { get; init; }
    public bool InUse { get; init; }
    public bool IsDirectory { get; init; }

    public string? Name { get; init; }
    public long ParentNumber { get; init; }
    public DateTime? Modified { get; init; }

    public long RealSize { get; init; }

    /// <summary>Content when the file is small and lives inside the MFT record itself.</summary>
    public byte[]? ResidentData { get; init; }

    public IReadOnlyList<DataRun> Runs { get; init; } = Array.Empty<DataRun>();

    /// <summary>Compressed or encrypted (EFS) file: the raw clusters are not usable as a copy.</summary>
    public bool HasSpecialData { get; init; }

    /// <summary>Has an $ATTRIBUTE_LIST: some attributes live in extension records (not handled yet).</summary>
    public bool HasAttributeList { get; init; }
}
