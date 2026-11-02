using System.Buffers.Binary;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class ConvertPartitionStyleTests
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;
    private const int Sector = 512;

    private static readonly Guid BasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private static readonly Guid Msr = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    private static readonly Guid Esp = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    private static readonly Guid LinuxData = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    // ---------- hand-built DRIVE_LAYOUT_INFORMATION_EX buffers ----------

    private const int Header = 48, Entry = 144;

    /// <summary>An MBR layout: four slots, then any logical partitions. Number 0 marks the extended container.</summary>
    private static byte[] Mbr(params (long Offset, long Length, byte Type, int Number)[] entries)
    {
        int slots = Math.Max(4, entries.Length);
        var buffer = new byte[Header + slots * Entry];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)slots);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 0xDEADBEEF);   // signature
        for (int i = 0; i < entries.Length; i++)
        {
            var e = buffer.AsSpan(Header + i * Entry, Entry);
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], entries[i].Offset);
            BinaryPrimitives.WriteInt64LittleEndian(e[16..], entries[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[24..], (uint)entries[i].Number);
            e[32] = entries[i].Type;
            e[33] = i == 0 ? (byte)0x80 : (byte)0;   // the first one is active
        }
        return buffer;
    }

    private static byte[] Gpt(long diskLength, int sectorSize, params (long Offset, long Length, Guid Type)[] entries)
    {
        long entrySectors = (128L * 128 + sectorSize - 1) / sectorSize;
        long first = (2 + entrySectors) * sectorSize;
        long end = (diskLength / sectorSize - 1 - entrySectors) * sectorSize;

        var buffer = new byte[Header + entries.Length * Entry];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)entries.Length);
        Guid.NewGuid().TryWriteBytes(buffer.AsSpan(8));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), first);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), end - first);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40), 128);
        for (int i = 0; i < entries.Length; i++)
        {
            var e = buffer.AsSpan(Header + i * Entry, Entry);
            BinaryPrimitives.WriteInt32LittleEndian(e, 1);
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], entries[i].Offset);
            BinaryPrimitives.WriteInt64LittleEndian(e[16..], entries[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[24..], (uint)(i + 1));
            entries[i].Type.TryWriteBytes(e[32..]);
        }
        return buffer;
    }

    private static DriveLayout ParseMbr(byte[] buffer, long length = GiB) => DriveLayout.Parse(buffer, length, allowExtended: true);

    private static Guid GptTypeOf(byte[] layout, int index) => new(layout.AsSpan(Header + index * Entry + 32, 16));

    // ---------- MBR -> GPT ----------

    [Fact]
    public void ToGpt_KeepsEveryPartitionWhereItIs()
    {
        var mbr = ParseMbr(Mbr((MiB, 100 * MiB, 0x07, 1), (101 * MiB, 200 * MiB, 0x0C, 2)));

        byte[] converted = mbr.ToGpt(Sector, GiB);
        var gpt = DriveLayout.Parse(converted, GiB);

        Assert.True(gpt.IsGpt);
        Assert.Equal(new[] { (MiB, 100 * MiB), (101 * MiB, 200 * MiB) }, gpt.Partitions.Select(p => (p.Offset, p.Length)));
        Assert.All(Enumerable.Range(0, 2), i => Assert.Equal(BasicData, GptTypeOf(converted, i)));
    }

    [Fact]
    public void ToGpt_UsableAreaLeavesRoomForBothTables()
    {
        var gpt = DriveLayout.Parse(ParseMbr(Mbr((MiB, 100 * MiB, 0x07, 1))).ToGpt(Sector, GiB), GiB);

        Assert.Equal(34 * Sector, gpt.UsableStart);                       // MBR, header and 32 sectors of entries
        Assert.Equal(GiB - 33 * Sector, gpt.UsableEnd);                    // 32 sectors of entries and the header
    }

    [Fact]
    public void ToGpt_On4KDisks_UsesFourSectorsOfEntries()
    {
        var gpt = DriveLayout.Parse(ParseMbr(Mbr((MiB, 100 * MiB, 0x07, 1))).ToGpt(4096, GiB), GiB);

        Assert.Equal(6 * 4096, gpt.UsableStart);
        Assert.Equal(GiB - 5 * 4096, gpt.UsableEnd);
    }

    [Fact]
    public void ToGpt_MapsKnownTypes()
    {
        byte[] converted = ParseMbr(Mbr((MiB, 10 * MiB, 0xEF, 1), (11 * MiB, 10 * MiB, 0x83, 2))).ToGpt(Sector, GiB);

        Assert.Equal(Esp, GptTypeOf(converted, 0));
        Assert.Equal(LinuxData, GptTypeOf(converted, 1));
    }

    [Fact]
    public void ToGpt_LogicalPartitionsBecomeOrdinaryOnes_AndTheContainerIsDropped()
    {
        var mbr = ParseMbr(Mbr(
            (MiB, 100 * MiB, 0x07, 1),
            (101 * MiB, 300 * MiB, 0x0F, 0),          // extended container
            (102 * MiB, 100 * MiB, 0x07, 5),          // logical
            (203 * MiB, 150 * MiB, 0x07, 6)));        // logical

        var gpt = DriveLayout.Parse(mbr.ToGpt(Sector, GiB), GiB);

        Assert.Equal(new[] { (MiB, 100 * MiB), (102 * MiB, 100 * MiB), (203 * MiB, 150 * MiB) },
            gpt.Partitions.Select(p => (p.Offset, p.Length)));
    }

    [Fact]
    public void ToGpt_RefusesAPartitionInTheBackupTableArea()
    {
        var mbr = ParseMbr(Mbr((MiB, GiB - MiB - 8192, 0x07, 1)));

        var ex = Assert.Throws<InvalidOperationException>(() => mbr.ToGpt(Sector, GiB));
        Assert.Contains("Shrink the last partition", ex.Message);
    }

    [Fact]
    public void ToGpt_RefusesAPartitionInTheFirstTableArea()
    {
        var mbr = ParseMbr(Mbr((10 * Sector, 100 * MiB, 0x07, 1)));

        var ex = Assert.Throws<InvalidOperationException>(() => mbr.ToGpt(Sector, GiB));
        Assert.Contains("Move it to the right", ex.Message);
    }

    [Fact]
    public void ToGpt_AcceptsTheClassicSector63Start() =>
        ParseMbr(Mbr((63 * Sector, 100 * MiB, 0x07, 1))).ToGpt(Sector, GiB);

    [Theory]
    [InlineData(0x42)]   // dynamic disk
    [InlineData(0xAB)]   // unknown
    public void ToGpt_RefusesTypesItCannotRepresent(byte type)
    {
        var mbr = ParseMbr(Mbr((MiB, 100 * MiB, type, 1)));

        Assert.Throws<InvalidOperationException>(() => mbr.ToGpt(Sector, GiB));
    }

    [Fact]
    public void ToGpt_RefusesAGptDisk() =>
        Assert.Throws<InvalidOperationException>(
            () => DriveLayout.Parse(Gpt(GiB, Sector, (MiB, 100 * MiB, BasicData)), GiB).ToGpt(Sector, GiB));

    [Fact]
    public void Parse_StillRefusesExtendedPartitionsUnlessAsked()
    {
        byte[] buffer = Mbr((MiB, 100 * MiB, 0x07, 1), (101 * MiB, 300 * MiB, 0x0F, 0), (102 * MiB, 100 * MiB, 0x07, 5));

        Assert.Throws<InvalidDataException>(() => DriveLayout.Parse(buffer, GiB));
    }

    // ---------- GPT -> MBR ----------

    [Fact]
    public void ToMbr_KeepsEveryPartitionWhereItIs_AndDropsTheReservedOne()
    {
        var gpt = DriveLayout.Parse(Gpt(GiB, Sector,
            (MiB, 16 * MiB, Msr), (17 * MiB, 100 * MiB, BasicData), (117 * MiB, 200 * MiB, BasicData)), GiB);

        var mbr = DriveLayout.Parse(gpt.ToMbr(Sector, GiB), GiB);

        Assert.False(mbr.IsGpt);
        Assert.Equal(new[] { (17 * MiB, 100 * MiB), (117 * MiB, 200 * MiB) }, mbr.Partitions.Select(p => (p.Offset, p.Length)));
    }

    [Fact]
    public void ToMbr_WritesTypeBytesAndASignature()
    {
        var gpt = DriveLayout.Parse(Gpt(GiB, Sector,
            (MiB, 100 * MiB, Esp), (101 * MiB, 100 * MiB, BasicData), (201 * MiB, 100 * MiB, BasicData)), GiB);

        byte[] mbr = gpt.ToMbr(Sector, GiB, p => p.Offset == 201 * MiB);

        Assert.Equal(0xEF, mbr[Header + 0 * Entry + 32]);
        Assert.Equal(0x07, mbr[Header + 1 * Entry + 32]);
        Assert.Equal(0x0C, mbr[Header + 2 * Entry + 32]);   // reported as FAT32
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(8)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(4)));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(mbr.AsSpan(Header + 3 * Entry + 16)));   // the fourth slot is empty
        Assert.Equal((uint)(101 * MiB / Sector), BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(Header + 1 * Entry + 36)));
    }

    [Fact]
    public void ToMbr_RefusesMoreThanFourPartitions()
    {
        var gpt = DriveLayout.Parse(Gpt(GiB, Sector,
            (1 * MiB, 10 * MiB, BasicData), (11 * MiB, 10 * MiB, BasicData), (21 * MiB, 10 * MiB, BasicData),
            (31 * MiB, 10 * MiB, BasicData), (41 * MiB, 10 * MiB, BasicData)), GiB);

        var ex = Assert.Throws<InvalidOperationException>(() => gpt.ToMbr(Sector, GiB));
        Assert.Contains("at most 4", ex.Message);
    }

    [Fact]
    public void ToMbr_TheReservedPartitionDoesNotCountTowardsTheFour()
    {
        var gpt = DriveLayout.Parse(Gpt(GiB, Sector,
            (1 * MiB, 10 * MiB, Msr), (11 * MiB, 10 * MiB, BasicData), (21 * MiB, 10 * MiB, BasicData),
            (31 * MiB, 10 * MiB, BasicData), (41 * MiB, 10 * MiB, BasicData)), GiB);

        gpt.ToMbr(Sector, GiB);
    }

    [Fact]
    public void ToMbr_RefusesAPartitionBeyondWhatMbrCanAddress()
    {
        long disk = 3L << 40;   // 3 TiB
        var gpt = DriveLayout.Parse(Gpt(disk, Sector, (MiB, 2_500L << 30, BasicData)), disk);

        var ex = Assert.Throws<InvalidOperationException>(() => gpt.ToMbr(Sector, disk));
        Assert.Contains("the most an MBR disk", ex.Message);
    }

    [Fact]
    public void ToMbr_RefusesATypeItCannotRepresent()
    {
        var gpt = DriveLayout.Parse(Gpt(GiB, Sector, (MiB, 100 * MiB, Guid.Parse("48465300-0000-11AA-AA11-00306543ECAC"))), GiB);   // Apple HFS+

        Assert.Throws<InvalidOperationException>(() => gpt.ToMbr(Sector, GiB));
    }

    [Fact]
    public void ToMbr_RefusesAnMbrDisk() =>
        Assert.Throws<InvalidOperationException>(() => ParseMbr(Mbr((MiB, 100 * MiB, 0x07, 1))).ToMbr(Sector, GiB));

    [Fact]
    public void ToMbr_RefusesAPartitionInsideTheTableArea()
    {
        var gpt = DriveLayout.Parse(Gpt(GiB, Sector, (20 * Sector, 100 * MiB, BasicData)), GiB);

        Assert.Throws<InvalidOperationException>(() => gpt.ToMbr(Sector, GiB));
    }

    [Fact]
    public void StaleGptRanges_AreTheHeadersAndEntryTables()
    {
        var gpt = DriveLayout.Parse(Gpt(GiB, Sector, (MiB, 100 * MiB, BasicData)), GiB);

        var ranges = gpt.StaleGptRanges(Sector, GiB);

        Assert.Equal(new[] { (512L, 33L * 512), (GiB - 33 * 512, 33L * 512) }, ranges);
    }

    [Fact]
    public void StaleGptRanges_NeverIncludeAPartition()
    {
        var gpt = DriveLayout.Parse(Gpt(GiB, Sector, (MiB, GiB - MiB - 4096, BasicData)), GiB);

        var ranges = gpt.StaleGptRanges(Sector, GiB);

        Assert.Equal(new[] { (512L, 33L * 512) }, ranges);   // the end region overlaps the partition, so it is left alone
    }

    [Fact]
    public void StaleGptRanges_AnMbrDiskHasNone() =>
        Assert.Empty(ParseMbr(Mbr((MiB, 100 * MiB, 0x07, 1))).StaleGptRanges(Sector, GiB));

    [Fact]
    public void CreateDiskBuffer_CarriesTheIdentityOfTheNewTable()
    {
        byte[] gptBuffer = ParseMbr(Mbr((MiB, 100 * MiB, 0x07, 1))).ToGpt(Sector, GiB);
        var gpt = DriveLayout.Parse(gptBuffer, GiB);
        byte[] create = gpt.CreateDiskBuffer();

        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(create));
        Assert.Equal(gptBuffer[8..24], create[4..20]);                       // the same disk GUID
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(create.AsSpan(20)));

        byte[] mbrBuffer = DriveLayout.Parse(Gpt(GiB, Sector, (MiB, 100 * MiB, BasicData)), GiB).ToMbr(Sector, GiB);
        byte[] createMbr = DriveLayout.Parse(mbrBuffer, GiB).CreateDiskBuffer();

        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(createMbr));
        Assert.Equal(mbrBuffer[8..12], createMbr[4..8]);                     // the same signature
    }

    // ---------- the operation ----------

    private sealed class FakeConverter(Action<LayoutConversionRequest>? onConvert = null) : ILayoutConverter
    {
        public List<LayoutConversionRequest> Requests { get; } = new();

        public void Convert(LayoutConversionRequest request, IProgress<string>? progress)
        {
            Requests.Add(request);
            onConvert?.Invoke(request);
        }
    }

    private sealed class RecordingExecutor(List<string>? order = null) : ICommandExecutor
    {
        public List<string> Scripts { get; } = new();

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            order?.Add("script");
            Scripts.Add(script);
            return Task.FromResult(new CommandResult(0, "", ""));
        }
    }

    private static ConvertPartitionStyle Op(ILayoutConverter? converter = null, DiskPartitionStyle target = DiskPartitionStyle.GPT,
        DriveMount[]? mounts = null) => new(2, target, mounts, converter);

    [Fact]
    public void Validate_AcceptsBothStyles()
    {
        Op(target: DiskPartitionStyle.GPT).Validate();
        Op(target: DiskPartitionStyle.MBR, mounts: [new DriveMount('E', MiB)]).Validate();
    }

    [Fact]
    public void Validate_RefusesBadValues()
    {
        Assert.Throws<ArgumentException>(() => new ConvertPartitionStyle(-1, DiskPartitionStyle.GPT).Validate());
        Assert.Throws<ArgumentException>(() => new ConvertPartitionStyle(1, (DiskPartitionStyle)9).Validate());
        Assert.Throws<ArgumentException>(() => Op(mounts: [new DriveMount('1', MiB)]).Validate());
        Assert.Throws<ArgumentException>(() => Op(mounts: [new DriveMount('E', -1)]).Validate());
    }

    [Fact]
    public void IsRefusedWhenItsDrivesIncludeTheWindowsDrive()
    {
        var queue = new OperationQueue(protectedDrive: 'C');

        Assert.Throws<InvalidOperationException>(() => queue.Add(Op(mounts: [new DriveMount('c', MiB)])));
        queue.Add(Op(mounts: [new DriveMount('E', MiB)]));
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void AdvertisesTheRiskAndWhatItWillNotDo()
    {
        var op = Op(target: DiskPartitionStyle.GPT);

        Assert.True(op.Destructive);
        Assert.Contains("UEFI", op.Warning);
        Assert.Contains("MBR disk holds at most four", Op(target: DiskPartitionStyle.MBR).Warning);
    }

    [Fact]
    public void GenerateScript_ChecksTheStyle_AndGivesTheLettersBack()
    {
        string script = Op(null, DiskPartitionStyle.GPT, [new DriveMount('e', 1048576), new DriveMount('F', 2097152)]).GenerateScript();

        Assert.Contains("(Get-Disk -Number 2).PartitionStyle -ne 'GPT'", script);
        Assert.Contains("'E:1048576', 'F:2097152'", script);
        Assert.DoesNotContain("Clear-Disk", script);
        Assert.DoesNotContain("Format-Volume", script);
        Assert.DoesNotContain("Remove-Partition", script);
    }

    [Fact]
    public void GenerateScript_WithoutDrives_StillChecksTheStyle()
    {
        string script = Op(target: DiskPartitionStyle.MBR).GenerateScript();

        Assert.Contains("-ne 'MBR'", script);
        Assert.Contains("foreach ($m in @())", script);
    }

    [Fact]
    public async Task Simulation_TouchesNothing()
    {
        var converter = new FakeConverter();
        var executor = new RecordingExecutor();
        var queue = new OperationQueue();
        queue.Add(Op(converter));

        await queue.ApplyAsync(executor, simulation: true);

        Assert.Empty(converter.Requests);
        Assert.Empty(executor.Scripts);
        Assert.Single(queue.Pending);
    }

    [Fact]
    public async Task Apply_ConvertsFirst_ThenRunsTheCheckScript()
    {
        var order = new List<string>();
        var converter = new FakeConverter(_ => order.Add("convert"));
        var executor = new RecordingExecutor(order);
        var queue = new OperationQueue();
        queue.Add(Op(converter, DiskPartitionStyle.MBR, [new DriveMount('E', MiB)]));

        var results = await queue.ApplyAsync(executor);

        Assert.True(results[0].Success);
        Assert.Equal(["convert", "script"], order);
        var request = Assert.Single(converter.Requests);
        Assert.Equal(2, request.DiskNumber);
        Assert.Equal(DiskPartitionStyle.MBR, request.Target);
        Assert.Equal(new DriveMount('E', MiB), Assert.Single(request.Mounts));
        Assert.Empty(queue.Pending);
    }

    [Fact]
    public async Task ConverterFailure_StopsTheQueue_AndRunsNoScript()
    {
        var failing = new FakeConverter(_ => throw new InvalidOperationException("Could not lock E:"));
        var executor = new RecordingExecutor();
        var queue = new OperationQueue();
        queue.Add(Op(failing));
        queue.Add(new ChangeVolumeLabel('D', "after"));

        var results = await queue.ApplyAsync(executor);

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Contains("Could not lock", results[0].Detail);
        Assert.Empty(executor.Scripts);
        Assert.Equal(2, queue.Pending.Count);
    }

    [Fact]
    public async Task CanceledBeforeStarting_NeverCallsTheConverter()
    {
        var converter = new FakeConverter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Op(converter).RunAsync(new RecordingExecutor(), "", null, cts.Token));
        Assert.Empty(converter.Requests);
    }
}
