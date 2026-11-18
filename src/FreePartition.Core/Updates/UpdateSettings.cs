using System.Text.Json;
using FreePartition.Core.Operations;

namespace FreePartition.Core.Updates;

/// <summary>User choices about updates, stored in a small JSON file next to the operations log.</summary>
public sealed class UpdateSettings
{
    /// <summary>Look for a new release when the app starts. The only network call the app makes.</summary>
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>Version the user chose to skip, so the startup check does not ask about it again.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary><c>settings.json</c> next to the executable (or in %LocalAppData%\FreePartition when that is not writable).</summary>
    public static string DefaultPath { get; } = ResolveDefaultPath();

    private static string ResolveDefaultPath()
    {
        // Follow the log: wherever it could be written, settings can be written too.
        string logDir = Path.GetDirectoryName(FileLog.DefaultPath)!;
        string dir = string.Equals(Path.GetFileName(logDir), "Log", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(logDir)!
            : logDir;
        return Path.Combine(dir, "settings.json");
    }

    /// <summary>Reads the file. A missing or damaged file gives the defaults.</summary>
    public static UpdateSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path)) ?? new UpdateSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new UpdateSettings();
    }

    /// <summary>Writes the file. Returns false when it could not be written (the choice then lasts only for this run).</summary>
    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
