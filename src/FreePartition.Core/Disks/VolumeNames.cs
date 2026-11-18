using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FreePartition.Core.Disks;

/// <summary>
/// Names of volumes. A volume is identified by its drive letter ("D") or, when it has none, by its volume GUID path
/// ("\\?\Volume{guid}\"). Both open the same raw volume device, so the data is what Windows sees: a BitLocker volume that
/// is unlocked reads as plain text, unlike the physical disk.
/// </summary>
public static partial class VolumeNames
{
    [GeneratedRegex(@"^\\\\[?.]\\Volume\{([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\}\\?$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidPathPattern();

    /// <summary>The canonical "\\?\Volume{guid}\" of a volume GUID path (with or without the final backslash), or null when it is not one.</summary>
    public static string? NormalizeGuidPath(string? text)
    {
        var match = GuidPathPattern().Match((text ?? "").Trim());
        return match.Success ? $@"\\?\Volume{{{match.Groups[1].Value.ToLowerInvariant()}}}\" : null;
    }

    public static bool IsGuidPath(string? text) => NormalizeGuidPath(text) is not null;

    /// <summary>The volume as a message names it: "D:" for a letter, "the selected volume" for a GUID path.</summary>
    public static string Describe(string volume) => IsGuidPath(volume) ? "the selected volume" : $"{volume}:";

    /// <summary>
    /// Whether <paramref name="folder"/> is on <paramref name="volume"/> (a letter or GUID path), so nothing is written
    /// where the data to recover lives. When the volume of a folder cannot be told, it counts as being on it.
    /// </summary>
    /// <param name="volumeOf">Volume GUID path of a folder; replaceable for tests.</param>
    public static bool IsOnVolume(string folder, string volume, Func<string, string?>? volumeOf = null)
    {
        if (NormalizeGuidPath(volume) is not { } guid)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? "";
            return root.StartsWith(volume + ":", StringComparison.OrdinalIgnoreCase);
        }

        string? actual = NormalizeGuidPath((volumeOf ?? VolumeOfFolder)(folder));
        return actual is null || string.Equals(actual, guid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Size of the volume, or 0 when Windows does not say.</summary>
    public static long TotalBytes(string volume)
    {
        if (NormalizeGuidPath(volume) is not { } guid) return 0;
        return GetDiskFreeSpaceEx(guid, out _, out ulong total, out _) && total <= long.MaxValue ? (long)total : 0;
    }

    /// <summary>The volume a folder is on (following folder mount points), as a GUID path; null when Windows does not say.</summary>
    public static string? VolumeOfFolder(string folder)
    {
        try
        {
            string path = Path.GetFullPath(folder);
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
                path = parent;

            var mountPoint = new char[261];
            if (!GetVolumePathName(path, mountPoint, mountPoint.Length)) return null;

            var name = new char[64];
            return GetVolumeNameForVolumeMountPoint(new string(mountPoint).TrimEnd('\0'), name, name.Length)
                ? new string(name).TrimEnd('\0')
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, [Out] char[] volumePathName, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);
}
