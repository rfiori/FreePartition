using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Operations;

namespace FreePartition.Core.Disks;

/// <summary>Size, free space and label of a volume, read from its own structures.</summary>
public sealed record FileSystemUsage(FileSystemFormat FileSystem, ulong Size, ulong Free, string? Label);

/// <summary>
/// Reads how full an NTFS, FAT32 or exFAT volume is straight from its structures (boot sector, then the NTFS $Bitmap,
/// the FAT or the exFAT allocation bitmap), for the partitions Windows has no volume for: an offline disk, automount
/// turned off, a hidden partition type. Read-only. Size and free space are counted in clusters, like Windows does.
/// </summary>
public static class FileSystemUsageReader
{
    private const int SectorBytes = 512;
    private const int ChunkBytes = 4 * 1024 * 1024;
    private const int NtfsBitmapRecord = 6;
    private const int NtfsVolumeRecord = 3;
    private const uint VolumeNameType = 0x60;
    private const uint EndOfAttributes = 0xFFFFFFFF;

    /// <param name="read">Reads from an offset relative to the start of the partition.</param>
    /// <param name="partitionSize">The volume must fit in it; anything else is not this partition's file system.</param>
    /// <returns>null when the partition does not start with a valid NTFS, FAT32 or exFAT boot sector, or its structures do not add up.</returns>
    public static FileSystemUsage? Read(ReadAt read, ulong partitionSize)
    {
        ArgumentNullException.ThrowIfNull(read);

        var boot = new byte[SectorBytes];
        if (read(0, boot) < SectorBytes || !BootSectorProbe.TryParse(boot, out var info) || info is null)
            return null;
        // NTFS keeps its backup boot sector right after the volume, so the volume plus that sector fit in the partition.
        if ((ulong)info.Length > partitionSize)
            return null;

        try
        {
            return info.FileSystem switch
            {
                FileSystemFormat.NTFS => ReadNtfs(read, boot),
                FileSystemFormat.FAT32 => ReadFat32(read, boot),
                FileSystemFormat.exFAT => ReadExFat(read, boot),
                _ => null
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ NTFS

    private static FileSystemUsage? ReadNtfs(ReadAt read, byte[] bootSector)
    {
        var boot = NtfsBootSector.Read(bootSector);
        long clusterBytes = boot.BytesPerCluster;
        long totalClusters = boot.TotalClusters;
        long mftOffset = boot.MftStartCluster * clusterBytes;

        // The first records of the MFT ($MFT … $Extend) always sit in its first run, one after the other.
        var record = ReadRecord(read, mftOffset, boot.MftRecordSize, NtfsBitmapRecord);
        if (record is null) return null;
        var bitmap = MftParser.Parse(record, NtfsBitmapRecord);
        if (bitmap is not { InUse: true, HasAttributeList: false, Runs.Count: > 0 } || bitmap.RealSize < (totalClusters + 7) / 8)
            return null;

        long used = 0, bit = 0;
        var buffer = new byte[ChunkBytes];
        foreach (var run in bitmap.Runs)
        {
            if (run.IsSparse) return null;   // the $Bitmap is never sparse
            long runOffset = run.Lcn * clusterBytes;
            long runBytes = run.Clusters * clusterBytes;
            for (long done = 0; done < runBytes && bit < totalClusters; done += ChunkBytes)
            {
                int size = (int)Math.Min(ChunkBytes, runBytes - done);
                if (read(runOffset + done, buffer.AsSpan(0, size)) < size) return null;
                used += CountBits(buffer.AsSpan(0, size), ref bit, totalClusters);
            }
        }
        if (bit < totalClusters) return null;

        string? label = null;
        if (ReadRecord(read, mftOffset, boot.MftRecordSize, NtfsVolumeRecord) is { } volume)
            label = NtfsVolumeLabel(volume);

        ulong volumeSize = (ulong)(totalClusters * clusterBytes);
        return new FileSystemUsage(FileSystemFormat.NTFS, volumeSize, (ulong)((totalClusters - used) * clusterBytes), label);
    }

    private static byte[]? ReadRecord(ReadAt read, long mftOffset, int recordSize, int number)
    {
        var record = new byte[recordSize];
        return read(mftOffset + (long)number * recordSize, record) == recordSize ? record : null;
    }

    /// <summary>The $VOLUME_NAME attribute of the $Volume record (the record is read raw: its fixups are applied here).</summary>
    internal static string? NtfsVolumeLabel(byte[] record)
    {
        if (record.Length < 48 || Encoding.ASCII.GetString(record, 0, 4) != "FILE" || !ApplyFixup(record))
            return null;

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        while (offset + 24 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset));
            if (type == EndOfAttributes) break;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4));
            if (length <= 0 || offset + length > record.Length) break;

            if (type == VolumeNameType && record[offset + 8] == 0)
            {
                int contentSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 0x10));
                int contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset + 0x14));
                if (contentSize < 0 || contentOffset + contentSize > length) return null;
                string name = Encoding.Unicode.GetString(record, offset + contentOffset, contentSize & ~1);
                return name.Length > 0 ? name : null;
            }
            offset += length;
        }
        return null;
    }

    private static bool ApplyFixup(byte[] record)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x04));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x06));
        if (count < 2 || usaOffset + count * 2 > record.Length) return false;
        for (int i = 1; i < count; i++)
        {
            int position = i * SectorBytes - 2;
            if (position + 2 > record.Length) return false;
            if (record[position] != record[usaOffset] || record[position + 1] != record[usaOffset + 1]) return false;
            record[position] = record[usaOffset + i * 2];
            record[position + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    // ------------------------------------------------------------------ FAT32

    private static FileSystemUsage? ReadFat32(ReadAt read, byte[] boot)
    {
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        int sectorsPerCluster = boot[0x0D];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0E));
        int fats = boot[0x10];
        long totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0x20));
        long fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0x24));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0x2C));

        long dataStart = reserved + fats * fatSectors;
        long clusters = (totalSectors - dataStart) / sectorsPerCluster;
        long clusterBytes = (long)sectorsPerCluster * bytesPerSector;
        if (clusters < 1 || clusters * 4 > fatSectors * bytesPerSector) return null;

        // Free clusters are the FAT entries 2 … clusters + 1 that are zero (the top 4 bits of an entry are reserved).
        long free = 0, entry = 0, end = clusters + 2;
        long fatOffset = (long)reserved * bytesPerSector;
        var buffer = new byte[ChunkBytes];
        while (entry < end)
        {
            int size = (int)Math.Min(ChunkBytes, (end - entry) * 4);
            if (read(fatOffset + entry * 4, buffer.AsSpan(0, size)) < size) return null;
            for (int i = 0; i < size; i += 4, entry++)
                if (entry >= 2 && (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) & 0x0FFFFFFF) == 0)
                    free++;
        }

        string? label = null;
        if (rootCluster >= 2 && rootCluster < end)
        {
            var root = new byte[clusterBytes];
            long rootOffset = dataStart * bytesPerSector + (rootCluster - 2) * clusterBytes;
            if (read(rootOffset, root) == root.Length)
                label = Fat32Label(root);
        }

        return new FileSystemUsage(FileSystemFormat.FAT32, (ulong)(clusters * clusterBytes), (ulong)(free * clusterBytes), label);
    }

    /// <summary>The volume label entry (attribute 0x08) of the root directory, as Windows shows it.</summary>
    internal static string? Fat32Label(ReadOnlySpan<byte> root)
    {
        for (int at = 0; at + 32 <= root.Length; at += 32)
        {
            byte first = root[at];
            if (first == 0x00) break;           // end of the directory
            if (first == 0xE5) continue;        // deleted entry
            byte attributes = root[at + 11];
            if (attributes == 0x0F || (attributes & 0x08) == 0) continue;   // long name piece, or not a label
            string name = Encoding.Latin1.GetString(root.Slice(at, 11)).TrimEnd(' ', '\0');
            return name.Length > 0 ? name : null;
        }
        return null;
    }

    // ------------------------------------------------------------------ exFAT

    private static FileSystemUsage? ReadExFat(ReadAt read, byte[] boot)
    {
        long fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0x50));
        long heapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0x58));
        long clusters = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0x5C));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0x60));
        int sectorShift = boot[0x6C];
        int clusterShift = boot[0x6D];
        long clusterBytes = 1L << (sectorShift + clusterShift);
        long heapOffset = heapOffsetSectors << sectorShift;
        long fatOffset = fatOffsetSectors << sectorShift;
        if (clusters < 1 || rootCluster < 2 || rootCluster >= clusters + 2) return null;

        long ClusterOffset(long cluster) => heapOffset + (cluster - 2) * clusterBytes;

        // Next cluster of a chain from the FAT (0xFFFFFFFF ends it).
        long Next(long cluster)
        {
            Span<byte> entry = stackalloc byte[4];
            if (read(fatOffset + cluster * 4, entry) < 4) return -1;
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            return next >= 2 && next < clusters + 2 ? next : -1;
        }

        // The allocation bitmap (0x81) and label (0x83) entries sit at the start of the root directory.
        var root = new byte[clusterBytes];
        if (read(ClusterOffset(rootCluster), root) < root.Length) return null;

        string? label = null;
        long bitmapCluster = -1, bitmapBytes = 0;
        for (int at = 0; at + 32 <= root.Length; at += 32)
        {
            byte type = root[at];
            if (type == 0x00) break;
            if (type == 0x83)
            {
                int count = Math.Min((int)root[at + 1], 11);
                label = count > 0 ? Encoding.Unicode.GetString(root, at + 2, count * 2) : null;
            }
            else if (type == 0x81 && (root[at + 1] & 1) == 0 && bitmapCluster < 0)
            {
                bitmapCluster = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(at + 20));
                bitmapBytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(at + 24));
            }
        }
        if (bitmapCluster < 2 || bitmapBytes < (clusters + 7) / 8) return null;

        // The bitmap follows its FAT chain; in practice it is contiguous, so the chain costs one small read per cluster.
        long used = 0, bit = 0;
        var buffer = new byte[clusterBytes];
        long cluster = bitmapCluster;
        for (long guard = 0; bit < clusters; guard++)
        {
            if (cluster < 0 || guard > clusters) return null;
            if (read(ClusterOffset(cluster), buffer) < buffer.Length) return null;
            used += CountBits(buffer, ref bit, clusters);
            if (bit < clusters) cluster = Next(cluster);
        }

        return new FileSystemUsage(FileSystemFormat.exFAT, (ulong)(clusters * clusterBytes),
            (ulong)((clusters - used) * clusterBytes), label);

    }

    // ------------------------------------------------------------------ Bits

    /// <summary>Counts the set bits of <paramref name="data"/>, stopping once <paramref name="bit"/> reaches <paramref name="limit"/>.</summary>
    internal static long CountBits(ReadOnlySpan<byte> data, ref long bit, long limit)
    {
        long count = 0;
        int i = 0;
        // Whole 64-bit words while all of their bits are below the limit.
        while (i + 8 <= data.Length && bit + 64 <= limit)
        {
            count += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
            i += 8;
            bit += 64;
        }
        for (; i < data.Length && bit < limit; i++)
        {
            int bits = (int)Math.Min(8, limit - bit);
            int mask = bits == 8 ? 0xFF : (1 << bits) - 1;
            count += BitOperations.PopCount((uint)(data[i] & mask));
            bit += bits;
        }
        return count;
    }
}
