using System.Buffers.Binary;

namespace FreePartition.Core.Ntfs;

/// <summary>
/// Pulls the application icon out of a PE file (EXE, DLL...) held in memory and returns it as the bytes of an <c>.ico</c> file.
/// The icon lives in the <c>.rsrc</c> section: a group-icon resource lists the sizes and each size is a separate icon
/// resource, so the <c>.ico</c> is just those pieces put side by side. Nothing is written anywhere, which matters because
/// a carved file only exists inside the volume being scanned. Any damage (a cut or fragmented file) gives <c>null</c>.
/// </summary>
public static class PeIconExtractor
{
    private const uint RtIcon = 3;
    private const uint RtGroupIcon = 14;
    private const int MaxIconImages = 64;
    private const int MaxEntriesPerDirectory = 1024;
    private const int MaxIconBytes = 8 << 20;
    private const uint SubdirectoryFlag = 0x80000000;

    public static byte[]? Extract(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var pe = PeFile.Parse(file);
        if (pe is null) return null;

        var group = pe.FindResource(RtGroupIcon, nameId: null);
        if (group is not { } groupData || groupData.Count < 6) return null;

        var g = groupData.AsSpan();
        // GRPICONDIR: reserved, type (1 = icon), count; then one 14-byte entry per image.
        if (BinaryPrimitives.ReadUInt16LittleEndian(g[2..]) != 1) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(g[4..]);
        if (count is < 1 or > MaxIconImages || g.Length < 6 + 14 * count) return null;

        var frames = new List<(byte[] Header, ArraySegment<byte> Data)>();
        long total = 6;
        for (int i = 0; i < count; i++)
        {
            var entry = g.Slice(6 + 14 * i, 14);
            int id = BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]);
            if (pe.FindResource(RtIcon, id) is not { } data || data.Count == 0) continue;   // a size that did not survive: skip it

            total += 16 + data.Count;
            if (total > MaxIconBytes) break;
            frames.Add((entry[..12].ToArray(), data));
        }
        if (frames.Count == 0) return null;

        // ICO file: the same entries as the group, but the last field is the image's offset in the file instead of its id.
        var ico = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), (ushort)frames.Count);

        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            var (header, data) = frames[i];
            var entry = ico.AsSpan(6 + 16 * i, 16);
            header.AsSpan(0, 8).CopyTo(entry);                                         // size, colors, planes, bit count
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)data.Count);    // bytes in the image
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            data.AsSpan().CopyTo(ico.AsSpan(offset));
            offset += data.Count;
        }
        return ico;
    }

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize);

    private sealed class PeFile
    {
        private readonly byte[] _data;
        private readonly Section[] _sections;
        private readonly int _resourceBase;

        private PeFile(byte[] data, Section[] sections, int resourceBase)
        {
            _data = data;
            _sections = sections;
            _resourceBase = resourceBase;
        }

        public static PeFile? Parse(byte[] d)
        {
            if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') return null;

            int pe = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x3C));
            if (!InRange(d, pe, 24) || BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pe)) != 0x00004550) return null;   // "PE\0\0"

            int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pe + 6));
            int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pe + 20));
            int optional = pe + 24;
            if (sectionCount is 0 or > 96 || optionalSize < 2 || !InRange(d, optional, optionalSize)) return null;

            // The data directories start at a different place in PE32 and PE32+.
            int directories = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(optional)) switch
            {
                0x10B => optional + 96,
                0x20B => optional + 112,
                _ => -1
            };
            if (directories < 0 || directories + 24 > optional + optionalSize) return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(directories - 4)) <= 2) return null;   // no resource directory

            uint resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(directories + 16));
            if (resourceRva == 0) return null;

            int table = optional + optionalSize;
            if (!InRange(d, table, sectionCount * 40)) return null;

            var sections = new Section[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                var s = d.AsSpan(table + 40 * i, 40);
                sections[i] = new Section(
                    VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
                    VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                    RawPointer: BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
                    RawSize: BinaryPrimitives.ReadUInt32LittleEndian(s[16..]));
            }

            int resourceBase = RvaToOffset(sections, d.Length, resourceRva);
            return resourceBase < 0 ? null : new PeFile(d, sections, resourceBase);
        }

        /// <summary>Bytes of the resource of this type, by name id (or the first one when <paramref name="nameId"/> is null).</summary>
        public ArraySegment<byte>? FindResource(uint typeId, int? nameId)
        {
            // Three levels: type, then name, then language; the last one points at the data entry.
            var typeDirectory = FindEntry(0, typeId, anyId: false);
            if (typeDirectory is not { IsDirectory: true } type) return null;

            var nameEntry = nameId is { } id ? FindEntry(type.Offset, (uint)id, anyId: false) : FindEntry(type.Offset, 0, anyId: true);
            if (nameEntry is not { IsDirectory: true } name) return null;

            var language = FindEntry(name.Offset, 0, anyId: true);
            if (language is not { IsDirectory: false } leaf) return null;

            int entry = _resourceBase + leaf.Offset;
            if (!InRange(_data, entry, 16)) return null;

            int start = RvaToOffset(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(entry)));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(entry + 4));
            if (start < 0 || size > MaxIconBytes || !InRange(_data, start, (int)size)) return null;

            return new ArraySegment<byte>(_data, start, (int)size);
        }

        private readonly record struct Entry(int Offset, bool IsDirectory);

        /// <summary>An entry of the directory at <paramref name="directory"/> (relative to the resource section).</summary>
        private Entry? FindEntry(int directory, uint id, bool anyId)
        {
            int at = _resourceBase + directory;
            if (!InRange(_data, at, 16)) return null;

            int named = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(at + 12));
            int ids = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(at + 14));
            if (named + ids > MaxEntriesPerDirectory || !InRange(_data, at + 16, (named + ids) * 8)) return null;

            // Named entries come first; icons are found by number, so they are skipped.
            for (int i = named; i < named + ids; i++)
            {
                var entry = _data.AsSpan(at + 16 + 8 * i, 8);
                uint entryId = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                if (!anyId && entryId != id) continue;

                uint target = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                bool isDirectory = (target & SubdirectoryFlag) != 0;
                return new Entry((int)(target & ~SubdirectoryFlag), isDirectory);
            }

            // Group icons are sometimes named, not numbered: the first entry is the one Windows shows.
            if (anyId && named > 0)
            {
                var entry = _data.AsSpan(at + 16, 8);
                uint target = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                return new Entry((int)(target & ~SubdirectoryFlag), (target & SubdirectoryFlag) != 0);
            }
            return null;
        }

        private int RvaToOffset(uint rva) => RvaToOffset(_sections, _data.Length, rva);

        private static int RvaToOffset(Section[] sections, int fileLength, uint rva)
        {
            foreach (var s in sections)
            {
                if (rva < s.VirtualAddress) continue;

                uint inside = rva - s.VirtualAddress;
                if (inside >= Math.Max(s.VirtualSize, s.RawSize)) continue;
                if (inside >= s.RawSize) return -1;   // part of the section that is not in the file

                long offset = (long)s.RawPointer + inside;
                return offset < fileLength ? (int)offset : -1;
            }
            return -1;
        }

        private static bool InRange(byte[] data, int offset, int length) =>
            offset >= 0 && length >= 0 && (long)offset + length <= data.Length;
    }
}
