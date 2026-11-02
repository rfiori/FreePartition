using FreePartition.Core.Disks;
using FreePartition.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FreePartition.Tests;

public class WipeTests
{
    private const long MiB = 1L << 20;

    // ---------- SectorWiper ----------

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        data.AsSpan().Replace((byte)0, (byte)1);   // no zero bytes, so a wiped byte is always visible
        return data;
    }

    /// <summary>Wipes [start, start+length) of a 16 KB temp file and returns the original and the result.</summary>
    private static (byte[] Before, byte[] After) WipeFile(long start, long length, WipePattern pattern, bool verify = false,
        int sector = 512, int chunk = 1024, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] before = Pattern(16 * 1024);
            File.WriteAllBytes(path, before);
            try
            {
                using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
                SectorWiper.Wipe(handle, start, length, pattern, verify, sector, progress, ct, chunk);
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            return (before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(4096, 4096, 1024)]    // several whole chunks
    [InlineData(1536, 3584, 1024)]    // the last chunk is shorter; not 4 KB-aligned
    [InlineData(0, 16384, 16384)]     // the whole file in one chunk
    public void SectorWiper_Zeros_WipesExactlyTheRange(int start, int length, int chunk)
    {
        var (before, after) = WipeFile(start, length, WipePattern.Zeros, chunk: chunk);

        Assert.All(after.AsSpan(start, length).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(before[..start], after[..start]);
        Assert.Equal(before[(start + length)..], after[(start + length)..]);
    }

    [Fact]
    public void SectorWiper_Random_ReplacesTheRange_AndLeavesTheRest()
    {
        var (before, after) = WipeFile(4096, 8192, WipePattern.Random);

        Assert.NotEqual(before[4096..12288], after[4096..12288]);
        Assert.Equal(before[..4096], after[..4096]);
        Assert.Equal(before[12288..], after[12288..]);
    }

    [Fact]
    public void SectorWiper_ZerosWithVerify_Passes_AndReportsUpToOne()
    {
        var reports = new List<double>();
        var (_, after) = WipeFile(0, 4096, WipePattern.Zeros, verify: true, progress: new SyncProgress<double>(reports.Add));

        Assert.All(after[..4096], b => Assert.Equal(0, b));
        Assert.Equal(8, reports.Count);          // 4 chunks written + 4 verified
        Assert.Equal(0.5, reports[3]);
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public void SectorWiper_VerifyWithRandom_IsRefused() =>
        Assert.Throws<ArgumentException>(() => WipeFile(0, 4096, WipePattern.Random, verify: true));

    [Theory]
    [InlineData(100, 4096, 512)]      // start off the sector grid
    [InlineData(512, 1000, 512)]      // length off the sector grid
    [InlineData(512, 4096, 4096)]     // a 512-byte offset on a 4Kn device
    public void SectorWiper_UnalignedRange_IsRefused_AndWritesNothing(int start, int length, int sector)
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] before = Pattern(16 * 1024);
            File.WriteAllBytes(path, before);
            using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
                Assert.Throws<InvalidOperationException>(() =>
                    SectorWiper.Wipe(handle, start, length, WipePattern.Zeros, false, sector, chunkBytes: 4096));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SectorWiper_CanceledBeforeStarting_WritesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string path = Path.GetTempFileName();
        try
        {
            byte[] before = Pattern(16 * 1024);
            File.WriteAllBytes(path, before);
            using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite))
                Assert.Throws<OperationCanceledException>(() =>
                    SectorWiper.Wipe(handle, 0, 4096, WipePattern.Zeros, false, 512, ct: cts.Token, chunkBytes: 1024));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- FreeSpaceWiper ----------

    /// <summary>A stream that fails with "disk full" once it holds <paramref name="capacity"/> bytes.</summary>
    private sealed class FullAfterStream(long capacity, int errorCode = 112) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > capacity)
                throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070000) | errorCode);
            base.Write(buffer, offset, count);
        }
    }

    [Theory]
    [InlineData(112)]   // ERROR_DISK_FULL
    [InlineData(39)]    // ERROR_HANDLE_DISK_FULL
    public void FreeSpaceWiper_Fill_FillsUpToTheLastCluster(int errorCode)
    {
        long capacity = 3 * MiB + 8192;
        using var stream = new FullAfterStream(capacity, errorCode);

        long written = FreeSpaceWiper.Fill(stream, WipePattern.Zeros, capacity);

        Assert.Equal(capacity, written);
        Assert.Equal(capacity, stream.Length);
        Assert.True(stream.ToArray().AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void FreeSpaceWiper_Fill_Random_IsNotAllZeros()
    {
        using var stream = new FullAfterStream(2 * MiB);

        FreeSpaceWiper.Fill(stream, WipePattern.Random, 2 * MiB);

        Assert.True(stream.ToArray().AsSpan().IndexOfAnyExcept((byte)0) >= 0);
    }

    [Fact]
    public void FreeSpaceWiper_Fill_ReportsProgressUpToOne()
    {
        var reports = new List<double>();
        using var stream = new FullAfterStream(2 * MiB);

        FreeSpaceWiper.Fill(stream, WipePattern.Zeros, 2 * MiB, new SyncProgress<double>(reports.Add));

        Assert.Equal(1.0, reports[^1]);
        Assert.All(reports, r => Assert.InRange(r, 0, 1));
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The device is not ready.", unchecked((int)0x80070015));
    }

    [Fact]
    public void FreeSpaceWiper_Fill_OtherErrors_Propagate()
    {
        using var stream = new BrokenStream();
        Assert.Throws<IOException>(() => FreeSpaceWiper.Fill(stream, WipePattern.Zeros, MiB));
    }

    [Fact]
    public void FreeSpaceWiper_Fill_Canceled_Stops()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stream = new FullAfterStream(MiB);

        Assert.Throws<OperationCanceledException>(() => FreeSpaceWiper.Fill(stream, WipePattern.Zeros, MiB, ct: cts.Token));
        Assert.Equal(0, stream.Length);
    }

    // ---------- Operations ----------

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    /// <summary>Records every call, in order, in a list shared with <see cref="RecordingExecutor"/>.</summary>
    private sealed class FakeWiper(List<string> calls, Action<string>? onCall = null) : IDiskWiper
    {
        public List<object> Requests { get; } = new();

        public void EnsureNotSystemDisk(int diskNumber) => Record($"guard {diskNumber}");

        public void WipeVolume(VolumeWipeRequest request, IProgress<string>? progress, CancellationToken ct)
        {
            Record("volume");
            Requests.Add(request);
        }

        public void WipeDisk(DiskWipeRequest request, IProgress<string>? progress, CancellationToken ct)
        {
            Record("disk");
            Requests.Add(request);
        }

        public long WipeFreeSpace(char drive, WipePattern pattern, IProgress<string>? progress, CancellationToken ct)
        {
            Record($"free {drive} {pattern}");
            return 1234;
        }

        private void Record(string call)
        {
            calls.Add(call);
            onCall?.Invoke(call);
        }
    }

    private sealed class RecordingExecutor(List<string> calls, int exitCode = 0) : ICommandExecutor
    {
        public List<string> Scripts { get; } = new();

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            calls.Add("script");
            Scripts.Add(script);
            return Task.FromResult(new CommandResult(exitCode, "", exitCode == 0 ? "" : "Clear-Disk failed"));
        }
    }

    private static WipePartition PartitionOp(IDiskWiper? wiper = null, char drive = 'E',
        WipePattern pattern = WipePattern.Zeros, bool verify = false) =>
        new(1, 2, drive, 30 * MiB, 20 * MiB, pattern, verify, wiper);

    [Fact]
    public void WipeOperations_AreDestructive_AndWarn()
    {
        DiskOperation[] ops = [PartitionOp(), new WipeDisk(3), new WipeFreeSpace('E')];

        Assert.All(ops, op =>
        {
            Assert.True(op.Destructive);
            Assert.True(op.ForbiddenOnSystemDrive);
            Assert.False(string.IsNullOrWhiteSpace(op.Warning));
        });
        Assert.Contains("DESTROYED", ops[0].Description);
        Assert.Contains("DESTROYED", ops[1].Description);
    }

    [Fact]
    public void WipeOperations_AreRefusedOnTheSystemDrive()
    {
        var queue = new OperationQueue(protectedDrive: 'C');

        Assert.Throws<InvalidOperationException>(() => queue.Add(PartitionOp(drive: 'C')));
        Assert.Throws<InvalidOperationException>(() => queue.Add(new WipeFreeSpace('c')));
        Assert.Throws<InvalidOperationException>(() => queue.Add(new WipeDisk(0, Drives: ['D', 'C'])));
        Assert.Empty(queue.Pending);

        queue.Add(PartitionOp());
        queue.Add(new WipeFreeSpace('E'));
        queue.Add(new WipeDisk(3, Drives: ['E', 'F']));
        Assert.Equal(3, queue.Pending.Count);
    }

    [Fact]
    public void WipeOperations_InvalidValues_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => PartitionOp(drive: '1').Validate());
        Assert.Throws<ArgumentException>(() => PartitionOp(pattern: WipePattern.Random, verify: true).Validate());
        Assert.Throws<ArgumentException>(() => PartitionOp(pattern: (WipePattern)9).Validate());
        Assert.Throws<ArgumentException>(() => new WipePartition(1, 0, 'E', 30 * MiB, 20 * MiB).Validate());
        Assert.Throws<ArgumentException>(() => new WipePartition(1, 2, 'E', 30 * MiB, 0).Validate());
        Assert.Throws<ArgumentException>(() => new WipePartition(1, 2, 'E', 30 * MiB + 1, 20 * MiB).Validate());
        Assert.Throws<ArgumentException>(() => new WipeDisk(-1).Validate());
        Assert.Throws<ArgumentException>(() => new WipeDisk(3, WipePattern.Random, Verify: true).Validate());
        Assert.Throws<ArgumentException>(() => new WipeDisk(3, Drives: ['$']).Validate());
        Assert.Throws<ArgumentException>(() => new WipeFreeSpace(';').Validate());
    }

    [Fact]
    public void WipeDisk_Script_OnlyUsesTheValidatedDiskNumber_AndRefusesTheSystemDisk()
    {
        string script = new WipeDisk(3).GenerateScript();

        Assert.Contains("Get-Disk -Number 3", script);
        Assert.Contains("$d.IsSystem -or $d.IsBoot", script);
        Assert.Contains("Clear-Disk -Number 3 -RemoveData", script);
        Assert.DoesNotContain("Number 0", script);
    }

    [Fact]
    public async Task WipeOperations_Simulation_TouchesNothing()
    {
        var calls = new List<string>();
        var wiper = new FakeWiper(calls);
        var queue = new OperationQueue();
        queue.Add(PartitionOp(wiper));
        queue.Add(new WipeDisk(3, Wiper: wiper));
        queue.Add(new WipeFreeSpace('E', Wiper: wiper));

        var results = await queue.ApplyAsync(new RecordingExecutor(calls), simulation: true);

        Assert.Empty(calls);
        Assert.Equal(3, results.Count);
        Assert.Equal(3, queue.Pending.Count);
    }

    [Fact]
    public async Task WipePartition_Apply_SendsTheSelectedPartitionToTheWiper()
    {
        var calls = new List<string>();
        var wiper = new FakeWiper(calls);
        var queue = new OperationQueue();
        queue.Add(PartitionOp(wiper, drive: 'e', verify: true));

        var results = await queue.ApplyAsync(new RecordingExecutor(calls));

        Assert.True(results[0].Success);
        Assert.Equal(["volume"], calls);
        Assert.Equal(new VolumeWipeRequest(1, 2, 'E', 30 * MiB, 20 * MiB, WipePattern.Zeros, true), Assert.Single(wiper.Requests));
    }

    [Fact]
    public async Task WipeDisk_Apply_GuardsThenClearsThenOverwrites()
    {
        var calls = new List<string>();
        var wiper = new FakeWiper(calls);
        var queue = new OperationQueue();
        queue.Add(new WipeDisk(3, WipePattern.Random, Wiper: wiper));

        var results = await queue.ApplyAsync(new RecordingExecutor(calls));

        Assert.True(results[0].Success);
        Assert.Equal(["guard 3", "script", "disk"], calls);
        Assert.Equal(new DiskWipeRequest(3, WipePattern.Random, false), Assert.Single(wiper.Requests));
    }

    [Fact]
    public async Task WipeDisk_WhenTheGuardRefuses_NothingRuns()
    {
        var calls = new List<string>();
        var wiper = new FakeWiper(calls, call =>
        {
            if (call.StartsWith("guard")) throw new InvalidOperationException("Disk 3 holds Windows");
        });
        var queue = new OperationQueue();
        queue.Add(new WipeDisk(3, Wiper: wiper));

        var results = await queue.ApplyAsync(new RecordingExecutor(calls));

        Assert.False(results[0].Success);
        Assert.Contains("holds Windows", results[0].Detail);
        Assert.Equal(["guard 3"], calls);
        Assert.Single(queue.Pending);
    }

    [Fact]
    public async Task WipeDisk_WhenClearDiskFails_DoesNotOverwrite()
    {
        var calls = new List<string>();
        var queue = new OperationQueue();
        queue.Add(new WipeDisk(3, Wiper: new FakeWiper(calls)));

        var results = await queue.ApplyAsync(new RecordingExecutor(calls, exitCode: 1));

        Assert.False(results[0].Success);
        Assert.Equal(["guard 3", "script"], calls);
    }

    [Fact]
    public async Task WipeFreeSpace_Apply_UsesTheWiper()
    {
        var calls = new List<string>();
        var queue = new OperationQueue();
        queue.Add(new WipeFreeSpace('e', WipePattern.Random, new FakeWiper(calls)));

        var results = await queue.ApplyAsync(new RecordingExecutor(calls));

        Assert.True(results[0].Success);
        Assert.Equal(["free E Random"], calls);
    }

    [Fact]
    public async Task WipeOperations_CanceledBeforeStarting_NeverCallTheWiper()
    {
        var calls = new List<string>();
        var wiper = new FakeWiper(calls);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PartitionOp(wiper).RunAsync(new RecordingExecutor(calls), "", null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WipeDisk(3, Wiper: wiper).RunAsync(new RecordingExecutor(calls), "", null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WipeFreeSpace('E', Wiper: wiper).RunAsync(new RecordingExecutor(calls), "", null, cts.Token));
        Assert.Empty(calls);
    }
}
