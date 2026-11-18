using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreePartition.Core.Disks;

/// <summary>
/// READ-ONLY access to the raw volume (\\.\C:, or \\?\Volume{guid} for a volume without a drive letter).
/// A raw volume requires sector-aligned reads; this type handles the alignment
/// (it uses 4096 bytes, which works for both 512-byte and 4Kn disks).
/// Requires running as administrator.
/// </summary>
public sealed partial class RawVolume : IDisposable
{
    private const int Alignment = 4096;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _handle;

    public string Drive { get; }

    private RawVolume(SafeFileHandle handle, string drive)
    {
        _handle = handle;
        Drive = drive;
    }

    /// <param name="volume">"C", "C:" or "C:\", or the GUID path of a volume without a letter ("\\?\Volume{guid}\")</param>
    public static RawVolume Open(string volume)
    {
        string name, device;
        if (VolumeNames.NormalizeGuidPath(volume) is { } guid)
        {
            name = guid;
            device = guid.TrimEnd('\\');   // with the final backslash CreateFile would open the file system, not the volume
        }
        else
        {
            string letter = (volume ?? "").Trim().TrimEnd('\\', ':').ToUpperInvariant();
            if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
                throw new ArgumentException("Enter a valid drive letter (e.g. C).", nameof(volume));
            name = letter;
            device = $@"\\.\{letter}:";
        }

        var handle = CreateFile(device, GenericRead, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error,
                $"Could not open volume {VolumeNames.Describe(name)} (run as administrator).");
        }

        return new RawVolume(handle, name);
    }

    /// <summary>
    /// Reads bytes from any offset (alignment is handled internally).
    /// Returns how many bytes were actually read (fewer than requested only at the end of the volume).
    /// </summary>
    public int Read(long offset, Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;

        long start = offset / Alignment * Alignment;
        long end = (offset + destination.Length + Alignment - 1) / Alignment * Alignment;
        int size = checked((int)(end - start));

        byte[] temp = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int bytesRead = ReadRaw(start, temp.AsSpan(0, size));
            int skip = (int)(offset - start);
            int available = Math.Max(0, bytesRead - skip);
            int copy = Math.Min(available, destination.Length);
            temp.AsSpan(skip, copy).CopyTo(destination);
            return copy;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private int ReadRaw(long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
