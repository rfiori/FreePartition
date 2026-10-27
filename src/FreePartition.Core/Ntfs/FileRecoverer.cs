using System.Buffers;
using FreePartition.Core.Disks;

namespace FreePartition.Core.Ntfs;

/// <summary>
/// Copies the content of a deleted file to a destination folder.
/// The source volume is opened read-only, and the destination must NEVER be on the same disk
/// (writing there could overwrite the very clusters you want to recover).
/// </summary>
public sealed class FileRecoverer
{
    private const int BufferSize = 1 << 20;   // 1 MiB

    /// <returns>Path of the recovered file.</returns>
    /// <param name="keepFolders">Recreates the original folder structure under <paramref name="destinationFolder"/>.</param>
    public string Recover(ScanResult result, DeletedFile file, string destinationFolder, bool keepFolders = false)
    {
        if (!file.IsRecoverable)
            throw new InvalidOperationException("This file cannot be recovered (compressed, encrypted or without data).");

        string destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationFolder)) ?? "";
        if (destinationRoot.StartsWith(result.Drive + ":", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Choose a folder on ANOTHER disk. Writing to {result.Drive}: may overwrite the data you want to recover.");

        if (keepFolders)
            destinationFolder = Path.Combine([destinationFolder, .. FolderSegments(file.Path)]);

        Directory.CreateDirectory(destinationFolder);
        string destination = UniquePath(destinationFolder, SafeName(file.Name));

        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        if (file.ResidentData is not null)
        {
            output.Write(file.ResidentData);
            return destination;
        }

        using var volume = RawVolume.Open(result.Drive);
        var reader = new RunReader(volume, file.Runs, result.BytesPerCluster, file.Size);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long position = 0;
            while (position < file.Size)
            {
                int request = (int)Math.Min(BufferSize, file.Size - position);
                int bytesRead = reader.Read(position, buffer.AsSpan(0, request));
                if (bytesRead <= 0) break;
                output.Write(buffer, 0, bytesRead);
                position += bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return destination;
    }

    /// <summary>Original folder names, sanitized so they can never escape the destination folder.</summary>
    public static IEnumerable<string> FolderSegments(string path) =>
        path.Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(SafeName)
            .Where(s => s.Trim('.', ' ').Length > 0);

    private static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "recovered_file" : name;
    }

    private static string UniquePath(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;

        string baseName = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            path = Path.Combine(folder, $"{baseName} ({i}){extension}");
            if (!File.Exists(path)) return path;
        }
    }
}
