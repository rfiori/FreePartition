namespace FreePartition.Core.Util;

public static class ByteFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(ulong bytes)
    {
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < Units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{value:0.##} {Units[i]}";
    }

    public static string Format(long bytes) => Format((ulong)Math.Max(0, bytes));
}
