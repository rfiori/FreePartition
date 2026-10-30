using System.Text;
using System.Windows;
using System.Windows.Media;
using FreePartition.Core.Disks;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Reads a whole volume, read-only, and says whether it was wiped: bytes that are not zero, where the first one is,
/// and which file system signatures survived. Works on a volume Windows no longer recognizes (RAW).
/// </summary>
public partial class VerifyWipeWindow : Window
{
    private const int DumpBytes = 512;

    private readonly PartitionRow _row;
    private CancellationTokenSource? _cts;

    public VerifyWipeWindow(PartitionRow row)
    {
        InitializeComponent();
        _row = row;
        TxtTitle.Text = row.Letter is { } l ? $"{l}:" + (string.IsNullOrEmpty(row.Label) ? "" : $"  {row.Label}") : row.Name;
        TxtSubtitle.Text = $"{row.Disk}  ·  {ByteFormatter.Format(row.Size)}";
        Closed += (_, _) => _cts?.Cancel();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_row.Letter is not { } letter) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        BtnStart.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        BtnCopy.IsEnabled = false;
        VerdictBox.Visibility = TxtDump.Visibility = TxtDumpTitle.Visibility = Visibility.Collapsed;
        TxtDetails.Text = "";
        Bar.Value = 0;
        TxtStatus.Text = "Reading the volume...";

        var progress = new Progress<double>(p =>
        {
            Bar.Value = p;
            TxtStatus.Text = $"Reading the volume... {p:P0}";
        });
        long length = checked((long)_row.Size);

        try
        {
            var (result, dump) = await Task.Run(() =>
            {
                using var volume = RawVolume.Open(letter.ToString());
                var result = WipeVerifier.Scan(volume.Read, length, progress, token);

                byte[]? dump = null;
                if (result.FirstNonZeroOffset >= 0)
                {
                    dump = new byte[DumpBytes];
                    long start = result.FirstNonZeroOffset / DumpBytes * DumpBytes;
                    int got = volume.Read(start, dump);
                    Array.Resize(ref dump, got);
                }
                return (result, dump);
            }, token);

            Show(result, dump);
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Canceled.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Could not verify: {ex.Message}";
        }
        finally
        {
            BtnStart.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Show(WipeVerification r, byte[]? dump)
    {
        Bar.Value = 1;
        TxtStatus.Text = "Done.";

        (string text, string back, string border) = r.Verdict switch
        {
            WipeVerdict.AllZeros => ("Wiped: every byte of the volume is zero.", "#E3F4E6", "#7CC48A"),
            WipeVerdict.LooksRandom => ("Overwritten: the volume holds random-looking data and no file system signature.", "#E3F4E6", "#7CC48A"),
            WipeVerdict.ResidualData => ("Not clean: data that is not zero is still on the volume.", "#FDE9E7", "#E39A93"),
            _ => ("Inconclusive: the read stopped before the end of the volume.", "#FFF3CD", "#E0C060")
        };
        VerdictBox.Background = Brush(back);
        VerdictBox.BorderBrush = Brush(border);
        TxtVerdict.Text = text;
        VerdictBox.Visibility = Visibility.Visible;

        var details = new StringBuilder();
        details.AppendLine($"Volume size: {ByteFormatter.Format((ulong)r.Length)}  ({r.Length:N0} bytes)");
        details.AppendLine($"Bytes read: {r.BytesScanned:N0}" + (r.Complete ? "" : $"  (stopped at {(double)r.BytesScanned / r.Length:P2})"));
        details.AppendLine($"Bytes that are not zero: {r.NonZeroBytes:N0}");
        details.AppendLine($"First non-zero byte: " + (r.FirstNonZeroOffset < 0 ? "none" : $"offset {r.FirstNonZeroOffset:N0} (0x{r.FirstNonZeroOffset:X})"));
        details.AppendLine($"Chunks of 4 KB that are all zeros: {r.ZeroChunks:N0} of {r.Chunks:N0}  ({r.ZeroChunkFraction:P2})");
        details.Append("File system signatures: ");
        details.Append(r.Signatures.Count == 0
            ? "none found"
            : string.Join(", ", r.Signatures.OrderBy(s => s.Key).Select(s => $"{s.Key} ×{s.Value:N0}")));
        if (r.Verdict == WipeVerdict.LooksRandom)
            details.AppendLine().AppendLine().Append("Random data cannot be told apart from encrypted or compressed data, so this is a hint, not proof.");
        if (r.Verdict == WipeVerdict.Incomplete)
            details.AppendLine().AppendLine().Append("The volume may be smaller than the partition, or the disk returned a read error. Try again.");
        TxtDetails.Text = details.ToString();

        if (dump is { Length: > 0 })
        {
            TxtDump.Text = FilePreviewReader.FormatHex(dump);
            TxtDump.Visibility = TxtDumpTitle.Visibility = Visibility.Visible;
            TxtDumpTitle.Text = $"First data found (sector at offset {r.FirstNonZeroOffset / DumpBytes * DumpBytes:N0})";
        }
        BtnCopy.IsEnabled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = $"{TxtTitle.Text}\n{TxtSubtitle.Text}\n\n{TxtVerdict.Text}\n\n{TxtDetails.Text}";
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is in use by another program. Try again.", "Verify wipe");
        }
    }

    private static Brush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
