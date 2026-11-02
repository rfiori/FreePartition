using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FreePartition.Core.Disks;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Properties of a partition: usage bar (used / free, and on a drive with Windows, the share of the operating system,
/// programs and users), plus everything the app can read about the partition, its volume and its disk. Read-only.
/// </summary>
public partial class PropertiesWindow : Window
{
    private static readonly Brush AccentBrush = Frozen("#2F6FA8");   // same as the "Accent" resource of the main window
    private static readonly Brush FreeBrush = Frozen("#E6E6E6");
    private static readonly Dictionary<string, Brush> SegmentBrushes = new()
    {
        [VolumeUsageScanner.OperatingSystem] = Frozen("#E8833A"),
        [VolumeUsageScanner.Programs] = Frozen("#70AD47"),
        [VolumeUsageScanner.Users] = Frozen("#8E6BBF"),
        [VolumeUsageScanner.Other] = Frozen("#5B9BD5")
    };

    private const string UsedName = "Used space";
    private const string FreeName = "Free space";

    private readonly CancellationTokenSource _cts = new();
    private readonly List<(string Section, string Key, string Value)> _facts = new();
    private readonly PartitionRow _row;
    private ulong _total, _used;
    private IReadOnlyList<UsageSegment> _segments = [];

    public PropertiesWindow(PartitionRow row, DiskInfo? disk, PartitionInfo? partition)
    {
        InitializeComponent();
        _row = row;
        Closed += (_, _) => _cts.Cancel();

        TxtTitle.Text = row.Letter is { } l ? $"{l}:" + (string.IsNullOrEmpty(row.Label) ? "" : $"  {row.Label}") : row.Name;
        TxtSubtitle.Text = string.Join("  ·  ", new[] { row.FileSystem, row.Capacity }.Where(s => !string.IsNullOrEmpty(s)));

        _total = row.Size;
        _used = row.UsedBytes;
        if (row.Letter is { } letter && ReadDriveInfo(letter) is { } drive)
            (_total, _used) = drive;

        CollectFacts(row, disk, partition);

        if (row.Letter is null)
        {
            UsagePanel.Visibility = Visibility.Collapsed;   // no volume, so no used/free to show
        }
        else
        {
            SetSegments([new UsageSegment(UsedName, Math.Min(_used, _total))]);
            Loaded += async (_, _) => await StartAnalysisAsync(row.Letter.Value);
        }
        RenderFacts();
    }

    // ------------------------------------------------------------------ Data

    /// <summary>Capacity and used bytes of the mounted volume (exact), or null when it is not ready.</summary>
    private static (ulong Total, ulong Used)? ReadDriveInfo(char letter)
    {
        try
        {
            var info = new DriveInfo(letter.ToString());
            if (!info.IsReady) return null;
            ulong total = (ulong)info.TotalSize;
            ulong free = (ulong)info.TotalFreeSpace;
            return (total, total > free ? total - free : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void CollectFacts(PartitionRow row, DiskInfo? disk, PartitionInfo? partition)
    {
        if (row.Letter is { } letter)
        {
            AddFact("General", "Drive", $"{letter}:");
            AddFact("General", "Label", string.IsNullOrEmpty(row.Label) ? "(none)" : row.Label);
        }
        AddFact("General", "File system", string.IsNullOrEmpty(row.FileSystem) ? "(none / not recognized)" : row.FileSystem);
        AddFact("General", "Partition type", row.Type);
        AddFact("General", "Status", row.State);
        if (row.Letter is { } drive)
        {
            bool isSystemDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root
                                 && char.ToUpperInvariant(root[0]) == drive;
            if (isSystemDrive)
                AddFact("General", "Windows", "This is the drive Windows is running from");
            else if (VolumeUsageScanner.LooksLikeWindows($@"{drive}:\"))
                AddFact("General", "Windows", "Contains another Windows installation");
        }

        if (row.Letter is not null)
        {
            AddFact("Capacity", "Volume capacity", $"{ByteFormatter.Format(_total)}  ({_total:N0} bytes)");
            AddFact("Capacity", "Used space", $"{ByteFormatter.Format(_used)}  ({Percent(_used, _total)})");
            AddFact("Capacity", "Free space", $"{ByteFormatter.Format(_total - Math.Min(_used, _total))}  ({Percent(_total - Math.Min(_used, _total), _total)})");
        }

        AddFact("Location", "Disk", row.Disk);
        if (disk is not null)
        {
            AddFact("Location", "Disk type", $"{(disk.IsSsd ? "SSD" : "HDD / other")}" +
                                             (string.IsNullOrEmpty(disk.Interface) ? "" : $"  ·  {disk.Interface}"));
            AddFact("Location", "Disk size", ByteFormatter.Format(disk.Size));
            AddFact("Location", "Disk status", disk.Status ?? "Unknown");
            AddFact("Location", "Partition style", disk.PartitionStyle);
        }
        if (row.PartitionNumber > 0)
            AddFact("Location", "Partition number", row.PartitionNumber.ToString(CultureInfo.InvariantCulture));
        AddFact("Location", "Starting offset", $"{ByteFormatter.Format(row.Offset)}  ({row.Offset:N0} bytes)");
        AddFact("Location", "Partition size", $"{ByteFormatter.Format(row.Size)}  ({row.Size:N0} bytes)");
        AddFact("Location", "Ending offset", $"{ByteFormatter.Format(row.Offset + row.Size)}");
        if (row.PartitionNumber > 0)
            AddFact("Location", "Alignment", PartitionAlignment.Describe(PartitionAlignment.Classify(row.Offset)));
        if (partition?.Details is { Count: > 0 } details)
            AddFact("Location", "Roles", string.Join(", ", details));
    }

    /// <summary>Reads the NTFS boot sector (read-only): cluster size, geometry and the volume serial number.</summary>
    private async Task LoadNtfsFactsAsync(char letter)
    {
        if (!string.Equals(_row.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var (boot, serial) = await Task.Run(() =>
            {
                using var volume = RawVolume.Open(letter.ToString());
                var sector = new byte[512];
                if (volume.Read(0, sector) < 512) throw new InvalidDataException("Short read.");
                return (NtfsBootSector.Read(sector), BitConverter.ToUInt32(sector, 0x48));
            }, _cts.Token);

            AddFact("NTFS volume", "Volume serial number", $"{serial >> 16:X4}-{serial & 0xFFFF:X4}");
            AddFact("NTFS volume", "Cluster size", $"{boot.BytesPerCluster:N0} bytes");
            AddFact("NTFS volume", "Sector size", $"{boot.BytesPerSector:N0} bytes");
            AddFact("NTFS volume", "Total clusters", $"{boot.TotalClusters:N0}");
            AddFact("NTFS volume", "MFT record size", $"{boot.MftRecordSize:N0} bytes");
            AddFact("NTFS volume", "MFT starts at cluster", $"{boot.MftStartCluster:N0}");
            RenderFacts();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not readable (no access, not NTFS anymore): the rest of the window is still useful.
        }
    }

    private async Task StartAnalysisAsync(char letter)
    {
        _ = LoadNtfsFactsAsync(letter);

        string root = $@"{letter}:\";
        if (!VolumeUsageScanner.LooksLikeWindows(root)) return;

        TxtScan.Text = "Analyzing the folders of this Windows drive...";
        try
        {
            var progress = new Progress<IReadOnlyList<UsageSegment>>(SetSegments);
            var result = await VolumeUsageScanner.ScanAsync(root, Math.Min(_used, _total), progress, _cts.Token);
            SetSegments(result);
            TxtScan.Text = "Sizes by folder are estimates: files are counted by their size, not by the space they take on the disk.";
        }
        catch (OperationCanceledException)
        {
            // The window was closed.
        }
        catch (Exception ex)
        {
            TxtScan.Text = $"Could not analyze the folders: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ Bar

    private void SetSegments(IReadOnlyList<UsageSegment> segments)
    {
        _segments = segments;
        BarGrid.Children.Clear();
        BarGrid.ColumnDefinitions.Clear();
        Legend.Children.Clear();
        if (_total == 0) return;

        ulong used = (ulong)segments.Sum(s => (long)s.Bytes);
        ulong free = _total > used ? _total - used : 0;
        var parts = segments.Select(s => (s.Name, s.Bytes, Brush: BrushFor(s.Name)))
            .Append((FreeName, free, FreeBrush)).Where(p => p.Item2 > 0).ToList();

        foreach (var (name, bytes, brush) in parts)
        {
            BarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
            var block = new Border
            {
                Background = brush, BorderBrush = Brushes.White, BorderThickness = new Thickness(0, 0, 1, 0),
                ToolTip = $"{name}: {ByteFormatter.Format(bytes)} ({Percent(bytes, _total)})"
            };
            Grid.SetColumn(block, BarGrid.ColumnDefinitions.Count - 1);
            BarGrid.Children.Add(block);

            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
            item.Children.Add(new Rectangle
            {
                Width = 12, Height = 12, Fill = brush, Stroke = Brushes.Silver, StrokeThickness = 0.5,
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
            });
            item.Children.Add(new TextBlock { Text = $"{name}: {ByteFormatter.Format(bytes)} ({Percent(bytes, _total)})" });
            Legend.Children.Add(item);
        }
    }

    private static Brush BrushFor(string name) => SegmentBrushes.GetValueOrDefault(name, SegmentBrushes[VolumeUsageScanner.Other]);

    private static string Percent(ulong part, ulong whole) =>
        whole == 0 ? "0%" : $"{(double)part / whole:P1}";

    // ------------------------------------------------------------------ Facts list

    private void AddFact(string section, string key, string value) => _facts.Add((section, key, value));

    private void RenderFacts()
    {
        InfoPanel.Children.Clear();
        foreach (var group in _facts.GroupBy(f => f.Section))
        {
            InfoPanel.Children.Add(new TextBlock
            {
                Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4),
                Foreground = AccentBrush
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var (_, key, value) in group)
            {
                int row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = key, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 8, 2) };
                var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
                Grid.SetRow(label, row);
                Grid.SetRow(text, row);
                Grid.SetColumn(text, 1);
                grid.Children.Add(label);
                grid.Children.Add(text);
            }
            InfoPanel.Children.Add(grid);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        text.AppendLine(TxtTitle.Text).AppendLine(TxtSubtitle.Text);
        if (_row.Letter is not null && _total > 0)
        {
            text.AppendLine().AppendLine("Usage");
            foreach (var s in _segments)
                text.AppendLine($"  {s.Name}: {ByteFormatter.Format(s.Bytes)} ({Percent(s.Bytes, _total)})");
            ulong used = (ulong)_segments.Sum(s => (long)s.Bytes);
            text.AppendLine($"  {FreeName}: {ByteFormatter.Format(_total > used ? _total - used : 0)}");
        }
        foreach (var group in _facts.GroupBy(f => f.Section))
        {
            text.AppendLine().AppendLine(group.Key);
            foreach (var (_, key, value) in group)
                text.AppendLine($"  {key}: {value}");
        }

        try { Clipboard.SetText(text.ToString()); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is in use by another program. Try again.", "Properties");
        }
    }

    private static Brush Frozen(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
