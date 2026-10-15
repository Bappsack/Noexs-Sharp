using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Noexs.Util;

namespace Noexs.UI.Controls;

/// <summary>An editable spinner over an unsigned 64-bit value shown as zero-padded hex.</summary>
public class HexSpinner : ButtonSpinner
{
    private readonly TextBox editor;
    private long value;
    private int size;

    protected override Type StyleKeyOverride => typeof(ButtonSpinner);

    /// <summary>Raised when the committed value changes.</summary>
    public event EventHandler<long>? ValueChanged;

    /// <summary>Raised whenever the editor text changes.</summary>
    public event EventHandler? TextChanged;

    public HexSpinner()
    {
        editor = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = null,
            MinHeight = 0,
            Padding = new Thickness(3, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Content = editor;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Width = 149;
        Height = 25;

        Spin += (_, e) =>
        {
            Commit();
            if (e.Direction == SpinDirection.Increase)
            {
                Increment(1);
            }
            else
            {
                Decrement(1);
            }
        };
        editor.LostFocus += (_, _) => Commit();
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        editor.TextChanged += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty);

        SetSize(8);
    }

    public TextBox Editor => editor;

    /// <summary>Number of hex digits shown (settable from XAML).</summary>
    public int Digits
    {
        get => size;
        set => SetSize(value);
    }

    public void SetSize(int len)
    {
        size = len;
        editor.Text = GetValueAsString();
    }

    /// <summary>
    /// The current value. Text that was typed but not yet committed is honoured, so reading the
    /// value right after typing (e.g. from a button click) behaves like the Java spinner.
    /// </summary>
    public long Value
    {
        get => TryParse(editor.Text, out long v) ? Clamp(v) : value;
        set
        {
            value = Clamp(value);
            long old = this.value;
            this.value = value;
            editor.Text = Format(value);
            if (old != value)
            {
                ValueChanged?.Invoke(this, value);
            }
        }
    }

    public string GetValueAsString()
    {
        return Format(value);
    }

    /// <summary>Lowest accepted value (compared signed); only used by subclasses with a range.</summary>
    public long Minimum { get; set; } = long.MinValue;

    /// <summary>Highest accepted value (compared signed); only used by subclasses with a range.</summary>
    public long Maximum { get; set; } = long.MaxValue;

    private long Clamp(long v)
    {
        return Math.Clamp(v, Minimum, Maximum);
    }

    protected virtual string Format(long v)
    {
        return HexUtils.Pad('0', size, ((ulong)v).ToString("X"));
    }

    protected virtual bool TryParse(string? text, out long v)
    {
        return HexUtils.TryParseHex(text, out v);
    }

    private void Commit()
    {
        // invalid input reverts to the previous value
        Value = TryParse(editor.Text, out long v) ? v : value;
    }

    public void Increment(int steps)
    {
        Value = value + steps;
    }

    public void Decrement(int steps)
    {
        Value = value - steps;
    }
}

/// <summary>Decimal integer spinner with a range (javafx Spinner + IntegerSpinnerValueFactory).</summary>
public class IntSpinner : HexSpinner
{
    public IntSpinner()
    {
        Minimum = 0;
        Maximum = int.MaxValue;
        SetSize(1);
    }

    public int IntValue
    {
        get => (int)Value;
        set => Value = value;
    }

    protected override string Format(long v)
    {
        return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    protected override bool TryParse(string? text, out long v)
    {
        return long.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out v);
    }
}

public class AddressSpinner : HexSpinner
{
    public AddressSpinner()
    {
        SetSize(10);
    }
}

/// <summary>A titled, non-collapsible group box (javafx TitledPane with collapsible=false).</summary>
public class TitledPane : HeaderedContentControl
{
}
