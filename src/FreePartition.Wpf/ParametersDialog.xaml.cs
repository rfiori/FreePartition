using System.Windows;
using System.Windows.Controls;

namespace FreePartition.Wpf;

/// <summary>Dialog field: free text or, if <paramref name="Options"/> is set, a closed list.</summary>
public sealed record DialogField(string Label, string Value = "", IReadOnlyList<string>? Options = null);

/// <summary>Simple dialog to ask for the parameters of a disk operation.</summary>
public partial class ParametersDialog : Window
{
    private readonly List<Control> _controls = new();

    public string[] Values { get; private set; } = [];

    public ParametersDialog(string title, string description, IReadOnlyList<DialogField> fields)
    {
        InitializeComponent();
        Title = title;
        TxtDescription.Text = description;

        foreach (var field in fields)
        {
            FieldsPanel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 6, 0, 2) });

            Control control;
            if (field.Options is { Count: > 0 })
            {
                var combo = new ComboBox { ItemsSource = field.Options };
                combo.SelectedItem = field.Options.Contains(field.Value) ? field.Value : field.Options[0];
                control = combo;
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
                _controls[0].Focus();
                if (_controls[0] is TextBox box) box.SelectAll();
            }
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Values = _controls
            .Select(c => c is ComboBox combo ? combo.SelectedItem as string ?? "" : ((TextBox)c).Text.Trim())
            .ToArray();
        DialogResult = true;
    }
}
