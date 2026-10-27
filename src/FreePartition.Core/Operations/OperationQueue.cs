namespace FreePartition.Core.Operations;

public sealed record OperationResult(DiskOperation Operation, bool Success, string Detail);

/// <summary>
/// Queue of pending operations: the user builds the list, reviews it, and only then clicks "Apply".
/// "Undo" here means removing from the queue before applying (nothing has touched the disk yet).
/// Use on the UI thread: list changes happen in the context that called ApplyAsync.
/// </summary>
public sealed class OperationQueue
{
    private readonly List<DiskOperation> _pending = new();
    private readonly char? _protectedDrive;
    private readonly IOperationLog _log;

    public event EventHandler? Changed;

    /// <param name="protectedDrive">Windows drive (e.g. 'C'): operations forbidden on it are refused.</param>
    public OperationQueue(char? protectedDrive = null, IOperationLog? log = null)
    {
        _protectedDrive = protectedDrive is { } u ? char.ToUpperInvariant(u) : null;
        _log = log ?? new NullLog();
    }

    /// <summary>Copy of the current list (safe to enumerate while the queue changes).</summary>
    public IReadOnlyList<DiskOperation> Pending => _pending.ToList();

    public void Add(DiskOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.Validate();
        Check(operation);

        _pending.Add(operation);
        _log.Log($"QUEUED: {operation.Description}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(DiskOperation operation)
    {
        bool removed = _pending.Remove(operation);
        if (removed)
        {
            _log.Log($"REMOVED FROM QUEUE: {operation.Description}");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    /// <summary>Discards all pending operations.</summary>
    public void Clear()
    {
        if (_pending.Count == 0) return;
        _pending.Clear();
        _log.Log("QUEUE CLEARED (all pending operations were discarded)");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs the operations in order. Stops at the first failure (the rest stay pending).
    /// In simulation mode nothing is executed: it only shows the commands, and the queue stays intact.
    /// </summary>
    public async Task<IReadOnlyList<OperationResult>> ApplyAsync(
        ICommandExecutor executor,
        bool simulation = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var results = new List<OperationResult>();

        foreach (var operation in _pending.ToList())
        {
            ct.ThrowIfCancellationRequested();

            string script;
            try
            {
                operation.Validate();
                Check(operation);
                script = operation.GenerateScript();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                results.Add(new OperationResult(operation, false, ex.Message));
                _log.Log($"REFUSED: {operation.Description} :: {ex.Message}");
                progress?.Report($"✖ {operation.Description}: {ex.Message}");
                break;
            }

            if (simulation)
            {
                _log.Log($"SIMULATION: {operation.Description} :: {script}");
                progress?.Report($"[simulation] {operation.Description}{Environment.NewLine}    {script}");
                results.Add(new OperationResult(operation, true, script));
                continue;
            }

            _log.Log($"STARTING: {operation.Description} :: {script}");
            progress?.Report($"Running: {operation.Description}...");

            CommandResult result;
            try
            {
                result = await executor.ExecuteAsync(script, ct);
            }
            catch (OperationCanceledException)
            {
                _log.Log($"CANCELED: {operation.Description}");
                throw;
            }
            catch (Exception ex)
            {
                result = new CommandResult(-1, "", ex.Message);
            }

            if (result.Success)
            {
                _pending.Remove(operation);
                _log.Log($"COMPLETED: {operation.Description}");
                progress?.Report($"✔ {operation.Description}");
                if (operation.ShowsOutput && !string.IsNullOrWhiteSpace(result.Output))
                    progress?.Report("    " + result.Output.Trim().ReplaceLineEndings(Environment.NewLine + "    "));
                results.Add(new OperationResult(operation, true, result.Output.Trim()));
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                string error = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
                _log.Log($"FAILED: {operation.Description} :: {error}");
                progress?.Report($"✖ {operation.Description}: {error}");
                results.Add(new OperationResult(operation, false, error));
                break;   // do not continue after a failure
            }
        }

        return results;
    }

    private void Check(DiskOperation operation)
    {
        if (_protectedDrive is { } protectedDrive
            && operation.ForbiddenOnSystemDrive
            && operation.AffectedDrives.Contains(protectedDrive))
        {
            throw new InvalidOperationException(
                $"This operation is not allowed on the system drive ({protectedDrive}:).");
        }
    }
}
