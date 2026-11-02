using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FreePartition.Core.Disks;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Draws a volume as a grid of sampled cells (zero, data, unreadable) and shows the bytes of the cell you click.
/// Read-only, and it works on a volume Windows no longer recognizes (RAW).
/// </summary>
public partial class ClusterMapWindow : Window
{
    private const int DumpBytes = 512;

    private readonly PartitionRow _row;
    private CancellationTokenSource? _cts;
    private ClusterMap? _map;
    private int _clusterBytes;

    public ClusterMapWindow(PartitionRow row)
    {
        InitializeComponent();
        _row = row;
        TxtTitle.Text = row.Letter is { } l ? $"{l}:" + (string.IsNullOrEmpty(row.Label) ? "" : $"  {row.Label}") : row.Name;
        TxtSubtitle.Text = $"{row.Disk}  ·  {ByteFormatter.Format(row.Size)}";
        CellGrid.CellSelected += CellSelected;
        Closed += (_, _) => _cts?.Cancel();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_row.Letter is not { } letter) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        BtnStart.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        CellGrid.Map = null;
        TxtCell.Text = TxtDump.Text = "";
        Legend.Children.Clear();
        Bar.Value = 0;
        TxtStatus.Text = "Sampling the volume...";

        var progress = new Progress<double>(p => Bar.Value = p);
        long length = checked((long)_row.Size);

        try
        {
            var (map, clusterBytes) = await Task.Run(() =>
            {
                using var volume = RawVolume.Open(letter.ToString());
                return (ClusterMap.Build(volume.Read, length, 4096, progress, token), ReadClusterSize(volume));
            }, token);

            _map = map;
            _clusterBytes = clusterBytes;
            CellGrid.Map = map;
            ShowLegend(map);
            TxtStatus.Text = $"Done. Each square is {ByteFormatter.Format((ulong)map.BytesPerCell)}.";
            Bar.Value = 1;
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Canceled.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Could not scan: {ex.Message}";
        }
        finally
        {
            BtnStart.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Cluster size from the NTFS boot sector, or 0 when the volume is not NTFS (any more).</summary>
    private static int ReadClusterSize(RawVolume volume)
    {
        try
        {
            var sector = new byte[512];
            return volume.Read(0, sector) == 512 ? checked((int)NtfsBootSector.Read(sector).BytesPerCluster) : 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            return 0;
        }
    }

    private void ShowLegend(ClusterMap map)
    {
        Legend.Children.Clear();
        foreach (var (state, color, name) in new[]
                 {
                     (CellState.Zero, "#BDEBDD", "Zero"),
                     (CellState.Data, "#E8833A", "Data"),
                     (CellState.Unreadable, "#C0392B", "Unreadable")
                 })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            item.Children.Add(new Rectangle
            {
                Width = 12, Height = 12, Fill = (Brush)new BrushConverter().ConvertFromString(color)!,
                Stroke = Brushes.Silver, StrokeThickness = 0.5, Margin = new Thickness(0, 0, 6, 0)
            });
            int count = map.CountOf(state);
            item.Children.Add(new TextBlock { Text = $"{name}: {count:N0} ({(double)count / map.Cells:P1})" });
            Legend.Children.Add(item);
        }
    }

    private async void CellSelected(int cell)
    {
        if (_map is null || _row.Letter is not { } letter) return;

        long start = _map.CellStart(cell), end = _map.CellEnd(cell);
        long dumpAt = _map.FirstDataOffset[cell] is >= 0 and var first ? first / DumpBytes * DumpBytes : start;

        string where = $"Square {cell + 1:N0} of {_map.Cells:N0}  ·  bytes {start:N0} – {end:N0}";
        if (_clusterBytes > 0)
            where += $"  ·  clusters {start / _clusterBytes:N0} – {(end - 1) / _clusterBytes:N0}";
        TxtCell.Text = $"{where}  ·  {_map.States[cell]}";
        TxtDump.Text = "Reading...";

        try
        {
            var data = await Task.Run(() =>
            {
                using var volume = RawVolume.Open(letter.ToString());
                var buffer = new byte[(int)Math.Min(DumpBytes, _row.Size - (ulong)dumpAt)];
                int got = volume.Read(dumpAt, buffer);
                return buffer[..got];
            });

            TxtDump.Text = $"Offset {dumpAt:N0}\n" + (data.Length == 0 ? "(nothing could be read)" : FilePreviewReader.FormatHex(data));
        }
        catch (Exception ex)
        {
            TxtDump.Text = $"Could not read: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
