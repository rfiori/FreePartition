namespace FreePartition.Core.Ntfs;

/// <summary>A folder in the tree of deleted files: its subfolders and the files directly inside it.</summary>
public sealed class FolderNode
{
    private readonly Dictionary<string, FolderNode> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DeletedFile> _files = new();

    public FolderNode(string name) => Name = name;

    public string Name { get; }

    /// <summary>Subfolders sorted by name.</summary>
    public IReadOnlyList<FolderNode> Folders { get; private set; } = [];

    /// <summary>Files directly in this folder, sorted by name.</summary>
    public IReadOnlyList<DeletedFile> Files => _files;

    /// <summary>Number of files in this folder and all its subfolders.</summary>
    public int TotalFiles { get; private set; }

    /// <summary>Sum of the sizes of all files in this folder and its subfolders.</summary>
    public long TotalSize { get; private set; }

    /// <summary>Builds the tree from the original folder of each file (e.g. <c>\Users\Ana\Docs</c>).</summary>
    public static FolderNode Build(IEnumerable<DeletedFile> files)
    {
        var root = new FolderNode("");
        foreach (var file in files)
        {
            var node = root;
            foreach (string segment in file.Path.Split('\\',StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node._folders.TryGetValue(segment, out var child))
                    node._folders[segment] = child = new FolderNode(segment);
                node = child;
            }
            node._files.Add(file);
        }
        root.Finish();
        return root;
    }

    private void Finish()
    {
        Folders = _folders.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        TotalFiles = _files.Count;
        TotalSize = _files.Sum(f => f.Size);
        foreach (var folder in Folders)
        {
            folder.Finish();
            TotalFiles += folder.TotalFiles;
            TotalSize += folder.TotalSize;
        }
    }
}
