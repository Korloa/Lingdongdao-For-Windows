using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LingDongDao;

public partial class SettingSlider : System.Windows.Controls.UserControl
{
    private bool _initialized;
    private bool _syncing;

    public event EventHandler? ValueChanged;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SettingSlider), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(SettingSlider), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(SettingSlider), new PropertyMetadata(0d, OnRangeChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SettingSlider), new PropertyMetadata(100d, OnRangeChanged));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(SettingSlider), new PropertyMetadata(1d, OnRangeChanged));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals), typeof(int), typeof(SettingSlider), new PropertyMetadata(0, OnRangeChanged));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SettingSlider),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public SettingSlider()
    {
        InitializeComponent();
        _initialized = true;
        SyncControls();
    }

    private static void OnRangeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((SettingSlider)sender).SyncControls();

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (SettingSlider)sender;
        control.SyncControls();
        if (control._initialized) control.ValueChanged?.Invoke(control, EventArgs.Empty);
    }

    private void SyncControls()
    {
        if (!_initialized || _syncing) return;
        _syncing = true;
        try
        {
            double lower = Math.Min(Minimum, Maximum);
            double upper = Math.Max(Minimum, Maximum);
            ValueSlider.Minimum = Math.Min(lower, ValueSlider.Minimum);
            ValueSlider.Maximum = Math.Max(upper, ValueSlider.Maximum);
            ValueSlider.Minimum = lower;
            ValueSlider.Maximum = upper;
            ValueSlider.TickFrequency = Step;
            ValueSlider.SmallChange = Step;
            ValueSlider.LargeChange = Step * 5;
            ValueSlider.Value = Math.Clamp(Value, lower, upper);
            ValueBox.Text = ValueSlider.Value.ToString($"F{Math.Clamp(Decimals, 0, 2)}", CultureInfo.CurrentCulture);
        }
        finally { _syncing = false; }
    }

    private void ValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initialized && !_syncing) SetCurrentValue(ValueProperty, e.NewValue);
    }

    private void CommitText()
    {
        string entered = ValueBox.Text.Trim();
        bool valid = double.TryParse(entered, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) ||
                     double.TryParse(entered, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        if (valid && double.IsFinite(parsed))
        {
            double step = Step > 0 ? Step : 1;
            double snapped = Minimum + Math.Round((Math.Clamp(parsed, Minimum, Maximum) - Minimum) / step) * step;
            SetCurrentValue(ValueProperty, Math.Clamp(snapped, Minimum, Maximum));
        }
        SyncControls();
    }

    private void ValueBox_LostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void ValueBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SyncControls();
            e.Handled = true;
        }
    }
}
