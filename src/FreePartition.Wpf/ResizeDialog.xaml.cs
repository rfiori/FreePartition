using System.Windows;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>Dialog to resize a partition by dragging a handle (or typing the size in GB).</summary>
public partial class ResizeDialog : Window
{
    private const long Granularity = 1L << 20;   // 1 MiB

    private readonly long _current;
    private readonly long _used;
    private readonly long _max;
    private long _size;

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
        Bar.Total = _max;
        Bar.Used = _used;
        Bar.Minimum = _used;
        Bar.Maximum = _max;
        Bar.ValueChanged += (_, _) => SetSize(Bar.Value);
        InputSize.Minimum = Math.Max(_used, Granularity);
        InputSize.Maximum = _max;
        InputSize.BytesChanged += (_, _) =>
        {
            if (InputSize.Bytes is { } bytes) SetSize(bytes);
        };
        UpdateAll(updateText: true);
    }

    private void SetSize(long bytes)
    {
        _size = Math.Clamp(bytes, _used, _max);
        UpdateAll(updateText: true);
    }

    private void UpdateAll(bool updateText)
    {
        Bar.Value = _size;

        if (updateText)
            InputSize.Bytes = _size;

        long delta = _size - _current;
        TxtDelta.Text = delta == 0 ? "No change"
            : delta > 0 ? $"Extend by {ByteFormatter.Format((ulong)delta)}"
            : $"Shrink by {ByteFormatter.Format((ulong)-delta)}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Changed)
        {
            MessageBox.Show(this, "The size was not changed.", "Resize partition");
            return;
        }
        DialogResult = true;
    }
}
