using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FreePartition.Wpf;

/// <summary>Numeric size input with spin buttons and an MB/GB unit selector.</summary>
public sealed class SizeInput : Grid
{
    private const double MiB = 1024.0 * 1024;
    private const double GiB = MiB * 1024;
    private static readonly string[] Units = ["MB", "GB"];

    private readonly TextBox _text = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _unit = new() { ItemsSource = Units, SelectedIndex = 1, Width = 56, Margin = new Thickness(6, 0, 0, 0) };
    private long? _bytes;

    /// <summary>Smallest accepted size in bytes (spin buttons and typed values are clamped to it).</summary>
    public long Minimum { get; set; } = 1L << 20;

    /// <summary>Largest accepted size in bytes (0 = no limit).</summary>
    public long Maximum { get; set; }

    /// <summary>The chosen size in bytes, or null when the box is empty.</summary>
    public long? Bytes
    {
        get => _bytes;
        set
        {
            _bytes = value is { } v ? Clamp(v) : null;
            if (_bytes is { } b) _unit.SelectedIndex = b >= GiB ? 1 : 0;
            ShowValue();
        }
    }

    /// <summary>Raised after the size changes through typing, the spin buttons or the unit selector.</summary>
    public event EventHandler? BytesChanged;

    private double UnitBytes => _unit.SelectedIndex == 1 ? GiB : MiB;
    private double Step => _unit.SelectedIndex == 1 ? 1 : 100;

    public SizeInput()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var up = SpinButton("▲", +1);
        var down = SpinButton("▼", -1);
        var spinner = new Grid { Width = 18, Margin = new Thickness(0, 1, 1, 1), HorizontalAlignment = HorizontalAlignment.Right };
        spinner.RowDefinitions.Add(new RowDefinition());
        spinner.RowDefinitions.Add(new RowDefinition());
        SetRow(down, 1);
        spinner.Children.Add(up);
        spinner.Children.Add(down);

        // The spin buttons sit inside the text box's right edge, like a standard numeric up/down.
        _text.Padding = new Thickness(2, 3, 22, 3);
        var box = new Grid();
        box.Children.Add(_text);
        box.Children.Add(spinner);
        Children.Add(box);

        SetColumn(_unit, 1);
        Children.Add(_unit);

        _text.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down)
            {
                Spin(e.Key == Key.Up ? +1 : -1);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Commit();
            }
        };
        _text.LostFocus += (_, _) => Commit();
        _unit.SelectionChanged += (_, _) =>
        {
            if (_bytes is null) return;
            ShowValue();
        };
        _text.GotKeyboardFocus += (_, _) => _text.SelectAll();
    }

    /// <summary>Focuses the text box and selects its content.</summary>
    public void FocusInput()
    {
        _text.Focus();
        _text.SelectAll();
    }

    private RepeatButton SpinButton(string glyph, int direction)
    {
        var button = new RepeatButton
        {
            Content = glyph,
            FontSize = 6,
            Padding = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Arrow
        };
        button.Click += (_, _) => Spin(direction);
        return button;
    }

    private void Spin(int direction)
    {
        Commit();
        double current = (_bytes ?? Minimum) / UnitBytes;
        SetBytes((long)((current + direction * Step) * UnitBytes));
    }

    private void Commit()
    {
        string text = _text.Text.Trim();
        if (text.Length == 0)
        {
            if (_bytes is not null)
            {
                _bytes = null;
                BytesChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) && value > 0)
            SetBytes((long)(value * (decimal)UnitBytes));
        else
            ShowValue();
    }

    private void SetBytes(long bytes)
    {
        long clamped = Clamp(bytes);
        bool changed = _bytes != clamped;
        _bytes = clamped;
        ShowValue();
        if (changed) BytesChanged?.Invoke(this, EventArgs.Empty);
    }

    private long Clamp(long bytes)
    {
        bytes = Math.Max(bytes, Minimum);
        return Maximum > 0 ? Math.Min(bytes, Maximum) : bytes;
    }

    private void ShowValue() =>
        _text.Text = _bytes is { } b ? (b / UnitBytes).ToString("0.##", CultureInfo.CurrentCulture) : "";
}
