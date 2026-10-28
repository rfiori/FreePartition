using System.Text.RegularExpressions;

namespace FreePartition.Core.Operations;

/// <summary>
/// Windows Defender's "Controlled folder access" (ransomware protection) blocks unknown apps from writing to disks,
/// which stops "Move partition". The app cannot (and must not) change that setting; it can only detect it and tell
/// the user how to allow the app.
/// </summary>
public static class ControlledFolderAccess
{
    /// <summary>Opens the Ransomware protection page of Windows Security.</summary>
    public const string SettingsUri = "windowsdefender://ransomwareprotection";

    public const string Steps =
        "1. Open Windows Security > Virus & threat protection.\n" +
        "2. Under Ransomware protection, click Manage ransomware protection.\n" +
        "3. Click \"Allow an app through Controlled folder access\".\n" +
        "4. Click Add an allowed app > Browse all apps, and choose FreePartition.exe.\n\n" +
        "(If Windows already blocked it, Windows Security > Protection history > \"Allow on device\" does the same.)";

    // Only characters a real install path has: the path goes into a PowerShell string.
    private static readonly Regex SafePath = new(@"^[A-Za-z]:\\[\p{L}\p{N} _\-\.\\\(\)]+$", RegexOptions.Compiled);

    /// <summary>
    /// True when Controlled folder access is on and <paramref name="exePath"/> is not in its allowed apps, false when it
    /// is not in the way, null when it could not be determined (then nothing should be assumed).
    /// </summary>
    public static async Task<bool?> IsBlockingAsync(string? exePath, ICommandExecutor executor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        if (exePath is null || !SafePath.IsMatch(exePath)) return null;

        string script = "$p = Get-MpPreference; " +
                        $"if ($p.EnableControlledFolderAccess -eq 1 -and @($p.ControlledFolderAccessAllowedApplications) -notcontains '{exePath}') " +
                        "{ 'blocked' } else { 'ok' }";
        try
        {
            var result = await executor.ExecuteAsync(script, ct);
            if (!result.Success) return null;
            return result.Output.Trim() switch
            {
                "blocked" => true,
                "ok" => false,
                _ => null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
