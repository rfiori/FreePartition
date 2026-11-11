using System.Text.RegularExpressions;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;
using FreePartition.Core.Util;

namespace FreePartition.Core.Cloning;

public enum CloneStepState { Pending, Running, Done, Skipped, Failed }

public sealed record WindowsCloneResult(bool Success, string Message, IReadOnlyList<string> Warnings);

/// <summary>
/// "Clone Windows to another disk", run from WinPE where the source Windows is not in use. Everything the user would do
/// by hand, in order: erase and initialize the destination, create the boot partition, copy the Windows partition
/// sector by sector, copy the recovery partition, write the boot files with bcdboot and check that they are there.
/// The source disk is only read. The destination is erased, so the caller must have asked the user first.
/// </summary>
public sealed partial class WindowsCloneWorkflow(
    ICommandExecutor executor, IPartitionCloner? cloner = null, Func<IReadOnlyCollection<char>>? usedLetters = null)
{
    public static readonly IReadOnlyList<string> StepTitles =
    [
        "Check the disks",
        "Erase and initialize the destination disk",
        "Create the boot partition",
        "Copy the Windows partition",
        "Copy the recovery partition",
        "Write the boot files (bcdboot)",
        "Verify the result"
    ];

    private const int StepCheck = 0, StepDisk = 1, StepBoot = 2, StepWindows = 3, StepRecovery = 4, StepBcd = 5, StepVerify = 6;

    private const string RecoveryGptType = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";

    /// <summary>Why the destination cannot be used, or null when it can. Nothing is read or written.</summary>
    public static string? CheckDestination(WindowsInstall source, DiskInfo destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Index == source.Disk.Index)
            return "The destination must be a different disk than the one Windows is on.";

        var plan = WindowsCloneLayout.Compute(source);
        if (!plan.FitsOn((long)destination.Size))
            return $"Disk {destination.Index} is too small: the copy needs {ByteFormatter.Format(plan.RequiredBytes)} and the disk has " +
                   $"{ByteFormatter.Format((long)destination.Size)}. Shrink the Windows partition in the main window first (Resize), " +
                   "apply it, then open this window again.";
        return null;
    }

    public async Task<WindowsCloneResult> RunAsync(
        WindowsInstall source, DiskInfo destination,
        Action<int, CloneStepState>? stepChanged, IProgress<string>? log, CancellationToken ct)
    {
        var warnings = new List<string>();
        int current = StepCheck;

        void Step(int index, CloneStepState state)
        {
            current = index;
            stepChanged?.Invoke(index, state);
        }

        try
        {
            // 1. Check
            Step(StepCheck, CloneStepState.Running);
            if (CheckDestination(source, destination) is { } problem)
                return Failed(StepCheck, problem);
            var plan = WindowsCloneLayout.Compute(source);
            var letters = PickLetters(usedLetters?.Invoke() ?? DefaultUsedLetters(), source.Letter);
            if (letters is null)
                return Failed(StepCheck, "There are not enough free drive letters.");
            char bootLetter = letters[0], windowsLetter = letters[1], recoveryLetter = letters[2], sourceRecoveryLetter = letters[3],
                 sourceBootLetter = letters[4];
            log?.Report($"Source: {source.Description}.");
            log?.Report($"Destination: disk {destination.Index} ({destination.Model}, {ByteFormatter.Format((long)destination.Size)}). " +
                        $"Layout: {(plan.Uefi ? "GPT / UEFI" : "MBR / BIOS")}.");
            Step(StepCheck, CloneStepState.Done);

            // 2. Erase and initialize
            ct.ThrowIfCancellationRequested();
            Step(StepDisk, CloneStepState.Running);
            log?.Report($"Erasing disk {destination.Index} and initializing it as {(plan.Uefi ? "GPT" : "MBR")}...");
            if (await RunScript(DiskPartScript(
                    $"select disk {destination.Index}", "clean", plan.Uefi ? "convert gpt" : "convert mbr"), log, ct) is { } diskError)
                return Failed(StepDisk, diskError);
            Step(StepDisk, CloneStepState.Done);

            // 3. Boot partition (and MSR)
            ct.ThrowIfCancellationRequested();
            Step(StepBoot, CloneStepState.Running);
            log?.Report(plan.Uefi ? "Creating the EFI system partition..." : "Creating the System Reserved partition...");
            var bootLines = new List<string> { $"select disk {destination.Index}" };
            if (plan.Uefi)
            {
                bootLines.Add($"create partition efi offset={plan.BootOffset / 1024} size={plan.BootSize >> 20}");
                bootLines.Add("format quick fs=fat32 label=\"System\"");
                bootLines.Add($"assign letter={bootLetter}");
                if (plan.MsrOffset is { } msrOffset)
                    bootLines.Add($"create partition msr offset={msrOffset / 1024} size={plan.MsrSize >> 20}");
            }
            else
            {
                bootLines.Add($"create partition primary offset={plan.BootOffset / 1024} size={plan.BootSize >> 20}");
                bootLines.Add("format quick fs=ntfs label=\"System Reserved\"");
                bootLines.Add("active");
                bootLines.Add($"assign letter={bootLetter}");
            }
            if (await RunScript(DiskPartScript([.. bootLines]), log, ct) is { } bootError)
                return Failed(StepBoot, bootError);
            Step(StepBoot, CloneStepState.Done);

            // 4. Windows partition: the same raw copy as "Clone partition"
            ct.ThrowIfCancellationRequested();
            Step(StepWindows, CloneStepState.Running);
            log?.Report($"Copying {source.Letter}: sector by sector ({ByteFormatter.Format(plan.WindowsSize)}). This takes a while.");
            var clone = new ClonePartition(source.Disk.Index, source.Windows.Number, source.Letter, (long)source.Windows.Offset,
                plan.WindowsSize, destination.Index, plan.WindowsOffset, FileSystemFormat.NTFS, windowsLetter, cloner);
            var cloneResult = await clone.RunAsync(executor, clone.GenerateScript(), log, ct);
            if (!cloneResult.Success)
                return Failed(StepWindows, Describe(cloneResult));

            // WinPE gives a new volume a letter of its own as soon as it appears, and the clone only adds the planned one
            // when there is none. Ask which letter the copy really has: every later step needs the right one.
            var letterResult = await executor.ExecuteAsync(WindowsLetterScript(destination.Index, plan.WindowsOffset, windowsLetter), ct);
            if (!letterResult.Success)
                return Failed(StepWindows, Describe(letterResult));
            if (LetterRegex().Match(letterResult.Output) is { Success: true } found)
            {
                windowsLetter = char.ToUpperInvariant(found.Groups[1].Value[0]);
                log?.Report($"The copy of Windows is on {windowsLetter}:.");
            }
            Step(StepWindows, CloneStepState.Done);

            // 5. Recovery partition (best effort: Windows runs without it)
            ct.ThrowIfCancellationRequested();
            bool recoveryCopied = false;
            if (plan.RecoveryOffset is { } recoveryOffset && source.Recovery is { } sourceRecovery)
            {
                Step(StepRecovery, CloneStepState.Running);
                log?.Report("Copying the recovery partition...");
                var (recoveryError, recoveryWarning) = await CopyRecoveryAsync(source, sourceRecovery, destination, plan,
                    recoveryOffset, recoveryLetter, sourceRecoveryLetter, windowsLetter, log, ct);
                if (recoveryError is null)
                {
                    recoveryCopied = true;
                    if (recoveryWarning is not null)
                    {
                        warnings.Add(recoveryWarning);
                        log?.Report("Warning: " + recoveryWarning);
                    }
                    Step(StepRecovery, CloneStepState.Done);
                }
                else
                {
                    warnings.Add("The recovery partition was not copied: " + recoveryError +
                                 " Windows starts without it; the \"Reset this PC\" and repair options will not work until it is recreated.");
                    log?.Report("Warning: " + warnings[^1]);
                    Step(StepRecovery, CloneStepState.Skipped);
                }
            }
            else
            {
                Step(StepRecovery, CloneStepState.Skipped);
                log?.Report("The source has no recovery partition: skipped.");
            }

            // 6. Boot files
            ct.ThrowIfCancellationRequested();
            Step(StepBcd, CloneStepState.Running);
            log?.Report("Writing the boot files...");
            if (await RunScript(BcdBootScript(windowsLetter, bootLetter, plan.Uefi), log, ct) is { } bcdError)
            {
                // bcdboot can refuse a Windows it does not match (another architecture or an old version, such as
                // Windows 7 from a recent WinPE). The source has its own boot files: copy them and repoint the boot store.
                if (source.Boot is not { } sourceBoot)
                    return Failed(StepBcd, bcdError + " The source has no boot partition to copy the boot files from.");

                log?.Report("bcdboot failed; copying the boot files of the source instead...");
                string? fallbackError = await RunScript(CopyBootFilesScript(source.Disk.Index, sourceBoot,
                    sourceBootLetter, windowsLetter, bootLetter, plan.Uefi), log, ct);
                if (fallbackError is not null)
                    return Failed(StepBcd, bcdError + "\nCopying the source's boot files failed too: " + fallbackError);

                warnings.Add("bcdboot could not write the boot files (" + FirstLine(bcdError) + "). The boot files of the source " +
                             "were copied and its boot store was repointed to the new disk instead.");
                log?.Report("Warning: " + warnings[^1]);
            }
            Step(StepBcd, CloneStepState.Done);

            // 7. Verify
            Step(StepVerify, CloneStepState.Running);
            if (await RunScript(VerifyFilesScript(bootLetter, plan.Uefi), log, ct) is { } verifyError)
                return Failed(StepVerify, verifyError);
            var entry = await executor.ExecuteAsync(VerifyEntryScript(windowsLetter, bootLetter, plan.Uefi), ct);
            if (!entry.Success)
            {
                warnings.Add("The boot entry could not be read back to confirm it points to the copy. " + Describe(entry));
                log?.Report("Warning: " + warnings[^1]);
            }
            else if (!string.IsNullOrWhiteSpace(entry.Output)) log?.Report(entry.Output.Trim());
            Step(StepVerify, CloneStepState.Done);

            string done = $"Windows was copied to disk {destination.Index}." +
                          (recoveryCopied ? "" : " (Without the recovery partition.)") +
                          " Shut down, remove or disconnect the old disk if you want to test the copy, and start from the new one." +
                          (plan.Uefi ? " The computer must boot in UEFI mode." : " The computer must boot in BIOS (legacy) mode.");
            return new WindowsCloneResult(true, done, warnings);
        }
        catch (OperationCanceledException)
        {
            return Failed(current, "Canceled. The destination disk was erased and is incomplete; run the clone again before using it.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            return Failed(current, ex.Message);
        }

        WindowsCloneResult Failed(int step, string message)
        {
            stepChanged?.Invoke(step, CloneStepState.Failed);
            log?.Report("Failed: " + message);
            return new WindowsCloneResult(false, message, warnings);
        }
    }

    // ---------------------------------------------------------------- Recovery

    /// <summary>
    /// Error is null when the recovery partition was created and copied (otherwise the reason it was not); Warning says
    /// when it was copied but the copied Windows could not be pointed to it.
    /// </summary>
    private async Task<(string? Error, string? Warning)> CopyRecoveryAsync(
        WindowsInstall source, PartitionInfo sourceRecovery, DiskInfo destination, WindowsClonePlan plan, long offset,
        char recoveryLetter, char sourceLetter, char windowsLetter, IProgress<string>? log, CancellationToken ct)
    {
        var lines = new List<string>
        {
            $"select disk {destination.Index}",
            $"create partition primary offset={offset / 1024} size={plan.RecoverySize >> 20}",
            "format quick fs=ntfs label=\"Recovery\"",
            $"assign letter={recoveryLetter}"
        };
        if (plan.Uefi)
        {
            lines.Add($"set id=\"{RecoveryGptType}\"");
            lines.Add("gpt attributes=0x8000000000000001");
        }
        else
        {
            lines.Add("set id=27");
        }
        if (await RunScript(DiskPartScript([.. lines]), log, ct) is { } createError) return (createError, null);

        // The source recovery partition usually has no letter: give it one while it is copied, then take it away again.
        var access = SourceAccess(source.Disk.Index, sourceRecovery, sourceLetter);
        string script =
            $"$src = '{access.Letter}:\\'; $dst = '{recoveryLetter}:\\'; " +
            access.Add +
            "try { " +
            "$out = & robocopy.exe $src $dst /E /COPYALL /DCOPY:DAT /XD 'System Volume Information' /R:1 /W:1 /NFL /NDL /NJH /NP 2>&1 | Out-String; " +
            "$rc = $LASTEXITCODE; Write-Output $out; if ($rc -ge 8) { throw \"robocopy failed ($rc).\" } " +
            "} finally { " + access.Remove + " }; " +
            RegisterRecoveryScript(recoveryLetter, windowsLetter);
        var result = await executor.ExecuteAsync(script, ct);
        if (!result.Success) return (Describe(result), null);
        if (!string.IsNullOrWhiteSpace(result.Output)) log?.Report(result.Output.Trim());

        // The files are on the new partition either way; only the registration in the copied Windows may be missing.
        var registration = RegistrationRegex().Match(result.Output);
        if (!registration.Success || registration.Groups[1].Value == "ok") return (null, null);
        string why = registration.Groups[1].Value == "missing"
            ? "reagentc.exe is not available in this WinPE"
            : "reagentc.exe could not register it";
        return (null, $"The recovery partition was copied, but it could not be registered in the copied Windows ({why}). " +
                      "After starting from the new disk, open a Command Prompt as administrator and run: reagentc /enable");
    }

    [GeneratedRegex(@"FP-REAGENTC=(ok|missing|failed)")]
    private static partial Regex RegistrationRegex();

    /// <summary>
    /// Points the copied Windows' recovery settings to the new recovery partition (offline). WinPE usually has no
    /// reagentc.exe, so the one of the copied Windows is used instead. Never fails: it prints <c>FP-REAGENTC=ok</c>,
    /// <c>missing</c> or <c>failed</c> (nothing when the partition has no Winre.wim).
    /// </summary>
    public static string RegisterRecoveryScript(char recovery, char windows) =>
        $"$wim = '{recovery}:\\Recovery\\WindowsRE'; " +
        "if (Test-Path -LiteralPath (Join-Path $wim 'Winre.wim')) { " +
        "$re = (Get-Command reagentc.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source; " +
        $"if (-not $re) {{ $c = '{windows}:\\Windows\\System32\\reagentc.exe'; if (Test-Path -LiteralPath $c) {{ $re = $c }} }}; " +
        "if (-not $re) { Write-Output 'FP-REAGENTC=missing' } else { " +
        $"try {{ $o = & $re /setreimage /path $wim /target '{windows}:\\Windows' 2>&1 | Out-String; Write-Output $o; " +
        "if ($LASTEXITCODE -eq 0) { Write-Output 'FP-REAGENTC=ok' } else { Write-Output 'FP-REAGENTC=failed' } } " +
        "catch { Write-Output $_.Exception.Message; Write-Output 'FP-REAGENTC=failed' } } }; exit 0";

    // ---------------------------------------------------------------- Scripts

    /// <summary>A PowerShell script that feeds the given lines to diskpart and fails when diskpart reports an error.</summary>
    public static string DiskPartScript(params string[] lines)
    {
        string quoted = string.Join(",", lines.Select(l => "'" + l.Replace("'", "''") + "'"));
        return "$f = Join-Path $env:TEMP 'fp-clone-diskpart.txt'; " +
               $"Set-Content -LiteralPath $f -Value @({quoted}) -Encoding ASCII; " +
               "$out = (& diskpart.exe /s $f 2>&1 | Out-String); $rc = $LASTEXITCODE; Write-Output $out; " +
               "Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue; " +
               "if ($rc -ne 0 -or $out -match 'DiskPart has encountered an error|Virtual Disk Service error') " +
               "{ throw 'DiskPart reported an error.' }; exit 0";
    }

    [GeneratedRegex(@"FP-LETTER=([A-Za-z])")]
    private static partial Regex LetterRegex();

    /// <summary>
    /// Finds the partition that the clone wrote (by its offset) and prints the drive letter it has, giving it
    /// <paramref name="preferred"/> when it has none: <c>FP-LETTER=X</c>.
    /// </summary>
    public static string WindowsLetterScript(int disk, long offset, char preferred) =>
        $"Update-Disk -Number {disk}; $o = {offset}; " +
        $"$p = Get-Partition -DiskNumber {disk} | Where-Object {{ $_.Offset -eq $o }}; " +
        "if (-not $p) { throw 'The copied Windows partition was not found on the destination disk.' }; " +
        "$a = $p.AccessPaths | Where-Object { $_ -match '^[A-Za-z]:\\\\' } | Select-Object -First 1; " +
        $"if (-not $a) {{ Add-PartitionAccessPath -InputObject $p -AccessPath '{preferred}:\\'; $a = '{preferred}:\\' }}; " +
        "Write-Output ('FP-LETTER=' + $a.Substring(0, 1)); exit 0";

    internal static string BcdBootScript(char windows, char boot, bool uefi) =>
        // The copy must be where the letter says (a missing drive would only show up as a confusing bcdboot error).
        $"if (-not (Test-Path -LiteralPath '{windows}:\\Windows\\System32')) {{ throw 'The copied Windows was not found at {windows}:\\Windows.' }}; " +
        // A hibernated copy (Fast Startup) would resume the old session on the new disk: delete the file so it starts clean.
        $"Remove-Item -LiteralPath '{windows}:\\hiberfil.sys' -Force -ErrorAction SilentlyContinue; " +
        // Helps to understand a bcdboot failure: both sides need the same architecture.
        $"Write-Output (\"WinPE: $env:PROCESSOR_ARCHITECTURE; copied Windows: \" + $(if (Test-Path -LiteralPath '{windows}:\\Windows\\SysWOW64') {{ '64-bit' }} else {{ '32-bit' }})); " +
        $"$out = & bcdboot.exe '{windows}:\\Windows' /s '{boot}:' /f {(uefi ? "UEFI" : "BIOS")} 2>&1 | Out-String; " +
        "$rc = $LASTEXITCODE; Write-Output $out; if ($rc -ne 0) { throw \"bcdboot failed ($rc).\" }; exit 0";

    /// <summary>
    /// Plan B for the boot files: copies everything on the source's boot partition (bootmgr, the boot store, the EFI folder)
    /// to the new boot partition and points the boot store to the new partitions. The new boot partition was just formatted,
    /// so it already has the boot code that loads bootmgr, and the MBR has its own.
    /// </summary>
    public static string CopyBootFilesScript(int sourceDisk, PartitionInfo sourceBoot, char tempLetter, char windows, char boot, bool uefi)
    {
        string store = uefi ? $@"{boot}:\EFI\Microsoft\Boot\BCD" : $@"{boot}:\Boot\BCD";
        var access = SourceAccess(sourceDisk, sourceBoot, tempLetter);
        return
            $"$src = '{access.Letter}:\\'; $dst = '{boot}:\\'; " +
            access.Add +
            "try { " +
            "$out = & robocopy.exe $src $dst /E /COPYALL /DCOPY:DAT /XD 'System Volume Information' '$RECYCLE.BIN' /R:1 /W:1 /NFL /NDL /NJH /NP 2>&1 | Out-String; " +
            "$rc = $LASTEXITCODE; Write-Output $out; if ($rc -ge 8) { throw \"robocopy failed ($rc).\" } " +
            "} finally { " + access.Remove + " }; " +
            $"$store = '{store}'; " +
            $"$cmds = @(@('{{bootmgr}}','device','partition={boot}:'), @('{{default}}','device','partition={windows}:'), " +
            $"@('{{default}}','osdevice','partition={windows}:')); " +
            // What the boot store and the two partitions look like now, so a failure below can be understood from the log.
            $"Write-Output ('Boot store before: ' + (& bcdedit.exe /store $store /enum all 2>&1 | Out-String)); " +
            $"Write-Output ((Get-Partition | Where-Object {{ $_.DriveLetter -in '{boot}', '{windows}' }} | " +
            "Format-Table DriveLetter, DiskNumber, PartitionNumber, Offset, Size, Type -AutoSize | Out-String)); " +
            "$failed = @(); " +
            "foreach ($c in $cmds) { $o = & bcdedit.exe /store $store /set $c[0] $c[1] $c[2] 2>&1 | Out-String; $rc = $LASTEXITCODE; " +
            "Write-Output ('bcdedit /set ' + ($c -join ' ') + ' -> exit ' + $rc + ' ' + $o.Trim()); " +
            "if ($rc -ne 0) { $failed += ($c -join ' ') } }; " +
            "if ($failed.Count -gt 0) { throw ('bcdedit failed for: ' + ($failed -join '; ')) }; exit 0";
    }

    /// <summary>
    /// How to reach a source partition from a script. One that already has a drive letter (for example the System Reserved
    /// partition of Windows 7) is used as it is: giving it a second letter fails. Otherwise a temporary letter is added
    /// before the copy and removed after it.
    /// </summary>
    private static (char Letter, string Add, string Remove) SourceAccess(int disk, PartitionInfo partition, char tempLetter)
    {
        foreach (var volume in partition.Volumes)
            if (volume.Letter is { Length: > 0 } text && char.IsAsciiLetter(text[0]))
                return (char.ToUpperInvariant(text[0]), "", "$null = 0");

        return (tempLetter,
            $"Add-PartitionAccessPath -DiskNumber {disk} -PartitionNumber {partition.Number} -AccessPath $src; ",
            $"Remove-PartitionAccessPath -DiskNumber {disk} -PartitionNumber {partition.Number} -AccessPath $src -ErrorAction SilentlyContinue");
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;

    internal static string VerifyFilesScript(char boot, bool uefi)
    {
        string loader = uefi ? $@"{boot}:\EFI\Microsoft\Boot\bootmgfw.efi" : $@"{boot}:\bootmgr";
        string store = uefi ? $@"{boot}:\EFI\Microsoft\Boot\BCD" : $@"{boot}:\Boot\BCD";
        return $"foreach ($p in '{loader}', '{store}') {{ if (-not (Test-Path -LiteralPath $p)) {{ throw \"Missing boot file: $p\" }} }}; exit 0";
    }

    internal static string VerifyEntryScript(char windows, char boot, bool uefi)
    {
        string store = uefi ? $@"{boot}:\EFI\Microsoft\Boot\BCD" : $@"{boot}:\Boot\BCD";
        return $"$out = & bcdedit.exe /store '{store}' /enum '{{default}}' 2>&1 | Out-String; $rc = $LASTEXITCODE; Write-Output $out; " +
               $"if ($rc -ne 0) {{ throw 'bcdedit could not read the boot store.' }}; " +
               $"if ($out -notmatch '(?i)partition={windows}:') {{ throw 'The default boot entry does not point to {windows}:.' }}; exit 0";
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>Runs a script; returns null when it worked, otherwise the message to show.</summary>
    private async Task<string?> RunScript(string script, IProgress<string>? log, CancellationToken ct)
    {
        var result = await executor.ExecuteAsync(script, ct);
        if (!string.IsNullOrWhiteSpace(result.Output)) log?.Report(result.Output.Trim());
        return result.Success ? null : Describe(result);
    }

    private static string Describe(CommandResult result)
    {
        string text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        text = text.Trim();
        return text.Length == 0 ? $"The command failed (exit code {result.ExitCode})." : text;
    }

    private static IReadOnlyCollection<char> DefaultUsedLetters() =>
        DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToList();

    /// <summary>Five free letters from Z downwards (boot, Windows, recovery copy, recovery source, source boot), or null.</summary>
    public static char[]? PickLetters(IReadOnlyCollection<char> used, char sourceLetter)
    {
        var taken = used.Select(char.ToUpperInvariant).Append(char.ToUpperInvariant(sourceLetter)).ToHashSet();
        var free = new List<char>();
        for (char c = 'Z'; c >= 'D' && free.Count < 5; c--)
            if (!taken.Contains(c)) free.Add(c);
        return free.Count == 5 ? [.. free] : null;
    }
}
