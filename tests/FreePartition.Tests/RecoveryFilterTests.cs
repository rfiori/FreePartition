using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class RecoveryFilterTests
{
    private static DeletedFile File(string name, long size = 10, DateTime? modified = null) =>
        new(1, name, @"\", size, modified, RecoveryChance.High, 1.0, [], null);

    [Fact]
    public void EmptyFilter_MatchesEverything()
    {
        var filter = new RecoveryFilter();
        Assert.True(filter.IsEmpty);
        Assert.True(filter.Matches(File("a.bin")));
    }

    [Theory]
    [InlineData("photo.JPG", FileCategory.Pictures)]
    [InlineData("report.pdf", FileCategory.Documents)]
    [InlineData("song.mp3", FileCategory.Audio)]
    [InlineData("clip.mkv", FileCategory.Video)]
    [InlineData("backup.zip", FileCategory.Archives)]
    [InlineData("data.xyz", FileCategory.Other)]
    [InlineData("noextension", FileCategory.Other)]
    public void Category_IsDerivedFromExtension(string name, FileCategory expected) =>
        Assert.Equal(expected, FileCategories.Of(name));

    [Fact]
    public void Matches_FiltersByCategory()
    {
        var filter = new RecoveryFilter { Category = FileCategory.Pictures };
        Assert.True(filter.Matches(File("a.png")));
        Assert.False(filter.Matches(File("a.pdf")));
    }

    [Fact]
    public void Matches_FiltersBySizeRange_Inclusive()
    {
        var filter = new RecoveryFilter { MinSize = 10, MaxSize = 20 };
        Assert.False(filter.Matches(File("a", 9)));
        Assert.True(filter.Matches(File("a", 10)));
        Assert.True(filter.Matches(File("a", 20)));
        Assert.False(filter.Matches(File("a", 21)));
    }

    [Fact]
    public void Matches_FiltersByDateRange_AndRejectsMissingDates()
    {
        var day = new DateTime(2024, 5, 10, 12, 0, 0, DateTimeKind.Local);
        var filter = new RecoveryFilter { ModifiedFrom = new DateTime(2024, 5, 10), ModifiedTo = new DateTime(2024, 5, 10) };

        Assert.True(filter.Matches(File("a", modified: day.ToUniversalTime())));
        Assert.False(filter.Matches(File("a", modified: day.AddDays(1).ToUniversalTime())));
        Assert.False(filter.Matches(File("a", modified: null)));
    }

    [Fact]
    public void Matches_CombinesAllCriteria()
    {
        var filter = new RecoveryFilter { NameContains = "inv", Category = FileCategory.Documents, MinSize = 5 };
        Assert.True(filter.Matches(File("Invoice.pdf", 10)));
        Assert.False(filter.Matches(File("Invoice.png", 10)));
        Assert.False(filter.Matches(File("Invoice.pdf", 1)));
        Assert.False(filter.Matches(File("other.pdf", 10)));
    }
}

public class FilePreviewReaderTests
{
    [Theory]
    [InlineData("a.jpg", PreviewKind.Image)]
    [InlineData("a.TXT", PreviewKind.Text)]
    [InlineData("a.exe", PreviewKind.Hex)]
    public void KindOf_UsesExtension(string name, PreviewKind expected) =>
        Assert.Equal(expected, FilePreviewReader.KindOf(name));

    [Fact]
    public void FormatHex_ShowsOffsetBytesAndAscii()
    {
        string dump = FilePreviewReader.FormatHex("Hi!"u8.ToArray());
        Assert.StartsWith("00000000  48 69 21 ", dump);
        Assert.Contains(" Hi!", dump);
    }

    [Fact]
    public void DecodeText_HonorsByteOrderMarks()
    {
        Assert.Equal("ok", FilePreviewReader.DecodeText([0xEF, 0xBB, 0xBF, (byte)'o', (byte)'k']));
        Assert.Equal("ok", FilePreviewReader.DecodeText([0xFF, 0xFE, (byte)'o', 0, (byte)'k', 0]));
    }

    [Fact]
    public void Read_ResidentFile_ReturnsItsContent_WithoutTouchingTheVolume()
    {
        var file = new DeletedFile(1, "note.txt", @"\", 5, null, RecoveryChance.High, 1.0, [], "hello"u8.ToArray());
        var scan = new ScanResult("Z", 4096, 0, TimeSpan.Zero, [file]);

        var preview = FilePreviewReader.Read(scan, file);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Equal("hello", preview.Text);
        Assert.False(preview.Truncated);
    }
}
