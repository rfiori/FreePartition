using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class ControlledFolderAccessTests
{
    private const string Exe = @"C:\Program Files\FreePartition\FreePartition.exe";

    private sealed class FakeExecutor(Func<CommandResult> response) : ICommandExecutor
    {
        public List<string> Scripts { get; } = new();

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            return Task.FromResult(response());
        }
    }

    [Fact]
    public async Task Blocked_WhenTheScriptSaysSo()
    {
        var executor = new FakeExecutor(() => new CommandResult(0, "blocked\r\n", ""));

        Assert.True(await ControlledFolderAccess.IsBlockingAsync(Exe, executor));
        Assert.Contains($"'{Exe}'", executor.Scripts[0]);
    }

    [Fact]
    public async Task NotBlocked_WhenOk()
    {
        Assert.False(await ControlledFolderAccess.IsBlockingAsync(Exe, new FakeExecutor(() => new CommandResult(0, "ok", ""))));
    }

    [Fact]
    public async Task Unknown_WhenTheQueryFailsOrIsGarbage()
    {
        Assert.Null(await ControlledFolderAccess.IsBlockingAsync(Exe, new FakeExecutor(() => new CommandResult(1, "", "no Defender"))));
        Assert.Null(await ControlledFolderAccess.IsBlockingAsync(Exe, new FakeExecutor(() => new CommandResult(0, "maybe", ""))));
        Assert.Null(await ControlledFolderAccess.IsBlockingAsync(Exe, new FakeExecutor(() => throw new IOException("boom"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\x'; calc; '\a.exe")]
    [InlineData(@"relative\a.exe")]
    public async Task UnsafePath_IsNeverPutInAScript(string? path)
    {
        var executor = new FakeExecutor(() => new CommandResult(0, "blocked", ""));

        Assert.Null(await ControlledFolderAccess.IsBlockingAsync(path, executor));
        Assert.Empty(executor.Scripts);
    }
}
