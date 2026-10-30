using FreePartition.Core.Disks;
using FreePartition.Core.Util;

namespace FreePartition.Core.Operations;

/// <summary>Texts and checks shared by the wipe operations.</summary>
public static class Wipe
{
    public const string SsdWarning =
        "On an SSD, overwriting cannot reach the spare cells the drive keeps for itself, so some old data may survive " +
        "there. It is still enough for most uses; for the strongest guarantee use the manufacturer's Secure Erase tool.";

    internal static void Validate(WipePattern pattern, bool verify)
    {
        if (!Enum.IsDefined(pattern))
            throw new ArgumentException("Invalid wipe pattern. Use zeros or random data.");
        if (verify && pattern != WipePattern.Zeros)
            throw new ArgumentException("Only a wipe with zeros can be verified.");
    }

    internal static string Describe(WipePattern pattern, bool verify) =>
        (pattern == WipePattern.Zeros ? "zeros" : "random data") + (verify ? ", verified" : "");
}

/// <summary>
/// Overwrites every sector of one partition. The partition entry stays, but its contents (files and file system)
/// are gone for good: it becomes RAW and has to be formatted. The other partitions are not touched, because the
/// writes go through the partition's own volume.
/// </summary>
public sealed record WipePartition(
    int DiskNumber, int PartitionNumber, char Drive, long OffsetBytes, long SizeBytes,
    WipePattern Pattern = WipePattern.Zeros, bool Verify = false, IDiskWiper? Wiper = null) : DiskOperation
{
    public override string Description =>
        $"Wipe {char.ToUpperInvariant(Drive)}: ({ByteFormatter.Format(SizeBytes)}) with {Wipe.Describe(Pattern, Verify)} — " +
        "ALL ITS DATA IS DESTROYED FOR GOOD";

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override string? Warning =>
        $"Every file on {char.ToUpperInvariant(Drive)}: will be destroyed and cannot be recovered by any tool, " +
        "including this one. The drive must not be in use. Canceling midway leaves it partly wiped and unusable.";

    public override void Validate()
    {
        NormalizeLetter(Drive);
        if (DiskNumber < 0)
            throw new ArgumentException("Invalid disk number.");
        if (PartitionNumber < 1)
            throw new ArgumentException("Invalid partition number.");
        if (OffsetBytes < 0 || SizeBytes <= 0
            || OffsetBytes % DriveLayout.MinSectorSize != 0 || SizeBytes % DriveLayout.MinSectorSize != 0)
            throw new ArgumentException("Invalid partition position.");
        Wipe.Validate(Pattern, Verify);
    }

    /// <summary>Shown in simulation mode: there is no PowerShell here, so this only describes what would happen.</summary>
    public override string GenerateScript()
    {
        Validate();
        return $"# Not PowerShell (raw write in the app): lock and dismount {NormalizeLetter(Drive)}:, then overwrite " +
               $"its {SizeBytes} bytes (partition {PartitionNumber} at offset {OffsetBytes} on PhysicalDrive{DiskNumber}) " +
               $"with {Wipe.Describe(Pattern, Verify)}";
    }

    public override async Task<CommandResult> RunAsync(
        ICommandExecutor executor, string script, IProgress<string>? progress, CancellationToken ct)
    {
        Validate();
        ct.ThrowIfCancellationRequested();

        var request = new VolumeWipeRequest(DiskNumber, PartitionNumber, NormalizeLetter(Drive), OffsetBytes, SizeBytes, Pattern, Verify);
        var wiper = Wiper ?? new PhysicalDiskWiper();
        await Task.Run(() => wiper.WipeVolume(request, progress, ct), CancellationToken.None);
        return new CommandResult(0, $"{NormalizeLetter(Drive)}: was wiped.", "");
    }
}

/// <summary>
/// Wipes a whole disk: removes every partition (Clear-Disk) and then overwrites every sector, from the first to the
/// last. Refused on the disk that holds Windows or this program, by the queue (through its drive letters), by the
/// script (IsSystem/IsBoot) and again by the wiper right before it writes.
/// </summary>
/// <param name="Drives">Drive letters on the disk, so the queue can refuse the disk that holds the Windows drive.</param>
public sealed record WipeDisk(
    int DiskNumber, WipePattern Pattern = WipePattern.Zeros, bool Verify = false,
    IReadOnlyList<char>? Drives = null, IDiskWiper? Wiper = null) : DiskOperation
{
    public override string Description =>
        $"Wipe the whole disk {DiskNumber} with {Wipe.Describe(Pattern, Verify)} — EVERY PARTITION AND FILE IS DESTROYED FOR GOOD";

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives =>
        (Drives ?? Array.Empty<char>()).Select(char.ToUpperInvariant).ToArray();

    public override string? Warning =>
        $"Every partition on disk {DiskNumber} will be deleted and every sector overwritten. Nothing on it can be " +
        "recovered by any tool, including this one. Canceling midway leaves the disk partly wiped and blank.";

    public override void Validate()
    {
        if (DiskNumber < 0)
            throw new ArgumentException("Invalid disk number.");
        foreach (char drive in Drives ?? Array.Empty<char>())
            NormalizeLetter(drive);
        Wipe.Validate(Pattern, Verify);
    }

    public override string GenerateScript()
    {
        Validate();
        return $"$d = Get-Disk -Number {DiskNumber}; " +
               $"if ($d.IsSystem -or $d.IsBoot) {{ throw 'Disk {DiskNumber} holds Windows and cannot be wiped.' }}; " +
               $"if ($d.PartitionStyle -ne 'RAW') {{ Clear-Disk -Number {DiskNumber} -RemoveData -RemoveOEM -Confirm:$false }}" +
               $" # then the app overwrites every sector of PhysicalDrive{DiskNumber} with {Wipe.Describe(Pattern, Verify)}";
    }

    public override async Task<CommandResult> RunAsync(
        ICommandExecutor executor, string script, IProgress<string>? progress, CancellationToken ct)
    {
        Validate();
        ct.ThrowIfCancellationRequested();

        var wiper = Wiper ?? new PhysicalDiskWiper();
        await Task.Run(() => wiper.EnsureNotSystemDisk(DiskNumber), CancellationToken.None);

        progress?.Report($"Removing the partitions of disk {DiskNumber}...");
        var cleared = await executor.ExecuteAsync(script, ct);
        if (!cleared.Success) return cleared;

        await Task.Run(() => wiper.WipeDisk(new DiskWipeRequest(DiskNumber, Pattern, Verify), progress, ct), CancellationToken.None);
        return new CommandResult(0, $"Disk {DiskNumber} was wiped.", "");
    }
}

/// <summary>
/// Overwrites the free space of a drive so files deleted earlier can no longer be recovered. Existing files stay.
/// Not allowed on the Windows drive, which would be left with no free space while it runs.
/// </summary>
public sealed record WipeFreeSpace(char Drive, WipePattern Pattern = WipePattern.Zeros, IDiskWiper? Wiper = null) : DiskOperation
{
    public override string Description =>
        $"Wipe the free space of {char.ToUpperInvariant(Drive)}: with {Wipe.Describe(Pattern, false)} — deleted files become unrecoverable";

    public override bool Destructive => true;   // it destroys what deleted-file recovery could still bring back
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives => new[] { char.ToUpperInvariant(Drive) };

    public override string? Warning =>
        $"{char.ToUpperInvariant(Drive)}: will be filled up completely for a while, so programs writing to it may fail " +
        "until it finishes. Files deleted before will no longer be recoverable. Very small deleted files kept inside " +
        "the NTFS file table are not reached.";

    public override void Validate()
    {
        NormalizeLetter(Drive);
        Wipe.Validate(Pattern, false);
    }

    /// <summary>Shown in simulation mode: there is no PowerShell here, so this only describes what would happen.</summary>
    public override string GenerateScript()
    {
        Validate();
        return $"# Not PowerShell (done in the app): fill {NormalizeLetter(Drive)}:\\{FreeSpaceWiper.FileName} with " +
               $"{Wipe.Describe(Pattern, false)} until the drive is full, flush it, then delete it";
    }

    public override async Task<CommandResult> RunAsync(
        ICommandExecutor executor, string script, IProgress<string>? progress, CancellationToken ct)
    {
        Validate();
        ct.ThrowIfCancellationRequested();

        var wiper = Wiper ?? new PhysicalDiskWiper();
        long written = await Task.Run(() => wiper.WipeFreeSpace(NormalizeLetter(Drive), Pattern, progress, ct), CancellationToken.None);
        return new CommandResult(0, $"Overwrote {ByteFormatter.Format(written)} of free space.", "");
    }
}
