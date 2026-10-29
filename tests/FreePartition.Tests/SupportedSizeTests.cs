using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class SupportedSizeTests
{
    private sealed class FakeExecutor(Func<string, CommandResult> response) : ICommandExecutor
    {
        public List<string> Scripts { get; } = new();

        public Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            return Task.FromResult(response(script));
        }
    }

    [Fact]
    public void Parse_ValidOutput_ReturnsMinAndMax()
    {
        var size = SupportedSize.Parse("1048576 107374182400");

        Assert.Equal(new SupportedSize(1048576, 107374182400), size);
    }

    [Theory]
    [InlineData("  100   200  ")]
    [InlineData("100\t200")]
    [InlineData("100\r\n200\r\n")]
    public void Parse_AcceptsAnyWhitespaceBetweenAndAround(string output)
    {
        Assert.Equal(new SupportedSize(100, 200), SupportedSize.Parse(output));
    }

    [Fact]
    public void Parse_MinEqualToMax_IsValid() =>
        Assert.Equal(new SupportedSize(500, 500), SupportedSize.Parse("500 500"));

    [Fact]
    public void Parse_HandlesValuesBeyondInt32() =>
        Assert.Equal(new SupportedSize(5_000_000_000, 10_000_000_000_000), SupportedSize.Parse("5000000000 10000000000000"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("100")]                // one value
    [InlineData("100 200 300")]        // three values
    [InlineData("abc def")]
    [InlineData("100 abc")]
    [InlineData("-100 200")]           // negative
    [InlineData("100 -200")]
    [InlineData("0 200")]              // min must be positive
    [InlineData("300 200")]            // max below min
    [InlineData("1.5 2.5")]            // not integers
    [InlineData("1,000 2,000")]
    [InlineData("99999999999999999999 99999999999999999999")]    // overflow
    public void Parse_InvalidOutput_ReturnsNull(string output) =>
        Assert.Null(SupportedSize.Parse(output));

    [Fact]
    public async Task QueryAsync_Success_ParsesTheOutput_AndUsesTheUppercaseDriveLetter()
    {
        var executor = new FakeExecutor(_ => new CommandResult(0, "100 200", ""));

        var size = await SupportedSize.QueryAsync('e', executor);

        Assert.Equal(new SupportedSize(100, 200), size);
        var script = Assert.Single(executor.Scripts);
        Assert.Contains("-DriveLetter E", script);
    }

    [Fact]
    public async Task QueryAsync_WhenTheCommandFails_ReturnsNull()
    {
        var executor = new FakeExecutor(_ => new CommandResult(1, "", "boom"));

        Assert.Null(await SupportedSize.QueryAsync('E', executor));
    }

    [Fact]
    public async Task QueryAsync_WhenTheExecutorThrows_ReturnsNull()
    {
        var executor = new FakeExecutor(_ => throw new InvalidOperationException("no powershell"));

        Assert.Null(await SupportedSize.QueryAsync('E', executor));
    }

    [Theory]
    [InlineData('1')]
    [InlineData('$')]
    [InlineData(';')]
    [InlineData(' ')]
    public async Task QueryAsync_WithANonLetterDrive_ReturnsNull_AndRunsNothing(char drive)
    {
        var executor = new FakeExecutor(_ => new CommandResult(0, "100 200", ""));

        Assert.Null(await SupportedSize.QueryAsync(drive, executor));
        Assert.Empty(executor.Scripts);
    }

    [Fact]
    public async Task QueryAsync_WhenCanceled_PropagatesTheCancellation()
    {
        var executor = new FakeExecutor(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => SupportedSize.QueryAsync('E', executor));
    }
}
