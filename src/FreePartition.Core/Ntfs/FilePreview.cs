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

        bool truncated = file.Size > data.Length;
        string text = kind == PreviewKind.Text ? DecodeText(data) : FormatHex(data);
        return new FilePreview(kind, data, truncated, text);
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
