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
        var files = FindSystemFiles();
        var ssdDisks = FindSsdDisks();

        using var diskSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
        foreach (var obj in diskSearcher.Get())
        {
            using var disk = (ManagementObject)obj;

            int index = Convert.ToInt32(disk["Index"] ?? 0);
            var partitions = ListPartitions(index, volumesByPartition, files);

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
                Partitions: partitions,
                IsSsd: ssdDisks.Contains(index)));
        }

        return result.OrderBy(d => d.Index).ToList();
    }

    // Direct queries (DiskIndex / Win32_LogicalDiskToPartition) instead of ASSOCIATORS OF,
    // which fails with "Not found" on some systems because of DeviceID escaping.
    private static List<PartitionInfo> ListPartitions(int diskIndex, Dictionary<string, List<LogicalVolumeInfo>> volumesByPartition, SystemFiles files)
    {
        var list = new List<PartitionInfo>();

        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskIndex}");

        var storage = StoragePartitions(diskIndex);

        foreach (var obj in searcher.Get())
        {
            using var partition = (ManagementObject)obj;
            string partitionId = Text(partition["DeviceID"]) ?? "";
            int index = Convert.ToInt32(partition["Index"] ?? 0);
            ulong offset = Number(partition["StartingOffset"]);
            ulong size = Number(partition["Size"]);

            var volumes = volumesByPartition.GetValueOrDefault(partitionId) ?? [];
            int number = index + 1;

            storage.TryGetValue(offset, out var info);
            if (info is not null)
            {
                number = info.Number;
                // RAW partitions have a letter but no Win32_LogicalDisk, so the volume map misses them.
                if (volumes.Count == 0 && info.Letter is { } letter)
                    volumes = [new LogicalVolumeInfo($"{letter}:", null, null, size, size)];
            }

            // Like the "Status" column of Disk Management: Boot, Page File, Crash Dump, Active, partition kind.
            char? driveLetter = volumes.Count > 0 && volumes[0].Letter.Length > 0 ? char.ToUpperInvariant(volumes[0].Letter[0]) : null;
            var details = new List<string>();
            if (info?.IsBoot == true) details.Add("Boot");
            if (driveLetter is { } l && files.PageFileDrives.Contains(l)) details.Add("Page File");
            if (driveLetter is { } c && files.CrashDumpDrive == c) details.Add("Crash Dump");
            if (info?.IsActive == true) details.Add("Active");
            if (info?.Kind is { } kind) details.Add(kind);
            if (info?.IsHidden == true) details.Add("Hidden");
            if (info?.IsReadOnly == true) details.Add("Read-only");

            list.Add(new PartitionInfo(
                Index: index,
                Type: Text(partition["Type"]) ?? "Unknown",
                Offset: offset,
                Size: size,
                Boot: partition["BootPartition"] is true,
                Volumes: volumes,
                Number: number,
                Details: details));
        }

        return list.OrderBy(p => p.Offset).ToList();
    }

    /// <summary>
    /// Partition number and drive letter as the Storage cmdlets see them, keyed by starting offset.
    /// Best effort: an empty result makes the caller fall back to Index + 1.
    /// </summary>
    private static Dictionary<ulong, StoragePartition> StoragePartitions(int diskIndex)
    {
        var map = new Dictionary<ulong, StoragePartition>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT PartitionNumber, DriveLetter, Offset, IsBoot, IsActive, IsHidden, IsReadOnly, GptType, MbrType " +
                $"FROM MSFT_Partition WHERE DiskNumber = {diskIndex}"));
            foreach (var obj in searcher.Get())
            {
                using var partition = (ManagementObject)obj;
                char letter = partition["DriveLetter"] is { } raw ? (char)Convert.ToUInt16(raw) : '\0';
                map[Number(partition["Offset"])] = new StoragePartition(
                    Convert.ToInt32(partition["PartitionNumber"] ?? 0),
                    char.IsAsciiLetter(letter) ? char.ToUpperInvariant(letter) : null,
                    partition["IsBoot"] is true, partition["IsActive"] is true,
                    partition["IsHidden"] is true, partition["IsReadOnly"] is true,
                    KindOf(Text(partition["GptType"]), partition["MbrType"]));
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException or FormatException)
        {
            map.Clear();
        }
        return map;
    }

    private sealed record StoragePartition(
        int Number, char? Letter, bool IsBoot, bool IsActive, bool IsHidden, bool IsReadOnly, string? Kind);

    private static string? KindOf(string? gptType, object? mbrType)
    {
        if (gptType is not null)
        {
            return gptType.Trim('{', '}').ToLowerInvariant() switch
            {
                "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "EFI System Partition",
                "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Reserved Partition",
                "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Basic Data Partition",
                "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Recovery Partition",
                _ => "Other Partition"
            };
        }
        return mbrType is null ? null : "Primary Partition";
    }

    /// <summary>Numbers of the physical disks that are SSDs (MSFT_PhysicalDisk.MediaType 4; best effort).</summary>
    private static HashSet<int> FindSsdDisks()
    {
        var ssds = new HashSet<int>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"));
            foreach (var obj in searcher.Get())
            {
                using var disk = (ManagementObject)obj;
                if (Convert.ToInt32(disk["MediaType"] ?? 0) == 4 && int.TryParse(Text(disk["DeviceId"]), out int number))
                    ssds.Add(number);
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException or FormatException)
        {
            ssds.Clear();
        }
        return ssds;
    }

    private sealed record SystemFiles(HashSet<char> PageFileDrives, char? CrashDumpDrive);

    /// <summary>Drives that hold a page file and the crash dump (read-only; best effort).</summary>
    private static SystemFiles FindSystemFiles()
    {
        var pageFiles = new HashSet<char>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
            foreach (var obj in searcher.Get())
            {
                using var usage = (ManagementObject)obj;
                if (Text(usage["Name"]) is { Length: > 1 } name && char.IsAsciiLetter(name[0]))
                    pageFiles.Add(char.ToUpperInvariant(name[0]));
            }
        }
        catch (ManagementException) { }

        // The dump file (%SystemRoot%\MEMORY.DMP by default) lives on the Windows drive when dumps are enabled.
        char? crashDump = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
            string? dumpFile = Environment.ExpandEnvironmentVariables(key?.GetValue("DumpFile") as string ?? "");
            if (key?.GetValue("CrashDumpEnabled") is int enabled && enabled != 0 && dumpFile.Length > 1 && char.IsAsciiLetter(dumpFile[0]))
                crashDump = char.ToUpperInvariant(dumpFile[0]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }

        return new SystemFiles(pageFiles, crashDump);
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
