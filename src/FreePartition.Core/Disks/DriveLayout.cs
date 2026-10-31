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

    /// <summary>Destinations must be multiples of this so raw I/O works on 512-byte and 4Kn disks alike.</summary>
    public const long Alignment = 4096;

    /// <summary>
    /// The smallest logical sector. A partition being moved only has to start and end on this, so the old layouts
    /// that start at sector 63 can still be moved, which is how they get realigned.
    /// </summary>
    public const long MinSectorSize = 512;

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
        var current = FindExpected(partitionNumber, expectedOffset, expectedLength);
        if (DestinationProblem(current, newOffset) is { } problem)
            throw new InvalidOperationException(problem);
        return current;
    }

    /// <summary>
    /// The nearest 1 MB boundary the partition can be moved to (the one just before it, else the one just after),
    /// or null when neither fits because the space next to it is taken.
    /// </summary>
    /// <exception cref="InvalidOperationException">The partition is missing or changed since it was read.</exception>
    public long? FindAlignedOffset(int partitionNumber, long expectedOffset, long expectedLength)
    {
        var current = FindExpected(partitionNumber, expectedOffset, expectedLength);
        long boundary = (long)PartitionAlignment.Recommended;
        long down = current.Offset / boundary * boundary;
        long up = down == current.Offset ? down : down + boundary;

        foreach (long candidate in new[] { down, up })
        {
            if (DestinationProblem(current, candidate) is null)
                return candidate;
        }
        return null;
    }

    /// <summary>The partition is identified by number AND by its offset and size, so a stale request is refused.</summary>
    private LayoutPartition FindExpected(int partitionNumber, long expectedOffset, long expectedLength)
    {
        var current = Partitions.Cast<LayoutPartition?>().FirstOrDefault(p => p!.Value.Number == partitionNumber)
                      ?? throw new InvalidOperationException($"Partition {partitionNumber} was not found on the disk.");

        if (current.Offset != expectedOffset || current.Length != expectedLength)
            throw new InvalidOperationException("The partition changed since the move was queued. Refresh the disks and queue it again.");
        return current;
    }

    /// <summary>Why the partition cannot go to <paramref name="newOffset"/>, or null when it can.</summary>
    private string? DestinationProblem(LayoutPartition current, long newOffset)
    {
        if (newOffset == current.Offset)
            return "The partition is already at that position.";
        if (current.Offset % MinSectorSize != 0 || current.Length % MinSectorSize != 0)
            return "This partition does not start and end on a sector boundary and cannot be moved safely.";
        if (newOffset % Alignment != 0)
            return "The destination is not aligned to 4 KB.";

        long end = newOffset + current.Length;
        if (newOffset < Math.Max(UsableStart, FirstUsableOffset) || end > UsableEnd)
            return "The destination is outside the usable area of the disk.";

        foreach (var other in Partitions)
        {
            if (other.Number == current.Number && other.Index == current.Index) continue;
            if (newOffset < other.Offset + other.Length && other.Offset < end)
                return $"The destination overlaps partition {other.Number}.";
        }
        return null;
    }

    private const int GptTypeOffset = 32, GptIdOffset = 48, GptNameOffset = 72, GptNameChars = 36;
    private static readonly Guid BasicDataType = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    /// <summary>
    /// A copy of the layout buffer with one more partition entry, ready for SET_DRIVE_LAYOUT_EX. Unlike New-Partition,
    /// which zeroes the start of the new partition, this writes the table only: the contents are never touched.
    /// </summary>
    /// <exception cref="InvalidOperationException">The partition does not fit (the message says why).</exception>
    public byte[] WithAddedPartition(long offset, long length, int typeCode, int sectorSize)
    {
        if (sectorSize <= 0 || offset % sectorSize != 0 || length <= 0 || length % sectorSize != 0)
            throw new InvalidOperationException("The partition does not start and end on a sector boundary of this disk.");

        long end = offset + length;
        long first = IsGpt ? Math.Max(UsableStart, FirstUsableOffset) : sectorSize;
        if (offset < first || end > UsableEnd)
            throw new InvalidOperationException("The partition is outside the usable area of the disk.");
        foreach (var other in Partitions)
        {
            if (offset < other.Offset + other.Length && other.Offset < end)
                throw new InvalidOperationException($"The partition overlaps partition {other.Number}.");
        }

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(4));
        var copy = IsGpt
            ? new byte[HeaderSize + (count + 1) * EntrySize]
            : (byte[])_data.Clone();
        if (IsGpt)
        {
            if (count + 1 > BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(40)))
                throw new InvalidOperationException("The partition table of this disk is full.");
            _data.AsSpan(0, HeaderSize + count * EntrySize).CopyTo(copy);
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(4), (uint)(count + 1));
        }

        int slot = count;   // GPT: the new last entry
        if (!IsGpt)
        {
            slot = Enumerable.Range(0, count)
                .FirstOrDefault(i => BinaryPrimitives.ReadInt64LittleEndian(copy.AsSpan(HeaderSize + i * EntrySize + 16)) <= 0, -1);
            if (slot < 0)
                throw new InvalidOperationException("The disk already has four partitions, the most an MBR disk holds.");
        }

        var entry = copy.AsSpan(HeaderSize + slot * EntrySize, EntrySize);
        entry.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(entry, IsGpt ? StyleGpt : StyleMbr);
        BinaryPrimitives.WriteInt64LittleEndian(entry[8..], offset);
        BinaryPrimitives.WriteInt64LittleEndian(entry[16..], length);
        entry[28] = 1;   // RewritePartition
        if (IsGpt)
        {
            BasicDataType.TryWriteBytes(entry[GptTypeOffset..]);
            Guid.NewGuid().TryWriteBytes(entry[GptIdOffset..]);
            System.Text.Encoding.Unicode.GetBytes("Basic data partition".AsSpan(), entry.Slice(GptNameOffset, GptNameChars * 2));
        }
        else
        {
            entry[32] = (byte)typeCode;
            entry[34] = 1;   // RecognizedPartition
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], (uint)Math.Min(offset / sectorSize, uint.MaxValue));   // HiddenSectors
        }
        return copy;
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
