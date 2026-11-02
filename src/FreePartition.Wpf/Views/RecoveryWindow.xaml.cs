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
    private bool _recovering;
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

    private void CmbDrives_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTrimWarning();
        UpdateCarveOption();
    }

    /// <summary>File systems the carver can read: only the MFT scan needs NTFS.</summary>
    private static readonly string[] SupportedFormats = ["NTFS", "FAT", "FAT32", "exFAT"];

    /// <summary>File system of each listed drive, by letter.</summary>
    private readonly Dictionary<string, string> _formats = new();

    private bool IsNtfs(string drive) =>
        _formats.TryGetValue(drive, out var format) && string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Carving is the only way to scan a FAT/exFAT drive, so it is forced on there; on NTFS it is the user's choice.</summary>
    private void UpdateCarveOption()
    {
        if (CmbDrives.SelectedItem is not string drive) return;

        bool ntfs = IsNtfs(drive);
        ChkCarve.IsChecked = !ntfs;
        ChkCarve.IsEnabled = ntfs && !_busy;
        UpdateCarveNote();
    }

    private const string CarvedFormats =
        "JPEG, PNG, GIF, BMP, WebP, PSD, PDF, DOCX/XLSX/PPTX, ZIP, 7z, MP4/MOV/HEIC, WAV, AVI, SQLite, EXE/DLL";

    private void ChkCarve_Changed(object sender, RoutedEventArgs e) => UpdateCarveNote();

    private void UpdateCarveNote()
    {
        bool carve = ChkCarve.IsChecked == true;
        bool ntfs = CmbDrives.SelectedItem is string drive && IsNtfs(drive);

        TxtCarveNote.Visibility = carve ? Visibility.Visible : Visibility.Collapsed;
        TxtCarveNote.Text = !ntfs
            ? $"This drive is not NTFS, so files are searched by their content ({CarvedFormats}). " +
              "They come back without their original names and folders, and a fragmented file may be incomplete."
            : $"Files are searched by their content ({CarvedFormats}) instead of the MFT. " +
              "This reads the whole drive, so it is slow; the files come back without their original names and folders.";
    }

    private void UpdateTrimWarning() =>
        TrimWarning.Visibility = CmbDrives.SelectedItem is string drive && _ssdDrives.Contains(drive)
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A scan can be interrupted after a confirmation; copying recovered files cannot, so closing waits for it.</summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_recovering)
        {
            MessageBox.Show(this, "Files are being recovered. Wait until the recovery finishes before closing this window.",
                "Recovery in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
        }
        else if (_busy && MessageBox.Show(this, "A scan is running. Stop it and close this window?",
                     "Scan in progress", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
    }

    private void LoadDrives(char? initialDrive)
    {
        var candidates = DriveInfo.GetDrives()
            .Where(d => d.IsReady
                        && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
                        && SupportedFormats.Contains(d.DriveFormat, StringComparer.OrdinalIgnoreCase))
            .ToList();

        _formats.Clear();
        foreach (var drive in candidates)
            _formats[drive.Name.TrimEnd('\\', ':')] = drive.DriveFormat;
        var drives = candidates.Select(d => d.Name.TrimEnd('\\', ':')).ToList();

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
            MessageBox.Show(this, "No NTFS, FAT32 or exFAT drive available.", "Notice");
            return;
        }

        bool carve = ChkCarve.IsChecked == true;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        ToggleScan(running: true);
        FilesTree.ItemsSource = null;
        _checked.Clear();
        UpdateRecoverButton();
        _result = null;
        Progress.Value = 0;

        bool carving = carve;
        var progress = new Progress<ScanProgress>(p =>
        {
            Progress.Value = p.Percent;
            TxtFileStatus.Text = carving
                ? $"{p.Phase ?? "Searching for file signatures..."} — {p.DeletedFound:N0} file(s) found"
                : p.Phase is { } phase
                    ? $"{phase} — {p.DeletedFound:N0} deleted file(s) found"
                    : $"Analyzing the MFT... {p.Percent:0}% — {p.DeletedFound:N0} deleted file(s) found";
        });

        try
        {
            if (!carve)
            {
                try
                {
                    _result = await new DeletedFileScanner().ScanAsync(drive, progress, _cts.Token);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    // The MFT cannot be read: carving does not depend on it, so it becomes the way to scan this drive.
                    carving = true;
                    ChkCarve.IsChecked = true;
                    TxtFileStatus.Text = $"The MFT could not be read ({ex.Message}). Searching by file signature instead...";
                }
            }

            if (carving)
                _result = await new FileCarver().ScanAsync(drive, progress, _cts.Token);

            RebuildTree();

            // A canceled carving scan still lists what it found: the search was already done.
            if (_result!.Canceled)
            {
                TxtFileStatus.Text =
                    $"Scan canceled: {_result.Files.Count:N0} file(s) found in the first " +
                    $"{ByteFormatter.Format(_result.RecordsAnalyzed * _result.BytesPerCluster)} of the drive.";
                return;
            }

            Progress.Value = 100;
            TxtFileStatus.Text = carving
                ? $"{_result!.Files.Count:N0} file(s) found by signature in {_result.Duration.TotalSeconds:0.0}s " +
                  $"({ByteFormatter.Format(_result.RecordsAnalyzed * _result.BytesPerCluster)} scanned)."
                : $"{_result!.Files.Count:N0} deleted file(s) in {_result.Duration.TotalSeconds:0.0}s " +
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
        ChkCarve.IsEnabled = !running && CmbDrives.SelectedItem is string drive && IsNtfs(drive);
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
        string note = file.Chance switch
        {
            RecoveryChance.Low => " The clusters may have been overwritten, so the content can be wrong.",
            RecoveryChance.Overwritten => " The data was overwritten with zeros: there is nothing left to recover.",
            _ => ""
        };

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

        _busy = _recovering = true;
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

        _busy = _recovering = false;
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
