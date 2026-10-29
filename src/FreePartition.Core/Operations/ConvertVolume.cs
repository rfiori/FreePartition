namespace FreePartition.Core.Operations;

/// <summary>
/// Converts a volume between FAT32 and NTFS keeping its files.
/// FAT32 → NTFS uses the in-place <c>convert.exe</c> that ships with Windows (the files are not copied).
/// NTFS → FAT32 has no in-place tool, so the files are copied to a staging folder on <paramref name="StagingDrive"/>,
/// verified, the volume is formatted as FAT32 and the files are copied back and verified again.
/// The staging copy is only deleted once the copy back has been verified.
/// </summary>
public sealed record ConvertVolume(char Drive, FileSystemFormat Target, char? StagingDrive = null) : DiskOperation
{
    /// <summary>Windows formats FAT32 volumes only up to 32 GB.</summary>
    public const long MaxFat32VolumeBytes = 32L << 30;

    /// <summary>FAT32 stores a file size in 32 bits.</summary>
    public const long MaxFat32FileBytes = uint.MaxValue;

    public const string ToNtfsWarning =
        "The volume is dismounted during the conversion, so close every program that uses it. " +
        "The conversion cannot be undone: NTFS → FAT32 means copying the files again.";

    public const string ToFat32Warning =
        "The files are copied to the staging drive, the volume is formatted as FAT32 and the files are copied back. " +
        "If the power fails or the disk is unplugged after the format starts, the files exist only in the staging folder " +
        "(\\FreePartition_Convert_<drive>) on the staging drive. FAT32 does not keep NTFS permissions, compression, " +
        "encryption, alternate data streams or hard links, and cannot hold a file of 4 GB or more.";

    public override string Description => Target == FileSystemFormat.NTFS
        ? $"Convert {char.ToUpperInvariant(Drive)}: from FAT32 to NTFS (files are kept)"
        : $"Convert {char.ToUpperInvariant(Drive)}: from NTFS to FAT32 (files are copied to " +
          $"{(StagingDrive is { } s ? char.ToUpperInvariant(s) : '?')}: and back)";

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };
    public override string? Warning => Target == FileSystemFormat.NTFS ? ToNtfsWarning : ToFat32Warning;

    /// <summary>Free space the staging drive needs for a volume that uses <paramref name="usedBytes"/> (5% plus 64 MB of slack). Same rule as the generated script.</summary>
    public static ulong RequiredStagingBytes(ulong usedBytes) => (ulong)(usedBytes * 1.05) + (64UL << 20);

    public override void Validate()
    {
        char d = NormalizeLetter(Drive);
        switch (Target)
        {
            case FileSystemFormat.NTFS:
                break;
            case FileSystemFormat.FAT32:
                if (StagingDrive is not { } staging)
                    throw new ArgumentException("Choose a drive to hold the files while the volume is converted to FAT32.");
                if (NormalizeLetter(staging) == d)
                    throw new ArgumentException("The staging drive must be a different drive from the one being converted.");
                break;
            default:
                throw new ArgumentException("A volume can only be converted to NTFS or FAT32.");
        }
    }

    public override string GenerateScript()
    {
        Validate();
        char d = NormalizeLetter(Drive);
        return Target == FileSystemFormat.NTFS ? ToNtfs(d) : ToFat32(d, NormalizeLetter(StagingDrive!.Value));
    }

    private static string ToNtfs(char d) =>
        $"$v = Get-Volume -DriveLetter {d}; " +
        $"if ($v.FileSystem -ne 'FAT32') {{ throw \"{d}: is $($v.FileSystem), not FAT32.\" }}; " +
        // convert.exe asks for the current label before it starts; /x dismounts the volume instead of asking to schedule it for the next restart.
        $"$out = $v.FileSystemLabel | & convert.exe {d}: /fs:ntfs /x | Out-String; " +
        $"if ($LASTEXITCODE -ne 0) {{ throw \"convert.exe failed (code $LASTEXITCODE). No files were changed. $out\" }}; " +
        $"if ((Get-Volume -DriveLetter {d}).FileSystem -ne 'NTFS') {{ throw \"The conversion did not finish. $out\" }}; " +
        $"\"{d}: is now NTFS.\"";

    private static string ToFat32(char d, char s) =>
        $"$v = Get-Volume -DriveLetter {d}; " +
        $"if ($v.FileSystem -ne 'NTFS') {{ throw \"{d}: is $($v.FileSystem), not NTFS.\" }}; " +
        $"if ($v.Size -gt {MaxFat32VolumeBytes}) {{ throw '{d}: is larger than 32 GB, the most Windows formats as FAT32.' }}; " +
        $"$sv = Get-Volume -DriveLetter {s}; " +
        $"if ($sv.SizeRemaining -lt (($v.Size - $v.SizeRemaining) * 1.05 + 64MB)) {{ throw 'The files of {d}: do not fit in the free space of {s}:.' }}; " +
        $"$big = Get-ChildItem -LiteralPath '{d}:\\' -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object {{ $_.Length -gt {MaxFat32FileBytes} }} | Select-Object -First 1; " +
        $"if ($big) {{ throw \"FAT32 cannot hold files of 4 GB or more: $($big.FullName)\" }}; " +
        $"$stage = '{s}:\\FreePartition_Convert_{d}'; if (Test-Path $stage) {{ throw \"$stage already exists.\" }}; " +
        $"$label = ($v.FileSystemLabel -replace '[^\\p{{L}}\\p{{N}} _\\-]', ''); if ($label.Length -gt 11) {{ $label = $label.Substring(0, 11) }}; " +
        // What must end up in the staging folder and, later, on the reformatted volume (the system folders are not copied).
        $"$ex = '{d}:\\System Volume Information', '{d}:\\$RECYCLE.BIN'; " +
        $"$src = Get-ChildItem -LiteralPath '{d}:\\' -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object {{ $f = $_.FullName; -not ($ex | Where-Object {{ $f.StartsWith($_ + '\\', [StringComparison]::OrdinalIgnoreCase) }}) }} | Measure-Object Length -Sum; " +
        $"robocopy '{d}:\\' $stage /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XD $ex | Out-Null; " +
        $"if ($LASTEXITCODE -ge 8) {{ throw \"Copy failed (robocopy code $LASTEXITCODE). Nothing was changed on {d}:.\" }}; " +
        // The root of a volume is Hidden+System and robocopy copies that to the staging folder.
        $"(Get-Item -LiteralPath $stage -Force).Attributes = 'Directory'; " +
        $"$dst = Get-ChildItem -LiteralPath $stage -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum; " +
        $"if ($src.Count -ne $dst.Count -or $src.Sum -ne $dst.Sum) {{ throw \"Verification failed: source has $($src.Count) files ($($src.Sum) bytes), copy has $($dst.Count) ($($dst.Sum) bytes). Nothing was changed on {d}:.\" }}; " +
        $"Format-Volume -DriveLetter {d} -FileSystem FAT32 -NewFileSystemLabel $label -Confirm:$false | Out-Null; " +
        $"robocopy $stage '{d}:\\' /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null; " +
        $"if ($LASTEXITCODE -ge 8) {{ throw \"Copy back failed (robocopy code $LASTEXITCODE). Your files are safe in $stage\" }}; " +
        $"$back = Get-ChildItem -LiteralPath '{d}:\\' -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object {{ $f = $_.FullName; -not ($ex | Where-Object {{ $f.StartsWith($_ + '\\', [StringComparison]::OrdinalIgnoreCase) }}) }} | Measure-Object Length -Sum; " +
        $"if ($src.Count -ne $back.Count -or $src.Sum -ne $back.Sum) {{ throw \"Verification failed after copying back. Your files are safe in $stage\" }}; " +
        $"Remove-Item -LiteralPath $stage -Recurse -Force; " +
        $"\"{d}: is now FAT32.\"";
}
