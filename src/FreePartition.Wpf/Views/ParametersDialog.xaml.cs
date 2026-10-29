using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace FreePartition.Wpf;

/// <summary>
/// Dialog field: free text, a closed list (<paramref name="Options"/>) or, with <paramref name="IsSize"/>, a size
/// input (MB/GB) whose value is returned in bytes (empty when left blank). A size field also gets a draggable
/// bar: <paramref name="BarTotal"/> is the length of the bar, <paramref name="BarUsed"/> the part already in use
/// and <paramref name="Reversed"/> makes the size the part at the right end (the new partition of a split).
/// </summary>
public sealed record DialogField(string Label, string Value = "", IReadOnlyList<string>? Options = null,
    bool IsSize = false, long MaxBytes = 0, long BarTotal = 0, long BarUsed = 0, bool Reversed = false);

/// <summary>Simple dialog to ask for the parameters of a disk operation.</summary>
public partial class ParametersDialog : Window
{
    private readonly List<FrameworkElement> _controls = new();

    public string[] Values { get; private set; } = [];

    public ParametersDialog(string title, string description, IReadOnlyList<DialogField> fields)
    {
        InitializeComponent();
        Title = title;
        TxtDescription.Text = description;

        foreach (var field in fields)
        {
            FieldsPanel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 6, 0, 2) });

            FrameworkElement control;
            if (field.Options is { Count: > 0 })
            {
                var combo = new ComboBox { ItemsSource = field.Options };
                combo.SelectedItem = field.Options.Contains(field.Value) ? field.Value : field.Options[0];
                control = combo;
            }
            else if (field.IsSize)
            {
                var input = new SizeInput { Maximum = field.MaxBytes };
                control = input;
                if (field.BarTotal > 0)
                {
                    var bar = new PartitionBar
                    {
                        Total = field.BarTotal, Used = field.BarUsed, Reversed = field.Reversed,
                        Minimum = input.Minimum, Maximum = field.MaxBytes > 0 ? field.MaxBytes : field.BarTotal,
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    // Empty input: a split offers the smallest part, a new partition takes all the free space.
                    long empty = field.Reversed ? bar.Minimum : bar.Maximum;
                    bar.Value = empty;
                    bar.ValueChanged += (_, _) => input.Bytes = bar.Value;
                    input.BytesChanged += (_, _) => bar.Value = input.Bytes ?? empty;
                    FieldsPanel.Children.Add(bar);
                }
            }
            else
            {
                control = new TextBox { Text = field.Value, Padding = new Thickness(2, 3, 2, 3) };
            }

            _controls.Add(control);
            FieldsPanel.Children.Add(control);
        }

        Loaded += (_, _) =>
        {
            if (_controls.Count > 0)
            {
                if (_controls[0] is SizeInput size) size.FocusInput();
                else
                {
                    _controls[0].Focus();
                    if (_controls[0] is TextBox box) box.SelectAll();
                }
            }
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Values = _controls
            .Select(c => c switch
            {
                ComboBox combo => combo.SelectedItem as string ?? "",
                SizeInput size => size.Bytes?.ToString(CultureInfo.InvariantCulture) ?? "",
                _ => ((TextBox)c).Text.Trim()
            })
            .ToArray();
        DialogResult = true;
    }
}
