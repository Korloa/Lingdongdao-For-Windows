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
    private readonly WeatherMonitor _weather;
    private readonly DispatcherTimer _perceptionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppSettings _working;
    private bool _ready;
    private bool _saved;
    private CloudHistoryWindow? _historyWindow;

    internal SettingsWindow(AppSettings current, Func<AppSettings, string?> apply,
        Action<AppSettings> preview, Action cancelPreview, PerceptionService perception)
        : this(current, apply, preview, cancelPreview, perception, new WeatherMonitor(() => current)) { }

    internal SettingsWindow(AppSettings current, Func<AppSettings, string?> apply,
        Action<AppSettings> preview, Action cancelPreview, PerceptionService perception,
        WeatherMonitor weather)
    {
        InitializeComponent();
        _apply = apply;
        _preview = preview;
        _cancelPreview = cancelPreview;
        _perception = perception;
        _weather = weather;
        _working = current.Clone();
        DataContext = _working;
        SetHotkeyValues();
        SetAppearanceValues();
        SetModeSelection();
        SetPerceptionValues();
        _perceptionTimer.Tick += (_, _) => RefreshPerceptionStatus();
        Loaded += (_, _) => { _ready = true; SelectPage("ModePage");
            _perceptionTimer.Start(); RefreshPerceptionStatus(); };
    }

    private void SetPerceptionValues()
    {
        PerceptionOptions options = _working.Perception;
        PerceptionEnabledCheck.IsChecked = options.Enabled;
        PerceptionPausedCheck.IsChecked = options.Paused;
        CloudUploadCheck.IsChecked = options.CloudEnabled && options.UploadConsent;
        GuiDetectorCheck.IsChecked = options.GuiDetectorEnabled;
        DebugOverlayCheck.IsChecked = options.DebugOverlayEnabled;
        ResourceModeChoice.SelectedIndex = (int)options.Mode;
        PerceptionSampleSlider.SetCurrentValue(SettingSlider.ValueProperty, options.SampleSeconds);
        PerceptionThresholdSlider.SetCurrentValue(SettingSlider.ValueProperty, options.EscalationThreshold);
        AgentMaxActionsSlider.SetCurrentValue(SettingSlider.ValueProperty, (double)options.AgentMaxActions);
        QuickAgentMaxActionsSlider.SetCurrentValue(SettingSlider.ValueProperty,
            (double)options.QuickAgentMaxActions);
        CloudDailyBudgetSlider.SetCurrentValue(SettingSlider.ValueProperty, options.DailyCloudBudgetUsd);
        AgentGoalBox.Text = _working.AgentGoal;
    }

    private void RefreshPerceptionStatus()
    {
        var score = _perception.LastAssessment;
        var recent = DateTimeOffset.UtcNow - _perception.LastMeaningfulTime <= TimeSpan.FromMinutes(2)
            ? _perception.LastMeaningfulAssessment : null;
        PerceptionStatusText.Text = $"状态：{(_perception.IsRunning ? "运行中" : "已停止")}  ·  当前软件：{_perception.CurrentProcess}" +
            $"\n当前判断：{score?.EventType ?? "—"}  ·  介入分：{score?.InterventionScore.ToString("0.00") ?? "—"}" +
            $"\n最近待关注：{recent?.EventType ?? "—"}  ·  介入分：{recent?.InterventionScore.ToString("0.00") ?? "—"}";
        CloudCredentialStatusText.Text = _perception.CloudConfigured
            ? $"{_perception.CloudModel} · 密钥已配置 · {_perception.CloudLastStatus}"
            : $"{_perception.CloudModel} · 未配置密钥";
        var (mb, cpu) = _perception.ResourceUsage();
        PerceptionResourceText.Text = $"进程合计 {mb + _perception.WorkerMb:0} MB（主进程 {mb:0} MB）· CPU {cpu:0.0}% · 截图缓存 {_perception.FrameCount} 张 / {_perception.FrameBytes / 1048576.0:0.0} MB" +
            $"\n云端调用 {_perception.CloudCallsToday} 次 · OCR: {_perception.OcrStatus} · GUI: {_perception.GuiStatus}";
        AgentStatusText.Text = $"Agent：{_perception.AgentStatus} · 已执行 {_perception.AgentActions} 步 · 云端判断 {_perception.AgentCalls} 次" +
            $"\n今日预估费用 ${_perception.EstimatedCloudSpendToday:0.00} / ${_working.Perception.DailyCloudBudgetUsd:0.00}";
        PerceptionLogBox.Text = string.Join(Environment.NewLine, _perception.RecentLogs()
            .TakeLast(35).Select(item => $"{item.Time:HH:mm:ss} [{item.Module}/{item.Level}] {item.Event} {item.Summary}"));
        WeatherCurrentText.Text = _weather.CurrentStatus;
        WeatherForecastText.Text = "雷雨预报：" + _weather.StatusText;
    }

    private void RefreshWeather_Click(object sender, RoutedEventArgs e)
    {
        _weather.RefreshSettings();
        WeatherCurrentText.Text = "正在更新天气…";
    }

    private void CloudUpload_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _working.Perception.UploadConsent) return;
        MessageBoxResult answer = System.Windows.MessageBox.Show(this,
            "启用后，本地评分达到阈值、你主动分析或启动 Agent 时，当前窗口截图、OCR 文字和操作摘要可能发往云端并产生费用。是否明确授权截图上传？",
            "授权云端截图上传", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) _working.Perception.UploadConsent = true;
        else CloudUploadCheck.IsChecked = false;
    }

    private void ClearFrames_Click(object sender, RoutedEventArgs e)
    {
        _perception.ClearFrames();
        RefreshPerceptionStatus();
    }

    private void StopAgent_Click(object sender, RoutedEventArgs e)
    {
        _perception.StopAgent();
        RefreshPerceptionStatus();
    }

    private void OpenCloudHistory_Click(object sender, RoutedEventArgs e)
        => ShowCloudHistory();

    internal void ShowCloudHistory()
    {
        if (_historyWindow is not null) { _historyWindow.Activate(); return; }
        _historyWindow = new CloudHistoryWindow(_perception) { Owner = this };
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    private void SaveDeepSeekKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AgentCredentialStore.Save(DeepSeekKeyBox.Password);
            DeepSeekKeyBox.Clear();
            RefreshPerceptionStatus();
            ErrorText.Text = "密钥已加密保存。";
        }
        catch (Exception exception)
        {
            DeepSeekKeyBox.Clear();
            ErrorText.Text = "密钥保存失败：" + exception.GetType().Name;
        }
    }

    private void RemoveDeepSeekKey_Click(object sender, RoutedEventArgs e)
    {
        AgentCredentialStore.Delete();
        DeepSeekKeyBox.Clear();
        RefreshPerceptionStatus();
        ErrorText.Text = "本机密钥已移除。";
    }

    internal void NavigateToPerception() => Dispatcher.BeginInvoke(
        new Action(() => SelectPage("PerceptionPage")));

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
        ManualAnalysisHotkeyBox.Text = _working.ManualAnalysisHotkey;
        AgentStartHotkeyBox.Text = _working.AgentStartHotkey;
        AgentCommandHotkeyBox.Text = _working.AgentCommandHotkey;
        AgentStopHotkeyBox.Text = _working.AgentStopHotkey;
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
             box != StyleHotkeyBox && box != CompressHotkeyBox &&
             box != ManualAnalysisHotkeyBox && box != AgentStartHotkeyBox &&
             box != AgentCommandHotkeyBox &&
             box != AgentStopHotkeyBox)) return false;
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
        _working.Perception.DebugOverlayEnabled = DebugOverlayCheck.IsChecked == true;
        _working.Perception.Mode = (ResourceMode)Math.Max(0, ResourceModeChoice.SelectedIndex);
        _working.Perception.SampleSeconds = PerceptionSampleSlider.Value;
        _working.Perception.EscalationThreshold = PerceptionThresholdSlider.Value;
        _working.Perception.AgentMaxActions = (int)AgentMaxActionsSlider.Value;
        _working.Perception.QuickAgentMaxActions = (int)QuickAgentMaxActionsSlider.Value;
        _working.Perception.DailyCloudBudgetUsd = CloudDailyBudgetSlider.Value;
        if (_working.Perception.CloudEnabled && !_working.Perception.UploadConsent)
        { ErrorText.Text = "请先明确授权云端截图上传。"; return; }
        _working.WeatherLookaheadHours = (int)WeatherHoursSlider.Value;
        _working.Hotkey = PanelHotkeyBox.Text;
        _working.ImageFormatHotkey = FormatHotkeyBox.Text;
        _working.ImageStyleHotkey = StyleHotkeyBox.Text;
        _working.ImageCompressHotkey = CompressHotkeyBox.Text;
        _working.ManualAnalysisHotkey = ManualAnalysisHotkeyBox.Text;
        _working.AgentStartHotkey = AgentStartHotkeyBox.Text;
        _working.AgentCommandHotkey = AgentCommandHotkeyBox.Text;
        _working.AgentStopHotkey = AgentStopHotkeyBox.Text;
        _working.AgentGoal = AgentGoalBox.Text;
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
        if (sender is System.Windows.Controls.Button { Tag: string page }) SelectPage(page);
    }

    private void SelectPage(string page)
    {
        (StackPanel View, System.Windows.Controls.Button Nav)[] pages =
        [
            (ModePage, ModeNav), (ShapePage, ShapeNav), (ImagePage, ImageNav),
            (AlertsPage, AlertsNav), (PerceptionPage, PerceptionNav), (StartupPage, StartupNav)
        ];
        foreach (var (view, nav) in pages)
        {
            bool selected = view.Name == page;
            view.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            nav.Background = selected ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(39, 64, 79)) :
                System.Windows.Media.Brushes.Transparent;
            nav.Foreground = selected ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(220, 246, 252)) :
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(169, 186, 196));
            if (selected) PageTitleText.Text = nav.Content.ToString();
        }
        SettingsScroll.ScrollToTop();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _perceptionTimer.Stop();
        if (!_saved) _cancelPreview?.Invoke();
        base.OnClosed(e);
    }
}
