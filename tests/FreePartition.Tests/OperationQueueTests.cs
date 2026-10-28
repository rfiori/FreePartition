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


    // ---------- initialize disk ----------

    [Fact]
    public void InitializeDisk_GeneratesScript_ThatRefusesInitializedDisks()
    {
        string script = new InitializeDisk(3, DiskPartitionStyle.GPT).GenerateScript();
        Assert.Contains("(Get-Disk -Number 3).PartitionStyle -ne 'RAW'", script);
        Assert.EndsWith("Initialize-Disk -Number 3 -PartitionStyle GPT", script);
        Assert.EndsWith("-PartitionStyle MBR", new InitializeDisk(0, DiskPartitionStyle.MBR).GenerateScript());
    }

    [Fact]
    public void InitializeDisk_InvalidParameters_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => new InitializeDisk(-1, DiskPartitionStyle.GPT).Validate());
        Assert.Throws<ArgumentException>(() => new InitializeDisk(1, (DiskPartitionStyle)99).Validate());
    }

    // ---------- create / delete partition ----------

    [Fact]
    public void CreatePartition_InSelectedGap_UsesOffsetInsideTheGap()
    {
        string script = new CreatePartition(2, FileSystemFormat.NTFS, "p3", 180_000_000, null, 1_000_000_000, 326_000_000).GenerateScript();

        Assert.Contains("$o = 1000000000; $e = 1326000000;", script);
        Assert.Contains("New-Partition -DiskNumber 2 -Offset $off -Size ([math]::Min(180000000, $max));", script);
        Assert.DoesNotContain("-UseMaximumSize", script);
        Assert.Throws<ArgumentException>(() => new CreatePartition(2, FileSystemFormat.NTFS, "", null, null, 1000, null).Validate());
    }

    [Fact]
    public void CreatePartition_GeneratesScript()
    {
        var all = new CreatePartition(1, FileSystemFormat.NTFS, "Data").GenerateScript();
        Assert.Equal("$n = New-Partition -DiskNumber 1 -UseMaximumSize; " +
                     "Format-Volume -Partition $n -FileSystem NTFS -NewFileSystemLabel 'Data' -Confirm:$false; " +
                     "$n = Get-Partition -DiskNumber $n.DiskNumber -PartitionNumber $n.PartitionNumber; " +
                     "Add-PartitionAccessPath -InputObject $n -AssignDriveLetter", all);

        var sized = new CreatePartition(2, FileSystemFormat.exFAT, "", 5_000_000_000, 'm').GenerateScript();
        Assert.Contains("-Size 5000000000;", sized);
        Assert.EndsWith("Add-PartitionAccessPath -InputObject $n -AccessPath 'M:\'", sized);
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

    // ---------- merge / split ----------

    [Fact]
    public void MergePartitions_CopiesBeforeDeleting_AndExtendsTheTarget()
    {
        string script = new MergePartitions('e', 'd').GenerateScript();

        int copy = script.IndexOf("robocopy 'E:\\' $dest", StringComparison.Ordinal);
        int check = script.IndexOf("$LASTEXITCODE -ge 8", StringComparison.Ordinal);
        int delete = script.IndexOf("Remove-Partition -DriveLetter E", StringComparison.Ordinal);
        int extend = script.IndexOf("Resize-Partition -DriveLetter D -Size ([math]::Min($s.Offset + $s.Size - $t.Offset, $max))", StringComparison.Ordinal);

        Assert.Contains("$dest = 'D:\\Merged_E'", script);
        Assert.True(copy >= 0 && copy < check && check < delete && delete < extend);
    }

    [Fact]
    public void MergePartitions_IsRefusedWhenTheSourceIsTheSystemDrive_ButNotTheTarget()
    {
        var queue = new OperationQueue(protectedDrive: 'C');
        Assert.Throws<InvalidOperationException>(() => queue.Add(new MergePartitions('C', 'D')));
        queue.Add(new MergePartitions('E', 'C'));
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void MergePartitions_WithSameOrInvalidLetters_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new MergePartitions('D', 'd').Validate());
        Assert.Throws<ArgumentException>(() => new MergePartitions('1', 'D').Validate());
    }

    [Fact]
    public void SplitPartition_ShrinksThenCreatesAndFormats()
    {
        string script = new SplitPartition('d', 10_000_000_000, FileSystemFormat.NTFS, "Data", 'm').GenerateScript();

        int shrink = script.IndexOf("Resize-Partition -DriveLetter D -Size ([math]::Floor(($p.Size - 10000000000) / 1MB) * 1MB)", StringComparison.Ordinal);
        int create = script.IndexOf("New-Partition -DiskNumber $k.DiskNumber -Offset $off -Size $size;", StringComparison.Ordinal);
        int format = script.IndexOf("Format-Volume -Partition $n -FileSystem NTFS -NewFileSystemLabel 'Data'", StringComparison.Ordinal);

        Assert.True(shrink >= 0 && shrink < create && create < format);
    }

    [Fact]
    public void SplitPartition_InvalidParameters_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => new SplitPartition('D', 1000, FileSystemFormat.NTFS).Validate());
        Assert.Throws<ArgumentException>(() => new SplitPartition('D', 1L << 30, FileSystemFormat.NTFS, "", 'd').Validate());
        Assert.Throws<ArgumentException>(() => new SplitPartition('D', 1L << 30, FileSystemFormat.NTFS, "x'; calc; '").Validate());
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
