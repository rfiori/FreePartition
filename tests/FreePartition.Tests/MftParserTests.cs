using System.Buffers.Binary;
using System.Text;
using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class MftParserTests
{
    // ---------- helpers to build synthetic MFT records ----------

    private static void W16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), v);
    private static void W32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);

    private static int WriteResidentAttribute(byte[] r, int off, uint type, byte[] content)
    {
        int size = (0x18 + content.Length + 7) & ~7;           // aligned to 8 bytes
        W32(r, off, type);
        W32(r, off + 4, (uint)size);
        r[off + 8] = 0;                                        // resident
        r[off + 9] = 0;                                        // unnamed
        W16(r, off + 0x0A, 0x18);
        W32(r, off + 0x10, (uint)content.Length);
        W16(r, off + 0x14, 0x18);
        content.CopyTo(r, off + 0x18);
        return off + size;
    }

    private static byte[] FileNameContent(string name, long parent)
    {
        var c = new byte[0x42 + name.Length * 2];
        BinaryPrimitives.WriteInt64LittleEndian(c, parent | (1L << 48));                // parent number + sequence
        BinaryPrimitives.WriteInt64LittleEndian(c.AsSpan(0x10), 133_500_000_000_000_000L);   // modified (FILETIME)
        c[0x40] = (byte)name.Length;
        c[0x41] = 1;                                                                    // Win32 namespace
        Encoding.Unicode.GetBytes(name).CopyTo(c, 0x42);
        return c;
    }

    private static byte[] CreateRecord(bool inUse, bool directory, string name, long parent, byte[]? data)
    {
        var r = new byte[1024];
        "FILE"u8.CopyTo(r);
        W16(r, 0x04, 0x30);          // update sequence array offset
        W16(r, 0x06, 3);             // 1 USN + 2 sectors
        W16(r, 0x14, 0x38);          // first attribute
        W16(r, 0x16, (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
        W32(r, 0x18, 1024);
        W32(r, 0x1C, 1024);

        // USA: USN = 0x0001; original end-of-sector values = 0x0000
        W16(r, 0x30, 1);
        W16(r, 0x32, 0);
        W16(r, 0x34, 0);
        W16(r, 510, 1);              // marker at the end of sector 1
        W16(r, 1022, 1);             // marker at the end of sector 2

        int off = 0x38;
        off = WriteResidentAttribute(r, off, 0x30, FileNameContent(name, parent));
        if (data is not null)
            off = WriteResidentAttribute(r, off, 0x80, data);
        W32(r, off, 0xFFFFFFFF);     // end of attributes
        return r;
    }

    // ---------- records ----------

    [Fact]
    public void ResidentDeletedFile_ReadsNameParentAndContent()
    {
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = CreateRecord(inUse: false, directory: false, "report.txt", parent: 5, content);

        var record = MftParser.Parse(buffer, 42);

        Assert.NotNull(record);
        Assert.Equal(42, record!.Number);
        Assert.False(record.InUse);
        Assert.False(record.IsDirectory);
        Assert.Equal("report.txt", record.Name);
        Assert.Equal(5, record.ParentNumber);
        Assert.Equal(5, record.RealSize);
        Assert.Equal(content, record.ResidentData);
        Assert.NotNull(record.Modified);
        Assert.Equal(2024, record.Modified!.Value.Year);
    }

    [Fact]
    public void InUseFile_IsIgnored_WhenRequested()
    {
        var buffer = CreateRecord(inUse: true, directory: false, "active.txt", parent: 5, new byte[] { 9 });

        Assert.Null(MftParser.Parse(buffer, 1, includeInUseFiles: false));
        Assert.NotNull(MftParser.Parse(CreateRecord(true, false, "active.txt", 5, new byte[] { 9 }), 1));
    }

    [Fact]
    public void InUseDirectory_IsNotIgnored()
    {
        var buffer = CreateRecord(inUse: true, directory: true, "Documents", parent: 5, data: null);

        var record = MftParser.Parse(buffer, 30, includeInUseFiles: false);

        Assert.NotNull(record);
        Assert.True(record!.IsDirectory);
        Assert.Equal("Documents", record.Name);
    }

    [Fact]
    public void Fixup_RestoresTheOriginalEndOfSectorBytes()
    {
        var buffer = CreateRecord(false, false, "a.txt", 5, new byte[] { 1 });
        W16(buffer, 0x32, 0xBBAA);   // original value of the end of sector 1
        W16(buffer, 0x34, 0xDDCC);   // original value of the end of sector 2

        Assert.NotNull(MftParser.Parse(buffer, 1));

        Assert.Equal(0xBBAA, BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(510)));
        Assert.Equal(0xDDCC, BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(1022)));
    }

    [Fact]
    public void Fixup_WithCorruptedMarker_ReturnsNull()
    {
        var buffer = CreateRecord(false, false, "a.txt", 5, new byte[] { 1 });
        W16(buffer, 1022, 0x7777);   // marker different from the USN

        Assert.Null(MftParser.Parse(buffer, 1));
    }

    [Fact]
    public void InvalidSignature_ReturnsNull()
    {
        var buffer = new byte[1024];
        Assert.Null(MftParser.Parse(buffer, 0));
    }

    [Fact]
    public void ExtensionRecord_IsIgnored()
    {
        var buffer = CreateRecord(false, false, "a.txt", 5, new byte[] { 1 });
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x20), 77);   // reference to the base record

        Assert.Null(MftParser.Parse(buffer, 1));
    }

    // ---------- data runs ----------

    [Fact]
    public void DecodeRuns_HandlesPositiveNegativeAndSparseDeltas()
    {
        byte[] data =
        {
            0x21, 0x18, 0x34, 0x56,   // 0x18 clusters starting at LCN 0x5634
            0x11, 0x10, 0xF0,         // 0x10 clusters, offset -16  => LCN 0x5624
            0x01, 0x08,               // 8 sparse clusters
            0x00                      // end
        };

        var runs = MftParser.DecodeRuns(data);

        Assert.Equal(3, runs.Count);
        Assert.Equal(new DataRun(0x5634, 0x18), runs[0]);
        Assert.Equal(new DataRun(0x5624, 0x10), runs[1]);
        Assert.Equal(new DataRun(-1, 8), runs[2]);
        Assert.True(runs[2].IsSparse);
    }

    // ---------- boot sector ----------

    [Fact]
    public void BootSector_ReadsNtfsParameters()
    {
        var sector = new byte[512];
        "NTFS    "u8.CopyTo(sector.AsSpan(3));
        W16(sector, 0x0B, 512);
        sector[0x0D] = 8;
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(0x28), 1_000_000);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(0x30), 786_432);
        sector[0x40] = 0xF6;           // -10 => record of 2^10 = 1024 bytes

        var boot = NtfsBootSector.Read(sector);

        Assert.Equal(4096, boot.BytesPerCluster);
        Assert.Equal(786_432, boot.MftStartCluster);
        Assert.Equal(1024, boot.MftRecordSize);
        Assert.Equal(125_000, boot.TotalClusters);
    }

    [Fact]
    public void BootSector_NotNtfs_ThrowsException()
    {
        var sector = new byte[512];
        "EXFAT   "u8.CopyTo(sector.AsSpan(3));

        Assert.Throws<InvalidDataException>(() => NtfsBootSector.Read(sector));
    }
}
