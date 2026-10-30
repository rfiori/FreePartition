using FreePartition.Core.Disks;

namespace FreePartition.Tests;

public class VolumeUsageScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fp-usage-" + Guid.NewGuid().ToString("N"));

    public VolumeUsageScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, int bytes)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    // ---------- Compose ----------

    [Fact]
    public void Compose_PutsTheRemainderInOther_AndSkipsEmptySegments()
    {
        var segments = VolumeUsageScanner.Compose(1000, 300, 0, 200);

        Assert.Equal(new[] { VolumeUsageScanner.OperatingSystem, VolumeUsageScanner.Users, VolumeUsageScanner.Other },
            segments.Select(s => s.Name));
        Assert.Equal(new ulong[] { 300, 200, 500 }, segments.Select(s => s.Bytes));
    }

    [Fact]
    public void Compose_NeverExceedsTheUsedSpace()
    {
        var segments = VolumeUsageScanner.Compose(1000, 700, 700, 700);

        Assert.Equal(1000L, segments.Sum(s => (long)s.Bytes));
        Assert.Equal(new ulong[] { 700, 300 }, segments.Select(s => s.Bytes));   // the rest is clamped away
    }

    [Fact]
    public void Compose_WithNothingMeasured_IsAllOther()
    {
        var only = Assert.Single(VolumeUsageScanner.Compose(500, 0, 0, 0));
        Assert.Equal(new UsageSegment(VolumeUsageScanner.Other, 500), only);
        Assert.Empty(VolumeUsageScanner.Compose(0, 0, 0, 0));
    }

    // ---------- scanning ----------

    [Fact]
    public void LooksLikeWindows_NeedsSystem32()
    {
        Assert.False(VolumeUsageScanner.LooksLikeWindows(_root));
        Directory.CreateDirectory(Path.Combine(_root, "Windows", "System32"));
        Assert.True(VolumeUsageScanner.LooksLikeWindows(_root));
    }

    [Fact]
    public void FolderSize_SumsNestedFiles_AndIgnoresAMissingFolder()
    {
        Write(@"a\one.bin", 100);
        Write(@"a\deep\two.bin", 250);

        Assert.Equal(350UL, VolumeUsageScanner.FolderSize(Path.Combine(_root, "a")));
        Assert.Equal(0UL, VolumeUsageScanner.FolderSize(Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task ScanAsync_SplitsTheDriveIntoItsPieces()
    {
        Write(@"Windows\System32\kernel.bin", 400);
        Write(@"Windows\other.bin", 100);
        Write("pagefile.sys", 50);
        Write(@"Program Files\app\app.bin", 200);
        Write(@"Users\me\doc.bin", 300);
        Write("loose.bin", 10);

        var reports = new List<IReadOnlyList<UsageSegment>>();
        var result = await VolumeUsageScanner.ScanAsync(_root, 1100, new SyncProgress(report => { lock (reports) reports.Add(report); }));   // the three scans report from different threads

        Assert.Equal(550UL, result.Single(s => s.Name == VolumeUsageScanner.OperatingSystem).Bytes);
        Assert.Equal(200UL, result.Single(s => s.Name == VolumeUsageScanner.Programs).Bytes);
        Assert.Equal(300UL, result.Single(s => s.Name == VolumeUsageScanner.Users).Bytes);
        Assert.Equal(50UL, result.Single(s => s.Name == VolumeUsageScanner.Other).Bytes);
        Assert.Equal(3, reports.Count);   // one report per finished piece
    }

    [Fact]
    public async Task ScanAsync_CanBeCanceled()
    {
        Write(@"Windows\x.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VolumeUsageScanner.ScanAsync(_root, 100, null, cts.Token));
    }

    private sealed class SyncProgress(Action<IReadOnlyList<UsageSegment>> onReport) : IProgress<IReadOnlyList<UsageSegment>>
    {
        public void Report(IReadOnlyList<UsageSegment> value) => onReport(value);
    }
}
