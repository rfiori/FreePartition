using System.Text.RegularExpressions;
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

    public string RiskText => Destructive ? "Destructive" : "Safe";

    /// <exception cref="ArgumentException">Invalid parameters.</exception>
    public abstract void Validate();

    public abstract string GenerateScript();

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

/// <summary>
/// Creates a partition in the unallocated space of a disk and formats it.
/// Without a size, uses all the remaining space. Without a letter, Windows picks one.
/// </summary>
public sealed record CreatePartition(
    int DiskNumber, FileSystemFormat FileSystem, string Label = "",
    long? SizeBytes = null, char? Letter = null) : DiskOperation
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
        ValidateLabel(Label, FileSystem == FileSystemFormat.FAT32 ? 11 : 32);
    }

    public override string GenerateScript()
    {
        Validate();
        string size = SizeBytes is { } bytes ? $"-Size {bytes}" : "-UseMaximumSize";
        string letter = Letter is { } l ? $"-DriveLetter {NormalizeLetter(l)}" : "-AssignDriveLetter";
        return $"$p = New-Partition -DiskNumber {DiskNumber} {size} {letter}; " +
               $"Format-Volume -Partition $p -FileSystem {FileSystem} -NewFileSystemLabel '{Label ?? ""}' -Confirm:$false";
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
