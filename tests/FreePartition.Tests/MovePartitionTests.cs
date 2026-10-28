using System.Buffers.Binary;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FreePartition.Tests;

public class MovePartitionTests
{
    private const long MiB = 1L << 20;

    // ---------- SectorCopier ----------

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    /// <summary>Moves [source, source+length) to destination in a temp file and checks the data arrived intact.</summary>
    private static void AssertMoveKeepsData(long source, long destination, int length, int chunk)
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] original = Pattern(length);
            byte[] disk = new byte[8 * 1024];
            Pattern(disk.Length).CopyTo(disk, 0);
            original.CopyTo(disk, (int)source);
            File.WriteAllBytes(path, disk);

            using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
                SectorCopier.Move(handle, source, destination, length, chunkBytes: chunk);

            byte[] result = File.ReadAllBytes(path);
            Assert.Equal(original, result.AsSpan((int)destination, length).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1024, 3072, 1024, 512)]    // to the right, no overlap
    [InlineData(3072, 1024, 1024, 512)]    // to the left, no overlap
    [InlineData(1024, 2048, 3000, 512)]    // to the right, overlapping, several chunks
    [InlineData(2048, 1024, 3000, 512)]    // to the left, overlapping, several chunks
    [InlineData(1024, 1536, 4096, 4096)]   // overlapping, a single chunk
    [InlineData(1536, 1024, 4096, 700)]    // chunk that does not divide the length
    public void SectorCopier_KeepsTheDataIntact(long source, long destination, int length, int chunk) =>
        AssertMoveKeepsData(source, destination, length, chunk);

    [Fact]
    public void SectorCopier_ReportsProgressUpToOne()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[8192]);
            var reports = new List<double>();
            using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
                SectorCopier.Move(handle, 0, 4096, 4096, new SyncProgress<double>(reports.Add), chunkBytes: 1024);

            Assert.Equal(4, reports.Count);
            Assert.Equal(1.0, reports[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    // ---------- DriveLayout ----------

    /// <summary>Builds a DRIVE_LAYOUT_INFORMATION_EX buffer with (offset, length, number) entries.</summary>
    private static byte[] BuildLayout(bool gpt, params (long Offset, long Length, int Number)[] partitions)
    {
        var buffer = new byte[48 + 144 * partitions.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, gpt ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)partitions.Length);
        if (gpt)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), 34 * 512);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), 100 * MiB - 34 * 512 - 33 * 512);
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

    private static DriveLayout GptDisk() =>
        DriveLayout.Parse(BuildLayout(true, (1 * MiB, 10 * MiB, 1), (30 * MiB, 20 * MiB, 2)), 100 * MiB);

    [Fact]
    public void DriveLayout_Parse_ReadsPartitionsAndLimits()
    {
        var layout = GptDisk();

        Assert.True(layout.IsGpt);
        Assert.Equal(new[] { 1, 2 }, layout.Partitions.Select(p => p.Number));
        Assert.Equal(30 * MiB, layout.Partitions[1].Offset);
        Assert.Equal(34 * 512, layout.UsableStart);
    }

    [Fact]
    public void DriveLayout_Parse_RefusesGarbageAndExtendedPartitions()
    {
        Assert.Throws<InvalidDataException>(() => DriveLayout.Parse(new byte[10], 0));
        Assert.Throws<InvalidDataException>(() => DriveLayout.Parse(new byte[48 + 144].Select((b, i) => i == 0 ? (byte)2 : b).ToArray(), 0));

        byte[] mbr = BuildLayout(false, (1 * MiB, 50 * MiB, 1));
        mbr[48 + 32] = 0x0F;   // extended (LBA) partition type
        Assert.Throws<InvalidDataException>(() => DriveLayout.Parse(mbr, 100 * MiB));
    }

    [Fact]
    public void DriveLayout_CheckMove_RefusesOverlapWithAnotherPartition()
    {
        var layout = GptDisk();
        var ex = Assert.Throws<InvalidOperationException>(() => layout.CheckMove(1, 1 * MiB, 10 * MiB, 25 * MiB));
        Assert.Contains("overlaps partition 2", ex.Message);
    }

    [Fact]
    public void DriveLayout_CheckMove_AcceptsOverlapWithItselfAndFreeSpace()
    {
        var layout = GptDisk();

        Assert.Equal(1, layout.CheckMove(1, 1 * MiB, 10 * MiB, 5 * MiB).Number);       // overlaps its old position
        Assert.Equal(1, layout.CheckMove(1, 1 * MiB, 10 * MiB, 20 * MiB).Number);      // ends exactly where partition 2 starts
        Assert.Equal(2, layout.CheckMove(2, 30 * MiB, 20 * MiB, 11 * MiB).Number);     // to the left, touching partition 1
    }

    [Fact]
    public void DriveLayout_CheckMove_RefusesBadRequests()
    {
        var layout = GptDisk();

        Assert.Throws<InvalidOperationException>(() => layout.CheckMove(9, 1 * MiB, 10 * MiB, 20 * MiB));             // no such partition
        Assert.Throws<InvalidOperationException>(() => layout.CheckMove(1, 2 * MiB, 10 * MiB, 20 * MiB));             // layout changed
        Assert.Throws<InvalidOperationException>(() => layout.CheckMove(1, 1 * MiB, 9 * MiB, 20 * MiB));              // size changed
        Assert.Throws<InvalidOperationException>(() => layout.CheckMove(1, 1 * MiB, 10 * MiB, 1 * MiB));              // same place
        Assert.Throws<InvalidOperationException>(() => layout.CheckMove(1, 1 * MiB, 10 * MiB, 20 * MiB + 512));       // not aligned
        Assert.Throws<InvalidOperationException>(() => layout.CheckMove(2, 30 * MiB, 20 * MiB, 90 * MiB));            // past the end
        Assert.Throws<InvalidOperationException>(() => layout.CheckMove(2, 30 * MiB, 20 * MiB, 0));                   // inside the first MiB
    }

    [Fact]
    public void DriveLayout_WithMovedPartition_ChangesOnlyTheOffsetAndRewriteFlag()
    {
        byte[] original = BuildLayout(true, (1 * MiB, 10 * MiB, 1), (30 * MiB, 20 * MiB, 2));
        var layout = DriveLayout.Parse(original, 100 * MiB);

        byte[] moved = layout.WithMovedPartition(layout.Partitions[0], 15 * MiB);

        var diff = Enumerable.Range(0, original.Length).Where(i => original[i] != moved[i]).ToList();
        Assert.Equal(15 * MiB, BinaryPrimitives.ReadInt64LittleEndian(moved.AsSpan(48 + 8)));
        Assert.Equal(1, moved[48 + 28]);
        Assert.All(diff, i => Assert.InRange(i, 48 + 8, 48 + 28));   // nothing outside offset..rewrite flag of entry 0
        Assert.Equal(original[(48 + 144)..], moved[(48 + 144)..]);   // the other partition is byte-identical
    }

    // ---------- MovePartition operation ----------

    private sealed class FakeMover(Action<PartitionMoveRequest>? onMove = null) : IPartitionMover
    {
        public List<PartitionMoveRequest> Requests { get; } = new();

        public void Move(PartitionMoveRequest request, IProgress<string>? progress)
        {
            Requests.Add(request);
            progress?.Report("copying");
            onMove?.Invoke(request);
        }
    }

    private sealed class NoScriptsExecutor : ICommandExecutor
    {
        public int Calls { get; private set; }

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new CommandResult(0, "", ""));
        }
    }

    private static MovePartition Op(IPartitionMover? mover = null, char drive = 'E') =>
        new(1, 2, drive, 30 * MiB, 20 * MiB, 60 * MiB, mover);

    [Fact]
    public void MovePartition_WarnsAboutPowerLoss_AndIsDestructive()
    {
        var op = Op();

        Assert.True(op.Destructive);
        Assert.Contains("CORRUPTED", op.Warning);
        Assert.Contains("power", op.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INTERRUPTED", op.Description);
    }

    [Fact]
    public void MovePartition_IsRefusedOnTheSystemDrive()
    {
        var queue = new OperationQueue(protectedDrive: 'C');

        Assert.Throws<InvalidOperationException>(() => queue.Add(Op(drive: 'C')));
        queue.Add(Op());
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void MovePartition_InvalidValues_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => new MovePartition(-1, 2, 'E', 30 * MiB, 20 * MiB, 60 * MiB).Validate());
        Assert.Throws<ArgumentException>(() => new MovePartition(1, 0, 'E', 30 * MiB, 20 * MiB, 60 * MiB).Validate());
        Assert.Throws<ArgumentException>(() => new MovePartition(1, 2, '1', 30 * MiB, 20 * MiB, 60 * MiB).Validate());
        Assert.Throws<ArgumentException>(() => new MovePartition(1, 2, 'E', 30 * MiB, 0, 60 * MiB).Validate());
        Assert.Throws<ArgumentException>(() => new MovePartition(1, 2, 'E', 30 * MiB, 20 * MiB, 30 * MiB).Validate());
        Assert.Throws<ArgumentException>(() => new MovePartition(1, 2, 'E', 30 * MiB, 20 * MiB, 60 * MiB + 1).Validate());
        Assert.Throws<ArgumentException>(() => new MovePartition(1, 2, 'E', 30 * MiB, 20 * MiB, 4096).Validate());
    }

    [Fact]
    public async Task MovePartition_Simulation_TouchesNothing()
    {
        var mover = new FakeMover();
        var executor = new NoScriptsExecutor();
        var queue = new OperationQueue();
        queue.Add(Op(mover));

        var results = await queue.ApplyAsync(executor, simulation: true);

        Assert.Empty(mover.Requests);
        Assert.Equal(0, executor.Calls);
        Assert.Contains("PhysicalDrive1", results[0].Detail);
        Assert.Single(queue.Pending);
    }

    [Fact]
    public async Task MovePartition_Apply_UsesTheMoverInsteadOfPowerShell()
    {
        var mover = new FakeMover();
        var executor = new NoScriptsExecutor();
        var queue = new OperationQueue();
        queue.Add(Op(mover));

        var results = await queue.ApplyAsync(executor);

        Assert.True(results[0].Success);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(new PartitionMoveRequest(1, 2, 'E', 30 * MiB, 20 * MiB, 60 * MiB), Assert.Single(mover.Requests));
        Assert.Empty(queue.Pending);
    }

    [Fact]
    public async Task MovePartition_MoverFailure_StopsTheQueueAndKeepsTheRest()
    {
        var failing = new FakeMover(_ => throw new InvalidOperationException("Could not lock E:"));
        var queue = new OperationQueue();
        queue.Add(Op(failing));
        queue.Add(new ChangeVolumeLabel('D', "after"));
        var executor = new NoScriptsExecutor();

        var results = await queue.ApplyAsync(executor);

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Contains("Could not lock", results[0].Detail);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(2, queue.Pending.Count);
    }

    [Fact]
    public async Task MovePartition_CanceledBeforeStarting_NeverCallsTheMover()
    {
        var mover = new FakeMover();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Op(mover).RunAsync(new NoScriptsExecutor(), "", null, cts.Token));
        Assert.Empty(mover.Requests);
    }
}
