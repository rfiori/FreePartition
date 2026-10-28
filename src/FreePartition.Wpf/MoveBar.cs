using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Bar that shows a stretch of free space with a partition inside it, and lets the user drag the partition
/// left or right within that stretch. The value is the partition's offset on the disk, in whole MiB.
/// </summary>
public sealed class MoveBar : StackPanel
{
    private const long Granularity = 1L << 20;   // 1 MiB

    private static readonly Brush Blue = Frozen("#5B9BD5");
    private static readonly Brush LightBlue = Frozen("#CFE2F3");
    private static readonly Brush DarkBlue = Frozen("#2F6FA8");

    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeWE };
    private readonly Rectangle _rectPartition = new() { Fill = LightBlue, Stroke = DarkBlue, StrokeThickness = 1, Height = 54 };
    private readonly Rectangle _rectUsed = new() { Fill = Blue, Height = 54 };
    private readonly TextBlock _title = new() { IsHitTestVisible = false, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _before = new() { Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _after = new() { Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Right };

    private long _start, _end, _length, _used, _min, _max, _value;
    private double _grab;   // distance from the partition's left edge to the mouse when the drag started
    private bool _dragging;

    /// <summary>Raised when the user drags the partition (not when the value is set from code).</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Lowest and highest offset the partition can take (a whole MiB, or its current offset).</summary>
    public long MinValue => _min;
    public long MaxValue => _max;

    /// <summary>Offset of the partition on the disk, in bytes.</summary>
    public long Value
    {
        get => _value;
        set { _value = Clamp(value); Refresh(); }
    }

    /// <param name="start">Where the free stretch (which includes the partition) begins on the disk.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="current">Offset the partition has now.</param>
    public MoveBar(string title, long start, long end, long length, long used, long current)
    {
        _start = start;
        _end = end;
        _length = length;
        _used = used;
        _title.Text = title;

        // Destinations are whole MiB; the current position always stays reachable.
        long first = (Math.Max(start, 1L << 20) + Granularity - 1) / Granularity * Granularity;
        long last = (end - length) / Granularity * Granularity;
        _min = Math.Min(first, current);
        _max = Math.Max(last, current);
        _value = current;

        _canvas.Children.Add(_rectPartition);
        _canvas.Children.Add(_rectUsed);
        _canvas.Children.Add(_title);
        _canvas.SizeChanged += (_, _) => Refresh();
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            double x = e.GetPosition(_canvas).X;
            double left = Canvas.GetLeft(_rectPartition);
            // Grab the partition where it was clicked; a click on the free space centers the partition there.
            _grab = x >= left && x <= left + _rectPartition.Width ? x - left : _rectPartition.Width / 2;
            _dragging = _canvas.CaptureMouse();
            SetFromPosition(x);
        };
        _canvas.MouseMove += (_, e) =>
        {
            if (_dragging) SetFromPosition(e.GetPosition(_canvas).X);
        };
        _canvas.MouseLeftButtonUp += (_, _) =>
        {
            _dragging = false;
            _canvas.ReleaseMouseCapture();
        };

        Children.Add(new Border
        {
            BorderBrush = Blue, BorderThickness = new Thickness(1), Background = Frozen("#EEEEEE"),
            Height = 56, Child = _canvas
        });
        var labels = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        labels.Children.Add(_before);
        labels.Children.Add(_after);
        Children.Add(labels);
    }

    /// <summary>Free space left before the partition, in bytes.</summary>
    public long FreeBefore => _value - _start;

    /// <summary>Free space left after the partition, in bytes.</summary>
    public long FreeAfter => _end - (_value + _length);

    private long Clamp(long offset) => Math.Clamp(offset, _min, _max);

    private void SetFromPosition(double x)
    {
        if (_canvas.ActualWidth <= 0 || _end <= _start) return;
        double left = x - _grab;
        long bytes = _start + (long)(left / _canvas.ActualWidth * (_end - _start));

        // Snap to whole MiB, but let both ends of the range be exact.
        bytes = Clamp(bytes);
        if (bytes != _min && bytes != _max)
            bytes = bytes / Granularity * Granularity;
        bytes = Clamp(bytes);

        if (bytes == _value) return;
        _value = bytes;
        Refresh();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        _before.Text = $"Free before: {ByteFormatter.Format(Math.Max(0, FreeBefore))}";
        _after.Text = $"Free after: {ByteFormatter.Format(Math.Max(0, FreeAfter))}";

        double width = _canvas.ActualWidth;
        if (width <= 0 || _end <= _start) return;

        double scale = width / (_end - _start);
        double left = (_value - _start) * scale;
        double partitionWidth = _length * scale;

        _rectPartition.Width = partitionWidth;
        Canvas.SetLeft(_rectPartition, left);
        _rectUsed.Width = Math.Min(_used * scale, partitionWidth);
        Canvas.SetLeft(_rectUsed, left);

        _title.MaxWidth = Math.Max(0, partitionWidth - 8);
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        Canvas.SetLeft(_title, left + 6);
        Canvas.SetTop(_title, 18);
    }

    private static Brush Frozen(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
