using System.IO;
using System.Windows;
using System.Windows.Controls;
using FreePartition.Core.Disks;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Util;
using Microsoft.Win32;

namespace FreePartition.Wpf;

/// <summary>MFT scan and recovery of deleted files (read-only on the source).</summary>
public partial class RecoveryWindow : Window
{
    private CancellationTokenSource? _cts;
    private bool _busy;
    private ScanResult? _result;
    private readonly HashSet<long> _checked = new();   // MFT record numbers of the checked files

    public RecoveryWindow(char? initialDrive = null)
    {
        InitializeComponent();
        CmbCategory.ItemsSource = Enum.GetValues<FileCategory>();
        CmbCategory.SelectedIndex = 0;
        CmbMinUnit.ItemsSource = CmbMaxUnit.ItemsSource = SizeUnits.Select(u => u.Name).ToList();
        CmbMinUnit.SelectedIndex = CmbMaxUnit.SelectedIndex = 1;   // MB
        LoadDrives(initialDrive);
        _ = LoadMediaTypesAsync();
    }

    /// <summary>Letters of the drives that sit on an SSD (filled in the background: WMI can be slow).</summary>
    private readonly HashSet<string> _ssdDrives = new();

    private async Task LoadMediaTypesAsync()
    {
        try
        {
            var disks = await Task.Run(DiskService.ListDisks);
            foreach (var disk in disks.Where(d => d.IsSsd))
            {
                foreach (var volume in disk.Partitions.SelectMany(p => p.Volumes))
                {
                    if (volume.Letter.Length > 0)
                        _ssdDrives.Add(char.ToUpperInvariant(volume.Letter[0]).ToString());
                }
            }
            UpdateTrimWarning();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException)
        {
            // No warning is better than a failed window: the scan itself does not depend on this.
        }
    }

    private void CmbDrives_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTrimWarning();

    private void UpdateTrimWarning() =>
        TrimWarning.Visibility = CmbDrives.SelectedItem is string drive && _ssdDrives.Contains(drive)
            ? Visibility.Visible : Visibility.Collapsed;

    private void Window_Closed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
    }

    private void LoadDrives(char? initialDrive)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady
                        && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
                        && string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name.TrimEnd('\\', ':'))
            .ToList();

        CmbDrives.ItemsSource = drives;
        if (initialDrive is { } letter && drives.Contains(char.ToUpperInvariant(letter).ToString()))
            CmbDrives.SelectedItem = char.ToUpperInvariant(letter).ToString();
        else if (drives.Count > 0)
            CmbDrives.SelectedIndex = 0;
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (CmbDrives.SelectedItem is not string drive)
        {
            MessageBox.Show(this, "No NTFS drive available.", "Notice");
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        ToggleScan(running: true);
        FilesTree.ItemsSource = null;
        _checked.Clear();
        UpdateRecoverButton();
        _result = null;
        Progress.Value = 0;

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress.Value = p.Percent;
            TxtFileStatus.Text = $"Analyzing the MFT... {p.Percent:0}% — {p.DeletedFound:N0} deleted file(s) found";
        });

        try
        {
            _result = await new DeletedFileScanner().ScanAsync(drive, progress, _cts.Token);

            RebuildTree();

            Progress.Value = 100;
            TxtFileStatus.Text =
                $"{_result.Files.Count:N0} deleted file(s) in {_result.Duration.TotalSeconds:0.0}s " +
                $"({_result.RecordsAnalyzed:N0} records analyzed).";
        }
        catch (OperationCanceledException)
        {
            TxtFileStatus.Text = "Scan canceled.";
        }
        catch (Exception ex)
        {
            TxtFileStatus.Text = "Scan failed.";
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ToggleScan(running: false);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ToggleScan(bool running)
    {
        BtnScan.IsEnabled = !running;
        BtnCancel.IsEnabled = running;
        CmbDrives.IsEnabled = !running;
        _busy = running;
        UpdateRecoverButton();
    }

    private void RebuildTree()
    {
        if (_result is null) return;

        var filter = BuildFilter();
        var files = filter.Apply(_result.Files);

        var root = FolderNode.Build(files);
        FilesTree.ItemsSource = TreeNode.Build(root, expandAll: !filter.IsEmpty,
            isChecked: f => _checked.Contains(f.MftRecordNumber),
            onFileChecked: (f, isChecked) =>
            {
                if (isChecked) _checked.Add(f.MftRecordNumber); else _checked.Remove(f.MftRecordNumber);
                UpdateRecoverButton();
            });
    }

    private void UpdateRecoverButton()
    {
        BtnRecover.IsEnabled = !_busy && _checked.Count > 0;
        BtnRecover.Content = _checked.Count > 0 ? $"Recover checked ({_checked.Count:N0})…" : "Recover checked…";
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => RebuildTree();

    private void Filter_Changed(object sender, EventArgs e) => RebuildTree();

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        TxtFilter.Clear();
        TxtMinSize.Clear();
        TxtMaxSize.Clear();
        CmbCategory.SelectedIndex = 0;
        DateFrom.SelectedDate = null;
        DateTo.SelectedDate = null;
        RebuildTree();
    }

    private static readonly (string Name, long Bytes)[] SizeUnits = [("KB", 1L << 10), ("MB", 1L << 20), ("GB", 1L << 30)];

    private RecoveryFilter BuildFilter() => new()
    {
        NameContains = TxtFilter.Text.Trim(),
        Category = CmbCategory.SelectedItem is FileCategory category ? category : FileCategory.All,
        MinSize = ParseSize(TxtMinSize.Text, CmbMinUnit.SelectedIndex),
        MaxSize = ParseSize(TxtMaxSize.Text, CmbMaxUnit.SelectedIndex),
        ModifiedFrom = DateFrom.SelectedDate,
        ModifiedTo = DateTo.SelectedDate
    };

    /// <summary>Size in bytes, or null when the box is empty or not a valid number.</summary>
    private static long? ParseSize(string text, int unitIndex)
    {
        if (unitIndex < 0 || !decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var value) || value < 0)
            return null;
        return (long)(value * SizeUnits[unitIndex].Bytes);
    }

    private int _previewVersion;

    private async void FilesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        int version = ++_previewVersion;
        ClearPreview();

        var result = _result;
        if (result is null || e.NewValue is not TreeNode { File: { } file }) return;

        if (!file.IsRecoverable)
        {
            TxtPreviewInfo.Text = "No preview: the file is compressed, encrypted or has no data.";
            return;
        }

        TxtPreviewInfo.Text = "Loading preview...";
        try
        {
            var preview = await Task.Run(() => FilePreviewReader.Read(result, file));
            if (version != _previewVersion) return;   // another file was selected meanwhile

            ShowPreview(preview, file);
        }
        catch (Exception ex)
        {
            if (version == _previewVersion) TxtPreviewInfo.Text = $"Preview failed: {ex.Message}";
        }
    }

    private void ShowPreview(FilePreview preview, DeletedFile file)
    {
        string note = file.Chance is RecoveryChance.Low
            ? " The clusters may have been overwritten, so the content can be wrong."
            : "";

        if (preview.Kind == PreviewKind.Image)
        {
            try
            {
                using var stream = new MemoryStream(preview.Data);
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();

                ImgPreview.Source = bitmap;
                ImgPreview.Visibility = Visibility.Visible;
                TxtPreviewInfo.Text = $"{bitmap.PixelWidth} x {bitmap.PixelHeight} px.{note}";
                return;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FileFormatException)
            {
                // Damaged image: show the raw bytes instead.
                preview = preview with { Kind = PreviewKind.Hex, Text = FilePreviewReader.FormatHex(preview.Data.AsSpan(0, Math.Min(preview.Data.Length, FilePreviewReader.MaxHexBytes))) };
                note = " The image could not be decoded (damaged or overwritten)." + note;
            }
        }

        TxtPreview.Text = preview.Text;
        TxtPreview.Visibility = Visibility.Visible;
        TxtPreviewInfo.Text = (preview.Truncated ? $"Showing the first {ByteFormatter.Format(preview.Data.Length)}." : "") + note;
    }

    private void ClearPreview()
    {
        ImgPreview.Source = null;
        ImgPreview.Visibility = Visibility.Collapsed;
        TxtPreview.Clear();
        TxtPreview.Visibility = Visibility.Collapsed;
        TxtPreviewInfo.Text = "";
    }

    private async void BtnRecover_Click(object sender, RoutedEventArgs e)
    {
        var result = _result;
        if (result is null) return;

        var selected = result.Files.Where(f => _checked.Contains(f.MftRecordNumber)).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Check at least one file in the tree.", "Notice");
            return;
        }

        var dialog = new OpenFolderDialog { Title = $"Destination folder (must be on ANOTHER disk, not {result.Drive}:)" };
        if (dialog.ShowDialog(this) != true) return;
        string destination = dialog.FolderName;

        _busy = true;
        UpdateRecoverButton();
        TxtFileStatus.Text = $"Recovering {selected.Count} file(s)...";

        int recovered = 0;
        var errors = new List<string>();
        var recoverer = new FileRecoverer();

        await Task.Run(() =>
        {
            foreach (var file in selected)
            {
                try
                {
                    recoverer.Recover(result, file, destination, keepFolders: true);
                    recovered++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{file.Name}: {ex.Message}");
                }
            }
        });

        _busy = false;
        UpdateRecoverButton();
        TxtFileStatus.Text = $"{recovered} file(s) recovered to {destination}.";

        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                $"{recovered:N0} of {selected.Count:N0} file(s) recovered to {destination}.\n\n" +
                $"{errors.Count} file(s) could not be recovered:\n\n" + string.Join("\n", errors.Take(10)),
                "Partial recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(this, $"{recovered:N0} file(s) recovered to {destination}.",
                "Recovery finished", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
