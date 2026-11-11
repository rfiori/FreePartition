using FreePartition.Core.Disks;

namespace FreePartition.Core.Cloning;

/// <summary>
/// An installed Windows found on a disk: its partition and the ones that belong to it (the boot partition and the
/// recovery partition). <see cref="Letter"/> is the drive letter the partition has right now (in WinPE, usually not C:).
/// </summary>
public sealed record WindowsInstall(
    DiskInfo Disk, PartitionInfo Windows, char Letter, PartitionInfo? Boot, PartitionInfo? Recovery, bool HasMsr)
{
    /// <summary>GPT disks boot through UEFI and an EFI system partition; MBR disks through the BIOS and an active partition.</summary>
    public bool Uefi => string.Equals(Disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase);

    public string Description =>
        $"Windows on {Letter}: — disk {Disk.Index} ({Disk.Model}), {Util.ByteFormatter.Format((long)Windows.Size)}, " +
        $"{(Uefi ? "UEFI/GPT" : "BIOS/MBR")}";
}

/// <summary>Looks for installed Windows copies among the disks the app already listed (no disk is read).</summary>
public static class WindowsInstallFinder
{
    public const string RegistryHive = @"Windows\System32\config\SYSTEM";

    /// <param name="runningLetter">The drive the app itself runs from (X: in WinPE): never a source.</param>
    /// <param name="fileExists">Only replaced by tests.</param>
    public static IReadOnlyList<WindowsInstall> Find(
        IReadOnlyList<DiskInfo> disks, char? runningLetter = null, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var found = new List<WindowsInstall>();

        foreach (var disk in disks)
        {
            foreach (var partition in disk.Partitions)
            {
                foreach (var volume in partition.Volumes)
                {
                    if (volume.Letter is not { Length: > 0 } text || !char.IsAsciiLetter(text[0])) continue;
                    char letter = char.ToUpperInvariant(text[0]);
                    if (letter == runningLetter) continue;
                    if (!fileExists($@"{letter}:\{RegistryHive}")) continue;

                    found.Add(new WindowsInstall(disk, partition, letter,
                        FindBoot(disk, partition), FindRecovery(disk, partition),
                        HasMsr: disk.Reserved is { Count: > 0 }));
                    break;   // one Windows per partition
                }
            }
        }
        return found;
    }

    private static bool Has(PartitionInfo partition, string detail) =>
        partition.Details?.Contains(detail, StringComparer.OrdinalIgnoreCase) == true;

    private static PartitionInfo? FindBoot(DiskInfo disk, PartitionInfo windows)
    {
        bool gpt = string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase);
        return disk.Partitions.FirstOrDefault(p => p.Offset != windows.Offset
            && (gpt ? Has(p, "EFI System Partition") : Has(p, "Active")));
    }

    private static PartitionInfo? FindRecovery(DiskInfo disk, PartitionInfo windows)
    {
        var recoveries = disk.Partitions.Where(p => p.Offset != windows.Offset && Has(p, "Recovery Partition")).ToList();
        return recoveries.FirstOrDefault(p => p.Offset > windows.Offset) ?? recoveries.FirstOrDefault();
    }
}
