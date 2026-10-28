namespace FreePartition.Core.Disks;

/// <summary>A slice of the used space of a volume (for the usage bar of the properties window).</summary>
public sealed record UsageSegment(string Name, ulong Bytes);

/// <summary>
/// Estimates how the used space of a drive that holds Windows is split: operating system, programs and data,
/// users, and everything else. Read-only. The sizes are the sum of file sizes (not the space they take on the disk),
/// and files that exist under several names (WinSxS hard links) are counted once per name, so the result is an
/// approximation: it is clamped so the pieces never add up to more than the used space.
/// </summary>
public static class VolumeUsageScanner
{
    public const string OperatingSystem = "Operating system";
    public const string Programs = "Programs and program data";
    public const string Users = "Users";
    public const string Other = "Other files and metadata";

    private static readonly string[] SystemFiles = ["pagefile.sys", "hiberfil.sys", "swapfile.sys"];
    private static readonly string[] ProgramFolders = ["Program Files", "Program Files (x86)", "ProgramData"];

    /// <summary>True when the drive holds a Windows installation (it has \Windows\System32).</summary>
    public static bool LooksLikeWindows(string root) =>
        Directory.Exists(Path.Combine(root, "Windows", "System32"));

    /// <summary>
    /// Splits <paramref name="used"/> into segments. A piece that is still being measured is passed as 0 and ends up
    /// in "Other". Pieces are clamped in order so the total is never more than <paramref name="used"/>.
    /// Segments with no bytes are left out.
    /// </summary>
    public static IReadOnlyList<UsageSegment> Compose(ulong used, ulong os, ulong programs, ulong users)
    {
        var segments = new List<UsageSegment>();
        ulong remaining = used;

        void Take(string name, ulong bytes)
        {
            ulong part = Math.Min(bytes, remaining);
            remaining -= part;
            if (part > 0) segments.Add(new UsageSegment(name, part));
        }

        Take(OperatingSystem, os);
        Take(Programs, programs);
        Take(Users, users);
        Take(Other, remaining);
        return segments;
    }

    /// <summary>
    /// Measures the pieces in parallel. <paramref name="progress"/> gets a new composition every time one finishes
    /// (the ones still running count as "Other" until then).
    /// </summary>
    public static async Task<IReadOnlyList<UsageSegment>> ScanAsync(
        string root, ulong usedBytes, IProgress<IReadOnlyList<UsageSegment>>? progress = null, CancellationToken ct = default)
    {
        ulong os = 0, programs = 0, users = 0;
        var gate = new object();

        void Publish(Action update)
        {
            IReadOnlyList<UsageSegment> snapshot;
            lock (gate)
            {
                update();
                snapshot = Compose(usedBytes, os, programs, users);
            }
            progress?.Report(snapshot);
        }

        await Task.WhenAll(
            Task.Run(() =>
            {
                ulong bytes = FolderSize(Path.Combine(root, "Windows"), ct) + SystemFilesSize(root);
                Publish(() => os = bytes);
            }, ct),
            Task.Run(() =>
            {
                ulong bytes = 0;
                foreach (string folder in ProgramFolders) bytes += FolderSize(Path.Combine(root, folder), ct);
                Publish(() => programs = bytes);
            }, ct),
            Task.Run(() =>
            {
                ulong bytes = FolderSize(Path.Combine(root, "Users"), ct);
                Publish(() => users = bytes);
            }, ct));

        lock (gate) return Compose(usedBytes, os, programs, users);
    }

    /// <summary>Sum of the sizes of the files under a folder. Skips junctions and symbolic links (no loops, no double counting).</summary>
    public static ulong FolderSize(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return 0;

        // The defaults skip hidden and system files, which is where most of Windows lives: ask for them.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        ulong total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { total += (ulong)file.Length; }
                catch (IOException) { /* vanished while scanning */ }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Whatever was counted so far is still a valid lower bound.
        }
        return total;
    }

    private static ulong SystemFilesSize(string root)
    {
        ulong total = 0;
        foreach (string name in SystemFiles)
        {
            try
            {
                var file = new FileInfo(Path.Combine(root, name));
                if (file.Exists) total += (ulong)file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }
}
