using System.Windows;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>Dialog to resize a partition by dragging a handle (or typing the size in GB).</summary>
public partial class ResizeDialog : Window
{
    private const long Granularity = 1L << 20;   // 1 MiB
    private const long UnmovableNoteThreshold = 1L << 30;   // 1 GiB between the files and Windows' minimum

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
    /// <param name="dataBytes">Space really used by files, drawn in the bar. Defaults to <paramref name="used"/>.
    /// When it is well below <paramref name="used"/>, the dialog explains why Windows will not shrink further.</param>
    public ResizeDialog(char drive, ulong current, ulong used, ulong max, ulong? dataBytes = null)
    {
        InitializeComponent();
        _current = (long)current;
        _max = Math.Max((long)max, _current);
        _used = Math.Min((long)used, _current);
        _size = _current;
        long data = Math.Min((long)(dataBytes ?? used), _current);

        TxtDescription.Text = $"Drive {drive}: — current size {ByteFormatter.Format(current)}";

        // Windows' minimum can be far above the files' size: unmovable files block shrinking past them.
        if (_used - data > UnmovableNoteThreshold)
        {
            TxtLimit.Text =
                $"Windows allows shrinking {drive}: by only {ByteFormatter.Format(_current - _used)}, even though " +
                $"{ByteFormatter.Format(_current - data)} is free. Files Windows cannot move (page file, hibernation " +
                "file, System Restore snapshots, the MFT) sit near the end of the partition.\n\n" +
                "To free more, temporarily turn off hibernation (powercfg /h off), the page file and System Restore " +
                "for this drive, restart, and try again.";
            LimitNote.Visibility = Visibility.Visible;
        }

        Bar.Total = _max;
        Bar.Used = data;
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
