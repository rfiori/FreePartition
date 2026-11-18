using System.Buffers.Binary;
using System.Text;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class FileSystemUsageTests
{
    private static FreePartition.Core.Ntfs.ReadAt Reader(byte[] image) => (offset, destination) =>
    {
        if (offset < 0 || offset >= image.Length) return 0;
        int count = (int)Math.Min(destination.Length, image.Length - offset);
        image.AsSpan((int)offset, count).CopyTo(destination);
        return count;
    };

    // ---- NTFS: 16384 sectors of 512 bytes, 4 KiB clusters (2048), MFT at cluster 4, $Bitmap in cluster 100

    private const int NtfsClusters = 2048, ClusterBytes = 4096, RecordBytes = 1024;

    private static byte[] NtfsImage(int usedClusters, string? label)
    {
        var image = new byte[(16384 + 1) * 512];
        var boot = image.AsSpan(0, 512);
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[0x0B..], 512);
        boot[0x0D] = 8;
        BinaryPrimitives.WriteInt64LittleEndian(boot[0x28..], 16384);
        BinaryPrimitives.WriteInt64LittleEndian(boot[0x30..], 4);
        boot[0x40] = 0xF6;   // 1 KiB records
        boot[510] = 0x55; boot[511] = 0xAA;

        long mft = 4L * ClusterBytes;

        // Record 6 ($Bitmap): non-resident unnamed $DATA, one run of 1 cluster at LCN 100.
        var bitmap = Record(image, mft + 6 * RecordBytes, out int at);
        var data = bitmap[at..];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], 0x48);
        data[8] = 1;                                                   // non-resident
        BinaryPrimitives.WriteUInt16LittleEndian(data[0x20..], 0x40);  // runs offset
        BinaryPrimitives.WriteInt64LittleEndian(data[0x28..], ClusterBytes);
        BinaryPrimitives.WriteInt64LittleEndian(data[0x30..], NtfsClusters / 8);
        BinaryPrimitives.WriteInt64LittleEndian(data[0x38..], NtfsClusters / 8);
        data[0x40] = 0x11; data[0x41] = 1; data[0x42] = 100;           // 1 cluster at LCN 100
        BinaryPrimitives.WriteUInt32LittleEndian(data[0x48..], 0xFFFFFFFF);

        for (int c = 0; c < usedClusters; c++)
            image[100 * ClusterBytes + c / 8] |= (byte)(1 << (c % 8));

        // Record 3 ($Volume): resident $VOLUME_NAME.
        var volume = Record(image, mft + 3 * RecordBytes, out at);
        var name = Encoding.Unicode.GetBytes(label ?? "");
        var attribute = volume[at..];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], (uint)(0x18 + (name.Length + 7) / 8 * 8));
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x10..], (uint)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x14..], 0x18);
        name.CopyTo(attribute[0x18..]);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[(0x18 + (name.Length + 7) / 8 * 8)..], 0xFFFFFFFF);

        return image;
    }

    /// <summary>An in-use FILE record with its fixups in place; <paramref name="attributes"/> is where the first attribute goes.</summary>
    private static Span<byte> Record(byte[] image, long offset, out int attributes)
    {
        var record = image.AsSpan((int)offset, RecordBytes);
        Encoding.ASCII.GetBytes("FILE").CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x04..], 0x30);   // update sequence array
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x06..], 3);      // USN + 2 sectors
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x14..], 0x38);   // first attribute
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x16..], 1);      // in use
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x30..], 7);      // USN (the original sector ends are zero)
        BinaryPrimitives.WriteUInt16LittleEndian(record[510..], 7);
        BinaryPrimitives.WriteUInt16LittleEndian(record[1022..], 7);
        attributes = 0x38;
        return record;
    }

    [Fact]
    public void Ntfs_CountsTheBitmapAndReadsTheLabel()
    {
        var image = NtfsImage(usedClusters: 50, label: "Backup");

        var usage = FileSystemUsageReader.Read(Reader(image), (ulong)image.Length);

        Assert.NotNull(usage);
        Assert.Equal(FileSystemFormat.NTFS, usage!.FileSystem);
        Assert.Equal((ulong)NtfsClusters * ClusterBytes, usage.Size);
        Assert.Equal((ulong)(NtfsClusters - 50) * ClusterBytes, usage.Free);
        Assert.Equal("Backup", usage.Label);
    }

    [Fact]
    public void Ntfs_WithoutLabel_LeavesItEmpty()
    {
        var image = NtfsImage(usedClusters: 0, label: null);

        var usage = FileSystemUsageReader.Read(Reader(image), (ulong)image.Length);

        Assert.Equal((ulong)NtfsClusters * ClusterBytes, usage!.Free);
        Assert.Null(usage.Label);
    }

    [Fact]
    public void Ntfs_BrokenBitmapRecord_IsUnknown()
    {
        var image = NtfsImage(usedClusters: 10, label: null);
        image[4 * ClusterBytes + 6 * RecordBytes] = (byte)'X';   // "XILE"

        Assert.Null(FileSystemUsageReader.Read(Reader(image), (ulong)image.Length));
    }

    // ---- FAT32: 70000 sectors of 512 bytes, 1 sector per cluster, 32 reserved, 2 FATs of 548 sectors

    private const int FatSectors = 548, FatReserved = 32, FatClusters = 70000 - FatReserved - 2 * FatSectors;

    private static byte[] Fat32Image(int usedClusters, string? label)
    {
        var image = new byte[70000 * 512];
        var boot = image.AsSpan(0, 512);
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[0x0B..], 512);
        boot[0x0D] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[0x0E..], FatReserved);
        boot[0x10] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(boot[0x20..], 70000);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[0x24..], FatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[0x2C..], 2);   // root directory in cluster 2
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot[0x52..]);
        boot[510] = 0x55; boot[511] = 0xAA;

        var fat = image.AsSpan(FatReserved * 512, FatSectors * 512);
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0x0FFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[4..], 0x0FFFFFFF);
        for (int c = 2; c < 2 + usedClusters; c++)
            BinaryPrimitives.WriteUInt32LittleEndian(fat[(c * 4)..], 0x0FFFFFFF);
        // A free entry with the reserved top bits set still counts as free.
        BinaryPrimitives.WriteUInt32LittleEndian(fat[((2 + usedClusters) * 4)..], 0xF0000000);

        var root = image.AsSpan((FatReserved + 2 * FatSectors) * 512, 512);
        Encoding.ASCII.GetBytes("DELETED    ").CopyTo(root);
        root[0] = 0xE5; root[11] = 0x08;                                   // a deleted label is skipped
        Encoding.ASCII.GetBytes("FILE    TXT").CopyTo(root[32..]);         // a file is not a label
        if (label is not null)
        {
            Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(root[64..]);
            root[64 + 11] = 0x08;
        }
        return image;
    }

    [Fact]
    public void Fat32_CountsTheFreeEntriesAndReadsTheLabel()
    {
        var image = Fat32Image(usedClusters: 3, label: "PHOTOS");

        var usage = FileSystemUsageReader.Read(Reader(image), (ulong)image.Length);

        Assert.NotNull(usage);
        Assert.Equal(FileSystemFormat.FAT32, usage!.FileSystem);
        Assert.Equal((ulong)FatClusters * 512, usage.Size);
        Assert.Equal((ulong)(FatClusters - 3) * 512, usage.Free);
        Assert.Equal("PHOTOS", usage.Label);
    }

    [Fact]
    public void Fat32_WithoutLabelEntry_LeavesItEmpty()
    {
        var usage = FileSystemUsageReader.Read(Reader(Fat32Image(usedClusters: 1, label: null)), 70000 * 512);

        Assert.Null(usage!.Label);
    }

    // ---- exFAT: 4 KiB clusters, FAT at sector 128, heap at sector 256, 1000 clusters; bitmap in 2, up-case in 3, root in 4

    private const int ExClusters = 1000;

    private static byte[] ExFatImage(int usedClusters, string? label)
    {
        long sectors = 256 + ExClusters * 8;
        var image = new byte[sectors * 512];
        var boot = image.AsSpan(0, 512);
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt64LittleEndian(boot[0x48..], (ulong)sectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[0x50..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[0x54..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[0x58..], 256);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[0x5C..], ExClusters);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[0x60..], 4);
        boot[0x6C] = 9; boot[0x6D] = 3; boot[0x6E] = 1;
        boot[510] = 0x55; boot[511] = 0xAA;

        long Cluster(long c) => 256 * 512 + (c - 2) * ClusterBytes;

        var fat = image.AsSpan(128 * 512, 8 * 512);
        for (int c = 2; c <= 4; c++)
            BinaryPrimitives.WriteUInt32LittleEndian(fat[(c * 4)..], 0xFFFFFFFF);

        for (int c = 0; c < usedClusters; c++)
            image[Cluster(2) + c / 8] |= (byte)(1 << (c % 8));

        var root = image.AsSpan((int)Cluster(4), ClusterBytes);
        if (label is not null)
        {
            root[0] = 0x83;
            root[1] = (byte)label.Length;
            Encoding.Unicode.GetBytes(label).CopyTo(root[2..]);
        }
        var entry = root[32..];
        entry[0] = 0x81;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[24..], (ExClusters + 7) / 8);
        return image;
    }

    [Fact]
    public void ExFat_CountsTheAllocationBitmapAndReadsTheLabel()
    {
        var image = ExFatImage(usedClusters: 3, label: "Camera");

        var usage = FileSystemUsageReader.Read(Reader(image), (ulong)image.Length);

        Assert.NotNull(usage);
        Assert.Equal(FileSystemFormat.exFAT, usage!.FileSystem);
        Assert.Equal((ulong)ExClusters * ClusterBytes, usage.Size);
        Assert.Equal((ulong)(ExClusters - 3) * ClusterBytes, usage.Free);
        Assert.Equal("Camera", usage.Label);
    }

    // ---- what is not a volume of this partition

    [Fact]
    public void NoBootSector_IsUnknown()
    {
        Assert.Null(FileSystemUsageReader.Read(Reader(new byte[1 << 20]), 1 << 20));
    }

    [Fact]
    public void VolumeLargerThanThePartition_IsUnknown()
    {
        var image = NtfsImage(usedClusters: 1, label: null);

        Assert.Null(FileSystemUsageReader.Read(Reader(image), (ulong)image.Length - 512));
    }
}
