using System.ComponentModel;
using System.Runtime.InteropServices;
using FreePartition.Core.Operations;
using Microsoft.Win32.SafeHandles;
using static FreePartition.Core.Disks.DiskIo;

namespace FreePartition.Core.Disks;

/// <summary>A drive letter and where its partition starts, so the conversion can lock it and check it is still that partition.</summary>
public sealed record DriveMount(char Letter, long Offset);

public sealed record LayoutConversionRequest(int DiskNumber, DiskPartitionStyle Target, IReadOnlyList<DriveMount> Mounts);

/// <summary>Does the raw part of "Convert MBR / GPT". Behind an interface so tests never touch a real disk.</summary>
public interface ILayoutConverter
{
    /// <exception cref="InvalidOperationException">The conversion was refused before anything was written.</exception>
    void Convert(LayoutConversionRequest request, IProgress<string>? progress);
}

/// <summary>
/// Changes the partition table of a physical disk from MBR to GPT or back, leaving every partition where it is. Only
/// the sectors of the tables are written (the first and the last few of the disk), never a sector inside a partition.
/// Order of events:
/// 1. read the table and work out the new one (refused here, before anything is written, if it does not fit);
/// 2. lock and dismount every volume of the disk, and keep a copy of the sectors the tables occupy;
/// 3. IOCTL_DISK_CREATE_DISK starts an empty table of the new style, IOCTL_DISK_SET_DRIVE_LAYOUT_EX fills it in;
/// 4. if step 3 fails, the saved sectors are written back, which restores the original table;
/// 5. read the table again and compare it with what was meant to be written.
/// </summary>
public sealed class PhysicalLayoutConverter : ILayoutConverter
{
    private const uint IoctlDiskCreateDisk = 0x0007C058;
    private const int ErrorAccessDenied = 5;

    private sealed record Plan(DriveLayout Original, byte[] Converted, DriveLayout Expected, int SectorSize, long Length);

    /// <summary>Why the disk cannot be converted, or nothing when it can. Read-only, so the UI can explain up front.</summary>
    /// <exception cref="InvalidOperationException">The disk cannot be converted (the message says why).</exception>
    public static void Check(int diskNumber, DiskPartitionStyle target)
    {
        using var disk = OpenDevice($@"\\.\PhysicalDrive{diskNumber}", GenericRead);
        MakePlan(diskNumber, disk, target);
    }

    public void Convert(LayoutConversionRequest request, IProgress<string>? progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNotSystemDisk(request.DiskNumber);

        using var disk = OpenDevice($@"\\.\PhysicalDrive{request.DiskNumber}", GenericRead | GenericWrite);
        var plan = MakePlan(request.DiskNumber, disk, request.Target);

        var volumes = new List<SafeFileHandle>();
        try
        {
            foreach (var mount in request.Mounts)
            {
                progress?.Report($"Locking {char.ToUpperInvariant(mount.Letter)}: ...");
                volumes.Add(LockVolume(request.DiskNumber, mount));
            }

            Control(disk, FsctlAllowExtendedDasdIo);
            var saved = SaveTables(disk, plan);

            progress?.Report($"Writing the {request.Target} partition table. Do NOT turn off the computer or unplug the disk.");
            WriteNewTable(disk, plan, saved, request.DiskNumber, progress);

            Control(disk, IoctlDiskUpdateProperties);
            Verify(disk, plan, request.Target);

            if (request.Target == DiskPartitionStyle.MBR)
                EraseStaleGpt(disk, plan, progress);
            progress?.Report($"Disk {request.DiskNumber} now uses {request.Target}.");
        }
        finally
        {
            foreach (var volume in volumes)
                volume.Dispose();
        }
    }

    // ------------------------------------------------------------------ planning

    private static Plan MakePlan(int diskNumber, SafeFileHandle disk, DiskPartitionStyle target)
    {
        if (!Enum.IsDefined(target))
            throw new InvalidOperationException("Invalid partition style. Use MBR or GPT.");

        long length = ReadLength(disk);
        int sectorSize = ReadSectorSize(disk);
        DriveLayout original;
        try
        {
            original = DriveLayout.Parse(PhysicalDiskMover.ReadLayout(disk), length, allowExtended: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                $"Disk {diskNumber} has no MBR or GPT partition table to convert ({ex.Message}). Initialize it instead.", ex);
        }

        byte[] converted = target == DiskPartitionStyle.GPT
            ? original.ToGpt(sectorSize, length)
            : original.ToMbr(sectorSize, length, partition => IsFat32(diskNumber, partition));
        return new Plan(original, converted, DriveLayout.Parse(converted, length, allowExtended: true), sectorSize, length);
    }

    /// <summary>A basic data partition is FAT32 (MBR type 0x0C) rather than NTFS/exFAT (0x07) when its boot sector says so.</summary>
    private static bool IsFat32(int diskNumber, LayoutPartition partition)
    {
        try
        {
            using var raw = RawDisk.Open(diskNumber);
            var sector = new byte[512];
            return raw.Read(partition.Offset, sector) == sector.Length
                   && BootSectorProbe.TryParse(sector, out var info) && info?.FileSystem == FileSystemFormat.FAT32;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            return false;   // the type byte is a hint: Windows mounts the volume by its contents either way
        }
    }

    private static void EnsureNotSystemDisk(int diskNumber)
    {
        foreach (var (letter, what) in PhysicalDiskWiper.ProtectedDrives())
        {
            IReadOnlyList<int> disks;
            try
            {
                disks = DisksOf(letter);
            }
            catch (Exception ex)
            {
                // Fail closed: if we cannot tell where Windows lives, no partition table gets rewritten.
                throw new InvalidOperationException(
                    $"Could not find out which disk holds {what} ({letter}:): {ex.Message}. Nothing was changed.", ex);
            }
            if (disks.Contains(diskNumber))
                throw new InvalidOperationException(
                    $"Disk {diskNumber} holds {what} ({letter}:). Its boot method depends on the partition style, so it is not converted here: " +
                    "use Microsoft's mbr2gpt.exe for the disk Windows boots from. Nothing was changed.");
        }
    }

    private static SafeFileHandle LockVolume(int diskNumber, DriveMount mount)
    {
        char letter = char.ToUpperInvariant(mount.Letter);
        var volume = OpenDevice($@"\\.\{letter}:", GenericRead | GenericWrite);
        try
        {
            var extents = ReadVolumeExtents(volume, $"{letter}:");
            if (extents.Count != 1 || extents[0].Disk != diskNumber || extents[0].Start != mount.Offset)
                throw new InvalidOperationException(
                    $"{letter}: is not the partition that was selected (the disks changed). Refresh and queue the conversion again.");
            if (!Control(volume, FsctlLockVolume))
                throw new InvalidOperationException(
                    $"Could not lock {letter}: — something is using it. Close every program using the drive and try again.");
            if (!Control(volume, FsctlDismountVolume))
                throw new InvalidOperationException($"Could not dismount {letter}:.");
            return volume;
        }
        catch
        {
            volume.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>The sectors at the start and the end of the disk that hold the tables (no partition shares them).</summary>
    private static (long Offset, byte[] Data)[] SaveTables(SafeFileHandle disk, Plan plan)
    {
        long start = DriveLayout.StartTableBytes(plan.SectorSize);
        long endLength = DriveLayout.EndTableBytes(plan.SectorSize);
        long endOffset = plan.Length / plan.SectorSize * plan.SectorSize - endLength;

        var regions = new (long Offset, byte[] Data)[] { (0, new byte[start]), (endOffset, new byte[endLength]) };
        foreach (var (offset, data) in regions)
        {
            int total = 0;
            while (total < data.Length)
            {
                int n = RandomAccess.Read(disk, data.AsSpan(total), offset + total);
                if (n == 0) throw new IOException("Unexpected end of the disk while saving the partition table.");
                total += n;
            }
        }
        return regions;
    }

    private static void WriteNewTable(SafeFileHandle disk, Plan plan, (long Offset, byte[] Data)[] saved, int diskNumber, IProgress<string>? progress)
    {
        try
        {
            CreateDisk(disk, plan.Expected.CreateDiskBuffer());
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(ex.NativeErrorCode == ErrorAccessDenied
                ? PhysicalDiskMover.WriteBlockedMessage("queue the conversion again", '?')
                : $"Windows refused to start the new partition table ({ex.Message}). Your files were not touched.", ex);
        }

        try
        {
            PhysicalDiskMover.SetLayout(disk, plan.Converted);
        }
        catch (Exception ex)
        {
            progress?.Report("Could not write the partitions; putting the original partition table back...");
            try
            {
                foreach (var (offset, data) in saved)
                    RandomAccess.Write(disk, data, offset);
                RandomAccess.FlushToDisk(disk);
                Control(disk, IoctlDiskUpdateProperties);
            }
            catch (Exception restoreError)
            {
                throw new IOException(
                    $"The new partition table could not be written ({ex.Message}) and the original could not be put back " +
                    $"({restoreError.Message}). Your files were not touched: use Recover partitions on disk {diskNumber} " +
                    "to bring the partitions back.", ex);
            }
            throw new IOException($"The new partition table could not be written ({ex.Message}). The original one was put back.", ex);
        }
    }

    private static void CreateDisk(SafeFileHandle disk, byte[] buffer)
    {
        if (!DeviceIoControl(disk, IoctlDiskCreateDisk, buffer, buffer.Length, default, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>Reads the table back: the style and every partition's offset and size must be what was written.</summary>
    private static void Verify(SafeFileHandle disk, Plan plan, DiskPartitionStyle target)
    {
        DriveLayout after;
        try
        {
            after = DriveLayout.Parse(PhysicalDiskMover.ReadLayout(disk), plan.Length, allowExtended: true);
        }
        catch (InvalidDataException ex)
        {
            throw new IOException($"The partition table could not be read back after the conversion ({ex.Message}).", ex);
        }

        static IEnumerable<(long, long)> Spans(DriveLayout layout) => layout.Partitions.Select(p => (p.Offset, p.Length)).Order();
        if (after.IsGpt != (target == DiskPartitionStyle.GPT) || !Spans(after).SequenceEqual(Spans(plan.Expected)))
            throw new IOException(
                "The partition table read back does not match the one that was written. Do not use the disk yet: refresh the " +
                "list and check that every partition is there; Recover partitions can bring back any that is missing.");
    }

    /// <summary>
    /// Erases the old GPT headers and entry tables, which an MBR disk no longer needs. Windows ignores them, but other
    /// tools would find the stale backup at the end of the disk and offer to restore it. A failure is not an error.
    /// </summary>
    private static void EraseStaleGpt(SafeFileHandle disk, Plan plan, IProgress<string>? progress)
    {
        try
        {
            foreach (var (offset, length) in plan.Original.StaleGptRanges(plan.SectorSize, plan.Length))
            {
                RandomAccess.Write(disk, new byte[length], offset);
            }
            RandomAccess.FlushToDisk(disk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress?.Report($"Note: the old GPT headers could not be erased ({ex.Message}). Windows ignores them.");
        }
    }
}
