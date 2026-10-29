using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace LingDongDao;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x11D0;
    private const int ConvertHotkeyId = 0x11D1;
    private const int WmHotkey = 0x0312;

    private AppSettings _settings;
    private AppSettings? _previewSettings;
    private IslandDisplayMode _renderedMode = IslandDisplayMode.Automatic;
    private readonly PowerMonitor _powerMonitor;
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly StatusMonitor _statusMonitor;
    private readonly WeatherMonitor _weatherMonitor;
    private readonly MusicMonitor _musicMonitor;
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _isOpen;
    private NoticeKind? _currentKind;
    private SettingsWindow? _settingsWindow;
    private HwndSource? _hotkeySource;
    private IntPtr _hotkeyHandle;
    private string? _registeredHotkey;
    private IslandNotice? _musicNotice;
    private bool _isPersistentMusicNotice;
    private BitmapSource? _pickerImage;
    private bool _pickerOpen;
    private bool _convertHotkeyRegistered;
    private int _pickerIndex = 1;
    private IntPtr _focusBeforePicker;
    private DateTime _ignoreIslandClickUntilUtc;

    public MainWindow(bool previewOnStart)
    {
        _settings = SettingsStore.Load();
        _settings.StartWithWindows = StartupManager.IsEnabledForCurrentExecutable();
        InitializeComponent();
        ApplyVisualSettings();
        _powerMonitor = new PowerMonitor(() => _settings);
        _powerMonitor.PluggedIn += power =>
        {
            if (_settings.ChargingEnabled) ShowCharging(power);
        };
        _powerMonitor.Unplugged += () =>
        {
            if (CurrentVisualSettings.DisplayMode == IslandDisplayMode.Automatic &&
                _currentKind == NoticeKind.Charging) Collapse();
        };
        _powerMonitor.LowBattery += power => ShowNotice(new IslandNotice(
            NoticeKind.BatteryLow, "电池电量过低", $"剩余 {power.Percent}% · 请连接电源", BatteryPercent: power.Percent));
        _clipboardMonitor = new ClipboardMonitor(this, () => _settings);
        _clipboardMonitor.Copied += ShowNotice;
        _statusMonitor = new StatusMonitor(() => _settings);
        _statusMonitor.Notice += ShowNotice;
        _weatherMonitor = new WeatherMonitor(() => _settings);
        _weatherMonitor.Notice += ShowNotice;
        _weatherMonitor.Start();
        _musicMonitor = new MusicMonitor(() => _settings);
        _musicMonitor.Playing += OnMusicPlaying;
        _musicMonitor.Stopped += OnMusicStopped;
        _musicMonitor.Start();
        _closeTimer.Tick += (_, _) => EndTemporaryNotice();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开控制面板", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var preview = new Forms.ToolStripMenuItem("预览提示");
        preview.DropDownItems.Add("充电", null, (_, _) => Dispatcher.Invoke(() => ShowCharging(_powerMonitor.Current)));
        preview.DropDownItems.Add("Wi‑Fi 已连接", null, (_, _) => Dispatcher.Invoke(() =>
            ShowNotice(new IslandNotice(NoticeKind.WifiConnected, "Wi‑Fi 已连接",
                _statusMonitor.CurrentWifi?.Ssid ?? "示例网络"))));
        preview.DropDownItems.Add("内存占用过高", null, (_, _) => Dispatcher.Invoke(() =>
            ShowNotice(new IslandNotice(NoticeKind.MemoryHigh, "内存占用过高", "当前已使用 88%"))));
        preview.DropDownItems.Add("温度过高", null, (_, _) => Dispatcher.Invoke(() =>
            ShowNotice(new IslandNotice(NoticeKind.TemperatureHigh, "电脑温度过高", "CPU 91°C"))));
        preview.DropDownItems.Add("电池电量过低", null, (_, _) => Dispatcher.Invoke(() =>
            ShowNotice(new IslandNotice(NoticeKind.BatteryLow, "电池电量过低",
                "剩余 15% · 请连接电源", BatteryPercent: 15))));
        preview.DropDownItems.Add("郫都区雷雨", null, (_, _) => Dispatcher.Invoke(() =>
            ShowNotice(new IslandNotice(NoticeKind.Thunderstorm, "未来可能有雷雨", "郫都区 · 明天 16:00 左右"))));
        preview.DropDownItems.Add("音乐卡片", null, (_, _) => Dispatcher.Invoke(() =>
            ShowNotice(new IslandNotice(NoticeKind.Music, "示例歌曲", "网易云音乐 · 示例歌手"))));
        menu.Items.Add(preview);
        menu.Items.Add("查看检测状态", null, (_, _) => Dispatcher.Invoke(() =>
            Forms.MessageBox.Show(_statusMonitor.GetStatusSummary() +
                $"\n郫都区雷雨预报：{_weatherMonitor.StatusText}" +
                $"\n音乐会话：{_musicMonitor.StatusText}", "灵动岛检测状态")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出灵动岛", null, (_, _) => Dispatcher.Invoke(Close));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "灵动岛",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowCharging(_powerMonitor.Current));
        SourceInitialized += (_, _) =>
        {
            _hotkeyHandle = new WindowInteropHelper(this).Handle;
            _hotkeySource = HwndSource.FromHwnd(_hotkeyHandle);
            _hotkeySource?.AddHook(HotkeyWndProc);
            if (!TrySetHotkey(_settings.Hotkey, out _))
            {
                _trayIcon.BalloonTipTitle = "灵动岛快捷键不可用";
                _trayIcon.BalloonTipText = "快捷键被占用，请从托盘打开控制面板修改。";
                _trayIcon.ShowBalloonTip(5000);
            }
            if (!TrySetConversionHotkey(_settings.ImageConversionEnabled, out _))
            {
                _trayIcon.BalloonTipTitle = "图片转换快捷键不可用";
                _trayIcon.BalloonTipText = "Ctrl+0 已被占用，无法打开图片格式选择。";
                _trayIcon.ShowBalloonTip(5000);
            }
        };

        // Show the state once when launched with a battery already charging.
        if (_settings.DisplayMode == IslandDisplayMode.Automatic &&
            (previewOnStart || (_settings.ChargingEnabled && _powerMonitor.Current.HasBattery && _powerMonitor.Current.IsPluggedIn)))
            Loaded += (_, _) => ShowCharging(_powerMonitor.Current);
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, ApplySettings, PreviewSettings, CancelSettingsPreview);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private string? ApplySettings(AppSettings candidate)
    {
        candidate.Normalize();
        string oldHotkey = _settings.Hotkey;
        if (!TrySetHotkey(candidate.Hotkey, out string? error)) return error;
        bool startupChanged = candidate.StartWithWindows != _settings.StartWithWindows;
        if (startupChanged && !StartupManager.TrySetEnabled(candidate.StartWithWindows, out error))
        {
            TrySetHotkey(oldHotkey, out _);
            return error;
        }
        bool conversionChanged = candidate.ImageConversionEnabled != _settings.ImageConversionEnabled;
        if (conversionChanged && !TrySetConversionHotkey(candidate.ImageConversionEnabled, out error))
        {
            if (startupChanged) StartupManager.TrySetEnabled(_settings.StartWithWindows, out _);
            TrySetHotkey(oldHotkey, out _);
            return error;
        }
        try { SettingsStore.Save(candidate); }
        catch (Exception exception)
        {
            if (conversionChanged) TrySetConversionHotkey(_settings.ImageConversionEnabled, out _);
            if (startupChanged) StartupManager.TrySetEnabled(_settings.StartWithWindows, out _);
            TrySetHotkey(oldHotkey, out _);
            return $"保存设置失败：{exception.Message}";
        }

        bool batteryChanged = candidate.BatteryLowEnabled != _settings.BatteryLowEnabled ||
                              candidate.BatteryLowPercent != _settings.BatteryLowPercent;
        _settings = candidate;
        if (!_settings.ImageConversionEnabled && _pickerOpen) ClosePicker();
        _previewSettings = null;
        ApplyVisualSettings();
        if (batteryChanged) _powerMonitor.RefreshSettings();
        _statusMonitor.RefreshSettings();
        _weatherMonitor.RefreshSettings();
        _musicMonitor.RefreshSettings();
        if (candidate.DisplayMode == IslandDisplayMode.Automatic && _musicNotice is not null && !_pickerOpen)
            ShowNoticeCore(_musicNotice, false);
        return null;
    }

    private AppSettings CurrentVisualSettings => _previewSettings ?? _settings;

    private void PreviewSettings(AppSettings candidate)
    {
        candidate.Normalize();
        _previewSettings = candidate;
        ApplyVisualSettings();
    }

    private void CancelSettingsPreview()
    {
        _previewSettings = null;
        ApplyVisualSettings();
        if (_musicNotice is not null && CurrentVisualSettings.DisplayMode == IslandDisplayMode.Automatic && !_pickerOpen)
            ShowNoticeCore(_musicNotice, false);
    }

    private void ApplyVisualSettings()
    {
        AppSettings visual = CurrentVisualSettings;
        if (visual.DisplayMode != IslandDisplayMode.Automatic && _pickerOpen)
        {
            _pickerOpen = false;
            _pickerImage = null;
            FormatPicker.Visibility = Visibility.Collapsed;
            ContentText.Visibility = Visibility.Visible;
        }
        bool modeChanged = _renderedMode != visual.DisplayMode;
        _renderedMode = visual.DisplayMode;
        Width = Math.Max(360, visual.IslandWidth + 40);
        Height = Math.Max(116, visual.IslandHeight + visual.TopOffset + 28);
        Island.Margin = new Thickness(0, visual.TopOffset, 0, 0);
        Island.CornerRadius = new CornerRadius(visual.CornerRadius);
        ExpandedContent.Margin = new Thickness(visual.HorizontalPadding, 0, visual.HorizontalPadding, 0);
        IconColumn.Width = new GridLength(visual.IconSize + 6);
        IconBadge.Width = IconBadge.Height = PreviewCircle.Width = PreviewCircle.Height = visual.IconSize;
        IconBadge.CornerRadius = new CornerRadius(visual.IconSize / 2);
        NoticeIcon.Width = NoticeIcon.Height = visual.IconSize * 0.52;
        TitleText.FontSize = visual.TitleFontSize;
        DetailText.FontSize = visual.DetailFontSize;
        _closeTimer.Interval = TimeSpan.FromSeconds(visual.NoticeSeconds);

        if (visual.DisplayMode == IslandDisplayMode.Hidden)
        {
            _closeTimer.Stop();
            if (_isOpen) Collapse();
            Island.Visibility = Visibility.Collapsed;
        }
        else if (visual.DisplayMode == IslandDisplayMode.Expanded)
        {
            Island.Visibility = Visibility.Visible;
            if (modeChanged || !_isOpen)
                ShowNoticeCore(new IslandNotice(NoticeKind.Charging, "正在充电",
                    "预览 · 电量 72%", BatteryPercent: 72), true);
            Animate(Island, WidthProperty, visual.IslandWidth);
            Animate(Island, HeightProperty, visual.IslandHeight);
            AnimateColor(IslandBrush, ExpandedBackground(visual));
            _closeTimer.Stop();
        }
        else
        {
            Island.Visibility = Visibility.Visible;
            if (modeChanged && _isOpen) Collapse();
            else if (_isOpen)
            {
                Animate(Island, WidthProperty, visual.IslandWidth);
                Animate(Island, HeightProperty, visual.IslandHeight);
                AnimateColor(IslandBrush, ExpandedBackground(visual));
                _closeTimer.Stop();
                if (!_pickerOpen && !_isPersistentMusicNotice) _closeTimer.Start();
            }
            else
            {
                Island.BeginAnimation(WidthProperty, null);
                Island.BeginAnimation(HeightProperty, null);
                Island.Width = visual.LineWidth;
                Island.Height = visual.LineThickness;
            }
        }
        PositionAtTop();
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            OpenSettings();
            handled = true;
        }
        else if (message == WmHotkey && wParam.ToInt32() == ConvertHotkeyId)
        {
            OpenFormatPicker();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private bool TrySetHotkey(string hotkey, out string? error)
    {
        error = null;
        if (_registeredHotkey == hotkey) return true;
        if (_hotkeyHandle == IntPtr.Zero)
        {
            error = "控制面板快捷键尚未初始化。";
            return false;
        }

        string? previous = _registeredHotkey;
        if (previous is not null) UnregisterHotKey(_hotkeyHandle, HotkeyId);
        (uint modifiers, uint key) = GlobalHotkey.Parse(hotkey);
        if (RegisterHotKey(_hotkeyHandle, HotkeyId, modifiers, key))
        {
            _registeredHotkey = hotkey;
            return true;
        }

        _registeredHotkey = null;
        if (previous is not null)
        {
            (modifiers, key) = GlobalHotkey.Parse(previous);
            if (RegisterHotKey(_hotkeyHandle, HotkeyId, modifiers, key)) _registeredHotkey = previous;
        }
        error = $"{hotkey} 已被其他程序占用，请换一个快捷键。";
        return false;
    }

    private bool TrySetConversionHotkey(bool enabled, out string? error)
    {
        error = null;
        if (enabled == _convertHotkeyRegistered) return true;
        if (!enabled)
        {
            UnregisterHotKey(_hotkeyHandle, ConvertHotkeyId);
            _convertHotkeyRegistered = false;
            return true;
        }
        if (RegisterHotKey(_hotkeyHandle, ConvertHotkeyId, 0x4000 | 0x0002, 0x30))
        {
            _convertHotkeyRegistered = true;
            return true;
        }
        error = "Ctrl+0 已被其他程序占用，无法启用图片转换快捷键。";
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    private void PositionAtTop()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(DrawingColor.Transparent);
            using var body = new SolidBrush(DrawingColor.FromArgb(17, 20, 27));
            using var accent = new SolidBrush(DrawingColor.FromArgb(84, 244, 176));
            graphics.FillRoundedRectangle(body, new Rectangle(2, 7, 28, 18), new System.Drawing.Size(9, 9));
            graphics.FillEllipse(accent, 11, 13, 5, 5);
            graphics.FillRoundedRectangle(accent, new Rectangle(19, 14, 7, 3), new System.Drawing.Size(1, 1));
        }
        IntPtr handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private void Island_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_pickerOpen || DateTime.UtcNow < _ignoreIslandClickUntilUtc) return;
        ShowCharging(_powerMonitor.Current);
    }

    private void ShowCharging(PowerSnapshot power)
    {
        ShowNotice(new IslandNotice(NoticeKind.Charging, "正在充电",
            power.HasBattery && power.Percent is int percent ? $"当前电量 {percent}%" : "已连接电源",
            BatteryPercent: power.Percent));
    }

    private void OnMusicPlaying(MusicSnapshot snapshot)
    {
        string detail = string.IsNullOrEmpty(snapshot.Artist)
            ? snapshot.PlayerName : $"{snapshot.PlayerName} · {snapshot.Artist}";
        _musicNotice = new IslandNotice(NoticeKind.Music, snapshot.Title, detail);
        if (!_pickerOpen && (!_isOpen || _currentKind == NoticeKind.Music))
            ShowNoticeCore(_musicNotice, false);
    }

    private void OnMusicStopped()
    {
        _musicNotice = null;
        if (_currentKind == NoticeKind.Music && !_pickerOpen) Collapse();
    }

    private void EndTemporaryNotice()
    {
        if (_pickerOpen) return;
        if (_musicNotice is not null && CurrentVisualSettings.DisplayMode == IslandDisplayMode.Automatic)
            ShowNoticeCore(_musicNotice, false);
        else Collapse();
    }

    private void OpenFormatPicker()
    {
        if (!_settings.ImageConversionEnabled || CurrentVisualSettings.DisplayMode != IslandDisplayMode.Automatic)
            return;
        try
        {
            BitmapSource? image = ClipboardMonitor.ReadCurrentImage();
            if (image is null)
            {
                ShowNotice(new IslandNotice(NoticeKind.ClipboardImage, "先复制图片", "然后按 Ctrl+0 选择格式"));
                return;
            }

            if (!_pickerOpen) _focusBeforePicker = GetForegroundWindow();
            _pickerOpen = false;
            _pickerImage = image;
            _pickerIndex = 1;
            ShowNoticeCore(new IslandNotice(NoticeKind.ClipboardImage, "转换图片", "选择格式", image), true);
            _pickerOpen = true;
            ContentText.Visibility = Visibility.Collapsed;
            FormatPicker.Visibility = Visibility.Visible;
            PickerHint.Text = "选择格式 · A/D 或滚轮";
            UpdateFormatPicker(0);
            _closeTimer.Stop();
            Activate();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_pickerOpen) CurrentFormat.Focus();
            }), DispatcherPriority.Input);
        }
        catch (Exception exception)
        {
            _pickerOpen = false;
            _pickerImage = null;
            ShowNotice(new IslandNotice(NoticeKind.ClipboardImage, "无法读取图片", exception.Message));
        }
    }

    private void UpdateFormatPicker(int direction)
    {
        int count = ClipboardImageConverter.Formats.Length;
        int previous = (_pickerIndex - 1 + count) % count;
        int next = (_pickerIndex + 1) % count;
        PreviousFormat.Tag = previous;
        PreviousFormat.Content = ClipboardImageConverter.Formats[previous];
        CurrentFormat.Tag = _pickerIndex;
        CurrentFormat.Content = ClipboardImageConverter.Formats[_pickerIndex];
        NextFormat.Tag = next;
        NextFormat.Content = ClipboardImageConverter.Formats[next];
        if (direction != 0)
        {
            var duration = TimeSpan.FromMilliseconds(160);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            FormatShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(direction > 0 ? 12 : -12, 0, duration)
                { EasingFunction = easing });
            CurrentFormatScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing });
            CurrentFormatScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing });
        }
    }

    private void MoveFormat(int direction)
    {
        int count = ClipboardImageConverter.Formats.Length;
        _pickerIndex = (_pickerIndex + direction + count) % count;
        UpdateFormatPicker(direction);
    }

    private void Picker_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_pickerOpen) return;
        MoveFormat(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_pickerOpen) return;
        switch (e.Key)
        {
            case Key.A or Key.Left:
                MoveFormat(-1);
                e.Handled = true;
                break;
            case Key.D or Key.Right:
                MoveFormat(1);
                e.Handled = true;
                break;
            case Key.Enter:
                ConvertSelectedFormat(_pickerIndex);
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePicker();
                e.Handled = true;
                break;
        }
    }

    private void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_pickerOpen || sender is not System.Windows.Controls.Button button || button.Tag is not int index)
            return;
        ConvertSelectedFormat(index);
    }

    private void ConvertSelectedFormat(int index)
    {
        if (!_pickerOpen || _pickerImage is null) return;
        string format = ClipboardImageConverter.Formats[index];
        try
        {
            _ignoreIslandClickUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
            ClipboardImageConverter.ConvertAndCopy(_pickerImage, format,
                _clipboardMonitor.SuppressNextUpdate);
            ClosePicker(showPrevious: false);
            ShowNoticeCore(new IslandNotice(NoticeKind.ImageConverted,
                $"{format} 复制成功", $"已转换为 {format} · 可粘贴"), false);
        }
        catch (Exception exception)
        {
            _clipboardMonitor.ClearSuppression();
            PickerHint.Text = $"转换失败：{exception.Message}";
        }
    }

    private void ClosePicker(bool showPrevious = true)
    {
        _pickerOpen = false;
        _pickerImage = null;
        FormatPicker.Visibility = Visibility.Collapsed;
        ContentText.Visibility = Visibility.Visible;
        if (_focusBeforePicker != IntPtr.Zero && _focusBeforePicker != _hotkeyHandle)
            SetForegroundWindow(_focusBeforePicker);
        _focusBeforePicker = IntPtr.Zero;
        if (showPrevious) EndTemporaryNotice();
    }

    private void ShowNotice(IslandNotice notice) => ShowNoticeCore(notice, false);

    private void ShowNoticeCore(IslandNotice notice, bool preview)
    {
        if (_pickerOpen && !preview) return;
        if (!preview && CurrentVisualSettings.DisplayMode != IslandDisplayMode.Automatic) return;
        _closeTimer.Stop();
        _isPersistentMusicNotice = ReferenceEquals(notice, _musicNotice);
        ContentText.Visibility = Visibility.Visible;
        FormatPicker.Visibility = Visibility.Collapsed;
        _currentKind = notice.Kind;
        TitleText.Text = notice.Title;
        DetailText.Text = notice.Detail;
        bool isImage = notice.Image is not null;
        IconBadge.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        PreviewCircle.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        PreviewCircle.Fill = isImage ? new ImageBrush(notice.Image) { Stretch = Stretch.UniformToFill } : null;
        BatteryIndicator.Visibility = notice.Kind is NoticeKind.Charging or NoticeKind.BatteryLow
            ? Visibility.Visible : Visibility.Collapsed;
        BatteryFill.Width = notice.BatteryPercent is int value
            ? Math.Max(3, 18 * value / 100.0) : 12;
        SetIcon(notice.Kind);

        if (!_isOpen)
        {
            _isOpen = true;
            ExpandedContent.Visibility = Visibility.Visible;
            Animate(Island, WidthProperty, CurrentVisualSettings.IslandWidth);
            Animate(Island, HeightProperty, CurrentVisualSettings.IslandHeight);
            AnimateColor(IslandBrush, ExpandedBackground(CurrentVisualSettings));
            AnimateColor(OutlineBrush, MediaColor.FromArgb(58, 255, 255, 255));
            AnimateShadow(0.46);
            Animate(ExpandedContent, OpacityProperty, 1);
        }
        if (!preview && !_isPersistentMusicNotice)
        {
            _closeTimer.Interval = TimeSpan.FromSeconds(CurrentVisualSettings.NoticeSeconds);
            _closeTimer.Start();
        }
    }

    private void Collapse()
    {
        if (_pickerOpen) return;
        _closeTimer.Stop();
        if (!_isOpen) return;
        _isOpen = false;
        _currentKind = null;
        _isPersistentMusicNotice = false;
        Animate(Island, WidthProperty, CurrentVisualSettings.LineWidth);
        Animate(Island, HeightProperty, CurrentVisualSettings.LineThickness);
        AnimateColor(IslandBrush, MediaColor.FromRgb(168, 175, 187));
        AnimateColor(OutlineBrush, MediaColor.FromArgb(0, 255, 255, 255));
        AnimateShadow(0);
        Animate(ExpandedContent, OpacityProperty, 0);
    }

    private void SetIcon(NoticeKind kind)
    {
        MediaColor accent = kind switch
        {
            NoticeKind.Charging or NoticeKind.BatteryLow => MediaColor.FromRgb(84, 244, 176),
            NoticeKind.ClipboardText or NoticeKind.ClipboardImage => MediaColor.FromRgb(128, 184, 255),
            NoticeKind.WifiConnected => MediaColor.FromRgb(103, 219, 244),
            NoticeKind.Thunderstorm => MediaColor.FromRgb(165, 160, 255),
            NoticeKind.Music => MediaColor.FromRgb(255, 119, 148),
            NoticeKind.ImageConverted => MediaColor.FromRgb(84, 244, 176),
            _ => MediaColor.FromRgb(255, 187, 88)
        };
        if (kind == NoticeKind.BatteryLow) accent = MediaColor.FromRgb(255, 174, 88);
        NoticeIcon.Fill = new SolidColorBrush(accent);
        IconBadge.Background = new SolidColorBrush(MediaColor.FromArgb(28, accent.R, accent.G, accent.B));
        NoticeIcon.Data = Geometry.Parse(kind switch
        {
            NoticeKind.Charging or NoticeKind.BatteryLow => "M 13,2 L 5,13 L 11,13 L 9,22 L 20,9 L 14,9 Z",
            NoticeKind.ClipboardText or NoticeKind.ClipboardImage =>
                "M 8,3 H 10 C 10,1 14,1 14,3 H 16 V 6 H 8 Z M 6,5 H 7 V 7 H 17 V 5 H 18 C 19,5 20,6 20,7 V 20 C 20,21 19,22 18,22 H 6 C 5,22 4,21 4,20 V 7 C 4,6 5,5 6,5 Z",
            NoticeKind.WifiConnected or NoticeKind.WifiSlow or NoticeKind.NoInternet =>
                "M 1,8 C 7,2 17,2 23,8 L 21,10 C 16,5 8,5 3,10 Z M 5,12 C 9,8 15,8 19,12 L 17,14 C 14,11 10,11 7,14 Z M 9,17 C 11,15 13,15 15,17 L 12,21 Z",
            NoticeKind.Thunderstorm =>
                "M 6,15 C 3,15 2,13 2,11 C 2,8 4,6 7,6 C 8,3 11,2 14,3 C 17,3 19,5 19,8 C 22,8 23,10 23,12 C 23,14 21,15 19,15 H 14 L 10,21 L 11,16 H 8 L 7,20 L 6,15 Z",
            NoticeKind.Music => "M 10,3 V 16 C 8,15 5,16 5,19 C 5,21 7,22 9,21 C 11,20 12,18 12,16 V 7 L 20,5 V 13 C 18,12 15,13 15,16 C 15,18 17,19 19,18 C 21,17 22,15 22,13 V 2 Z",
            NoticeKind.ImageConverted => "M 4,4 H 20 V 20 H 4 Z M 7,16 L 11,12 L 14,15 L 17,10 L 20,15 V 20 H 4 Z",
            _ => "M 10,2 H 14 L 13,15 H 11 Z M 10,18 H 14 V 22 H 10 Z"
        });
    }

    private void AnimateShadow(double to)
    {
        IslandShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(CurrentVisualSettings.AnimationMilliseconds)));
    }

    private static MediaColor ExpandedBackground(AppSettings settings) => MediaColor.FromArgb(
        (byte)Math.Round(255 * settings.BackgroundOpacityPercent / 100), 9, 11, 17);

    private void AnimateColor(SolidColorBrush brush, MediaColor to)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(to, TimeSpan.FromMilliseconds(CurrentVisualSettings.AnimationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void Animate(DependencyObject target, DependencyProperty property, double to)
    {
        var animation = new DoubleAnimation(to,
            TimeSpan.FromMilliseconds(CurrentVisualSettings.AnimationMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        if (target is UIElement element)
            element.BeginAnimation(property, animation);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer.Stop();
        if (_registeredHotkey is not null) UnregisterHotKey(_hotkeyHandle, HotkeyId);
        if (_convertHotkeyRegistered) UnregisterHotKey(_hotkeyHandle, ConvertHotkeyId);
        _hotkeySource?.RemoveHook(HotkeyWndProc);
        _settingsWindow?.Close();
        _clipboardMonitor.Dispose();
        _statusMonitor.Dispose();
        _weatherMonitor.Dispose();
        _musicMonitor.Dispose();
        _powerMonitor.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Icon?.Dispose();
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}
