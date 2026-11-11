using FreePartition.Core.Cloning;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class WindowsCloneTests
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;

    // ---------- WinPeDetector ----------

    [Theory]
    [InlineData(true, @"C:\Windows\System32", true)]    // the MiniNT key alone is enough
    [InlineData(false, @"X:\Windows\System32", true)]   // the RAM-disk system drive
    [InlineData(false, @"x:\windows\system32", true)]
    [InlineData(false, @"C:\Windows\System32", false)]  // an installed Windows
    [InlineData(false, @"X:\Other", false)]
    [InlineData(false, "", false)]
    public void IsWinPe_UsesTheRegistryKeyOrTheSystemDrive(bool miniNt, string systemDirectory, bool expected) =>
        Assert.Equal(expected, WinPeDetector.IsWinPe(miniNt, systemDirectory));

    // ---------- Disk builders ----------

    private static PartitionInfo Part(int number, long offset, long size, string? letter, params string[] details) =>
        new(number - 1, "GPT: Basic Data", (ulong)offset, (ulong)size, false,
            letter is null ? [] : [new LogicalVolumeInfo(letter + ":", null, "NTFS", (ulong)size, 0)],
            number, details);

    private static DiskInfo GptDisk(int index = 0, long size = 500 * GiB, bool msr = true) => new(
        index, "Source disk", (ulong)size, null, null, null, "GPT",
        [
            Part(1, 1 * MiB, 100 * MiB, null, "EFI System Partition", "Hidden"),
            Part(2, 117 * MiB, 100 * GiB, "C", "Boot", "Page File", "Basic Data Partition"),
            Part(3, 117 * MiB + 100 * GiB, 900 * MiB, null, "Recovery Partition", "Hidden"),
            Part(4, 117 * MiB + 100 * GiB + 900 * MiB, 50 * GiB, "D", "Basic Data Partition")
        ],
        Reserved: msr ? [new ReservedRange((ulong)(101 * MiB), (ulong)(16 * MiB))] : null);

    private static DiskInfo MbrDisk(int index = 0, string? bootLetter = null) => new(
        index, "Old disk", (ulong)(120 * GiB), null, null, null, "MBR",
        [
            Part(1, 1 * MiB, 100 * MiB, bootLetter, "Active", "Primary Partition"),
            Part(2, 101 * MiB, 60 * GiB, "C", "Boot", "Primary Partition")
        ]);

    private static DiskInfo EmptyDisk(int index, long size) =>
        new(index, "Target disk", (ulong)size, null, null, null, "Uninitialized", []);

    private static bool HasHive(string path) => path.StartsWith("C:", StringComparison.OrdinalIgnoreCase);

    // ---------- WindowsInstallFinder ----------

    [Fact]
    public void Find_ReturnsTheWindowsPartitionWithItsBootAndRecoveryPartitions()
    {
        var found = Assert.Single(WindowsInstallFinder.Find([GptDisk()], 'X', HasHive));

        Assert.Equal('C', found.Letter);
        Assert.True(found.Uefi);
        Assert.True(found.HasMsr);
        Assert.Equal(1 * MiB, (long)found.Boot!.Offset);
        Assert.Equal(900 * MiB, (long)found.Recovery!.Size);
    }

    [Fact]
    public void Find_OnAnMbrDisk_UsesTheActivePartitionAsBootAndHasNoRecovery()
    {
        var found = Assert.Single(WindowsInstallFinder.Find([MbrDisk()], 'X', HasHive));

        Assert.False(found.Uefi);
        Assert.Equal(1 * MiB, (long)found.Boot!.Offset);
        Assert.Null(found.Recovery);
    }

    [Fact]
    public void Find_IgnoresTheDriveTheAppRunsFrom()
    {
        Assert.Empty(WindowsInstallFinder.Find([GptDisk()], 'C', HasHive));
    }

    [Fact]
    public void Find_IgnoresPartitionsWithoutTheRegistryHive()
    {
        Assert.Empty(WindowsInstallFinder.Find([GptDisk()], 'X', _ => false));
    }

    // ---------- WindowsCloneLayout ----------

    [Fact]
    public void Layout_PlacesBootMsrWindowsAndRecoveryInOrderOnMibBoundaries()
    {
        var source = Assert.Single(WindowsInstallFinder.Find([GptDisk()], 'X', HasHive));
        var plan = WindowsCloneLayout.Compute(source);

        Assert.True(plan.Uefi);
        Assert.Equal(1 * MiB, plan.BootOffset);
        Assert.Equal(100 * MiB, plan.BootSize);
        Assert.Equal(101 * MiB, plan.MsrOffset);
        Assert.Equal(117 * MiB, plan.WindowsOffset);
        Assert.Equal(100 * GiB, plan.WindowsSize);
        Assert.Equal(117 * MiB + 100 * GiB, plan.RecoveryOffset);
        Assert.Equal(900 * MiB, plan.RecoverySize);
        Assert.Equal(117 * MiB + 100 * GiB + 900 * MiB + MiB, plan.RequiredBytes);
        Assert.All([plan.BootOffset, plan.WindowsOffset, plan.RecoveryOffset!.Value], o => Assert.Equal(0, o % MiB));
    }

    [Fact]
    public void Layout_WithoutAnMsr_SkipsIt()
    {
        var source = Assert.Single(WindowsInstallFinder.Find([GptDisk(msr: false)], 'X', HasHive));
        var plan = WindowsCloneLayout.Compute(source);

        Assert.Null(plan.MsrOffset);
        Assert.Equal(101 * MiB, plan.WindowsOffset);
    }

    [Fact]
    public void Layout_FitsOnlyOnADiskLargeEnough()
    {
        var plan = WindowsCloneLayout.Compute(Assert.Single(WindowsInstallFinder.Find([GptDisk()], 'X', HasHive)));

        Assert.True(plan.FitsOn(101 * GiB + 200 * MiB));
        Assert.False(plan.FitsOn(100 * GiB));
        Assert.True(plan.ShortfallOn(100 * GiB) > 0);
        Assert.Equal(0, plan.ShortfallOn(500 * GiB));
    }

    // ---------- Letters and scripts ----------

    [Fact]
    public void PickLetters_TakesFourFreeLettersFromZDownwards()
    {
        Assert.Equal(['Z', 'X', 'W', 'V', 'U'], WindowsCloneWorkflow.PickLetters(['C', 'D', 'Y'], 'C')!);
        Assert.Null(WindowsCloneWorkflow.PickLetters(Enumerable.Range('D', 23).Select(i => (char)i).ToList(), 'C'));
    }

    [Fact]
    public void DiskPartScript_EscapesQuotesAndFailsOnDiskPartErrors()
    {
        string script = WindowsCloneWorkflow.DiskPartScript("select disk 1", "format quick fs=ntfs label=\"It's\"");

        Assert.Contains("'format quick fs=ntfs label=\"It''s\"'", script);
        Assert.Contains("DiskPart has encountered an error", script);
        Assert.Contains("$LASTEXITCODE", script);
    }

    // ---------- Workflow ----------

    private sealed class FakeExecutor(Func<string, CommandResult>? response = null) : ICommandExecutor
    {
        public List<string> Scripts { get; } = [];

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            return Task.FromResult(response?.Invoke(script) ?? new CommandResult(0, "", ""));
        }
    }

    private sealed class FakeCloner : IPartitionCloner
    {
        public List<PartitionCloneRequest> Requests { get; } = [];

        public void Clone(PartitionCloneRequest request, IProgress<string>? progress, CancellationToken ct) =>
            Requests.Add(request);
    }

    private static WindowsInstall Source(DiskInfo disk) =>
        Assert.Single(WindowsInstallFinder.Find([disk], 'X', HasHive));

    private static async Task<(WindowsCloneResult Result, FakeExecutor Executor, FakeCloner Cloner, Dictionary<int, CloneStepState> Steps)> Run(
        DiskInfo source, DiskInfo destination, Func<string, CommandResult>? response = null)
    {
        var executor = new FakeExecutor(response);
        var cloner = new FakeCloner();
        var steps = new Dictionary<int, CloneStepState>();
        var workflow = new WindowsCloneWorkflow(executor, cloner, () => ['C', 'D']);
        var result = await workflow.RunAsync(Source(source), destination, (i, s) => steps[i] = s, null, CancellationToken.None);
        return (result, executor, cloner, steps);
    }

    [Fact]
    public async Task Run_Uefi_ErasesCreatesBootCopiesWindowsAndRecoveryThenWritesTheBootFiles()
    {
        var (result, executor, cloner, steps) = await Run(GptDisk(), EmptyDisk(1, 500 * GiB));

        Assert.True(result.Success, result.Message);
        Assert.Empty(result.Warnings);
        Assert.All(steps.Values, s => Assert.Equal(CloneStepState.Done, s));
        Assert.Equal(WindowsCloneWorkflow.StepTitles.Count, steps.Count);

        // The raw copy: the source Windows partition into the planned offset of disk 1.
        var copy = Assert.Single(cloner.Requests);
        Assert.Equal((0, 2, 'C'), (copy.SourceDisk, copy.SourcePartition, copy.Drive));
        Assert.Equal((1, 117 * MiB, 100 * GiB), (copy.DestinationDisk, copy.DestinationOffset, copy.Length));

        string all = string.Join("\n", executor.Scripts);
        Assert.Contains("select disk 1", all);
        Assert.Contains("'clean'", all);
        Assert.Contains("'convert gpt'", all);
        Assert.Contains($"create partition efi offset={MiB / 1024} size=100", all);
        Assert.Contains("fs=fat32", all);
        Assert.Contains("create partition msr", all);
        Assert.Contains("de94bba4-06d1-4d40-a16a-bfd50179d6ac", all);
        Assert.Contains("robocopy.exe", all);
        // Free letters are taken from Z down: boot Z:, the copy of Windows Y:.
        Assert.Contains(@"bcdboot.exe 'Y:\Windows' /s 'Z:' /f UEFI", all);

        // The order matters: erase first, boot files last.
        int clean = executor.Scripts.FindIndex(s => s.Contains("'clean'"));
        int bcd = executor.Scripts.FindIndex(s => s.Contains("bcdboot.exe"));
        int verify = executor.Scripts.FindIndex(s => s.Contains("bootmgfw.efi"));
        Assert.True(clean >= 0 && clean < bcd && bcd < verify);
    }

    [Fact]
    public async Task Run_UsesTheLetterWinPEGaveTheCopyOfWindows()
    {
        // WinPE mounted the copy as F: before the clone could give it Y:.
        var (result, executor, _, _) = await Run(MbrDisk(), EmptyDisk(1, 200 * GiB),
            script => script.Contains("FP-LETTER") ? new CommandResult(0, "FP-LETTER=F", "") : new CommandResult(0, "", ""));

        Assert.True(result.Success, result.Message);
        string all = string.Join("\n", executor.Scripts);
        Assert.Contains(@"bcdboot.exe 'F:\Windows' /s 'Z:' /f BIOS", all);
        Assert.DoesNotContain(@"'Y:\Windows'", all);
    }

    [Fact]
    public async Task Run_WhenTheCopyCannotBeFound_FailsTheWindowsStep()
    {
        var (result, executor, _, steps) = await Run(MbrDisk(), EmptyDisk(1, 200 * GiB),
            script => script.Contains("FP-LETTER") ? new CommandResult(1, "", "The copied Windows partition was not found.") : new CommandResult(0, "", ""));

        Assert.False(result.Success);
        Assert.Equal(CloneStepState.Failed, steps[3]);
        Assert.DoesNotContain(executor.Scripts, s => s.Contains("bcdboot.exe"));
    }

    [Fact]
    public void WindowsLetterScript_FindsThePartitionByOffsetAndOnlyAddsTheLetterWhenThereIsNone()
    {
        string script = WindowsCloneWorkflow.WindowsLetterScript(1, 105906176, 'Y');

        Assert.Contains("Get-Partition -DiskNumber 1", script);
        Assert.Contains("$_.Offset -eq $o", script);
        Assert.Contains("$o = 105906176", script);
        Assert.Contains("if (-not $a) { Add-PartitionAccessPath -InputObject $p -AccessPath 'Y:\\'", script);
        Assert.Contains("FP-LETTER=", script);
    }

    [Fact]
    public async Task Run_Bios_UsesAnActiveSystemReservedPartitionAndBiosBootFiles()
    {
        var (result, executor, cloner, steps) = await Run(MbrDisk(), EmptyDisk(1, 200 * GiB));

        Assert.True(result.Success, result.Message);
        Assert.Equal(CloneStepState.Skipped, steps[4]);   // MBR source: no recovery partition
        Assert.Single(cloner.Requests);

        string all = string.Join("\n", executor.Scripts);
        Assert.Contains("'convert mbr'", all);
        Assert.Contains("create partition primary", all);
        Assert.Contains("'active'", all);
        Assert.Contains("/f BIOS", all);
        Assert.DoesNotContain("/f UEFI", all);
        Assert.DoesNotContain("create partition efi", all);
    }

    [Fact]
    public async Task Run_WhenTheDiskCannotBePrepared_StopsBeforeCopyingAnything()
    {
        var (result, _, cloner, steps) = await Run(GptDisk(), EmptyDisk(1, 500 * GiB),
            script => script.Contains("'clean'") ? new CommandResult(1, "", "DiskPart reported an error.") : new CommandResult(0, "", ""));

        Assert.False(result.Success);
        Assert.Contains("DiskPart reported an error", result.Message);
        Assert.Equal(CloneStepState.Failed, steps[1]);
        Assert.Empty(cloner.Requests);
    }

    [Fact]
    public async Task Run_WhenTheRecoveryCopyFails_StillSucceedsWithAWarning()
    {
        var (result, executor, _, steps) = await Run(GptDisk(), EmptyDisk(1, 500 * GiB),
            script => script.Contains("robocopy.exe") ? new CommandResult(1, "", "robocopy failed (16).") : new CommandResult(0, "", ""));

        Assert.True(result.Success);
        Assert.Single(result.Warnings);
        Assert.Contains("recovery", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CloneStepState.Skipped, steps[4]);
        Assert.Contains(executor.Scripts, s => s.Contains("bcdboot.exe"));
    }

    [Fact]
    public async Task Run_WhenReagentcIsMissing_CountsTheRecoveryAsCopiedAndTellsHowToEnableIt()
    {
        // The real case: WinPE has no reagentc.exe, but the files were copied.
        var (result, _, _, steps) = await Run(GptDisk(), EmptyDisk(1, 500 * GiB),
            script => script.Contains("robocopy.exe") ? new CommandResult(0, "FP-REAGENTC=missing", "") : new CommandResult(0, "", ""));

        Assert.True(result.Success, result.Message);
        Assert.Equal(CloneStepState.Done, steps[4]);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("was copied", warning);
        Assert.Contains("reagentc /enable", warning);
        Assert.DoesNotContain("Without the recovery partition", result.Message);
    }

    [Fact]
    public async Task Run_WhenReagentcRegistersTheRecovery_HasNoWarning()
    {
        var (result, _, _, steps) = await Run(GptDisk(), EmptyDisk(1, 500 * GiB),
            script => script.Contains("robocopy.exe") ? new CommandResult(0, "REAGENTC.EXE: Operation Successful.\nFP-REAGENTC=ok", "") : new CommandResult(0, "", ""));

        Assert.True(result.Success, result.Message);
        Assert.Equal(CloneStepState.Done, steps[4]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RegisterRecoveryScript_FallsBackToTheReagentcOfTheCopiedWindowsAndNeverThrows()
    {
        string script = WindowsCloneWorkflow.RegisterRecoveryScript('W', 'F');

        Assert.Contains(@"$wim = 'W:\Recovery\WindowsRE'", script);
        Assert.Contains("Get-Command reagentc.exe -ErrorAction SilentlyContinue", script);
        Assert.Contains(@"'F:\Windows\System32\reagentc.exe'", script);
        Assert.Contains(@"/setreimage /path $wim /target 'F:\Windows'", script);
        Assert.Contains("FP-REAGENTC=missing", script);
        Assert.Contains("catch {", script);
        Assert.DoesNotContain("throw", script);
        Assert.EndsWith("exit 0", script);
    }

    [Fact]
    public async Task Run_WhenBcdbootFails_ReportsTheFailureOnThatStep()
    {
        var (result, _, _, steps) = await Run(GptDisk(), EmptyDisk(1, 500 * GiB),
            script => script.Contains("bcdboot.exe") || script.Contains("bcdedit.exe")
                ? new CommandResult(1, "", "bcdboot failed (1).")
                : new CommandResult(0, "", ""));

        Assert.False(result.Success);
        Assert.Equal(CloneStepState.Failed, steps[5]);
        Assert.False(steps.ContainsKey(6));
    }

    [Fact]
    public async Task Run_WhenBcdbootFails_CopiesTheBootFilesOfTheSourceAndRepointsTheStore()
    {
        var (result, executor, _, steps) = await Run(MbrDisk(), EmptyDisk(1, 200 * GiB),
            script => script.Contains("bcdboot.exe")
                ? new CommandResult(1, "Failure when attempting to copy boot files.", "bcdboot failed (193).")
                : new CommandResult(0, "", ""));

        Assert.True(result.Success, result.Message);
        Assert.Equal(CloneStepState.Done, steps[5]);
        Assert.Contains("bcdboot", Assert.Single(result.Warnings));

        // The source's boot partition (disk 0, partition 1) goes to Z:, and the store points to Z: and to the copy of Windows (Y:).
        string fallback = Assert.Single(executor.Scripts, s => s.Contains("-PartitionNumber 1 -AccessPath $src") && s.Contains("bcdedit.exe"));
        Assert.Contains(@"$store = 'Z:\Boot\BCD'", fallback);
        Assert.Contains("partition=Z:", fallback);
        Assert.Contains("partition=Y:", fallback);
    }

    [Fact]
    public async Task Run_Fallback_UsesTheLetterTheSourceBootPartitionAlreadyHas()
    {
        // Windows 7 gives the System Reserved partition a letter in WinPE: adding a second one fails.
        var (result, executor, _, _) = await Run(MbrDisk(bootLetter: "E"), EmptyDisk(1, 200 * GiB),
            script => script.Contains("bcdboot.exe") ? new CommandResult(1, "", "bcdboot failed (193).") : new CommandResult(0, "", ""));

        Assert.True(result.Success, result.Message);
        string fallback = Assert.Single(executor.Scripts, s => s.Contains("$store = ") && s.Contains("robocopy.exe"));
        Assert.Contains(@"$src = 'E:\'", fallback);
        Assert.DoesNotContain("Add-PartitionAccessPath", fallback);
        Assert.DoesNotContain("Remove-PartitionAccessPath", fallback);
    }

    [Fact]
    public async Task Run_WhenBcdbootAndTheFallbackBothFail_FailsTheStep()
    {
        var (result, _, _, steps) = await Run(MbrDisk(), EmptyDisk(1, 200 * GiB),
            script => script.Contains("bcdboot.exe") || script.Contains("bcdedit.exe")
                ? new CommandResult(1, "", "boom")
                : new CommandResult(0, "", ""));

        Assert.False(result.Success);
        Assert.Equal(CloneStepState.Failed, steps[5]);
        Assert.Contains("failed too", result.Message);
    }

    [Fact]
    public async Task Run_RefusesTheSourceDiskAndADiskThatIsTooSmall()
    {
        var same = await Run(GptDisk(), GptDisk(0));
        Assert.False(same.Result.Success);
        Assert.Empty(same.Executor.Scripts);

        var small = await Run(GptDisk(), EmptyDisk(1, 50 * GiB));
        Assert.False(small.Result.Success);
        Assert.Contains("too small", small.Result.Message);
        Assert.Empty(small.Executor.Scripts);
        Assert.Empty(small.Cloner.Requests);
    }
}
