using System.Text.RegularExpressions;
using FreePartition.Core.Disks;
using FreePartition.Core.Util;

namespace FreePartition.Core.Operations;

/// <summary>
/// A disk operation that waits in the queue until the user clicks "Apply".
/// Each operation validates its own parameters and generates a PowerShell script
/// (only from validated values: letters, numbers and closed lists).
/// </summary>
public abstract record DiskOperation
{
    private static readonly Regex LabelPattern = new(@"^[\p{L}\p{N} _\-]*$", RegexOptions.Compiled);

    public abstract string Description { get; }

    /// <summary>Can cause data loss or is hard to reverse.</summary>
    public abstract bool Destructive { get; }

    /// <summary>Operations that must never run on the drive where Windows is installed.</summary>
    public abstract bool ForbiddenOnSystemDrive { get; }

    public abstract IReadOnlyList<char> AffectedDrives { get; }

    /// <summary>The command's output is the point of the operation (e.g. a scan result), so it is shown in the log.</summary>
    public virtual bool ShowsOutput => false;

    public string RiskText => Destructive ? "Destructive" : "Safe";

    /// <summary>Extra warning the user must see before applying (e.g. what happens if the power fails).</summary>
    public virtual string? Warning => null;

    /// <exception cref="ArgumentException">Invalid parameters.</exception>
    public abstract void Validate();

    public abstract string GenerateScript();

    /// <summary>
    /// Runs the operation. By default that is the generated script; an operation that cannot be a PowerShell
    /// script (the raw copy of a move) overrides this and does its work in-process.
    /// </summary>
    public virtual Task<CommandResult> RunAsync(
        ICommandExecutor executor, string script, IProgress<string>? progress, CancellationToken ct) =>
        executor.ExecuteAsync(script, ct);

    /// <summary>
    /// Assigns the drive letter only after the new partition is formatted: with a letter on a RAW volume Windows
    /// opens an Explorer window that shows "E:\ is not accessible" while the format is still running.
    /// Expects the new partition in $n.
    /// </summary>
    protected static string AssignLetter(char? letter) =>
        "$n = Get-Partition -DiskNumber $n.DiskNumber -PartitionNumber $n.PartitionNumber; " +
        (letter is { } l
            ? $"Add-PartitionAccessPath -InputObject $n -AccessPath '{NormalizeLetter(l)}:\'"
            : "Add-PartitionAccessPath -InputObject $n -AssignDriveLetter");

    protected static char NormalizeLetter(char letter)
    {
        if (!char.IsAsciiLetter(letter))
            throw new ArgumentException($"Invalid drive letter: '{letter}'.");
        return char.ToUpperInvariant(letter);
    }

    /// <summary>Accepts only letters, numbers, space, '_' and '-': nothing that could break the script.</summary>
    protected static string ValidateLabel(string? label, int maxLength)
    {
        label ??= "";
        if (label.Length > maxLength)
            throw new ArgumentException($"The label can have at most {maxLength} characters.");
        if (!LabelPattern.IsMatch(label))
            throw new ArgumentException("The label can only contain letters, numbers, space, '_' and '-'.");
        return label;
    }
}

/// <summary>Shrinks or extends a partition. Without a size, extends to the maximum possible.</summary>
public sealed record ResizePartition(char Drive, long? NewSizeBytes = null) : DiskOperation
{
    private const long MinimumSize = 1L << 20;   // 1 MiB

    public override string Description => NewSizeBytes is null
        ? $"Extend {char.ToUpperInvariant(Drive)}: to the maximum available"
        : $"Resize {char.ToUpperInvariant(Drive)}: to {ByteFormatter.Format(NewSizeBytes.Value)}";

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => false;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override void Validate()
    {
        NormalizeLetter(Drive);
        if (NewSizeBytes is < MinimumSize)
            throw new ArgumentException("The new size must be at least 1 MB.");
    }

    public override string GenerateScript()
    {
        char d = NormalizeLetter(Drive);
        return NewSizeBytes is null
            ? $"$max = (Get-PartitionSupportedSize -DriveLetter {d}).SizeMax; Resize-Partition -DriveLetter {d} -Size $max"
            : $"Resize-Partition -DriveLetter {d} -Size {NewSizeBytes.Value}";
    }
}

public enum FileSystemFormat
{
    NTFS,
    FAT32,
    exFAT
}

/// <summary>Formats a volume. ERASES all data on the drive.</summary>
public sealed record FormatVolume(char Drive, FileSystemFormat FileSystem, string Label = "") : DiskOperation
{
    public override string Description =>
        $"Format {char.ToUpperInvariant(Drive)}: as {FileSystem}" +
        (string.IsNullOrEmpty(Label) ? "" : $" (label \"{Label}\")") + " — ERASES ALL DATA";

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override void Validate()
    {
        NormalizeLetter(Drive);
        if (!Enum.IsDefined(FileSystem))
            throw new ArgumentException("Invalid file system. Use NTFS, FAT32 or exFAT.");
        ValidateLabel(Label, FileSystem == FileSystemFormat.FAT32 ? 11 : 32);
    }

    public override string GenerateScript()
    {
        Validate();
        char d = NormalizeLetter(Drive);
        return $"Format-Volume -DriveLetter {d} -FileSystem {FileSystem} -NewFileSystemLabel '{Label ?? ""}' -Confirm:$false";
    }
}

public sealed record ChangeVolumeLabel(char Drive, string NewLabel) : DiskOperation
{
    public override string Description => $"Change label of {char.ToUpperInvariant(Drive)}: to \"{NewLabel}\"";
    public override bool Destructive => false;
    public override bool ForbiddenOnSystemDrive => false;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override void Validate()
    {
        NormalizeLetter(Drive);
        ValidateLabel(NewLabel, 32);
    }

    public override string GenerateScript()
    {
        Validate();
        return $"Set-Volume -DriveLetter {NormalizeLetter(Drive)} -NewFileSystemLabel '{NewLabel ?? ""}'";
    }
}

public sealed record ChangeDriveLetter(char Current, char New) : DiskOperation
{
    public override string Description =>
        $"Change letter of {char.ToUpperInvariant(Current)}: to {char.ToUpperInvariant(New)}:";

    public override bool Destructive => false;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Current), char.ToUpperInvariant(New) };

    public override void Validate()
    {
        char c = NormalizeLetter(Current);
        char n = NormalizeLetter(New);
        if (c == n)
            throw new ArgumentException("The new letter must be different from the current one.");
    }

    public override string GenerateScript()
    {
        Validate();
        return $"Set-Partition -DriveLetter {NormalizeLetter(Current)} -NewDriveLetter {NormalizeLetter(New)}";
    }
}

public enum DiskPartitionStyle
{
    MBR,
    GPT
}

/// <summary>
/// Initializes a blank (RAW) disk with an MBR or GPT partition table. The script refuses to touch a disk
/// that is already initialized, so existing partitions can never be lost through this operation.
/// </summary>
public sealed record InitializeDisk(int DiskNumber, DiskPartitionStyle Style) : DiskOperation
{
    public override string Description => $"Initialize disk {DiskNumber} as {Style}";
    public override bool Destructive => false;
    public override bool ForbiddenOnSystemDrive => false;
    public override IReadOnlyList<char> AffectedDrives => Array.Empty<char>();

    public override void Validate()
    {
        if (DiskNumber < 0)
            throw new ArgumentException("Invalid disk number.");
        if (!Enum.IsDefined(Style))
            throw new ArgumentException("Invalid partition style. Use MBR or GPT.");
    }

    public override string GenerateScript()
    {
        Validate();
        return $"if ((Get-Disk -Number {DiskNumber}).PartitionStyle -ne 'RAW') {{ throw 'Disk {DiskNumber} is already initialized.' }}; " +
               $"Initialize-Disk -Number {DiskNumber} -PartitionStyle {Style}";
    }
}

/// <summary>
/// Creates a partition in the unallocated space of a disk and formats it.
/// Without a size, uses all the remaining space. Without a letter, Windows picks one.
/// With <paramref name="GapOffsetBytes"/> and <paramref name="GapSizeBytes"/> the partition is placed inside that
/// unallocated stretch (the one the user selected) instead of wherever Windows finds room first.
/// </summary>
public sealed record CreatePartition(
    int DiskNumber, FileSystemFormat FileSystem, string Label = "",
    long? SizeBytes = null, char? Letter = null, long? GapOffsetBytes = null, long? GapSizeBytes = null) : DiskOperation
{
    private const long MinimumSize = 1L << 20;   // 1 MiB

    public override string Description =>
        $"Create {(SizeBytes is null ? "a partition using all free space" : $"a {ByteFormatter.Format(SizeBytes.Value)} partition")} " +
        $"on disk {DiskNumber} as {FileSystem}" +
        (Letter is { } l ? $" (letter {char.ToUpperInvariant(l)}:)" : "") +
        (string.IsNullOrEmpty(Label) ? "" : $" (label \"{Label}\")");

    public override bool Destructive => false;
    public override bool ForbiddenOnSystemDrive => false;
    public override IReadOnlyList<char> AffectedDrives =>
        Letter is { } l ? new[] { char.ToUpperInvariant(l) } : Array.Empty<char>();

    public override void Validate()
    {
        if (DiskNumber < 0)
            throw new ArgumentException("Invalid disk number.");
        if (Letter is { } l) NormalizeLetter(l);
        if (!Enum.IsDefined(FileSystem))
            throw new ArgumentException("Invalid file system. Use NTFS, FAT32 or exFAT.");
        if (SizeBytes is < MinimumSize)
            throw new ArgumentException("The new size must be at least 1 MB.");
        if (GapOffsetBytes is < 0 || GapSizeBytes is <= 0 || GapOffsetBytes is null != GapSizeBytes is null)
            throw new ArgumentException("Invalid unallocated stretch.");
        ValidateLabel(Label, FileSystem == FileSystemFormat.FAT32 ? 11 : 32);
    }

    public override string GenerateScript()
    {
        Validate();
        string create;
        if (GapOffsetBytes is { } gapOffset && GapSizeBytes is { } gapSize)
        {
            // Earlier queued creations may already occupy the start of the stretch: begin after them,
            // on a 1 MiB boundary, and never go past the end of the stretch.
            string size = SizeBytes is { } bytes ? $"([math]::Min({bytes}, $max))" : "$max";
            create = $"$o = {gapOffset}; $e = {gapOffset + gapSize}; " +
                     $"foreach ($q in Get-Partition -DiskNumber {DiskNumber}) {{ if ($q.Offset -ge $o -and $q.Offset -lt $e) {{ $o = [math]::Max($o, $q.Offset + $q.Size) }} }}; " +
                     $"$off = [math]::Ceiling($o / 1MB) * 1MB; $max = [math]::Floor(($e - $off) / 1MB) * 1MB; " +
                     $"if ($max -lt 1MB) {{ throw 'The selected unallocated space has no room left.' }}; " +
                     $"$n = New-Partition -DiskNumber {DiskNumber} -Offset $off -Size {size}; ";
        }
        else
        {
            string size = SizeBytes is { } bytes ? $"-Size {bytes}" : "-UseMaximumSize";
            create = $"$n = New-Partition -DiskNumber {DiskNumber} {size}; ";
        }
        return create +
               $"Format-Volume -Partition $n -FileSystem {FileSystem} -NewFileSystemLabel '{Label ?? ""}' -Confirm:$false; " +
               AssignLetter(Letter);
    }
}

/// <summary>
/// Checks a volume for file system errors (like chkdsk). A scan only reads; a repair can change the file system
/// (and takes the volume offline), so it is refused on the Windows drive.
/// </summary>
public sealed record CheckVolume(char Drive, bool Repair = false) : DiskOperation
{
    public override string Description => Repair
        ? $"Check {char.ToUpperInvariant(Drive)}: for errors and repair them"
        : $"Check {char.ToUpperInvariant(Drive)}: for errors (scan only)";

    public override bool Destructive => Repair;
    public override bool ForbiddenOnSystemDrive => Repair;
    public override bool ShowsOutput => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override void Validate() => NormalizeLetter(Drive);

    public override string GenerateScript()
    {
        Validate();
        char d = NormalizeLetter(Drive);
        string mode = Repair ? "-OfflineScanAndFix" : "-Scan";
        return $"$r = Repair-Volume -DriveLetter {d} {mode}; \"Result for {d}: $r\"";
    }
}

/// <summary>Marks an MBR partition as active (bootable). Not applicable to GPT disks.</summary>
public sealed record SetActivePartition(char Drive) : DiskOperation
{
    public override string Description => $"Set partition {char.ToUpperInvariant(Drive)}: as active (MBR only)";
    public override bool Destructive => false;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override void Validate() => NormalizeLetter(Drive);

    public override string GenerateScript()
    {
        Validate();
        return $"Set-Partition -DriveLetter {NormalizeLetter(Drive)} -IsActive $true";
    }
}

/// <summary>Hides a partition by removing its drive letter. The data stays untouched.</summary>
public sealed record HidePartition(char Drive) : DiskOperation
{
    public override string Description => $"Hide partition {char.ToUpperInvariant(Drive)}: (remove its drive letter)";
    public override bool Destructive => false;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override void Validate() => NormalizeLetter(Drive);

    public override string GenerateScript()
    {
        Validate();
        char d = NormalizeLetter(Drive);
        return $"Remove-PartitionAccessPath -DriveLetter {d} -AccessPath '{d}:\\'";
    }
}

/// <summary>Shows a hidden partition by giving it a drive letter.</summary>
public sealed record ShowPartition(int DiskNumber, int PartitionNumber, char Letter) : DiskOperation
{
    public override string Description =>
        $"Show partition {PartitionNumber} of disk {DiskNumber} as {char.ToUpperInvariant(Letter)}:";

    public override bool Destructive => false;
    public override bool ForbiddenOnSystemDrive => false;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Letter) };

    public override void Validate()
    {
        if (DiskNumber < 0)
            throw new ArgumentException("Invalid disk number.");
        if (PartitionNumber < 1)
            throw new ArgumentException("Invalid partition number.");
        NormalizeLetter(Letter);
    }

    public override string GenerateScript()
    {
        Validate();
        // Skips the cmdlet when the partition already has this path (it fails with "already in use" otherwise).
        char d = NormalizeLetter(Letter);
        return $"$p = Get-Partition -DiskNumber {DiskNumber} -PartitionNumber {PartitionNumber}; " +
               $"if ($p.AccessPaths -notcontains '{d}:\\') {{ Add-PartitionAccessPath -InputObject $p -AccessPath '{d}:\\' }}";
    }
}

/// <summary>Deletes a partition (and its volume). ERASES all data on the drive.</summary>
public sealed record DeletePartition(char Drive) : DiskOperation
{
    public override string Description => $"Delete partition {char.ToUpperInvariant(Drive)}: — ERASES ALL DATA";
    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override void Validate() => NormalizeLetter(Drive);

    public override string GenerateScript()
    {
        Validate();
        return $"Remove-Partition -DriveLetter {NormalizeLetter(Drive)} -Confirm:$false";
    }
}

/// <summary>
/// Merges a partition into the one right before it on the same disk without losing files: copies everything into
/// a "Merged_X" folder on the target, deletes the source and extends the target by exactly the size of the source (other free space is left alone).
/// The script stops before deleting anything if the partitions are not adjacent, the files do not fit or the copy fails.
/// </summary>
public sealed record MergePartitions(char Source, char Target) : DiskOperation
{
    public override string Description =>
        $"Merge {char.ToUpperInvariant(Source)}: into {char.ToUpperInvariant(Target)}: " +
        $"(files go to {char.ToUpperInvariant(Target)}:\\Merged_{char.ToUpperInvariant(Source)}, then {char.ToUpperInvariant(Source)}: is deleted)";

    /// <summary>Free space the target needs to take the files of a source that uses <paramref name="sourceUsedBytes"/> (5% plus 64 MB of slack). Same rule as the generated script.</summary>
    public static ulong RequiredFreeBytes(ulong sourceUsedBytes) => (ulong)(sourceUsedBytes * 1.05) + (64UL << 20);

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;

    // Only the source is listed: it is the one that disappears. Extending the Windows drive is allowed.
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Source) };

    public override void Validate()
    {
        char s = NormalizeLetter(Source);
        char t = NormalizeLetter(Target);
        if (s == t)
            throw new ArgumentException("The source and the target must be different partitions.");
    }

    public override string GenerateScript()
    {
        Validate();
        char s = NormalizeLetter(Source);
        char t = NormalizeLetter(Target);
        return $"$s = Get-Partition -DriveLetter {s}; $t = Get-Partition -DriveLetter {t}; " +
               $"if ($s.DiskNumber -ne $t.DiskNumber) {{ throw 'The partitions are on different disks.' }}; " +
               $"if ($s.Offset -le $t.Offset -or @(Get-Partition -DiskNumber $t.DiskNumber | Where-Object {{ $_.Offset -gt $t.Offset -and $_.Offset -lt $s.Offset }}).Count -gt 0) " +
               $"{{ throw '{s}: must come right after {t}: on the disk.' }}; " +
               $"$sv = Get-Volume -DriveLetter {s}; $tv = Get-Volume -DriveLetter {t}; " +
               $"if ($tv.SizeRemaining -lt (($sv.Size - $sv.SizeRemaining) * 1.05 + 64MB)) {{ throw 'The files of {s}: do not fit in the free space of {t}:.' }}; " +
               $"$dest = '{t}:\\Merged_{s}'; if (Test-Path $dest) {{ throw \"$dest already exists.\" }}; " +
               $"robocopy '{s}:\\' $dest /E /COPYALL /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XD '{s}:\\System Volume Information' '{s}:\\$RECYCLE.BIN' | Out-Null; " +
               $"if ($LASTEXITCODE -ge 8) {{ throw \"Copy failed (robocopy code $LASTEXITCODE). Nothing was deleted.\" }}; " +
               // The root of a volume is Hidden+System and robocopy copies that to the destination folder, hiding it in Explorer.
               $"(Get-Item -LiteralPath $dest -Force).Attributes = 'Directory'; " +
               // Verify before deleting: the copy must have the same number of files and bytes as the source.
               $"$ex = '{s}:\\System Volume Information', '{s}:\\$RECYCLE.BIN'; " +
               $"$src = Get-ChildItem -LiteralPath '{s}:\\' -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object {{ $f = $_.FullName; -not ($ex | Where-Object {{ $f.StartsWith($_ + '\\', [StringComparison]::OrdinalIgnoreCase) }}) }} | Measure-Object Length -Sum; " +
               $"$dst = Get-ChildItem -LiteralPath $dest -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum; " +
               $"if ($src.Count -ne $dst.Count -or $src.Sum -ne $dst.Sum) {{ throw \"Verification failed: source has $($src.Count) files ($($src.Sum) bytes), copy has $($dst.Count) ($($dst.Sum) bytes). Nothing was deleted.\" }}; " +
               $"Remove-Partition -DriveLetter {s} -Confirm:$false; " +
               $"$max = (Get-PartitionSupportedSize -DriveLetter {t}).SizeMax; " +
               $"Resize-Partition -DriveLetter {t} -Size ([math]::Min($s.Offset + $s.Size - $t.Offset, $max))";
    }
}

/// <summary>
/// Splits a partition in two: shrinks it and creates a new, formatted partition in the freed space at its end.
/// The files stay on the original partition.
/// </summary>
public sealed record SplitPartition(
    char Drive, long NewPartitionBytes, FileSystemFormat FileSystem, string Label = "", char? Letter = null) : DiskOperation
{
    private const long MinimumSize = 1L << 20;   // 1 MiB

    public override string Description =>
        $"Split {char.ToUpperInvariant(Drive)}: — new {ByteFormatter.Format(NewPartitionBytes)} {FileSystem} partition" +
        (Letter is { } l ? $" (letter {char.ToUpperInvariant(l)}:)" : "") +
        (string.IsNullOrEmpty(Label) ? "" : $" (label \"{Label}\")");

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => false;
    public override IReadOnlyList<char> AffectedDrives =>
        Letter is { } l ? new[] { char.ToUpperInvariant(Drive), char.ToUpperInvariant(l) } : new[] { char.ToUpperInvariant(Drive) };

    public override void Validate()
    {
        NormalizeLetter(Drive);
        if (Letter is { } l && NormalizeLetter(l) == NormalizeLetter(Drive))
            throw new ArgumentException("The new partition needs a different drive letter.");
        if (NewPartitionBytes < MinimumSize)
            throw new ArgumentException("The new partition must be at least 1 MB.");
        if (!Enum.IsDefined(FileSystem))
            throw new ArgumentException("Invalid file system. Use NTFS, FAT32 or exFAT.");
        ValidateLabel(Label, FileSystem == FileSystemFormat.FAT32 ? 11 : 32);
    }

    public override string GenerateScript()
    {
        Validate();
        char d = NormalizeLetter(Drive);
        // New-Partition rejects offsets that are not 1 MiB aligned, so the original partition is shrunk to a whole
        // number of MiB (its start is aligned) and the offset is rounded up as a safety net.
        return $"$p = Get-Partition -DriveLetter {d}; $end = $p.Offset + $p.Size; " +
               $"if ({NewPartitionBytes} -ge $p.Size) {{ throw 'The new partition must be smaller than {d}:.' }}; " +
               $"Resize-Partition -DriveLetter {d} -Size ([math]::Floor(($p.Size - {NewPartitionBytes}) / 1MB) * 1MB); " +
               $"$k = Get-Partition -DriveLetter {d}; $off = [math]::Ceiling(($k.Offset + $k.Size) / 1MB) * 1MB; " +
               $"$size = [math]::Floor(($end - $off) / 1MB) * 1MB; " +
               $"if ($size -lt 1MB) {{ throw 'Not enough space was freed for the new partition.' }}; " +
               $"$n = New-Partition -DiskNumber $k.DiskNumber -Offset $off -Size $size; " +
               $"Format-Volume -Partition $n -FileSystem {FileSystem} -NewFileSystemLabel '{Label ?? ""}' -Confirm:$false; " +
               AssignLetter(Letter);
    }
}

/// <summary>
/// Moves a partition to another position on the same disk, into free space (before or after it). The sectors are
/// copied in place and the partition table is then rewritten with the new start. This is NOT a PowerShell script:
/// Windows has no cmdlet that changes a partition's offset, so <see cref="RunAsync"/> uses an <see cref="IPartitionMover"/>.
/// Unlike every other operation, interrupting it midway (power loss, crash, unplugging) corrupts the data.
/// </summary>
public sealed record MovePartition(
    int DiskNumber, int PartitionNumber, char Drive, long SourceOffsetBytes, long SizeBytes, long NewOffsetBytes,
    IPartitionMover? Mover = null) : DiskOperation
{
    public const string PowerLossWarning =
        "If the power fails or the computer is turned off while the partition is being moved, the data on it will be " +
        "CORRUPTED and cannot be recovered. Back up the files first, keep the computer plugged in (laptops on AC power, " +
        "desktops on a UPS if possible) and do not unplug the disk until the move has finished. It cannot be canceled once started.";

    public override string Description =>
        $"Move {char.ToUpperInvariant(Drive)}: on disk {DiskNumber} from {ByteFormatter.Format(SourceOffsetBytes)} " +
        $"to {ByteFormatter.Format(NewOffsetBytes)} — DATA IS LOST IF INTERRUPTED";

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };
    public override string? Warning => PowerLossWarning;

    public override void Validate()
    {
        NormalizeLetter(Drive);
        if (DiskNumber < 0)
            throw new ArgumentException("Invalid disk number.");
        if (PartitionNumber < 1)
            throw new ArgumentException("Invalid partition number.");
        if (SizeBytes <= 0 || SourceOffsetBytes < 0 || NewOffsetBytes < 0)
            throw new ArgumentException("Invalid partition position.");
        if (NewOffsetBytes == SourceOffsetBytes)
            throw new ArgumentException("The partition is already at that position.");
        if (SourceOffsetBytes % DriveLayout.Alignment != 0 || SizeBytes % DriveLayout.Alignment != 0
            || NewOffsetBytes % DriveLayout.Alignment != 0)
            throw new ArgumentException("The partition and its destination must be aligned to 4 KB.");
        if (NewOffsetBytes < DriveLayout.FirstUsableOffset)
            throw new ArgumentException("The destination must be after the first 1 MB of the disk.");
    }

    /// <summary>Shown in simulation mode: there is no PowerShell here, so this only describes what would happen.</summary>
    public override string GenerateScript()
    {
        Validate();
        return $"# Not PowerShell (raw copy in the app): lock and dismount {NormalizeLetter(Drive)}:, copy {SizeBytes} bytes " +
               $"from offset {SourceOffsetBytes} to offset {NewOffsetBytes} on PhysicalDrive{DiskNumber}, " +
               $"then set the start of partition {PartitionNumber} to {NewOffsetBytes}";
    }

    public override async Task<CommandResult> RunAsync(
        ICommandExecutor executor, string script, IProgress<string>? progress, CancellationToken ct)
    {
        Validate();
        ct.ThrowIfCancellationRequested();   // the last point where canceling is still safe

        var request = new PartitionMoveRequest(DiskNumber, PartitionNumber, NormalizeLetter(Drive),
            SourceOffsetBytes, SizeBytes, NewOffsetBytes);
        var mover = Mover ?? new PhysicalDiskMover();
        await Task.Run(() => mover.Move(request, progress), CancellationToken.None);
        return new CommandResult(0, $"Partition moved to {ByteFormatter.Format(NewOffsetBytes)}.", "");
    }
}
