using System.IO.Compression;
using System.Text;
using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class FileCarverTests
{
    private const int Sector = FileCarver.SectorSize;

    /// <summary>A reader over an in-memory volume, like RawVolume.Read.</summary>
    internal static ReadAt Over(byte[] data) => (offset, destination) =>
    {
        long remaining = data.Length - offset;
        if (remaining <= 0) return 0;
        int n = (int)Math.Min(destination.Length, remaining);
        data.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    };

    /// <summary>A volume full of filler (never a signature or a digit) with the given files at the given offsets.</summary>
    internal static byte[] Volume(int size, params (int Offset, byte[] Data)[] parts)
    {
        var image = new byte[size];
        Array.Fill(image, (byte)0xAA);
        foreach (var (offset, data) in parts)
            data.CopyTo(image, offset);
        return image;
    }

    internal static List<DeletedFile> Carve(byte[] volume) => FileCarver.Carve(Over(volume), volume.Length).Files;

    private static byte[] Jpeg(int scanBytes = 300, byte[]? embedded = null)
    {
        var b = new List<byte> { 0xFF, 0xD8 };
        b.AddRange([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (embedded is not null)
        {
            b.AddRange([0xFF, 0xE1, (byte)((embedded.Length + 2) >> 8), (byte)((embedded.Length + 2) & 0xFF)]);
            b.AddRange(embedded);
        }
        b.AddRange([0xFF, 0xC0, 0x00, 0x0B, 8, 0, 1, 0, 1, 1, 1, 0x11, 0]);
        b.AddRange([0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 0x3F, 0]);
        for (int i = 0; i < scanBytes; i++)
        {
            b.Add((byte)(i * 7 % 250));
            if (i % 50 == 0) b.AddRange([0xFF, 0x00]);     // stuffed byte
            if (i % 97 == 0) b.AddRange([0xFF, 0xD0]);     // restart marker
        }
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    /// <summary>A JPEG whose comment segment holds another complete JPEG that starts on a sector boundary.</summary>
    private static byte[] JpegContainingAlignedJpeg(byte[] inner)
    {
        const int Padding = Sector - 6;   // SOI + COM marker + length = 6 bytes, so the payload after the padding is aligned
        int length = 2 + Padding + inner.Length;
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xFE, (byte)(length >> 8), (byte)(length & 0xFF) };
        b.AddRange(new byte[Padding]);
        b.AddRange(inner);
        b.AddRange(Jpeg()[2..]);   // the rest of an ordinary JPEG, without its SOI
        return [.. b];
    }

    private static byte[] Png(int dataBytes = 100)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        void Chunk(string type, byte[] data)
        {
            b.AddRange([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            b.AddRange(Encoding.ASCII.GetBytes(type));
            b.AddRange(data);
            b.AddRange([1, 2, 3, 4]);   // CRC
        }
        Chunk("IHDR", new byte[13]);
        Chunk("IDAT", Enumerable.Range(0, dataBytes).Select(i => (byte)(i % 200)).ToArray());
        Chunk("IEND", []);
        return [.. b];
    }

    internal static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Zip(CompressionLevel level, params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var entryStream = archive.CreateEntry(name, level).Open();
                entryStream.Write(data);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public void Carve_FindsEachFormat_WithExactSizeAndPosition()
    {
        var jpeg = Jpeg();
        var png = Png();
        var pdf = Text("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\nstartxref\n9\n%%EOF\n");
        var zip = Zip(CompressionLevel.Optimal, ("a.txt", Text("hello")));
        var volume = Volume(16 * Sector,
            (0, jpeg), (2 * Sector, png), (4 * Sector, pdf), (6 * Sector, zip));

        var files = Carve(volume);

        Assert.Equal(["jpg", "png", "pdf", "zip"], files.Select(f => Path.GetExtension(f.Name).TrimStart('.')));
        Assert.Equal([jpeg.Length, png.Length, pdf.Length, zip.Length], files.Select(f => (int)f.Size));
        Assert.Equal([0L, 2L, 4L, 6L], files.Select(f => f.Runs[0].Lcn));
    }

    [Fact]
    public void Carve_Result_IsReadableThroughItsRun_AndMatchesTheOriginalBytes()
    {
        var png = Png(3000);
        var volume = Volume(16 * Sector, (3 * Sector, png));

        var file = Assert.Single(Carve(volume));

        var run = Assert.Single(file.Runs);
        Assert.Equal(3, run.Lcn);
        Assert.Equal((png.Length + Sector - 1) / Sector, run.Clusters);
        Assert.Equal(png, volume.AsSpan((int)(run.Lcn * Sector), (int)file.Size).ToArray());
    }

    [Fact]
    public void Carve_NamesAndFoldersTheFiles_AndTheyCanBeRecovered()
    {
        var volume = Volume(8 * Sector, (0, Jpeg()), (2 * Sector, Png()));

        var files = Carve(volume);

        Assert.Equal(["carved_00001.jpg", "carved_00002.png"], files.Select(f => f.Name));
        Assert.Equal([@"\Carved\JPG", @"\Carved\PNG"], files.Select(f => f.Path));
        Assert.All(files, f =>
        {
            Assert.Equal(RecoveryChance.Carved, f.Chance);
            Assert.True(f.CanRecover);
            Assert.Null(f.Modified);
        });
        Assert.Equal(2, files.Select(f => f.MftRecordNumber).Distinct().Count());
    }

    [Fact]
    public void Jpeg_WithEmbeddedThumbnail_IsNotCutAtTheThumbnailsEnd()
    {
        var thumbnail = new byte[] { 0xFF, 0xD8, 0x11, 0x22, 0xFF, 0xD9 };
        var jpeg = Jpeg(embedded: thumbnail);

        var file = Assert.Single(Carve(Volume(8 * Sector, (0, jpeg))));

        Assert.Equal(jpeg.Length, file.Size);
    }

    [Fact]
    public void Jpeg_StuffedBytesAndRestartMarkers_AreNotTakenForTheEnd()
    {
        var jpeg = Jpeg(scanBytes: 2000);

        var file = Assert.Single(Carve(Volume(16 * Sector, (0, jpeg))));

        Assert.Equal(jpeg.Length, file.Size);
    }

    [Fact]
    public void Jpeg_WithoutEndMarker_IsNotCarved()
    {
        var jpeg = Jpeg()[..^2];   // cut off the EOI

        Assert.Empty(Carve(Volume(8 * Sector, (0, jpeg))));
    }

    [Fact]
    public void Carve_AFileInsideAnotherOne_IsNotReportedTwice()
    {
        var inner = Jpeg(scanBytes: 50);
        var outer = JpegContainingAlignedJpeg(inner);
        var volume = Volume(16 * Sector, (0, outer));

        var file = Assert.Single(Carve(volume));

        Assert.Equal(outer.Length, file.Size);
    }

    [Fact]
    public void Png_EndsAfterIend_EvenWithJunkAfterIt()
    {
        var png = Png(5000);

        var file = Assert.Single(Carve(Volume(32 * Sector, (Sector, png))));

        Assert.Equal(png.Length, file.Size);
    }

    [Fact]
    public void Png_WithoutIhdrFirst_IsNotCarved()
    {
        var png = Png();
        png[12] = (byte)'X';   // first chunk type

        Assert.Empty(Carve(Volume(8 * Sector, (0, png))));
    }

    [Fact]
    public void Png_Truncated_IsNotCarved()
    {
        var png = Png();

        Assert.Empty(Carve(Volume(8 * Sector, (0, png[..^20]))));
    }

    [Fact]
    public void Pdf_WithSeveralEofMarkers_EndsAtTheLastOne()
    {
        var pdf = Text("%PDF-1.5\n%%EOF\n2 0 obj\n<<>>\nendobj\nstartxref\n120\n%%EOF\n");

        var file = Assert.Single(Carve(Volume(8 * Sector, (0, pdf))));

        Assert.Equal(pdf.Length, file.Size);
    }

    [Fact]
    public void Pdf_EofFollowedByCrLf_IncludesTheLineBreak()
    {
        var pdf = Text("%PDF-1.4\r\nstartxref\r\n9\r\n%%EOF\r\n");

        var file = Assert.Single(Carve(Volume(8 * Sector, (0, pdf))));

        Assert.Equal(pdf.Length, file.Size);
    }

    [Fact]
    public void Pdf_WithoutEof_IsNotCarved()
    {
        Assert.Empty(Carve(Volume(8 * Sector, (0, Text("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n")))));
    }

    [Fact]
    public void Zip_WithAZipInsideIt_EndsAtTheOuterDirectory()
    {
        var inner = Zip(CompressionLevel.Optimal, ("inner.txt", Text("inside")));
        var outer = Zip(CompressionLevel.NoCompression, ("a.txt", Text("x")), ("inner.zip", inner));

        // The nested archive's end record must really appear before the outer one, or this test proves nothing.
        var eocd = new byte[] { 0x50, 0x4B, 0x05, 0x06 };
        Assert.True(outer.AsSpan().IndexOf(eocd) < outer.AsSpan().LastIndexOf(eocd));

        var file = Assert.Single(Carve(Volume(16 * Sector, (0, outer))));

        Assert.Equal(outer.Length, file.Size);
        Assert.EndsWith(".zip", file.Name);
    }

    [Fact]
    public void Zip_WithComment_IncludesTheComment()
    {
        var zip = Zip(CompressionLevel.Optimal, ("a.txt", Text("hello")));
        var comment = Text("a comment");
        var withComment = zip.Concat(comment).ToArray();
        withComment[withComment.Length - comment.Length - 2] = (byte)comment.Length;   // the comment length field of the end record

        var file = Assert.Single(Carve(Volume(8 * Sector, (0, withComment))));

        Assert.Equal(withComment.Length, file.Size);
    }

    [Theory]
    [InlineData("word/document.xml", "docx")]
    [InlineData("xl/workbook.xml", "xlsx")]
    [InlineData("ppt/presentation.xml", "pptx")]
    [InlineData("other/data.xml", "zip")]
    public void Zip_OfficePackages_GetTheirOwnExtension(string part, string extension)
    {
        var package = Zip(CompressionLevel.Optimal, ("[Content_Types].xml", Text("<Types/>")), (part, Text("<x/>")));

        var file = Assert.Single(Carve(Volume(8 * Sector, (0, package))));

        Assert.Equal(extension, Path.GetExtension(file.Name).TrimStart('.'));
        Assert.Equal($@"\Carved\{extension.ToUpperInvariant()}", file.Path);
    }

    [Fact]
    public void Zip_WithoutContentTypes_IsAPlainZip_EvenWithAWordFolder()
    {
        var zip = Zip(CompressionLevel.Optimal, ("word/notes.txt", Text("x")));

        var file = Assert.Single(Carve(Volume(8 * Sector, (0, zip))));

        Assert.EndsWith(".zip", file.Name);
    }

    [Fact]
    public void Carve_OnlyLooksAtSectorBoundaries()
    {
        Assert.Empty(Carve(Volume(8 * Sector, (100, Png()))));
    }

    [Fact]
    public void Carve_FileCrossingAChunkBoundary_IsStillFound()
    {
        const int Chunk = 4 << 20;
        var png = Png(1500);
        var volume = Volume(Chunk + 4 * Sector, (Chunk - Sector, png));

        var file = Assert.Single(Carve(volume));

        Assert.Equal(png.Length, file.Size);
        Assert.Equal((Chunk - Sector) / Sector, file.Runs[0].Lcn);
    }

    [Fact]
    public void Carve_FilesInDifferentChunks_AreAllFound()
    {
        const int Chunk = 4 << 20;
        var volume = Volume(2 * Chunk + Sector, (0, Png()), (Chunk, Png()), (2 * Chunk, Jpeg(scanBytes: 20)));

        var files = Carve(volume);

        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void Carve_EmptyOrTinyVolume_FindsNothing()
    {
        Assert.Empty(Carve([]));
        Assert.Empty(Carve(new byte[Sector - 1]));
    }

    [Fact]
    public void Carve_ReportsHowManyBytesWereScanned()
    {
        var volume = Volume(8 * Sector);

        var (files, scanned, canceled) = FileCarver.Carve(Over(volume), volume.Length);

        Assert.Empty(files);
        Assert.Equal(volume.Length, scanned);
        Assert.False(canceled);
    }

    [Fact]
    public void Carve_ReadError_IsSkipped_AndTheScanEnds()
    {
        var volume = Volume(8 * Sector, (0, Png()));
        var inner = Over(volume);
        ReadAt failing = (offset, destination) =>
            offset == 0 ? throw new IOException("bad sector") : inner(offset, destination);

        var (files, _, _) = FileCarver.Carve(failing, volume.Length);

        Assert.Empty(files);   // the only file sat in the unreadable chunk; the point is that it neither hangs nor throws
    }

    [Fact]
    public void Carve_AlreadyCanceled_ReturnsAnEmptyPartialResult_WithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (files, scanned, canceled) = FileCarver.Carve(Over(Volume(8 * Sector, (0, Png()))), 8 * Sector, ct: cts.Token);

        Assert.Empty(files);
        Assert.Equal(0, scanned);
        Assert.True(canceled);
    }

    [Fact]
    public void Carve_CanceledPartWay_KeepsWhatWasAlreadyFound()
    {
        const int Chunk = 4 << 20;
        var volume = Volume(3 * Chunk, (0, Png()), (Sector, Jpeg()), (Chunk, Png()), (2 * Chunk, Png()));
        var inner = Over(volume);
        using var cts = new CancellationTokenSource();

        // The user cancels while the second chunk is being read.
        ReadAt cancelingRead = (offset, destination) =>
        {
            int n = inner(offset, destination);
            if (offset >= Chunk) cts.Cancel();
            return n;
        };

        var (files, scanned, canceled) = FileCarver.Carve(cancelingRead, volume.Length, ct: cts.Token);

        Assert.True(canceled);
        Assert.Equal(2, files.Count);                       // both files of the first chunk, nothing of the later ones
        Assert.Equal([0L, 1L], files.Select(f => f.Runs[0].Lcn));
        Assert.InRange(scanned, Chunk, 2 * Chunk);
    }
}
