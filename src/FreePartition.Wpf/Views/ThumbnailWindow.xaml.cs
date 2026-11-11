using System.ComponentModel;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreePartition.Core.Ntfs;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>
/// Thumbnails of every previewable image in the scan result, so files can be checked at a glance instead of
/// selecting them one by one. Read-only on the source: it only reads the same bytes the single-file preview reads.
/// </summary>
public partial class ThumbnailWindow : Window
{
    public static readonly DependencyProperty CellWidthProperty =
        DependencyProperty.Register(nameof(CellWidth), typeof(double), typeof(ThumbnailWindow), new PropertyMetadata(160.0));

    private readonly ScanResult _result;
    private readonly HashSet<long> _checked;
    private readonly Action _onCheckedChanged;
    private readonly List<ThumbnailItem> _items;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Items shown (and decoded) at a time; the rest wait on other pages so the list and memory stay manageable.</summary>
    private const int PageSize = 1000;

    private CancellationTokenSource? _pageCts;
    private List<ThumbnailItem> _pageItems = [];
    private int _pageIndex;

    public ThumbnailWindow(ScanResult result, IEnumerable<DeletedFile> images, HashSet<long> checkedFiles, Action onCheckedChanged)
    {
        InitializeComponent();
        WindowFit.Apply(this);

        _result = result;
        _checked = checkedFiles;
        _onCheckedChanged = onCheckedChanged;
        _items = images.Select(f => new ThumbnailItem(f, _checked.Contains(f.MftRecordNumber), OnItemChecked)).ToList();

        UpdateSummary();
        Loaded += (_, _) => ShowPage(0);
    }

    private int PageCount => Math.Max(1, (_items.Count + PageSize - 1) / PageSize);

    private void ShowPage(int pageIndex)
    {
        _pageCts?.Cancel();
        foreach (var old in _pageItems) old.ClearThumbnail();   // free the decoded images of the page being left

        _pageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        _pageItems = _items.Skip(_pageIndex * PageSize).Take(PageSize).ToList();
        _anchor = null;

        Items.ItemsSource = _pageItems;
        if (_pageItems.Count > 0) Items.ScrollIntoView(_pageItems[0]);

        PagerPanel.Visibility = PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnPrev.IsEnabled = _pageIndex > 0;
        BtnNext.IsEnabled = _pageIndex < PageCount - 1;
        int first = _pageItems.Count == 0 ? 0 : _pageIndex * PageSize + 1;
        TxtPage.Text = $"Page {_pageIndex + 1:N0} / {PageCount:N0} ({first:N0}–{_pageIndex * PageSize + _pageItems.Count:N0})";

        _pageCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = LoadThumbnailsAsync(_pageItems, _pageCts.Token);
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => ShowPage(_pageIndex - 1);

    private void BtnNext_Click(object sender, RoutedEventArgs e) => ShowPage(_pageIndex + 1);

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    private void OnItemChecked(ThumbnailItem item)
    {
        if (item.IsChecked) _checked.Add(item.File.MftRecordNumber); else _checked.Remove(item.File.MftRecordNumber);
        _onCheckedChanged();
        UpdateSummary();
    }

    private void UpdateSummary() =>
        TxtSummary.Text = $"{_items.Count:N0} item(s) — {_items.Count(i => i.IsChecked):N0} checked";

    /// <summary>The files read ahead of the decoders: a few at a time, so a long list of large photos does not fill the memory.</summary>
    private readonly record struct Work(ThumbnailItem Item, byte[] Bytes);

    /// <summary>
    /// Two stages. One task reads the files off the volume in order, which is what a spinning disk handles best; several
    /// others decode them at once, one per spare core, which is the slow part for large photos. Each cell fills in as soon
    /// as its image is decoded, so they may complete slightly out of order.
    /// </summary>
    private async Task LoadThumbnailsAsync(List<ThumbnailItem> pageItems, CancellationToken token)
    {
        int decoders = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        var queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(decoders) { SingleWriter = true });
        int done = 0;

        void Finished(ThumbnailItem item, BitmapSource? thumbnail, string? error)
        {
            int count = Interlocked.Increment(ref done);
            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested) return;
                if (thumbnail is not null || error is not null) item.SetThumbnail(thumbnail, error);   // else keep the cell's own text
                TxtStatus.Text = count < pageItems.Count
                    ? $"Loading thumbnails... {count:N0} / {pageItems.Count:N0}"
                    : $"{count:N0} thumbnail(s) loaded.";
            });
        }

        var reader = Task.Run(async () =>
        {
            try
            {
                foreach (var item in pageItems)
                {
                    token.ThrowIfCancellationRequested();
                    if (!item.File.CanRecover)   // wiped: nothing to show, the cell already says so
                    {
                        Finished(item, null, null);
                        continue;
                    }

                    byte[]? bytes;
                    try
                    {
                        bytes = FilePreviewReader.ReadThumbnailSource(_result, item.File);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        Finished(item, null, "Damaged or overwritten");
                        continue;
                    }

                    if (bytes is null)
                        Finished(item, null, FilePreviewReader.HasIcon(item.File.Name) ? "No icon" : "Too large to preview");
                    else
                        await queue.Writer.WriteAsync(new Work(item, bytes), token);
                }
            }
            finally
            {
                queue.Writer.TryComplete();
            }
        }, token);

        var workers = Enumerable.Range(0, decoders).Select(_ => Task.Run(async () =>
        {
            await foreach (var work in queue.Reader.ReadAllAsync(token))
            {
                var (thumbnail, error) = Decode(work.Item.File, work.Bytes);
                Finished(work.Item, thumbnail, error);
            }
        }, token)).ToList();

        try
        {
            await Task.WhenAll(workers.Append(reader));
        }
        catch (OperationCanceledException)
        {
            // The window was closed: nothing left to show.
        }
    }

    private static (BitmapSource? Thumbnail, string? Error) Decode(DeletedFile file, byte[] bytes)
    {
        try
        {
            if (FilePreviewReader.HasIcon(file.Name))
                return PeIconExtractor.Extract(bytes) is { } ico ? (IconDecoder.Largest(ico), null) : (null, "No icon");

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320;   // the largest size the slider allows: keeps memory low for large photos
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return (bitmap, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FileFormatException
                                       or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, "Damaged or overwritten");
        }
    }

    private ThumbnailItem? _anchor;      // last cell clicked: the start of a Shift+click range
    private bool _anchorState;           // what that click set it to; a range and a drag apply the same value
    private bool _dragging;

    private ThumbnailItem? ItemAt(Point position) =>
        Items.InputHitTest(position) is DependencyObject element
            ? ItemsControl.ContainerFromElement(Items, element) is ListBoxItem { DataContext: ThumbnailItem item } ? item : null
            : null;

    private void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsScrollBar(source)) return;   // let the scroll bar work

        var item = ItemAt(e.GetPosition(Items));
        if (item is null) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor is not null)
        {
            int from = _pageItems.IndexOf(_anchor), to = _pageItems.IndexOf(item);
            for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                _pageItems[i].IsChecked = _anchorState;
        }
        else
        {
            _anchorState = !item.IsChecked;
            item.IsChecked = _anchorState;
            _anchor = item;
            _dragging = true;
            Items.CaptureMouse();   // keep receiving moves while the button is down, even outside the list
        }

        e.Handled = true;
    }

    private static bool IsScrollBar(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    private void Items_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Items.ReleaseMouseCapture();
            return;
        }

        if (ItemAt(e.GetPosition(Items)) is { } item)
            item.IsChecked = _anchorState;   // the drag paints every cell it passes over with the first cell's new state
    }

    private void Items_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Items.ReleaseMouseCapture();

    private void Items_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void SldSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => CellWidth = e.NewValue;

    private void BtnCheckAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void BtnUncheckAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool isChecked)
    {
        foreach (var item in _items)
            item.IsChecked = isChecked;
    }

    private void BtnDone_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e) => _cts.Cancel();
}

/// <summary>One thumbnail cell: the image (once loaded) and the check box that mirrors the recovery selection.</summary>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private readonly Action<ThumbnailItem> _onChecked;
    private bool _isChecked;
    private BitmapSource? _thumbnail;
    private string _placeholder = "Loading...";

    public ThumbnailItem(DeletedFile file, bool isChecked, Action<ThumbnailItem> onChecked)
    {
        File = file;
        _isChecked = isChecked && file.CanRecover;
        _onChecked = onChecked;
        if (!file.CanRecover) _placeholder = "Overwritten";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeletedFile File { get; }

    public bool CanCheck => File.CanRecover;

    public string Caption => File.Name;

    public string ToolTip =>
        $"{File.Name}\n{File.Path}\n{ByteFormatter.Format(File.Size)} — {File.ChanceText}";

    public BitmapSource? Thumbnail => _thumbnail;

    public string Placeholder => _placeholder;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value && !CanCheck) return;
            if (_isChecked == value) return;
            _isChecked = value;
            Notify(nameof(IsChecked));
            _onChecked(this);
        }
    }

    /// <summary>Drops the decoded image (when its page is left) and goes back to the "Loading..." text.</summary>
    public void ClearThumbnail()
    {
        if (_thumbnail is null) return;
        _thumbnail = null;
        _placeholder = "Loading...";
        Notify(nameof(Thumbnail));
        Notify(nameof(Placeholder));
    }

    public void SetThumbnail(BitmapSource? thumbnail, string? error)
    {
        _thumbnail = thumbnail;
        _placeholder = thumbnail is null ? error ?? "" : "";
        Notify(nameof(Thumbnail));
        Notify(nameof(Placeholder));
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
