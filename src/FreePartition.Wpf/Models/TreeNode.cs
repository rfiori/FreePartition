using System.ComponentModel;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>A folder or a deleted file in the recovery tree, with a check box (folders are three-state).</summary>
public sealed class TreeNode : INotifyPropertyChanged
{
    private readonly Action<DeletedFile, bool>? _onFileChecked;
    private bool? _isChecked = false;
    private bool _isExpanded;

    private TreeNode(string name, Action<DeletedFile, bool>? onFileChecked)
    {
        Name = name;
        _onFileChecked = onFileChecked;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string SizeText { get; private init; } = "";
    public string ModifiedText { get; private init; } = "";
    public string ChanceText { get; private init; } = "";

    /// <summary>Glyph from Segoe MDL2 Assets: folder or document.</summary>
    public string Icon => File is null ? "" : "";

    public DeletedFile? File { get; private init; }

    /// <summary>
    /// Whether the check box is usable: a file whose data was wiped has nothing to recover, and a folder is usable
    /// only when something inside it is.
    /// </summary>
    public bool CanCheck { get; private set; } = true;

    public TreeNode? Parent { get; private set; }
    public IReadOnlyList<TreeNode> Children { get; private set; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
        }
    }

    /// <summary>Set by the user: a folder applies the value to everything inside it.</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value ?? false, updateChildren: true, updateParent: true);
    }

    /// <param name="isChecked">Whether a given file starts checked (used to keep the selection across rebuilds).</param>
    /// <param name="onFileChecked">Called whenever a file is checked or unchecked.</param>
    public static IReadOnlyList<TreeNode> Build(FolderNode root, bool expandAll,
        Func<DeletedFile, bool> isChecked, Action<DeletedFile, bool> onFileChecked)
    {
        var nodes = BuildChildren(root, expandAll, isChecked, onFileChecked, parent: null);
        foreach (var node in nodes)
            node.RefreshFromChildren(recursive: true);
        return nodes;
    }

    private static List<TreeNode> BuildChildren(FolderNode folder, bool expandAll,
        Func<DeletedFile, bool> isChecked, Action<DeletedFile, bool> onFileChecked, TreeNode? parent)
    {
        var result = new List<TreeNode>();

        foreach (var sub in folder.Folders)
        {
            var node = new TreeNode(sub.Name, onFileChecked)
            {
                SizeText = ByteFormatter.Format(sub.TotalSize),
                ChanceText = $"{sub.TotalFiles:N0} file(s)",
                Parent = parent,
                _isExpanded = expandAll
            };
            node.Children = BuildChildren(sub, expandAll, isChecked, onFileChecked, node);
            node.CanCheck = node.Children.Any(c => c.CanCheck);
            result.Add(node);
        }

        foreach (var file in folder.Files)
        {
            result.Add(new TreeNode(file.Name, onFileChecked)
            {
                File = file,
                CanCheck = file.CanRecover,
                SizeText = file.SizeFormatted,
                ModifiedText = file.ModifiedFormatted,
                ChanceText = file.ChanceText,
                Parent = parent,
                _isChecked = file.CanRecover && isChecked(file)
            });
        }

        return result;
    }

    /// <summary>All files at or below this node.</summary>
    public IEnumerable<DeletedFile> AllFiles()
    {
        if (File is not null) yield return File;
        foreach (var child in Children)
            foreach (var file in child.AllFiles())
                yield return file;
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (value == true && !CanCheck) return;   // nothing here can be recovered
        if (_isChecked == value) return;
        _isChecked = value;

        if (updateChildren && value.HasValue)
        {
            foreach (var child in Children)
                child.SetChecked(value, updateChildren: true, updateParent: false);
        }

        if (File is not null && value.HasValue)
            _onFileChecked?.Invoke(File, value.Value);

        if (updateParent)
            Parent?.RefreshFromChildren(recursive: false);

        Notify(nameof(IsChecked));
    }

    /// <summary>A folder is checked if everything inside is, unchecked if nothing is, otherwise indeterminate.</summary>
    private void RefreshFromChildren(bool recursive)
    {
        if (Children.Count == 0) return;
        foreach (var child in Children)
            if (recursive && child.File is null) child.RefreshFromChildren(recursive: true);

        // Only what can be checked counts: files that cannot be recovered stay unchecked without making the folder partial.
        var checkable = Children.Where(c => c.CanCheck).ToList();
        bool? state = checkable.Count == 0 || checkable.All(c => c._isChecked == false) ? false
            : checkable.All(c => c._isChecked == true) ? true
            : null;
        SetChecked(state, updateChildren: false, updateParent: false);
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
