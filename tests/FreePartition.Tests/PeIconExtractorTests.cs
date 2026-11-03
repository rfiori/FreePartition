using System.Buffers.Binary;
using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class PeIconExtractorTests
{
    private const int RsrcRaw = 0x200;       // where the resource section sits in the file
    private const uint RsrcRva = 0x1000;     // and in memory
    private static readonly byte[] IconData = Enumerable.Range(0, 40).Select(i => (byte)(0xA0 + i)).ToArray();

    /// <summary>
    /// A minimal PE32+ with one .rsrc section holding one group icon (a single 16x16 image, id 1) and that image.
    /// Resource layout (relative to the section): type dirs at 0x00, icon name dir 0x20, icon language dir 0x40,
    /// icon data entry 0x60, group type dir 0x80, group name dir 0xA0, group data entry 0xC0, group data 0xE0, icon data 0x100.
    /// </summary>
    private static byte[] BuildPe(bool pe32Plus = true, bool withGroup = true, bool withIconImage = true)
    {
        var file = new byte[RsrcRaw + 0x100 + IconData.Length];
        file[0] = (byte)'M';
        file[1] = (byte)'Z';
        W32(file, 0x3C, 0x80);

        int pe = 0x80;
        W32(file, pe, 0x00004550);
        W16(file, pe + 6, 1);                                   // one section
        int optionalSize = pe32Plus ? 240 : 224;
        W16(file, pe + 20, optionalSize);

        int optional = pe + 24;
        W16(file, optional, pe32Plus ? 0x20B : 0x10B);
        int directories = optional + (pe32Plus ? 112 : 96);
        W32(file, directories - 4, 16);
        W32(file, directories + 16, RsrcRva);                   // resource directory
        W32(file, directories + 20, 0x200);

        int section = optional + optionalSize;
        W32(file, section + 8, 0x200);                          // virtual size
        W32(file, section + 12, RsrcRva);
        W32(file, section + 16, 0x100 + IconData.Length);       // raw size
        W32(file, section + 20, RsrcRaw);

        int r = RsrcRaw;
        // Root: types 3 (icon) and 14 (group icon); the group type is left out when asked.
        W16(file, r + 14, withGroup ? 2 : 1);
        W32(file, r + 16, 3); W32(file, r + 20, 0x80000000 | 0x20);
        if (withGroup) { W32(file, r + 24, 14); W32(file, r + 28, 0x80000000 | 0x80); }

        Directory(file, r + 0x20, id: 1, target: 0x80000000 | 0x40);       // icon id 1
        Directory(file, r + 0x40, id: 0x409, target: 0x60);                // language -> data entry
        W32(file, r + 0x60, RsrcRva + 0x100);
        W32(file, r + 0x64, withIconImage ? IconData.Length : 0);

        Directory(file, r + 0x80, id: 1, target: 0x80000000 | 0xA0);       // group id 1
        Directory(file, r + 0xA0, id: 0x409, target: 0xC0);
        W32(file, r + 0xC0, RsrcRva + 0xE0);
        W32(file, r + 0xC4, 20);

        int g = r + 0xE0;
        W16(file, g + 2, 1);                                    // type: icon
        W16(file, g + 4, 1);                                    // one image
        file[g + 6] = 16; file[g + 7] = 16;                     // 16x16
        W16(file, g + 10, 1);                               // planes
        W16(file, g + 12, 32);                              // bits per pixel
        W32(file, g + 14, IconData.Length);
        W16(file, g + 18, 1);                               // icon resource id

        IconData.CopyTo(file, r + 0x100);
        return file;
    }

    /// <summary>A directory with a single numbered entry.</summary>
    private static void Directory(byte[] file, int at, uint id, uint target)
    {
        W16(file, at + 14, 1);
        W32(file, at + 16, id);
        W32(file, at + 20, target);
    }

    private static void W16(byte[] d, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), (ushort)value);
    private static void W32(byte[] d, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), value);
    private static void W32(byte[] d, int at, int value) => W32(d, at, (uint)value);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extract_BuildsAnIcoFromTheGroupAndItsImage(bool pe32Plus)
    {
        var ico = PeIconExtractor.Extract(BuildPe(pe32Plus));

        Assert.NotNull(ico);
        Assert.Equal(6 + 16 + IconData.Length, ico.Length);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));    // type: icon
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));    // one image
        Assert.Equal(16, ico[6]);                                                  // width
        Assert.Equal(16, ico[7]);                                                  // height
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(12)));  // bits per pixel
        Assert.Equal((uint)IconData.Length, BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(14)));
        Assert.Equal(22u, BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(18)));  // image offset
        Assert.Equal(IconData, ico.AsSpan(22).ToArray());
    }

    [Fact]
    public void Extract_ReturnsNull_WhenThereIsNoIconGroup() =>
        Assert.Null(PeIconExtractor.Extract(BuildPe(withGroup: false)));

    [Fact]
    public void Extract_ReturnsNull_WhenTheImageIsMissing() =>
        Assert.Null(PeIconExtractor.Extract(BuildPe(withIconImage: false)));

    [Fact]
    public void Extract_ReturnsNull_WhenTheFileIsCutBeforeTheResources() =>
        Assert.Null(PeIconExtractor.Extract(BuildPe().AsSpan(0, RsrcRaw + 0x50).ToArray()));

    [Fact]
    public void Extract_ReturnsNull_WhenItIsNotAPeFile()
    {
        var file = BuildPe();
        file[0] = (byte)'X';
        Assert.Null(PeIconExtractor.Extract(file));
        Assert.Null(PeIconExtractor.Extract([]));
        Assert.Null(PeIconExtractor.Extract(new byte[4096]));
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTheHeaderPointsOutsideTheFile()
    {
        var file = BuildPe();
        W32(file, 0x3C, 0x7FFFFFF0);   // e_lfanew far past the end
        Assert.Null(PeIconExtractor.Extract(file));
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("LIB.DLL", true)]
    [InlineData("a.scr", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("carved_00001.zip", false)]
    public void HasIcon_ChecksTheExtension(string name, bool expected) =>
        Assert.Equal(expected, FilePreviewReader.HasIcon(name));

    [Fact]
    public void ReadIcon_ReadsTheIconOfAResidentProgram()
    {
        var pe = BuildPe();
        var scan = new ScanResult("Z", 4096, 0, TimeSpan.Zero, []);
        var file = new DeletedFile(1, "tool.exe", @"\", pe.Length, null, RecoveryChance.High, 1.0, [], pe);

        var ico = FilePreviewReader.ReadIcon(scan, file);

        Assert.NotNull(ico);
        Assert.Equal(IconData, ico.AsSpan(22).ToArray());
    }

    [Fact]
    public void ReadThumbnailSource_ReturnsTheWholeFile_ForImagesAndPrograms_AndNullOtherwise()
    {
        var pe = BuildPe();
        var scan = new ScanResult("Z", 4096, 0, TimeSpan.Zero, []);
        DeletedFile File(string name, long size, RecoveryChance chance = RecoveryChance.High) =>
            new(1, name, @"\", size, null, chance, 1.0, [], pe);

        Assert.Equal(pe, FilePreviewReader.ReadThumbnailSource(scan, File("tool.exe", pe.Length)));
        Assert.Equal(pe.Length, FilePreviewReader.ReadThumbnailSource(scan, File("photo.jpg", pe.Length))!.Length);
        Assert.Null(FilePreviewReader.ReadThumbnailSource(scan, File("notes.txt", pe.Length)));
        Assert.Null(FilePreviewReader.ReadThumbnailSource(scan, File("photo.jpg", FilePreviewReader.MaxImageBytes + 1L)));
        Assert.Null(FilePreviewReader.ReadThumbnailSource(scan, File("tool.exe", FilePreviewReader.MaxProgramBytes + 1L)));
        Assert.Null(FilePreviewReader.ReadThumbnailSource(scan, File("photo.jpg", pe.Length, RecoveryChance.Unavailable)));
    }

    [Fact]
    public void ReadIcon_ReturnsNull_ForOtherFilesAndOverwrittenData()
    {
        var pe = BuildPe();
        var scan = new ScanResult("Z", 4096, 0, TimeSpan.Zero, []);

        var notAProgram = new DeletedFile(1, "photo.jpg", @"\", pe.Length, null, RecoveryChance.High, 1.0, [], pe);
        var noData = new DeletedFile(2, "tool.exe", @"\", pe.Length, null, RecoveryChance.Unavailable, 1.0, [], pe);

        Assert.Null(FilePreviewReader.ReadIcon(scan, notAProgram));
        Assert.Null(FilePreviewReader.ReadIcon(scan, noData));
    }
}
