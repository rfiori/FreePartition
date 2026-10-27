using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FreePartition.Core.Ntfs;
using Microsoft.Win32;

namespace FreePartition.Wpf;

/// <summary>MFT scan and recovery of deleted files (read-only on the source).</summary>
public partial class RecoveryWindow : Window
{
    private CancellationTokenSource? _cts;
    private ScanResult? _result;
    private ICollectionView? _view;

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
        FilesGrid.ItemsSource = null;
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

            _view = CollectionViewSource.GetDefaultView(_result.Files);
            _view.Filter = FilterByName;
            FilesGrid.ItemsSource = _view;

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
        BtnRecover.IsEnabled = !running;
    }

    private bool FilterByName(object item)
    {
        if (item is not DeletedFile file) return false;
        string filter = TxtFilter.Text.Trim();
        return filter.Length == 0 || file.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private async void BtnRecover_Click(object sender, RoutedEventArgs e)
    {
        var result = _result;
        if (result is null) return;

        var selected = FilesGrid.SelectedItems.Cast<DeletedFile>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select at least one file in the list.", "Notice");
            return;
        }

        var dialog = new OpenFolderDialog { Title = $"Destination folder (must be on ANOTHER disk, not {result.Drive}:)" };
        if (dialog.ShowDialog(this) != true) return;
        string destination = dialog.FolderName;

        BtnRecover.IsEnabled = false;
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
                    recoverer.Recover(result, file, destination);
                    recovered++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{file.Name}: {ex.Message}");
                }
            }
        });

        BtnRecover.IsEnabled = true;
        TxtFileStatus.Text = $"{recovered} file(s) recovered to {destination}.";

        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                $"{errors.Count} file(s) could not be recovered:\n\n" + string.Join("\n", errors.Take(10)),
                "Partial recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
