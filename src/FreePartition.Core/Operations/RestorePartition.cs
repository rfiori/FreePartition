using FreePartition.Core.Disks;
using FreePartition.Core.Util;

namespace FreePartition.Core.Operations;

/// <summary>Reads the sector at the start of a partition about to be restored. Behind an interface so tests never touch a disk.</summary>
public interface IBootSectorSource
{
    /// <summary>The first sector of the range starting at <paramref name="offset"/> on the disk.</summary>
    byte[] ReadSector(int diskNumber, long offset);
}

/// <summary>Reads the sector from the physical disk, read-only.</summary>
public sealed class PhysicalBootSectorSource : IBootSectorSource
{
    public byte[] ReadSector(int diskNumber, long offset)
    {
        using var disk = RawDisk.Open(diskNumber);
        var sector = new byte[512];
        int got = disk.Read(offset, sector);
        return got == sector.Length ? sector : [];
    }
}

/// <summary>
/// Puts a lost partition back in the partition table: it adds an entry for a range of the disk that still holds a
/// file system, found by <see cref="LostPartitionScanner"/>. Only the table is written, never the partition itself,
/// so its files come back as they were. Right before it creates the entry it checks again that the boot sector is
/// still there and that nothing else occupies the range.
/// </summary>
public sealed record RestorePartition(
    int DiskNumber, long OffsetBytes, long SizeBytes, FileSystemFormat FileSystem,
    IBootSectorSource? Source = null, IPartitionAdder? Adder = null) : DiskOperation
{
    private const long MinimumSize = 1L << 20;   // 1 MiB

    public override string Description =>
        $"Restore the {FileSystem} partition of {ByteFormatter.Format(SizeBytes)} at {ByteFormatter.Format(OffsetBytes)} on disk {DiskNumber} " +
        "(only the partition table is written; its files are not touched)";

    public override bool Destructive => false;
    public override bool ForbiddenOnSystemDrive => false;
    public override IReadOnlyList<char> AffectedDrives => Array.Empty<char>();

    public override void Validate()
    {
        if (DiskNumber < 0)
            throw new ArgumentException("Invalid disk number.");
        if (!Enum.IsDefined(FileSystem))
            throw new ArgumentException("Invalid file system. Use NTFS, FAT32 or exFAT.");
        if (OffsetBytes < 0 || SizeBytes < MinimumSize
            || OffsetBytes % DriveLayout.MinSectorSize != 0 || SizeBytes % DriveLayout.MinSectorSize != 0)
            throw new ArgumentException("Invalid partition position.");
    }

    /// <summary>
    /// What runs after the entry is in the table (the entry itself is written by the app, see <see cref="RunAsync"/>):
    /// give the volume a drive letter if Windows did not. No Format-Volume: the file system is already there.
    /// </summary>
    public override string GenerateScript()
    {
        Validate();
        return $"Update-Disk -Number {DiskNumber}; $o = {OffsetBytes}; " +
               $"$n = Get-Partition -DiskNumber {DiskNumber} | Where-Object {{ $_.Offset -eq $o }}; " +
               $"if (-not $n) {{ throw 'The restored partition was not found.' }}; " +
               $"if (-not ($n.AccessPaths | Where-Object {{ $_ -match '^[A-Za-z]:\\\\' }})) {{ Add-PartitionAccessPath -InputObject $n -AssignDriveLetter }}" +
               $" # before this, the app writes the entry (offset {OffsetBytes}, size {SizeBytes}) into the partition table of PhysicalDrive{DiskNumber} itself: New-Partition would zero the start of the partition";
    }

    public override async Task<CommandResult> RunAsync(
        ICommandExecutor executor, string script, IProgress<string>? progress, CancellationToken ct)
    {
        Validate();
        ct.ThrowIfCancellationRequested();

        // The queue may have waited a long time, and the disk may have changed: the boot sector must still be this one.
        var source = Source ?? new PhysicalBootSectorSource();
        byte[] sector = await Task.Run(() => source.ReadSector(DiskNumber, OffsetBytes), CancellationToken.None);
        if (!BootSectorProbe.TryParse(sector, out var info) || info is null
            || info.FileSystem != FileSystem || info.Length != SizeBytes)
            return new CommandResult(-1, "",
                $"There is no longer a {FileSystem} boot sector of {ByteFormatter.Format(SizeBytes)} at that position of disk {DiskNumber}. Nothing was changed.");

        progress?.Report("Writing the partition table entry...");
        var request = new PartitionAddRequest(DiskNumber, OffsetBytes, SizeBytes, FileSystem);
        await Task.Run(() => (Adder ?? new PhysicalPartitionAdder()).Add(request), CancellationToken.None);

        // The entry is in the table: the rest only mounts the volume, and the script comment line is skipped by PowerShell.
        return await executor.ExecuteAsync(script, ct);
    }
}
