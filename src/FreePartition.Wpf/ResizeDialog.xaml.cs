using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>Dialog to resize a partition by dragging a handle (or typing the size in GB).</summary>
public partial class ResizeDialog : Window
{
    private const long Granularity = 1L << 20;   // 1 MiB
    private const double GiB = 1024.0 * 1024 * 1024;

    private readonly long _current;
    private readonly long _used;
    private readonly long _max;
    private long _size;
    private bool _dragging;

    /// <summary>The chosen size in bytes, or null to extend to the maximum available.</summary>
    public long? NewSizeBytes => _size >= _max && _max > _current ? null : _size;

    /// <summary>True when the chosen size differs from the current one.</summary>
    public bool Changed => _size != _current;

    /// <param name="used">Smallest allowed size (space already in use).</param>
    /// <param name="max">Largest allowed size (the partition plus the free space right after it).</param>
    public ResizeDialog(char drive, ulong current, ulong used, ulong max)
    {
        InitializeComponent();
        _current = (long)current;
        _max = Math.Max((long)max, _current);
        _used = Math.Min((long)used, _current);
        _size = _current;

        TxtDescription.Text = $"Drive {drive}: — current size {ByteFormatter.Format(current)}";
        TxtMin.Text = $"Min {ByteFormatter.Format((ulong)_used)}";
        TxtMax.Text = $"Max {ByteFormatter.Format((ulong)_max)}";
        UpdateAll(updateText: true);
    }

    private void Bar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAll(updateText: false);

    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = Bar.CaptureMouse();
        SetFromPosition(e.GetPosition(Bar).X);
    }

    private void Bar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging) SetFromPosition(e.GetPosition(Bar).X);
    }

    private void Bar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Bar.ReleaseMouseCapture();
    }

    private void SetFromPosition(double x)
    {
        if (Bar.ActualWidth <= 0 || _max <= 0) return;
        double bytes = Math.Clamp(x / Bar.ActualWidth, 0, 1) * _max;
        SetSize((long)bytes);
    }

    private void SetSize(long bytes)
    {
        // Snap to whole MiB, but let the ends of the range be exact.
        bytes = Math.Clamp(bytes, _used, _max);
        if (bytes != _used && bytes != _max)
            bytes = bytes / Granularity * Granularity;
        _size = Math.Clamp(bytes, _used, _max);
        UpdateAll(updateText: true);
    }

    private void UpdateAll(bool updateText)
    {
        double width = Bar.ActualWidth;
        if (width > 0 && _max > 0)
        {
            double partitionWidth = (double)_size / _max * width;
            double usedWidth = (double)_used / _max * width;
            RectPartition.Width = partitionWidth;
            RectUsed.Width = Math.Min(usedWidth, partitionWidth);
            Canvas.SetLeft(Handle, Math.Clamp(partitionWidth - Handle.Width / 2, 0, Math.Max(0, width - Handle.Width)));
        }

        if (updateText)
            TxtSize.Text = (_size / GiB).ToString("0.##", CultureInfo.CurrentCulture);

        long delta = _size - _current;
        TxtDelta.Text = delta == 0 ? "No change"
            : delta > 0 ? $"Extend by {ByteFormatter.Format((ulong)delta)}"
            : $"Shrink by {ByteFormatter.Format((ulong)-delta)}";
    }

    private void ApplyTypedSize()
    {
        if (decimal.TryParse(TxtSize.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var gb) && gb > 0)
            SetSize((long)(gb * (decimal)GiB));
        else
            UpdateAll(updateText: true);
    }

    private void TxtSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyTypedSize();
            e.Handled = true;   // do not trigger the default button before the value is applied
        }
    }

    private void TxtSize_LostFocus(object sender, RoutedEventArgs e) => ApplyTypedSize();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ApplyTypedSize();
        if (!Changed)
        {
            MessageBox.Show(this, "The size was not changed.", "Resize partition");
            return;
        }
        DialogResult = true;
    }
}
