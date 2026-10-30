using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FreePartition.Core.Disks;

namespace FreePartition.Wpf;

/// <summary>Draws a <see cref="ClusterMap"/> as a square-celled grid; a click selects a cell, the mouse over one shows its range.</summary>
public sealed class ClusterGrid : FrameworkElement
{
    private const int Columns = 64;
    private const double Gap = 1;

    private static readonly Brush ZeroBrush = Frozen("#BDEBDD");
    private static readonly Brush DataBrush = Frozen("#E8833A");
    private static readonly Brush UnreadableBrush = Frozen("#C0392B");
    private static readonly Pen SelectionPen = FrozenPen("#1B1B1B", 2);

    private ClusterMap? _map;
    private int _selected = -1;

    public event Action<int>? CellSelected;

    public ClusterMap? Map
    {
        get => _map;
        set
        {
            _map = value;
            _selected = -1;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private double CellSize => _map is null ? 0 : Math.Max(2, Math.Min(ActualWidth / Columns, 14));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_map is null) return;

        double size = CellSize;
        for (int i = 0; i < _map.Cells; i++)
        {
            var brush = _map.States[i] switch
            {
                CellState.Data => DataBrush,
                CellState.Unreadable => UnreadableBrush,
                _ => ZeroBrush
            };
            dc.DrawRectangle(brush, null, CellRect(i, size));
        }
        if (_selected >= 0) dc.DrawRectangle(null, SelectionPen, CellRect(_selected, size));
    }

    protected override Size MeasureOverride(Size available)
    {
        if (_map is null) return new Size(0, 0);
        double width = double.IsInfinity(available.Width) ? Columns * 14 : available.Width;
        double size = Math.Max(2, Math.Min(width / Columns, 14));
        int rows = (_map.Cells + Columns - 1) / Columns;
        return new Size(Math.Min(width, Columns * size), rows * size);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int cell = CellAt(e.GetPosition(this));
        ToolTip = cell < 0 || _map is null ? null
            : $"{_map.CellStart(cell):N0} – {_map.CellEnd(cell):N0}  ({_map.States[cell]})";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int cell = CellAt(e.GetPosition(this));
        if (cell < 0) return;
        _selected = cell;
        InvalidateVisual();
        CellSelected?.Invoke(cell);
    }

    private int CellAt(Point p)
    {
        if (_map is null) return -1;
        double size = CellSize;
        int column = (int)(p.X / size), row = (int)(p.Y / size);
        int cell = row * Columns + column;
        return column is >= 0 and < Columns && p.X >= 0 && p.Y >= 0 && cell < _map.Cells ? cell : -1;
    }

    private static Rect CellRect(int cell, double size) =>
        new(cell % Columns * size, cell / Columns * size, Math.Max(1, size - Gap), Math.Max(1, size - Gap));

    private static Brush Frozen(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
