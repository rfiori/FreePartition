using System.Windows;

namespace FreePartition.Wpf;

/// <summary>
/// Keeps a window inside the usable screen area. Small resolutions (WinPE usually starts at 800x600 or 1024x768)
/// would otherwise show the window larger than the screen, with its edges and buttons out of reach.
/// </summary>
public static class WindowFit
{
    /// <summary>Space left for the window frame/title bar (the work area does not include the frame).</summary>
    private const double Margin = 16;

    /// <summary>Call after InitializeComponent. Shrinks the size and the minimum size to the work area.</summary>
    /// <param name="maximizeWhenShrunk">Maximize when the window had to be reduced (the main window).</param>
    public static void Apply(Window window, bool maximizeWhenShrunk = false)
    {
        Rect area = SystemParameters.WorkArea;
        double maxWidth = Math.Max(320, area.Width - Margin);
        double maxHeight = Math.Max(240, area.Height - Margin);

        bool shrunk = false;
        if (!double.IsNaN(window.Width) && window.Width > maxWidth) { window.Width = maxWidth; shrunk = true; }
        if (!double.IsNaN(window.Height) && window.Height > maxHeight) { window.Height = maxHeight; shrunk = true; }

        // A minimum larger than the screen would force the window back out of it.
        if (window.MinWidth > maxWidth) window.MinWidth = maxWidth;
        if (window.MinHeight > maxHeight) window.MinHeight = maxHeight;

        // Content-sized windows (SizeToContent) cannot grow past the screen either.
        if (window.SizeToContent != SizeToContent.Manual)
        {
            window.MaxWidth = maxWidth;
            window.MaxHeight = maxHeight;
        }

        if (shrunk && maximizeWhenShrunk) window.WindowState = WindowState.Maximized;

        // CenterOwner centers on the parent; if the parent is partly off-screen the child would be too.
        window.Loaded += (_, _) => KeepOnScreen(window);
    }

    private static void KeepOnScreen(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        Rect area = SystemParameters.WorkArea;
        double left = Math.Min(Math.Max(window.Left, area.Left), Math.Max(area.Left, area.Right - window.ActualWidth));
        double top = Math.Min(Math.Max(window.Top, area.Top), Math.Max(area.Top, area.Bottom - window.ActualHeight));
        if (!double.IsNaN(left)) window.Left = left;
        if (!double.IsNaN(top)) window.Top = top;
    }
}
