using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public sealed class FileRecovererTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FreePartitionTests-" + Guid.NewGuid().ToString("N"));
    private readonly FileRecoverer _recoverer = new();

    public FileRecovererTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A scan result for a drive letter that is never the one holding the temp folder.</summary>
    private ScanResult ScanOnAnotherDrive()
    {
        char tempDrive = char.ToUpperInvariant(_root[0]);
        char other = tempDrive == 'Z' ? 'Y' : 'Z';
        return new ScanResult(other.ToString(), 4096, 0, TimeSpan.Zero, []);
    }

    private ScanResult ScanOnSameDrive() =>
        new(char.ToUpperInvariant(_root[0]).ToString(), 4096, 0, TimeSpan.Zero, []);

    private static DeletedFile Resident(string name, byte[] data, string path = @"\", RecoveryChance chance = RecoveryChance.High) =>
        new(1, name, path, data.Length, null, chance, 1.0, [], data);

    private string Dest(string name = "out") => Path.Combine(_root, name);

    [Fact]
    public void Recover_ResidentFile_WritesItsContent()
    {
        byte[] data = [1, 2, 3, 4, 5];

        string path = _recoverer.Recover(ScanOnAnotherDrive(), Resident("a.bin", data), Dest());

        Assert.Equal(Path.Combine(Dest(), "a.bin"), path);
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void Recover_DestinationOnTheSameDrive_IsRefused_AndNothingIsWritten()
    {
        var file = Resident("a.bin", [1, 2, 3]);

        Assert.Throws<InvalidOperationException>(() => _recoverer.Recover(ScanOnSameDrive(), file, Dest()));

        Assert.False(Directory.Exists(Dest()));
    }

    [Fact]
    public void Recover_SameDriveCheck_IgnoresCaseOfTheDriveLetter()
    {
        string lower = char.ToLowerInvariant(_root[0]).ToString();
        var scan = new ScanResult(lower, 4096, 0, TimeSpan.Zero, []);

        Assert.Throws<InvalidOperationException>(() =>
            _recoverer.Recover(scan, Resident("a.bin", [1]), Dest()));
    }

    [Fact]
    public void Recover_UnavailableFile_IsRefused()
    {
        var file = Resident("a.bin", [1], chance: RecoveryChance.Unavailable);

        Assert.Throws<InvalidOperationException>(() => _recoverer.Recover(ScanOnAnotherDrive(), file, Dest()));

        Assert.False(Directory.Exists(Dest()));
    }

    [Fact]
    public void Recover_OverwrittenFile_IsRefused_AndNothingIsWritten()
    {
        var file = Resident("a.bin", [0, 0, 0], chance: RecoveryChance.Overwritten);

        var ex = Assert.Throws<InvalidOperationException>(() => _recoverer.Recover(ScanOnAnotherDrive(), file, Dest()));

        Assert.Contains("overwritten", ex.Message);
        Assert.False(Directory.Exists(Dest()));
    }

    [Fact]
    public void Recover_InvalidCharactersInName_AreReplaced_AndFileStaysInsideDestination()
    {
        string path = _recoverer.Recover(ScanOnAnotherDrive(), Resident("a:b*c?d\"e<f>g|h.txt", [1]), Dest());

        string fileName = Path.GetFileName(path);
        Assert.Equal("a_b_c_d_e_f_g_h.txt", fileName);
        Assert.Equal(Dest(), Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Recover_NameWithPathSeparators_CannotEscapeTheDestination()
    {
        string path = _recoverer.Recover(ScanOnAnotherDrive(), Resident(@"..\..\evil.txt", [1]), Dest());

        Assert.Equal(Dest(), Path.GetDirectoryName(path));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, Path.GetFileName(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Recover_BlankName_UsesAFallbackName(string name)
    {
        string path = _recoverer.Recover(ScanOnAnotherDrive(), Resident(name, [1]), Dest());

        Assert.Equal("recovered_file", Path.GetFileName(path));
    }

    [Fact]
    public void Recover_DuplicateNames_GetNumberedSuffixes_AndNeverOverwrite()
    {
        var scan = ScanOnAnotherDrive();

        string first = _recoverer.Recover(scan, Resident("a.txt", [1]), Dest());
        string second = _recoverer.Recover(scan, Resident("a.txt", [2]), Dest());
        string third = _recoverer.Recover(scan, Resident("a.txt", [3]), Dest());

        Assert.Equal(["a.txt", "a (1).txt", "a (2).txt"],
            new[] { first, second, third }.Select(Path.GetFileName));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(second));
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(third));
    }

    [Fact]
    public void Recover_DuplicateNameWithoutExtension_GetsNumberedSuffix()
    {
        var scan = ScanOnAnotherDrive();

        _recoverer.Recover(scan, Resident("notes", [1]), Dest());
        string second = _recoverer.Recover(scan, Resident("notes", [2]), Dest());

        Assert.Equal("notes (1)", Path.GetFileName(second));
    }

    [Fact]
    public void Recover_KeepFolders_RecreatesTheOriginalFolderStructure()
    {
        string path = _recoverer.Recover(ScanOnAnotherDrive(), Resident("a.txt", [1], @"\Docs\2024"), Dest(), keepFolders: true);

        Assert.Equal(Path.Combine(Dest(), "Docs", "2024", "a.txt"), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Recover_KeepFolders_WithTraversalInPath_StaysInsideTheDestination()
    {
        string path = _recoverer.Recover(ScanOnAnotherDrive(), Resident("a.txt", [1], @"\..\..\Windows"), Dest(), keepFolders: true);

        string fullDest = Path.GetFullPath(Dest()) + Path.DirectorySeparatorChar;
        Assert.StartsWith(fullDest, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recover_WithoutKeepFolders_PutsTheFileDirectlyInTheDestination()
    {
        string path = _recoverer.Recover(ScanOnAnotherDrive(), Resident("a.txt", [1], @"\Docs\2024"), Dest());

        Assert.Equal(Dest(), Path.GetDirectoryName(path));
    }

    [Fact]
    public void Recover_EmptyResidentFile_CreatesAnEmptyFile()
    {
        string path = _recoverer.Recover(ScanOnAnotherDrive(), Resident("empty.txt", []), Dest());

        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void FolderSegments_SanitizesInvalidCharacters_AndDropsDotOnlyNames()
    {
        var segments = FileRecoverer.FolderSegments(@"\a:b\...\..\c");

        Assert.Equal(["a_b", "c"], segments);
    }

    [Fact]
    public void FolderSegments_BlankFolderName_BecomesTheFallbackName()
    {
        // Not dropped: SafeName replaces a whitespace-only name before the dot filter runs.
        // Harmless (the folder stays inside the destination), so this just pins the behavior.
        Assert.Equal(["recovered_file"], FileRecoverer.FolderSegments(@"\ "));
    }

    [Fact]
    public void FolderSegments_OfRootOrEmptyPath_IsEmpty()
    {
        Assert.Empty(FileRecoverer.FolderSegments(@"\"));
        Assert.Empty(FileRecoverer.FolderSegments(""));
    }
}
