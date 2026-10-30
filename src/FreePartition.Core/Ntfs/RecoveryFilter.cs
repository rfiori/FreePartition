namespace FreePartition.Core.Ntfs;

public enum FileCategory
{
    All,
    Pictures,
    Documents,
    Audio,
    Video,
    Archives,
    Other
}

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    public static FileCategory Of(string fileName)
    {
        string extension = Path.GetExtension(fileName).TrimStart('.');
        return ByExtension.TryGetValue(extension, out var category) ? category : FileCategory.Other;
    }

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory category, string extensions)
        {
            foreach (string extension in extensions.Split(' '))
                map[extension] = category;
        }

        Add(FileCategory.Pictures, "jpg jpeg png gif bmp tif tiff webp heic avif ico svg raw cr2 cr3 nef dng psd psb");
        Add(FileCategory.Documents, "doc docx xls xlsx ppt pptx pdf txt rtf odt ods odp csv md html htm xml json log");
        Add(FileCategory.Audio, "mp3 wav flac aac ogg wma m4a opus mid");
        Add(FileCategory.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp 3g2");
        Add(FileCategory.Archives, "zip rar 7z tar gz bz2 xz iso cab");
        return map;
    }
}

/// <summary>Criteria that narrow the list of deleted files. A null criterion is not applied.</summary>
public sealed record RecoveryFilter
{
    public string? NameContains { get; init; }
    public FileCategory Category { get; init; } = FileCategory.All;
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }

    /// <summary>First day (local time, inclusive) of the modified date.</summary>
    public DateTime? ModifiedFrom { get; init; }

    /// <summary>Last day (local time, inclusive) of the modified date.</summary>
    public DateTime? ModifiedTo { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(NameContains) && Category == FileCategory.All
        && MinSize is null && MaxSize is null && ModifiedFrom is null && ModifiedTo is null;

    public bool Matches(DeletedFile file)
    {
        if (!string.IsNullOrWhiteSpace(NameContains)
            && !file.Name.Contains(NameContains.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (Category != FileCategory.All && FileCategories.Of(file.Name) != Category) return false;
        if (MinSize is { } min && file.Size < min) return false;
        if (MaxSize is { } max && file.Size > max) return false;

        if (ModifiedFrom is not null || ModifiedTo is not null)
        {
            // Files without a date cannot satisfy a date criterion.
            if (file.Modified is not { } modified) return false;
            DateTime day = modified.ToLocalTime().Date;
            if (ModifiedFrom is { } from && day < from.Date) return false;
            if (ModifiedTo is { } to && day > to.Date) return false;
        }

        return true;
    }

    public List<DeletedFile> Apply(IEnumerable<DeletedFile> files) =>
        IsEmpty ? files.ToList() : files.Where(Matches).ToList();
}
