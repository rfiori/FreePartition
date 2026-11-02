using System.Windows;
using FreePartition.Core.Operations;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>Dialog to move a partition by dragging it inside the free space around it.</summary>
public partial class MoveDialog : Window
{
    private readonly MoveBar _bar;
    private readonly long _current;

    /// <summary>Where the partition will start on the disk, in bytes.</summary>
    public long NewOffsetBytes => _bar.Value;

    /// <param name="regionStart">Start of the free space around the partition (the partition's own space included).</param>
    /// <param name="regionEnd">End of that free space.</param>
    public MoveDialog(char drive, ulong size, ulong used, long regionStart, long regionEnd, long current)
    {
        InitializeComponent();
        _current = current;

        TxtDescription.Text = $"Drive {drive}: ({ByteFormatter.Format(size)}) — close every program using it first; " +
                              "the drive is locked and unmounted while it moves.";
        TxtWarning.Text = $"⚠ {MovePartition.PowerLossWarning}";

        _bar = new MoveBar($"{drive}:", regionStart, regionEnd, (long)size, (long)used, current, ByteFormatter.Format(size));
        _bar.ValueChanged += (_, _) => UpdateDelta();
        BarHost.Content = _bar;

        BtnStart.IsEnabled = BtnEnd.IsEnabled = _bar.MinValue != _bar.MaxValue;
        UpdateDelta();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e) => SetValue(_bar.MinValue);

    private void BtnEnd_Click(object sender, RoutedEventArgs e) => SetValue(_bar.MaxValue);

    private void SetValue(long value)
    {
        _bar.Value = value;
        UpdateDelta();
    }

    private void UpdateDelta()
    {
        long delta = _bar.Value - _current;
        TxtDelta.Text = delta == 0 ? "No change"
            : delta > 0 ? $"Moves {ByteFormatter.Format(delta)} to the right"
            : $"Moves {ByteFormatter.Format(-delta)} to the left";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_bar.Value == _current)
        {
            MessageBox.Show(this, "The partition was not moved.", "Move partition");
            return;
        }
        DialogResult = true;
    }
}
