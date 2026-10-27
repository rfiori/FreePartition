using System.Diagnostics;

namespace FreePartition.Core.Operations;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Abstraction of the engine that runs the scripts (lets tests swap in a fake).</summary>
public interface ICommandExecutor
{
    Task<CommandResult> ExecuteAsync(string script, CancellationToken ct);
}

/// <summary>Runs scripts in Windows PowerShell. Requires administrator.</summary>
public sealed class PowerShellExecutor : ICommandExecutor
{
    public async Task<CommandResult> ExecuteAsync(string script, CancellationToken ct)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("$ErrorActionPreference = 'Stop'; " + script);   // an error becomes exit code 1

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException("Could not start PowerShell.");

        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        return new CommandResult(process.ExitCode, await output, await error);
    }
}
