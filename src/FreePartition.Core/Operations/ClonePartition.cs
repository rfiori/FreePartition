using FreePartition.Core.Disks;
using FreePartition.Core.Util;

namespace FreePartition.Core.Operations;

/// <summary>
/// Copies a partition, sector by sector, into unallocated space of the same or another disk. The original is only
/// read; the copy gets the same size and file system and appears as a new partition. This is NOT a PowerShell script:
/// <see cref="RunAsync"/> uses an <see cref="IPartitionCloner"/>, and the script only mounts the copy afterwards.
/// </summary>
public sealed record ClonePartition(
    int SourceDisk, int SourcePartition, char Drive, long SourceOffsetBytes, long SizeBytes,
    int DestinationDisk, long DestinationOffsetBytes, FileSystemFormat FileSystem, char? Letter = null,
    IPartitionCloner? Cloner = null) : DiskOperation
{
    public const string DuplicateWarning =
        "The copy is identical to the original, including its volume serial number. Windows copes with that, but " +
        "programs that identify a drive by serial number may confuse the two. The unallocated space it goes into is " +
        "overwritten, so anything deleted there can no longer be recovered. The original is not changed and the copy " +
        "can be canceled while it runs.";

    public override string Description =>
        $"Clone {char.ToUpperInvariant(Drive)}: ({ByteFormatter.Format(SizeBytes)}) to {ByteFormatter.Format(DestinationOffsetBytes)} " +
        $"on disk {DestinationDisk}" + (Letter is { } l ? $" (letter {char.ToUpperInvariant(l)}:)" : "");

    // The unallocated space it overwrites may hold deleted files, but no existing partition is changed.
    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives =>
        Letter is { } l ? new[] { char.ToUpperInvariant(Drive), char.ToUpperInvariant(l) } : new[] { char.ToUpperInvariant(Drive) };
    public override string? Warning => DuplicateWarning;

    public override void Validate()
    {
        NormalizeLetter(Drive);
        if (Letter is { } l && NormalizeLetter(l) == NormalizeLetter(Drive))
            throw new ArgumentException("The copy needs a different drive letter.");
        if (SourceDisk < 0 || DestinationDisk < 0)
            throw new ArgumentException("Invalid disk number.");
        if (SourcePartition < 1)
            throw new ArgumentException("Invalid partition number.");
        if (!Enum.IsDefined(FileSystem))
            throw new ArgumentException("Invalid file system. Use NTFS, FAT32 or exFAT.");
        if (SourceOffsetBytes < 0 || SizeBytes < 1L << 20 || DestinationOffsetBytes < 0
            || SourceOffsetBytes % DriveLayout.MinSectorSize != 0 || SizeBytes % DriveLayout.MinSectorSize != 0)
            throw new ArgumentException("Invalid partition position.");
        if (DestinationOffsetBytes % DriveLayout.Alignment != 0)
            throw new ArgumentException("The destination must be aligned to 4 KB.");
        if (DestinationOffsetBytes < DriveLayout.FirstUsableOffset)
            throw new ArgumentException("The destination must be after the first 1 MB of the disk.");
        if (SourceDisk == DestinationDisk
            && DestinationOffsetBytes < SourceOffsetBytes + SizeBytes && SourceOffsetBytes < DestinationOffsetBytes + SizeBytes)
            throw new ArgumentException("The destination overlaps the partition being cloned.");
    }

    /// <summary>Shown in simulation mode, and the part that runs after the app has copied and added the partition.</summary>
    public override string GenerateScript()
    {
        Validate();
        string letter = Letter is { } l
            ? $"Add-PartitionAccessPath -InputObject $n -AccessPath '{NormalizeLetter(l)}:\\'"
            : "Add-PartitionAccessPath -InputObject $n -AssignDriveLetter";
        return $"Update-Disk -Number {DestinationDisk}; $o = {DestinationOffsetBytes}; " +
               $"$n = Get-Partition -DiskNumber {DestinationDisk} | Where-Object {{ $_.Offset -eq $o }}; " +
               $"if (-not $n) {{ throw 'The cloned partition was not found.' }}; " +
               $"if (-not ($n.AccessPaths | Where-Object {{ $_ -match '^[A-Za-z]:\\\\' }})) {{ {letter} }}" +
               $" # before this, the app locks {NormalizeLetter(Drive)}:, copies its {SizeBytes} bytes " +
               $"(partition {SourcePartition} at offset {SourceOffsetBytes} on PhysicalDrive{SourceDisk}) to offset " +
               $"{DestinationOffsetBytes} of PhysicalDrive{DestinationDisk} and adds the partition table entry";
    }

    public override async Task<CommandResult> RunAsync(
        ICommandExecutor executor, string script, IProgress<string>? progress, CancellationToken ct)
    {
        Validate();
        ct.ThrowIfCancellationRequested();

        var request = new PartitionCloneRequest(SourceDisk, SourcePartition, NormalizeLetter(Drive), SourceOffsetBytes,
            SizeBytes, DestinationDisk, DestinationOffsetBytes, FileSystem);
        var cloner = Cloner ?? new PhysicalPartitionCloner();
        await Task.Run(() => cloner.Clone(request, progress, ct), CancellationToken.None);

        // The copy is in the table: the rest only mounts it, and the script comment is skipped by PowerShell.
        return await executor.ExecuteAsync(script, ct);
    }
}
