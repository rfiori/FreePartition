using System.Windows;
using FreePartition.Core.Operations;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Dialog that resizes and/or moves a partition in one go: drag the partition to move it, drag its edges to change
/// where it starts or ends, or type the free space before it, its size and the free space after it.
/// </summary>
public partial class ResizeMoveDialog : Window
{
    private const long UnmovableNoteThreshold = 1L << 30;   // 1 GiB between the files and Windows' minimum

    private readonly ResizeMoveBar _bar;
    private readonly long _currentStart, _currentSize;

    /// <summary>Where the partition will start on the disk, in bytes.</summary>
    public long NewStart => _bar.Start;

    /// <summary>Its new size in bytes.</summary>
    public long NewSize => _bar.Size;

    public bool Moved => _bar.Start != _currentStart;

    public bool Resized => _bar.Size != _currentSize;

    /// <summary>Not moved and grown to all the free space after it (queued as "extend to the maximum").</summary>
    public bool ExtendsToMaximum => !Moved && Resized && _bar.Size == _bar.MaxSizeAtStart;

    /// <param name="regionStart">Start of the free space around the partition (its own space included).</param>
    /// <param name="regionEnd">End of that free space.</param>
    /// <param name="minSize">Smallest size Windows allows (unmovable files can make it larger than the used space).</param>
    /// <param name="dataBytes">Space really used by files, drawn in the bar.</param>
    /// <param name="moveBlocked">Why the partition cannot move (then only its end can change), or null.</param>
    public ResizeMoveDialog(char drive, long start, long size, long regionStart, long regionEnd, long minSize, long dataBytes,
        string? moveBlocked)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _currentStart = start;
        _currentSize = size;
        long data = Math.Min(dataBytes, size);

        TxtDescription.Text = $"Drive {drive}: — current size {ByteFormatter.Format(size)}";
        TxtHint.Text = moveBlocked is null
            ? "Drag the partition to move it, or drag its left or right edge to shrink or extend it from that side."
            : "Drag the right edge to shrink or extend the partition.";
        if (moveBlocked is not null)
        {
            TxtMoveNote.Text = moveBlocked;
            MoveNote.Visibility = Visibility.Visible;
        }

        // Windows' minimum can be far above the files' size: unmovable files block shrinking past them.
        if (minSize - data > UnmovableNoteThreshold)
        {
            TxtLimit.Text =
                $"Windows allows shrinking {drive}: by only {ByteFormatter.Format(size - minSize)}, even though " +
                $"{ByteFormatter.Format(size - data)} is free. Files Windows cannot move (page file, hibernation " +
                "file, System Restore snapshots, the MFT) sit near the end of the partition.\n\n" +
                "To free more, temporarily turn off hibernation (powercfg /h off), the page file and System Restore " +
                "for this drive, restart, and try again.";
            LimitNote.Visibility = Visibility.Visible;
        }

        TxtWarning.Text = $"⚠ Moving: {MovePartition.PowerLossWarning}";

        _bar = new ResizeMoveBar($"{drive}:", regionStart, regionEnd, start, size, data, minSize, moveBlocked is null);
        _bar.Changed += (_, _) => UpdateAll();
        BarHost.Content = _bar;

        long room = _bar.RegionEnd - _bar.RegionStart;
        InputBefore.Minimum = InputAfter.Minimum = 0;
        InputBefore.Maximum = InputAfter.Maximum = Math.Max(1, room);
        InputBefore.IsEnabled = _bar.CanMove;
        InputSize.Minimum = _bar.MinSize;
        InputSize.Maximum = room;

        InputBefore.BytesChanged += (_, _) =>
        {
            if (InputBefore.Bytes is { } before) _bar.SetStart(_bar.RegionStart + before);
            UpdateAll();
        };
        InputSize.BytesChanged += (_, _) =>
        {
            if (InputSize.Bytes is { } bytes) _bar.SetSize(bytes);
            UpdateAll();
        };
        InputAfter.BytesChanged += (_, _) =>
        {
            if (InputAfter.Bytes is { } after)
            {
                // A movable partition keeps its size and slides; a fixed one grows or shrinks at its end.
                if (_bar.CanMove) _bar.SetStart(_bar.RegionEnd - after - _bar.Size);
                else _bar.SetSize(_bar.RegionEnd - after - _bar.Start);
            }
            UpdateAll();
        };
        UpdateAll();
    }

    private void UpdateAll()
    {
        InputBefore.Bytes = _bar.FreeBefore;
        InputSize.Bytes = _bar.Size;
        InputAfter.Bytes = _bar.FreeAfter;

        var changes = new List<string>();
        long delta = _bar.Size - _currentSize;
        if (delta > 0) changes.Add($"Extend by {ByteFormatter.Format(delta)}");
        if (delta < 0) changes.Add($"Shrink by {ByteFormatter.Format(-delta)}");
        long shift = _bar.Start - _currentStart;
        if (shift > 0) changes.Add($"Move {ByteFormatter.Format(shift)} to the right");
        if (shift < 0) changes.Add($"Move {ByteFormatter.Format(-shift)} to the left");
        TxtDelta.Text = changes.Count == 0 ? "No change" : string.Join("\n", changes);

        PowerWarning.Visibility = Moved ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Moved && !Resized)
        {
            MessageBox.Show(this, "The partition was not changed.", Title);
            return;
        }
        DialogResult = true;
    }
}
