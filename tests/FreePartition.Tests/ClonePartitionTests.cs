using FreePartition.Core.Disks;
using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class ClonePartitionTests
{
    private const long MiB = 1L << 20;

    // ---------- SectorCopier.Copy ----------

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    [Fact]
    public void Copy_BetweenTwoFiles_CopiesTheRangeAndLeavesTheSourceAlone()
    {
        string from = Path.GetTempFileName(), to = Path.GetTempFileName();
        try
        {
            byte[] source = Pattern(8192);
            byte[] target = new byte[8192];
            File.WriteAllBytes(from, source);
            File.WriteAllBytes(to, target);

            using (var src = File.OpenHandle(from, FileMode.Open, FileAccess.Read))
            using (var dst = File.OpenHandle(to, FileMode.Open, FileAccess.ReadWrite))
                SectorCopier.Copy(src, 1024, dst, 4096, 3000, chunkBytes: 1000);   // 3 chunks and a shorter last one

            byte[] result = File.ReadAllBytes(to);
            Assert.Equal(source[1024..4024], result[4096..7096]);
            Assert.All(result[..4096], b => Assert.Equal(0, b));      // nothing before the destination
            Assert.All(result[7096..], b => Assert.Equal(0, b));      // nothing after it
            Assert.Equal(source, File.ReadAllBytes(from));
        }
        finally
        {
            File.Delete(from);
            File.Delete(to);
        }
    }

    [Fact]
    public void Copy_Canceled_StopsBetweenChunks()
    {
        string from = Path.GetTempFileName(), to = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(from, Pattern(4096));
            File.WriteAllBytes(to, new byte[4096]);
            using var cts = new CancellationTokenSource();
            using var src = File.OpenHandle(from, FileMode.Open, FileAccess.Read);
            using var dst = File.OpenHandle(to, FileMode.Open, FileAccess.ReadWrite);
            var progress = new Progress(_ => cts.Cancel());

            Assert.Throws<OperationCanceledException>(
                () => SectorCopier.Copy(src, 0, dst, 0, 4096, progress, cts.Token, chunkBytes: 1024));
        }
        finally
        {
            File.Delete(from);
            File.Delete(to);
        }
    }

    private sealed class Progress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    // ---------- ClonePartition operation ----------

    private sealed class FakeCloner(Action<PartitionCloneRequest>? onClone = null) : IPartitionCloner
    {
        public List<PartitionCloneRequest> Requests { get; } = new();

        public void Clone(PartitionCloneRequest request, IProgress<string>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            onClone?.Invoke(request);
        }
    }

    private sealed class RecordingExecutor : ICommandExecutor
    {
        public List<string> Scripts { get; } = new();

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            return Task.FromResult(new CommandResult(0, "", ""));
        }
    }

    private static ClonePartition Op(IPartitionCloner? cloner = null, char drive = 'E', char? letter = null,
        int destinationDisk = 2, long destinationOffset = 100 * MiB) =>
        new(1, 2, drive, 30 * MiB, 20 * MiB, destinationDisk, destinationOffset, FileSystemFormat.NTFS, letter, cloner);

    [Fact]
    public void Validate_AcceptsAnotherDiskAndTheFreeSpaceOfTheSameDisk()
    {
        Op().Validate();
        Op(destinationDisk: 1, destinationOffset: 60 * MiB).Validate();
    }

    [Theory]
    [InlineData(1, 40 * MiB)]    // starts inside the source
    [InlineData(1, 20 * MiB)]    // ends inside the source
    [InlineData(1, 30 * MiB)]    // on top of the source
    public void Validate_RefusesADestinationOverlappingTheSource(int disk, long offset) =>
        Assert.Throws<ArgumentException>(() => Op(destinationDisk: disk, destinationOffset: offset).Validate());

    [Fact]
    public void Validate_SameOffsetOnAnotherDisk_IsFine() =>
        Op(destinationDisk: 3, destinationOffset: 30 * MiB).Validate();

    [Theory]
    [InlineData(100 * MiB + 512)]   // not on 4 KB
    [InlineData(0)]                 // inside the first MB
    [InlineData(-4096)]
    public void Validate_RefusesABadDestination(long offset) =>
        Assert.Throws<ArgumentException>(() => Op(destinationOffset: offset).Validate());

    [Fact]
    public void Validate_RefusesTheSameLetterForTheCopy() =>
        Assert.Throws<ArgumentException>(() => Op(letter: 'e').Validate());

    [Fact]
    public void ClonePartition_IsRefusedOnTheSystemDrive()
    {
        var queue = new OperationQueue(protectedDrive: 'C');

        Assert.Throws<InvalidOperationException>(() => queue.Add(Op(drive: 'C')));
        queue.Add(Op());
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void GenerateScript_MountsTheCopyAtItsOffset_WithTheChosenLetter()
    {
        string script = Op(letter: 'k').GenerateScript();

        Assert.Contains("$o = 104857600", script);
        Assert.Contains("Get-Partition -DiskNumber 2", script);
        Assert.Contains("Add-PartitionAccessPath -InputObject $n -AccessPath 'K:\\'", script);
        Assert.DoesNotContain("Format-Volume", script);
    }

    [Fact]
    public void GenerateScript_WithoutALetter_AssignsOneAutomatically() =>
        Assert.Contains("-AssignDriveLetter", Op().GenerateScript());

    [Fact]
    public async Task Simulation_TouchesNothing()
    {
        var cloner = new FakeCloner();
        var executor = new RecordingExecutor();
        var queue = new OperationQueue();
        queue.Add(Op(cloner));

        await queue.ApplyAsync(executor, simulation: true);

        Assert.Empty(cloner.Requests);
        Assert.Empty(executor.Scripts);
        Assert.Single(queue.Pending);
    }

    [Fact]
    public async Task Apply_ClonesFirst_ThenRunsTheMountScript()
    {
        var order = new List<string>();
        var cloner = new FakeCloner(_ => order.Add("clone"));
        var executor = new RecordingExecutor();
        var queue = new OperationQueue();
        queue.Add(Op(cloner));

        var results = await queue.ApplyAsync(new OrderedExecutor(executor, order));

        Assert.True(results[0].Success);
        Assert.Equal(["clone", "script"], order);
        Assert.Equal(new PartitionCloneRequest(1, 2, 'E', 30 * MiB, 20 * MiB, 2, 100 * MiB, FileSystemFormat.NTFS),
            Assert.Single(cloner.Requests));
        Assert.Empty(queue.Pending);
    }

    private sealed class OrderedExecutor(RecordingExecutor inner, List<string> order) : ICommandExecutor
    {
        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            order.Add("script");
            return inner.ExecuteAsync(script, ct);
        }
    }

    [Fact]
    public async Task ClonerFailure_StopsTheQueue_AndRunsNoScript()
    {
        var failing = new FakeCloner(_ => throw new InvalidOperationException("Could not lock E:"));
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
    public async Task CanceledBeforeStarting_NeverCallsTheCloner()
    {
        var cloner = new FakeCloner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Op(cloner).RunAsync(new RecordingExecutor(), "", null, cts.Token));
        Assert.Empty(cloner.Requests);
    }
}
