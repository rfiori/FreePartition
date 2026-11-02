using System.Diagnostics;
using FreePartition.Core.Disks;

namespace FreePartition.Core.Ntfs;

/// <summary>
/// Scans the MFT of an NTFS volume looking for records marked as "not in use"
/// (deleted files). Read-only: nothing is written to the volume.
/// </summary>
public sealed class DeletedFileScanner
{
    private const long RootRecord = 5;
    private const long BitmapRecord = 6;
    private const int RecordsPerBlock = 1024;
    private const long BitmapByteLimit = 1L << 30;   // 1 GiB of bitmap = gigantic volume

    public Task<ScanResult> ScanAsync(
        string drive,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(drive, progress, ct), ct);

    private static ScanResult Scan(string drive, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var volume = RawVolume.Open(drive);

        // 1) Boot sector
        var boot = NtfsBootSector.Read(ReadBytes(volume, 0, 512));
        int recordSize = boot.MftRecordSize;

        // 2) Record 0 ($MFT): its data runs say where the whole MFT is (it may be fragmented)
        var buffer0 = ReadBytes(volume, boot.MftStartCluster * boot.BytesPerCluster, recordSize);
        var mft0 = MftParser.Parse(buffer0, 0)
                   ?? throw new InvalidDataException("Invalid or corrupted $MFT record.");
        if (mft0.Runs.Count == 0)
            throw new InvalidDataException("Could not locate the MFT data.");

        var mftReader = new RunReader(volume, mft0.Runs, boot.BytesPerCluster, mft0.RealSize);
        long totalRecords = mftReader.Size / recordSize;

        // 3) $Bitmap: says which clusters are free (used to estimate the recovery chance)
        byte[]? bitmap = LoadBitmap(volume, mftReader, boot);

        // 4) Sequential MFT scan
        var directories = new Dictionary<long, (long Parent, string Name)>();
        var deleted = new List<MftRecord>();
        var block = new byte[RecordsPerBlock * recordSize];
        long analyzed = 0;

        for (long start = 0; start < totalRecords; start += RecordsPerBlock)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(RecordsPerBlock, totalRecords - start);
            int bytesRead = mftReader.Read(start * recordSize, block.AsSpan(0, count * recordSize));
            int complete = bytesRead / recordSize;

            for (int j = 0; j < complete; j++)
            {
                var record = MftParser.Parse(
                    block.AsSpan(j * recordSize, recordSize), start + j, includeInUseFiles: false);

                if (record?.Name is null) continue;

                if (record.IsDirectory)
                {
                    // Folders (in use or deleted) are used to build the file paths.
                    if (record.InUse) directories[record.Number] = (record.ParentNumber, record.Name);
                    else directories.TryAdd(record.Number, (record.ParentNumber, record.Name));
                }
                else if (!record.InUse)
                {
                    deleted.Add(record);
                }
            }

            analyzed += complete;
            progress?.Report(new ScanProgress(analyzed, totalRecords, deleted.Count));
            if (complete == 0) break;   // read failed: avoids an infinite loop
        }

        // 5) Final result: full path + recovery chance
        var files = new List<DeletedFile>(deleted.Count);
        foreach (var record in deleted)
        {
            var (chance, fraction) = EvaluateChance(record, bitmap, boot.TotalClusters);
            files.Add(new DeletedFile(
                MftRecordNumber: record.Number,
                Name: record.Name!,
                Path: ResolvePath(record.ParentNumber, directories),
                Size: record.RealSize,
                Modified: record.Modified,
                Chance: chance,
                FreeClusterFraction: fraction,
                Runs: record.Runs,
                ResidentData: record.ResidentData));
        }

        // 6) Free clusters that hold nothing but zeros were wiped: say so instead of promising a recovery
        MarkOverwritten(volume, files, boot.BytesPerCluster, analyzed, totalRecords, progress, ct);

        stopwatch.Stop();
        return new ScanResult(volume.Drive, boot.BytesPerCluster, analyzed, stopwatch.Elapsed, files);
    }

    /// <summary>
    /// Looks at the start, middle and end of every file that has clusters to read and marks those that are all zeros
    /// as <see cref="RecoveryChance.Overwritten"/>. The files are visited in disk order, which keeps the number of
    /// seeks low on a hard disk. Read-only.
    /// </summary>
    private static void MarkOverwritten(RawVolume volume, List<DeletedFile> files, int bytesPerCluster,
        long analyzed, long totalRecords, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var candidates = Enumerable.Range(0, files.Count)
            .Where(i => files[i] is { ResidentData: null, Size: > 0, Runs.Count: > 0, Chance: not RecoveryChance.Unavailable })
            .OrderBy(i => FirstLcn(files[i].Runs))
            .ToList();

        int done = 0;
        foreach (int i in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var file = files[i];
            var reader = new RunReader(volume, file.Runs, bytesPerCluster, file.Size);
            if (OverwriteCheck.IsZeroFilled(reader.Read, file.Size))
                files[i] = file with { Chance = RecoveryChance.Overwritten };

            done++;
            if (done % 128 == 0 || done == candidates.Count)
                progress?.Report(new ScanProgress(analyzed, totalRecords, files.Count,
                    $"Checking whether the data was overwritten... {done:N0} of {candidates.Count:N0}"));
        }
    }

    private static long FirstLcn(IReadOnlyList<DataRun> runs)
    {
        foreach (var run in runs)
        {
            if (!run.IsSparse) return run.Lcn;
        }
        return long.MaxValue;
    }

    private static byte[]? LoadBitmap(RawVolume volume, RunReader mftReader, NtfsBootSector boot)
    {
        int size = boot.MftRecordSize;
        var buffer = new byte[size];
        if (mftReader.Read(BitmapRecord * size, buffer) < size) return null;

        var record = MftParser.Parse(buffer, BitmapRecord);
        if (record is null || record.Runs.Count == 0 || record.RealSize <= 0 || record.RealSize > BitmapByteLimit)
            return null;

        var reader = new RunReader(volume, record.Runs, boot.BytesPerCluster, record.RealSize);
        var bitmap = new byte[record.RealSize];
        int bytesRead = reader.Read(0, bitmap);
        return bytesRead == bitmap.Length ? bitmap : null;
    }

    private static (RecoveryChance, double) EvaluateChance(MftRecord record, byte[]? bitmap, long totalClusters)
    {
        // Small enough that the content is inside the MFT record itself.
        if (record.ResidentData is not null) return (RecoveryChance.High, 1.0);
        if (record.HasSpecialData) return (RecoveryChance.Unavailable, 0);
        if (record.Runs.Count == 0)
        {
            // An empty file has nothing to lose; with no runs and a size > 0, there is nowhere to look for the data.
            if (record.RealSize == 0) return (RecoveryChance.High, 1.0);
            return (RecoveryChance.Unavailable, 0.0);
        }
        if (bitmap is null) return (RecoveryChance.Unavailable, 0);

        long total = 0, free = 0;
        foreach (var run in record.Runs)
        {
            if (run.IsSparse) continue;
            for (long c = run.Lcn; c < run.Lcn + run.Clusters; c++)
            {
                total++;
                long byteIndex = c >> 3;
                if (c < totalClusters && byteIndex < bitmap.Length && (bitmap[byteIndex] & (1 << (int)(c & 7))) == 0)
                    free++;
            }
        }

        if (total == 0) return (RecoveryChance.Unavailable, 0);
        double fraction = (double)free / total;
        var chance = fraction >= 0.999 ? RecoveryChance.High
                   : fraction >= 0.5 ? RecoveryChance.Medium
                   : RecoveryChance.Low;
        return (chance, fraction);
    }

    private static string ResolvePath(long parent, Dictionary<long, (long Parent, string Name)> directories)
    {
        var parts = new Stack<string>();

        for (int depth = 0; depth < 64; depth++)
        {
            if (parent == RootRecord) break;
            if (!directories.TryGetValue(parent, out var dir))
            {
                parts.Push("<unknown folder>");
                break;
            }
            parts.Push(dir.Name);
            parent = dir.Parent;
        }

        return parts.Count == 0 ? "\\" : "\\" + string.Join('\\', parts);
    }

    private static byte[] ReadBytes(RawVolume volume, long offset, int size)
    {
        var data = new byte[size];
        if (volume.Read(offset, data) < size)
            throw new IOException("Incomplete read from the volume.");
        return data;
    }
}
