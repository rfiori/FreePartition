using System.Text;
using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class FilePreviewReaderTests
{
    private static readonly ScanResult Scan = new("Z", 4096, 0, TimeSpan.Zero, []);

    private static DeletedFile Resident(string name, byte[] data, long? size = null, RecoveryChance chance = RecoveryChance.High) =>
        new(1, name, @"\", size ?? data.Length, null, chance, 1.0, [], data);

    [Theory]
    [InlineData("photo.jpg", PreviewKind.Image)]
    [InlineData("photo.JPEG", PreviewKind.Image)]
    [InlineData("a.PNG", PreviewKind.Image)]
    [InlineData("a.gif", PreviewKind.Image)]
    [InlineData("a.bmp", PreviewKind.Image)]
    [InlineData("a.tiff", PreviewKind.Image)]
    [InlineData("a.ico", PreviewKind.Image)]
    [InlineData("notes.txt", PreviewKind.Text)]
    [InlineData("NOTES.TXT", PreviewKind.Text)]
    [InlineData("data.csv", PreviewKind.Text)]
    [InlineData("config.json", PreviewKind.Text)]
    [InlineData("Program.cs", PreviewKind.Text)]
    [InlineData("archive.zip", PreviewKind.Hex)]
    [InlineData("program.exe", PreviewKind.Hex)]
    [InlineData("noextension", PreviewKind.Hex)]
    [InlineData("", PreviewKind.Hex)]
    [InlineData("trailingdot.", PreviewKind.Hex)]
    public void KindOf_ChoosesThePreviewByExtension(string name, PreviewKind expected) =>
        Assert.Equal(expected, FilePreviewReader.KindOf(name));

    [Fact]
    public void DecodeText_PlainUtf8()
    {
        Assert.Equal("héllo", FilePreviewReader.DecodeText(Encoding.UTF8.GetBytes("héllo")));
    }

    [Fact]
    public void DecodeText_Utf8WithBom_DropsTheBom()
    {
        byte[] data = [0xEF, 0xBB, 0xBF, .. "hi"u8];

        Assert.Equal("hi", FilePreviewReader.DecodeText(data));
    }

    [Fact]
    public void DecodeText_Utf16LittleEndianWithBom()
    {
        byte[] data = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("olá")];

        Assert.Equal("olá", FilePreviewReader.DecodeText(data));
    }

    [Fact]
    public void DecodeText_Utf16BigEndianWithBom()
    {
        byte[] data = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("olá")];

        Assert.Equal("olá", FilePreviewReader.DecodeText(data));
    }

    [Fact]
    public void DecodeText_Empty_IsEmpty() =>
        Assert.Equal("", FilePreviewReader.DecodeText([]));

    [Fact]
    public void DecodeText_InvalidBytes_DoNotThrow()
    {
        string text = FilePreviewReader.DecodeText([0x61, 0xC3, 0x28, 0xFF, 0x62]);

        Assert.StartsWith("a", text);
        Assert.EndsWith("b", text);
    }

    [Fact]
    public void FormatHex_Empty_IsEmpty() =>
        Assert.Equal("", FilePreviewReader.FormatHex([]));

    [Fact]
    public void FormatHex_PartialLine_PadsTheHexColumnsAndShowsAscii()
    {
        string expected = "00000000  " + "48 69 " + new string(' ', 14 * 3) + " " + "Hi" + Environment.NewLine;

        Assert.Equal(expected, FilePreviewReader.FormatHex("Hi"u8));
    }

    [Fact]
    public void FormatHex_NonPrintableBytes_AreShownAsDots()
    {
        string hex = FilePreviewReader.FormatHex([0x00, 0x1F, 0x41, 0x7F, 0xFF]);

        Assert.EndsWith(" ..A.." + Environment.NewLine, hex);
    }

    [Fact]
    public void FormatHex_MoreThan16Bytes_WrapsWithIncreasingOffsets()
    {
        byte[] data = new byte[40];

        string[] lines = FilePreviewReader.FormatHex(data)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("00000000  ", lines[0]);
        Assert.StartsWith("00000010  ", lines[1]);
        Assert.StartsWith("00000020  ", lines[2]);
    }

    [Fact]
    public void FormatHex_ExactlyOneFullLine_HasNoSecondLine()
    {
        string[] lines = FilePreviewReader.FormatHex(new byte[16])
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
    }

    [Fact]
    public void Read_SmallTextFile_ReturnsDecodedText_NotTruncated()
    {
        var preview = FilePreviewReader.Read(Scan, Resident("a.txt", "hello"u8.ToArray()));

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Equal("hello", preview.Text);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public void Read_LargeTextFile_IsCutAtTheTextLimit_AndMarkedTruncated()
    {
        byte[] data = new byte[FilePreviewReader.MaxTextBytes + 10];
        Array.Fill(data, (byte)'x');

        var preview = FilePreviewReader.Read(Scan, Resident("big.txt", data));

        Assert.Equal(FilePreviewReader.MaxTextBytes, preview.Data.Length);
        Assert.True(preview.Truncated);
    }

    [Fact]
    public void Read_BinaryFile_ReturnsAHexDump_LimitedToTheHexSize()
    {
        byte[] data = new byte[2000];

        var preview = FilePreviewReader.Read(Scan, Resident("a.exe", data));

        Assert.Equal(PreviewKind.Hex, preview.Kind);
        Assert.Equal(FilePreviewReader.MaxHexBytes, preview.Data.Length);
        Assert.True(preview.Truncated);
        Assert.StartsWith("00000000  ", preview.Text);
    }

    [Fact]
    public void Read_ImageLargerThanTheLimit_FallsBackToHex()
    {
        // Resident data is only a stand-in: the declared size decides the fallback.
        var file = Resident("huge.png", new byte[FilePreviewReader.MaxHexBytes], size: FilePreviewReader.MaxImageBytes + 1);

        var preview = FilePreviewReader.Read(Scan, file);

        Assert.Equal(PreviewKind.Hex, preview.Kind);
        Assert.True(preview.Truncated);
    }

    [Fact]
    public void Read_SmallImage_KeepsTheRawBytesForDecoding()
    {
        byte[] data = [0x89, 0x50, 0x4E, 0x47];

        var preview = FilePreviewReader.Read(Scan, Resident("a.png", data));

        Assert.Equal(PreviewKind.Image, preview.Kind);
        Assert.Equal(data, preview.Data);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public void Read_WhenResidentDataIsShorterThanTheSize_ReturnsWhatExists()
    {
        var preview = FilePreviewReader.Read(Scan, Resident("a.txt", "abc"u8.ToArray(), size: 10));

        Assert.Equal(3, preview.Data.Length);
        Assert.True(preview.Truncated);
    }

    [Fact]
    public void Read_UnavailableFile_Throws()
    {
        var file = Resident("a.txt", [1], chance: RecoveryChance.Unavailable);

        Assert.Throws<InvalidOperationException>(() => FilePreviewReader.Read(Scan, file));
    }
}
