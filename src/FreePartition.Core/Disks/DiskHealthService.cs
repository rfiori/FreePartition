using System.Management;

namespace FreePartition.Core.Disks;

/// <summary>
/// Reads disk health (read-only). ATA S.M.A.R.T. tables come from the <c>root\wmi</c> storage driver
/// classes; NVMe drives are asked for their health log page directly; health status and the
/// reliability counters come from the Storage Management classes. Every source is optional:
/// a missing one (USB bridge, RAID controller) just leaves its fields empty, and virtual disks
/// (VHD/VHDX) have no S.M.A.R.T. at all.
/// </summary>
public static class DiskHealthService
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";
    private const string DriverScope = @"root\wmi";

    public static IReadOnlyList<DiskHealth> ReadAll()
    {
        var predict = QueryByInstance("MSStorageDriver_FailurePredictStatus", "PredictFailure");
        var smart = QueryByInstance("MSStorageDriver_ATAPISmartData", "VendorSpecific");
        var thresholds = QueryByInstance("MSStorageDriver_FailurePredictThresholds", "VendorSpecific");
        var physical = QueryPhysicalDisks();

        var result = new List<DiskHealth>();
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
        foreach (var obj in searcher.Get())
        {
            using var disk = (ManagementObject)obj;
            int index = Convert.ToInt32(disk["Index"] ?? 0);
            string model = disk["Model"]?.ToString() ?? "Unknown disk";
            string pnp = disk["PNPDeviceID"]?.ToString() ?? "";

            physical.TryGetValue(index, out var info);

            if (IsVirtual(pnp, info?.BusType))
            {
                result.Add(new DiskHealth(index, model, HealthLevel.Unknown,
                    "This is a virtual disk (VHD/VHDX): it has no S.M.A.R.T. data. Check the disk that stores the file instead.",
                    info?.HealthStatus, null, null, null, null, [], [],
                    SmartUnavailable: "S.M.A.R.T. data is not available: this is a virtual disk."));
                continue;
            }

            // Driver instance names are the PnP device id plus a "_0" suffix.
            object? predictValue = Find(predict, pnp);
            var attributes = Find(smart, pnp) is byte[] table
                ? SmartParser.ParseAttributes(table, Find(thresholds, pnp) as byte[] ?? [])
                : [];

            var nvme = NvmeReader.TryRead(index);
            var counters = info?.Counters;

            int? temperature = nvme is { TemperatureCelsius: > 0 } ? nvme.TemperatureCelsius
                : counters?.Temperature
                ?? (attributes.FirstOrDefault(a => a.Id is 194 or 190) is { } t ? (int)(t.Raw & 0xFF) : null);
            long? hours = nvme is not null ? (long)nvme.PowerOnHours
                : counters?.PowerOnHours
                ?? (attributes.FirstOrDefault(a => a.Id == 9) is { } h ? (long)h.Raw : null);
            int? wear = nvme?.PercentageUsed ?? counters?.Wear;
            bool? predictFailure = predictValue is bool b ? b : null;

            var (level, summary) = SmartParser.Evaluate(info?.HealthStatus, predictFailure, temperature, wear, attributes, nvme);
            string? unavailable = attributes.Count > 0 || nvme is not null
                ? null
                : "Could not read the S.M.A.R.T. data of this drive. The drive, its USB bridge or its RAID "
                  + "controller may not expose it, or the app is not running as administrator."
                  + (info?.HealthStatus is null ? "" : " Only the overall status reported by Windows is shown.");
            result.Add(new DiskHealth(index, model, level, summary, info?.HealthStatus, predictFailure,
                temperature, hours, wear, attributes, nvme is null ? [] : NvmeDetails(nvme), unavailable));
        }

        return result.OrderBy(d => d.DiskIndex).ToList();
    }

    private static bool IsVirtual(string pnpDeviceId, int? busType) =>
        busType == 15 // File-backed virtual
        || pnpDeviceId.Contains("VEN_MSFT&PROD_VIRTUAL_DISK", StringComparison.OrdinalIgnoreCase);

    private static List<KeyValuePair<string, string>> NvmeDetails(NvmeHealth n) =>
    [
        new("Critical warning", n.CriticalWarning == 0 ? "None" : $"0x{n.CriticalWarning:X2}"),
        new("Available spare", $"{n.AvailableSparePercent}% (threshold {n.SpareThresholdPercent}%)"),
        new("Percentage used", $"{n.PercentageUsed}%"),
        new("Data read", $"{NvmeHealth.UnitsToTerabytes(n.DataUnitsRead):N2} TB"),
        new("Data written", $"{NvmeHealth.UnitsToTerabytes(n.DataUnitsWritten):N2} TB"),
        new("Power cycles", n.PowerCycles.ToString()),
        new("Power-on hours", n.PowerOnHours.ToString()),
        new("Unsafe shutdowns", n.UnsafeShutdowns.ToString()),
        new("Media errors", n.MediaErrors.ToString()),
    ];

    private sealed record Counters(int? Temperature, long? PowerOnHours, int? Wear);

    private sealed record PhysicalInfo(string? HealthStatus, int? BusType, Counters? Counters);

    private static object? Find(Dictionary<string, object?> byInstance, string pnpDeviceId)
    {
        if (pnpDeviceId.Length == 0) return null;
        foreach (var (instance, value) in byInstance)
            if (instance.StartsWith(pnpDeviceId, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    private static Dictionary<string, object?> QueryByInstance(string className, string property)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(DriverScope, $"SELECT * FROM {className}");
            foreach (var obj in searcher.Get())
            {
                using var item = (ManagementObject)obj;
                if (item["InstanceName"]?.ToString() is { } name)
                    map[name] = item[property];
            }
        }
        catch (ManagementException) { /* class not provided by this system's drivers (e.g. NVMe) */ }
        catch (UnauthorizedAccessException) { }
        return map;
    }

    /// <summary>Disk number → health status, bus type and reliability counters.</summary>
    private static Dictionary<int, PhysicalInfo> QueryPhysicalDisks()
    {
        var map = new Dictionary<int, PhysicalInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageScope, "SELECT * FROM MSFT_PhysicalDisk");
            foreach (var obj in searcher.Get())
            {
                using var item = (ManagementObject)obj;
                if (!int.TryParse(item["DeviceId"]?.ToString(), out int number)) continue;

                string? status = item["HealthStatus"] is { } code
                    ? Convert.ToInt32(code) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null }
                    : null;
                int? bus = item["BusType"] is { } b ? Convert.ToInt32(b) : null;
                map[number] = new PhysicalInfo(status, bus, ReadCounters(item));
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        return map;
    }

    // MSFT_StorageReliabilityCounter cannot be enumerated on its own: it is only reachable
    // through its association with the physical disk.
    private static Counters? ReadCounters(ManagementObject physicalDisk)
    {
        try
        {
            using var related = physicalDisk.GetRelated("MSFT_StorageReliabilityCounter");
            foreach (var obj in related)
            {
                using var item = (ManagementObject)obj;
                return new Counters(
                    Temperature: PositiveOrNull(item["Temperature"]) is { } t ? (int)t : null,
                    PowerOnHours: PositiveOrNull(item["PowerOnHours"]),
                    Wear: item["Wear"] is { } w ? Convert.ToInt32(w) : null);
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static long? PositiveOrNull(object? value) =>
        value is not null && Convert.ToInt64(value) is > 0 and var n ? n : null;
}
