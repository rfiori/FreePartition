using System.Text;
using FreePartition.Core.Disks;

namespace FreePartition.Core.Ntfs;

public enum PreviewKind
{
    Image,
    Text,
    Hex
}

public sealed record FilePreview(PreviewKind Kind, byte[] Data, bool Truncated, string? Text);

/// <summary>
/// Reads the first bytes of a deleted file so the user can look at it before recovering.
/// Read-only on the source volume; nothing is written anywhere.
/// </summary>
public static class FilePreviewReader
{
    public const int MaxImageBytes = 16 << 20;
    public const int MaxTextBytes = 64 << 10;
    public const int MaxHexBytes = 512;

    /// <summary>The icon is in the resource section, usually near the end, so the whole program is read; bigger ones are skipped.</summary>
    public const int MaxProgramBytes = 32 << 20;

    private static readonly HashSet<string> IconExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "exe", "dll", "scr", "ocx", "cpl" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "ico" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "csv", "md", "log", "json", "xml", "html", "htm", "ini", "cs", "xaml", "js", "css", "sql", "cfg" };

    public static PreviewKind KindOf(string fileName)
    {
        string extension = Path.GetExtension(fileName).TrimStart('.');
        if (ImageExtensions.Contains(extension)) return PreviewKind.Image;
        return TextExtensions.Contains(extension) ? PreviewKind.Text : PreviewKind.Hex;
    }

    public static FilePreview Read(ScanResult result, DeletedFile file)
    {
        if (!file.IsRecoverable)
            throw new InvalidOperationException("This file has no readable data (compressed, encrypted or empty).");

        var kind = KindOf(file.Name);
        int limit = kind switch
        {
            PreviewKind.Image => MaxImageBytes,
            PreviewKind.Text => MaxTextBytes,
            _ => MaxHexBytes
        };

        // A truncated image cannot be decoded reliably: fall back to the hex view.
        if (kind == PreviewKind.Image && file.Size > limit)
        {
            kind = PreviewKind.Hex;
            limit = MaxHexBytes;
        }

        var data = ReadBytes(result, file, limit);
        bool truncated = file.Size > data.Length;
        string text = kind == PreviewKind.Text ? DecodeText(data) : FormatHex(data);
        return new FilePreview(kind, data, truncated, text);
    }

    /// <summary>Programs whose icon can be shown.</summary>
    public static bool HasIcon(string fileName) =>
        IconExtensions.Contains(Path.GetExtension(fileName).TrimStart('.'));

    /// <summary>
    /// The icon of a carved or deleted program as the bytes of an <c>.ico</c> file, or null when it has none or the file is
    /// too large or damaged to read it. Read-only, like <see cref="Read"/>.
    /// </summary>
    public static byte[]? ReadIcon(ScanResult result, DeletedFile file)
    {
        if (!file.IsRecoverable || !HasIcon(file.Name) || file.Size > MaxProgramBytes) return null;

        return PeIconExtractor.Extract(ReadBytes(result, file, MaxProgramBytes));
    }

    /// <summary>
    /// The raw bytes a thumbnail is made from: the image itself, or the whole program for its icon. Null when the file is
    /// neither or is too large. Only reads; the decoding is left to the caller so it can run on several threads.
    /// </summary>
    public static byte[]? ReadThumbnailSource(ScanResult result, DeletedFile file)
    {
        if (!file.IsRecoverable) return null;

        int limit = HasIcon(file.Name) ? MaxProgramBytes : KindOf(file.Name) == PreviewKind.Image ? MaxImageBytes : 0;
        return limit == 0 || file.Size > limit ? null : ReadBytes(result, file, limit);
    }

    private static byte[] ReadBytes(ScanResult result, DeletedFile file, int limit)
    {
        int length = (int)Math.Min(file.Size, limit);
        byte[] data = new byte[length];
        int read;

        if (file.ResidentData is { } resident)
        {
            read = Math.Min(length, resident.Length);
            resident.AsSpan(0, read).CopyTo(data);
        }
        else
        {
            using var volume = RawVolume.Open(result.Drive);
            var reader = new RunReader(volume, file.Runs, result.BytesPerCluster, file.Size);
            read = reader.Read(0, data);
        }

        if (read < length) Array.Resize(ref data, read);
        return data;
    }

    public static string DecodeText(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>Classic hex dump: offset, 16 bytes per line, printable characters on the right.</summary>
    public static string FormatHex(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            var line = data.Slice(offset, Math.Min(16, data.Length - offset));
            builder.Append(offset.ToString("X8")).Append("  ");

            for (int i = 0; i < 16; i++)
                builder.Append(i < line.Length ? line[i].ToString("X2") : "  ").Append(' ');

            builder.Append(' ');
            foreach (byte b in line)
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            builder.AppendLine();
        }
        return builder.ToString();
    }
}
