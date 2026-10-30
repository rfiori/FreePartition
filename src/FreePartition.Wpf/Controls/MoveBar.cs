using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Bar that shows a stretch of free space (aqua green) with a partition (yellow) inside it, and lets the user drag
/// the partition left or right within that stretch. The value is the partition's offset on the disk, in whole MiB.
/// </summary>
public sealed class MoveBar : StackPanel
{
    private const long Granularity = 1L << 20;   // 1 MiB

    private readonly Canvas _canvas = new() { Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.SizeWE };
    private readonly PartitionBlock _block = new();
    private readonly Ellipse _handleLeft = BarVisuals.CreateHandle();
    private readonly Ellipse _handleRight = BarVisuals.CreateHandle();
    private readonly SizeBracket _bracket = new();
    private readonly TextBlock _before = new() { Foreground = System.Windows.Media.Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _after = new() { Foreground = System.Windows.Media.Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Right };

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

    /// <param name="title">First line inside the partition, e.g. "E:".</param>
    /// <param name="start">Where the free stretch (which includes the partition) begins on the disk.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="current">Offset the partition has now.</param>
    /// <param name="subtitle">Second line inside the partition, e.g. its size.</param>
    public MoveBar(string title, long start, long end, long length, long used, long current, string subtitle = "")
    {
        _start = start;
        _end = end;
        _length = length;
        _used = used;
        _block.Title = title;
        _block.Subtitle = subtitle;
        _block.Fraction = length > 0 ? (double)used / length : 0;
        _bracket.Text = $"Available space: {ByteFormatter.Format(Math.Max(0, end - start))}";

        // Destinations are whole MiB; the current position always stays reachable.
        long first = (Math.Max(start, 1L << 20) + Granularity - 1) / Granularity * Granularity;
        long last = (end - length) / Granularity * Granularity;
        _min = Math.Min(first, current);
        _max = Math.Max(last, current);
        _value = current;

        Canvas.SetTop(_block, BarVisuals.BlockTop);
        _canvas.Children.Add(_block);
        _canvas.Children.Add(_handleLeft);
        _canvas.Children.Add(_handleRight);
        _canvas.SizeChanged += (_, _) => Refresh();
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            double x = e.GetPosition(_canvas).X;
            double left = Canvas.GetLeft(_block);
            // Grab the partition where it was clicked; a click on the free space centers the partition there.
            _grab = x >= left && x <= left + _block.Width ? x - left : _block.Width / 2;
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

        Children.Add(BarVisuals.CreateTrack(_canvas));
        Children.Add(_bracket);
        var labels = new Grid();
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

        _block.Width = Math.Max(partitionWidth, 0);
        Canvas.SetLeft(_block, left);
        BarVisuals.PlaceHandle(_handleLeft, left);
        BarVisuals.PlaceHandle(_handleRight, left + partitionWidth);
    }
}
