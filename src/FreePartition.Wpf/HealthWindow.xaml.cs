using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FreePartition.Core.Disks;

namespace FreePartition.Wpf;

/// <summary>Shows the S.M.A.R.T. / health report of each physical disk (read-only).</summary>
public partial class HealthWindow : Window
{
    private IReadOnlyList<DiskHealth> _disks = [];

    public HealthWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        int previous = (CmbDisks.SelectedItem as DiskItem)?.Health.DiskIndex ?? -1;
        try
        {
            BtnReload.IsEnabled = false;
            TxtLevel.Text = "Reading disk health...";
            TxtSummary.Text = "";

            _disks = await Task.Run(() => DiskHealthService.ReadAll());

            var items = _disks.Select(d => new DiskItem(d)).ToList();
            CmbDisks.ItemsSource = items;
            CmbDisks.SelectedItem = items.FirstOrDefault(i => i.Health.DiskIndex == previous) ?? items.FirstOrDefault();
            if (items.Count == 0) TxtLevel.Text = "No disks found.";
        }
        catch (Exception ex)
        {
            TxtLevel.Text = "Failed to read the disk health.";
            TxtSummary.Text = ex.Message;
        }
        finally
        {
            BtnReload.IsEnabled = true;
        }
    }

    private void CmbDisks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbDisks.SelectedItem is not DiskItem { Health: var health }) return;

        (string text, string background, string border) = health.Level switch
        {
            HealthLevel.Good => ("Good", "#E8F6EC", "#8BCB9E"),
            HealthLevel.Warning => ("Warning", "#FFF6E5", "#E8C77A"),
            HealthLevel.Critical => ("Critical", "#FDECEA", "#E59A93"),
            _ => ("Unknown", "#F3F3F3", "#CCCCCC"),
        };
        TxtLevel.Text = text;
        TxtSummary.Text = health.Summary;
        StatusBox.Background = Brush(background);
        StatusBox.BorderBrush = Brush(border);

        TxtHealth.Text = health.HealthStatus ?? "—";
        TxtTemperature.Text = health.TemperatureCelsius is { } t ? $"{t} °C" : "—";
        TxtPowerOn.Text = health.PowerOnHours is { } h ? $"{h:N0} h (~{h / 24:N0} days)" : "—";
        TxtWear.Text = health.WearPercent is { } w ? $"{w}%" : "—";
        AttributesGrid.ItemsSource = health.Attributes;
        DetailsGrid.ItemsSource = health.Details;
        bool nvme = health.Details.Count > 0;
        bool unavailable = health.SmartUnavailable is not null;
        UnavailableBox.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        TxtUnavailable.Text = health.SmartUnavailable;
        DetailsGrid.Visibility = nvme && !unavailable ? Visibility.Visible : Visibility.Collapsed;
        AttributesGrid.Visibility = !nvme && !unavailable ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private sealed record DiskItem(DiskHealth Health)
    {
        public override string ToString() => $"Disk {Health.DiskIndex} — {Health.Model}";
    }
}
