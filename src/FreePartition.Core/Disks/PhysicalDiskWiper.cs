using Microsoft.Win32.SafeHandles;
using static FreePartition.Core.Disks.DiskIo;

namespace FreePartition.Core.Disks;

/// <param name="Drive">The partition's drive letter: its volume is locked and dismounted during the wipe.</param>
public sealed record VolumeWipeRequest(
    int DiskNumber, int PartitionNumber, char Drive, long Offset, long Length, WipePattern Pattern, bool Verify);

public sealed record DiskWipeRequest(int DiskNumber, WipePattern Pattern, bool Verify);

/// <summary>Does the raw part of the wipe operations. Behind an interface so tests never touch a real disk.</summary>
public interface IDiskWiper
{
    /// <exception cref="InvalidOperationException">The disk holds Windows or this program, or that cannot be ruled out.</exception>
    void EnsureNotSystemDisk(int diskNumber);

    /// <exception cref="InvalidOperationException">The wipe was refused before anything was written.</exception>
    void WipeVolume(VolumeWipeRequest request, IProgress<string>? progress, CancellationToken ct);

    /// <exception cref="InvalidOperationException">The wipe was refused before anything was written.</exception>
    void WipeDisk(DiskWipeRequest request, IProgress<string>? progress, CancellationToken ct);

    /// <returns>How many bytes of free space were overwritten.</returns>
    long WipeFreeSpace(char drive, WipePattern pattern, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// Overwrites partitions, whole disks or free space. Every entry point refuses the drive (and, for a whole disk, the
/// disk) that holds Windows or this program before anything is opened for writing. A partition is wiped through
/// its own volume handle, so no write can land outside it.
/// </summary>
public sealed class PhysicalDiskWiper : IDiskWiper
{
    public void EnsureNotSystemDisk(int diskNumber)
    {
        foreach (var (letter, what) in ProtectedDrives())
        {
            IReadOnlyList<int> disks;
            try
            {
                disks = DisksOf(letter);
            }
            catch (Exception ex)
            {
                // Fail closed: if we cannot tell where Windows lives, nothing gets wiped.
                throw new InvalidOperationException(
                    $"Could not find out which disk holds {what} ({letter}:): {ex.Message}. Nothing was changed.", ex);
            }
            if (disks.Contains(diskNumber))
                throw new InvalidOperationException(
                    $"Disk {diskNumber} holds {what} ({letter}:), so it cannot be wiped. Nothing was changed.");
        }
    }

    public void WipeVolume(VolumeWipeRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        char letter = char.ToUpperInvariant(request.Drive);
        EnsureNotProtectedDrive(letter);

        int sectorSize;
        using (var disk = OpenDevice($@"\\.\PhysicalDrive{request.DiskNumber}", GenericRead))
            sectorSize = ReadSectorSize(disk);

        using var volume = OpenDevice($@"\\.\{letter}:", GenericRead | GenericWrite);
        var extents = ReadVolumeExtents(volume, $"{letter}:");
        if (extents.Count != 1 || extents[0].Disk != request.DiskNumber
            || extents[0].Start != request.Offset || extents[0].Length != request.Length)
            throw new InvalidOperationException(
                $"{letter}: is not the partition that was selected (the disks changed). Refresh and queue the wipe again.");

        progress?.Report($"Locking {letter}: ...");
        if (!Control(volume, FsctlLockVolume))
            throw new InvalidOperationException(
                $"Could not lock {letter}: — something is using it. Close every program using the drive and try again.");
        if (!Control(volume, FsctlDismountVolume))
            throw new InvalidOperationException($"Could not dismount {letter}:.");
        Control(volume, FsctlAllowExtendedDasdIo);   // the volume's last sectors lie past what the file system reports

        Overwrite(volume, request.Length, request.Pattern, request.Verify, sectorSize, letter, progress, ct);
        progress?.Report($"{letter}: was wiped. It is now RAW: format it before using it again.");
    }

    public void WipeDisk(DiskWipeRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNotSystemDisk(request.DiskNumber);

        using var disk = OpenDevice($@"\\.\PhysicalDrive{request.DiskNumber}", GenericRead | GenericWrite);
        long length = ReadLength(disk);
        int sectorSize = ReadSectorSize(disk);
        length -= length % sectorSize;   // a disk is a whole number of sectors; this only guards against odd reports

        Overwrite(disk, length, request.Pattern, request.Verify, sectorSize, null, progress, ct);
        Control(disk, IoctlDiskUpdateProperties);
        progress?.Report($"Disk {request.DiskNumber} was wiped. Initialize it before using it again.");
    }

    public long WipeFreeSpace(char drive, WipePattern pattern, IProgress<string>? progress, CancellationToken ct)
    {
        char letter = char.ToUpperInvariant(drive);
        EnsureNotProtectedDrive(letter);

        progress?.Report($"Filling the free space of {letter}: — programs writing to it may report that it is full.");
        long written = FreeSpaceWiper.Wipe($@"{letter}:\", pattern, PercentReporter(progress, "overwritten"), ct);
        progress?.Report($"Overwrote {written:N0} bytes of free space on {letter}:.");
        return written;
    }

    private static void Overwrite(SafeFileHandle device, long length, WipePattern pattern, bool verify, int sectorSize,
        char? letter, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(verify ? "Overwriting, then reading everything back to verify..." : "Overwriting...");
        bool wroteSomething = false;
        var percent = PercentReporter(progress, verify ? "done (write + verify)" : "overwritten");
        try
        {
            SectorWiper.Wipe(device, 0, length, pattern, verify, sectorSize,
                new PhysicalDiskMover.SyncProgress(fraction => { wroteSomething = true; percent.Report(fraction); }), ct);
        }
        catch (UnauthorizedAccessException) when (!wroteSomething)
        {
            throw new InvalidOperationException(
                PhysicalDiskMover.WriteBlockedMessage("queue the wipe again", letter ?? '?'));
        }
    }

    /// <summary>Turns a 0..1 fraction into "    40% overwritten" lines, one every 5%.</summary>
    private static IProgress<double> PercentReporter(IProgress<string>? progress, string verb)
    {
        int last = -1;
        return new PhysicalDiskMover.SyncProgress(fraction =>
        {
            int percent = (int)(fraction * 100);
            if (percent / 5 == last / 5 && percent < 100) return;
            last = percent;
            progress?.Report($"    {percent}% {verb}");
        });
    }

    private static void EnsureNotProtectedDrive(char letter)
    {
        foreach (var (drive, what) in ProtectedDrives())
        {
            if (drive == letter)
                throw new InvalidOperationException($"{letter}: holds {what}, so it cannot be wiped. Nothing was changed.");
        }
    }

    /// <summary>The drive Windows runs from and the drive this program runs from.</summary>
    private static IEnumerable<(char Letter, string What)> ProtectedDrives()
    {
        if (LetterOf(Environment.SystemDirectory) is { } windows)
            yield return (windows, "Windows");
        if (LetterOf(AppContext.BaseDirectory) is { } app)
            yield return (app, "FreePartition itself");
    }

    private static char? LetterOf(string path) =>
        Path.GetPathRoot(path) is [var c, ':', ..] && char.IsAsciiLetter(c) ? char.ToUpperInvariant(c) : null;
}
