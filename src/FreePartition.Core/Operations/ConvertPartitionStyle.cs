using FreePartition.Core.Disks;

namespace FreePartition.Core.Operations;

/// <summary>
/// Converts a disk between MBR and GPT without touching the partitions: only the partition table is rewritten, so the
/// files stay where they are. This is NOT a PowerShell script: <see cref="RunAsync"/> uses an
/// <see cref="ILayoutConverter"/>, and the script only checks the result and gives the drive letters back.
/// Refused on the disk that holds Windows or this program, because it could no longer boot.
/// </summary>
/// <param name="Mounts">The drive letters on the disk: they are locked during the change and given back afterwards.</param>
public sealed record ConvertPartitionStyle(
    int DiskNumber, DiskPartitionStyle Target, IReadOnlyList<DriveMount>? Mounts = null,
    ILayoutConverter? Converter = null) : DiskOperation
{
    public static string WarningFor(DiskPartitionStyle target) =>
        "Only the partition table is rewritten; the partitions and their files stay where they are. The drives are locked " +
        "while it runs, so close every program using them. " +
        (target == DiskPartitionStyle.GPT
            ? "A GPT disk boots only in UEFI mode, and the active flag and disk signature are dropped. "
            : "An MBR disk holds at most four partitions and 2 TB, and the Microsoft Reserved partition is dropped. ") +
        "Windows may change the drive letters (it tries to give them back), and anything that identifies the disk by " +
        "its partition table, such as a boot loader or a backup job, has to be set up again. If the power fails during " +
        "the few milliseconds the table is written, use Recover partitions to bring the partitions back. It cannot be " +
        "canceled once started.";

    public override string Description =>
        $"Convert disk {DiskNumber} to {Target} (only the partition table is rewritten; the files are not touched)";

    public override bool Destructive => true;
    public override bool ForbiddenOnSystemDrive => true;
    public override IReadOnlyList<char> AffectedDrives =>
        (Mounts ?? []).Select(m => char.ToUpperInvariant(m.Letter)).ToArray();
    public override string? Warning => WarningFor(Target);

    public override void Validate()
    {
        if (DiskNumber < 0)
            throw new ArgumentException("Invalid disk number.");
        if (!Enum.IsDefined(Target))
            throw new ArgumentException("Invalid partition style. Use MBR or GPT.");
        foreach (var mount in Mounts ?? [])
        {
            NormalizeLetter(mount.Letter);
            if (mount.Offset < 0)
                throw new ArgumentException("Invalid partition position.");
        }
    }

    /// <summary>Shown in simulation mode, and the part that runs after the app has rewritten the table.</summary>
    public override string GenerateScript()
    {
        Validate();
        // "E:1048576" pairs (letter and partition offset), split inside the loop: a one-element array of arrays would be flattened by PowerShell.
        string pairs = string.Join(", ", (Mounts ?? []).Select(m => $"'{NormalizeLetter(m.Letter)}:{m.Offset}'"));
        return $"Update-Disk -Number {DiskNumber}; " +
               $"if ((Get-Disk -Number {DiskNumber}).PartitionStyle -ne '{Target}') {{ throw 'Disk {DiskNumber} is not {Target} after the conversion.' }}; " +
               // Best effort: Windows gives the partitions new identities, so a letter may come back different.
               $"foreach ($m in @({pairs})) {{ try {{ " +
               $"$l, $o = $m -split ':'; $p = Get-Partition -DiskNumber {DiskNumber} | Where-Object {{ $_.Offset -eq [long]$o }}; " +
               $"if ($p -and \"$($p.DriveLetter)\" -ne $l -and -not (Get-Partition -DriveLetter $l -ErrorAction SilentlyContinue)) {{ " +
               $"if ([int]$p.DriveLetter -ne 0) {{ Set-Partition -InputObject $p -NewDriveLetter $l }} " +
               $"else {{ Add-PartitionAccessPath -InputObject $p -AccessPath (\"$($l):\\\") }} }} }} catch {{ }} }}" +
               $" # before this, the app locks the drives of disk {DiskNumber}, saves the sectors of the old table and writes " +
               $"the {Target} table (CREATE_DISK, then SET_DRIVE_LAYOUT_EX) with every partition at the same offset and size";
    }

    public override async Task<CommandResult> RunAsync(
        ICommandExecutor executor, string script, IProgress<string>? progress, CancellationToken ct)
    {
        Validate();
        ct.ThrowIfCancellationRequested();   // the last point where canceling is still safe

        var request = new LayoutConversionRequest(DiskNumber, Target, Mounts ?? []);
        var converter = Converter ?? new PhysicalLayoutConverter();
        await Task.Run(() => converter.Convert(request, progress), CancellationToken.None);

        // The new table is in place: the rest only checks it and gives the letters back, and the script comment is skipped by PowerShell.
        return await executor.ExecuteAsync(script, ct);
    }
}
