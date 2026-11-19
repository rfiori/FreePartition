using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Bar that shows the free space around a partition (aqua green) with the partition (yellow) inside it. Dragging the
/// partition moves it; dragging its left or right edge changes where it starts or ends, so one bar both resizes and
/// moves it. Starts are whole MiB (the current start always stays reachable); sizes snap to whole MiB except at
/// their limits. When the partition cannot move, only its right edge can be dragged (a plain resize).
/// </summary>
public sealed class ResizeMoveBar : StackPanel
{
    private const long MiB = 1L << 20;
    private const double EdgeTolerance = 7;   // pixels around an edge that grab it instead of the whole partition

    private enum Drag { None, Move, Left, Right }

    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly PartitionBlock _block = new();
    private readonly Ellipse _handleLeft = BarVisuals.CreateHandle();
    private readonly Ellipse _handleRight = BarVisuals.CreateHandle();
    private readonly SizeBracket _bracket = new();
    private readonly TextBlock _before = new() { Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _after = new() { Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Right };

    private readonly long _regionStart, _regionEnd, _originalStart, _originalSize, _used, _minSize, _firstAligned;
    private readonly bool _canMove;
    private long _start, _size;
    private Drag _drag;
    private double _grab;   // distance from the partition's left edge to the mouse when a move started

    /// <summary>Raised when the user drags the bar (not when the values are set from code).</summary>
    public event EventHandler? Changed;

    /// <param name="title">First line inside the partition, e.g. "E:".</param>
    /// <param name="regionStart">Start of the free space around the partition (its own space included).</param>
    /// <param name="regionEnd">End of that free space (the last usable byte of the disk at most).</param>
    /// <param name="start">Where the partition starts now.</param>
    /// <param name="size">Its current size.</param>
    /// <param name="used">Space used by files, drawn in the partition's usage bar.</param>
    /// <param name="minSize">Smallest size Windows allows for it.</param>
    /// <param name="canMove">False keeps the start where it is (only the end can be dragged).</param>
    public ResizeMoveBar(string title, long regionStart, long regionEnd, long start, long size, long used, long minSize, bool canMove)
    {
        _regionStart = regionStart;
        _regionEnd = Math.Max(regionEnd, start + size);
        _originalStart = _start = start;
        _originalSize = _size = size;
        _used = used;
        _canMove = canMove;
        // A whole MiB, but never above the current size (that size is always allowed).
        _minSize = Math.Min(Math.Max(MiB, (minSize + MiB - 1) / MiB * MiB), size);
        _firstAligned = (Math.Max(regionStart, DriveFirstUsable) + MiB - 1) / MiB * MiB;

        _block.Title = title;
        _bracket.Text = $"Available space: {ByteFormatter.Format(Math.Max(0, _regionEnd - _regionStart))}";
        _handleLeft.Visibility = canMove ? Visibility.Visible : Visibility.Collapsed;

        Canvas.SetTop(_block, BarVisuals.BlockTop);
        _canvas.Children.Add(_block);
        _canvas.Children.Add(_handleLeft);
        _canvas.Children.Add(_handleRight);
        _canvas.SizeChanged += (_, _) => Refresh();
        _canvas.MouseLeftButtonDown += (_, e) => BeginDrag(e.GetPosition(_canvas).X);
        _canvas.MouseMove += (_, e) =>
        {
            double x = e.GetPosition(_canvas).X;
            if (_drag != Drag.None) DragTo(x);
            else _canvas.Cursor = HitTest(x) switch
            {
                Drag.Left or Drag.Right => Cursors.SizeWE,
                Drag.Move => Cursors.SizeAll,
                _ => Cursors.Arrow
            };
        };
        _canvas.MouseLeftButtonUp += (_, _) =>
        {
            _drag = Drag.None;
            _canvas.ReleaseMouseCapture();
        };

        Children.Add(BarVisuals.CreateTrack(_canvas));
        Children.Add(_bracket);
        var labels = new Grid();
        labels.Children.Add(_before);
        labels.Children.Add(_after);
        Children.Add(labels);
        Refresh();
    }

    /// <summary>No partition may start inside the first MiB of the disk.</summary>
    private const long DriveFirstUsable = MiB;

    public bool CanMove => _canMove;
    public long RegionStart => _regionStart;
    public long RegionEnd => _regionEnd;
    public long MinSize => _minSize;

    /// <summary>Where the partition will start on the disk, in bytes.</summary>
    public long Start => _start;

    /// <summary>Its new size in bytes.</summary>
    public long Size => _size;

    public long FreeBefore => _start - _regionStart;
    public long FreeAfter => _regionEnd - (_start + _size);

    /// <summary>Largest size the partition can have at its current start.</summary>
    public long MaxSizeAtStart => _regionEnd - _start;

    /// <summary>Sets the size, keeping the start (from the size box).</summary>
    public void SetSize(long size)
    {
        _size = SnapSize(size, _start);
        Refresh();
    }

    /// <summary>Moves the partition so it starts near <paramref name="start"/>, keeping its size when it fits.</summary>
    public void SetStart(long start)
    {
        if (!_canMove) return;
        _size = Math.Min(_size, _regionEnd - _firstAligned);
        _start = SnapStart(start, _regionEnd - _size);
        _size = Math.Min(_size, _regionEnd - _start);
        Refresh();
    }

    // ------------------------------------------------------------------ Snapping

    /// <summary>
    /// The allowed start nearest to <paramref name="wanted"/> that is not after <paramref name="latest"/>: a whole MiB
    /// in the free space, or the current start (which may not be a whole MiB).
    /// </summary>
    private long SnapStart(long wanted, long latest)
    {
        if (!_canMove) return _originalStart;

        long? aligned = null;
        long lastAligned = latest / MiB * MiB;
        if (lastAligned >= _firstAligned)
            aligned = Math.Clamp((wanted + MiB / 2) / MiB * MiB, _firstAligned, lastAligned);

        bool originalFits = _originalStart <= latest;
        if (aligned is not { } a) return originalFits ? _originalStart : _start;
        if (!originalFits) return a;
        return Math.Abs(wanted - _originalStart) <= Math.Abs(wanted - a) ? _originalStart : a;
    }

    /// <summary>A size between the minimum and the room left after <paramref name="start"/>, in whole MiB except at the limits.</summary>
    private long SnapSize(long wanted, long start)
    {
        long max = Math.Max(_minSize, _regionEnd - start);
        if (Math.Abs(wanted - _originalSize) < MiB / 2 && _originalSize <= max) return _originalSize;
        long rounded = (wanted + MiB / 2) / MiB * MiB;
        return Math.Clamp(rounded, _minSize, max);
    }

    // ------------------------------------------------------------------ Mouse

    private double Scale => _canvas.ActualWidth / Math.Max(1, _regionEnd - _regionStart);

    private long BytesAt(double x) => _regionStart + (long)(x / Scale);

    private Drag HitTest(double x)
    {
        double left = (_start - _regionStart) * Scale;
        double right = left + _size * Scale;
        if (Math.Abs(x - right) <= EdgeTolerance) return Drag.Right;
        if (_canMove && Math.Abs(x - left) <= EdgeTolerance) return Drag.Left;
        if (_canMove && x > left && x < right) return Drag.Move;
        return Drag.None;
    }

    private void BeginDrag(double x)
    {
        if (_canvas.ActualWidth <= 0) return;
        _drag = HitTest(x);
        double left = (_start - _regionStart) * Scale;
        if (_drag == Drag.None)
        {
            // A click on the free space: the partition jumps there (centered), or its end does when it cannot move.
            _drag = _canMove ? Drag.Move : Drag.Right;
            _grab = _size * Scale / 2;
        }
        else
        {
            _grab = x - left;
        }
        if (!_canvas.CaptureMouse()) _drag = Drag.None;
        DragTo(x);
    }

    private void DragTo(double x)
    {
        if (_canvas.ActualWidth <= 0) return;
        long start = _start, size = _size;
        switch (_drag)
        {
            case Drag.Move:
                start = SnapStart(BytesAt(x - _grab), _regionEnd - _size);
                break;
            case Drag.Left:
                long end = _start + _size;
                start = SnapStart(BytesAt(x), end - _minSize);
                size = end - start;
                break;
            case Drag.Right:
                size = SnapSize(BytesAt(x) - _start, _start);
                break;
            default:
                return;
        }
        if (start == _start && size == _size) return;
        _start = start;
        _size = size;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ Drawing

    private void Refresh()
    {
        _before.Text = $"Free before: {ByteFormatter.Format(Math.Max(0, FreeBefore))}";
        _after.Text = $"Free after: {ByteFormatter.Format(Math.Max(0, FreeAfter))}";
        _block.Subtitle = ByteFormatter.Format(_size);
        _block.Fraction = _size > 0 ? (double)_used / _size : 0;

        if (_canvas.ActualWidth <= 0) return;
        double left = (_start - _regionStart) * Scale;
        double width = _size * Scale;
        _block.Width = Math.Max(width, 0);
        Canvas.SetLeft(_block, left);
        BarVisuals.PlaceHandle(_handleLeft, left);
        BarVisuals.PlaceHandle(_handleRight, left + width);
    }
}
