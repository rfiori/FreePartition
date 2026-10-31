using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FreePartition.Core.Util;

namespace FreePartition.Core.Disks;

/// <summary>
/// Conversion between MBR and GPT. Both directions only build the new SET_DRIVE_LAYOUT_EX buffer: the partitions keep
/// their offset and size, so no sector inside a partition is ever read or written, and the files stay where they are.
/// Anything that cannot be represented in the other style is refused, with the reason, before anything is written.
/// </summary>
public sealed partial class DriveLayout
{
    private const int GptEntryBytes = 128;
    private const int DefaultGptEntries = 128;
    private const int MbrSlots = 4;

    private static readonly Guid EfiSystemType = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    private static readonly Guid MsrType = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    private static readonly Guid RecoveryType = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
    private static readonly Guid LinuxDataType = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid LinuxSwapType = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");
    private static readonly Guid LinuxLvmType = new("E6D6D379-F507-44C2-A23C-238F2A3DF928");
    private static readonly Guid LinuxRaidType = new("A19D880F-05FC-4D3B-A006-743F0F84911E");

    /// <summary>GPT attributes of a Windows recovery partition: required by the platform, and no drive letter.</summary>
    private const ulong RecoveryAttributes = 0x8000000000000001;

    private const int MbrTypeDynamicDisk = 0x42;

    /// <summary>Sectors the GPT needs next to the entries: the header at the start and the one at the end.</summary>
    private static long EntrySectors(int sectorSize, int entries = DefaultGptEntries) =>
        ((long)entries * GptEntryBytes + sectorSize - 1) / sectorSize;

    private static void CheckGeometry(int sectorSize, long diskLength)
    {
        if (sectorSize < 512 || (sectorSize & (sectorSize - 1)) != 0)
            throw new InvalidOperationException($"Unsupported sector size: {sectorSize} bytes.");
        if (diskLength < 1L << 20)
            throw new InvalidOperationException("The disk is too small to convert.");
    }

    /// <summary>The GPT type, name and attributes for an MBR partition type byte, or null when GPT has no equivalent.</summary>
    private static (Guid Type, string Name, ulong Attributes)? GptTypeFor(byte mbrType) => mbrType switch
    {
        0x01 or 0x04 or 0x06 or 0x07 or 0x0B or 0x0C or 0x0E or 0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C or 0x1E
            => (BasicDataType, "Basic data partition", 0),
        0x27 => (RecoveryType, "Windows recovery environment", RecoveryAttributes),
        0x82 => (LinuxSwapType, "Linux swap", 0),
        0x83 => (LinuxDataType, "Linux filesystem", 0),
        0x8E => (LinuxLvmType, "Linux LVM", 0),
        0xFD => (LinuxRaidType, "Linux RAID", 0),
        0xEF => (EfiSystemType, "EFI system partition", 0),
        _ => null
    };

    /// <summary>The MBR type byte for a GPT partition type, or null when MBR has no equivalent.</summary>
    private static byte? MbrTypeFor(Guid gptType, bool fat32)
    {
        if (gptType == BasicDataType) return fat32 ? (byte)0x0C : (byte)0x07;
        if (gptType == EfiSystemType) return 0xEF;
        if (gptType == RecoveryType) return 0x27;
        if (gptType == LinuxDataType) return 0x83;
        if (gptType == LinuxSwapType) return 0x82;
        if (gptType == LinuxLvmType) return 0x8E;
        if (gptType == LinuxRaidType) return 0xFD;
        return null;
    }

    private int EntryAt(LayoutPartition partition) => HeaderSize + partition.Index * EntrySize;

    // ------------------------------------------------------------------ MBR -> GPT

    /// <summary>
    /// The layout of this MBR disk as a GPT disk: same partitions, same offsets and sizes (logical partitions of an
    /// extended partition become ordinary ones). The active flag and the disk signature are not carried over.
    /// </summary>
    /// <exception cref="InvalidOperationException">The disk cannot be converted (the message says why).</exception>
    public byte[] ToGpt(int sectorSize, long diskLength)
    {
        if (IsGpt)
            throw new InvalidOperationException("The disk already uses GPT.");
        CheckGeometry(sectorSize, diskLength);

        long entrySectors = EntrySectors(sectorSize);
        long firstUsable = (2 + entrySectors) * sectorSize;
        long usableEnd = (diskLength / sectorSize - 1 - entrySectors) * sectorSize;
        if (usableEnd <= firstUsable)
            throw new InvalidOperationException("The disk is too small to hold a GPT.");

        var ordered = Partitions.OrderBy(p => p.Offset).ToList();
        var types = new List<(Guid Type, string Name, ulong Attributes)>();
        foreach (var p in ordered)
        {
            byte mbrType = _data[EntryAt(p) + 32];
            if (mbrType == MbrTypeDynamicDisk)
                throw new InvalidOperationException(
                    "This is a dynamic disk (Windows LDM). It has to be converted back to a basic disk first, which erases it.");
            if (GptTypeFor(mbrType) is not { } gpt)
                throw new InvalidOperationException(
                    $"Partition {p.Number} has the MBR type 0x{mbrType:X2}, which GPT has no equivalent for, so the disk cannot be converted without losing it.");
            types.Add(gpt);

            if (p.Offset % sectorSize != 0 || p.Length % sectorSize != 0)
                throw new InvalidOperationException($"Partition {p.Number} does not start and end on a sector boundary.");
            if (p.Offset < firstUsable)
                throw new InvalidOperationException(
                    $"Partition {p.Number} starts at {ByteFormatter.Format(p.Offset)}, inside the first {ByteFormatter.Format(firstUsable)} " +
                    "that GPT needs for its table. Move it to the right first.");
            if (p.Offset + p.Length > usableEnd)
                throw new InvalidOperationException(
                    $"Partition {p.Number} ends {ByteFormatter.Format(p.Offset + p.Length - usableEnd)} too far: GPT keeps a backup of its table " +
                    $"in the last {ByteFormatter.Format(diskLength - usableEnd)} of the disk. Shrink the last partition by at least 1 MB first.");
        }

        var buffer = new byte[HeaderSize + ordered.Count * EntrySize];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, StyleGpt);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)ordered.Count);
        Guid.NewGuid().TryWriteBytes(buffer.AsSpan(8));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), firstUsable);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), usableEnd - firstUsable);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40), DefaultGptEntries);

        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = buffer.AsSpan(HeaderSize + i * EntrySize, EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(entry, StyleGpt);
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], ordered[i].Offset);
            BinaryPrimitives.WriteInt64LittleEndian(entry[16..], ordered[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], (uint)(i + 1));
            entry[28] = 1;   // RewritePartition
            types[i].Type.TryWriteBytes(entry[GptTypeOffset..]);
            Guid.NewGuid().TryWriteBytes(entry[GptIdOffset..]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[64..], types[i].Attributes);
            Encoding.Unicode.GetBytes(types[i].Name.AsSpan(), entry.Slice(GptNameOffset, GptNameChars * 2));
        }
        return buffer;
    }

    // ------------------------------------------------------------------ GPT -> MBR

    /// <summary>
    /// The layout of this GPT disk as an MBR disk: same partitions, same offsets and sizes. The Microsoft Reserved
    /// partition (which never holds data) is dropped; partition names, GUIDs and attributes are not carried over.
    /// </summary>
    /// <param name="isFat32">Tells a FAT32 basic data partition (MBR type 0x0C) from an NTFS/exFAT one (0x07).</param>
    /// <exception cref="InvalidOperationException">The disk cannot be converted (the message says why).</exception>
    public byte[] ToMbr(int sectorSize, long diskLength, Func<LayoutPartition, bool>? isFat32 = null)
    {
        if (!IsGpt)
            throw new InvalidOperationException("The disk already uses MBR.");
        CheckGeometry(sectorSize, diskLength);

        var kept = new List<(LayoutPartition Partition, byte Type)>();
        foreach (var p in Partitions.OrderBy(p => p.Offset))
        {
            var type = new Guid(_data.AsSpan(EntryAt(p) + GptTypeOffset, 16));
            if (type == MsrType) continue;

            if (MbrTypeFor(type, type == BasicDataType && isFat32?.Invoke(p) == true) is not { } mbrType)
                throw new InvalidOperationException(
                    $"Partition {p.Number} has the GPT type {type}, which MBR has no equivalent for, so the disk cannot be converted without losing it.");
            kept.Add((p, mbrType));
        }

        if (kept.Count > MbrSlots)
            throw new InvalidOperationException(
                $"The disk has {kept.Count} partitions and MBR holds at most {MbrSlots} primary ones. Delete or merge some first.");

        long startTable = StartTableBytes(sectorSize), endTable = diskLength / sectorSize * sectorSize - EndTableBytes(sectorSize);
        foreach (var (p, _) in kept)
        {
            if (p.Offset % sectorSize != 0 || p.Length % sectorSize != 0)
                throw new InvalidOperationException($"Partition {p.Number} does not start and end on a sector boundary.");
            // The tables are saved before they are rewritten, which is only safe if no partition shares their sectors.
            if (p.Offset < startTable || p.Offset + p.Length > endTable)
                throw new InvalidOperationException(
                    $"Partition {p.Number} sits inside the area a standard GPT reserves for its tables, so this GPT is not a standard one and is not converted.");
            if (p.Offset / sectorSize > uint.MaxValue || p.Length / sectorSize > uint.MaxValue
                || (p.Offset + p.Length) / sectorSize > uint.MaxValue)
                throw new InvalidOperationException(
                    $"Partition {p.Number} reaches past {ByteFormatter.Format((long)uint.MaxValue * sectorSize)}, the most an MBR disk with " +
                    $"{sectorSize}-byte sectors can address. Shrink it or keep the disk as GPT.");
        }

        var buffer = new byte[HeaderSize + MbrSlots * EntrySize];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, StyleMbr);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), MbrSlots);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));

        for (int i = 0; i < MbrSlots; i++)
        {
            var entry = buffer.AsSpan(HeaderSize + i * EntrySize, EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(entry, StyleMbr);
            entry[28] = 1;   // RewritePartition, also for an empty slot, so it is cleared
            if (i >= kept.Count) continue;

            var (p, mbrType) = kept[i];
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], p.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(entry[16..], p.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], (uint)(i + 1));
            entry[32] = mbrType;
            entry[34] = 1;   // RecognizedPartition
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], (uint)(p.Offset / sectorSize));   // HiddenSectors
        }
        return buffer;
    }

    /// <summary>
    /// The sectors that still hold the GPT headers and entry tables of this GPT disk and are not part of any partition.
    /// After a conversion to MBR they are erased, so no tool finds a stale GPT and brings it back.
    /// </summary>
    public IReadOnlyList<(long Offset, long Length)> StaleGptRanges(int sectorSize, long diskLength)
    {
        if (!IsGpt) return [];
        CheckGeometry(sectorSize, diskLength);

        long entrySectors = EntrySectors(sectorSize);
        var ranges = new List<(long Offset, long Length)>
        {
            (sectorSize, (1 + entrySectors) * sectorSize),                                   // header and entries after the MBR
            (diskLength - (1 + entrySectors) * sectorSize, (1 + entrySectors) * sectorSize)   // entries and header at the end
        };
        return ranges
            .Where(r => r.Offset >= sectorSize && r.Offset + r.Length <= diskLength)
            .Where(r => !Partitions.Any(p => r.Offset < p.Offset + p.Length && p.Offset < r.Offset + r.Length))
            .ToList();
    }

    /// <summary>Bytes at the start of the disk a conversion may rewrite: the MBR sector, the GPT header and its entries.</summary>
    public static long StartTableBytes(int sectorSize) => (2 + EntrySectors(sectorSize)) * sectorSize;

    /// <summary>Bytes at the end of the disk a conversion may rewrite: the backup GPT entries and header.</summary>
    public static long EndTableBytes(int sectorSize) => (1 + EntrySectors(sectorSize)) * sectorSize;

    /// <summary>The header values CREATE_DISK needs to start a table of the same style and identity as this layout.</summary>
    public byte[] CreateDiskBuffer()
    {
        var buffer = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, IsGpt ? StyleGpt : StyleMbr);
        if (IsGpt)
        {
            _data.AsSpan(8, 16).CopyTo(buffer.AsSpan(4));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(40)));
        }
        else
        {
            _data.AsSpan(8, 4).CopyTo(buffer.AsSpan(4));
        }
        return buffer;
    }
}
