using System.Management;

namespace FreePartition.Core.Disks;

/// <summary>
/// Lists disks, partitions and volumes using WMI (read-only).
/// Changes (create/resize/format) go through the operation queue and PowerShell.
/// </summary>
public static class DiskService
{
    public static IReadOnlyList<DiskInfo> ListDisks()
    {
        var result = new List<DiskInfo>();
        var volumesByPartition = MapVolumes();

        using var diskSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
        foreach (var obj in diskSearcher.Get())
        {
            using var disk = (ManagementObject)obj;

            int index = Convert.ToInt32(disk["Index"] ?? 0);
            var partitions = ListPartitions(index, volumesByPartition);

            // Win32_DiskPartition.Type starts with "GPT:" on GPT disks.
            string style = partitions.Count == 0
                ? "Uninitialized"
                : partitions.Any(p => p.Type.StartsWith("GPT", StringComparison.OrdinalIgnoreCase)) ? "GPT" : "MBR";

            result.Add(new DiskInfo(
                Index: index,
                Model: Text(disk["Model"]) ?? "Unknown disk",
                Size: Number(disk["Size"]),
                MediaType: Text(disk["MediaType"]),
                Interface: Text(disk["InterfaceType"]),
                Status: Text(disk["Status"]),
                PartitionStyle: style,
                Partitions: partitions));
        }

        return result.OrderBy(d => d.Index).ToList();
    }

    // Direct queries (DiskIndex / Win32_LogicalDiskToPartition) instead of ASSOCIATORS OF,
    // which fails with "Not found" on some systems because of DeviceID escaping.
    private static List<PartitionInfo> ListPartitions(int diskIndex, Dictionary<string, List<LogicalVolumeInfo>> volumesByPartition)
    {
        var list = new List<PartitionInfo>();

        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskIndex}");

        foreach (var obj in searcher.Get())
        {
            using var partition = (ManagementObject)obj;
            string partitionId = Text(partition["DeviceID"]) ?? "";

            list.Add(new PartitionInfo(
                Index: Convert.ToInt32(partition["Index"] ?? 0),
                Type: Text(partition["Type"]) ?? "Unknown",
                Offset: Number(partition["StartingOffset"]),
                Size: Number(partition["Size"]),
                Boot: partition["BootPartition"] is true,
                Volumes: volumesByPartition.GetValueOrDefault(partitionId) ?? []));
        }

        return list.OrderBy(p => p.Offset).ToList();
    }

    /// <summary>Partition DeviceID (e.g. "Disk #0, Partition #1") → logical volumes on it.</summary>
    private static Dictionary<string, List<LogicalVolumeInfo>> MapVolumes()
    {
        var logicalDisks = new Dictionary<string, LogicalVolumeInfo>(StringComparer.OrdinalIgnoreCase);
        using (var logicalSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDisk"))
        {
            foreach (var obj in logicalSearcher.Get())
            {
                using var volume = (ManagementObject)obj;
                string letter = Text(volume["DeviceID"]) ?? "?";
                logicalDisks[letter] = new LogicalVolumeInfo(
                    Letter: letter,
                    Label: Text(volume["VolumeName"]),
                    FileSystem: Text(volume["FileSystem"]),
                    Size: Number(volume["Size"]),
                    Free: Number(volume["FreeSpace"]));
            }
        }

        var map = new Dictionary<string, List<LogicalVolumeInfo>>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition");
        foreach (var obj in searcher.Get())
        {
            using var link = (ManagementObject)obj;
            string? partitionId = ExtractDeviceId(Text(link["Antecedent"]));
            string? letter = ExtractDeviceId(Text(link["Dependent"]));
            if (partitionId is null || letter is null || !logicalDisks.TryGetValue(letter, out var info))
                continue;

            if (!map.TryGetValue(partitionId, out var list))
                map[partitionId] = list = [];
            list.Add(info);
        }

        return map;
    }

    /// <summary>Extracts the DeviceID value from a WMI object path (…DeviceID="value").</summary>
    private static string? ExtractDeviceId(string? path)
    {
        if (path is null) return null;
        int start = path.IndexOf('"');
        int end = path.LastIndexOf('"');
        return start >= 0 && end > start ? path[(start + 1)..end] : null;
    }

    private static string? Text(object? value)
    {
        var s = value?.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static ulong Number(object? value) =>
        value is null ? 0UL : Convert.ToUInt64(value);
}
