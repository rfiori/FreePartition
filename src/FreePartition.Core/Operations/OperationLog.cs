namespace FreePartition.Core.Operations;

public interface IOperationLog
{
    void Log(string message);
}

public sealed class NullLog : IOperationLog
{
    public void Log(string message) { }
}

/// <summary>Writes one timestamped line per event to a text file.</summary>
public sealed class FileLog : IOperationLog
{
    private readonly string _path;
    private readonly object _lock = new();

    public FileLog(string path) => _path = path;

    /// <summary>
    /// <c>Log\operations.log</c> next to the executable (handy on WinPE/USB, where nothing else persists).
    /// Falls back to %LocalAppData% when that folder is not writable (for example under Program Files).
    /// </summary>
    public static string DefaultPath { get; } = ResolveDefaultPath();

    private static string ResolveDefaultPath()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Log");
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Path.Combine(dir, "operations.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FreePartition", "operations.log");
        }
    }

    public void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* logging must never bring down the operation */ }
        catch (UnauthorizedAccessException) { }
    }
}
