using System.IO;
using System.Windows.Media.Imaging;

namespace FreePartition.Wpf;

/// <summary>Decodes the bytes of an <c>.ico</c> file held in memory.</summary>
public static class IconDecoder
{
    /// <summary>The biggest size in the icon, frozen so it can be built on a worker thread. Null if it has no usable image.</summary>
    public static BitmapSource? Largest(byte[] ico)
    {
        using var stream = new MemoryStream(ico);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        frame?.Freeze();
        return frame;
    }
}
