using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FreePartition.Wpf;

/// <summary>"Share the app" icons, in the toolbar (right side) and in the status bar. Hidden in WinPE.</summary>
public partial class MainWindow
{
    private const string ShareUrl = "https://github.com/rfiori/FreePartition";
    private const string ShareText = "FreePartition: a free, open-source partition manager for Windows";

    /// <summary>A network: its tag (used by <see cref="Share_Click"/>), tooltip, color on hover and white icon.</summary>
    /// <param name="Icon">Builds the icon for a button of the given size (22 in the status bar, 28 in the toolbar).</param>
    private sealed record ShareNetwork(string Tag, string ToolTip, Brush Color, Func<double, UIElement> Icon);

    private static readonly ShareNetwork[] ShareNetworks =
    [
        new("whatsapp", "Share on WhatsApp", Solid("#25D366"), s => Glyph("", s * 0.5)),
        new("instagram", "Copy the link and open Instagram", InstagramGradient(), s => PathIcon(
            "M7 1h10a6 6 0 0 1 6 6v10a6 6 0 0 1-6 6H7a6 6 0 0 1-6-6V7a6 6 0 0 1 6-6zM7 3a4 4 0 0 0-4 4v10a4 4 0 0 0 4 4h10a4 4 0 0 0 4-4V7a4 4 0 0 0-4-4zM12 6.5a5.5 5.5 0 1 1 0 11a5.5 5.5 0 1 1 0-11zM12 8.5a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7zM17.8 4.8a1.4 1.4 0 1 1 0 2.8a1.4 1.4 0 1 1 0-2.8z",
            s * 0.5)),
        new("x", "Share on X", Solid("#000000"), s => PathIcon(
            "M18.901 1.153h3.68l-8.04 9.19L24 22.846h-7.406l-5.8-7.584-6.638 7.584H.474l8.6-9.83L0 1.154h7.594l5.243 6.932ZM17.61 20.644h2.039L6.486 3.24H4.298Z",
            s * 0.45)),
        new("facebook", "Share on Facebook", Solid("#1877F2"), s => PathIcon(
            "M13.5 24V13.2h3.6l.55-4.2H13.5V6.3c0-1.2.34-2.03 2.07-2.03h2.2V.5A29.6 29.6 0 0 0 14.55.33C11.36.33 9.2 2.28 9.2 5.85V9H5.6v4.2h3.6V24z",
            s * 0.5)),
        new("linkedin", "Share on LinkedIn", Solid("#0A66C2"), s => PathIcon(
            "M4.98 3.5C4.98 4.88 3.87 6 2.5 6S.02 4.88.02 3.5 1.13 1 2.5 1s2.48 1.12 2.48 2.5zM.4 8h4.2v14H.4zM8 8h4v1.92h.06C12.62 8.86 14 7.8 16.06 7.8 20.32 7.8 21 10.6 21 14.24V22h-4.2v-6.9c0-1.64-.03-3.76-2.3-3.76-2.3 0-2.64 1.8-2.64 3.65V22H8z",
            s * 0.45)),
        new("reddit", "Share on Reddit", Solid("#FF4500"), s => RedditIcon(s * 0.55)),
        new("telegram", "Share on Telegram", Solid("#26A5E4"), s => PathIcon(
            "M1 11.2 22.6 2.4 19 21.6 12.6 16.8 9.4 20.2 9.2 15 18.8 6.2 7.2 13.6z", s * 0.5, new Thickness(0, 0, s * 0.05, 0))),
        new("bluesky", "Share on Bluesky", Solid("#0085FF"), s => PathIcon(
            "M12 10.8C10.9 8.6 7.9 4.5 5.1 2.6 2.4.7 1.4 1 .7 1.3 0 1.7 0 3 0 3.7c0 .8.4 6.4.7 7.3.9 2.9 4 3.9 6.9 3.6-4.2.6-7.9 2.2-3 7.7 5.4 5.6 7.4-1.2 8.4-4.6 1 3.4 2.2 9.9 8.3 4.6 4.6-4.6 1.3-7-2.9-7.6 2.9.3 6-.7 6.8-3.6.3-.9.7-6.5.7-7.3 0-.8-.1-2-.8-2.4-.7-.3-1.7-.6-4.4 1.3C16.1 4.5 13.1 8.6 12 10.8z",
            s * 0.5)),
        new("threads", "Share on Threads", Solid("#000000"), s => new TextBlock
        {
            Text = "@", FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = s * 0.6,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -s * 0.09, 0, 0)
        }),
        new("email", "Share by email", Solid("#0078D4"), s => Glyph("", s * 0.5)),
    ];

    /// <summary>Fills the toolbar and the status bar with the share icons (or hides them in WinPE).</summary>
    private void BuildShareButtons()
    {
        if (_isWinPe)
        {
            ToolbarShareIcons.Visibility = Visibility.Collapsed;
            SharePanel.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var network in ShareNetworks)
        {
            ToolbarShareIcons.Children.Add(CreateShareButton(network, "ToolbarShareButton", 28));
            FooterShareIcons.Children.Add(CreateShareButton(network, "ShareButton", 22));
        }
    }

    private Button CreateShareButton(ShareNetwork network, string style, double size)
    {
        var button = new Button
        {
            Style = (Style)FindResource(style),
            Tag = network.Tag,
            ToolTip = network.ToolTip,
            BorderBrush = network.Color,   // the style shows it on hover
            Content = network.Icon(size)
        };
        button.Click += Share_Click;
        return button;
    }

    private static SolidColorBrush Solid(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private static Brush InstagramGradient()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xFE, 0xDA, 0x75), 0),
                new GradientStop(Color.FromRgb(0xFA, 0x7E, 0x1E), 0.25),
                new GradientStop(Color.FromRgb(0xD6, 0x29, 0x76), 0.5),
                new GradientStop(Color.FromRgb(0x96, 0x2F, 0xBF), 0.75),
                new GradientStop(Color.FromRgb(0x4F, 0x5B, 0xD5), 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static Path PathIcon(string data, double size, Thickness margin = default) => new()
    {
        Data = Geometry.Parse(data),
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Fill = Brushes.White,
        Margin = margin
    };

    private static TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Simplified Reddit head: antenna, ears and face, with the eyes in the button's color.</summary>
    private static Viewbox RedditIcon(double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Path
        {
            Stroke = Brushes.White, StrokeThickness = 1.6, Data = Geometry.Parse("M12 9 13.6 3.2 17.5 4.2")
        });
        canvas.Children.Add(new Path
        {
            Fill = Brushes.White,
            Data = Geometry.Parse(
                "M17.5 2.2a2 2 0 1 1 0 4a2 2 0 1 1 0-4zM3.5 9.6a2.3 2.3 0 1 1 0 4.6a2.3 2.3 0 1 1 0-4.6zM20.5 9.6a2.3 2.3 0 1 1 0 4.6a2.3 2.3 0 1 1 0-4.6zM12 8.6c5 0 9.2 2.8 9.2 6.6S17 21.8 12 21.8 2.8 19 2.8 15.2 7 8.6 12 8.6z")
        });
        var eyes = new Path
        {
            Data = Geometry.Parse("M8.6 12.6a1.5 1.5 0 1 1 0 3a1.5 1.5 0 1 1 0-3zM15.4 12.6a1.5 1.5 0 1 1 0 3a1.5 1.5 0 1 1 0-3z")
        };
        eyes.SetBinding(Shape.FillProperty, new Binding(nameof(Background))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
        });
        canvas.Children.Add(eyes);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    /// <summary>Opens the share page of the clicked network (or a new email) with the app's link.</summary>
    private void Share_Click(object sender, RoutedEventArgs e)
    {
        string url = Uri.EscapeDataString(ShareUrl);
        string text = Uri.EscapeDataString(ShareText);
        string? network = (sender as FrameworkElement)?.Tag as string;
        if (network == "instagram")
        {
            // Instagram has no link-sharing page: the text goes to the clipboard to paste in a post, story or bio.
            try { Clipboard.SetText($"{ShareText}\n{ShareUrl}"); } catch { /* clipboard busy: still open the site */ }
        }
        string? target = network switch
        {
            "instagram" => "https://www.instagram.com/",
            "facebook" => $"https://www.facebook.com/sharer/sharer.php?u={url}",
            "x" => $"https://twitter.com/intent/tweet?text={text}&url={url}",
            "linkedin" => $"https://www.linkedin.com/sharing/share-offsite/?url={url}",
            "whatsapp" => $"https://wa.me/?text={text}%20{url}",
            "reddit" => $"https://www.reddit.com/submit?url={url}&title={text}",
            "telegram" => $"https://t.me/share/url?url={url}&text={text}",
            "bluesky" => $"https://bsky.app/intent/compose?text={text}%20{url}",
            "threads" => $"https://www.threads.net/intent/post?text={text}%20{url}",
            "email" => $"mailto:?subject={Uri.EscapeDataString("FreePartition")}&body={text}%0D%0A{url}",
            _ => null
        };
        if (target is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the share page:\n{ex.Message}", "Share",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
