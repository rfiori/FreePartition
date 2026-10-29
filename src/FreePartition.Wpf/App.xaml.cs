using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FreePartition.Wpf;

public partial class App : Application
{
    private const int DwmaCaptionColor = 35;
    private const int DwmaTextColor = 36;

    // COLORREF is 0x00BBGGRR: dark blue (#1F3A5F) with white text.
    private const int ChildTitleBarColor = 0x5F3A1F;
    private const int ChildTitleBarTextColor = 0xFFFFFF;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent, new KeyEventHandler(OnWindowKeyDown));
    }

    /// <summary>
    /// Esc closes any window except the main one (closing it would quit the app). It uses the bubbling KeyDown, so
    /// a control that already used Esc (closing a drop-down or the date picker) does not close the window as well.
    /// </summary>
    private static void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || sender is not Window window || window is MainWindow) return;

        e.Handled = true;
        window.Close();
    }

    /// <summary>Child windows get a dark blue title bar; the main window keeps the system one.</summary>
    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window { Owner: not null } window) return;

        // The caption color needs Windows 11; on older versions the call just fails and the title bar stays as is.
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            int caption = ChildTitleBarColor, text = ChildTitleBarTextColor;
            DwmSetWindowAttribute(hwnd, DwmaCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmaTextColor, ref text, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
