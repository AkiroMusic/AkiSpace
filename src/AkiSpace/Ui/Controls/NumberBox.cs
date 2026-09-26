using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;

namespace AkiSpace.Ui.Controls;

/// <summary>
/// Bounded numeric input: fixed increment, arrow buttons, direct text entry with
/// clamping on focus loss. WPF has no built-in NumericUpDown.
/// </summary>
[TemplatePart(Name = PartText, Type = typeof(TextBox))]
[TemplatePart(Name = PartUp, Type = typeof(RepeatButton))]
[TemplatePart(Name = PartDown, Type = typeof(RepeatButton))]
public sealed class NumberBox : Control
{
    private const string PartText = "PART_Text";
    private const string PartUp = "PART_Up";
    private const string PartDown = "PART_Down";

    private TextBox? _text;
    private bool _syncing;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberBox), new PropertyMetadata(0, OnLimitsChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberBox), new PropertyMetadata(100, OnLimitsChanged));

    public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(
        nameof(Increment), typeof(int), typeof(NumberBox), new PropertyMetadata(1));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Increment { get => (int)GetValue(IncrementProperty); set => SetValue(IncrementProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _text = GetTemplateChild(PartText) as TextBox;
        if (_text is not null)
        {
            _text.LostFocus += (_, _) => CommitText();
            _text.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) CommitText();
                if (e.Key == Key.Up) { Step(+1); e.Handled = true; }
                if (e.Key == Key.Down) { Step(-1); e.Handled = true; }
            };
        }
        if (GetTemplateChild(PartUp) is RepeatButton up) up.Click += (_, _) => Step(+1);
        if (GetTemplateChild(PartDown) is RepeatButton down) down.Click += (_, _) => Step(-1);
        SyncText();
    }

    private void Step(int direction)
    {
        Value = Math.Clamp(Value + direction * Increment, Minimum, Maximum);
    }

    private void CommitText()
    {
        if (_text is null) return;
        if (!int.TryParse(_text.Text.Trim(), out var parsed))
        {
            SyncText();
            return;
        }
        Value = Math.Clamp(parsed, Minimum, Maximum);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NumberBox)d).SyncText();

    private static void OnLimitsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumberBox)d;
        // XAML attribute order sets Minimum before Maximum — clamping with a
        // temporarily inverted range (new min > still-default max) would throw.
        if (box.Minimum > box.Maximum) return;
        box.Value = Math.Clamp(box.Value, box.Minimum, box.Maximum);
    }

    private void SyncText()
    {
        if (_text is null || _syncing) return;
        _syncing = true;
        _text.Text = Value.ToString();
        _syncing = false;
    }
}
