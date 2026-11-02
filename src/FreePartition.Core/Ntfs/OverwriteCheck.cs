namespace FreePartition.Core.Ntfs;

/// <summary>Reads from a logical offset of a file's data; returns how many bytes were filled (like <see cref="RunReader.Read"/>).</summary>
public delegate int ReadAt(long offset, Span<byte> destination);

/// <summary>
/// Tells whether the data a deleted file points to was wiped: it looks at the start, the middle and the end of the
/// file, and only says yes when every byte it sees is zero. Read-only.
/// </summary>
public static class OverwriteCheck
{
    public const int SampleBytes = 4096;

    /// <summary>
    /// True when the samples were read completely and are all zeros. A short or failed read, or an empty file,
    /// is not proof of anything, so the answer is false. Only three spots are looked at, so a file that is zero
    /// there but not elsewhere would be called wiped; a real file is very unlikely to be zero in all three.
    /// </summary>
    public static bool IsZeroFilled(ReadAt read, long size)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (size <= 0) return false;

        int length = (int)Math.Min(size, SampleBytes);
        long middle = Math.Max(0, Math.Min(size / 2, size - length));
        var buffer = new byte[length];

        foreach (long offset in new HashSet<long> { 0, middle, size - length })
        {
            if (read(offset, buffer) < length) return false;
            if (buffer.AsSpan().IndexOfAnyExcept((byte)0) >= 0) return false;
        }
        return true;
    }
}
