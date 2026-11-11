using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FreePartition.Core.Cloning;
using FreePartition.Core.Disks;
using FreePartition.Core.Operations;
using FreePartition.Core.Util;

namespace FreePartition.Wpf;

/// <summary>One line of the progress list: the step's name and how far it got.</summary>
public sealed class CloneStepRow(string title) : INotifyPropertyChanged
{
    private CloneStepState _state = CloneStepState.Pending;

    public string Title { get; } = title;

    public CloneStepState State
    {
        get => _state;
        set
        {
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }
    }

    public string Status => State switch
    {
        CloneStepState.Running => "Running…",
        CloneStepState.Done => "OK",
        CloneStepState.Skipped => "Skipped",
        CloneStepState.Failed => "FAILED",
        _ => "—"
    };

    public Brush StatusBrush => State switch
    {
        CloneStepState.Done => Brushes.ForestGreen,
        CloneStepState.Failed => Brushes.Firebrick,
        CloneStepState.Running => Brushes.DodgerBlue,
        _ => Brushes.Gray
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Guided "Clone Windows to another disk" (WinPE only): choose the Windows to copy and the disk to copy it to, read the
/// requirements, confirm, and follow each step. The work itself is <see cref="WindowsCloneWorkflow"/>.
/// </summary>
public partial class CloneWindowsWindow : Window
{
    private readonly IReadOnlyList<DiskInfo> _disks;
    private readonly IReadOnlyList<WindowsInstall> _installs;
    private readonly Action _refresh;
    private readonly FileLog _fileLog = new(FileLog.DefaultPath);
    private readonly List<CloneStepRow> _steps = WindowsCloneWorkflow.StepTitles.Select(t => new CloneStepRow(t)).ToList();
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _changed;

    private sealed record SourceChoice(WindowsInstall Install)
    {
        public override string ToString() => Install.Description;
    }

    private sealed record DestinationChoice(DiskInfo Disk)
    {
        public override string ToString() =>
            $"Disk {Disk.Index} — {Disk.Model} · {ByteFormatter.Format((long)Disk.Size)} · {Disk.PartitionStyle}" +
            (Disk.Partitions.Count > 0 ? $" · {Disk.Partitions.Count} partition(s) will be lost" : " · empty");
    }

    /// <param name="refresh">Called when the window closes after the disks were changed, so the main window reloads them.</param>
    public CloneWindowsWindow(IReadOnlyList<DiskInfo> disks, IReadOnlyList<WindowsInstall> installs, Action refresh)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _disks = disks;
        _installs = installs;
        _refresh = refresh;

        StepsList.ItemsSource = _steps;
        CmbSource.ItemsSource = installs.Select(i => new SourceChoice(i)).ToList();
        CmbSource.SelectedIndex = 0;   // fills the destination list and the summary

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            if (_changed) _refresh();
        };
    }

    private WindowsInstall? Source => (CmbSource.SelectedItem as SourceChoice)?.Install;
    private DiskInfo? Destination => (CmbDestination.SelectedItem as DestinationChoice)?.Disk;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        e.Cancel = true;
        MessageBox.Show(this, "The copy is running. Click Cancel first and wait for it to stop.", Title,
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Selection_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && Source is null) return;

        // A change of source rebuilds the destination list (the disk Windows is on cannot be the destination).
        if (ReferenceEquals(sender, CmbSource) && Source is { } source)
        {
            var previous = Destination?.Index;
            var choices = _disks.Where(d => d.Index != source.Disk.Index).Select(d => new DestinationChoice(d)).ToList();
            CmbDestination.ItemsSource = choices;
            CmbDestination.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Disk.Index == previous));
            ChkErase.IsChecked = false;
        }
        if (ReferenceEquals(sender, CmbDestination)) ChkErase.IsChecked = false;

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        ProblemBox.Visibility = Visibility.Collapsed;
        BtnStart.IsEnabled = false;
        if (_running) return;

        if (Source is not { } source)
        {
            TxtPlan.Text = "No installed Windows was found on the disks.";
            return;
        }
        var plan = WindowsCloneLayout.Compute(source);
        TxtPlan.Text =
            $"The copy needs {ByteFormatter.Format(plan.RequiredBytes)}: " +
            $"{(plan.Uefi ? "EFI system partition" : "System Reserved partition")} {ByteFormatter.Format(plan.BootSize)}, " +
            $"Windows {ByteFormatter.Format(plan.WindowsSize)}" +
            (plan.RecoveryOffset is null ? "" : $", recovery {ByteFormatter.Format(plan.RecoverySize)}") +
            (source.Recovery is null ? ". The source has no recovery partition." : ".");

        if (Destination is not { } destination)
        {
            ShowProblem("There is no other disk to copy to. Connect the destination disk and refresh the main window.");
            return;
        }

        TxtErase.Text = $"I understand that disk {destination.Index} ({destination.Model}, " +
                        $"{ByteFormatter.Format((long)destination.Size)}) will be erased.";
        if (WindowsCloneWorkflow.CheckDestination(source, destination) is { } problem)
        {
            ShowProblem(problem);
            return;
        }
        BtnStart.IsEnabled = ChkErase.IsChecked == true;
    }

    private void ShowProblem(string text)
    {
        TxtProblem.Text = text;
        ProblemBox.Visibility = Visibility.Visible;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Source is not { } source || Destination is not { } destination) return;

        var answer = MessageBox.Show(this,
            $"Disk {destination.Index} ({destination.Model}, {ByteFormatter.Format((long)destination.Size)}) will be ERASED and " +
            $"{source.Letter}: will be copied to it.\n\nThis cannot be undone. Continue?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        foreach (var step in _steps) step.State = CloneStepState.Pending;
        TxtLog.Clear();
        _running = true;
        _changed = true;   // the destination is erased from the first step on
        SetControlsEnabled(false);
        _cts = new CancellationTokenSource();

        var log = new Progress<string>(line =>
        {
            TxtLog.AppendText(line + Environment.NewLine);
            TxtLog.ScrollToEnd();
            _fileLog.Log("Clone Windows: " + line.Replace(Environment.NewLine, " | "));
        });

        WindowsCloneResult result;
        try
        {
            var workflow = new WindowsCloneWorkflow(new PowerShellExecutor());
            result = await workflow.RunAsync(source, destination, (i, state) => _steps[i].State = state, log, _cts.Token);
        }
        catch (Exception ex)
        {
            result = new WindowsCloneResult(false, ex.Message, []);
            ((IProgress<string>)log).Report("Failed: " + ex.Message);
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
        }

        SetControlsEnabled(true);
        ChkErase.IsChecked = false;   // a second run asks for the confirmation again
        UpdateSummary();

        string text = result.Message + (result.Warnings.Count > 0 ? "\n\n" + string.Join("\n", result.Warnings) : "");
        MessageBox.Show(this, text, Title, MessageBoxButton.OK,
            result.Success ? (result.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information) : MessageBoxImage.Error);
    }

    private void SetControlsEnabled(bool enabled)
    {
        CmbSource.IsEnabled = CmbDestination.IsEnabled = ChkErase.IsEnabled = BtnClose.IsEnabled = enabled;
        BtnCancel.IsEnabled = !enabled;
        if (!enabled) BtnStart.IsEnabled = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        BtnCancel.IsEnabled = false;
        _cts?.Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
