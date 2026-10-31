using System.Windows;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>Row of the list of partitions found. The check box is what the user decides; the rest is read-only.</summary>
public sealed class LostPartitionRow
{
    public LostPartitionRow(LostPartition partition)
    {
        Partition = partition;
        CanRestore = true;
        IsChecked = CanRestore && partition.BackupConfirmed;
    }

    public LostPartition Partition { get; }
    public bool CanRestore { get; }
    public bool IsChecked { get; set; }

    public string FileSystem => Partition.FileSystem.ToString();
    public string Size => ByteFormatter.Format(Partition.Length);
    public string Serial => Partition.Serial;

    public string Confidence =>
        Partition.BackupConfirmed ? "High: the backup boot sector matches"
        : "Low: only the first boot sector was found";
}

/// <summary>
/// Looks for partitions missing from the partition table by their boot sectors (read-only) and queues a
/// <see cref="RestorePartition"/> for each one the user checks. Nothing is written until Apply.
/// </summary>
public partial class LostPartitionsWindow : Window
{
    private DiskInfo _disk;
    private readonly Action<DiskOperation> _queue;
    private CancellationTokenSource? _cts;

    public LostPartitionsWindow(IReadOnlyList<DiskInfo> disks, int initialDiskIndex, Action<DiskOperation> queue)
    {
        InitializeComponent();
        _queue = queue;

        CmbDisks.ItemsSource = disks.Select(d => new DiskChoice(d)).ToList();
        CmbDisks.SelectedIndex = Math.Max(0, disks.ToList().FindIndex(d => d.Index == initialDiskIndex));
        _disk = ((DiskChoice)CmbDisks.SelectedItem).Disk;
        Closed += (_, _) => _cts?.Cancel();
    }

    private sealed record DiskChoice(DiskInfo Disk)
    {
        public override string ToString() =>
            $"Disk {Disk.Index} — {Disk.Model} · {Disk.PartitionStyle} · {ByteFormatter.Format(Disk.Size)}";
    }

    private void CmbDisks_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbDisks.SelectedItem is not DiskChoice choice) return;
        _disk = choice.Disk;

        ResultsList.ItemsSource = null;
        BtnRestore.IsEnabled = false;
        Bar.Value = 0;
        TxtStatus.Text = "";
        NoteBox.Visibility = _disk.PartitionStyle == "Uninitialized" ? Visibility.Visible : Visibility.Collapsed;
        TxtNote.Text = "This disk has no partition table. Queue Initialize disk (as MBR or GPT) before restoring the partitions " +
                       "found here; initializing writes only the table, not the data.";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var mode = RbDeep.IsChecked == true ? LostPartitionScanMode.Deep : LostPartitionScanMode.Quick;

        BtnStart.IsEnabled = BtnRestore.IsEnabled = RbQuick.IsEnabled = RbDeep.IsEnabled = CmbDisks.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        ResultsList.ItemsSource = null;
        Bar.Value = 0;
        TxtStatus.Text = "Scanning the unallocated space...";

        var progress = new Progress<double>(p =>
        {
            Bar.Value = p;
            TxtStatus.Text = $"Scanning the unallocated space... {p:P0}";
        });

        try
        {
            var (scan, gaps) = await Task.Run(() =>
            {
                using var disk = RawDisk.Open(_disk.Index);
                var occupied = _disk.Partitions.Select(p => new DiskRange((long)p.Offset, (long)(p.Offset + p.Size)))
                    .Concat((_disk.Reserved ?? []).Select(r => new DiskRange((long)r.Offset, (long)(r.Offset + r.Size))));
                var gaps = LostPartitionScanner.FindGaps(disk.Length, disk.SectorSize, _disk.PartitionStyle == "GPT", occupied);
                return (LostPartitionScanner.Scan(disk.Read, disk.SectorSize, gaps, mode, progress, token), gaps);
            });

            ShowResult(scan, gaps);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Could not scan: {ex.Message}";
        }
        finally
        {
            BtnStart.IsEnabled = RbQuick.IsEnabled = RbDeep.IsEnabled = CmbDisks.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ShowResult(LostPartitionScan scan, IReadOnlyList<DiskRange> gaps)
    {
        var rows = scan.Found.Select(p => new LostPartitionRow(p)).ToList();
        ResultsList.ItemsSource = rows;
        BtnRestore.IsEnabled = rows.Any(r => r.CanRestore);

        string space = gaps.Count == 0 ? "There is no unallocated space to look in."
            : $"Looked in {ByteFormatter.Format(gaps.Sum(g => g.Length))} of unallocated space.";
        TxtStatus.Text = (scan.Completed ? "Done. " : "Canceled: showing what was found so far. ") + space + " " +
            (rows.Count == 0 ? "No lost partition found." : $"{rows.Count} partition(s) found; the ones with high confidence are checked.");
        if (scan.Completed) Bar.Value = 1;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var chosen = (ResultsList.ItemsSource as IEnumerable<LostPartitionRow>)?
            .Where(r => r.IsChecked && r.CanRestore).Select(r => r.Partition).OrderBy(p => p.Offset).ToList() ?? [];
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Check at least one partition.", Title);
            return;
        }

        for (int i = 1; i < chosen.Count; i++)
        {
            if (chosen[i - 1].Range.Overlaps(chosen[i].Range))
            {
                MessageBox.Show(this,
                    $"The partitions at {ByteFormatter.Format(chosen[i - 1].Offset)} and {ByteFormatter.Format(chosen[i].Offset)} overlap, " +
                    "so only one of them can be real. Check just one.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        foreach (var p in chosen)
            _queue(new RestorePartition(_disk.Index, p.Offset, p.Length, p.FileSystem));

        MessageBox.Show(this, $"{chosen.Count} partition(s) added to the queue. Review them and click Apply to write the partition table.",
            Title, MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }
}
