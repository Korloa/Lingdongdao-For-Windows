using System.Windows;

namespace LingDongDao;

public partial class SettingsWindow : Window
{
    private readonly Func<AppSettings, string?> _apply;
    private readonly Action<AppSettings> _preview;
    private readonly Action _cancelPreview;
    private AppSettings _working;
    private bool _ready;
    private bool _saved;

    internal SettingsWindow(AppSettings current, Func<AppSettings, string?> apply,
        Action<AppSettings> preview, Action cancelPreview)
    {
        InitializeComponent();
        _apply = apply;
        _preview = preview;
        _cancelPreview = cancelPreview;
        _working = current.Clone();
        DataContext = _working;
        HotkeyCombo.ItemsSource = GlobalHotkey.Options;
        HotkeyCombo.SelectedItem = _working.Hotkey;
        SetAppearanceValues();
        SetModeSelection();
        Loaded += (_, _) => _ready = true;
    }

    private void SetAppearanceValues()
    {
        LineWidthSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.LineWidth);
        LineThicknessSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.LineThickness);
        IslandWidthSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.IslandWidth);
        IslandHeightSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.IslandHeight);
        TopOffsetSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.TopOffset);
        CornerRadiusSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.CornerRadius);
        PaddingSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.HorizontalPadding);
        IconSizeSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.IconSize);
        TitleSizeSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.TitleFontSize);
        DetailSizeSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.DetailFontSize);
        OpacitySlider.SetCurrentValue(SettingSlider.ValueProperty, _working.BackgroundOpacityPercent);
        AnimationSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.AnimationMilliseconds);
        DurationSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.NoticeSeconds);
        WeatherHoursSlider.SetCurrentValue(SettingSlider.ValueProperty, (double)_working.WeatherLookaheadHours);
    }

    private void SetModeSelection()
    {
        AutomaticMode.IsChecked = _working.DisplayMode == IslandDisplayMode.Automatic;
        ExpandedMode.IsChecked = _working.DisplayMode == IslandDisplayMode.Expanded;
        HiddenMode.IsChecked = _working.DisplayMode == IslandDisplayMode.Hidden;
    }

    private void DisplayMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _working.DisplayMode = ExpandedMode.IsChecked == true ? IslandDisplayMode.Expanded
            : HiddenMode.IsChecked == true ? IslandDisplayMode.Hidden : IslandDisplayMode.Automatic;
        PreviewAppearance();
    }

    private void Appearance_Changed(object sender, EventArgs e)
    {
        if (!_ready) return;
        PreviewAppearance();
    }

    private void PreviewAppearance()
    {
        _working.LineWidth = LineWidthSlider.Value;
        _working.LineThickness = LineThicknessSlider.Value;
        _working.IslandWidth = IslandWidthSlider.Value;
        _working.IslandHeight = IslandHeightSlider.Value;
        _working.TopOffset = TopOffsetSlider.Value;
        _working.CornerRadius = CornerRadiusSlider.Value;
        _working.HorizontalPadding = PaddingSlider.Value;
        _working.IconSize = IconSizeSlider.Value;
        _working.TitleFontSize = TitleSizeSlider.Value;
        _working.DetailFontSize = DetailSizeSlider.Value;
        _working.BackgroundOpacityPercent = OpacitySlider.Value;
        _working.AnimationMilliseconds = AnimationSlider.Value;
        _working.NoticeSeconds = DurationSlider.Value;
        _preview(_working.Clone());
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        _working = new AppSettings();
        DataContext = _working;
        HotkeyCombo.SelectedItem = _working.Hotkey;
        SetAppearanceValues();
        SetModeSelection();
        _ready = true;
        _preview(_working.Clone());
        ErrorText.Text = "已预览默认值，保存后生效。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _working.WeatherLookaheadHours = (int)WeatherHoursSlider.Value;
        _working.Hotkey = HotkeyCombo.SelectedItem as string ?? GlobalHotkey.Options[0];
        string? error = _apply(_working.Clone());
        if (error is null)
        {
            _saved = true;
            Close();
        }
        else ErrorText.Text = error;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (!_saved) _cancelPreview();
        base.OnClosed(e);
    }
}
