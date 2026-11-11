using Microsoft.Win32;

namespace FreePartition.Core.Cloning;

/// <summary>Tells whether the app runs inside Windows PE (the offline environment booted from a USB drive or ISO).</summary>
public static class WinPeDetector
{
    /// <summary>
    /// Windows PE always has HKLM\SYSTEM\CurrentControlSet\Control\MiniNT, which an installed Windows never has.
    /// The RAM-disk system drive (X:) is the second sign, for when the key cannot be read.
    /// </summary>
    public static bool IsWinPe() =>
        IsWinPe(MiniNtKeyExists(), Environment.SystemDirectory);

    /// <summary>The decision on its own, so tests do not need the registry.</summary>
    public static bool IsWinPe(bool miniNtKeyExists, string? systemDirectory) =>
        miniNtKeyExists
        || (!string.IsNullOrEmpty(systemDirectory)
            && char.ToUpperInvariant(systemDirectory[0]) == 'X'
            && systemDirectory.Length > 1 && systemDirectory[1] == ':'
            && systemDirectory.EndsWith(@"\Windows\System32", StringComparison.OrdinalIgnoreCase));

    private static bool MiniNtKeyExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
