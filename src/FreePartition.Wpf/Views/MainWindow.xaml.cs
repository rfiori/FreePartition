using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FreePartition.Core.Cloning;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;
using FreePartition.Core.Updates;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>Row of the partition list (a partition/volume or an unallocated stretch).</summary>
public sealed record PartitionRow(
    string Disk, string Name, string FileSystem, string Capacity, string Used,
    string Unused, string State, string Type,
    char? Letter, string? Label, ulong Size, ulong UsedBytes, bool Unallocated, int DiskIndex,
    int PartitionNumber = 0, bool Gpt = false, bool Uninitialized = false, ulong Offset = 0,
    IReadOnlyList<string>? Details = null, bool Reserved = false,
    ulong? VolumeSize = null,   // capacity of the volume when its usage is known (with or without a drive letter)
    string? VolumePath = null,   // GUID path of a volume without a drive letter (what a scan opens it by)
    bool Unmounted = false);   // usage read straight from the disk: Windows has no volume to act on

/// <summary>A disk with its rows, for the graphical map. <c>Map</c> also holds the reserved (MSR) stretches the list leaves out.</summary>
public sealed record DiskVisual(int Index, string Style, ulong Size, IReadOnlyList<PartitionRow> Rows, IReadOnlyList<PartitionRow> Map,
    bool Offline = false, bool IsSystem = false);   // IsSystem: holds the Windows drive or the boot partition

/// <summary>Numbered row of the operation queue.</summary>
public sealed record QueueItem(int Order, DiskOperation Operation);

public partial class MainWindow : Window
{
    // Gaps smaller than this between partitions are alignment, not "unallocated".
    private const ulong UnallocatedThreshold = 8UL * 1024 * 1024;

    // File system column of a partition that holds no volume Windows can read.
    private const string UnknownFileSystem = "Unknown";

    // Used-space bar of the disk map (the border adds 2 to this height).
    private const double UsageBarHeight = 15;
    private const double MapBoxHeight = 51;

    /// <summary>Text with a smooth white outline: the glyph outline is drawn as a vector (white stroke under black fill), so
    /// it stays anti-aliased and readable on any background.</summary>
    private static FrameworkElement OutlinedText(string text)
    {
        // Dark text with a soft white glow instead of a hard stroke: smooth edges, still readable on the light track and on the fill.
        return new TextBlock
        {
            Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, ShadowDepth = 2, BlurRadius = 1, Opacity = 1
            }
        };
    }
    private static readonly Brush OfflineText = Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)));   // the red of REAL MODE
    private static readonly Brush TakeOfflineBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x1A, 0x1A)));   // dark red
    private static readonly Brush UsageTrack = Freeze(Gradient(0xFA, 0xFA, 0xFA, 0xDC, 0xDC, 0xDC));
    private static readonly Brush UsageTrackBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0x9C)));

    // Used-space fill by kind of area, like the legend under the map.
    private static readonly Brush PrimaryFill = Freeze(Gradient(0x8C, 0x90, 0xE0, 0x3B, 0x3F, 0x9E));
    private static readonly Brush OtherFill = Freeze(Gradient(0xA6, 0xE8, 0x84, 0x48, 0xA8, 0x36));
    private static readonly Brush UnallocatedFill = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xF2, 0xF2, 0xF2), 0), new(Color.FromRgb(0xF2, 0xF2, 0xF2), 0.5),
            new(Color.FromRgb(0xD0, 0xD0, 0xD0), 0.5), new(Color.FromRgb(0xD0, 0xD0, 0xD0), 1)
        }, new Point(0, 0), new Point(5, 5))
    {
        MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat
    });

    // Frame of a disk: blue, or green for the disk of the selected partition.
    private static readonly Brush DiskGroupBackground = Freeze(Gradient(0xF1, 0xF5, 0xFC, 0xCF, 0xDC, 0xF0));
    private static readonly Brush DiskGroupBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x95, 0xAC, 0xD2)));
    private static readonly Brush SelectedDiskBackground = Freeze(Gradient(0xEC, 0xF8, 0xE2, 0xB4, 0xE0, 0x92));
    private static readonly Brush SelectedDiskBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x7B, 0xB6, 0x57)));

    private static readonly Brush BoxBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x8F, 0xA7, 0xCC)));
    private static readonly Brush NormalBox = Brushes.White;
    private static readonly Brush SelectedBox = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xC4)));
    private static readonly Brush SelectedBoxBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x2E)));

    private readonly OperationQueue _queue;
    private readonly char? _systemLetter;
    private readonly Dictionary<PartitionRow, Border> _boxes = new();
    private readonly Dictionary<int, Border> _diskGroups = new();
    private CancellationTokenSource? _operationsCts;
    private IReadOnlyList<DiskInfo> _disks = [];
    private readonly bool _isWinPe;
    private readonly string _currentVersion;
    private readonly UpdateSettings _updateSettings = UpdateSettings.Load(UpdateSettings.DefaultPath);
    private bool _updateCheckRunning;

    public MainWindow()
    {
        InitializeComponent();
        WindowFit.Apply(this, maximizeWhenShrunk: true);   // small screens (WinPE): fit and maximize

        // Windows drive (e.g. 'C'): formatting it or changing its letter is blocked.
        _systemLetter = Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? char.ToUpperInvariant(root[0]) : null;
        _queue = new OperationQueue(_systemLetter, new FileLog(FileLog.DefaultPath));
        // A running Windows cannot be copied consistently: cloning runs from WinPE, elsewhere it explains how to get there.
        _isWinPe = WinPeDetector.IsWinPe();
        _queue.Changed += (_, _) => RefreshPending();
        Loaded += (_, _) => DisksGrid.ContextMenu = BuildContextMenu();

        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString()
                         ?? "unknown";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        TxtVersion.Text = $"v{version}";
        _currentVersion = version;
        MenuUpdateOnStartup.IsChecked = _updateSettings.CheckOnStartup;
        BuildShareButtons();
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

        // WinPE runs from a RAM disk (nothing would survive a restart) and usually has no network; debug builds are the dev's own.
#if !DEBUG
        if (_updateSettings.CheckOnStartup && !_isWinPe) _ = CheckForUpdateAsync(manual: false);
#endif
        await LoadDisksAsync();
    }

    // ------------------------------------------------------------------ Updates

    /// <summary>Why an update cannot start now: it restarts the app, so running or queued operations would be lost.</summary>
    private string? UpdateBlockReason() =>
        _operationsRunning ? "Disk operations are running. Wait until they finish, then update."
        : _queue.Pending.Count > 0 ? "There are pending operations and they would be lost when FreePartition restarts. Apply or undo them, then update."
        : null;

    /// <summary>Asks GitHub for the latest release. The startup check is silent unless there is something to offer.</summary>
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateCheckRunning) return;
        if (_isWinPe)
        {
            if (manual) MessageBox.Show(this, "Updates are not available in WinPE.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _updateCheckRunning = true;
        SetBusy(true);
        try
        {
            using var http = UpdateService.CreateHttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            bool dependent = UpdateService.IsFrameworkDependent(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            var release = await new UpdateService(http).CheckAsync(_currentVersion, dependent);

            if (release is null)
            {
                if (manual) MessageBox.Show(this, $"You have the latest version (v{_currentVersion}).", "Check for updates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // The startup check respects "Skip this version" and does not interrupt running operations.
            if (!manual && (release.Tag == _updateSettings.SkippedVersion || _operationsRunning)) return;

            new UpdateWindow(release, _currentVersion, _updateSettings, UpdateBlockReason) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            if (manual) MessageBox.Show(this, $"Could not check for updates.\n\n{ex.Message}", "Check for updates",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            _updateCheckRunning = false;
        }
    }

    private async void MenuCheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(manual: true);

    private void MenuUpdateOnStartup_Click(object sender, RoutedEventArgs e)
    {
        _updateSettings.CheckOnStartup = MenuUpdateOnStartup.IsChecked;
        if (!_updateSettings.Save(UpdateSettings.DefaultPath))
            MessageBox.Show(this, "Could not save the setting. It applies only until you close FreePartition.", "Updates",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
                // No volume Windows can read (unformatted, ext4, locked BitLocker...): say so instead of looking empty.
                rows.Add(new PartitionRow(title, $"Partition {partition.Index}", UnknownFileSystem, ByteFormatter.Format(partition.Size),
                    "Unknown", "Unknown", state, type, null, null, partition.Size, 0, false, disk.Index, partition.Number, gpt,
                    Offset: partition.Offset, Details: partition.Details));
            }

            foreach (var volume in partition.Volumes)
            {
                char? letter = volume.Letter.Length > 0 && char.IsLetter(volume.Letter[0]) ? char.ToUpperInvariant(volume.Letter[0]) : null;
                string name = letter is null ? $"Partition {partition.Index}"
                    : string.IsNullOrEmpty(volume.Label) ? $"{letter}:" : $"{letter}: {volume.Label}";
                if (letter is null && !string.IsNullOrEmpty(volume.Label))
                    name += $" {volume.Label}";
                ulong used = volume.Size > volume.Free ? volume.Size - volume.Free : 0;

                rows.Add(new PartitionRow(title, name, volume.FileSystem ?? "",
                    ByteFormatter.Format(volume.Size), WithPercent(used, volume.Size),
                    WithPercent(volume.Free, volume.Size),
                    state,
                    type, letter, volume.Label, partition.Size, used, false, disk.Index, partition.Number, gpt,
                    Offset: partition.Offset, Details: partition.Details,
                    VolumeSize: letter is not null || volume.FileSystem is not null ? volume.Size : null,
                    VolumePath: letter is null ? volume.VolumePath : null,
                    Unmounted: volume.Unmounted));
            }

            cursor = Math.Max(cursor, partition.Offset + partition.Size);
        }

        if (disk.Size > cursor)
            AddFree(cursor, disk.Size, disk.PartitionStyle == "Uninitialized");

        var map = rows.Concat(reserved).OrderBy(r => r.Offset).ToList();
        // The disk Windows runs or boots from: it is never offered to be taken offline.
        bool isSystem = rows.Any(r => (r.Letter is { } l && l == _systemLetter) || r.Details?.Contains("Boot") == true);
        return new DiskVisual(disk.Index, disk.PartitionStyle, disk.Size, rows, map, disk.IsOffline, isSystem);
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
        _diskGroups.Clear();

        foreach (var disk in disks)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Disk name, style and size straight on the frame, like Disk Management.
            var label = new StackPanel { Margin = new Thickness(4, 2, 6, 2) };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
                Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = $"Disk {disk.Index}", FontWeight = FontWeights.Bold, Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            label.Children.Add(header);
            label.Children.Add(new TextBlock { Text = $"Basic {disk.Style}", FontSize = 11 });
            label.Children.Add(new TextBlock { Text = ByteFormatter.Format(disk.Size), FontSize = 11 });
            if (disk.Offline)
                label.Children.Add(OfflineBadge(disk.Index));
            else if (!disk.IsSystem)
                label.Children.Add(TakeOfflineButton(disk));
            Grid.SetColumn(label, 0);
            line.Children.Add(label);

            var boxes = new Grid();
            double total = disk.Map.Sum(r => (double)r.Size);
            int column = 0;
            foreach (var part in disk.Map)
            {
                boxes.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max((double)part.Size, total * 0.06), GridUnitType.Star),
                    MinWidth = 48
                });
                var box = CreateBox(part);
                Grid.SetColumn(box, column++);
                boxes.Children.Add(box);
                _boxes[part] = box;
            }
            Grid.SetColumn(boxes, 1);
            line.Children.Add(boxes);

            // One frame around the whole disk: its label and all its partitions.
            var group = new Border
            {
                Background = DiskGroupBackground, BorderBrush = DiskGroupBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 3, 0, 3), Margin = new Thickness(0, 0, 0, 6), Child = line
            };
            _diskGroups[disk.Index] = group;
            DiskMap.Children.Add(group);
        }

        DiskMap.Children.Add(BuildLegend());
        HighlightSelection();
    }

    private static LinearGradientBrush Gradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2) =>
        new(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), 90);

    /// <summary>"Primary / Other / Unallocated" key for the used-space colors of the map.</summary>
    private static StackPanel BuildLegend()
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 4) };
        foreach (var (text, brush) in new[]
                 {
                     ("Primary", PrimaryFill), ("EFI / Other", OtherFill), ("Unallocated", UnallocatedFill)
                 })
        {
            legend.Children.Add(new Border
            {
                Width = 14, Height = 12, Background = brush, BorderBrush = UsageTrackBorder, BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            });
            legend.Children.Add(new TextBlock
            {
                Text = text, FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(4, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return legend;
    }

    /// <summary>"Offline" in bold under the disk size, with a button that queues bringing the disk online.</summary>
    private StackPanel OfflineBadge(int diskIndex)
    {
        var badge = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        badge.Children.Add(new TextBlock
        {
            Text = "Offline", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = OfflineText
        });
        badge.Children.Add(DiskStateButton("Bring online", null,
            $"Queue \"Bring disk {diskIndex} online\" (click Apply to run it). Windows then mounts its volumes again.",
            () => _queue.Pending.OfType<SetDiskOnline>().Any(o => o.DiskNumber == diskIndex),
            () => new SetDiskOnline(diskIndex)));
        return badge;
    }

    /// <summary>
    /// Dark red "Take offline" button under the disk size. Not offered for the disk that holds Windows; the queue
    /// and the script refuse that disk too.
    /// </summary>
    private Button TakeOfflineButton(DiskVisual disk)
    {
        var drives = disk.Rows.Select(r => r.Letter).OfType<char>().Distinct().ToArray();
        return DiskStateButton("Take offline", TakeOfflineBackground,
            $"Queue \"Take disk {disk.Index} offline\" (click Apply to run it). Windows unmounts its volumes until it is brought online again.",
            () => _queue.Pending.OfType<SetDiskOffline>().Any(o => o.DiskNumber == disk.Index),
            () => new SetDiskOffline(disk.Index, drives));
    }

    /// <summary>Rounded power button of the disk map that queues an operation (once) on the disk.</summary>
    private Button DiskStateButton(string text, Brush? background, string tooltip, Func<bool> alreadyQueued, Func<DiskOperation> create)
    {
        var caption = new StackPanel { Orientation = Orientation.Horizontal };
        caption.Children.Add(new TextBlock
        {
            Text = "\uE7E8", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
        });
        caption.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = caption, Style = (Style)FindResource("MapActionButton"), Margin = new Thickness(0, 5, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Center, ToolTip = tooltip
        };
        if (background is not null) button.Background = background;
        button.Click += (_, _) =>
        {
            if (alreadyQueued()) return;
            try
            {
                _queue.Add(create());
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        return button;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private Border CreateBox(PartitionRow part)
    {
        bool empty = part.Unallocated || part.Reserved;
        string? usageTip = null;
        var content = new StackPanel { Margin = new Thickness(3) };

        // Used-space bar on top: colored by kind of partition, hatched for space that holds nothing.
        var bar = new Grid();
        if (!empty)
        {
            // Without a readable volume there is no usage to show, so the bar stays empty instead of claiming 0%.
            bool hasUsage = part.VolumeSize is not null;
            // Same base as the Used column: the volume's capacity, not the partition's size.
            ulong capacity = part.VolumeSize ?? part.Size;
            double fraction = !hasUsage || capacity == 0 ? 0 : Math.Clamp((double)part.UsedBytes / capacity, 0, 1);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });
            if (fraction > 0)
                bar.Children.Add(new Border { Background = part.Type == "Primary" ? PrimaryFill : OtherFill });
            if (hasUsage)
            {
                string percent = fraction.ToString("P0", CultureInfo.CurrentCulture);
                usageTip = $"Used: {ByteFormatter.Format(part.UsedBytes)} ({percent})";
                // Percentage in the middle of the bar: dark with a white outline, readable on the light track and on the fill.
                var label = OutlinedText(percent);
                Grid.SetColumnSpan(label, 2);
                bar.Children.Add(label);
            }
        }
        content.Children.Add(new Border
        {
            Height = UsageBarHeight, CornerRadius = new CornerRadius(2), Background = empty ? UnallocatedFill : UsageTrack,
            BorderBrush = UsageTrackBorder, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 3), Child = bar
        });

        // "C: System" in bold, then "200,08 GB NTFS".
        string title = part.Reserved ? "Reserved (MSR)" : part.Unallocated ? "*" : part.Name;
        string detail = part.Unallocated ? $"{part.Capacity} Unallocated"
            : string.IsNullOrEmpty(part.FileSystem) ? part.Capacity : $"{part.Capacity} {part.FileSystem}";
        content.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.Bold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 0)
        });
        title = part.Unallocated ? "Unallocated" : string.IsNullOrEmpty(part.FileSystem) ? title : $"{title} ({part.FileSystem})";
        title += $"\n{part.Capacity}";

        string tooltip = usageTip is null ? title : $"{title}\n{usageTip}";
        if (!empty && part.PartitionNumber > 0
            && PartitionAlignment.Classify(part.Offset) == AlignmentStatus.Misaligned)
        {
            content.Children.Add(new TextBlock
            {
                Text = "⚠ Not aligned", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Foreground = Brushes.DarkOrange
            });
            tooltip += $"\n{PartitionAlignment.Describe(AlignmentStatus.Misaligned)}";
        }

        var box = new Border
        {
            Background = NormalBox, BorderBrush = BoxBorder, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 3, 0), Child = content, ToolTip = tooltip,
            MinHeight = MapBoxHeight, CornerRadius = new CornerRadius(3)
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
        HighlightSelection();
        RefreshAvailableOperations();
    }

    /// <summary>Marks the selected partition's box and the frame of its disk on the map.</summary>
    private void HighlightSelection()
    {
        foreach (var (row, box) in _boxes)
        {
            bool selected = ReferenceEquals(row, DisksGrid.SelectedItem);
            box.Background = selected ? SelectedBox : NormalBox;
            box.BorderBrush = selected ? SelectedBoxBorder : BoxBorder;
        }

        // The disk that holds the selected partition turns green, like the reference layout.
        int? selectedDisk = (DisksGrid.SelectedItem as PartitionRow)?.DiskIndex;
        foreach (var (index, group) in _diskGroups)
        {
            bool selected = index == selectedDisk;
            group.Background = selected ? SelectedDiskBackground : DiskGroupBackground;
            group.BorderBrush = selected ? SelectedDiskBorder : DiskGroupBorder;
        }
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
        // Format and Delete also work on a partition without a letter (addressed by disk and number); Label and
        // Check need a volume Windows can read there.
        bool hasPartition = hasLetter || Selected is { Unallocated: false, PartitionNumber: > 0 };
        bool hasVolume = hasLetter || Selected is { Unallocated: false, PartitionNumber: > 0, VolumeSize: not null, Unmounted: false };
        OpResize.IsEnabled = hasLetter;
        OpMerge.IsEnabled = hasLetter && PreviousNeighbor() is not null;
        OpSplit.IsEnabled = hasLetter;
        OpClone.IsEnabled = Selected is { Unallocated: false };   // always clickable: it explains why a partition cannot be cloned
        OpAlign.IsEnabled = Selected is { Unallocated: false, PartitionNumber: > 0 } aligned
                            && PartitionAlignment.Classify(aligned.Offset) == AlignmentStatus.Misaligned;
        OpLabel.IsEnabled = hasVolume;
        OpLetter.IsEnabled = hasLetter;
        OpFormat.IsEnabled = hasPartition;
        OpConvert.IsEnabled = hasLetter && Selected is { FileSystem: "FAT32" or "NTFS" };
        OpConvertStyle.IsEnabled = Selected is not null;   // any row of the disk; it explains why a disk cannot be converted
        OpDelete.IsEnabled = hasPartition;
        OpWipe.IsEnabled = hasLetter && Selected is { PartitionNumber: > 0 };
        OpWipeFree.IsEnabled = hasLetter;
        OpWipeDisk.IsEnabled = Selected is not null;   // any row of the disk; it explains why a disk cannot be wiped
        OpCreate.IsEnabled = Selected is { Unallocated: true, Uninitialized: false };
        OpInitialize.IsEnabled = Selected is { Unallocated: true, Uninitialized: true };
        OpCheck.IsEnabled = hasVolume;
        OpActive.IsEnabled = Selected is { Unallocated: false, Letter: not null, Gpt: false, PartitionNumber: > 0 };
        OpHide.IsEnabled = hasLetter;
        OpShow.IsEnabled = Selected is { Unallocated: false, Letter: null, PartitionNumber: > 0 };
        OpExplore.IsEnabled = hasLetter;
        OpRecover.IsEnabled = hasLetter || Selected is { Unallocated: false, VolumePath: not null } unlettered
                              && RecoveryWindow.CanScan(unlettered.FileSystem);
        OpRecoverPartitions.IsEnabled = _disks.Count > 0;   // the window lets you pick the disk
        OpProperties.IsEnabled = Selected is { Unallocated: false };
        OpVerifyWipe.IsEnabled = hasLetter;
        OpClusterMap.IsEnabled = hasLetter;
    }

    /// <summary>
    /// Resize/Move partition: one dialog that shrinks, extends and moves the partition. Windows only resizes a
    /// partition at its end and the move keeps the size, so a change of both is queued as two operations, in the
    /// order that copies less and always has room: shrink then move, or move then extend.
    /// </summary>
    private async void OpResizeMove_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Resize/Move partition";
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

        long start = (long)row.Offset, size = (long)row.Size;
        long regionStart = start, regionEnd = start + (long)max;

        // A partition that may move slides within the free space around it, inside the disk's usable area.
        string? moveBlocked = MoveBlockedReason(row);
        if (moveBlocked is null)
        {
            var (layout, error) = await LoadLayoutAsync(row.DiskIndex);
            if (layout is null)
            {
                moveBlocked = $"{error} It can still be resized at its end.";
            }
            else
            {
                var home = FreeRegionAround(row);
                regionStart = Math.Max(home.Start, layout.UsableStart);
                regionEnd = Math.Max(Math.Min(home.End, layout.UsableEnd), start + size);
            }
        }
        else
        {
            moveBlocked += " It can still be resized at its end.";
        }

        var dialog = new ResizeMoveDialog(letter, start, size, regionStart, regionEnd, (long)min, (long)row.UsedBytes, moveBlocked)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Moved && !ConfirmMoveBootRoles(row, letter)) return;

        long newStart = dialog.NewStart, newSize = dialog.NewSize;
        var operations = new List<DiskOperation>();
        if (!dialog.Moved)
        {
            operations.Add(new ResizePartition(letter, dialog.ExtendsToMaximum ? null : newSize));
        }
        else if (newSize <= size)
        {
            // Shrink first (Windows moves the files out of the end), then move the smaller partition.
            if (newSize < size) operations.Add(new ResizePartition(letter, newSize));
            operations.Add(new MovePartition(row.DiskIndex, row.PartitionNumber, letter, start, newSize, newStart));
        }
        else
        {
            // Move first, then grow into the free space left behind; the extend waits for Windows to remount the drive.
            operations.Add(new MovePartition(row.DiskIndex, row.PartitionNumber, letter, start, size, newStart));
            operations.Add(new ResizePartition(letter, newSize, WaitForDrive: true));
        }

        // All or nothing: a half-queued change would leave the partition somewhere the user did not ask for.
        var added = new List<DiskOperation>();
        try
        {
            foreach (var operation in operations)
            {
                _queue.Add(operation);
                added.Add(operation);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            foreach (var operation in added) _queue.Remove(operation);
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// The free space around a partition, counting its own space as free (it is vacated while it moves). Two free
    /// rows only merge when nothing sits between them.
    /// </summary>
    private (long Start, long End) FreeRegionAround(PartitionRow row)
    {
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
        long current = (long)row.Offset;
        return regions.FirstOrDefault(r => r.Start <= current && current < r.End);
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

    /// <summary>Why a partition can never be moved (no letter, the Windows drive...), or null when it may move.</summary>
    private string? MoveBlockedReason(PartitionRow row)
    {
        if (row.Letter is not { } letter)
            return "This partition has no drive letter, so it cannot be moved. Use \"Show partition\" to give it a letter first.";
        if (letter == _systemLetter)
            return $"{letter}: is the Windows drive. Moving it could make Windows impossible to start, so it is not allowed.";
        if (row.PartitionNumber < 1)
            return $"Windows does not expose {letter}: as a partition that can be moved.";
        return null;
    }

    /// <summary>
    /// Boot/active partitions (another Windows install, a boot manager) read their own position from the boot
    /// sector, which a move does not rewrite: moving them can leave that system unable to start. Asks first.
    /// </summary>
    private bool ConfirmMoveBootRoles(PartitionRow row, char letter)
    {
        var bootRoles = row.Details?.Where(d => d is "Boot" or "Active" or "Page File" or "Crash Dump").ToList();
        if (bootRoles is not { Count: > 0 }) return true;

        var answer = MessageBox.Show(this,
            $"{letter}: is marked as {string.Join(", ", bootRoles)}.\n\n" +
            "This partition is used to start or run Windows. Moving it does not update its boot sector, so the " +
            "system that boots from it may fail to start, and a drive in use cannot be locked anyway.\n\n" +
            "Only continue if you are sure this is just a data partition. Move it anyway?",
            "Move partition", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Explains up front why a partition cannot be moved, instead of failing later while applying.
    /// Returns its drive letter when the move may go ahead.
    /// </summary>
    private char? CheckCanMove(PartitionRow row)
    {
        if (MoveBlockedReason(row) is { } reason)
        {
            ShowMoveRefused(reason);
            return null;
        }
        char letter = row.Letter!.Value;
        return ConfirmMoveBootRoles(row, letter) ? letter : null;
    }

    /// <summary>Reads the partition table (read-only); on failure, says why the disk cannot be rearranged.</summary>
    private async Task<(DriveLayout? Layout, string? Error)> LoadLayoutAsync(int diskIndex)
    {
        Cursor = Cursors.Wait;
        SetBusy(true);
        try
        {
            return (await Task.Run(() => PhysicalDiskMover.LoadLayout(diskIndex)), null);
        }
        catch (InvalidDataException ex)
        {
            return (null, $"Disk {diskIndex} cannot be rearranged: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"Could not read the partition table of disk {diskIndex}: {ex.Message}");
        }
        finally
        {
            Cursor = null;
            SetBusy(false);
        }
    }

    /// <summary>Reads the partition table (read-only), or explains why the disk cannot be rearranged.</summary>
    private async Task<DriveLayout?> LoadLayoutForMoveAsync(int diskIndex)
    {
        var (layout, error) = await LoadLayoutAsync(diskIndex);
        if (error is not null) ShowMoveRefused(error);
        return layout;
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
        AddPartitionOperation("Change label",
            [new DialogField("New label:", Selected?.Label ?? "")],
            (letter, target, v) => new ChangeVolumeLabel(letter, v[0], target),
            needsVolume: true);

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
        AddPartitionOperation("Format partition",
            [new DialogField("File system:", "NTFS", Enum.GetNames<FileSystemFormat>()),
             new DialogField("Label:", Selected?.Label ?? "")],
            (letter, target, v) => new FormatVolume(letter, ConvertFileSystem(v[0]), v[1], target),
            "Formatting ERASES all data on the drive.", erases: true);

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
        if (Selected is not { Unallocated: true, Uninitialized: false } free || RefuseOffline(free.DiskIndex, "Create partition")) return;

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
        AddPartitionOperation("Delete partition", [],
            (letter, target, _) => new DeletePartition(letter, target),
            "Deleting ERASES all data on the drive.", erases: true);

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
        AddPartitionOperation("Check for errors",
            [new DialogField("Mode:", ScanOnly, [ScanOnly, ScanAndRepair])],
            (letter, target, v) => new CheckVolume(letter, Repair: v[0] == ScanAndRepair, Partition: target),
            "Repairing takes the volume offline and is not allowed on the Windows drive.",
            needsVolume: true);

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
        if (Selected is not { Unallocated: false, Letter: null, PartitionNumber: > 0 } row || RefuseOffline(row.DiskIndex, "Show partition")) return;

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

    /// <summary>
    /// Windows refuses to change an offline disk (Add-PartitionAccessPath, Format-Volume… fail with "Not Supported"),
    /// so explain it before queueing an operation that would only fail when applied. True when it was refused.
    /// </summary>
    private bool RefuseOffline(int diskIndex, string title)
    {
        if (_disks.FirstOrDefault(d => d.Index == diskIndex) is not { IsOffline: true }) return false;
        MessageBox.Show(this,
            $"Disk {diskIndex} is offline, so Windows does not allow changes to it.\n\n" +
            "Bring it online first (Disk Management: right-click the disk → Online, or in PowerShell as administrator: " +
            $"Set-Disk -Number {diskIndex} -IsOffline $false), then click Refresh.",
            title, MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>
    /// Why a partition without a drive letter must not be erased, or null when it may be. The letter-based checks
    /// (Windows drive) cannot see these, so the Status roles of the row are used; the generated script also refuses
    /// a partition that is boot or system.
    /// </summary>
    private static string? ProtectedReason(PartitionRow row)
    {
        var role = row.Details?.FirstOrDefault(d =>
            d is "Boot" or "Page File" or "Crash Dump" or "Active" or "EFI System Partition" or "Reserved Partition");
        return role is null ? null : $"This partition is marked \"{role}\" (it holds Windows or boot files), so it cannot be changed here.";
    }

    /// <summary>
    /// Like <see cref="AddWithDialog"/> for operations that also work on a partition without a drive letter, which
    /// is then addressed by disk number, partition number and offset. <paramref name="needsVolume"/>: the operation
    /// needs a file system Windows can read; <paramref name="erases"/>: refuse the partitions that hold the boot files.
    /// </summary>
    private void AddPartitionOperation(string title, IReadOnlyList<DialogField> fields,
        Func<char, PartitionTarget?, string[], DiskOperation> create, string? warning = null,
        bool needsVolume = false, bool erases = false)
    {
        if (Selected is not { Unallocated: false } row || RefuseOffline(row.DiskIndex, title)) return;

        PartitionTarget? target = null;
        string subject;
        if (row.Letter is { } letter)
        {
            subject = $"Drive {letter}:";
        }
        else
        {
            if (row.PartitionNumber < 1) return;
            if (needsVolume && row.Unmounted)
            {
                MessageBox.Show(this, "Windows has not mounted this partition (the disk is offline or automount is off), so this does not apply to it.",
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (needsVolume && row.VolumeSize is null)
            {
                MessageBox.Show(this, "Windows cannot read a file system on this partition, so this does not apply to it.",
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (erases && ProtectedReason(row) is { } reason)
            {
                MessageBox.Show(this, reason, title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            target = new PartitionTarget(row.DiskIndex, row.PartitionNumber, (long)row.Offset);
            subject = $"Disk {row.DiskIndex}, partition {row.PartitionNumber} ({row.Capacity}" +
                      (string.IsNullOrEmpty(row.FileSystem) || row.FileSystem == UnknownFileSystem ? ")" : $", {row.FileSystem})");
        }

        var dialog = new ParametersDialog(title, warning is null ? subject : $"{subject} — {warning}", fields) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _queue.Add(create(row.Letter ?? DiskOperation.NoLetter, target, dialog.Values));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
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

    /// <summary>Trash can of a pending operation: removes only that one from the queue.</summary>
    private void BtnRemovePending_Click(object sender, RoutedEventArgs e)
    {
        if (_operationsRunning || (sender as FrameworkElement)?.DataContext is not QueueItem item) return;

        // Later operations may have been planned on top of this one (e.g. formatting a partition it creates).
        var pending = _queue.Pending;
        if (!ReferenceEquals(item.Operation, pending[^1]))
        {
            var answer = MessageBox.Show(this,
                $"Remove \"{item.Operation.Description}\" from the queue?\n\n"
                + "Operations queued after it may depend on it and fail when applied.",
                "Remove operation", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        _queue.Remove(item.Operation);
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
        var initial = Selected switch
        {
            { Letter: { } letter } => new RecoverySource(letter.ToString(), $"{letter}:", null),
            { Unallocated: false, VolumePath: { } path } row when RecoveryWindow.CanScan(row.FileSystem) =>
                RecoverySource.ForPartition(path, row.DiskIndex, row.PartitionNumber, row.Label, row.FileSystem, row.Capacity),
            _ => null
        };
        var window = new RecoveryWindow(initial) { Owner = this };
        window.Show();
    }

    private void OpRecoverPartitions_Click(object sender, RoutedEventArgs e)
    {
        if (_disks.Count == 0) return;
        new LostPartitionsWindow(_disks, Selected?.DiskIndex ?? _disks[0].Index, TryQueue) { Owner = this }.Show();
    }

    private void MenuShowLog_Changed(object sender, RoutedEventArgs e)
    {
        bool show = MenuShowLog.IsChecked;
        LogPanel.Visibility = LogSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // The log lives in its own row (resizable with the splitter); collapse the rows when it is hidden.
        LogSplitterRow.Height = new GridLength(show ? 5 : 0);
        LogRow.MinHeight = show ? 40 : 0;
        LogRow.Height = new GridLength(show ? 130 : 0);
    }

    /// <summary>The "X" of the log panel: same as unchecking View > Show operations log.</summary>
    private void BtnCloseLog_Click(object sender, RoutedEventArgs e) => MenuShowLog.IsChecked = false;

    /// <summary>Ctrl + mouse wheel zooms the log text (8 to 24 pt).</summary>
    private void TxtOperationsLog_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        TxtOperationsLog.FontSize = Math.Clamp(TxtOperationsLog.FontSize + (e.Delta > 0 ? 1 : -1), 8, 24);
        e.Handled = true;
    }

    private void MenuOpenLog_Click(object sender, RoutedEventArgs e)
    {
        string? folder = Path.GetDirectoryName(FileLog.DefaultPath);
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void MenuCloneWindows_Click(object sender, RoutedEventArgs e)
    {
        const string title = "Clone Windows to another disk";
        if (!_isWinPe)
        {
            new WinPeGuideWindow { Owner = this }.ShowDialog();
            return;
        }
        if (_disks.Count == 0) return;
        if (_queue.Pending.Count > 0)
        {
            MessageBox.Show(this, "There are pending operations. Apply or undo them first: cloning Windows works on the disks as they are now.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var installs = WindowsInstallFinder.Find(_disks, _systemLetter);
        if (installs.Count == 0)
        {
            MessageBox.Show(this, "No installed Windows was found. A Windows partition needs a drive letter to be found: " +
                "use \"Show partition\" to give it one, then refresh the disks.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_disks.Count < 2)
        {
            MessageBox.Show(this, "There is only one disk. Connect the destination disk, then refresh the disks.", title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new CloneWindowsWindow(_disks, installs, () => _ = LoadDisksAsync()) { Owner = this }.ShowDialog();
    }

    private void BtnDiskHealth_Click(object sender, RoutedEventArgs e) =>
        new HealthWindow { Owner = this }.ShowDialog();

    private void MenuAbout_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();
}
