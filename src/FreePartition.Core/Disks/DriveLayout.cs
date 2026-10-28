using System.Buffers.Binary;

namespace FreePartition.Core.Disks;

/// <summary>One used entry of a <see cref="DriveLayout"/>.</summary>
public readonly record struct LayoutPartition(int Index, long Offset, long Length, int Number);

/// <summary>
/// Parser and editor for the buffer of IOCTL_DISK_GET_DRIVE_LAYOUT_EX (DRIVE_LAYOUT_INFORMATION_EX), which is also
/// what IOCTL_DISK_SET_DRIVE_LAYOUT_EX takes back. It works on plain bytes so it can be tested without a disk.
/// Moving a partition changes nothing but one StartingOffset: every other byte is sent back exactly as read.
/// </summary>
public sealed class DriveLayout
{
    private const int HeaderSize = 48;
    private const int EntrySize = 144;
    private const int StyleMbr = 0;
    private const int StyleGpt = 1;

    /// <summary>The first MiB of a disk (MBR, GPT headers, alignment) is never a destination.</summary>
    public const long FirstUsableOffset = 1L << 20;

    /// <summary>Offsets and sizes must be multiples of this so raw I/O works on 512-byte and 4Kn disks alike.</summary>
    public const long Alignment = 4096;

    private readonly byte[] _data;

    public bool IsGpt { get; }
    public long UsableStart { get; }
    public long UsableEnd { get; }
    public IReadOnlyList<LayoutPartition> Partitions { get; }

    private DriveLayout(byte[] data, bool isGpt, long usableStart, long usableEnd, List<LayoutPartition> partitions)
    {
        _data = data;
        IsGpt = isGpt;
        UsableStart = usableStart;
        UsableEnd = usableEnd;
        Partitions = partitions;
    }

    /// <param name="diskLength">Size of the disk, used as the limit of an MBR disk.</param>
    /// <exception cref="InvalidDataException">Not a valid MBR or GPT layout.</exception>
    public static DriveLayout Parse(ReadOnlySpan<byte> buffer, long diskLength)
    {
        if (buffer.Length < HeaderSize)
            throw new InvalidDataException("The drive layout is too short.");

        uint style = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (style is not (StyleMbr or StyleGpt))
            throw new InvalidDataException("Only MBR and GPT disks can be rearranged.");
        if (count > 4096 || HeaderSize + (long)count * EntrySize > buffer.Length)
            throw new InvalidDataException("The drive layout is inconsistent.");

        bool gpt = style == StyleGpt;
        long usableStart = 0, usableEnd = diskLength;
        if (gpt)
        {
            usableStart = BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]);
            usableEnd = usableStart + BinaryPrimitives.ReadInt64LittleEndian(buffer[32..]);
        }

        var partitions = new List<LayoutPartition>();
        for (int i = 0; i < count; i++)
        {
            var entry = buffer.Slice(HeaderSize + i * EntrySize, EntrySize);
            long length = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            if (length <= 0) continue;   // empty MBR slot

            // Extended partitions hold logical ones chained through EBRs, which this code does not rewrite.
            if (!gpt && entry[32] is 0x05 or 0x0F or 0x85)
                throw new InvalidDataException("Disks with extended partitions are not supported.");

            partitions.Add(new LayoutPartition(i, BinaryPrimitives.ReadInt64LittleEndian(entry[8..]), length,
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[24..])));
        }

        return new DriveLayout(buffer.ToArray(), gpt, usableStart, usableEnd, partitions);
    }

    /// <summary>
    /// Checks that moving the partition is safe. The partition is identified by number AND by its current offset and
    /// size, so a layout that changed since the operation was queued is refused instead of moving the wrong thing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The move cannot be done (the message says why).</exception>
    public LayoutPartition CheckMove(int partitionNumber, long expectedOffset, long expectedLength, long newOffset)
    {
        var current = Partitions.Cast<LayoutPartition?>().FirstOrDefault(p => p!.Value.Number == partitionNumber)
                      ?? throw new InvalidOperationException($"Partition {partitionNumber} was not found on the disk.");

        if (current.Offset != expectedOffset || current.Length != expectedLength)
            throw new InvalidOperationException("The partition changed since the move was queued. Refresh the disks and queue it again.");
        if (newOffset == current.Offset)
            throw new InvalidOperationException("The partition is already at that position.");
        if (current.Offset % Alignment != 0 || current.Length % Alignment != 0)
            throw new InvalidOperationException("This partition is not aligned to 4 KB and cannot be moved safely.");
        if (newOffset % Alignment != 0)
            throw new InvalidOperationException("The destination is not aligned to 4 KB.");

        long end = newOffset + current.Length;
        if (newOffset < Math.Max(UsableStart, FirstUsableOffset) || end > UsableEnd)
            throw new InvalidOperationException("The destination is outside the usable area of the disk.");

        foreach (var other in Partitions)
        {
            if (other.Number == current.Number && other.Index == current.Index) continue;
            if (newOffset < other.Offset + other.Length && other.Offset < end)
                throw new InvalidOperationException($"The destination overlaps partition {other.Number}.");
        }

        return current;
    }

    /// <summary>A copy of the layout buffer with the start of one partition changed, ready for SET_DRIVE_LAYOUT_EX.</summary>
    public byte[] WithMovedPartition(LayoutPartition partition, long newOffset)
    {
        var copy = (byte[])_data.Clone();
        int at = HeaderSize + partition.Index * EntrySize;
        BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(at + 8), newOffset);
        copy[at + 28] = 1;   // RewritePartition
        return copy;
    }
}
