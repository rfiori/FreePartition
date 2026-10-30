using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Draggable bar that shows a partition (yellow) inside the space it can use (aqua green) and lets the user pick a
/// size with the mouse by dragging the round handle on its edge. Normally the value grows from the left edge; with
/// <see cref="Reversed"/> it is the part at the right end (the new partition of a split), so dragging the handle
/// left makes it bigger.
/// </summary>
public sealed class PartitionBar : StackPanel
{
    private const long Granularity = 1L << 20;   // 1 MiB

    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeWE };
    private readonly PartitionBlock _block = new();
    private readonly TextBlock _newLabel = new()
    {
        FontWeight = FontWeights.SemiBold, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis,
        Visibility = Visibility.Collapsed
    };
    private readonly Ellipse _handle = BarVisuals.CreateHandle();
    private readonly SizeBracket _bracket = new();
    private long _total, _used, _minimum, _maximum, _value;
    private bool _reversed, _dragging;

    /// <summary>Raised when the user drags the bar (not when a property is set from code).</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Whole length of the bar in bytes.</summary>
    public long Total { get => _total; set { _total = value; Refresh(); } }

    /// <summary>Bytes already in use, drawn in the used-space bar of the partition.</summary>
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
        Canvas.SetTop(_block, BarVisuals.BlockTop);
        _canvas.Children.Add(_block);
        _canvas.Children.Add(_newLabel);
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

        Children.Add(BarVisuals.CreateTrack(_canvas));
        Children.Add(_bracket);
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
        string min = ByteFormatter.Format((ulong)Math.Max(0, _minimum));
        string max = ByteFormatter.Format((ulong)Math.Max(0, _maximum));
        _bracket.Text = _minimum > 0 ? $"Minimum: {min}   ·   Maximum: {max}" : $"Maximum: {max}";

        double width = _canvas.ActualWidth;
        if (width <= 0 || _total <= 0) return;

        double valueWidth = Math.Clamp((double)_value / _total, 0, 1) * width;
        double edge = _reversed ? width - valueWidth : valueWidth;   // where the partition ends and the handle sits
        long blockBytes = _reversed ? Math.Max(0, _total - _value) : _value;

        _block.Width = Math.Max(edge, 0);
        _block.Title = ByteFormatter.Format((ulong)Math.Max(0, blockBytes));
        _block.Subtitle = _reversed ? "Remaining" : "";
        _block.Fraction = blockBytes > 0 ? (double)_used / blockBytes : 0;

        // The part past the handle is free space; in a split it is the new partition, so say how big it is.
        _newLabel.Visibility = _reversed ? Visibility.Visible : Visibility.Collapsed;
        if (_reversed)
        {
            _newLabel.Text = $"New: {ByteFormatter.Format((ulong)Math.Max(0, _value))}";
            _newLabel.MaxWidth = Math.Max(0, valueWidth - 14);
            Canvas.SetLeft(_newLabel, edge + 10);
            Canvas.SetTop(_newLabel, (BarVisuals.TrackHeight - 2 - 16) / 2);
        }

        BarVisuals.PlaceHandle(_handle, edge);
    }
}
