using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class OperationQueueTests
{
    private sealed class FakeExecutor : ICommandExecutor
    {
        private readonly Func<string, CommandResult> _response;
        public List<string> Scripts { get; } = new();

        public FakeExecutor(Func<string, CommandResult>? response = null)
            => _response = response ?? (_ => new CommandResult(0, "ok", ""));

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            return Task.FromResult(_response(script));
        }
    }

    /// <summary>Progress that runs on the calling thread (Progress&lt;T&gt; posts, so a test could miss the message).</summary>
    private sealed class SyncProgress(Action<string> onReport) : IProgress<string>
    {
        public void Report(string value) => onReport(value);
    }

    private sealed class FakeLog : IOperationLog
    {
        public List<string> Lines { get; } = new();
        public void Log(string message) => Lines.Add(message);
    }

    // ---------- script generation ----------

    [Fact]
    public void Resize_GeneratesScriptWithSize()
    {
        var op = new ResizePartition('d', 50_000_000_000);
        Assert.Equal("Resize-Partition -DriveLetter D -Size 50000000000", op.GenerateScript());
    }

    [Fact]
    public void Extend_WithoutSize_UsesMaximumSupportedSize()
    {
        var script = new ResizePartition('E').GenerateScript();
        Assert.Contains("Get-PartitionSupportedSize -DriveLetter E", script);
        Assert.Contains("Resize-Partition -DriveLetter E -Size $max", script);
    }

    [Fact]
    public void Format_GeneratesScriptWithFileSystemAndLabel()
    {
        var op = new FormatVolume('f', FileSystemFormat.exFAT, "Backup 2026");
        Assert.Equal("Format-Volume -DriveLetter F -FileSystem exFAT -NewFileSystemLabel 'Backup 2026' -Confirm:$false",
            op.GenerateScript());
    }

    [Fact]
    public void ChangeLetter_GeneratesScript()
    {
        Assert.Equal("Set-Partition -DriveLetter G -NewDriveLetter H", new ChangeDriveLetter('g', 'h').GenerateScript());
    }


    // ---------- create / delete partition ----------

    [Fact]
    public void CreatePartition_GeneratesScript()
    {
        var all = new CreatePartition(1, FileSystemFormat.NTFS, "Data").GenerateScript();
        Assert.Equal("$p = New-Partition -DiskNumber 1 -UseMaximumSize -AssignDriveLetter; " +
                     "Format-Volume -Partition $p -FileSystem NTFS -NewFileSystemLabel 'Data' -Confirm:$false", all);

        var sized = new CreatePartition(2, FileSystemFormat.exFAT, "", 5_000_000_000, 'm').GenerateScript();
        Assert.Contains("-Size 5000000000 -DriveLetter M;", sized);
    }

    [Fact]
    public void CreatePartition_InvalidParameters_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => new CreatePartition(-1, FileSystemFormat.NTFS).Validate());
        Assert.Throws<ArgumentException>(() => new CreatePartition(1, FileSystemFormat.NTFS, "", 1000).Validate());
        Assert.Throws<ArgumentException>(() => new CreatePartition(1, FileSystemFormat.NTFS, "", null, '1').Validate());
        Assert.Throws<ArgumentException>(() => new CreatePartition(1, FileSystemFormat.NTFS, "x'; calc; '").Validate());
    }

    [Fact]
    public void DeletePartition_GeneratesScript_AndIsRefusedOnSystemDrive()
    {
        Assert.Equal("Remove-Partition -DriveLetter F -Confirm:$false", new DeletePartition('f').GenerateScript());

        var queue = new OperationQueue(protectedDrive: 'C');
        Assert.Throws<InvalidOperationException>(() => queue.Add(new DeletePartition('c')));
        Assert.Empty(queue.Pending);
    }

    // ---------- active / hide / show ----------

    [Fact]
    public void SetActive_GeneratesScript_AndIsRefusedOnSystemDrive()
    {
        Assert.Equal("Set-Partition -DriveLetter F -IsActive $true", new SetActivePartition('f').GenerateScript());

        var queue = new OperationQueue(protectedDrive: 'C');
        Assert.Throws<InvalidOperationException>(() => queue.Add(new SetActivePartition('c')));
    }

    [Fact]
    public void HidePartition_GeneratesScript_AndIsRefusedOnSystemDrive()
    {
        Assert.Equal("Remove-PartitionAccessPath -DriveLetter F -AccessPath 'F:\\'", new HidePartition('f').GenerateScript());

        var queue = new OperationQueue(protectedDrive: 'C');
        Assert.Throws<InvalidOperationException>(() => queue.Add(new HidePartition('c')));
    }

    [Fact]
    public void ShowPartition_GeneratesScript()
    {
        Assert.Equal("$p = Get-Partition -DiskNumber 1 -PartitionNumber 2; " +
                     "if ($p.AccessPaths -notcontains 'G:\\') { Add-PartitionAccessPath -InputObject $p -AccessPath 'G:\\' }",
            new ShowPartition(1, 2, 'g').GenerateScript());
    }

    [Fact]
    public void ShowPartition_WithInvalidValues_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ShowPartition(-1, 1, 'G').Validate());
        Assert.Throws<ArgumentException>(() => new ShowPartition(0, 0, 'G').Validate());
        Assert.Throws<ArgumentException>(() => new ShowPartition(0, 1, '1').Validate());
    }

    // ---------- check volume ----------

    [Fact]
    public void CheckVolume_ScanOnly_IsSafeAndAllowedOnSystemDrive()
    {
        var scan = new CheckVolume('c');
        Assert.Equal("$r = Repair-Volume -DriveLetter C -Scan; \"Result for C: $r\"", scan.GenerateScript());
        Assert.False(scan.Destructive);

        var queue = new OperationQueue(protectedDrive: 'C');
        queue.Add(scan);
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void CheckVolume_Repair_IsDestructiveAndRefusedOnSystemDrive()
    {
        var repair = new CheckVolume('f', Repair: true);
        Assert.Equal("$r = Repair-Volume -DriveLetter F -OfflineScanAndFix; \"Result for F: $r\"", repair.GenerateScript());
        Assert.True(repair.Destructive);

        var queue = new OperationQueue(protectedDrive: 'C');
        Assert.Throws<InvalidOperationException>(() => queue.Add(new CheckVolume('c', Repair: true)));
    }

    [Fact]
    public async Task CheckVolume_ShowsTheCommandOutputInTheProgress()
    {
        var queue = new OperationQueue();
        queue.Add(new CheckVolume('D'));
        var messages = new List<string>();

        await queue.ApplyAsync(new FakeExecutor(_ => new CommandResult(0, "Result for D: NoErrorsFound", "")),
            progress: new SyncProgress(messages.Add));

        Assert.Contains(messages, m => m.Contains("NoErrorsFound"));
    }

    // ---------- validation ----------

    [Theory]
    [InlineData("x'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("a$(calc)")]
    [InlineData("bad;label")]
    public void Label_WithDangerousCharacters_IsRefused(string label)
    {
        Assert.Throws<ArgumentException>(() => new ChangeVolumeLabel('D', label).Validate());
        Assert.Throws<ArgumentException>(() => new FormatVolume('D', FileSystemFormat.NTFS, label).Validate());
    }

    [Fact]
    public void Fat32_LabelLongerThan11Characters_IsRefused()
    {
        var op = new FormatVolume('D', FileSystemFormat.FAT32, "VeryLongLabel");
        Assert.Throws<ArgumentException>(op.Validate);
    }

    [Fact]
    public void InvalidLetter_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ResizePartition('1').Validate());
        Assert.Throws<ArgumentException>(() => new ChangeDriveLetter('D', 'D').Validate());
    }

    [Fact]
    public void Resize_WithSizeBelow1MB_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ResizePartition('D', 1000).Validate());
    }

    // ---------- queue ----------

    [Fact]
    public void FormatSystemDrive_IsRefused()
    {
        var queue = new OperationQueue(protectedDrive: 'C');

        Assert.Throws<InvalidOperationException>(
            () => queue.Add(new FormatVolume('c', FileSystemFormat.NTFS)));
        Assert.Empty(queue.Pending);

        // Other drives and safe operations on the system drive are still allowed.
        queue.Add(new FormatVolume('D', FileSystemFormat.NTFS));
        queue.Add(new ChangeVolumeLabel('C', "System"));
        Assert.Equal(2, queue.Pending.Count);
    }

    [Fact]
    public void AddRemoveAndClear_UpdateTheQueueAndRaiseTheEvent()
    {
        var queue = new OperationQueue();
        int events = 0;
        queue.Changed += (_, _) => events++;

        var a = new ChangeVolumeLabel('D', "A");
        var b = new ChangeVolumeLabel('E', "B");
        queue.Add(a);
        queue.Add(b);
        Assert.True(queue.Remove(a));
        Assert.Single(queue.Pending);
        queue.Clear();
        Assert.Empty(queue.Pending);
        Assert.Equal(4, events);
    }

    // ---------- apply ----------

    [Fact]
    public async Task Apply_RunsInOrder_AndRemovesCompletedOnes()
    {
        var log = new FakeLog();
        var queue = new OperationQueue(log: log);
        queue.Add(new ChangeVolumeLabel('D', "Data"));
        queue.Add(new ChangeDriveLetter('E', 'Z'));
        var executor = new FakeExecutor();

        var results = await queue.ApplyAsync(executor);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(new[]
        {
            "Set-Volume -DriveLetter D -NewFileSystemLabel 'Data'",
            "Set-Partition -DriveLetter E -NewDriveLetter Z"
        }, executor.Scripts);
        Assert.Empty(queue.Pending);
        Assert.Contains(log.Lines, l => l.StartsWith("COMPLETED"));
    }

    [Fact]
    public async Task Apply_StopsAtFirstFailure_AndKeepsTheRestPending()
    {
        var queue = new OperationQueue();
        var first = new ChangeVolumeLabel('D', "A");
        var second = new ChangeVolumeLabel('E', "B");
        var third = new ChangeVolumeLabel('F', "C");
        queue.Add(first);
        queue.Add(second);
        queue.Add(third);

        var executor = new FakeExecutor(script => script.Contains("-DriveLetter E")
            ? new CommandResult(1, "", "Access denied")
            : new CommandResult(0, "", ""));

        var results = await queue.ApplyAsync(executor);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
        Assert.Equal("Access denied", results[1].Detail);
        Assert.Equal(2, executor.Scripts.Count);                 // the third one was never run
        Assert.Equal(new DiskOperation[] { second, third }, queue.Pending);
    }

    [Fact]
    public async Task Simulation_RunsNothing_AndKeepsTheQueue()
    {
        var queue = new OperationQueue();
        queue.Add(new FormatVolume('D', FileSystemFormat.NTFS, "X"));
        var executor = new FakeExecutor();

        var results = await queue.ApplyAsync(executor, simulation: true);

        Assert.Empty(executor.Scripts);
        Assert.Single(results);
        Assert.Contains("Format-Volume", results[0].Detail);
        Assert.Single(queue.Pending);
    }

    [Fact]
    public async Task Apply_WithExceptionInExecutor_BecomesFailureAndStops()
    {
        var queue = new OperationQueue();
        queue.Add(new ChangeVolumeLabel('D', "A"));
        var executor = new FakeExecutor(_ => throw new IOException("no PowerShell"));

        var results = await queue.ApplyAsync(executor);

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Contains("no PowerShell", results[0].Detail);
        Assert.Single(queue.Pending);
    }

    // ---------- supported size ----------

    [Fact]
    public void SupportedSize_Parse_AcceptsTwoNumbersAndRejectsGarbage()
    {
        Assert.Equal(new SupportedSize(1000, 5000), SupportedSize.Parse("1000 5000\r\n"));
        Assert.Null(SupportedSize.Parse(""));
        Assert.Null(SupportedSize.Parse("abc 5000"));
        Assert.Null(SupportedSize.Parse("5000 1000"));
        Assert.Null(SupportedSize.Parse("1 2 3"));
    }

    [Fact]
    public async Task SupportedSize_Query_UsesExecutorAndHandlesFailure()
    {
        var ok = new FakeExecutor(_ => new CommandResult(0, "2000 9000", ""));
        Assert.Equal(new SupportedSize(2000, 9000), await SupportedSize.QueryAsync('d', ok));
        Assert.Contains("Get-PartitionSupportedSize -DriveLetter D", ok.Scripts[0]);

        var failing = new FakeExecutor(_ => new CommandResult(1, "", "boom"));
        Assert.Null(await SupportedSize.QueryAsync('d', failing));
    }
}
