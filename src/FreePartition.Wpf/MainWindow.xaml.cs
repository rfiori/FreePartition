using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    int PartitionNumber = 0, bool Gpt = false);

/// <summary>A disk with its rows, for the graphical map.</summary>
public sealed record DiskVisual(int Index, string Style, ulong Size, IReadOnlyList<PartitionRow> Rows);

/// <summary>Numbered row of the operation queue.</summary>
public sealed record QueueItem(int Order, DiskOperation Operation);

public partial class MainWindow : Window
{
    // Gaps smaller than this between partitions are alignment, not "unallocated".
    private const ulong UnallocatedThreshold = 8UL * 1024 * 1024;

    private static readonly Brush NormalBox = Brushes.White;
    private static readonly Brush SelectedBox = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xB8));

    private readonly OperationQueue _queue;
    private readonly char? _systemLetter;
    private readonly Dictionary<PartitionRow, Border> _boxes = new();
    private CancellationTokenSource? _operationsCts;

    public MainWindow()
    {
        InitializeComponent();

        // Windows drive (e.g. 'C'): formatting it or changing its letter is blocked.
        _systemLetter = Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? char.ToUpperInvariant(root[0]) : null;
        _queue = new OperationQueue(_systemLetter, new FileLog(FileLog.DefaultPath));
        _queue.Changed += (_, _) => RefreshPending();
        Loaded += (_, _) => DisksGrid.ContextMenu = BuildContextMenu();
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
        try
        {
            BtnRefreshDisks.IsEnabled = false;
            TxtDiskStatus.Text = "Reading disks...";

            var disks = await Task.Run(() => DiskService.ListDisks());
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
        }
    }

    private DiskVisual BuildDisk(DiskInfo disk)
    {
        string title = $"Disk {disk.Index} ({disk.PartitionStyle}) — {disk.Model}";
        bool gpt = disk.PartitionStyle == "GPT";
        var rows = new List<PartitionRow>();
        ulong cursor = 0;

        foreach (var partition in disk.Partitions)
        {
            if (partition.Offset > cursor && partition.Offset - cursor >= UnallocatedThreshold)
                rows.Add(UnallocatedRow(title, disk.Index, partition.Offset - cursor));

            string type = TranslateType(partition.Type, gpt);
            string state = StatusText(disk.Status, partition.Details);

            if (partition.Volumes.Count == 0)
            {
                rows.Add(new PartitionRow(title, $"Partition {partition.Index}", "", ByteFormatter.Format(partition.Size),
                    "", "", state, type, null, null, partition.Size, 0, false, disk.Index, partition.Number, gpt));
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
                    type, letter, volume.Label, partition.Size, used, false, disk.Index, partition.Number, gpt));
            }

            cursor = Math.Max(cursor, partition.Offset + partition.Size);
        }

        if (disk.Size > cursor && disk.Size - cursor >= UnallocatedThreshold)
            rows.Add(UnallocatedRow(title, disk.Index, disk.Size - cursor));

        return new DiskVisual(disk.Index, disk.PartitionStyle, disk.Size, rows);
    }

    private static PartitionRow UnallocatedRow(string disk, int diskIndex, ulong size) =>
        new(disk, "*", "Unallocated", ByteFormatter.Format(size), "0 B", ByteFormatter.Format(size),
            "None", "", null, null, size, 0, true, diskIndex);

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
            double total = disk.Rows.Sum(r => (double)r.Size);
            int column = 0;
            foreach (var part in disk.Rows)
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

    private Border CreateBox(PartitionRow part)
    {
        var borderBrush = part.Unallocated ? Brushes.Silver : new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xD5));
        var content = new StackPanel { Margin = new Thickness(3) };

        if (!part.Unallocated)
        {
            double fraction = part.Size == 0 ? 0 : Math.Clamp((double)part.UsedBytes / part.Size, 0, 1);
            var bar = new Grid { Height = 10, Margin = new Thickness(0, 0, 0, 3) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });
            var filled = new Border { Background = new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xD5)) };
            Grid.SetColumn(filled, 0);
            bar.Children.Add(filled);
            content.Children.Add(new Border { BorderBrush = borderBrush, BorderThickness = new Thickness(1), Child = bar });
        }
        else
        {
            content.Children.Add(new Border { Height = 12 });
        }

        string title = part.Unallocated ? "Unallocated"
            : string.IsNullOrEmpty(part.FileSystem) ? part.Name : $"{part.Name} ({part.FileSystem})";
        content.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
        content.Children.Add(new TextBlock { Text = part.Capacity, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Brushes.DimGray });

        var box = new Border
        {
            Background = NormalBox, BorderBrush = borderBrush, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand, Child = content, ToolTip = title,
            ContextMenu = BuildContextMenu()
        };
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

    /// <summary>Right-click menu that mirrors the Operations panel (each item clicks the matching button).</summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var buttons = new[] { OpResize, OpLabel, OpLetter, OpFormat, OpCreate, OpDelete, OpActive, OpHide, OpShow, OpExplore, OpProperties };
        foreach (var button in buttons)
        {
            var texts = ((StackPanel)button.Content).Children.OfType<TextBlock>().ToList();
            var item = new MenuItem
            {
                Header = texts[1].Text,
                Icon = new TextBlock { Text = texts[0].Text, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = (Brush)FindResource("Accent") }
            };
            item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) =>
        {
            for (int i = 0; i < buttons.Length; i++)
                ((MenuItem)menu.Items[i]).IsEnabled = buttons[i].IsEnabled;
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
        OpLabel.IsEnabled = hasLetter;
        OpLetter.IsEnabled = hasLetter;
        OpFormat.IsEnabled = hasLetter;
        OpDelete.IsEnabled = hasLetter;
        OpCreate.IsEnabled = Selected is { Unallocated: true };
        OpActive.IsEnabled = Selected is { Unallocated: false, Letter: not null, Gpt: false, PartitionNumber: > 0 };
        OpHide.IsEnabled = hasLetter;
        OpShow.IsEnabled = Selected is { Unallocated: false, Letter: null, PartitionNumber: > 0 };
        OpExplore.IsEnabled = hasLetter;
        OpProperties.IsEnabled = Selected is { Unallocated: false };
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
        try
        {
            if (await SupportedSize.QueryAsync(letter, new PowerShellExecutor()) is { } limits)
                (min, max) = ((ulong)limits.MinBytes, Math.Max((ulong)limits.MaxBytes, row.Size));
        }
        finally
        {
            Cursor = null;
        }

        var dialog = new ResizeDialog(letter, row.Size, min, max) { Owner = this };
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

    private void OpCreate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Unallocated: true } free) return;

        var dialog = new ParametersDialog("Create partition",
            $"Disk {free.DiskIndex} — free space: {free.Capacity}",
            [new DialogField("Size (GB, empty = all free space):"),
             new DialogField("File system:", "NTFS", Enum.GetNames<FileSystemFormat>()),
             new DialogField("Label:"),
             LetterField("Drive letter:", AutomaticLetter)]) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var v = dialog.Values;
            long? size = ConvertGbToBytes(v[0]);
            if (size is { } bytes && (ulong)bytes > free.Size)
                throw new ArgumentException($"The size exceeds the free space ({free.Capacity}).");
            char? letter = v[3].Length == 1 ? char.ToUpperInvariant(v[3][0]) : null;   // "(automatic)" = no letter
            if (letter is not null && DisksGrid.Items.OfType<PartitionRow>().Any(r => r.Letter == letter))
                throw new ArgumentException($"Drive letter {letter}: is already in use.");

            _queue.Add(new CreatePartition(free.DiskIndex, ConvertFileSystem(v[1]), v[2], size, letter));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "Invalid operation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpDelete_Click(object sender, RoutedEventArgs e) =>
        AddWithDialog("Delete partition", [],
            (letter, _) => new DeletePartition(letter),
            "Deleting ERASES all data on the drive.");

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
        if (Selected is not { } p) return;
        MessageBox.Show(this,
            $"Partition: {p.Name}\nFile system: {p.FileSystem}\nCapacity: {p.Capacity}\n" +
            $"Used: {p.Used}\nUnused: {p.Unused}\nStatus: {p.State}\nType: {p.Type}\n\n{p.Disk}",
            "Properties", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static long? ConvertGbToBytes(string text)
    {
        if (text.Length == 0) return null;   // empty = extend to the maximum
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var gb) || gb <= 0)
            throw new FormatException("Invalid size. Use a number in GB (e.g. 120 or 120.5).");
        return (long)(gb * 1024m * 1024m * 1024m);
    }

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

        if (!simulation)
        {
            string list = string.Join("\n", pending.Select(p => $"• [{p.RiskText}] {p.Description}"));
            var answer = MessageBox.Show(this,
                $"Apply the operations below?\n\n{list}\n\nDestructive operations cannot be undone.",
                "Confirm operations", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        _operationsCts?.Dispose();
        _operationsCts = new CancellationTokenSource();
        ToggleOperations(running: true);

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
                await LoadDisksAsync();
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
        }
    }

    private void ToggleOperations(bool running)
    {
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
