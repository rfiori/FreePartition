using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class FolderNodeTests
{
    private static DeletedFile File(string path, string name, long size = 10) =>
        new(1, name, path, size, null, RecoveryChance.High, 1.0, [], null);

    [Fact]
    public void Build_GroupsFilesIntoFoldersAndSubfolders()
    {
        var root = FolderNode.Build(
        [
            File(@"\A\B", "z.txt", 5),
            File(@"\A", "a.txt", 7),
            File(@"\a\B", "y.txt", 3),   // NTFS names are case-insensitive: same folders as above
            File(@"\", "root.txt", 1),
            File("", "also-root.txt", 1),
        ]);

        Assert.Equal(["also-root.txt", "root.txt"], root.Files.Select(f => f.Name));
        var a = Assert.Single(root.Folders);
        Assert.Equal(["a.txt"], a.Files.Select(f => f.Name));
        var b = Assert.Single(a.Folders);
        Assert.Equal(["y.txt", "z.txt"], b.Files.Select(f => f.Name));

        Assert.Equal(5, root.TotalFiles);
        Assert.Equal(17, root.TotalSize);
        Assert.Equal(3, a.TotalFiles);
        Assert.Equal(15, a.TotalSize);
    }

    [Fact]
    public void Build_SortsFoldersByName()
    {
        var root = FolderNode.Build([File(@"\b", "1"), File(@"\C", "2"), File(@"\a", "3")]);
        Assert.Equal(["a", "b", "C"], root.Folders.Select(f => f.Name));
    }

    [Fact]
    public void Build_WithNoFiles_IsEmpty()
    {
        var root = FolderNode.Build([]);
        Assert.Empty(root.Folders);
        Assert.Empty(root.Files);
        Assert.Equal(0, root.TotalFiles);
    }
}

public class FolderSegmentsTests
{
    [Fact]
    public void FolderSegments_SplitsPath_AndDropsTraversal()
    {
        var segments = FileRecoverer.FolderSegments(@"\StarCounter\..\bin\Debug\");
        Assert.Equal(new[] { "StarCounter", "bin", "Debug" }, segments);
    }
}
