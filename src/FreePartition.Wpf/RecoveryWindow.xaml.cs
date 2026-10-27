using System.IO;
using System.Windows;
using System.Windows.Controls;
using FreePartition.Core.Ntfs;
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
        LoadDrives(initialDrive);
    }

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

        string filter = TxtFilter.Text.Trim();
        var files = filter.Length == 0
            ? _result.Files
            : _result.Files.Where(f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var root = FolderNode.Build(files);
        FilesTree.ItemsSource = TreeNode.Build(root, expandAll: filter.Length > 0,
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
