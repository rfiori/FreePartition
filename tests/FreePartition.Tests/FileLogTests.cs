using System.Text.RegularExpressions;
using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FreePartitionTests-" + Guid.NewGuid().ToString("N"));

    public FileLogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Log_WritesATimestampedLine()
    {
        string path = Path.Combine(_root, "operations.log");

        new FileLog(path).Log("Format E:");

        string line = Assert.Single(File.ReadAllLines(path));
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}  Format E:$"), line);
    }

    [Fact]
    public void Log_AppendsInsteadOfOverwriting_InOrder()
    {
        string path = Path.Combine(_root, "operations.log");
        var log = new FileLog(path);

        log.Log("first");
        log.Log("second");
        new FileLog(path).Log("third");    // a new instance keeps appending to the same file

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("first", lines[0]);
        Assert.EndsWith("second", lines[1]);
        Assert.EndsWith("third", lines[2]);
    }

    [Fact]
    public void Log_CreatesMissingParentFolders()
    {
        string path = Path.Combine(_root, "a", "b", "operations.log");

        new FileLog(path).Log("hello");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Log_WhenAParentPathIsAFile_DoesNotThrow()
    {
        string blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "i am a file");

        var ex = Record.Exception(() => new FileLog(Path.Combine(blocker, "sub", "operations.log")).Log("x"));

        Assert.Null(ex);
    }

    [Fact]
    public void Log_WhenThePathIsADirectory_DoesNotThrow()
    {
        string path = Path.Combine(_root, "iAmAFolder");
        Directory.CreateDirectory(path);

        var ex = Record.Exception(() => new FileLog(path).Log("x"));

        Assert.Null(ex);
    }

    [Fact]
    public void Log_WhenTheFileIsLockedByAnotherProcess_DoesNotThrow()
    {
        string path = Path.Combine(_root, "operations.log");
        using var locked = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        var ex = Record.Exception(() => new FileLog(path).Log("x"));

        Assert.Null(ex);
    }

    [Fact]
    public void Log_FromManyThreads_WritesEveryLineIntact()
    {
        string path = Path.Combine(_root, "operations.log");
        var log = new FileLog(path);

        Parallel.For(0, 50, i => log.Log($"message {i}"));

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(50, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"  message \d+$", l));
    }

    [Fact]
    public void DefaultPath_IsUnderLocalAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreePartition", "operations.log");

        Assert.Equal(expected, FileLog.DefaultPath);
    }

    [Fact]
    public void NullLog_DoesNothing()
    {
        var ex = Record.Exception(() => new NullLog().Log("anything"));

        Assert.Null(ex);
    }
}
