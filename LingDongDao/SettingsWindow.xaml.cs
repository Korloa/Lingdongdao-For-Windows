using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LingDongDao.Perception;

namespace LingDongDao;

public partial class SettingsWindow : Window
{
    private readonly Func<AppSettings, string?> _apply;
    private readonly Action<AppSettings> _preview;
    private readonly Action _cancelPreview;
    private readonly PerceptionService _perception;
    private readonly DispatcherTimer _perceptionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppSettings _working;
    private bool _ready;
    private bool _saved;

    internal SettingsWindow(AppSettings current, Func<AppSettings, string?> apply,
        Action<AppSettings> preview, Action cancelPreview, PerceptionService perception)
    {
        InitializeComponent();
        _apply = apply;
        _preview = preview;
        _cancelPreview = cancelPreview;
        _perception = perception;
        _working = current.Clone();
        DataContext = _working;
        SetHotkeyValues();
        SetAppearanceValues();
        SetModeSelection();
        SetPerceptionValues();
        _perceptionTimer.Tick += (_, _) => RefreshPerceptionStatus();
        Loaded += (_, _) => { _ready = true; _perceptionTimer.Start(); RefreshPerceptionStatus(); };
    }

    private void SetPerceptionValues()
    {
        PerceptionOptions options = _working.Perception;
        PerceptionEnabledCheck.IsChecked = options.Enabled;
        PerceptionPausedCheck.IsChecked = options.Paused;
        CloudUploadCheck.IsChecked = options.CloudEnabled && options.UploadConsent;
        GuiDetectorCheck.IsChecked = options.GuiDetectorEnabled;
        ResourceModeChoice.SelectedIndex = (int)options.Mode;
        PerceptionSampleSlider.SetCurrentValue(SettingSlider.ValueProperty, options.SampleSeconds);
        PerceptionThresholdSlider.SetCurrentValue(SettingSlider.ValueProperty, options.EscalationThreshold);
    }

    private void RefreshPerceptionStatus()
    {
        var score = _perception.LastAssessment;
        PerceptionStatusText.Text = $"状态：{(_perception.IsRunning ? "运行中" : "已停止")}  ·  当前软件：{_perception.CurrentProcess}" +
            $"\n最近事件：{score?.EventType ?? "—"}  ·  本地介入分：{score?.InterventionScore.ToString("0.00") ?? "—"}";
        var (mb, cpu) = _perception.ResourceUsage();
        PerceptionResourceText.Text = $"进程合计 {mb + _perception.WorkerMb:0} MB（主进程 {mb:0} MB）· CPU {cpu:0.0}% · 截图缓存 {_perception.FrameCount} 张 / {_perception.FrameBytes / 1048576.0:0.0} MB" +
            $"\n云端调用 {_perception.CloudCallsToday} 次 · OCR: {_perception.OcrStatus} · GUI: {_perception.GuiStatus}";
        PerceptionLogBox.Text = string.Join(Environment.NewLine, _perception.RecentLogs()
            .TakeLast(35).Select(item => $"{item.Time:HH:mm:ss} [{item.Module}/{item.Level}] {item.Event} {item.Summary}"));
    }

    private void CloudUpload_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _working.Perception.UploadConsent) return;
        MessageBoxResult answer = System.Windows.MessageBox.Show(this,
            "启用后，只有本地评分通过门控时，当前窗口截图、OCR 文字和操作摘要才会发往你配置的云端接口，可能产生 API 费用。是否明确授权截图上传？",
            "授权云端截图上传", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) _working.Perception.UploadConsent = true;
        else CloudUploadCheck.IsChecked = false;
    }

    private void ClearFrames_Click(object sender, RoutedEventArgs e)
    {
        _perception.ClearFrames();
        RefreshPerceptionStatus();
    }

    internal void NavigateToPerception() => Dispatcher.BeginInvoke(
        new Action(() => PerceptionPanel.BringIntoView()));

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
        HoverExpandCheck.IsChecked = _working.HoverExpandEnabled;
        HoverHorizontalSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.HoverHorizontalPadding);
        HoverVerticalSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.HoverVerticalPadding);
        ImageCornerRadiusSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.ImageCornerRadius);
        ImagePreviewSizeSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.ImagePreviewSize);
        FormatVisibleItemsSlider.SetCurrentValue(SettingSlider.ValueProperty, (double)_working.FormatVisibleItems);
        FormatItemWidthSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.FormatItemWidth);
        FormatSpacingSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.FormatSpacing);
        FormatFontSizeSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.FormatFontSize);
        FormatSelectedFontSizeSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.FormatSelectedFontSize);
        FormatScrollSlider.SetCurrentValue(SettingSlider.ValueProperty, _working.FormatScrollMilliseconds);
        WeatherHoursSlider.SetCurrentValue(SettingSlider.ValueProperty, (double)_working.WeatherLookaheadHours);
    }

    private void SetModeSelection()
    {
        AutomaticMode.IsChecked = _working.DisplayMode == IslandDisplayMode.Automatic;
        ExpandedMode.IsChecked = _working.DisplayMode == IslandDisplayMode.Expanded;
        HiddenMode.IsChecked = _working.DisplayMode == IslandDisplayMode.Hidden;
    }

    private void SetHotkeyValues()
    {
        PanelHotkeyBox.Text = _working.Hotkey;
        FormatHotkeyBox.Text = _working.ImageFormatHotkey;
        StyleHotkeyBox.Text = _working.ImageStyleHotkey;
        CompressHotkeyBox.Text = _working.ImageCompressHotkey;
    }

    internal void SyncStartup(bool enabled)
    {
        _working.StartWithWindows = enabled;
        StartupCheck.IsChecked = enabled;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (sender is System.Windows.Controls.TextBox box &&
            GlobalHotkey.TryCapture(key, Keyboard.Modifiers, out string value))
        {
            box.Text = value;
            ErrorText.Text = string.Empty;
        }
        e.Handled = true;
    }

    internal bool TryCaptureNativeHotkey(uint packed)
    {
        if (Keyboard.FocusedElement is not System.Windows.Controls.TextBox box ||
            (box != PanelHotkeyBox && box != FormatHotkeyBox &&
             box != StyleHotkeyBox && box != CompressHotkeyBox)) return false;
        if (!GlobalHotkey.TryFormatNative(packed & 0xFFFF, packed >> 16, out string value)) return false;
        box.Text = value;
        ErrorText.Text = string.Empty;
        return true;
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
        _working.HoverExpandEnabled = HoverExpandCheck.IsChecked == true;
        _working.HoverHorizontalPadding = HoverHorizontalSlider.Value;
        _working.HoverVerticalPadding = HoverVerticalSlider.Value;
        _working.ImageCornerRadius = ImageCornerRadiusSlider.Value;
        _working.ImagePreviewSize = ImagePreviewSizeSlider.Value;
        _working.FormatVisibleItems = (int)FormatVisibleItemsSlider.Value;
        _working.FormatItemWidth = FormatItemWidthSlider.Value;
        _working.FormatSpacing = FormatSpacingSlider.Value;
        _working.FormatFontSize = FormatFontSizeSlider.Value;
        _working.FormatSelectedFontSize = FormatSelectedFontSizeSlider.Value;
        _working.FormatScrollMilliseconds = FormatScrollSlider.Value;
        _preview(_working.Clone());
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        _working = new AppSettings();
        DataContext = _working;
        SetHotkeyValues();
        SetAppearanceValues();
        SetModeSelection();
        SetPerceptionValues();
        _ready = true;
        _preview(_working.Clone());
        ErrorText.Text = "已预览默认值，保存后生效。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _working.Perception.Enabled = PerceptionEnabledCheck.IsChecked == true;
        _working.Perception.Paused = PerceptionPausedCheck.IsChecked == true;
        _working.Perception.CloudEnabled = CloudUploadCheck.IsChecked == true;
        _working.Perception.UploadConsent = _working.Perception.CloudEnabled && _working.Perception.UploadConsent;
        _working.Perception.GuiDetectorEnabled = GuiDetectorCheck.IsChecked == true;
        _working.Perception.Mode = (ResourceMode)Math.Max(0, ResourceModeChoice.SelectedIndex);
        _working.Perception.SampleSeconds = PerceptionSampleSlider.Value;
        _working.Perception.EscalationThreshold = PerceptionThresholdSlider.Value;
        if (_working.Perception.CloudEnabled && !_working.Perception.UploadConsent)
        { ErrorText.Text = "请先明确授权云端截图上传。"; return; }
        _working.WeatherLookaheadHours = (int)WeatherHoursSlider.Value;
        _working.Hotkey = PanelHotkeyBox.Text;
        _working.ImageFormatHotkey = FormatHotkeyBox.Text;
        _working.ImageStyleHotkey = StyleHotkeyBox.Text;
        _working.ImageCompressHotkey = CompressHotkeyBox.Text;
        string? error = _apply(_working.Clone());
        if (error is null)
        {
            _saved = true;
            Close();
        }
        else ErrorText.Text = error;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string anchor }) return;
        if (FindName(anchor) is FrameworkElement section) section.BringIntoView();
    }

    protected override void OnClosed(EventArgs e)
    {
        _perceptionTimer.Stop();
        if (!_saved) _cancelPreview();
        base.OnClosed(e);
    }
}
