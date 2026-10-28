using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Draggable bar that shows a partition (or free space) and lets the user pick a size with the mouse.
/// Normally the value grows from the left edge; with <see cref="Reversed"/> it is the part at the right end
/// (the new partition of a split), so dragging the handle left makes it bigger.
/// </summary>
public sealed class PartitionBar : StackPanel
{
    private const long Granularity = 1L << 20;   // 1 MiB

    private static readonly Brush Blue = Frozen("#5B9BD5");
    private static readonly Brush LightBlue = Frozen("#CFE2F3");
    private static readonly Brush LightGreen = Frozen("#C6E0B4");
    private static readonly Brush DarkBlue = Frozen("#2F6FA8");

    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeWE };
    private readonly Rectangle _rectPartition = new() { Fill = LightBlue, Height = 54 };
    private readonly Rectangle _rectNew = new() { Fill = LightGreen, Height = 54, Visibility = Visibility.Collapsed };
    private readonly Rectangle _rectUsed = new() { Fill = Blue, Height = 54 };
    private readonly Rectangle _handle = new() { Fill = DarkBlue, Width = 6, Height = 54 };
    private readonly TextBlock _left = new() { Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _right = new() { Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Right };
    private long _total, _used, _minimum, _maximum, _value;
    private bool _reversed, _dragging;

    /// <summary>Raised when the user drags the bar (not when a property is set from code).</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Whole length of the bar in bytes.</summary>
    public long Total { get => _total; set { _total = value; Refresh(); } }

    /// <summary>Bytes already in use, drawn in the darker color from the left edge.</summary>
    public long Used { get => _used; set { _used = value; Refresh(); } }

    public long Minimum { get => _minimum; set { _minimum = value; Refresh(); } }

    public long Maximum { get => _maximum; set { _maximum = value; Refresh(); } }

    /// <summary>The selected size in bytes, always within <see cref="Minimum"/> and <see cref="Maximum"/>.</summary>
    public long Value
    {
        get => _value;
        set { _value = Math.Clamp(value, _minimum, Math.Max(_minimum, _maximum)); Refresh(); }
    }

    /// <summary>True when the value is the part at the right end of the bar.</summary>
    public bool Reversed { get => _reversed; set { _reversed = value; Refresh(); } }

    public PartitionBar()
    {
        _canvas.Children.Add(_rectPartition);
        _canvas.Children.Add(_rectNew);
        _canvas.Children.Add(_rectUsed);
        _canvas.Children.Add(_handle);
        _canvas.SizeChanged += (_, _) => Refresh();
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            _dragging = _canvas.CaptureMouse();
            SetFromPosition(e.GetPosition(_canvas).X);
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
        labels.Children.Add(_left);
        labels.Children.Add(_right);
        Children.Add(labels);
    }

    private void SetFromPosition(double x)
    {
        if (_canvas.ActualWidth <= 0 || _total <= 0) return;
        double fraction = Math.Clamp(x / _canvas.ActualWidth, 0, 1);
        long bytes = (long)((_reversed ? 1 - fraction : fraction) * _total);

        // Snap to whole MiB, but let the ends of the range be exact.
        bytes = Math.Clamp(bytes, _minimum, Math.Max(_minimum, _maximum));
        if (bytes != _minimum && bytes != _maximum)
            bytes = bytes / Granularity * Granularity;
        bytes = Math.Clamp(bytes, _minimum, Math.Max(_minimum, _maximum));

        if (bytes == _value) return;
        _value = bytes;
        Refresh();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        _left.Text = $"{(_reversed ? "Max" : "Min")} {ByteFormatter.Format((ulong)Math.Max(0, _reversed ? _maximum : _minimum))}";
        _right.Text = $"{(_reversed ? "Min" : "Max")} {ByteFormatter.Format((ulong)Math.Max(0, _reversed ? _minimum : _maximum))}";

        double width = _canvas.ActualWidth;
        if (width <= 0 || _total <= 0) return;

        double valueWidth = Math.Clamp((double)_value / _total, 0, 1) * width;
        double usedWidth = Math.Clamp((double)_used / _total, 0, 1) * width;
        double edge;   // where the handle sits
        if (_reversed)
        {
            edge = width - valueWidth;
            _rectPartition.Width = edge;
            _rectNew.Visibility = Visibility.Visible;
            Canvas.SetLeft(_rectNew, edge);
            _rectNew.Width = valueWidth;
        }
        else
        {
            edge = valueWidth;
            _rectPartition.Width = valueWidth;
            _rectNew.Visibility = Visibility.Collapsed;
        }
        _rectUsed.Width = Math.Min(usedWidth, edge);
        Canvas.SetLeft(_handle, Math.Clamp(edge - _handle.Width / 2, 0, Math.Max(0, width - _handle.Width)));
    }

    private static Brush Frozen(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
