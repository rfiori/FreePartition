using System.Diagnostics;
using System.IO;
using System.Windows;
using FreePartition.Core.Updates;

namespace FreePartition.Wpf;

/// <summary>
/// Offers a newer GitHub release. "Update now" downloads it, checks the SHA-256, swaps the files and restarts the app.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly ReleaseInfo _release;
    private readonly UpdateSettings _settings;
    private readonly Func<string?> _blockReason;
    private CancellationTokenSource? _cts;
    private bool _installing;

    /// <param name="blockReason">Returns why an update cannot happen right now (operations running or queued), or null.</param>
    public UpdateWindow(ReleaseInfo release, string currentVersion, UpdateSettings settings, Func<string?> blockReason)
    {
        InitializeComponent();
        _release = release;
        _settings = settings;
        _blockReason = blockReason;

        TxtTitle.Text = $"FreePartition {release.Tag} is available";
        TxtSubtitle.Text = $"You have v{currentVersion}.";
        TxtNotes.Text = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes." : release.Notes.Trim();

        if (!release.CanInstall)
        {
            BtnInstall.Content = "Open release page";
            TxtStatus.Text = "This release cannot be installed automatically (no verified package). Download it from the release page.";
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!_release.CanInstall)
        {
            OpenReleasePage();
            return;
        }

        string? blocked = _blockReason();
        if (blocked is not null)
        {
            MessageBox.Show(this, blocked, "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The swap needs the real FreePartition.exe (not 'dotnet run' or a debugger host).
        string? exe = Environment.ProcessPath;
        if (exe is null || !Path.GetFileName(exe).Equals("FreePartition.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "This copy of FreePartition cannot update itself. Download the new version from the release page.",
                "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenReleasePage();
            return;
        }

        _installing = true;
        BtnInstall.IsEnabled = BtnSkip.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        TxtStatus.Text = "Downloading and verifying...";
        _cts = new CancellationTokenSource();

        string? staged = null;
        try
        {
            using var http = UpdateService.CreateHttpClient();
            var progress = new Progress<double>(p => Bar.Value = p);
            staged = await new UpdateService(http).DownloadAsync(_release, progress, _cts.Token);

            // Checked again after the download, which can take a while: the user may have queued something.
            if (_blockReason() is { } late)
            {
                TxtStatus.Text = late;
                return;
            }

            TxtStatus.Text = "Installing...";
            UpdateService.InstallFiles(staged, AppContext.BaseDirectory);

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Update canceled.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "The update failed. Nothing was changed.";
            MessageBox.Show(this, ex.Message + "\n\nYou can download the new version from the release page.", "Update",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (staged is not null) UpdateService.TryDeleteDirectory(Path.GetDirectoryName(staged)!);
            _installing = false;
            Bar.Visibility = Visibility.Collapsed;
            BtnInstall.IsEnabled = BtnSkip.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _settings.SkippedVersion = _release.Tag;
        _settings.Save(UpdateSettings.DefaultPath);
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing during the download cancels it. During the file swap (after the download) it must not be interrupted.
        if (_installing) _cts?.Cancel();
    }

    private void OpenReleasePage()
    {
        if (!Uri.TryCreate(_release.PageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
