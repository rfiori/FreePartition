using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace FreePartition.Wpf;

/// <summary>
/// Shown by "Clone Windows" outside WinPE: explains how to get the WinPE USB drive the clone runs from.
/// </summary>
public partial class WinPeGuideWindow : Window
{
    /// <summary>The releases page, where the ISO (FreePartition-WinPE.iso) is attached to each release.</summary>
    public const string IsoUrl = "https://github.com/rfiori/FreePartition/releases/latest";

    public WinPeGuideWindow()
    {
        InitializeComponent();
        LinkIso.NavigateUri = new Uri(IsoUrl);
        TxtIsoUrl.Text = IsoUrl;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void Download_Click(object sender, RoutedEventArgs e) => Open(IsoUrl);

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(IsoUrl); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
