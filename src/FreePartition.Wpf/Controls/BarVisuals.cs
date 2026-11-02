using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FreePartition.Wpf;

/// <summary>Colors and sizes shared by the bars that show where a partition sits (move, resize, create, split).</summary>
internal static class BarVisuals
{
    public const double TrackHeight = 62;
    public const double BlockHeight = 54;
    public const double BlockTop = 3;
    public const double HandleSize = 12;

    public static readonly Brush Free = Frozen("#BDEBDD");               // free space: aqua green
    public static readonly Brush FreeBorder = Frozen("#7FCDB8");
    public static readonly Brush Partition = Frozen("#FFE9A8");
    public static readonly Brush PartitionBorder = Frozen("#E0B84C");
    public static readonly Brush UsageTrack = Frozen("#EAF3FB");
    public static readonly Brush UsageBorder = Frozen("#5B9BD5");
    public static readonly Brush Bracket = Frozen("#8A8A8A");

    /// <summary>The rounded aqua-green strip that stands for the free space the partition can use.</summary>
    public static Border CreateTrack(UIElement canvas) => new()
    {
        Height = TrackHeight, CornerRadius = new CornerRadius(7), Background = Free, BorderBrush = FreeBorder,
        BorderThickness = new Thickness(1), Child = canvas
    };

    /// <summary>The round grip drawn on an edge of the partition. Mouse input is handled by the canvas.</summary>
    public static Ellipse CreateHandle() => new()
    {
        Width = HandleSize, Height = HandleSize, Fill = Partition, Stroke = PartitionBorder, StrokeThickness = 1.5,
        IsHitTestVisible = false
    };

    public static void PlaceHandle(Ellipse handle, double edge)
    {
        Canvas.SetLeft(handle, edge - HandleSize / 2);
        Canvas.SetTop(handle, (TrackHeight - 2 - HandleSize) / 2);
    }

    public static Brush Frozen(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}

/// <summary>A partition drawn as a yellow rounded block: a used-space bar on top, then its name and size.</summary>
internal sealed class PartitionBlock : Border
{
    private readonly ColumnDefinition _used = new();
    private readonly ColumnDefinition _rest = new();
    private readonly Border _fill = new() { Background = BarVisuals.UsageBorder, CornerRadius = new CornerRadius(2) };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new()
    {
        FontSize = 11, Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis
    };

    public PartitionBlock()
    {
        Height = BarVisuals.BlockHeight;
        Background = BarVisuals.Partition;
        BorderBrush = BarVisuals.PartitionBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        IsHitTestVisible = false;

        var usage = new Grid();
        usage.ColumnDefinitions.Add(_used);
        usage.ColumnDefinitions.Add(_rest);
        usage.Children.Add(_fill);
        var bar = new Border
        {
            Height = 12, CornerRadius = new CornerRadius(3), Background = BarVisuals.UsageTrack,
            BorderBrush = BarVisuals.UsageBorder, BorderThickness = new Thickness(1), Margin = new Thickness(6, 6, 6, 3),
            Child = usage
        };

        var texts = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        texts.Children.Add(_title);
        texts.Children.Add(_subtitle);

        var stack = new StackPanel();
        stack.Children.Add(bar);
        stack.Children.Add(texts);
        Child = stack;
        Fraction = 0;
    }

    public string Title { set => _title.Text = value; }

    public string Subtitle { set => _subtitle.Text = value; }

    /// <summary>Share of the block that is in use, 0 to 1.</summary>
    public double Fraction
    {
        set
        {
            value = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
            _used.Width = new GridLength(Math.Max(value, 0.0001), GridUnitType.Star);
            _rest.Width = new GridLength(Math.Max(1 - value, 0.0001), GridUnitType.Star);
            _fill.Visibility = value > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

/// <summary>A bracket under a bar with a size in the middle, like a dimension line on a drawing.</summary>
internal sealed class SizeBracket : Grid
{
    private readonly TextBlock _text = new()
    {
        FontSize = 11, Foreground = Brushes.DimGray, Padding = new Thickness(6, 0, 6, 0),
        Background = SystemColors.ControlBrush, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0)
    };

    public SizeBracket()
    {
        Height = 20;
        Margin = new Thickness(0, 2, 0, 0);
        Children.Add(new Border
        {
            Height = 10, VerticalAlignment = VerticalAlignment.Top, BorderBrush = BarVisuals.Bracket,
            BorderThickness = new Thickness(1, 0, 1, 1), CornerRadius = new CornerRadius(0, 0, 3, 3),
            Margin = new Thickness(1, 0, 1, 0)
        });
        Children.Add(_text);
    }

    public string Text { get => _text.Text; set => _text.Text = value; }
}
