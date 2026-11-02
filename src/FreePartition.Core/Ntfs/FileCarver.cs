using System.Diagnostics;
using FreePartition.Core.Disks;

namespace FreePartition.Core.Ntfs;

/// <summary>
/// Finds files by their content instead of the file system: it walks the volume looking for the signature a format
/// starts with (JPEG, PNG, GIF, BMP, WebP, PSD, PDF, ZIP and the Office formats built on it, 7z, MP4/MOV/HEIC, WAV, AVI,
/// SQLite, EXE/DLL) and works out where each file ends.
/// Meant for FAT/exFAT volumes and for NTFS volumes whose MFT is damaged. Read-only: nothing is written to the volume.
/// The files come back without their names or folders, and a fragmented file is cut at the first gap.
/// </summary>
public sealed class FileCarver
{
    /// <summary>Granularity of the scan: every file system starts a file on a sector boundary.</summary>
    public const int SectorSize = 512;

    /// <summary>A file whose end has to be searched for is not carved past this: it is not worth scanning that far.</summary>
    public const long MaxFileSize = 256L << 20;

    /// <summary>A file whose header states its own size can be much larger (video), because nothing is scanned to find its end.</summary>
    public const long MaxKnownLength = 16L << 30;

    private const int ChunkSize = 4 << 20;
    private const int ReportEveryChunks = 8;
    private const int MaxConsecutiveReadErrors = 64;
    private const int MaxCentralDirectoryBytes = 4 << 20;

    public Task<ScanResult> ScanAsync(
        string drive,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(drive, progress, ct), ct);

    private static ScanResult Scan(string drive, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var volume = RawVolume.Open(drive);

        var (files, scanned, canceled) = Carve(volume.Read, ExpectedLength(drive), progress, ct);

        stopwatch.Stop();
        return new ScanResult(volume.Drive, SectorSize, scanned / SectorSize, stopwatch.Elapsed, files, canceled);
    }

    /// <summary>Only used to show a percentage: the scan itself stops when the volume does.</summary>
    private static long ExpectedLength(string drive)
    {
        try
        {
            return new DriveInfo(drive.Trim().TrimEnd('\\', ':')).TotalSize;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <param name="read">Reads from an absolute offset of the volume; returns fewer bytes than asked only at its end.</param>
    /// <param name="expectedLength">Size of the volume when known, just for the progress percentage (0 when unknown).</param>
    /// <remarks>
    /// Canceling does not throw: the scan stops and returns what it found so far, since that work is already done and
    /// <c>Canceled</c> tells the caller the list is partial.
    /// </remarks>
    /// <returns>The files found, how many bytes were scanned, and whether the scan was stopped before the end of the volume.</returns>
    public static (List<DeletedFile> Files, long BytesScanned, bool Canceled) Carve(
        ReadAt read, long expectedLength, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);

        var files = new List<DeletedFile>();
        var chunk = new byte[ChunkSize];
        long position = 0;       // start of the chunk being read
        long nextHeader = 0;     // headers before this offset are inside a file that was already carved
        int errors = 0;
        long chunkNumber = 0;
        bool canceled = false;

        while (!canceled)
        {
            if (ct.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            int bytesRead;
            try
            {
                bytesRead = read(position, chunk);
            }
            catch (IOException)
            {
                // A bad spot on the disk: skip it and keep going, but do not loop forever past the end.
                if (++errors > MaxConsecutiveReadErrors) break;
                position += ChunkSize;
                continue;
            }

            errors = 0;
            if (bytesRead < SectorSize) break;   // end of the volume

            for (int i = 0; i < bytesRead; i += SectorSize)
            {
                long offset = position + i;
                if (offset < nextHeader) continue;

                var kind = Identify(chunk.AsSpan(i, Math.Min(16, bytesRead - i)));
                if (kind is null) continue;

                // Measuring can read far ahead, so this is where a long stretch of work can be interrupted.
                if (ct.IsCancellationRequested)
                {
                    canceled = true;
                    bytesRead = i;
                    break;
                }

                var carved = Measure(kind.Value, read, offset);
                if (carved is not { } found) continue;

                files.Add(Describe(files.Count + 1, offset, found));
                nextHeader = (offset + found.Length + SectorSize - 1) / SectorSize * SectorSize;
            }

            position += bytesRead;
            if (++chunkNumber % ReportEveryChunks == 0)
            {
                long total = Math.Max(expectedLength, position);
                progress?.Report(new ScanProgress(position, total, files.Count,
                    $"Searching for file signatures... {position * 100.0 / total:0}%"));
            }

            if (bytesRead < ChunkSize) break;
        }

        if (!canceled) progress?.Report(new ScanProgress(position, position, files.Count));
        return (files, position, canceled);
    }

    private static DeletedFile Describe(int number, long offset, Carved found) => new(
        MftRecordNumber: number,
        Name: $"carved_{number:D5}.{found.Extension}",
        Path: $@"\Carved\{found.Extension.ToUpperInvariant()}",
        Size: found.Length,
        Modified: null,
        Chance: RecoveryChance.Carved,
        FreeClusterFraction: 1.0,
        Runs: [new DataRun(offset / SectorSize, (found.Length + SectorSize - 1) / SectorSize)],
        ResidentData: null);

    private enum Kind { Jpeg, Png, Gif, Bmp, Riff, Psd, Pdf, Zip, SevenZip, Mp4, Sqlite, Pe }

    private readonly record struct Carved(long Length, string Extension);

    /// <summary>Looks at the first bytes of a sector. Short signatures are only a hint: each measure checks the header in depth.</summary>
    private static Kind? Identify(ReadOnlySpan<byte> start) => start switch
    {
        [0xFF, 0xD8, 0xFF, ..] => Kind.Jpeg,
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Kind.Png,
        [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => Kind.Gif,                   // GIF87a / GIF89a
        [0x42, 0x4D, ..] => Kind.Bmp,                                                   // BM
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, ..] => Kind.Riff,                          // RIFF
        [0x38, 0x42, 0x50, 0x53, ..] => Kind.Psd,                                       // 8BPS
        [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => Kind.Pdf,                                 // %PDF-
        [0x50, 0x4B, 0x03, 0x04, ..] => Kind.Zip,                                       // PK\3\4
        [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, ..] => Kind.SevenZip,
        [_, _, _, _, 0x66, 0x74, 0x79, 0x70, ..] => Kind.Mp4,                           // ....ftyp
        [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, ..] => Kind.Sqlite,           // SQLite f
        [0x4D, 0x5A, ..] => Kind.Pe,                                                    // MZ
        _ => null
    };

    private static Carved? Measure(Kind kind, ReadAt read, long offset)
    {
        var reader = new FileReader(read, offset);
        (long length, string extension) = kind switch
        {
            Kind.Jpeg => (MeasureJpeg(reader), "jpg"),
            Kind.Png => (MeasurePng(reader), "png"),
            Kind.Gif => (MeasureGif(reader), "gif"),
            Kind.Bmp => (MeasureBmp(reader), "bmp"),
            Kind.Riff => MeasureRiff(reader),
            Kind.Psd => MeasurePsd(reader),
            Kind.Pdf => (MeasurePdf(reader), "pdf"),
            Kind.Zip => MeasureZip(reader),
            Kind.SevenZip => (MeasureSevenZip(reader), "7z"),
            Kind.Mp4 => MeasureMp4(reader),
            Kind.Sqlite => (MeasureSqlite(reader), "sqlite"),
            _ => MeasurePe(reader)
        };
        return length > 0 ? new Carved(length, extension) : null;
    }

    /// <summary>The end when the header says how long the file is: only believable if the last byte can be read.</summary>
    private static long IfComplete(FileReader r, long length) =>
        length is > 0 and <= MaxKnownLength && r.Byte(length - 1) >= 0 ? length : -1;

    private static string Ascii(FileReader r, long position, int count)
    {
        var chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            int b = r.Byte(position + i);
            if (b < 0x20 || b >= 0x7F) return "";
            chars[i] = (char)b;
        }
        return new string(chars);
    }

    /// <summary>The size is in the header, but "BM" is only two bytes: the DIB header and the pixel offset have to make sense too.</summary>
    private static long MeasureBmp(FileReader r)
    {
        long size = r.LittleEndian32(2);
        long pixelOffset = r.LittleEndian32(10);
        long headerSize = r.LittleEndian32(14);
        if (r.LittleEndian32(6) != 0) return -1;   // reserved
        if (headerSize is not (12 or 40 or 52 or 56 or 64 or 108 or 124)) return -1;
        if (pixelOffset < 14 + headerSize || pixelOffset >= size) return -1;

        long planes = r.LittleEndian16(headerSize == 12 ? 22 : 26);
        long bitsPerPixel = r.LittleEndian16(headerSize == 12 ? 24 : 28);
        if (planes != 1 || bitsPerPixel is not (1 or 2 or 4 or 8 or 16 or 24 or 32)) return -1;

        return IfComplete(r, size);
    }

    /// <summary>RIFF holds WAV, AVI and WebP: the chunk size at offset 4 covers everything after those 8 bytes.</summary>
    private static (long Length, string Extension) MeasureRiff(FileReader r)
    {
        string extension = Ascii(r, 8, 4) switch
        {
            "WAVE" => "wav",
            "AVI " => "avi",
            "WEBP" => "webp",
            _ => ""
        };
        if (extension.Length == 0) return (-1, "");

        long size = r.LittleEndian32(4);
        if (size < 12 || Ascii(r, 12, 4).Length == 0) return (-1, "");   // the first chunk must have a readable name

        return (IfComplete(r, size + 8), extension);
    }

    /// <summary>Walks the blocks (extensions and images) until the trailer byte.</summary>
    private static long MeasureGif(FileReader r)
    {
        if (r.LittleEndian16(6) == 0 || r.LittleEndian16(8) == 0) return -1;   // logical screen size

        int packed = r.Byte(10);
        if (packed < 0) return -1;
        long pos = 13 + ((packed & 0x80) != 0 ? 3L << ((packed & 7) + 1) : 0);

        while (pos <= MaxFileSize)
        {
            int block = r.Byte(pos++);
            switch (block)
            {
                case 0x3B:
                    return pos;   // trailer
                case 0x21:        // extension: a label, then sub-blocks
                    if (r.Byte(pos++) < 0) return -1;
                    break;
                case 0x2C:        // image: 9 bytes of descriptor, an optional local palette, the LZW code size, then sub-blocks
                    int descriptor = r.Byte(pos + 8);
                    if (descriptor < 0) return -1;
                    pos += 9 + ((descriptor & 0x80) != 0 ? 3L << ((descriptor & 7) + 1) : 0) + 1;
                    break;
                default:
                    return -1;
            }

            // Sub-blocks: a length byte and that many bytes, ended by a zero length.
            for (int size = r.Byte(pos++); size != 0; size = r.Byte(pos++))
            {
                if (size < 0 || pos > MaxFileSize) return -1;
                pos += size;
            }
        }
        return -1;
    }

    /// <summary>The page size and the page count are in the 100-byte header; the page count is only valid when the version fields agree.</summary>
    private static long MeasureSqlite(FileReader r)
    {
        if (!r.Matches(0, "SQLite format 3\0"u8)) return -1;

        long pageSize = r.BigEndian16(16);
        if (pageSize == 1) pageSize = 65536;
        if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0) return -1;

        // Payload fractions are fixed by the format, and a page count of 0 or a stale one means the header cannot be trusted.
        if (r.Byte(21) != 64 || r.Byte(22) != 32 || r.Byte(23) != 32) return -1;
        long pages = r.BigEndian32(28);
        if (pages <= 0 || r.BigEndian32(24) != r.BigEndian32(92)) return -1;

        return IfComplete(r, pages * pageSize);
    }

    /// <summary>The 32-byte start header holds the offset and size of the last block, and a CRC that proves the header is real.</summary>
    private static long MeasureSevenZip(FileReader r)
    {
        var header = r.Slice(12, 20);
        if (header.Length != 20 || Crc32(header) != r.LittleEndian32(8)) return -1;

        long nextOffset = BitConverter.ToInt64(header, 0);
        long nextSize = BitConverter.ToInt64(header, 8);
        if (nextOffset < 0 || nextSize < 0 || nextOffset > MaxKnownLength || nextSize > MaxKnownLength) return -1;

        return IfComplete(r, 32 + nextOffset + nextSize);
    }

    private static long Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>
    /// ISO base media (MP4, MOV, M4A, 3GP, HEIC...): a chain of boxes that each start with their own size, so the file ends
    /// where the chain of known top-level boxes does. It must hold a movie or media box, or it is only a stray "ftyp".
    /// </summary>
    private static (long Length, string Extension) MeasureMp4(FileReader r)
    {
        string[] topLevel = ["ftyp", "moov", "mdat", "free", "skip", "wide", "pnot", "uuid", "meta", "moof", "mfra", "sidx", "styp", "pdin", "junk"];

        long firstSize = r.BigEndian32(0);
        string brand = Ascii(r, 8, 4);
        if (firstSize is < 12 or > 512 || brand.Length == 0) return (-1, "");

        long pos = 0;
        bool hasMedia = false;
        while (pos <= MaxKnownLength)
        {
            long size = r.BigEndian32(pos);
            string type = Ascii(r, pos + 4, 4);
            if (size < 0 || !topLevel.Contains(type)) break;

            if (size == 1) size = r.BigEndian64(pos + 8);   // 64-bit size
            else if (size == 0) return (-1, "");           // "until the end of the file": the end is unknowable
            if (size < 8 || pos + size > MaxKnownLength) return (-1, "");

            hasMedia |= type is "moov" or "mdat" or "moof";
            pos += size;
        }

        return hasMedia ? (IfComplete(r, pos), Mp4Extension(brand)) : (-1, "");
    }

    private static string Mp4Extension(string brand) => brand switch
    {
        "qt  " => "mov",
        "M4A " or "M4B " => "m4a",
        "M4V " or "M4VH" or "M4VP" => "m4v",
        "heic" or "heix" or "hevc" or "heim" or "heis" or "mif1" or "msf1" => "heic",
        "avif" or "avis" => "avif",
        "crx " => "cr3",
        _ when brand.StartsWith("3gp", StringComparison.Ordinal) => "3gp",
        _ when brand.StartsWith("3g2", StringComparison.Ordinal) => "3g2",
        _ => "mp4"
    };

    /// <summary>
    /// Photoshop: five sections, each prefixed by its length, then the merged image. That one is raw or RLE (whose byte
    /// counts are in a table); a ZIP-compressed image has no length to read, so such a file is not carved.
    /// </summary>
    private static (long Length, string Extension) MeasurePsd(FileReader r)
    {
        long version = r.BigEndian16(4);
        if (version is not (1 or 2)) return (-1, "");
        bool big = version == 2;   // PSB: 8-byte lengths and 4-byte RLE counts

        for (int i = 6; i < 12; i++)
        {
            if (r.Byte(i) != 0) return (-1, "");   // reserved
        }

        long channels = r.BigEndian16(12), height = r.BigEndian32(14), width = r.BigEndian32(18);
        long depth = r.BigEndian16(22), mode = r.BigEndian16(24);
        long limit = big ? 300_000 : 30_000;
        if (channels is < 1 or > 56 || height < 1 || height > limit || width < 1 || width > limit) return (-1, "");
        if (depth is not (1 or 8 or 16 or 32) || mode is < 0 or > 9) return (-1, "");

        long pos = 26;
        for (int section = 0; section < 3; section++)   // color mode data, image resources, layer and mask info
        {
            bool eightBytes = big && section == 2;
            long length = eightBytes ? r.BigEndian64(pos) : r.BigEndian32(pos);
            if (length < 0 || length > MaxKnownLength) return (-1, "");
            pos += (eightBytes ? 8 : 4) + length;
        }

        long compression = r.BigEndian16(pos);
        pos += 2;
        long end;
        switch (compression)
        {
            case 0:
                end = pos + (width * depth + 7) / 8 * height * channels;
                break;
            case 1:
                long rows = height * channels;
                long total = 0;
                long entry = big ? 4 : 2;
                if (rows * entry > MaxCentralDirectoryBytes) return (-1, "");
                for (long row = 0; row < rows; row++)
                {
                    long count = big ? r.BigEndian32(pos + row * entry) : r.BigEndian16(pos + row * entry);
                    if (count < 0) return (-1, "");
                    total += count;
                }
                end = pos + rows * entry + total;
                break;
            default:
                return (-1, "");
        }

        return (IfComplete(r, end), big ? "psb" : "psd");
    }

    /// <summary>
    /// Portable Executable: the file ends after its last section or its signature, whichever is later. Data appended after
    /// that (installers, self-extractors) is not part of what the headers describe, so it is left out.
    /// </summary>
    private static (long Length, string Extension) MeasurePe(FileReader r)
    {
        long peOffset = r.LittleEndian32(0x3C);
        if (peOffset is < 0x40 or > 0x1000 || !r.Matches(peOffset, "PE\0\0"u8)) return (-1, "");

        long sections = r.LittleEndian16(peOffset + 6);
        long optionalSize = r.LittleEndian16(peOffset + 20);
        long characteristics = r.LittleEndian16(peOffset + 22);
        long magic = r.LittleEndian16(peOffset + 24);
        if (sections is < 1 or > 96 || optionalSize < 96 || (characteristics & 0x0002) == 0) return (-1, "");
        if (magic is not (0x10B or 0x20B)) return (-1, "");

        long end = 0;
        long table = peOffset + 24 + optionalSize;
        for (int i = 0; i < sections; i++)
        {
            long rawSize = r.LittleEndian32(table + i * 40 + 16);
            long rawPointer = r.LittleEndian32(table + i * 40 + 20);
            if (rawSize < 0 || rawPointer < 0) return (-1, "");
            end = Math.Max(end, rawPointer + rawSize);
        }

        // The Authenticode signature (data directory 4) is stored after the sections and is addressed by file offset.
        long directories = peOffset + 24 + (magic == 0x20B ? 112 : 96);
        if (optionalSize >= directories - (peOffset + 24) + 5 * 8)
        {
            long signatureOffset = r.LittleEndian32(directories + 4 * 8);
            long signatureSize = r.LittleEndian32(directories + 4 * 8 + 4);
            if (signatureOffset > 0 && signatureSize > 0 && signatureOffset + signatureSize < MaxFileSize)
                end = Math.Max(end, signatureOffset + signatureSize);
        }

        return (end > table ? IfComplete(r, end) : -1, (characteristics & 0x2000) != 0 ? "dll" : "exe");
    }

    /// <summary>Walks the marker segments so the thumbnail some cameras embed does not end the file early.</summary>
    private static long MeasureJpeg(FileReader r)
    {
        long pos = 2;   // after SOI
        while (pos <= MaxFileSize)
        {
            if (r.Byte(pos) != 0xFF) return -1;

            int marker;
            do { marker = r.Byte(++pos); } while (marker == 0xFF);   // fill bytes
            if (marker <= 0) return -1;
            pos++;

            if (marker == 0xD9) return pos;                                       // EOI
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8) continue;        // markers without a length

            long length = r.BigEndian16(pos);
            if (length < 2) return -1;
            pos += length;

            if (marker != 0xDA) continue;

            // Start of scan: entropy-coded data runs until the next real marker (stuffed 00 and RSTn are data).
            while (pos <= MaxFileSize)
            {
                int b = r.Byte(pos);
                if (b < 0) return -1;
                if (b != 0xFF) { pos++; continue; }

                int next = r.Byte(pos + 1);
                if (next < 0) return -1;
                if (next == 0xFF) { pos++; continue; }
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7) { pos += 2; continue; }
                break;
            }
        }
        return -1;
    }

    /// <summary>PNG is a list of length-prefixed chunks, so its size is exact: it ends after IEND.</summary>
    private static long MeasurePng(FileReader r)
    {
        const long Ihdr = 0x49484452, Iend = 0x49454E44;
        long pos = 8;
        bool first = true;

        while (pos <= MaxFileSize)
        {
            long length = r.BigEndian32(pos);
            long type = r.BigEndian32(pos + 4);
            if (length < 0 || type < 0 || length > MaxFileSize) return -1;
            if (first && (type != Ihdr || length != 13)) return -1;
            first = false;

            for (int shift = 24; shift >= 0; shift -= 8)
            {
                if (!char.IsAsciiLetter((char)((type >> shift) & 0xFF))) return -1;
            }

            pos += 12 + length;   // length, type, data, CRC
            if (type == Iend) return r.Byte(pos - 1) >= 0 ? pos : -1;
        }
        return -1;
    }

    /// <summary>
    /// Ends at the last %%EOF of the file. A linearized PDF or one with incremental updates has more than one, and
    /// what follows those is more objects (a digit), whereas after the real end comes whatever was on the disk before.
    /// </summary>
    private static long MeasurePdf(FileReader r)
    {
        for (long pos = 5; pos <= MaxFileSize; pos++)
        {
            int b = r.Byte(pos);
            if (b < 0) return -1;
            if (b != '%' || !r.Matches(pos, "%%EOF"u8)) continue;

            long end = pos + 5;
            if (r.Byte(end) == '\r') end++;
            if (r.Byte(end) == '\n') end++;

            if (r.Byte(end) is >= '0' and <= '9') { pos = end - 1; continue; }
            return end;
        }
        return -1;
    }

    /// <summary>
    /// Ends after the end-of-central-directory record. The record is checked against the directory it points to,
    /// so a ZIP stored inside this one does not end it. The directory also tells a Word, Excel or PowerPoint file apart.
    /// </summary>
    private static (long Length, string Extension) MeasureZip(FileReader r)
    {
        const int EocdSize = 22;

        for (long pos = 4; pos <= MaxFileSize; pos++)
        {
            int b = r.Byte(pos);
            if (b < 0) return (-1, "zip");
            if (b != 'P' || !r.Matches(pos, [0x50, 0x4B, 0x05, 0x06])) continue;

            long directorySize = r.LittleEndian32(pos + 12);
            long directoryOffset = r.LittleEndian32(pos + 16);
            long commentLength = r.LittleEndian16(pos + 20);
            if (directorySize < 0 || directoryOffset < 0 || commentLength < 0) return (-1, "zip");

            // ZIP64 stores these as 0xFFFFFFFF; the check is skipped then (the signature and the end are all there is).
            bool zip64 = directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF;
            if (!zip64 && directoryOffset + directorySize != pos) continue;

            long end = pos + EocdSize + commentLength;
            if (r.Byte(end - 1) < 0) return (-1, "zip");

            return (end, zip64 ? "zip" : ExtensionOf(r.Slice(directoryOffset, directorySize)));
        }
        return (-1, "zip");
    }

    private static string ExtensionOf(ReadOnlySpan<byte> centralDirectory)
    {
        if (centralDirectory.IndexOf("[Content_Types].xml"u8) < 0) return "zip";
        if (centralDirectory.IndexOf("word/"u8) >= 0) return "docx";
        if (centralDirectory.IndexOf("xl/"u8) >= 0) return "xlsx";
        if (centralDirectory.IndexOf("ppt/"u8) >= 0) return "pptx";
        return "zip";
    }

    /// <summary>Buffered, bounds-checked reader over the bytes that follow a header (offsets are relative to it).</summary>
    private sealed class FileReader(ReadAt read, long start)
    {
        private readonly byte[] _buffer = new byte[64 << 10];
        private long _bufferStart = -1;
        private int _bufferLength;

        /// <summary>The byte at <paramref name="position"/>, or -1 past the end of the volume or on a read error.</summary>
        public int Byte(long position)
        {
            if (position < 0 || position > MaxKnownLength) return -1;

            if (_bufferStart < 0 || position < _bufferStart || position >= _bufferStart + _bufferLength)
            {
                _bufferStart = position;
                try
                {
                    _bufferLength = read(start + position, _buffer);
                }
                catch (IOException)
                {
                    _bufferLength = 0;
                }
                if (_bufferLength <= 0)
                {
                    _bufferStart = -1;
                    return -1;
                }
            }
            return _buffer[position - _bufferStart];
        }

        public bool Matches(long position, ReadOnlySpan<byte> expected)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (Byte(position + i) != expected[i]) return false;
            }
            return true;
        }

        public long BigEndian16(long position) => Combine(position, 2, bigEndian: true);
        public long BigEndian32(long position) => Combine(position, 4, bigEndian: true);
        public long BigEndian64(long position) => Combine(position, 8, bigEndian: true);
        public long LittleEndian16(long position) => Combine(position, 2, bigEndian: false);
        public long LittleEndian32(long position) => Combine(position, 4, bigEndian: false);

        /// <summary>Reads a stretch of the file; empty when it is too large to be a central directory or cannot be read.</summary>
        public byte[] Slice(long position, long length)
        {
            if (length is <= 0 or > MaxCentralDirectoryBytes) return [];

            var data = new byte[length];
            try
            {
                return read(start + position, data) == length ? data : [];
            }
            catch (IOException)
            {
                return [];
            }
        }

        private long Combine(long position, int count, bool bigEndian)
        {
            long value = 0;
            for (int i = 0; i < count; i++)
            {
                int b = Byte(position + i);
                if (b < 0) return -1;
                value |= (long)b << (8 * (bigEndian ? count - 1 - i : i));
            }
            return value;
        }
    }
}
