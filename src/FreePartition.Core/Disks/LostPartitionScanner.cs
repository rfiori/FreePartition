using System.Buffers.Binary;
using System.Text;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Operations;

namespace FreePartition.Core.Disks;

/// <summary>A byte range of a disk, <see cref="Start"/> included and <see cref="End"/> excluded.</summary>
public readonly record struct DiskRange(long Start, long End)
{
    public long Length => End - Start;

    public bool Overlaps(DiskRange other) => Start < other.End && other.Start < End;
}

/// <summary>What a boot sector says about the partition it belongs to.</summary>
/// <param name="Length">Size of the whole partition in bytes, as it has to be in the partition table.</param>
/// <param name="BackupOffset">Where the backup copy of the boot sector sits, relative to the partition start.</param>
public sealed record BootSectorInfo(FileSystemFormat FileSystem, long Length, int BytesPerSector, string Serial, long BackupOffset);

/// <summary>A partition found by its boot sector in space no partition covers.</summary>
/// <param name="BackupConfirmed">The backup boot sector at the end of the volume says the same: it is very unlikely to be a coincidence.</param>
public sealed record LostPartition(
    long Offset, long Length, FileSystemFormat FileSystem, string Serial, bool BackupConfirmed)
{
    public long End => Offset + Length;
    public DiskRange Range => new(Offset, End);
}

/// <param name="Found">Partitions found; when <paramref name="Completed"/> is false, only those found before the scan was stopped.</param>
/// <param name="Completed">False when the scan was canceled before it covered every unallocated range.</param>
public sealed record LostPartitionScan(IReadOnlyList<LostPartition> Found, bool Completed);

public enum LostPartitionScanMode
{
    /// <summary>Looks only where partitions usually start (every 1 MiB, the old cylinder and track boundaries): fast, finds nearly everything made since Vista.</summary>
    Quick,

    /// <summary>Reads every sector of the unallocated space: slow, finds partitions that start anywhere.</summary>
    Deep
}

/// <summary>
/// Recognizes the boot sector of an NTFS, FAT32 or exFAT volume and says how big the volume is. The checks are strict
/// (signature, boot mark and the fields the file system requires to be in range) because a deep scan looks at every
/// sector of a disk, and a boot sector from a file or an old image must not be mistaken for a partition.
/// </summary>
public static class BootSectorProbe
{
    private const int BootSectorBytes = 512;

    public static bool TryParse(ReadOnlySpan<byte> sector, out BootSectorInfo? info)
    {
        info = null;
        if (sector.Length < BootSectorBytes || sector[510] != 0x55 || sector[511] != 0xAA)
            return false;

        info = Matches(sector, 3, "NTFS    ") ? ParseNtfs(sector)
             : Matches(sector, 3, "EXFAT   ") ? ParseExFat(sector)
             : Matches(sector, 0x52, "FAT32   ") ? ParseFat32(sector)
             : null;
        return info is not null;
    }

    private static bool Matches(ReadOnlySpan<byte> sector, int at, string text) =>
        sector.Slice(at, text.Length).SequenceEqual(Encoding.ASCII.GetBytes(text));

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static bool IsSectorSize(int value) => value is >= 512 and <= 4096 && IsPowerOfTwo(value);

    private static BootSectorInfo? ParseNtfs(ReadOnlySpan<byte> sector)
    {
        // The fields NTFS keeps at zero (reserved sectors, FAT leftovers, root entries, 16-bit totals).
        if (BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0E..]) != 0 || sector[0x10] != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(sector[0x11..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(sector[0x13..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(sector[0x16..]) != 0)
            return null;

        NtfsBootSector boot;
        try { boot = NtfsBootSector.Read(sector); }
        catch (InvalidDataException) { return null; }

        if (!IsSectorSize(boot.BytesPerSector) || !IsPowerOfTwo(boot.SectorsPerCluster) || boot.SectorsPerCluster > 128
            || boot.TotalSectors < 2048 || boot.TotalSectors > long.MaxValue / 2 / boot.BytesPerSector
            || boot.MftStartCluster >= boot.TotalClusters)
            return null;

        // The volume has one more sector than TotalSectors: the backup boot sector at its very end.
        long length = (boot.TotalSectors + 1) * boot.BytesPerSector;
        string serial = BinaryPrimitives.ReadUInt64LittleEndian(sector[0x48..]).ToString("X16");
        return new BootSectorInfo(FileSystemFormat.NTFS, length, boot.BytesPerSector, serial, boot.TotalSectors * boot.BytesPerSector);
    }

    private static BootSectorInfo? ParseFat32(ReadOnlySpan<byte> sector)
    {
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0B..]);
        int sectorsPerCluster = sector[0x0D];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0E..]);
        int fats = sector[0x10];
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(sector[0x20..]);
        uint fatSize = BinaryPrimitives.ReadUInt32LittleEndian(sector[0x24..]);
        int backupSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x32..]);

        if (!IsSectorSize(bytesPerSector) || !IsPowerOfTwo(sectorsPerCluster) || sectorsPerCluster > 128
            || reserved < 1 || fats is < 1 or > 2
            || BinaryPrimitives.ReadUInt16LittleEndian(sector[0x11..]) != 0      // root entries: 0 on FAT32
            || BinaryPrimitives.ReadUInt16LittleEndian(sector[0x13..]) != 0      // 16-bit total: 0 on FAT32
            || total < 2048 || fatSize == 0
            || reserved + (long)fats * fatSize >= total)
            return null;

        string serial = BinaryPrimitives.ReadUInt32LittleEndian(sector[0x43..]).ToString("X8");
        long backup = backupSector >= 1 && backupSector < reserved ? (long)backupSector * bytesPerSector : 0;
        return new BootSectorInfo(FileSystemFormat.FAT32, (long)total * bytesPerSector, bytesPerSector, serial, backup);
    }

    private static BootSectorInfo? ParseExFat(ReadOnlySpan<byte> sector)
    {
        // "MustBeZero": the 53 bytes where a FAT boot sector keeps its BPB.
        if (sector[0x0B..0x40].IndexOfAnyExcept((byte)0) >= 0)
            return null;

        ulong volumeLength = BinaryPrimitives.ReadUInt64LittleEndian(sector[0x48..]);
        int sectorShift = sector[0x6C];
        int clusterShift = sector[0x6D];
        int fats = sector[0x6E];

        if (sectorShift is < 9 or > 12 || clusterShift > 25 - sectorShift || fats is < 1 or > 2
            || volumeLength < 2048 || volumeLength > (ulong)long.MaxValue >> (sectorShift + 1))
            return null;

        string serial = BinaryPrimitives.ReadUInt32LittleEndian(sector[0x64..]).ToString("X8");
        return new BootSectorInfo(FileSystemFormat.exFAT, (long)volumeLength << sectorShift, 1 << sectorShift, serial, 12L << sectorShift);
    }
}

/// <summary>
/// Finds partitions whose entry is gone from the partition table (deleted, or a disk that lost or never kept its table)
/// by looking for their boot sectors in the space no partition covers. Read-only. Nothing is restored here: that is
/// the job of <see cref="RestorePartition"/>, which waits in the queue for Apply like every other operation.
/// </summary>
public static class LostPartitionScanner
{
    private const long MiB = 1L << 20;
    private const int BufferBytes = 4 * 1024 * 1024;
    private const int BootSectorBytes = 512;

    /// <summary>The first and the last sectors of a GPT disk hold the table; no partition lives there.</summary>
    private const int GptHeadSectors = 34, GptTailSectors = 33;

    /// <summary>Old Windows put partitions on cylinder boundaries of 255 heads × 63 sectors, the first one after one track.</summary>
    private const int LegacyTrackSectors = 63, LegacyCylinderSectors = 255 * 63;

    /// <summary>The unallocated stretches of a disk, once the ranges that partitions occupy (and the GPT tables) are left out. Ranges under 1 MiB are alignment, not room for a partition.</summary>
    public static IReadOnlyList<DiskRange> FindGaps(long diskLength, int sectorSize, bool gpt, IEnumerable<DiskRange> occupied)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        if (diskLength <= 0 || sectorSize <= 0) return [];

        long usableStart = gpt ? GptHeadSectors * (long)sectorSize : sectorSize;
        long usableEnd = gpt ? diskLength - GptTailSectors * (long)sectorSize : diskLength;

        var gaps = new List<DiskRange>();
        long cursor = usableStart;
        foreach (var range in occupied.Where(r => r.Length > 0).OrderBy(r => r.Start))
        {
            if (range.Start > cursor)
                AddGap(gaps, cursor, Math.Min(range.Start, usableEnd));
            cursor = Math.Max(cursor, range.End);
        }
        if (cursor < usableEnd)
            AddGap(gaps, cursor, usableEnd);
        return gaps;
    }

    private static void AddGap(List<DiskRange> gaps, long start, long end)
    {
        if (end - start >= MiB)
            gaps.Add(new DiskRange(start, end));
    }

    /// <param name="read">Reads from an offset of the whole disk; returns how many bytes were filled.</param>
    /// <param name="gaps">Where to look (see <see cref="FindGaps"/>); a partition found must fit entirely inside one.</param>
    /// <param name="progress">Receives the fraction scanned, from 0 to 1.</param>
    /// <remarks>Canceling does not throw: the partitions found so far come back with <see cref="LostPartitionScan.Completed"/> false.</remarks>
    public static LostPartitionScan Scan(ReadAt read, int sectorSize, IReadOnlyList<DiskRange> gaps, LostPartitionScanMode mode,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(gaps);
        if (sectorSize < BootSectorBytes || (sectorSize & (sectorSize - 1)) != 0 || BufferBytes % sectorSize != 0)
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Unsupported sector size.");

        var found = new Dictionary<long, LostPartition>();
        long total = Math.Max(1, gaps.Sum(g => g.Length));
        long done = 0;
        bool completed = true;

        foreach (var gap in gaps)
        {
            try
            {
                if (mode == LostPartitionScanMode.Quick)
                    ScanQuick(read, sectorSize, gap, gaps, found, done, total, progress, cancellationToken);
                else
                    ScanDeep(read, sectorSize, gap, gaps, found, done, total, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                completed = false;
                break;
            }
            done += gap.Length;
            progress?.Report((double)done / total);
        }

        return new LostPartitionScan(DropNested(found.Values), completed);
    }

    private static void ScanQuick(ReadAt read, int sectorSize, DiskRange gap, IReadOnlyList<DiskRange> gaps,
        Dictionary<long, LostPartition> found, long done, long total, IProgress<double>? progress, CancellationToken ct)
    {
        var sector = new byte[BootSectorBytes];
        long probed = 0;

        foreach (long offset in QuickOffsets(gap, sectorSize))
        {
            ct.ThrowIfCancellationRequested();
            if (++probed % 512 == 0)
                progress?.Report((double)(done + Math.Min(gap.Length, offset - gap.Start)) / total);
            Probe(read, sectorSize, offset, sector, gap, gaps, found);
        }
    }

    /// <summary>Where a partition most likely starts inside <paramref name="gap"/>, in ascending order and without repeats.</summary>
    public static IEnumerable<long> QuickOffsets(DiskRange gap, int sectorSize)
    {
        // Start of the gap (a partition right after a neighbor), then the track and cylinder boundaries of old layouts
        // (only the early ones are worth it: each is a read), then every MiB.
        var seen = new HashSet<long>();
        long first = RoundUp(gap.Start, sectorSize);
        var early = new List<long> { first };
        for (long k = 0; k < 16; k++)
        {
            early.Add(k * LegacyCylinderSectors * sectorSize);
            early.Add((k * LegacyCylinderSectors + LegacyTrackSectors) * sectorSize);
        }

        foreach (long offset in early.Where(o => o >= gap.Start && o < gap.End).OrderBy(o => o))
        {
            if (seen.Add(offset)) yield return offset;
        }
        for (long offset = RoundUp(gap.Start, MiB); offset < gap.End; offset += MiB)
        {
            if (seen.Add(offset)) yield return offset;
        }
    }

    private static void ScanDeep(ReadAt read, int sectorSize, DiskRange gap, IReadOnlyList<DiskRange> gaps,
        Dictionary<long, LostPartition> found, long done, long total, IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = new byte[BufferBytes];
        var sector = new byte[BootSectorBytes];
        long position = RoundUp(gap.Start, sectorSize);

        while (position < gap.End)
        {
            ct.ThrowIfCancellationRequested();

            int want = (int)Math.Min(BufferBytes, gap.End - position);
            int got = read(position, buffer.AsSpan(0, want));
            if (got <= 0) break;

            for (int at = 0; at + sectorSize <= got; at += sectorSize)
            {
                // Cheap test first: only a boot sector ends in 55 AA, which is 1 sector in 65,536 of random data.
                if (buffer[at + 510] != 0x55 || buffer[at + 511] != 0xAA) continue;
                Probe(read, sectorSize, position + at, sector, gap, gaps, found, buffer.AsSpan(at, BootSectorBytes));
            }

            position += got;
            progress?.Report((double)(done + Math.Min(gap.Length, position - gap.Start)) / total);
            if (got < want) break;   // a short read means the end of the disk
        }
    }

    private static void Probe(ReadAt read, int sectorSize, long offset, byte[] scratch, DiskRange gap, IReadOnlyList<DiskRange> gaps,
        Dictionary<long, LostPartition> found, ReadOnlySpan<byte> already = default)
    {
        ReadOnlySpan<byte> sector = already;
        if (sector.IsEmpty)
        {
            if (read(offset, scratch) < BootSectorBytes) return;
            sector = scratch;
        }

        if (!BootSectorProbe.TryParse(sector, out var info) || info is null) return;

        // The partition table needs whole sectors, and the partition has to fit in the free space it was found in.
        long length = info.Length;
        if (length % sectorSize != 0 || offset + length > gap.End) return;

        found[offset] = new LostPartition(offset, length, info.FileSystem, info.Serial,
            BackupMatches(read, offset, info, scratch));
    }

    /// <summary>Reads the backup boot sector at the end of the volume (or where the file system keeps it) and checks that it is the same volume.</summary>
    private static bool BackupMatches(ReadAt read, long offset, BootSectorInfo info, byte[] scratch)
    {
        if (info.BackupOffset <= 0 || read(offset + info.BackupOffset, scratch) < BootSectorBytes)
            return false;
        return BootSectorProbe.TryParse(scratch, out var backup) && backup is not null
               && backup.FileSystem == info.FileSystem && backup.Length == info.Length && backup.Serial == info.Serial;
    }

    /// <summary>
    /// A deep scan also finds boot sectors that are not partitions: the backup copy at the end of every volume (it
    /// parses exactly like the first one) and those of disk images, virtual disks and old copies stored inside a real
    /// partition. All of them start inside a candidate that comes first and is at least as big, so they are dropped and
    /// the outer partition stays.
    /// </summary>
    private static IReadOnlyList<LostPartition> DropNested(IEnumerable<LostPartition> all)
    {
        var list = all.OrderBy(p => p.Offset).ToList();
        return list
            .Where(p => !list.Any(o => o.Offset < p.Offset && p.Offset < o.End && o.Length >= p.Length))
            .ToList();
    }

    private static long RoundUp(long value, long multiple) => (value + multiple - 1) / multiple * multiple;
}
