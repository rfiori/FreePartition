using System.Buffers.Binary;
using System.Text;
using FreePartition.Core.Disks;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class LostPartitionTests
{
    private const long MiB = 1L << 20;

    // ---- hand-built boot sectors

    private static byte[] Ntfs(long totalSectors = 2_000_000, ulong serial = 0x1122334455667788, long mftCluster = 4)
    {
        var s = new byte[512];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(s, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0B), 512);
        s[0x0D] = 8;
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(0x28), totalSectors);
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(0x30), mftCluster);
        s[0x40] = 0xF6;   // 1 KiB records
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(0x48), serial);
        s[510] = 0x55; s[511] = 0xAA;
        return s;
    }

    private static byte[] Fat32(uint totalSectors = 1_000_000, uint serial = 0xCAFEBABE)
    {
        var s = new byte[512];
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(s, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0B), 512);
        s[0x0D] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0E), 32);
        s[0x10] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0x20), totalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0x24), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x32), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0x43), serial);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(s, 0x52);
        s[510] = 0x55; s[511] = 0xAA;
        return s;
    }

    private static byte[] ExFat(ulong sectors = 4_000_000, uint serial = 0x0BADF00D)
    {
        var s = new byte[512];
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(s, 3);
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(0x48), sectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0x64), serial);
        s[0x6C] = 9;    // 512-byte sectors
        s[0x6D] = 6;    // 32 KiB clusters
        s[0x6E] = 1;
        s[510] = 0x55; s[511] = 0xAA;
        return s;
    }

    // ---- the probe

    [Fact]
    public void Probe_Ntfs_ReportsSizeWithTheBackupSector()
    {
        Assert.True(BootSectorProbe.TryParse(Ntfs(2_000_000), out var info));

        Assert.Equal(FileSystemFormat.NTFS, info!.FileSystem);
        Assert.Equal(2_000_001L * 512, info.Length);            // one more sector than TotalSectors
        Assert.Equal(2_000_000L * 512, info.BackupOffset);      // that last sector holds the backup
        Assert.Equal("1122334455667788", info.Serial);
    }

    [Fact]
    public void Probe_Fat32_ReportsSize()
    {
        Assert.True(BootSectorProbe.TryParse(Fat32(1_000_000), out var info));

        Assert.Equal(FileSystemFormat.FAT32, info!.FileSystem);
        Assert.Equal(1_000_000L * 512, info.Length);
        Assert.Equal(6 * 512, info.BackupOffset);
        Assert.Equal("CAFEBABE", info.Serial);
    }

    [Fact]
    public void Probe_ExFat_ReportsSize()
    {
        Assert.True(BootSectorProbe.TryParse(ExFat(4_000_000), out var info));

        Assert.Equal(FileSystemFormat.exFAT, info!.FileSystem);
        Assert.Equal(4_000_000L * 512, info.Length);
        Assert.Equal(12 * 512, info.BackupOffset);
        Assert.Equal("0BADF00D", info.Serial);
    }

    [Fact]
    public void Probe_WithoutTheBootMark_IsRejected()
    {
        var sector = Ntfs();
        sector[511] = 0;
        Assert.False(BootSectorProbe.TryParse(sector, out _));
    }

    [Fact]
    public void Probe_ShortOrBlankSector_IsRejected()
    {
        Assert.False(BootSectorProbe.TryParse(new byte[100], out _));
        Assert.False(BootSectorProbe.TryParse(new byte[512], out _));
    }

    [Theory]
    [InlineData(0x0B, 0x00, 0x03)]   // 768 bytes per sector
    [InlineData(0x0D, 0x03, 0x00)]   // 3 sectors per cluster
    [InlineData(0x10, 0x02, 0x00)]   // FAT count set: not an NTFS field
    [InlineData(0x0E, 0x20, 0x00)]   // reserved sectors set
    public void Probe_Ntfs_WithOutOfRangeFields_IsRejected(int at, byte low, byte high)
    {
        var sector = Ntfs();
        sector[at] = low;
        if (at == 0x0B) sector[at + 1] = high;
        Assert.False(BootSectorProbe.TryParse(sector, out _));
    }

    [Fact]
    public void Probe_Ntfs_WithTheMftOutsideTheVolume_IsRejected()
    {
        var sector = Ntfs(totalSectors: 4096, mftCluster: 786_432);   // only 512 clusters
        Assert.False(BootSectorProbe.TryParse(sector, out _));
    }

    [Fact]
    public void Probe_Fat32_WithNoFatSize_IsRejected()
    {
        var sector = Fat32();
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x24), 0);
        Assert.False(BootSectorProbe.TryParse(sector, out _));
    }

    [Fact]
    public void Probe_ExFat_WithAnythingWhereTheBpbWas_IsRejected()
    {
        var sector = ExFat();
        sector[0x20] = 1;
        Assert.False(BootSectorProbe.TryParse(sector, out _));
    }

    [Fact]
    public void Probe_ExFat_WithABadSectorShift_IsRejected()
    {
        var sector = ExFat();
        sector[0x6C] = 13;
        Assert.False(BootSectorProbe.TryParse(sector, out _));
    }

    // ---- gaps

    [Fact]
    public void FindGaps_SkipsPartitionsAndTinyAlignmentGaps()
    {
        var gaps = LostPartitionScanner.FindGaps(100 * MiB, 512, gpt: false,
        [
            new DiskRange(MiB, 20 * MiB),
            new DiskRange(20 * MiB + 4096, 60 * MiB)
        ]);

        Assert.Equal([new DiskRange(60 * MiB, 100 * MiB)], gaps);
    }

    [Fact]
    public void FindGaps_OnAnEmptyGptDisk_LeavesOutTheTables()
    {
        var gaps = LostPartitionScanner.FindGaps(100 * MiB, 512, gpt: true, []);

        Assert.Equal([new DiskRange(34 * 512, 100 * MiB - 33 * 512)], gaps);
    }

    [Fact]
    public void FindGaps_UnsortedAndOverlappingRanges_AreMerged()
    {
        var gaps = LostPartitionScanner.FindGaps(100 * MiB, 512, gpt: false,
        [
            new DiskRange(50 * MiB, 70 * MiB),
            new DiskRange(1024, 55 * MiB),
        ]);

        Assert.Equal([new DiskRange(70 * MiB, 100 * MiB)], gaps);
    }

    // ---- scanning

    private static ReadAt Over(byte[] data) => (offset, destination) =>
    {
        long remaining = data.Length - offset;
        if (remaining <= 0) return 0;
        int n = (int)Math.Min(destination.Length, remaining);
        data.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    };

    /// <summary>A 64 MiB disk with an NTFS volume of <paramref name="sectors"/> sectors at <paramref name="at"/>, backup sector included.</summary>
    private static byte[] DiskWithNtfs(long at, long sectors, bool backup = true)
    {
        var disk = new byte[64 * MiB];
        Ntfs(sectors - 1).CopyTo(disk, at);
        if (backup)
            Ntfs(sectors - 1).CopyTo(disk, at + (sectors - 1) * 512);
        return disk;
    }

    private static IReadOnlyList<DiskRange> Whole(byte[] disk) => [new DiskRange(512, disk.Length)];

    [Fact]
    public void Quick_FindsAPartitionOnAMiBBoundary_AndConfirmsItByTheBackup()
    {
        var disk = DiskWithNtfs(10 * MiB, 40_960);   // 20 MiB

        var scan = LostPartitionScanner.Scan(Over(disk), 512, Whole(disk), LostPartitionScanMode.Quick);

        var p = Assert.Single(scan.Found);
        Assert.True(scan.Completed);
        Assert.Equal(10 * MiB, p.Offset);
        Assert.Equal(40_960L * 512, p.Length);
        Assert.Equal(FileSystemFormat.NTFS, p.FileSystem);
        Assert.True(p.BackupConfirmed);
    }

    [Fact]
    public void Quick_WithoutTheBackup_StillFindsItButWithLowConfidence()
    {
        var disk = DiskWithNtfs(10 * MiB, 40_960, backup: false);

        var p = Assert.Single(LostPartitionScanner.Scan(Over(disk), 512, Whole(disk), LostPartitionScanMode.Quick).Found);

        Assert.False(p.BackupConfirmed);
    }

    [Fact]
    public void Quick_DoesNotLookOffTheBoundaries_ButDeepDoes()
    {
        long at = 10 * MiB + 512 * 100;   // not a boundary anyone uses
        var disk = DiskWithNtfs(at, 40_960);

        Assert.Empty(LostPartitionScanner.Scan(Over(disk), 512, Whole(disk), LostPartitionScanMode.Quick).Found);

        var deep = Assert.Single(LostPartitionScanner.Scan(Over(disk), 512, Whole(disk), LostPartitionScanMode.Deep).Found);
        Assert.Equal(at, deep.Offset);
    }

    [Fact]
    public void Quick_FindsAnOldPartitionAtSector63()
    {
        var disk = new byte[8 * MiB];
        Ntfs(15_000).CopyTo(disk, 63 * 512);

        var p = Assert.Single(LostPartitionScanner.Scan(Over(disk), 512, [new DiskRange(512, disk.Length)], LostPartitionScanMode.Quick).Found);

        Assert.Equal(63 * 512, p.Offset);
    }

    [Fact]
    public void Scan_IgnoresAPartitionThatDoesNotFitInTheFreeSpace()
    {
        var disk = DiskWithNtfs(10 * MiB, 40_960);   // ends at 30 MiB
        var gaps = new[] { new DiskRange(512, 20 * MiB) };

        Assert.Empty(LostPartitionScanner.Scan(Over(disk), 512, gaps, LostPartitionScanMode.Quick).Found);
        Assert.Empty(LostPartitionScanner.Scan(Over(disk), 512, gaps, LostPartitionScanMode.Deep).Found);
    }

    [Fact]
    public void Scan_DropsAVolumeStoredInsideAnotherOne()
    {
        var disk = DiskWithNtfs(10 * MiB, 40_960);   // 10..30 MiB
        Ntfs(2_000).CopyTo(disk, 15 * MiB);          // an old image inside it

        var found = LostPartitionScanner.Scan(Over(disk), 512, Whole(disk), LostPartitionScanMode.Deep).Found;

        Assert.Equal(10 * MiB, Assert.Single(found).Offset);
    }

    [Fact]
    public void Deep_DoesNotReportTheBackupBootSectorAsAnotherPartition()
    {
        var disk = DiskWithNtfs(10 * MiB, 40_960);   // the backup copy sits at 30 MiB - 512 and parses like the first sector

        var found = LostPartitionScanner.Scan(Over(disk), 512, Whole(disk), LostPartitionScanMode.Deep).Found;

        Assert.Equal(10 * MiB, Assert.Single(found).Offset);
    }

    [Fact]
    public void Scan_FindsSeveralPartitions_InOrder()
    {
        var disk = new byte[64 * MiB];
        Fat32(20_480).CopyTo(disk, 30 * MiB);
        Ntfs(20_000).CopyTo(disk, 5 * MiB);

        var found = LostPartitionScanner.Scan(Over(disk), 512, Whole(disk), LostPartitionScanMode.Quick).Found;

        Assert.Equal([5 * MiB, 30 * MiB], found.Select(p => p.Offset).ToArray());
        Assert.Equal([FileSystemFormat.NTFS, FileSystemFormat.FAT32], found.Select(p => p.FileSystem).ToArray());
    }

    [Fact]
    public void Scan_OfRandomishData_FindsNothing()
    {
        var disk = new byte[8 * MiB];
        new Random(7).NextBytes(disk);

        Assert.Empty(LostPartitionScanner.Scan(Over(disk), 512, Whole(disk), LostPartitionScanMode.Deep).Found);
    }

    [Fact]
    public void Scan_Canceled_ReturnsWhatWasFoundSoFar()
    {
        var disk = new byte[64 * MiB];
        Ntfs(20_000).CopyTo(disk, 5 * MiB);
        using var cts = new CancellationTokenSource();
        var gaps = new[] { new DiskRange(512, 20 * MiB), new DiskRange(30 * MiB, 60 * MiB) };

        // Cancels as soon as the first gap is done.
        var progress = new SynchronousProgress(p => { if (p >= 20.0 * MiB / (50 * MiB) - 0.01) cts.Cancel(); });
        var scan = LostPartitionScanner.Scan(Over(disk), 512, gaps, LostPartitionScanMode.Quick, progress, cts.Token);

        Assert.False(scan.Completed);
        Assert.Equal(5 * MiB, Assert.Single(scan.Found).Offset);
    }

    private sealed class SynchronousProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }

    [Fact]
    public void QuickOffsets_AreAscendingWithoutRepeats_AndInsideTheGap()
    {
        var gap = new DiskRange(512, 40 * MiB);

        var offsets = LostPartitionScanner.QuickOffsets(gap, 512).ToList();

        Assert.Equal(offsets.Distinct().Count(), offsets.Count);
        Assert.All(offsets, o => Assert.InRange(o, gap.Start, gap.End - 1));
        Assert.Contains(63L * 512, offsets);
        Assert.Contains(MiB, offsets);
        Assert.Contains(39 * MiB, offsets);
    }

    // ---- adding an entry to the partition table

    /// <summary>A DRIVE_LAYOUT_INFORMATION_EX buffer the way Windows returns it for a 100 MiB disk.</summary>
    private static byte[] BuildLayout(bool gpt, params (long Offset, long Length, int Number)[] partitions)
    {
        int entries = gpt ? partitions.Length : 4;   // an MBR disk always reports its four slots
        var buffer = new byte[48 + 144 * entries];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, gpt ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)entries);
        if (gpt)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), 34 * 512);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), 100 * MiB - 67 * 512);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40), 128);
        }
        for (int i = 0; i < partitions.Length; i++)
        {
            int at = 48 + i * 144;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at + 8), partitions[i].Offset);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at + 16), partitions[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 24), (uint)partitions[i].Number);
        }
        return buffer;
    }

    [Fact]
    public void AddPartition_Gpt_AppendsOneEntryAndLeavesTheRestAlone()
    {
        byte[] original = BuildLayout(true, (MiB, 10 * MiB, 1));
        var layout = DriveLayout.Parse(original, 100 * MiB);

        byte[] updated = layout.WithAddedPartition(30 * MiB, 20 * MiB, 0x07, 512);

        Assert.Equal(original.Length + 144, updated.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(4)));
        Assert.Equal(original.Where((b, i) => i is < 4 or >= 8).ToArray(),
                     updated.Take(original.Length).Where((b, i) => i is < 4 or >= 8).ToArray());   // only the count changed
        var entry = updated.AsSpan(48 + 144);
        Assert.Equal(30 * MiB, BinaryPrimitives.ReadInt64LittleEndian(entry[8..]));
        Assert.Equal(20 * MiB, BinaryPrimitives.ReadInt64LittleEndian(entry[16..]));
        Assert.Equal(1, entry[28]);   // RewritePartition
        Assert.Equal(new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"), new Guid(entry.Slice(32, 16)));
        Assert.NotEqual(Guid.Empty, new Guid(entry.Slice(48, 16)));
    }

    [Fact]
    public void AddPartition_Mbr_UsesTheFirstEmptySlot_WithTheTypeAndHiddenSectors()
    {
        var layout = DriveLayout.Parse(BuildLayout(false, (MiB, 10 * MiB, 1)), 100 * MiB);

        byte[] updated = layout.WithAddedPartition(30 * MiB, 20 * MiB, 0x0C, 512);

        Assert.Equal(48 + 144 * 4, updated.Length);
        var entry = updated.AsSpan(48 + 144);   // slot 1: slot 0 is taken
        Assert.Equal(30 * MiB, BinaryPrimitives.ReadInt64LittleEndian(entry[8..]));
        Assert.Equal(0x0C, entry[32]);
        Assert.Equal(1, entry[28]);
        Assert.Equal((uint)(30 * MiB / 512), BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]));
    }

    [Fact]
    public void AddPartition_Mbr_WithFourPartitions_IsRefused()
    {
        var layout = DriveLayout.Parse(BuildLayout(false,
            (MiB, 5 * MiB, 1), (10 * MiB, 5 * MiB, 2), (20 * MiB, 5 * MiB, 3), (30 * MiB, 5 * MiB, 4)), 100 * MiB);

        Assert.Throws<InvalidOperationException>(() => layout.WithAddedPartition(50 * MiB, 10 * MiB, 0x07, 512));
    }

    [Theory]
    [InlineData(5 * MiB, 10 * MiB)]         // overlaps the existing partition (1..11 MiB)
    [InlineData(0, 2 * MiB)]                // inside the GPT tables
    [InlineData(95 * MiB, 10 * MiB)]        // past the usable end
    [InlineData(30 * MiB + 100, 10 * MiB)]  // not on a sector boundary
    public void AddPartition_OutOfPlace_IsRefused(long offset, long length)
    {
        var layout = DriveLayout.Parse(BuildLayout(true, (MiB, 10 * MiB, 1)), 100 * MiB);

        Assert.Throws<InvalidOperationException>(() => layout.WithAddedPartition(offset, length, 0x07, 512));
    }

    [Fact]
    public void AddPartition_AtSector63_IsAccepted()
    {
        var layout = DriveLayout.Parse(BuildLayout(false), 100 * MiB);

        Assert.NotEmpty(layout.WithAddedPartition(63 * 512, 20 * MiB, 0x07, 512));
    }

    // ---- the queued operation

    private sealed class FakeSource(byte[] sector) : IBootSectorSource
    {
        public byte[] ReadSector(int diskNumber, long offset) => sector;
    }

    private sealed class FakeAdder : IPartitionAdder
    {
        public List<PartitionAddRequest> Requests { get; } = [];
        public List<string> Order { get; init; } = [];

        public void Add(PartitionAddRequest request)
        {
            Requests.Add(request);
            Order.Add("table");
        }
    }

    private sealed class RecordingExecutor(List<string>? order = null) : ICommandExecutor
    {
        public List<string> Scripts { get; } = [];

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct = default)
        {
            Scripts.Add(script);
            order?.Add("script");
            return Task.FromResult(new CommandResult(0, "", ""));
        }
    }

    private static RestorePartition Restore() => new(1, 10 * MiB, 2_000_001L * 512, FileSystemFormat.NTFS);

    [Fact]
    public void Restore_Script_OnlyMountsTheVolume_AndNeverCreatesOrFormatsAPartition()
    {
        string script = Restore().GenerateScript();

        Assert.Contains("Update-Disk -Number 1", script);
        Assert.Contains("Add-PartitionAccessPath", script);
        Assert.Contains($"$o = {10 * MiB}", script);
        Assert.DoesNotContain("New-Partition -", script);   // New-Partition zeroes the new partition
        Assert.DoesNotContain("Format-Volume", script);
        Assert.False(Restore().Destructive);
    }

    [Theory]
    [InlineData(-1, 10 * MiB, 20 * MiB)]
    [InlineData(1, 10 * MiB, 100)]                // too small
    [InlineData(1, 10 * MiB, 20 * MiB + 1)]       // not whole sectors
    [InlineData(1, 10 * MiB + 1, 20 * MiB)]       // not on a sector boundary
    public void Restore_WithInvalidValues_IsRefused(int disk, long offset, long size)
    {
        Assert.Throws<ArgumentException>(() => new RestorePartition(disk, offset, size, FileSystemFormat.NTFS).Validate());
    }

    [Fact]
    public void Restore_AtSector63_IsAllowed()
    {
        new RestorePartition(1, 63 * 512, 20 * MiB, FileSystemFormat.NTFS).Validate();
    }

    [Fact]
    public void Restore_WithAnUnknownFileSystem_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new RestorePartition(1, 10 * MiB, 20 * MiB, (FileSystemFormat)99).Validate());
    }

    [Fact]
    public async Task Restore_WritesTheEntryFirst_ThenRunsTheScript()
    {
        var order = new List<string>();
        var adder = new FakeAdder { Order = order };
        var executor = new RecordingExecutor(order);
        var op = Restore() with { Source = new FakeSource(Ntfs(2_000_000)), Adder = adder };

        var result = await op.RunAsync(executor, op.GenerateScript(), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["table", "script"], order);
        var request = Assert.Single(adder.Requests);
        Assert.Equal((1, 10 * MiB, 2_000_001L * 512, FileSystemFormat.NTFS),
                     (request.DiskNumber, request.Offset, request.Length, request.FileSystem));
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("other file system")]
    [InlineData("other size")]
    public async Task Restore_ChangesNothing_WhenTheBootSectorIsGoneOrDifferent(string case_)
    {
        var sector = case_ switch
        {
            "blank" => new byte[512],
            "other file system" => Fat32(2_000_001),
            _ => Ntfs(1_000_000)
        };
        var adder = new FakeAdder();
        var executor = new RecordingExecutor();
        var op = Restore() with { Source = new FakeSource(sector), Adder = adder };

        var result = await op.RunAsync(executor, op.GenerateScript(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Nothing was changed", result.Error);
        Assert.Empty(adder.Requests);
        Assert.Empty(executor.Scripts);
    }

    [Fact]
    public void Queue_AcceptsARestore_WithoutTouchingTheSystemDriveRule()
    {
        var queue = new OperationQueue('C');

        queue.Add(Restore());

        Assert.Single(queue.Pending);
    }
}
