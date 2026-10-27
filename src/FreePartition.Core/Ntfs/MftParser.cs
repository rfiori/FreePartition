using System.Buffers.Binary;
using System.Text;

namespace FreePartition.Core.Ntfs;

/// <summary>Interprets raw MFT records (typically 1024 bytes each).</summary>
public static class MftParser
{
    private const int FixupSectorSize = 512;      // the "update sequence array" always uses 512-byte steps
    private const uint AttributeListType = 0x20;
    private const uint FileNameType = 0x30;
    private const uint DataType = 0x80;
    private const uint EndOfAttributes = 0xFFFFFFFF;
    private const long RecordNumberMask = 0x0000FFFFFFFFFFFF;
    private const long MaxFileTime = 2650467743999999999L;

    /// <summary>
    /// Parses a record. The buffer is modified (end-of-sector "fixups" are applied in place).
    /// Returns null if the record is invalid, an extension record, or irrelevant.
    /// </summary>
    /// <param name="includeInUseFiles">
    /// false = skips in-use files (keeps in-use directories and everything that was deleted).
    /// Speeds up the scan, since we only need folder names and deleted items.
    /// </param>
    public static MftRecord? Parse(Span<byte> record, long number, bool includeInUseFiles = true)
    {
        if (record.Length < 48) return null;
        if (record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
            return null;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        bool inUse = (flags & 0x01) != 0;
        bool isDirectory = (flags & 0x02) != 0;

        if (!includeInUseFiles && inUse && !isDirectory) return null;

        // Extension record (belongs to another base record): ignored.
        long baseReference = BinaryPrimitives.ReadInt64LittleEndian(record[0x20..]) & RecordNumberMask;
        if (baseReference != 0) return null;

        if (!ApplyFixup(record)) return null;

        string? name = null;
        int namePriority = -1;
        long parent = -1;
        DateTime? modified = null;
        long realSize = 0;
        byte[]? resident = null;
        IReadOnlyList<DataRun> runs = Array.Empty<DataRun>();
        bool special = false;
        bool hasList = false;
        bool dataFound = false;

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);

        while (offset + 8 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == EndOfAttributes) break;

            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (size <= 0 || offset + size > record.Length) break;

            var attribute = record.Slice(offset, size);
            bool nonResident = attribute[8] != 0;
            int attributeNameLength = attribute[9];

            if (type == AttributeListType)
            {
                hasList = true;
            }
            else if (type == FileNameType && !nonResident)
            {
                ReadFileName(attribute, ref name, ref namePriority, ref parent, ref modified);
            }
            else if (type == DataType && attributeNameLength == 0 && !dataFound)
            {
                if (!nonResident)
                {
                    int contentSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
                    int contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
                    if (contentSize >= 0 && contentOffset + contentSize <= attribute.Length)
                    {
                        resident = attribute.Slice(contentOffset, contentSize).ToArray();
                        realSize = contentSize;
                        dataFound = true;
                    }
                }
                else
                {
                    long startVcn = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]);
                    if (startVcn == 0)
                    {
                        ushort attributeFlags = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x0C..]);
                        int runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);
                        realSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
                        special = (attributeFlags & 0x4001) != 0;   // 0x0001 compressed, 0x4000 encrypted
                        if (runsOffset > 0 && runsOffset < attribute.Length)
                            runs = DecodeRuns(attribute[runsOffset..]);
                        dataFound = true;
                    }
                }
            }

            offset += size;
        }

        return new MftRecord
        {
            Number = number,
            InUse = inUse,
            IsDirectory = isDirectory,
            Name = name,
            ParentNumber = parent,
            Modified = modified,
            RealSize = realSize,
            ResidentData = resident,
            Runs = runs,
            HasSpecialData = special,
            HasAttributeList = hasList
        };
    }

    private static void ReadFileName(
        ReadOnlySpan<byte> attribute,
        ref string? name, ref int currentPriority, ref long parent, ref DateTime? modified)
    {
        int contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        if (contentOffset + 0x42 > attribute.Length) return;

        var fn = attribute[contentOffset..];
        int nameLength = fn[0x40];
        int namespaceId = fn[0x41];                      // 0 POSIX, 1 Win32, 2 DOS (8.3), 3 Win32+DOS
        if (0x42 + nameLength * 2 > fn.Length) return;

        // Prefers the long name (Win32) over the short one (DOS 8.3).
        int priority = namespaceId switch { 1 or 3 => 3, 0 => 2, 2 => 1, _ => 0 };
        if (priority <= currentPriority) return;

        currentPriority = priority;
        name = Encoding.Unicode.GetString(fn.Slice(0x42, nameLength * 2));
        parent = BinaryPrimitives.ReadInt64LittleEndian(fn) & RecordNumberMask;

        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(fn[0x10..]);   // modification time
        modified = fileTime > 0 && fileTime < MaxFileTime ? DateTime.FromFileTimeUtc(fileTime) : null;
    }

    /// <summary>
    /// In each 512-byte sector, the last 2 bytes were replaced by a marker (USN).
    /// The original values live in the "update sequence array". Restores them and validates the marker.
    /// </summary>
    private static bool ApplyFixup(Span<byte> record)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]);   // includes the USN itself
        if (count < 2 || usaOffset + count * 2 > record.Length) return false;

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);

        for (int i = 1; i < count; i++)
        {
            int position = i * FixupSectorSize - 2;
            if (position + 2 > record.Length) return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[position..]) != usn) return false;

            record[position] = record[usaOffset + i * 2];
            record[position + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>
    /// Decodes the list of "data runs". Each run: header byte (low nibble = size bytes,
    /// high nibble = offset bytes), size (unsigned) and offset (signed, relative to the previous run).
    /// </summary>
    public static List<DataRun> DecodeRuns(ReadOnlySpan<byte> data)
    {
        var runs = new List<DataRun>();
        int pos = 0;
        long lcn = 0;

        while (pos < data.Length && data[pos] != 0)
        {
            int header = data[pos++];
            int sizeBytes = header & 0x0F;
            int offsetBytes = header >> 4;

            if (sizeBytes == 0 || sizeBytes > 8 || offsetBytes > 8 || pos + sizeBytes + offsetBytes > data.Length)
                break;

            long clusters = 0;
            for (int i = 0; i < sizeBytes; i++)
                clusters |= (long)data[pos + i] << (8 * i);
            pos += sizeBytes;

            if (offsetBytes == 0)
            {
                runs.Add(new DataRun(-1, clusters));     // sparse stretch
                continue;
            }

            long delta = 0;
            for (int i = 0; i < offsetBytes; i++)
                delta |= (long)data[pos + i] << (8 * i);
            if ((data[pos + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8)
                delta |= -1L << (8 * offsetBytes);       // sign extension
            pos += offsetBytes;

            lcn += delta;
            runs.Add(new DataRun(lcn, clusters));
        }

        return runs;
    }
}
