using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>Row of the partition list (a partition/volume or an unallocated stretch).</summary>
public sealed record PartitionRow(
    string Disk, string Name, string FileSystem, string Capacity, string Used,
    string Unused, string State, string Type,
    char? Letter, string? Label, ulong Size, ulong UsedBytes, bool Unallocated, int DiskIndex,
    int PartitionNumber = 0, bool Gpt = false, bool Uninitialized = false, ulong Offset = 0,
    IReadOnlyList<string>? Details = null, bool Reserved = false);

/// <summary>A disk with its rows, for the graphical map. <c>Map</c> also holds the reserved (MSR) stretches the list leaves out.</summary>
public sealed record DiskVisual(int Index, string Style, ulong Size, IReadOnlyList<PartitionRow> Rows, IReadOnlyList<PartitionRow> Map);

/// <summary>Numbered row of the operation queue.</summary>
public sealed record QueueItem(int Order, DiskOperation Operation);

public partial class MainWindow : Window
{
    // Gaps smaller than this between partitions are alignment, not "unallocated".
    private const ulong UnallocatedThreshold = 8UL * 1024 * 1024;

    // Used-space bar of the disk map (the border adds 2 to this height).
    private const double UsageBarHeight = 18;
    private static readonly Brush UsageTrack = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)));
    private static readonly Brush UsageTrackBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xBC, 0xBC, 0xBC)));
    private static readonly Brush UsageText = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)));
    private static readonly Brush UsageFill = Freeze(new LinearGradientBrush(
        Color.FromRgb(0x8B, 0xC8, 0xF2), Color.FromRgb(0x4A, 0x9D, 0xDE), 90));

    private static readonly Brush NormalBox = Brushes.White;
    private static readonly Brush SelectedBox = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xB8));

    private readonly OperationQueue _queue;
    private readonly char? _systemLetter;
    private readonly Dictionary<PartitionRow, Border> _boxes = new();
    private CancellationTokenSource? _operationsCts;
    private IReadOnlyList<DiskInfo> _disks = [];

    public MainWindow()
    {
        InitializeComponent();

        // Windows drive (e.g. 'C'): formatting it or changing its letter is blocked.
        _systemLetter = Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? char.ToUpperInvariant(root[0]) : null;
        _queue = new OperationQueue(_systemLetter, new FileLog(FileLog.DefaultPath));
        _queue.Changed += (_, _) => RefreshPending();
        Loaded += (_, _) => DisksGrid.ContextMenu = BuildContextMenu();

        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString()
                         ?? "unknown";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        TxtVersion.Text = $"v{version}";
    }

    private int _busyCount;

    /// <summary>Shows the status-bar spinner while any long-running work is in progress.</summary>
    private void SetBusy(bool busy)
    {
        _busyCount = Math.Max(0, _busyCount + (busy ? 1 : -1));
        BusySpinner.Visibility = _busyCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshPending();
        RefreshAvailableOperations();
        await LoadDisksAsync();
    }

    // ------------------------------------------------------------------ Disks

    private async void BtnRefreshDisks_Click(object sender, RoutedEventArgs e) => await LoadDisksAsync();

    private async Task LoadDisksAsync()
    {
        SetBusy(true);
        try
        {
            BtnRefreshDisks.IsEnabled = false;
            TxtDiskStatus.Text = "Reading disks...";

            var disks = await Task.Run(() => DiskService.ListDisks());
            _disks = disks;
            var visuals = disks.Select(BuildDisk).ToList();

            var rows = visuals.SelectMany(d => d.Rows).ToList();
            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PartitionRow.Disk)));
            DisksGrid.ItemsSource = view;

            BuildMap(visuals);
            RefreshAvailableOperations();

            TxtDiskStatus.Text = $"{disks.Count} disk(s), {rows.Count(r => !r.Unallocated)} partition(s).";
        }
        catch (Exception ex)
        {
            TxtDiskStatus.Text = "Failed to read the disks.";
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnRefreshDisks.IsEnabled = true;
            SetBusy(false);
        }
    }

    private DiskVisual BuildDisk(DiskInfo disk)
    {
        string title = $"Disk {disk.Index} ({disk.PartitionStyle}) — {disk.Model}";
        bool gpt = disk.PartitionStyle == "GPT";
        var rows = new List<PartitionRow>();
        var reserved = (disk.Reserved ?? []).Select(r => ReservedRow(title, disk.Index, r.Offset, r.Size)).ToList();
        ulong cursor = 0;

        // A free stretch of the disk, minus any reserved partition inside it.
        void AddFree(ulong from, ulong to, bool uninitialized)
        {
            foreach (var r in reserved.Where(r => r.Offset >= from && r.Offset < to).OrderBy(r => r.Offset))
            {
                if (r.Offset - from >= UnallocatedThreshold)
                    rows.Add(UnallocatedRow(title, disk.Index, from, r.Offset - from, uninitialized));
                from = r.Offset + r.Size;
            }
            if (to > from && to - from >= UnallocatedThreshold)
                rows.Add(UnallocatedRow(title, disk.Index, from, to - from, uninitialized));
        }

        foreach (var partition in disk.Partitions)
        {
            if (partition.Offset > cursor)
                AddFree(cursor, partition.Offset, false);

            string type = TranslateType(partition.Type, gpt);
            string state = StatusText(disk.Status, partition.Details);

            if (partition.Volumes.Count == 0)
            {
                rows.Add(new PartitionRow(title, $"Partition {partition.Index}", "", ByteFormatter.Format(partition.Size),
                    "", "", state, type, null, null, partition.Size, 0, false, disk.Index, partition.Number, gpt,
                    Offset: partition.Offset, Details: partition.Details));
            }

            foreach (var volume in partition.Volumes)
            {
                char? letter = volume.Letter.Length > 0 && char.IsLetter(volume.Letter[0]) ? char.ToUpperInvariant(volume.Letter[0]) : null;
                string name = letter is null ? $"Partition {partition.Index}"
                    : string.IsNullOrEmpty(volume.Label) ? $"{letter}:" : $"{letter}: {volume.Label}";
                ulong used = volume.Size > volume.Free ? volume.Size - volume.Free : 0;

                rows.Add(new PartitionRow(title, name, volume.FileSystem ?? "",
                    ByteFormatter.Format(volume.Size), WithPercent(used, volume.Size),
                    WithPercent(volume.Free, volume.Size),
                    state,
                    type, letter, volume.Label, partition.Size, used, false, disk.Index, partition.Number, gpt,
                    Offset: partition.Offset, Details: partition.Details));
            }

            cursor = Math.Max(cursor, partition.Offset + partition.Size);
        }

        if (disk.Size > cursor)
            AddFree(cursor, disk.Size, disk.PartitionStyle == "Uninitialized");

        var map = rows.Concat(reserved).OrderBy(r => r.Offset).ToList();
        return new DiskVisual(disk.Index, disk.PartitionStyle, disk.Size, rows, map);
    }

    /// <summary>The hidden MSR stretch: drawn on the map only, so no operation can pick it.</summary>
    private static PartitionRow ReservedRow(string disk, int diskIndex, ulong offset, ulong size) =>
        new(disk, "MSR", "", ByteFormatter.Format(size), "", "", "", "Reserved", null, null, size, 0, false, diskIndex,
            Offset: offset, Reserved: true);

    private static PartitionRow UnallocatedRow(string disk, int diskIndex, ulong offset, ulong size, bool uninitialized) =>
        new(disk, "*", "Unallocated", ByteFormatter.Format(size), "0 B", ByteFormatter.Format(size),
            "None", "", null, null, size, 0, true, diskIndex, Uninitialized: uninitialized, Offset: offset);

    /// <summary>"120 GB (33%)": the size followed by its share of the volume.</summary>
    private static string WithPercent(ulong bytes, ulong total) =>
        total == 0 ? ByteFormatter.Format(bytes) : $"{ByteFormatter.Format(bytes)} ({(double)bytes / total:P0})";

    /// <summary>Like Disk Management: "Healthy (Boot, Page File, Crash Dump, Basic Data Partition)".</summary>
    private static string StatusText(string? diskStatus, IReadOnlyList<string>? details)
    {
        string health = string.Equals(diskStatus, "OK", StringComparison.OrdinalIgnoreCase) ? "Healthy" : diskStatus ?? "Unknown";
        return details is { Count: > 0 } ? $"{health} ({string.Join(", ", details)})" : health;
    }

    private static string TranslateType(string type, bool gpt)
    {
        if (!gpt) return "Primary";
        if (type.Contains("System", StringComparison.OrdinalIgnoreCase)) return "EFI";
        if (type.Contains("Basic Data", StringComparison.OrdinalIgnoreCase)) return "Primary";
        return "Other";
    }

    // ------------------------------------------------------------------ Graphical map

    private void BuildMap(IReadOnlyList<DiskVisual> disks)
    {
        DiskMap.Children.Clear();
        _boxes.Clear();

        foreach (var disk in disks)
        {
            var line = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new StackPanel { Margin = new Thickness(2, 4, 8, 4) };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20,
                Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock { Text = $"Disk{disk.Index}", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(header);
            label.Children.Add(new TextBlock { Text = $"Basic {disk.Style}", FontSize = 11, Foreground = Brushes.DimGray });
            label.Children.Add(new TextBlock { Text = ByteFormatter.Format(disk.Size), FontSize = 11, Foreground = Brushes.DimGray });
            Grid.SetColumn(label, 0);
            line.Children.Add(label);

            var boxes = new Grid();
            double total = disk.Map.Sum(r => (double)r.Size);
            int column = 0;
            foreach (var part in disk.Map)
            {
                boxes.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max((double)part.Size, total * 0.07), GridUnitType.Star),
                    MinWidth = 56
                });
                var box = CreateBox(part);
                Grid.SetColumn(box, column++);
                boxes.Children.Add(box);
                _boxes[part] = box;
            }
            Grid.SetColumn(boxes, 1);
            line.Children.Add(boxes);

            DiskMap.Children.Add(line);
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private Border CreateBox(PartitionRow part)
    {
        bool empty = part.Unallocated || part.Reserved;
        var borderBrush = empty ? Brushes.Silver : new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xD5));
        var content = new StackPanel { Margin = new Thickness(3) };

        if (!empty)
        {
            // Used space as a rounded bar with the percentage inside. Without a volume (no drive letter) there is
            // no usage to show, so the bar stays empty instead of claiming 0%.
            bool hasUsage = part.Letter is not null;
            double fraction = !hasUsage || part.Size == 0 ? 0 : Math.Clamp((double)part.UsedBytes / part.Size, 0, 1);
            var bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });
            if (fraction > 0)
            {
                var filled = new Border
                {
                    Background = UsageFill,
                    CornerRadius = fraction >= 0.999 ? new CornerRadius(3) : new CornerRadius(3, 0, 0, 3)
                };
                Grid.SetColumn(filled, 0);
                bar.Children.Add(filled);
            }
            if (hasUsage)
            {
                var percent = new TextBlock
                {
                    Text = fraction.ToString("P0", CultureInfo.CurrentCulture), FontSize = 11, Foreground = UsageText,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumnSpan(percent, 2);
                bar.Children.Add(percent);
            }
            content.Children.Add(new Border
            {
                Height = UsageBarHeight, CornerRadius = new CornerRadius(4), Background = UsageTrack, BorderBrush = UsageTrackBorder,
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 3), Child = bar
            });
        }
        else
        {
            content.Children.Add(new Border { Height = UsageBarHeight + 3 });   // keeps the text lines level with the other boxes
        }

        string title = part.Reserved ? "Reserved (MSR)" : part.Unallocated ? "Unallocated"
            : string.IsNullOrEmpty(part.FileSystem) ? part.Name : $"{part.Name} ({part.FileSystem})";
        content.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
        content.Children.Add(new TextBlock { Text = part.Capacity, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Brushes.DimGray });

        string tooltip = title;
        if (!empty && part.PartitionNumber > 0
            && PartitionAlignment.Classify(part.Offset) == AlignmentStatus.Misaligned)
        {
            content.Children.Add(new TextBlock
            {
                Text = "⚠ Not aligned", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Foreground = Brushes.DarkOrange
            });
            tooltip = $"{title}\n{PartitionAlignment.Describe(AlignmentStatus.Misaligned)}";
        }

        var box = new Border
        {
            Background = NormalBox, BorderBrush = borderBrush, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 3, 0), Child = content, ToolTip = tooltip
        };
        if (part.Reserved)
        {
            box.ToolTip = "Microsoft Reserved Partition: space that Windows keeps for itself on GPT disks. It cannot be used.";
            return box;
        }
        box.Cursor = Cursors.Hand;
        box.ContextMenu = BuildContextMenu();
        box.MouseRightButtonDown += (_, _) => DisksGrid.SelectedItem = part;
        box.MouseLeftButtonDown += (_, _) =>
        {
            DisksGrid.SelectedItem = part;
            DisksGrid.ScrollIntoView(part);
        };
        return box;
    }

    private void DisksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var (row, box) in _boxes)
            box.Background = ReferenceEquals(row, DisksGrid.SelectedItem) ? SelectedBox : NormalBox;

        RefreshAvailableOperations();
    }

    // ------------------------------------------------------------------ Context menu

    /// <summary>
    /// Right-click menu that mirrors the Operations panel: same items, same order, a separator where the panel has
    /// a group title, and the same highlight color. Each item clicks the matching button.
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var mirrored = new List<(MenuItem Item, Button Button)>();
        foreach (var child in OperationsPanel.Children)
        {
            if (child is TextBlock)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                continue;
            }
            if (child is not Button button) continue;

            var content = ((StackPanel)button.Content).Children;
            var item = new MenuItem
            {
                Header = content.OfType<TextBlock>().Last().Text,
                ToolTip = button.ToolTip,
                Icon = content[0] switch
                {
                    // A drawn icon (not in the icon font): same shape and color as the button's.
                    System.Windows.Shapes.Path path => new System.Windows.Shapes.Path
                    {
                        Data = path.Data, Fill = path.Fill, Width = path.Width, Height = path.Height, Stretch = path.Stretch
                    },
                    TextBlock glyph => new TextBlock
                    {
                        Text = glyph.Text, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = (Brush)FindResource("Accent")
                    },
                    _ => null
                }
            };
            if (button.Background is SolidColorBrush { Color.A: > 0 } highlight)
            {
                item.Background = highlight;
                item.FontWeight = FontWeights.SemiBold;
            }
            item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            menu.Items.Add(item);
            mirrored.Add((item, button));
        }

        menu.Opened += (_, _) =>
        {
            foreach (var (item, button) in mirrored)
                item.IsEnabled = button.IsEnabled;
        };
        return menu;
    }

    private void DisksGrid_RightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow)
            source = VisualTreeHelper.GetParent(source);
        if (source is DataGridRow row)
            row.IsSelected = true;
    }

    // ------------------------------------------------------------------ Operations panel

    private PartitionRow? Selected => DisksGrid.SelectedItem as PartitionRow;

    private void RefreshAvailableOperations()
    {
        bool hasLetter = Selected is { Unallocated: false, Letter: not null };
        OpResize.IsEnabled = hasLetter;
        OpMerge.IsEnabled = hasLetter && PreviousNeighbor() is not null;
        OpSplit.IsEnabled = hasLetter;
        OpClone.IsEnabled = Selected is { Unallocated: false };   // always clickable: it explains why a partition cannot be cloned
        OpMove.IsEnabled = Selected is { Unallocated: false };   // always clickable: it explains why a partition cannot move
        OpAlign.IsEnabled = Selected is { Unallocated: false, PartitionNumber: > 0 } aligned
                            && PartitionAlignment.Classify(aligned.Offset) == AlignmentStatus.Misaligned;
        OpLabel.IsEnabled = hasLetter;
        OpLetter.IsEnabled = hasLetter;
        OpFormat.IsEnabled = hasLetter;
        OpConvert.IsEnabled = hasLetter && Selected is { FileSystem: "FAT32" or "NTFS" };
        OpConvertStyle.IsEnabled = Selected is not null;   // any row of the disk; it explains why a disk cannot be converted
        OpDelete.IsEnabled = hasLetter;
        OpWipe.IsEnabled = hasLetter && Selected is { PartitionNumber: > 0 };
        OpWipeFree.IsEnabled = hasLetter;
        OpWipeDisk.IsEnabled = Selected is not null;   // any row of the disk; it explains why a disk cannot be wiped
        OpCreate.IsEnabled = Selected is { Unallocated: true, Uninitialized: false };
        OpInitialize.IsEnabled = Selected is { Unallocated: true, Uninitialized: true };
        OpCheck.IsEnabled = hasLetter;
        OpActive.IsEnabled = Selected is { Unallocated: false, Letter: not null, Gpt: false, PartitionNumber: > 0 };
        OpHide.IsEnabled = hasLetter;
        OpShow.IsEnabled = Selected is { Unallocated: false, Letter: null, PartitionNumber: > 0 };
        OpExplore.IsEnabled = hasLetter;
        OpRecover.IsEnabled = hasLetter;
        OpRecoverPartitions.IsEnabled = _disks.Count > 0;   // the window lets you pick the disk
        OpProperties.IsEnabled = Selected is { Unallocated: false };
        OpVerifyWipe.IsEnabled = hasLetter;
        OpClusterMap.IsEnabled = hasLetter;
    }

    private async void OpResize_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: false, Letter: { } letter } row) return;

        // Room to extend = unallocated space right after the partition on the same disk.
        var rows = DisksGrid.Items.OfType<PartitionRow>().ToList();
        int at = rows.IndexOf(row);
        ulong room = at >= 0 && at + 1 < rows.Count && rows[at + 1] is { Unallocated: true } next && next.DiskIndex == row.DiskIndex
            ? next.Size : 0;

        // Ask Windows for the real limits (unmovable files make the minimum larger than the used space).
        // If the query fails, fall back to the used space and the adjacent free space.
        ulong min = row.UsedBytes, max = row.Size + room;
        Cursor = Cursors.Wait;
        SetBusy(true);
        try
        {
            if (await SupportedSize.QueryAsync(letter, new PowerShellExecutor()) is { } limits)
                (min, max) = ((ulong)limits.MinBytes, Math.Max((ulong)limits.MaxBytes, row.Size));
        }
        finally
        {
            Cursor = null;
            SetBusy(false);
        }

        var dialog = new ResizeDialog(letter, row.Size, min, max, row.UsedBytes) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _queue.Add(new ResizePartition(letter, dialog.NewSizeBytes));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The partition right before the selected one on the same disk, if it has a drive letter.</summary>
    private PartitionRow? PreviousNeighbor()
    {
        if (Selected is not { Unallocated: false } row) return null;
        var rows = DisksGrid.Items.OfType<PartitionRow>().ToList();
        int at = rows.IndexOf(row);
        return at > 0 && rows[at - 1] is { Unallocated: false, Letter: not null } prev && prev.DiskIndex == row.DiskIndex ? prev : null;
    }

    private void OpMerge_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: false, Letter: { } source } row || PreviousNeighbor() is not { Letter: { } target } prev) return;

        ulong free = prev.Size > prev.UsedBytes ? prev.Size - prev.UsedBytes : 0;
        ulong needed = MergePartitions.RequiredFreeBytes(row.UsedBytes);
        if (free < needed)
        {
            MessageBox.Show(this,
                $"The files of {source}: ({ByteFormatter.Format(row.UsedBytes)}) do not fit in {target}:. " +
                $"{target}: needs {ByteFormatter.Format(needed)} free (5% plus 64 MB of slack) but has {ByteFormatter.Format(free)}.\n\n" +
                $"Free up space on {target}: or on {source}: and try again.",
                "Merge partitions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ParametersDialog("Merge partitions",
            $"Merge {source}: into {target}: — the files of {source}: are copied to {target}:\\Merged_{source}, " +
            $"then {source}: is deleted and {target}: is extended. {target}: needs {ByteFormatter.Format(needed)} free " +
            $"(has {ByteFormatter.Format(free)}).", []) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _queue.Add(new MergePartitions(source, target));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OpSplit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: false, Letter: { } letter } row) return;

        ulong min = row.UsedBytes;
        Cursor = Cursors.Wait;
        SetBusy(true);
        try
        {
            if (await SupportedSize.QueryAsync(letter, new PowerShellExecutor()) is { } limits)
                min = (ulong)limits.MinBytes;
        }
        finally
        {
            Cursor = null;
            SetBusy(false);
        }

        ulong room = row.Size > min ? row.Size - min : 0;
        if (room < 2UL * 1024 * 1024)
        {
            MessageBox.Show(this, $"{letter}: has no free space to split off.", "Split partition", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ParametersDialog("Split partition",
            $"Drive {letter}: — at most {ByteFormatter.Format(room)} can go to the new partition. The files stay on {letter}:.",
            [new DialogField("New partition size:", IsSize: true, MaxBytes: (long)room,
                             BarTotal: (long)row.Size, BarUsed: (long)min, Reversed: true),
             new DialogField("File system:", "NTFS", Enum.GetNames<FileSystemFormat>()),
             new DialogField("Label:"),
             LetterField("Drive letter:", AutomaticLetter)]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var v = dialog.Values;
            long size = ConvertBytes(v[0]) ?? throw new FormatException("Enter the size of the new partition.");
            if ((ulong)size > room)
                throw new ArgumentException($"The new partition can have at most {ByteFormatter.Format(room)}.");
            char? newLetter = v[3].Length == 1 ? char.ToUpperInvariant(v[3][0]) : null;
            if (newLetter is not null && DisksGrid.Items.OfType<PartitionRow>().Any(r => r.Letter == newLetter))
                throw new ArgumentException($"Drive letter {newLetter}: is already in use.");

            _queue.Add(new SplitPartition(letter, size, ConvertFileSystem(v[1]), v[2], newLetter));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowMoveRefused(string reason) =>
        MessageBox.Show(this, reason, "Move partition", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>
    /// Explains up front why a partition cannot be moved, instead of failing later while applying.
    /// Returns its drive letter when the move may go ahead.
    /// </summary>
    private char? CheckCanMove(PartitionRow row)
    {
        if (row.Letter is not { } letter)
        {
            ShowMoveRefused("This partition has no drive letter, so it cannot be moved. " +
                            "Use \"Show partition\" to give it a letter first.");
            return null;
        }
        if (letter == _systemLetter)
        {
            ShowMoveRefused($"{letter}: is the Windows drive. Moving it could make Windows impossible to start, so it is not allowed.");
            return null;
        }
        if (row.PartitionNumber < 1)
        {
            ShowMoveRefused($"Windows does not expose {letter}: as a partition that can be moved.");
            return null;
        }

        // Boot/active partitions (another Windows install, a boot manager) read their own position from the boot
        // sector, which a move does not rewrite: moving them can leave that system unable to start.
        var bootRoles = row.Details?.Where(d => d is "Boot" or "Active" or "Page File" or "Crash Dump").ToList();
        if (bootRoles is { Count: > 0 })
        {
            var answer = MessageBox.Show(this,
                $"{letter}: is marked as {string.Join(", ", bootRoles)}.\n\n" +
                "This partition is used to start or run Windows. Moving it does not update its boot sector, so the " +
                "system that boots from it may fail to start, and a drive in use cannot be locked anyway.\n\n" +
                "Only continue if you are sure this is just a data partition. Move it anyway?",
                "Move partition", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return null;
        }
        return letter;
    }

    /// <summary>Reads the partition table (read-only), or explains why the disk cannot be rearranged.</summary>
    private async Task<DriveLayout?> LoadLayoutForMoveAsync(int diskIndex)
    {
        Cursor = Cursors.Wait;
        SetBusy(true);
        try
        {
            return await Task.Run(() => PhysicalDiskMover.LoadLayout(diskIndex));
        }
        catch (InvalidDataException ex)
        {
            ShowMoveRefused($"Disk {diskIndex} cannot be rearranged: {ex.Message}");
        }
        catch (Exception ex)
        {
            ShowMoveRefused($"Could not read the partition table of disk {diskIndex}: {ex.Message}");
        }
        finally
        {
            Cursor = null;
            SetBusy(false);
        }
        return null;
    }

    private async void OpAlign_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: false } row || CheckCanMove(row) is not { } letter) return;
        if (await LoadLayoutForMoveAsync(row.DiskIndex) is not { } layout) return;

        long length = (long)row.Size, current = (long)row.Offset;
        long? destination;
        try
        {
            destination = layout.FindAlignedOffset(row.PartitionNumber, current, length);
        }
        catch (InvalidOperationException ex)
        {
            ShowMoveRefused(ex.Message);
            return;
        }
        if (destination is not { } target)
        {
            ShowMoveRefused($"There is no room to move {letter}: to a 1 MB boundary: it needs up to 1 MB of free space " +
                            "right before or right after it. Shrink this partition (or the one next to it) by 1 MB, " +
                            "apply that, then align it.");
            return;
        }

        var answer = MessageBox.Show(this,
            $"{letter}: starts at {current:N0} bytes, which is not a multiple of 4 KB. It will be moved to " +
            $"{target:N0} bytes ({ByteFormatter.Format(target)}), a 1 MB boundary, so writes no longer straddle two " +
            $"physical sectors.\n\n{MovePartition.PowerLossWarning}\n\nQueue the move?",
            "Align partition", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _queue.Add(new MovePartition(row.DiskIndex, row.PartitionNumber, letter, current, length, target));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OpMove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: false } row || CheckCanMove(row) is not { } letter) return;
        if (await LoadLayoutForMoveAsync(row.DiskIndex) is null) return;

        const long mib = 1L << 20;
        long length = (long)row.Size, current = (long)row.Offset;

        // Free regions of this disk, counting the partition's own space as free (it is vacated while it moves).
        // Two free rows only merge when nothing sits between them.
        var regions = new List<(long Start, long End)>();
        bool previousFree = false;
        foreach (var r in DisksGrid.Items.OfType<PartitionRow>()
                     .Where(r => r.DiskIndex == row.DiskIndex && !r.Uninitialized).OrderBy(r => r.Offset))
        {
            if (!r.Unallocated && !ReferenceEquals(r, row))
            {
                previousFree = false;
                continue;
            }
            long start = (long)r.Offset, end = (long)(r.Offset + r.Size);
            if (previousFree) regions[^1] = (regions[^1].Start, end);
            else regions.Add((start, end));
            previousFree = true;
        }

        // The partition can slide within the free space around it (the unallocated stretches touching it).
        var home = regions.FirstOrDefault(r => r.Start <= current && current < r.End);
        if (home.End - home.Start - length < mib)
        {
            MessageBox.Show(this, $"There is no free space next to {letter}: on disk {row.DiskIndex} to move it into.",
                "Move partition", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new MoveDialog(letter, row.Size, row.UsedBytes, home.Start, home.End, current) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _queue.Add(new MovePartition(row.DiskIndex, row.PartitionNumber, letter, current, length, dialog.NewOffsetBytes));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpClone_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Clone partition";
        if (Selected is not { Unallocated: false } row) return;
        if (row.Letter is not { } letter || row.PartitionNumber < 1)
        {
            MessageBox.Show(this, "Only a partition with a drive letter can be cloned. " +
                "Use \"Show partition\" to give it one first.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (letter == _systemLetter)
        {
            MessageBox.Show(this, $"{letter}: is the Windows drive. A copy of a drive that is running would be inconsistent, so it is not allowed.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (row.FileSystem is not ("NTFS" or "FAT32" or "exFAT"))
        {
            MessageBox.Show(this, "Only NTFS, FAT32 and exFAT partitions can be cloned.", title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        const long mib = 1L << 20;
        long length = (long)row.Size;

        // Unallocated stretches (any disk) with room for the partition from their first 1 MB boundary.
        var targets = new List<(string Text, int Disk, long Offset)>();
        foreach (var r in DisksGrid.Items.OfType<PartitionRow>().Where(r => r.Unallocated && !r.Uninitialized))
        {
            long start = ((long)r.Offset + mib - 1) / mib * mib;
            if (start + length > (long)(r.Offset + r.Size)) continue;
            targets.Add(($"Disk {r.DiskIndex} — {ByteFormatter.Format((long)r.Size)} free at {ByteFormatter.Format(start)}", r.DiskIndex, start));
        }
        if (targets.Count == 0)
        {
            MessageBox.Show(this, $"There is no unallocated space of {ByteFormatter.Format(length)} or more on any disk. " +
                "Free some, or add a disk, then try again.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ParametersDialog(title,
            $"Copy {letter}: ({ByteFormatter.Format(length)}) sector by sector into unallocated space. The original is not " +
            "changed. The copy has the same size; resize it afterwards if the space is larger.",
            [new DialogField("Copy to:", "", targets.Select(t => t.Text).ToList()),
             LetterField("Drive letter of the copy:", AutomaticLetter)]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var target = targets.FirstOrDefault(t => t.Text == dialog.Values[0]);
        if (target.Text is null) return;
        char? copyLetter = dialog.Values[1] is { Length: 1 } text ? char.ToUpperInvariant(text[0]) : null;

        var answer = MessageBox.Show(this,
            $"{ClonePartition.DuplicateWarning}\n\nQueue the clone of {letter}: to disk {target.Disk}?",
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        TryQueue(new ClonePartition(row.DiskIndex, row.PartitionNumber, letter, (long)row.Offset, length,
            target.Disk, target.Offset, ConvertFileSystem(row.FileSystem), copyLetter));
    }

    private async void OpConvertStyle_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Convert MBR / GPT";
        if (Selected is not { } selected || _disks.FirstOrDefault(d => d.Index == selected.DiskIndex) is not { } disk) return;

        DiskPartitionStyle? target = disk.PartitionStyle switch
        {
            "MBR" => DiskPartitionStyle.GPT,
            "GPT" => DiskPartitionStyle.MBR,
            _ => null
        };
        if (target is not { } style)
        {
            MessageBox.Show(this, $"Disk {disk.Index} has no MBR or GPT partition table to convert. Use \"Initialize disk\" on a blank disk.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rows = DisksGrid.Items.OfType<PartitionRow>()
            .Where(r => r.DiskIndex == disk.Index && r.Letter is not null && r.PartitionNumber > 0).ToList();
        if (_systemLetter is { } system && rows.Any(r => r.Letter == system))
        {
            MessageBox.Show(this, $"Disk {disk.Index} holds the Windows drive ({system}:). Which style it uses decides how the " +
                "computer boots, so it is not converted here. Use Microsoft's mbr2gpt.exe for the disk Windows boots from.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Read-only look at the table: explains up front why the disk cannot be converted.
        Cursor = Cursors.Wait;
        SetBusy(true);
        string? problem = null;
        try
        {
            await Task.Run(() => PhysicalLayoutConverter.Check(disk.Index, style));
        }
        catch (Exception ex)
        {
            problem = ex is InvalidOperationException ? ex.Message : $"Could not read the partition table of disk {disk.Index}: {ex.Message}";
        }
        finally
        {
            Cursor = null;
            SetBusy(false);
        }
        if (problem is not null)
        {
            MessageBox.Show(this, problem, title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string drives = rows.Count == 0 ? "no drive letters" : string.Join(", ", rows.Select(r => $"{r.Letter}:"));
        var answer = MessageBox.Show(this,
            $"Convert disk {disk.Index} ({disk.Model}, {ByteFormatter.Format(disk.Size)}; {drives}) from {disk.PartitionStyle} to {style}?\n\n" +
            ConvertPartitionStyle.WarningFor(style),
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        TryQueue(new ConvertPartitionStyle(disk.Index, style,
            rows.Select(r => new DriveMount(r.Letter!.Value, (long)r.Offset)).ToList()));
    }

    private void OpLabel_Click(object sender, RoutedEventArgs e) =>
        AddWithDialog("Change label",
            [new DialogField("New label:", Selected?.Label ?? "")],
            (letter, v) => new ChangeVolumeLabel(letter, v[0]));

    private const string AutomaticLetter = "(automatic)";

    /// <summary>Combo-box field with the letters still free (not on a volume, a drive or the pending queue).</summary>
    private DialogField LetterField(string label, string? first = null)
    {
        var used = DisksGrid.Items.OfType<PartitionRow>().Where(r => r.Letter is not null).Select(r => r.Letter!.Value)
            .Concat(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])))
            .Concat(_queue.Pending.SelectMany(op => op.AffectedDrives))
            .ToHashSet();

        var options = new List<string>();
        if (first is not null) options.Add(first);
        options.AddRange(Enumerable.Range('C', 'Z' - 'C' + 1).Select(c => (char)c)
            .Where(c => !used.Contains(c)).Select(c => c.ToString()));
        return new DialogField(label, "", options);
    }

    private void OpLetter_Click(object sender, RoutedEventArgs e) =>
        AddWithDialog("Change letter",
            [LetterField("New letter:")],
            (letter, v) =>
            {
                if (v[0].Length != 1)
                    throw new ArgumentException("No drive letter is available.");
                char newLetter = char.ToUpperInvariant(v[0][0]);
                if (DisksGrid.Items.OfType<PartitionRow>().Any(r => r.Letter == newLetter))
                    throw new ArgumentException($"Drive letter {newLetter}: is already in use.");
                return new ChangeDriveLetter(letter, newLetter);
            });

    private void OpFormat_Click(object sender, RoutedEventArgs e) =>
        AddWithDialog("Format partition",
            [new DialogField("File system:", "NTFS", Enum.GetNames<FileSystemFormat>()),
             new DialogField("Label:", Selected?.Label ?? "")],
            (letter, v) => new FormatVolume(letter, ConvertFileSystem(v[0]), v[1]),
            "Formatting ERASES all data on the drive.");

    private void OpConvert_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: false, Letter: { } letter, FileSystem: "FAT32" or "NTFS" } row) return;

        const string title = "Convert FAT32 / NTFS";
        if (row.FileSystem == "FAT32")
        {
            var toNtfs = new ParametersDialog(title,
                $"Convert {letter}: from FAT32 to NTFS in place — the files are kept. {ConvertVolume.ToNtfsWarning}", []) { Owner = this };
            if (toNtfs.ShowDialog() == true)
                TryQueue(new ConvertVolume(letter, FileSystemFormat.NTFS));
            return;
        }

        if (row.Size > (ulong)ConvertVolume.MaxFat32VolumeBytes)
        {
            MessageBox.Show(this,
                $"{letter}: is {ByteFormatter.Format(row.Size)}. Windows formats FAT32 volumes only up to 32 GB, " +
                "so this volume cannot be converted.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ulong needed = ConvertVolume.RequiredStagingBytes(row.UsedBytes);
        var candidates = DisksGrid.Items.OfType<PartitionRow>()
            .Where(r => r is { Unallocated: false, Letter: not null } && r.Letter != letter
                        && r.Size > r.UsedBytes && r.Size - r.UsedBytes >= needed)
            .Select(r => r.Letter!.Value.ToString())
            .ToList();
        if (candidates.Count == 0)
        {
            MessageBox.Show(this,
                $"Converting {letter}: to FAT32 copies its files ({ByteFormatter.Format(row.UsedBytes)}) to another drive " +
                $"first, which needs {ByteFormatter.Format(needed)} free (5% plus 64 MB of slack). No other drive has that much free space.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var toFat32 = new ParametersDialog(title,
            $"Convert {letter}: from NTFS to FAT32. {ConvertVolume.ToFat32Warning}",
            [new DialogField("Staging drive (needs " + ByteFormatter.Format(needed) + " free):", "", candidates)]) { Owner = this };
        if (toFat32.ShowDialog() != true) return;

        TryQueue(new ConvertVolume(letter, FileSystemFormat.FAT32, toFat32.Values[0][0]));
    }

    private void TryQueue(DiskOperation operation)
    {
        try
        {
            _queue.Add(operation);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpCreate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: true, Uninitialized: false } free) return;

        var dialog = new ParametersDialog("Create partition",
            $"Disk {free.DiskIndex} — free space: {free.Capacity}",
            [new DialogField("Size (empty = all free space):", IsSize: true, MaxBytes: (long)free.Size, BarTotal: (long)free.Size),
             new DialogField("File system:", "NTFS", Enum.GetNames<FileSystemFormat>()),
             new DialogField("Label:"),
             LetterField("Drive letter:", AutomaticLetter)]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var v = dialog.Values;
            long? size = ConvertBytes(v[0]);
            if (size is { } bytes && (ulong)bytes > free.Size)
                throw new ArgumentException($"The size exceeds the free space ({free.Capacity}).");
            if (size is { } all && (ulong)all == free.Size) size = null;   // dragged to the end: use all free space
            char? letter = v[3].Length == 1 ? char.ToUpperInvariant(v[3][0]) : null;   // "(automatic)" = no letter
            if (letter is not null && DisksGrid.Items.OfType<PartitionRow>().Any(r => r.Letter == letter))
                throw new ArgumentException($"Drive letter {letter}: is already in use.");

            _queue.Add(new CreatePartition(free.DiskIndex, ConvertFileSystem(v[1]), v[2], size, letter, (long)free.Offset, (long)free.Size));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpInitialize_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: true, Uninitialized: true } row) return;

        var dialog = new ParametersDialog("Initialize disk",
            $"Disk {row.DiskIndex} — {row.Capacity}. Only blank disks can be initialized.",
            [new DialogField("Partition style:", nameof(DiskPartitionStyle.GPT), Enum.GetNames<DiskPartitionStyle>())]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (!Enum.TryParse(dialog.Values[0], ignoreCase: true, out DiskPartitionStyle style))
                throw new ArgumentException("Enter the partition style: MBR or GPT.");
            _queue.Add(new InitializeDisk(row.DiskIndex, style));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpDelete_Click(object sender, RoutedEventArgs e) =>
        AddWithDialog("Delete partition", [],
            (letter, _) => new DeletePartition(letter),
            "Deleting ERASES all data on the drive.");

    // ------------------------------------------------------------------ Wipe

    private const string WipeZeros = "Zeros (one pass)";
    private const string WipeZerosVerified = "Zeros, then read back to verify (twice as long)";
    private const string WipeRandom = "Random data (one pass)";

    private static (WipePattern Pattern, bool Verify) ConvertWipeMethod(string text) => text switch
    {
        WipeZerosVerified => (WipePattern.Zeros, true),
        WipeRandom => (WipePattern.Random, false),
        _ => (WipePattern.Zeros, false)
    };

    private void ShowWipeRefused(string title, string reason) =>
        MessageBox.Show(this, reason, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>The " On an SSD..." paragraph when the disk is an SSD, otherwise nothing.</summary>
    private string SsdNote(int diskIndex) =>
        _disks.FirstOrDefault(d => d.Index == diskIndex) is { IsSsd: true } ? "\n\n" + Wipe.SsdWarning : "";

    private void OpWipe_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Wipe partition";
        if (Selected is not { Unallocated: false, Letter: { } letter, PartitionNumber: > 0 } row) return;
        if (letter == _systemLetter)
        {
            ShowWipeRefused(title, $"{letter}: is the Windows drive, so it cannot be wiped.");
            return;
        }
        var roles = row.Details?.Where(d => d is "Boot" or "Active" or "Page File" or "Crash Dump" or "System").ToList();
        if (roles is { Count: > 0 })
        {
            ShowWipeRefused(title, $"{letter}: is marked as {string.Join(", ", roles)}: it is used to start or run " +
                                   "Windows, so it cannot be wiped.");
            return;
        }

        var dialog = new ParametersDialog(title,
            $"Drive {letter}: ({row.Capacity}) — every file on it is destroyed for good and the partition becomes RAW " +
            $"(format it afterwards). The other partitions are not touched.{SsdNote(row.DiskIndex)}",
            [new DialogField("Method:", WipeZeros, [WipeZeros, WipeZerosVerified, WipeRandom]),
             new DialogField($"To confirm, type the drive letter ({letter}):")]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (!string.Equals(dialog.Values[1].TrimEnd(':'), letter.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            ShowWipeRefused(title, "The drive letter you typed does not match, so nothing was queued.");
            return;
        }
        var (pattern, verify) = ConvertWipeMethod(dialog.Values[0]);
        TryQueue(new WipePartition(row.DiskIndex, row.PartitionNumber, letter, (long)row.Offset, (long)row.Size, pattern, verify));
    }

    private void OpWipeFree_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Wipe free space";
        if (Selected is not { Unallocated: false, Letter: { } letter } row) return;
        if (letter == _systemLetter)
        {
            ShowWipeRefused(title, $"{letter}: is the Windows drive. Filling it up while Windows runs could make it " +
                                   "unstable, so this is not allowed.");
            return;
        }

        var dialog = new ParametersDialog(title,
            $"Drive {letter}: — the free space ({row.Unused}) is overwritten so files deleted earlier can no longer be " +
            "recovered. The files on the drive are kept. The drive is completely full while this runs." +
            SsdNote(row.DiskIndex),
            [new DialogField("Method:", WipeZeros, [WipeZeros, WipeRandom])]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        TryQueue(new WipeFreeSpace(letter, ConvertWipeMethod(dialog.Values[0]).Pattern));
    }

    private void OpWipeDisk_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Wipe disk";
        if (Selected is not { } selected || _disks.FirstOrDefault(d => d.Index == selected.DiskIndex) is not { } disk) return;

        var letters = DisksGrid.Items.OfType<PartitionRow>()
            .Where(r => r.DiskIndex == disk.Index && r.Letter is not null).Select(r => r.Letter!.Value).Distinct().ToList();
        if (_systemLetter is { } system && letters.Contains(system))
        {
            ShowWipeRefused(title, $"Disk {disk.Index} holds the Windows drive ({system}:), so it cannot be wiped.");
            return;
        }

        string drives = letters.Count == 0 ? "no drive letters" : string.Join(", ", letters.Select(l => $"{l}:"));
        var dialog = new ParametersDialog(title,
            $"Disk {disk.Index} — {disk.Model}, {ByteFormatter.Format(disk.Size)} ({drives}). Every partition is " +
            "deleted and every sector overwritten: nothing on this disk can be recovered afterwards. It ends up blank " +
            $"and has to be initialized again.{SsdNote(disk.Index)}",
            [new DialogField("Method:", WipeZeros, [WipeZeros, WipeZerosVerified, WipeRandom]),
             new DialogField($"To confirm, type the disk number ({disk.Index}):")]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (dialog.Values[1] != disk.Index.ToString(CultureInfo.InvariantCulture))
        {
            ShowWipeRefused(title, "The disk number you typed does not match, so nothing was queued.");
            return;
        }
        var (pattern, verify) = ConvertWipeMethod(dialog.Values[0]);
        TryQueue(new WipeDisk(disk.Index, pattern, verify, letters));
    }

    private const string ScanOnly = "Scan only (read-only)";
    private const string ScanAndRepair = "Scan and repair";

    private void OpCheck_Click(object sender, RoutedEventArgs e) =>
        AddWithDialog("Check for errors",
            [new DialogField("Mode:", ScanOnly, [ScanOnly, ScanAndRepair])],
            (letter, v) => new CheckVolume(letter, Repair: v[0] == ScanAndRepair),
            "Repairing takes the volume offline and is not allowed on the Windows drive.");

    private void OpActive_Click(object sender, RoutedEventArgs e) =>
        AddWithDialog("Set active partition", [],
            (letter, _) => new SetActivePartition(letter),
            "Marks the partition as active (bootable). MBR disks only.");

    private void OpHide_Click(object sender, RoutedEventArgs e) =>
        AddWithDialog("Hide partition", [],
            (letter, _) => new HidePartition(letter),
            "Removes the drive letter. The data stays untouched.");

    private void OpShow_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: false, Letter: null, PartitionNumber: > 0 } row) return;

        var dialog = new ParametersDialog("Show partition",
            $"Disk {row.DiskIndex}, partition {row.PartitionNumber} — {row.Capacity}",
            [LetterField("Drive letter:")]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            string text = dialog.Values[0];
            if (text.Length != 1)
                throw new ArgumentException("No drive letter is available.");
            char letter = char.ToUpperInvariant(text[0]);
            if (DisksGrid.Items.OfType<PartitionRow>().Any(r => r.Letter == letter))
                throw new ArgumentException($"Drive letter {letter}: is already in use.");

            _queue.Add(new ShowPartition(row.DiskIndex, row.PartitionNumber, letter));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddWithDialog(string title, IReadOnlyList<DialogField> fields,
        Func<char, string[], DiskOperation> create, string? warning = null)
    {
        if (Selected is not { Letter: { } letter }) return;

        string description = warning is null ? $"Drive {letter}:" : $"Drive {letter}: — {warning}";
        var dialog = new ParametersDialog(title, description, fields) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _queue.Add(create(letter, dialog.Values));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpExplore_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { Letter: { } letter })
            Process.Start(new ProcessStartInfo("explorer.exe", $"{letter}:\\") { UseShellExecute = true });
    }

    private void OpProperties_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: false } p) return;

        var disk = _disks.FirstOrDefault(d => d.Index == p.DiskIndex);
        var partition = disk?.Partitions.FirstOrDefault(x => x.Number == p.PartitionNumber);
        new PropertiesWindow(p, disk, partition) { Owner = this }.Show();
    }

    private void OpVerifyWipe_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { Unallocated: false, Letter: not null } p)
            new VerifyWipeWindow(p) { Owner = this }.Show();
    }

    private void OpClusterMap_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { Unallocated: false, Letter: not null } p)
            new ClusterMapWindow(p) { Owner = this }.Show();
    }

    /// <summary>Parses a size field (bytes, invariant culture); empty = extend to the maximum.</summary>
    private static long? ConvertBytes(string text) =>
        text.Length == 0 ? null : long.Parse(text, CultureInfo.InvariantCulture);

    private static FileSystemFormat ConvertFileSystem(string text)
    {
        foreach (var name in Enum.GetNames<FileSystemFormat>())
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<FileSystemFormat>(name);
        }
        throw new ArgumentException("Enter the file system: NTFS, FAT32 or exFAT.");
    }

    // ------------------------------------------------------------------ Pending operations

    private void RefreshPending()
    {
        PendingList.ItemsSource = _queue.Pending.Select((op, i) => new QueueItem(i + 1, op)).ToList();
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        var pending = _queue.Pending;
        if (pending.Count > 0)
            _queue.Remove(pending[^1]);
    }

    private void BtnClearQueue_Click(object sender, RoutedEventArgs e) => _queue.Clear();

    private void BtnCancelOperations_Click(object sender, RoutedEventArgs e) => _operationsCts?.Cancel();

    private async void BtnApplyOperations_Click(object sender, RoutedEventArgs e)
    {
        var pending = _queue.Pending;
        if (pending.Count == 0)
        {
            MessageBox.Show(this, "The pending operations queue is empty.", "Notice");
            return;
        }

        bool simulation = ChkSimulation.IsChecked == true;

        if (!simulation && pending.Any(WritesRawSectors) && !await ConfirmControlledFolderAccessAsync())
            return;

        if (!simulation)
        {
            string list = string.Join("\n", pending.Select(p => $"• [{p.RiskText}] {p.Description}"));
            string warnings = string.Concat(pending.Select(p => p.Warning).OfType<string>().Distinct().Select(w => $"\n\n⚠ {w}"));
            var answer = MessageBox.Show(this,
                $"Apply the operations below?\n\n{list}{warnings}\n\nDestructive operations cannot be undone.",
                "Confirm operations", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        _operationsCts?.Dispose();
        _operationsCts = new CancellationTokenSource();
        ToggleOperations(running: true);
        SetBusy(true);

        MenuShowLog.IsChecked = true;
        TxtOperationsLog.Clear();
        IProgress<string> progress = new Progress<string>(message =>
        {
            TxtOperationsLog.AppendText(message + Environment.NewLine);
            TxtOperationsLog.ScrollToEnd();
        });

        try
        {
            var results = await _queue.ApplyAsync(new PowerShellExecutor(), simulation, progress, _operationsCts.Token);

            int failures = results.Count(r => !r.Success);
            progress.Report(simulation
                ? $"Simulation finished: {results.Count} operation(s) analyzed. Nothing was changed."
                : $"Done: {results.Count - failures} succeeded, {failures} failed. Log: {FileLog.DefaultPath}");

            if (!simulation)
            {
                await LoadDisksAsync();

                // After a move Windows takes a moment to mount the volume at its new position; until then it has no
                // letter, size or free space, so reload a few times until every moved drive is back.
                var moved = results.Where(r => r.Success).Select(r => r.Operation).OfType<MovePartition>()
                    .Select(m => char.ToUpperInvariant(m.Drive)).ToList();
                for (int attempt = 0; attempt < 8 && moved.Count > 0
                     && !moved.All(d => DisksGrid.Items.OfType<PartitionRow>().Any(r => r.Letter == d && r.FileSystem.Length > 0)); attempt++)
                {
                    await Task.Delay(1000);
                    await LoadDisksAsync();
                }
            }

            if (results.Any(r => !r.Success && WritesRawSectors(r.Operation) && r.Detail.Contains("Controlled folder access")))
                ShowControlledFolderAccessHelp(
                    "Windows blocked FreePartition from writing to the disk (Controlled folder access). Nothing was changed.");
        }
        catch (OperationCanceledException)
        {
            TxtOperationsLog.AppendText("Canceled." + Environment.NewLine);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ToggleOperations(running: false);
            SetBusy(false);
        }
    }

    /// <summary>
    /// Before a move: if Windows' Controlled folder access would block the write, say how to allow the app.
    /// Returns false when the user chose not to apply now.
    /// </summary>
    private async Task<bool> ConfirmControlledFolderAccessAsync()
    {
        Cursor = Cursors.Wait;
        SetBusy(true);
        bool? blocking;
        try
        {
            blocking = await ControlledFolderAccess.IsBlockingAsync(Environment.ProcessPath, new PowerShellExecutor());
        }
        finally
        {
            Cursor = null;
            SetBusy(false);
        }
        if (blocking != true) return true;

        var answer = MessageBox.Show(this,
            "Windows Security has \"Controlled folder access\" (ransomware protection) turned on, and FreePartition.exe is " +
            "not on its list of allowed apps. Windows will block the move or wipe before it starts.\n\n" +
            $"To allow it:\n{ControlledFolderAccess.Steps}\n\n" +
            "Yes = open Windows Security now (the queue is kept; click Apply again afterwards)\n" +
            "No = apply anyway\nCancel = do nothing",
            "Allow FreePartition in Windows Security", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Yes)
            OpenControlledFolderAccessSettings();
        return answer == MessageBoxResult.No;
    }

    private void ShowControlledFolderAccessHelp(string reason)
    {
        var answer = MessageBox.Show(this,
            $"{reason}\n\nTo allow FreePartition:\n{ControlledFolderAccess.Steps}\n\n" +
            "Open Windows Security now? Afterwards, queue the operation again.",
            "Allow FreePartition in Windows Security", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes)
            OpenControlledFolderAccessSettings();
    }

    /// <summary>Operations that write sectors directly, which Controlled folder access blocks.</summary>
    private static bool WritesRawSectors(DiskOperation operation) => operation is MovePartition or WipePartition or WipeDisk or RestorePartition or ClonePartition or ConvertPartitionStyle;

    private void OpenControlledFolderAccessSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ControlledFolderAccess.SettingsUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open Windows Security: {ex.Message}\nOpen it from the Start menu and follow the steps.",
                "Windows Security", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool _operationsRunning;

    /// <summary>Closing the app while the queue runs could leave a disk half-changed (a move cannot be canceled).</summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_operationsRunning) return;

        MessageBox.Show(this, "Disk operations are running. Wait until they finish (or cancel them) before closing FreePartition.",
            "Operations in progress", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Cancel = true;
    }

    private void ToggleOperations(bool running)
    {
        _operationsRunning = running;
        BtnApplyOperations.IsEnabled = !running;
        BtnUndo.IsEnabled = !running;
        BtnClearQueue.IsEnabled = !running;
        BtnCancelOperations.IsEnabled = running;
    }

    // ------------------------------------------------------------------ Menus and tools

    private void BtnRecoverFiles_Click(object sender, RoutedEventArgs e)
    {
        var window = new RecoveryWindow(Selected?.Letter) { Owner = this };
        window.Show();
    }

    private void OpRecoverPartitions_Click(object sender, RoutedEventArgs e)
    {
        if (_disks.Count == 0) return;
        new LostPartitionsWindow(_disks, Selected?.DiskIndex ?? _disks[0].Index, TryQueue) { Owner = this }.Show();
    }

    private void MenuShowLog_Changed(object sender, RoutedEventArgs e) =>
        TxtOperationsLog.Visibility = MenuShowLog.IsChecked ? Visibility.Visible : Visibility.Collapsed;

    private void MenuOpenLog_Click(object sender, RoutedEventArgs e)
    {
        string? folder = Path.GetDirectoryName(FileLog.DefaultPath);
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void BtnDiskHealth_Click(object sender, RoutedEventArgs e) =>
        new HealthWindow { Owner = this }.ShowDialog();

    private void MenuAbout_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();
}
