using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using FreePartition.Core.Operations;

namespace FreePartition.Wpf;

/// <summary>About dialog: version, repository, license and the environment the app is running in.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString()
                         ?? "unknown";
        // Strip the source-revision suffix the SDK appends (ex.: "0.0.1+a1b2c3d").
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        TxtVersion.Text = version;
        TxtRuntime.Text = RuntimeInformation.FrameworkDescription;
        TxtOs.Text = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        TxtElevation.Text = IsElevated() ? "Administrator" : "Standard user — disk operations will fail";
        TxtCopyright.Text = $"Copyright (c) 2026 Ricardo F M. FreePartition is an independent project ";

        string? folder = Path.GetDirectoryName(FileLog.DefaultPath);
        if (folder is not null) LinkLog.NavigateUri = new Uri(folder);
        else LinkLog.IsEnabled = false;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        string target = e.Uri.IsFile ? e.Uri.LocalPath : e.Uri.AbsoluteUri;
        if (e.Uri.IsFile) Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var details = new StringBuilder()
            .AppendLine("FreePartition")
            .AppendLine($"Version: {TxtVersion.Text}")
            .AppendLine($"Runtime: {TxtRuntime.Text}")
            .AppendLine($"Operating system: {TxtOs.Text}")
            .AppendLine($"Running as: {TxtElevation.Text}")
            .AppendLine("Repository: https://github.com/rfiori/FreePartition")
            .AppendLine("License: MIT")
            .ToString();

        try { Clipboard.SetText(details); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy details", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
