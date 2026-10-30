using System.Text;
using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

/// <summary>The formats whose size is stated by their own header (or a short walk through it); the rest are in <see cref="FileCarverTests"/>.</summary>
public class FileCarverFormatsTests
{
    private const int Sector = FileCarver.SectorSize;

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static void Le16(byte[] b, int at, long value) { b[at] = (byte)value; b[at + 1] = (byte)(value >> 8); }
    private static void Le32(byte[] b, int at, long value) { Le16(b, at, value); Le16(b, at + 2, value >> 16); }
    private static void Le64(byte[] b, int at, long value) { Le32(b, at, value); Le32(b, at + 4, value >> 32); }
    private static void Be16(byte[] b, int at, long value) { b[at] = (byte)(value >> 8); b[at + 1] = (byte)value; }
    private static void Be32(byte[] b, int at, long value) { Be16(b, at, value >> 16); Be16(b, at + 2, value); }

    private static string ExtensionOf(DeletedFile file) => Path.GetExtension(file.Name).TrimStart('.');

    /// <summary>The single file found in a volume holding <paramref name="data"/> at the start.</summary>
    private static DeletedFile Single(byte[] data, int sectors = 16) =>
        Assert.Single(FileCarverTests.Carve(FileCarverTests.Volume(sectors * Sector, (0, data))));

    private static void AssertNothing(byte[] data, int sectors = 16) =>
        Assert.Empty(FileCarverTests.Carve(FileCarverTests.Volume(sectors * Sector, (0, data))));

    /// <summary>A volume that ends <paramref name="sectors"/> sectors into the file, so the file's real end is missing.</summary>
    private static void AssertTruncated(byte[] data, int sectors)
    {
        Assert.True(sectors * Sector < data.Length, "the test volume must end before the file does");
        Assert.Empty(FileCarverTests.Carve(data[..(sectors * Sector)]));
    }

    // ---- BMP ----

    private static byte[] Bmp(int width = 4, int height = 4)
    {
        int row = (width * 3 + 3) / 4 * 4;
        var b = new byte[54 + row * height];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        Le32(b, 2, b.Length);
        Le32(b, 10, 54);
        Le32(b, 14, 40);
        Le32(b, 18, width);
        Le32(b, 22, height);
        Le16(b, 26, 1);
        Le16(b, 28, 24);
        Array.Fill(b, (byte)0x55, 54, b.Length - 54);
        return b;
    }

    [Fact]
    public void Bmp_IsCarvedWithTheSizeFromItsHeader()
    {
        var bmp = Bmp(40, 30);

        var file = Single(bmp);

        Assert.Equal("bmp", ExtensionOf(file));
        Assert.Equal(bmp.Length, file.Size);
    }

    [Fact]
    public void Bmp_PlainTextStartingWithBm_IsNotCarved() =>
        AssertNothing(Ascii("BM is also the start of some ordinary text, which must not be taken for an image."));

    [Fact]
    public void Bmp_WithNonZeroReservedField_IsNotCarved()
    {
        var bmp = Bmp();
        bmp[6] = 1;

        AssertNothing(bmp);
    }

    [Fact]
    public void Bmp_WithUnknownBitDepth_IsNotCarved()
    {
        var bmp = Bmp();
        Le16(bmp, 28, 7);

        AssertNothing(bmp);
    }

    [Fact]
    public void Bmp_CutShort_IsNotCarved() => AssertTruncated(Bmp(40, 30), sectors: 4);

    // ---- RIFF: WAV, AVI, WebP ----

    private static byte[] Riff(string form, int dataBytes = 100)
    {
        byte[] fmt = [.. Ascii("fmt "), 16, 0, 0, 0, .. new byte[16]];
        var data = new byte[8 + dataBytes];
        Ascii("data").CopyTo(data, 0);
        Le32(data, 4, dataBytes);

        byte[] body = [.. Ascii(form), .. fmt, .. data];
        var riff = new byte[8 + body.Length];
        Ascii("RIFF").CopyTo(riff, 0);
        Le32(riff, 4, body.Length);
        body.CopyTo(riff, 8);
        return riff;
    }

    [Theory]
    [InlineData("WAVE", "wav")]
    [InlineData("AVI ", "avi")]
    [InlineData("WEBP", "webp")]
    public void Riff_IsCarvedWithTheRightExtension_AndTheSizeFromItsHeader(string form, string extension)
    {
        var riff = Riff(form, dataBytes: 2000);

        var file = Single(riff);

        Assert.Equal(extension, ExtensionOf(file));
        Assert.Equal(riff.Length, file.Size);
    }

    [Fact]
    public void Riff_OfAnotherKind_IsNotCarved() => AssertNothing(Riff("RMID"));

    [Fact]
    public void Riff_CutShort_IsNotCarved() => AssertTruncated(Riff("WAVE", 3000), sectors: 4);

    // ---- GIF ----

    private static byte[] Gif(bool trailer = true, bool badBlock = false)
    {
        var b = new List<byte>(Ascii("GIF89a"));
        b.AddRange([2, 0, 2, 0, 0x80, 0, 0]);                        // 2x2, a global palette of 2 colors
        b.AddRange([0, 0, 0, 255, 255, 255]);                        // the palette
        b.AddRange([0x21, 0xF9, 4, 0, 0, 0, 0, 0]);                  // graphic control extension
        if (badBlock) b.Add(0x99);
        b.AddRange([0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0]);               // image descriptor, no local palette
        b.AddRange([2, 3, 0x44, 0x01, 0x05, 0]);                     // LZW code size, one data sub-block, end
        if (trailer) b.Add(0x3B);
        return [.. b];
    }

    [Fact]
    public void Gif_EndsAtItsTrailer()
    {
        var gif = Gif();

        var file = Single(gif);

        Assert.Equal("gif", ExtensionOf(file));
        Assert.Equal(gif.Length, file.Size);
    }

    [Fact]
    public void Gif_WithoutTrailer_IsNotCarved() => AssertNothing(Gif(trailer: false));

    [Fact]
    public void Gif_WithAnUnknownBlock_IsNotCarved() => AssertNothing(Gif(badBlock: true));

    // ---- SQLite ----

    private static byte[] Sqlite(int pageSize = 1024, int pages = 3)
    {
        var b = new byte[pageSize * pages];
        Ascii("SQLite format 3\0").CopyTo(b, 0);
        Be16(b, 16, pageSize);
        b[18] = 1; b[19] = 1;
        b[21] = 64; b[22] = 32; b[23] = 32;
        Be32(b, 24, 5);       // file change counter
        Be32(b, 28, pages);
        Be32(b, 92, 5);       // version-valid-for
        return b;
    }

    [Fact]
    public void Sqlite_EndsAfterItsLastPage()
    {
        var db = Sqlite(pageSize: 4096, pages: 3);

        var file = Single(db, sectors: 40);

        Assert.Equal("sqlite", ExtensionOf(file));
        Assert.Equal(db.Length, file.Size);
    }

    [Fact]
    public void Sqlite_With64KPageSize_IsRead()
    {
        // A stored page size of 1 means 65536.
        var db = Sqlite(pageSize: 512, pages: 2);
        Be16(db, 16, 1);
        var big = new byte[65536 * 2];
        db.AsSpan(0, 100).CopyTo(big);

        var file = Single(big, sectors: 300);

        Assert.Equal(big.Length, file.Size);
    }

    [Fact]
    public void Sqlite_WithAStalePageCount_IsNotCarved()
    {
        var db = Sqlite();
        Be32(db, 92, 4);   // does not match the change counter, so the page count cannot be trusted

        AssertNothing(db);
    }

    [Fact]
    public void Sqlite_WithPageCountZero_IsNotCarved()
    {
        var db = Sqlite();
        Be32(db, 28, 0);

        AssertNothing(db);
    }

    [Fact]
    public void Sqlite_WithAPageSizeThatIsNotAPowerOfTwo_IsNotCarved()
    {
        var db = Sqlite();
        Be16(db, 16, 1000);

        AssertNothing(db);
    }

    // ---- 7z ----

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] SevenZip(long nextOffset = 100, long nextSize = 40)
    {
        var b = new byte[32 + nextOffset + nextSize];
        byte[] signature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4];
        signature.CopyTo(b, 0);
        Le64(b, 12, nextOffset);
        Le64(b, 20, nextSize);
        Le32(b, 8, Crc32(b.AsSpan(12, 20)));
        Array.Fill(b, (byte)0x33, 32, b.Length - 32);
        return b;
    }

    [Fact]
    public void SevenZip_EndsAfterItsLastHeader()
    {
        var archive = SevenZip(nextOffset: 700, nextSize: 60);

        var file = Single(archive);

        Assert.Equal("7z", ExtensionOf(file));
        Assert.Equal(archive.Length, file.Size);
    }

    [Fact]
    public void SevenZip_WithABadHeaderCrc_IsNotCarved()
    {
        var archive = SevenZip();
        archive[8] ^= 0xFF;

        AssertNothing(archive);
    }

    [Fact]
    public void SevenZip_CutShort_IsNotCarved() => AssertTruncated(SevenZip(2000, 100), sectors: 3);

    // ---- MP4 and friends ----

    private static byte[] Box(string type, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        Be32(b, 0, b.Length);
        Ascii(type).CopyTo(b, 4);
        payload.CopyTo(b, 8);
        return b;
    }

    private static byte[] Ftyp(string brand) => Box("ftyp", [.. Ascii(brand), 0, 0, 0, 0, .. Ascii("isom")]);

    private static byte[] Mp4(string brand = "isom", int mdatBytes = 1000) =>
        [.. Ftyp(brand), .. Box("moov", new byte[40]), .. Box("mdat", new byte[mdatBytes])];

    [Theory]
    [InlineData("isom", "mp4")]
    [InlineData("mp42", "mp4")]
    [InlineData("qt  ", "mov")]
    [InlineData("M4A ", "m4a")]
    [InlineData("M4V ", "m4v")]
    [InlineData("heic", "heic")]
    [InlineData("avif", "avif")]
    [InlineData("3gp5", "3gp")]
    [InlineData("3g2a", "3g2")]
    [InlineData("crx ", "cr3")]
    public void Mp4Family_GetsTheExtensionOfItsBrand_AndTheEndOfItsLastBox(string brand, string extension)
    {
        var video = Mp4(brand);

        var file = Single(video);

        Assert.Equal(extension, ExtensionOf(file));
        Assert.Equal(video.Length, file.Size);
    }

    [Fact]
    public void Mp4_WithTheMovieBoxAfterTheMedia_IsCarvedWhole()
    {
        byte[] video = [.. Ftyp("isom"), .. Box("mdat", new byte[3000]), .. Box("moov", new byte[200])];

        Assert.Equal(video.Length, Single(video).Size);
    }

    [Fact]
    public void Mp4_WithA64BitBoxSize_IsFollowed()
    {
        // size == 1 means the real size comes next, as 8 bytes.
        var payload = new byte[2000];
        var mdat = new byte[16 + payload.Length];
        Be32(mdat, 0, 1);
        Ascii("mdat").CopyTo(mdat, 4);
        Be32(mdat, 12, mdat.Length);
        byte[] video = [.. Ftyp("isom"), .. mdat];

        Assert.Equal(video.Length, Single(video).Size);
    }

    [Fact]
    public void Mp4_WithOnlyAFtypBox_IsNotCarved() => AssertNothing(Ftyp("isom"));

    [Fact]
    public void Mp4_WhoseLastBoxRunsToTheEndOfTheFile_IsNotCarved()
    {
        var mdat = Box("mdat", new byte[500]);
        Be32(mdat, 0, 0);   // size 0: "to the end of the file", so the end cannot be known
        byte[] video = [.. Ftyp("isom"), .. mdat];

        AssertNothing(video);
    }

    [Fact]
    public void Mp4_CutShort_IsNotCarved() => AssertTruncated(Mp4(mdatBytes: 5000), sectors: 6);

    [Fact]
    public void Mp4_LargerThanTheSearchLimit_IsStillCarved_BecauseItsSizeIsInTheHeader()
    {
        // A 1 GiB movie: never held in memory, only its first bytes exist.
        const long Total = 1L << 30;
        byte[] head = [.. Ftyp("isom"), .. Box("moov", new byte[40])];
        var mdatHeader = new byte[8];
        Be32(mdatHeader, 0, Total - head.Length);
        Ascii("mdat").CopyTo(mdatHeader, 4);
        byte[] start = [.. head, .. mdatHeader];

        ReadAt volume = (offset, destination) =>
        {
            long remaining = Total + 4096 - offset;
            if (remaining <= 0) return 0;
            int n = (int)Math.Min(destination.Length, remaining);
            destination[..n].Fill(0xAA);
            if (offset < start.Length)
                start.AsSpan((int)offset, (int)Math.Min(n, start.Length - offset)).CopyTo(destination);
            return n;
        };

        var file = Assert.Single(FileCarver.Carve(volume, Total + 4096).Files);

        Assert.Equal("mp4", ExtensionOf(file));
        Assert.Equal(Total, file.Size);
        Assert.True(file.Size > FileCarver.MaxFileSize);
    }

    // ---- PSD ----

    private static byte[] Psd(int compression, bool big = false, int width = 4, int height = 3, int rleCount = 6)
    {
        const int Channels = 3;
        var b = new List<byte>(Ascii("8BPS"));
        void Be(long value, int bytes)
        {
            for (int i = bytes - 1; i >= 0; i--) b.Add((byte)(value >> (8 * i)));
        }

        Be(big ? 2 : 1, 2);
        Be(0, 6);
        Be(Channels, 2);
        Be(height, 4);
        Be(width, 4);
        Be(8, 2);
        Be(3, 2);                       // RGB
        Be(0, 4);                       // color mode data
        Be(0, 4);                       // image resources
        Be(0, big ? 8 : 4);             // layer and mask information
        Be(compression, 2);

        if (compression == 0)
        {
            b.AddRange(new byte[width * height * Channels]);
        }
        else if (compression == 1)
        {
            for (int row = 0; row < height * Channels; row++) Be(rleCount, big ? 4 : 2);
            b.AddRange(new byte[rleCount * height * Channels]);
        }
        else
        {
            b.AddRange(new byte[50]);
        }
        return [.. b];
    }

    [Theory]
    [InlineData(0, false, "psd")]
    [InlineData(1, false, "psd")]
    [InlineData(0, true, "psb")]
    [InlineData(1, true, "psb")]
    public void Psd_EndsAfterItsImageData(int compression, bool big, string extension)
    {
        var psd = Psd(compression, big);

        var file = Single(psd);

        Assert.Equal(extension, ExtensionOf(file));
        Assert.Equal(psd.Length, file.Size);
    }

    [Fact]
    public void Psd_WithZipCompressedImageData_IsNotCarved() => AssertNothing(Psd(compression: 2));

    [Fact]
    public void Psd_WithAnImpossibleDepth_IsNotCarved()
    {
        var psd = Psd(0);
        Be16(psd, 22, 5);

        AssertNothing(psd);
    }

    [Fact]
    public void Psd_WithNonZeroReservedBytes_IsNotCarved()
    {
        var psd = Psd(0);
        psd[8] = 1;

        AssertNothing(psd);
    }

    [Fact]
    public void Psd_CutShort_IsNotCarved() => AssertTruncated(Psd(0, width: 400, height: 300), sectors: 8);

    // ---- EXE / DLL ----

    private static byte[] Pe(bool dll, long signatureSize = 0)
    {
        const int PeOffset = 0x80, OptionalSize = 0xF0, OptionalStart = PeOffset + 24, Table = OptionalStart + OptionalSize;
        const int SectionsEnd = 0x600;
        var b = new byte[SectionsEnd + signatureSize];

        b[0] = (byte)'M'; b[1] = (byte)'Z';
        Le32(b, 0x3C, PeOffset);
        Ascii("PE\0\0").CopyTo(b, PeOffset);
        Le16(b, PeOffset + 4, 0x8664);
        Le16(b, PeOffset + 6, 2);                                  // sections
        Le16(b, PeOffset + 20, OptionalSize);
        Le16(b, PeOffset + 22, dll ? 0x2022 : 0x0022);
        Le16(b, OptionalStart, 0x20B);                             // PE32+
        Le32(b, OptionalStart + 112 + 4 * 8, signatureSize > 0 ? SectionsEnd : 0);
        Le32(b, OptionalStart + 112 + 4 * 8 + 4, signatureSize);

        Le32(b, Table + 16, 0x200); Le32(b, Table + 20, 0x200);
        Le32(b, Table + 40 + 16, 0x200); Le32(b, Table + 40 + 20, 0x400);
        return b;
    }

    [Theory]
    [InlineData(false, "exe")]
    [InlineData(true, "dll")]
    public void Pe_EndsAfterItsLastSection(bool dll, string extension)
    {
        var pe = Pe(dll);

        var file = Single(pe);

        Assert.Equal(extension, ExtensionOf(file));
        Assert.Equal(pe.Length, file.Size);
    }

    [Fact]
    public void Pe_WithASignature_IncludesIt()
    {
        var pe = Pe(dll: false, signatureSize: 0x180);

        var file = Single(pe);

        Assert.Equal(0x600 + 0x180, file.Size);
    }

    [Fact]
    public void Pe_OnlyMzWithNoPeHeader_IsNotCarved()
    {
        var notPe = new byte[0x600];
        notPe[0] = (byte)'M'; notPe[1] = (byte)'Z';

        AssertNothing(notPe);
    }

    [Fact]
    public void Pe_CutShort_IsNotCarved() => AssertTruncated(Pe(dll: false), sectors: 2);

    // ---- all together ----

    [Fact]
    public void Carve_MixedFormats_AreAllFound_AndNothingIsCountedTwice()
    {
        var volume = FileCarverTests.Volume(64 * Sector,
            (0, Bmp()), (4 * Sector, Riff("WAVE")), (8 * Sector, Gif()), (10 * Sector, Sqlite()),
            (16 * Sector, SevenZip()), (20 * Sector, Mp4()), (24 * Sector, Psd(0)), (28 * Sector, Pe(dll: true)));

        var files = FileCarverTests.Carve(volume);

        Assert.Equal(["bmp", "wav", "gif", "sqlite", "7z", "mp4", "psd", "dll"], files.Select(ExtensionOf));
    }
}
