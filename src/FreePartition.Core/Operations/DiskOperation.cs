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
