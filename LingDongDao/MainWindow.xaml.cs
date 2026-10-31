using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Ink;
using Button = System.Windows.Controls.Button;
using Canvas = System.Windows.Controls.Canvas;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace LingDongDao;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x11D0;
    private const int ConvertHotkeyId = 0x11D1;
    private const int StyleHotkeyId = 0x11D2;
    private const int CompressHotkeyId = 0x11D3;
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
    private readonly DispatcherTimer _imageHoverCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _idleHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _startupMenuItem;
    private bool _isOpen;
    private NoticeKind? _currentKind;
    private SettingsWindow? _settingsWindow;
    private HwndSource? _hotkeySource;
    private IntPtr _hotkeyHandle;
    private readonly Dictionary<int, string> _registeredHotkeys = new();
    private IslandNotice? _musicNotice;
    private MusicSnapshot? _musicSnapshot;
    private bool _isPersistentMusicNotice;
    private BitmapSource? _pickerImage;
    private BitmapSource? _imagePreviewSource;
    private string? _imagePreviewPath;
    private double _imagePreviewZoom = 1;
    private bool _imageEditing;
    private BitmapSource? _editImage;
    private byte[]? _editEncodedBytes;
    private string _editExtension = "png";
    private long _editSourceBytes;
    private readonly Stack<EditSnapshot> _editHistory = new();
    private EditTool _editTool;
    private Point? _cropStart;
    private bool _hoverExpanded;
    private bool _pickerOpen;
    private PickerOperation _pickerOperation;
    private int[] _pickerOptionIndices = [];
    private double _pickerItemWidth;
    private int _pickerIndex = 1;
    private Button[] _formatButtons = [];
    private double _formatStep;
    private double _formatRestX;
    private bool _formatMoving;
    private bool _convertAfterMove;
    private int _formatAnimationToken;
    private IntPtr _focusBeforePicker;
    private DateTime _ignoreIslandClickUntilUtc;
    private DateTime _imageZoomHoldUntilUtc;
    private DateTime _hoverExpandHoldUntilUtc;

    private enum EditTool { None, Draw, Crop, Compress, Style }
    private sealed record EditSnapshot(BitmapSource Image, byte[]? EncodedBytes, string Extension, long SourceBytes);

    public MainWindow(bool previewOnStart)
    {
        _settings = SettingsStore.Load();
        _settings.StartWithWindows = StartupManager.IsEnabledForCurrentExecutable();
        InitializeComponent();
        ImageHoverPopup.PlacementTarget = Island;
        ImageStyleChoice.ItemsSource = ClipboardImageEffects.Styles;
        ImageStyleChoice.SelectedIndex = 0;
        ImageEditInk.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = MediaColor.FromRgb(244, 197, 138), Width = 3, Height = 3, FitToCurve = true
        };
        CreateFormatButtons();
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
        _musicMonitor.Updated += OnMusicUpdated;
        _musicMonitor.Stopped += OnMusicStopped;
        _musicMonitor.Start();
        _closeTimer.Tick += (_, _) => EndTemporaryNotice();
        _idleHoverTimer.Tick += (_, _) => PollIdleHover();
        Loaded += (_, _) => _idleHoverTimer.Start();
        _imageHoverCloseTimer.Tick += (_, _) =>
        {
            if (_imageEditing || DateTime.UtcNow < _imageZoomHoldUntilUtc ||
                PreviewImageFrame.IsMouseOver || ImageHoverPanel.IsMouseOver) return;
            HideImagePreview();
            if (_isOpen && !_pickerOpen && !_isPersistentMusicNotice &&
                CurrentVisualSettings.DisplayMode == IslandDisplayMode.Automatic)
                _closeTimer.Start();
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开控制面板", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        _startupMenuItem = new Forms.ToolStripMenuItem("开机自动启动")
        {
            Checked = _settings.StartWithWindows
        };
        _startupMenuItem.Click += (_, _) => Dispatcher.Invoke(ToggleStartupFromTray);
        menu.Items.Add(_startupMenuItem);
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
                $"\n音乐：{_musicMonitor.StatusText}", "灵动岛检测状态")));
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
            if (!TrySetHotkeys(_settings, out string? hotkeyError))
            {
                _trayIcon.BalloonTipTitle = "灵动岛快捷键不可用";
                _trayIcon.BalloonTipText = hotkeyError ?? "请从托盘打开控制面板修改快捷键。";
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

    private void ToggleStartupFromTray()
    {
        bool enabled = !_settings.StartWithWindows;
        if (!StartupManager.TrySetEnabled(enabled, out string? error))
        {
            _trayIcon.BalloonTipText = error ?? "开机启动设置失败";
            _trayIcon.ShowBalloonTip(4000);
            return;
        }
        AppSettings candidate = _settings.Clone();
        candidate.StartWithWindows = enabled;
        try { SettingsStore.Save(candidate); }
        catch (Exception exception)
        {
            StartupManager.TrySetEnabled(_settings.StartWithWindows, out _);
            _trayIcon.BalloonTipText = $"保存设置失败：{exception.Message}";
            _trayIcon.ShowBalloonTip(4000);
            return;
        }
        _settings = candidate;
        _startupMenuItem.Checked = enabled;
        _settingsWindow?.SyncStartup(enabled);
    }

    private string? ApplySettings(AppSettings candidate)
    {
        candidate.Normalize();
        if (!TrySetHotkeys(candidate, out string? error)) return error;
        bool startupChanged = candidate.StartWithWindows != _settings.StartWithWindows;
        if (startupChanged && !StartupManager.TrySetEnabled(candidate.StartWithWindows, out error))
        {
            TrySetHotkeys(_settings, out _);
            return error;
        }
        try { SettingsStore.Save(candidate); }
        catch (Exception exception)
        {
            if (startupChanged) StartupManager.TrySetEnabled(_settings.StartWithWindows, out _);
            TrySetHotkeys(_settings, out _);
            return $"保存设置失败：{exception.Message}";
        }

        bool batteryChanged = candidate.BatteryLowEnabled != _settings.BatteryLowEnabled ||
                              candidate.BatteryLowPercent != _settings.BatteryLowPercent;
        _settings = candidate;
        _startupMenuItem.Checked = candidate.StartWithWindows;
        if (_pickerOpen && !PickerEnabled(_pickerOperation)) ClosePicker();
        _previewSettings = null;
        ApplyVisualSettings();
        if (batteryChanged) _powerMonitor.RefreshSettings();
        _statusMonitor.RefreshSettings();
        _weatherMonitor.RefreshSettings();
        _musicMonitor.RefreshSettings();
        if (candidate.DisplayMode == IslandDisplayMode.Automatic && _musicSnapshot?.IsPlaying == true &&
            _musicNotice is not null && !_pickerOpen)
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
        if (_musicSnapshot?.IsPlaying == true && _musicNotice is not null &&
            CurrentVisualSettings.DisplayMode == IslandDisplayMode.Automatic && !_pickerOpen)
            ShowNoticeCore(_musicNotice, false);
    }

    private void ApplyVisualSettings()
    {
        AppSettings visual = CurrentVisualSettings;
        if (visual.DisplayMode != IslandDisplayMode.Automatic && _pickerOpen)
        {
            CancelFormatMovement();
            _pickerOpen = false;
            _pickerImage = null;
            _focusBeforePicker = IntPtr.Zero;
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
        IconBadge.Width = IconBadge.Height = PreviewImageFrame.Width = PreviewImageFrame.Height = visual.IconSize;
        IconBadge.CornerRadius = new CornerRadius(visual.IconSize / 2);
        PreviewImageFrame.Tag = new CornerRadius(Math.Min(visual.ImageCornerRadius, visual.IconSize / 2));
        IdleHoverTarget.Width = IdleHoverZone.Width(visual);
        IdleHoverTarget.Height = IdleHoverZone.EdgeHeight(visual);
        IdleHoverTarget.Margin = new Thickness(0);
        NoticeIcon.Width = NoticeIcon.Height = visual.IconSize * 0.52;
        TitleText.FontSize = visual.TitleFontSize;
        DetailText.FontSize = visual.DetailFontSize;
        UpdateFormatCarouselLayout();
        _closeTimer.Interval = TimeSpan.FromSeconds(visual.NoticeSeconds);
        if (_hoverExpanded && !visual.HoverExpandEnabled) Collapse();
        IdleHoverTarget.Visibility = visual.DisplayMode == IslandDisplayMode.Automatic &&
                                     visual.HoverExpandEnabled && !_isOpen
            ? Visibility.Visible : Visibility.Collapsed;
        if (ImageHoverPopup.IsOpen) LayoutImageHoverPreview();

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
        if (message == WmHotkey && _settingsWindow?.TryCaptureNativeHotkey(unchecked((uint)lParam.ToInt64())) == true)
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            OpenSettings();
            handled = true;
        }
        else if (message == WmHotkey && wParam.ToInt32() == ConvertHotkeyId)
        {
            OpenPicker(PickerOperation.Format);
            handled = true;
        }
        else if (message == WmHotkey && wParam.ToInt32() == StyleHotkeyId)
        {
            OpenPicker(PickerOperation.Style);
            handled = true;
        }
        else if (message == WmHotkey && wParam.ToInt32() == CompressHotkeyId)
        {
            OpenPicker(PickerOperation.Compress);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static (int Id, string Name, string Gesture, bool Enabled)[] HotkeyBindings(AppSettings settings) =>
    [
        (HotkeyId, "控制面板", settings.Hotkey, true),
        (ConvertHotkeyId, "图片格式", settings.ImageFormatHotkey, settings.ImageConversionEnabled),
        (StyleHotkeyId, "图片风格", settings.ImageStyleHotkey, settings.ImageStyleEnabled),
        (CompressHotkeyId, "图片压缩", settings.ImageCompressHotkey, settings.ImageCompressionEnabled)
    ];

    private bool TrySetHotkeys(AppSettings settings, out string? error)
    {
        error = null;
        if (_hotkeyHandle == IntPtr.Zero)
        {
            error = "快捷键尚未初始化。";
            return false;
        }
        var requested = HotkeyBindings(settings);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in requested)
        {
            if (!GlobalHotkey.TryParse(binding.Gesture, out _, out string canonical))
            {
                error = $"{binding.Name}快捷键无效，请按 Ctrl、Alt、Shift 或 Win 加字母、数字、方向键或 F1–F12。";
                return false;
            }
            if (!seen.Add(canonical))
            {
                error = $"{canonical} 被多个功能使用，请修改其中一项。";
                return false;
            }
        }
        var previous = _registeredHotkeys.ToArray();
        foreach (var (id, _) in previous) UnregisterHotKey(_hotkeyHandle, id);
        _registeredHotkeys.Clear();
        foreach (var binding in requested.Where(item => item.Enabled))
        {
            GlobalHotkey.TryParse(binding.Gesture, out var chord, out string canonical);
            if (RegisterHotKey(_hotkeyHandle, binding.Id, chord.Modifiers, chord.Key))
            {
                _registeredHotkeys[binding.Id] = canonical;
                continue;
            }
            error = $"{canonical} 已被其他程序占用，请换一个快捷键。";
            foreach (int id in _registeredHotkeys.Keys) UnregisterHotKey(_hotkeyHandle, id);
            _registeredHotkeys.Clear();
            foreach (var (id, gesture) in previous)
            {
                if (!GlobalHotkey.TryParse(gesture, out var oldChord, out _)) continue;
                if (RegisterHotKey(_hotkeyHandle, id, oldChord.Modifiers, oldChord.Key))
                    _registeredHotkeys[id] = gesture;
            }
            return false;
        }
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

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
        if (_currentKind == NoticeKind.Music) return;
        ShowCharging(_powerMonitor.Current);
    }

    private void IdleHoverTarget_MouseEnter(object sender, MouseEventArgs e)
        => ExpandFromIdleHover();

    private void PollIdleHover()
    {
        if (_pickerOpen || _imageEditing || !CurrentVisualSettings.HoverExpandEnabled ||
            CurrentVisualSettings.DisplayMode != IslandDisplayMode.Automatic) return;
        if (_hoverExpanded)
        {
            if (DateTime.UtcNow >= _hoverExpandHoldUntilUtc &&
                !IsCursorInIdleHoverZone() && !Island.IsMouseOver) Collapse();
            return;
        }
        if (!_isOpen && IsCursorInIdleHoverZone()) ExpandFromIdleHover();
    }

    private bool IsCursorInIdleHoverZone()
    {
        if (!GetCursorPos(out NativePoint cursor)) return false;
        Point local = PointFromScreen(new Point(cursor.X, cursor.Y));
        return IdleHoverZone.Contains(CurrentVisualSettings,
            ActualWidth > 0 ? ActualWidth : Width, local.X, local.Y);
    }

    private void ExpandFromIdleHover()
    {
        if (_isOpen || _pickerOpen || !CurrentVisualSettings.HoverExpandEnabled ||
            CurrentVisualSettings.DisplayMode != IslandDisplayMode.Automatic) return;

        PowerSnapshot power = _powerMonitor.Current;
        IslandNotice notice = _musicSnapshot is { IsPlaying: false } && _musicNotice is not null
            ? _musicNotice : power.HasBattery
            ? new IslandNotice(NoticeKind.Charging,
                power.IsPluggedIn ? "正在充电" : "电池状态",
                power.Percent is int percent ? $"当前电量 {percent}%" : "电量暂不可用",
                BatteryPercent: power.Percent)
            : new IslandNotice(NoticeKind.Idle, "灵动岛", "Ctrl+Alt+D 打开控制面板");
        ShowNoticeCore(notice, true);
        _hoverExpanded = true;
        _hoverExpandHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(
            CurrentVisualSettings.AnimationMilliseconds + 150);
    }

    private void IdleHoverTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!_pickerOpen) ShowCharging(_powerMonitor.Current);
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverExpanded && !_pickerOpen && !_imageEditing &&
            DateTime.UtcNow >= _hoverExpandHoldUntilUtc && !IsCursorInIdleHoverZone()) Collapse();
    }

    private void PreviewImageFrame_MouseEnter(object sender, MouseEventArgs e)
    {
        OpenImagePreview();
    }

    private void PreviewImageFrame_Click(object sender, RoutedEventArgs e)
    {
        OpenImagePreview();
        e.Handled = true;
    }

    private void OpenImagePreview()
    {
        if (!_isOpen || PreviewImageFrame.Visibility != Visibility.Visible) return;
        if (_imageEditing) return;
        _imageHoverCloseTimer.Stop();
        try
        {
            if (_imagePreviewPath is not null && _imagePreviewSource is null)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(_imagePreviewPath);
                image.EndInit();
                image.Freeze();
                _imagePreviewSource = image;
            }
            _imagePreviewSource ??= PreviewImageBrush.ImageSource as BitmapSource;
            if (_imagePreviewSource is null) return;
            ImageHoverFull.Source = _imagePreviewSource;
            LayoutImageHoverPreview();
            ImageHoverPopup.IsOpen = true;
            _closeTimer.Stop();
        }
        catch (Exception)
        {
            _imagePreviewSource = PreviewImageBrush.ImageSource as BitmapSource;
            if (_imagePreviewSource is null) return;
            ImageHoverFull.Source = _imagePreviewSource;
            LayoutImageHoverPreview();
            ImageHoverPopup.IsOpen = true;
            _closeTimer.Stop();
        }
    }

    private void PreviewImageFrame_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ImageHoverPopup.IsOpen && !_imageEditing) _imageHoverCloseTimer.Start();
    }

    private void ImageHoverPanel_MouseEnter(object sender, MouseEventArgs e) => _imageHoverCloseTimer.Stop();

    private void ImageHoverPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ImageHoverPopup.IsOpen && !_imageEditing) _imageHoverCloseTimer.Start();
    }

    private void ImageHoverPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!ImageHoverPopup.IsOpen) return;
        try
        {
            CommitInkStrokes();
            _imagePreviewZoom = Math.Clamp(_imagePreviewZoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.5, 4);
            _imageZoomHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            LayoutImageHoverPreview();
        }
        catch (Exception exception) { if (_imageEditing) ImageEditStatus.Text = exception.Message; }
        e.Handled = true;
    }

    private void LayoutImageHoverPreview()
    {
        BitmapSource? source = _imageEditing ? _editImage : _imagePreviewSource;
        if (source is null) return;
        double maxSide = CurrentVisualSettings.ImagePreviewSize;
        double scale = Math.Min(maxSide / source.PixelWidth, maxSide / source.PixelHeight) * _imagePreviewZoom;
        double width = Math.Max(1, Math.Round(source.PixelWidth * scale));
        double height = Math.Max(1, Math.Round(source.PixelHeight * scale));
        ImageHoverFull.Width = ImageStage.Width = ImageEditInk.Width = ImageCropOverlay.Width = width;
        ImageHoverFull.Height = ImageStage.Height = ImageEditInk.Height = ImageCropOverlay.Height = height;
        ImageHoverScroll.MaxWidth = Math.Max(200, SystemParameters.PrimaryScreenWidth - 50);
        ImageHoverScroll.MaxHeight = Math.Max(140,
            SystemParameters.PrimaryScreenHeight - Island.ActualHeight - (_imageEditing ? 160 : 45));
        double panelWidth = Math.Min(width, ImageHoverScroll.MaxWidth);
        if (_imageEditing) panelWidth = Math.Max(panelWidth, 380);
        ImageHoverPopup.HorizontalOffset = (Island.ActualWidth - panelWidth - 20) / 2;
    }

    private void HideImagePreview()
    {
        _imageHoverCloseTimer.Stop();
        DiscardImageEdit();
        ImageHoverPopup.IsOpen = false;
        ImageHoverFull.Source = null;
        _imagePreviewZoom = 1;
    }

    private void ImageEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePreviewSource is null || _imageEditing) return;
        try
        {
            _editImage = _imagePreviewSource;
            _editEncodedBytes = null;
            _editExtension = "png";
            _editSourceBytes = _imagePreviewPath is not null && File.Exists(_imagePreviewPath)
                ? new FileInfo(_imagePreviewPath).Length
                : ClipboardImageConverter.EncodeImage(_editImage, "PNG").LongLength;
            _editHistory.Clear();
            _imageEditing = true;
            _editTool = EditTool.None;
            ImageEditButton.Visibility = Visibility.Collapsed;
            ImageEditorBar.Visibility = Visibility.Visible;
            ImageEditStatus.Text = "滚轮缩放 · 选择工具开始编辑";
            _closeTimer.Stop();
            _imageHoverCloseTimer.Stop();
            LayoutImageHoverPreview();
        }
        catch (Exception exception)
        {
            _editImage = null;
            ShowNotice(new IslandNotice(NoticeKind.ClipboardImage, "图片无法编辑", exception.Message));
        }
    }

    private void ImageDraw_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SelectEditTool(EditTool.Draw);
            ImageEditStatus.Text = "在图片上拖动鼠标画画";
        }
        catch (Exception exception) { ImageEditStatus.Text = exception.Message; }
    }

    private void ImageCrop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SelectEditTool(EditTool.Crop);
            ImageEditStatus.Text = "在图片上拖动框选截图区域";
        }
        catch (Exception exception) { ImageEditStatus.Text = exception.Message; }
    }

    private void ImageCompress_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SelectEditTool(EditTool.Compress);
            RefreshEditSizeChoices();
        }
        catch (Exception exception) { ImageEditStatus.Text = exception.Message; }
    }

    private void ImageStyle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SelectEditTool(EditTool.Style);
            ImageStyleChoice.SelectedIndex = 0;
            ImageEditStatus.Text = "选择风格后点击应用";
        }
        catch (Exception exception) { ImageEditStatus.Text = exception.Message; }
    }

    private void SelectEditTool(EditTool tool, bool commitInk = true)
    {
        if (!_imageEditing) return;
        if (commitInk && _editTool == EditTool.Draw) CommitInkStrokes();
        _editTool = tool;
        ImageEditInk.Visibility = tool == EditTool.Draw ? Visibility.Visible : Visibility.Collapsed;
        ImageCropOverlay.Visibility = tool == EditTool.Crop ? Visibility.Visible : Visibility.Collapsed;
        ImageCropSelection.Visibility = Visibility.Collapsed;
        ImageEditOptions.Visibility = tool is EditTool.Style or EditTool.Compress
            ? Visibility.Visible : Visibility.Collapsed;
        ImageStyleChoice.Visibility = tool == EditTool.Style ? Visibility.Visible : Visibility.Collapsed;
        ImageSizeChoice.Visibility = tool == EditTool.Compress ? Visibility.Visible : Visibility.Collapsed;
        Button[] toolButtons = [ImageDrawButton, ImageCropButton, ImageCompressButton, ImageStyleButton];
        foreach (Button button in toolButtons)
        {
            button.ClearValue(Button.BackgroundProperty);
            button.ClearValue(Button.ForegroundProperty);
        }
        if (tool != EditTool.None)
        {
            Button selected = toolButtons[(int)tool - 1];
            selected.Background = new SolidColorBrush(MediaColor.FromRgb(244, 197, 138));
            selected.Foreground = new SolidColorBrush(MediaColor.FromRgb(23, 32, 33));
        }
    }

    private void CommitInkStrokes()
    {
        if (!_imageEditing || _editImage is null || ImageEditInk.Strokes.Count == 0) return;
        BitmapSource result = ImageEditOperations.Draw(_editImage, ImageEditInk.Strokes,
            ImageStage.Width, ImageStage.Height);
        PushEditUndo();
        _editImage = result;
        _editEncodedBytes = null;
        _editExtension = "png";
        _editSourceBytes = -1;
        ImageHoverFull.Source = result;
        ImageEditInk.Strokes.Clear();
    }

    private void PushEditUndo()
    {
        if (_editImage is not null)
            _editHistory.Push(new EditSnapshot(_editImage, _editEncodedBytes, _editExtension, _editSourceBytes));
    }

    private void ImageCrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_imageEditing || _editTool != EditTool.Crop) return;
        _cropStart = e.GetPosition(ImageCropOverlay);
        Canvas.SetLeft(ImageCropSelection, _cropStart.Value.X);
        Canvas.SetTop(ImageCropSelection, _cropStart.Value.Y);
        ImageCropSelection.Width = ImageCropSelection.Height = 0;
        ImageCropSelection.Visibility = Visibility.Visible;
        ImageCropOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void ImageCrop_MouseMove(object sender, MouseEventArgs e)
    {
        if (_cropStart is null) return;
        Point end = e.GetPosition(ImageCropOverlay);
        double x = Math.Clamp(end.X, 0, ImageStage.Width);
        double y = Math.Clamp(end.Y, 0, ImageStage.Height);
        Canvas.SetLeft(ImageCropSelection, Math.Min(x, _cropStart.Value.X));
        Canvas.SetTop(ImageCropSelection, Math.Min(y, _cropStart.Value.Y));
        ImageCropSelection.Width = Math.Abs(x - _cropStart.Value.X);
        ImageCropSelection.Height = Math.Abs(y - _cropStart.Value.Y);
        e.Handled = true;
    }

    private void ImageCrop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropStart is null || _editImage is null) return;
        ImageCropOverlay.ReleaseMouseCapture();
        Rect selection = new(Canvas.GetLeft(ImageCropSelection), Canvas.GetTop(ImageCropSelection),
            ImageCropSelection.Width, ImageCropSelection.Height);
        _cropStart = null;
        ImageCropSelection.Visibility = Visibility.Collapsed;
        if (selection.Width < 6 || selection.Height < 6)
        {
            ImageEditStatus.Text = "框选区域太小，请重新拖动";
            return;
        }
        try
        {
            BitmapSource cropped = ImageEditOperations.Crop(_editImage, selection,
                ImageStage.Width, ImageStage.Height);
            PushEditUndo();
            _editImage = cropped;
            _editEncodedBytes = null;
            _editExtension = "png";
            _editSourceBytes = -1;
            ImageHoverFull.Source = cropped;
            SelectEditTool(EditTool.None, false);
            LayoutImageHoverPreview();
            ImageEditStatus.Text = $"已截取 {cropped.PixelWidth} × {cropped.PixelHeight} · 可撤回";
        }
        catch (Exception exception) { ImageEditStatus.Text = exception.Message; }
        e.Handled = true;
    }

    private void RefreshEditSizeChoices()
    {
        if (_editImage is null) return;
        if (_editSourceBytes < 0)
            _editSourceBytes = ClipboardImageConverter.EncodeImage(_editImage, "PNG").LongLength;
        ImageSizeChoice.Items.Clear();
        foreach (int index in ClipboardImageEffects.SmallerSizeOptions(_editSourceBytes))
            ImageSizeChoice.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = ClipboardImageEffects.SizeDescriptions[index], Tag = index
            });
        if (ImageSizeChoice.Items.Count == 0)
        {
            SelectEditTool(EditTool.None, false);
            ImageEditStatus.Text = "当前图片已小于最小压缩档位";
        }
        else
        {
            ImageSizeChoice.SelectedIndex = 0;
            ImageEditStatus.Text = $"原图约 {_editSourceBytes / 1024.0:0.#} KB · 选择更小档位";
        }
    }

    private void ImageApplyOption_Click(object sender, RoutedEventArgs e)
    {
        if (_editImage is null) return;
        try
        {
            if (_editTool == EditTool.Style)
            {
                int index = ImageStyleChoice.SelectedIndex;
                if (index < 0) return;
                BitmapSource styled = ClipboardImageEffects.ApplyStyle(_editImage, index);
                PushEditUndo();
                _editImage = styled;
                _editEncodedBytes = null;
                _editExtension = "png";
                _editSourceBytes = -1;
                ImageEditStatus.Text = $"已应用{ClipboardImageEffects.Styles[index]}风格 · 可撤回";
            }
            else if (_editTool == EditTool.Compress &&
                     ImageSizeChoice.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: int index })
            {
                (byte[] bytes, _) = ClipboardImageEffects.Compress(_editImage, index);
                BitmapSource compressed = ImageEditOperations.DecodeJpeg(bytes);
                PushEditUndo();
                _editImage = compressed;
                _editEncodedBytes = bytes;
                _editExtension = "jpg";
                _editSourceBytes = bytes.LongLength;
                ImageEditStatus.Text = $"已压缩到 {bytes.Length / 1024.0:0.#} KB · 可撤回";
                SelectEditTool(EditTool.None, false);
            }
            else return;
            ImageHoverFull.Source = _editImage;
            LayoutImageHoverPreview();
        }
        catch (Exception exception) { ImageEditStatus.Text = $"处理失败：{exception.Message}"; }
    }

    private void ImageUndo_Click(object sender, RoutedEventArgs e)
    {
        if (ImageEditInk.Strokes.Count > 0)
        {
            ImageEditInk.Strokes.RemoveAt(ImageEditInk.Strokes.Count - 1);
            ImageEditStatus.Text = "已撤回上一笔";
            return;
        }
        if (_editHistory.Count == 0)
        {
            ImageEditStatus.Text = "没有可撤回的操作";
            return;
        }
        SelectEditTool(EditTool.None, false);
        EditSnapshot previous = _editHistory.Pop();
        _editImage = previous.Image;
        _editEncodedBytes = previous.EncodedBytes;
        _editExtension = previous.Extension;
        _editSourceBytes = previous.SourceBytes;
        ImageHoverFull.Source = _editImage;
        LayoutImageHoverPreview();
        ImageEditStatus.Text = "已撤回上一步";
    }

    private void ImageCancel_Click(object sender, RoutedEventArgs e)
    {
        DiscardImageEdit();
        LayoutImageHoverPreview();
        if (!ImageHoverPanel.IsMouseOver) _imageHoverCloseTimer.Start();
    }

    private void ImageConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_editImage is null) return;
        try
        {
            CommitInkStrokes();
            BitmapSource result = _editImage!;
            byte[] bytes = _editEncodedBytes ?? ClipboardImageConverter.EncodeImage(result, "PNG");
            string extension = _editEncodedBytes is null ? "png" : _editExtension;
            ClipboardImageConverter.CopyEncoded(result, extension, bytes,
                _clipboardMonitor.SuppressNextUpdate);
            HideImagePreview();
            ShowNoticeCore(new IslandNotice(NoticeKind.ImageConverted,
                "图片编辑完成", $"已复制 {extension.ToUpperInvariant()} · 可粘贴"), false);
        }
        catch (Exception exception)
        {
            _clipboardMonitor.ClearSuppression();
            ImageEditStatus.Text = $"保存失败：{exception.Message}";
        }
    }

    private void DiscardImageEdit()
    {
        if (!_imageEditing) return;
        _imageEditing = false;
        _editImage = null;
        _editEncodedBytes = null;
        _editHistory.Clear();
        _cropStart = null;
        _editTool = EditTool.None;
        ImageEditInk.Strokes.Clear();
        ImageEditInk.Visibility = Visibility.Collapsed;
        ImageCropOverlay.Visibility = Visibility.Collapsed;
        ImageCropSelection.Visibility = Visibility.Collapsed;
        ImageEditOptions.Visibility = Visibility.Collapsed;
        ImageEditorBar.Visibility = Visibility.Collapsed;
        ImageEditButton.Visibility = Visibility.Visible;
        foreach (Button button in new[] { ImageDrawButton, ImageCropButton, ImageCompressButton, ImageStyleButton })
        {
            button.ClearValue(Button.BackgroundProperty);
            button.ClearValue(Button.ForegroundProperty);
        }
        ImageHoverFull.Source = _imagePreviewSource;
    }

    private void ShowCharging(PowerSnapshot power)
    {
        ShowNotice(new IslandNotice(NoticeKind.Charging, "正在充电",
            power.HasBattery && power.Percent is int percent ? $"当前电量 {percent}%" : "已连接电源",
            BatteryPercent: power.Percent));
    }

    private void OnMusicUpdated(MusicSnapshot snapshot)
    {
        MusicSnapshot? previous = _musicSnapshot;
        bool changed = previous is null || previous.Source != snapshot.Source ||
                       previous.Title != snapshot.Title || previous.Artist != snapshot.Artist ||
                       previous.IsPlaying != snapshot.IsPlaying;
        _musicSnapshot = snapshot;
        UpdateMusicProgress();
        if (!changed) return;
        string detail = string.IsNullOrEmpty(snapshot.Artist)
            ? snapshot.PlayerName : $"{snapshot.PlayerName} · {snapshot.Artist}";
        _musicNotice = new IslandNotice(NoticeKind.Music, snapshot.Title, detail);
        if (!_pickerOpen && !_imageEditing && (snapshot.IsPlaying || previous?.IsPlaying == true) &&
            (!_isOpen || _currentKind == NoticeKind.Music))
            ShowNoticeCore(_musicNotice, false);
    }

    private void OnMusicStopped()
    {
        _musicSnapshot = null;
        _musicNotice = null;
        if (_currentKind == NoticeKind.Music && !_pickerOpen) Collapse();
    }

    private void EndTemporaryNotice()
    {
        if (_pickerOpen || _imageEditing) return;
        if (_musicSnapshot?.IsPlaying == true && _musicNotice is not null &&
            CurrentVisualSettings.DisplayMode == IslandDisplayMode.Automatic)
            ShowNoticeCore(_musicNotice, false);
        else Collapse();
    }

    private void UpdateMusicProgress()
    {
        if (_musicSnapshot is not { } snapshot) return;
        MusicToggleButton.Content = snapshot.IsPlaying ? "Ⅱ" : "▶";
        System.Windows.Automation.AutomationProperties.SetName(MusicToggleButton,
            snapshot.IsPlaying ? "暂停" : "播放");
        MusicTimeText.Text = snapshot.Duration > TimeSpan.Zero
            ? $"{FormatMusicTime(snapshot.Position)} / {FormatMusicTime(snapshot.Duration)}" : "— / —";
        double fraction = snapshot.Duration > TimeSpan.Zero
            ? Math.Clamp(snapshot.Position.TotalSeconds / snapshot.Duration.TotalSeconds, 0, 1) : 0;
        MusicProgressFill.Width = MusicProgressTrack.ActualWidth * fraction;
        MusicProgressTrack.Cursor = _musicMonitor.CanSeek ? System.Windows.Input.Cursors.Hand :
            System.Windows.Input.Cursors.Arrow;
        MusicProgressTrack.ToolTip = _musicMonitor.CanSeek ? "点击跳转播放位置" : "当前播放器仅提供进度显示";
    }

    private static string FormatMusicTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private void MusicProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateMusicProgress();

    private async void MusicProgressTrack_MouseLeftButtonDown(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_musicMonitor.CanSeek || _musicSnapshot is not { Duration: var duration } ||
            duration <= TimeSpan.Zero || MusicProgressTrack.ActualWidth <= 0) return;
        double fraction = Math.Clamp(e.GetPosition(MusicProgressTrack).X /
            MusicProgressTrack.ActualWidth, 0, 1);
        await _musicMonitor.SeekAsync(TimeSpan.FromTicks((long)(duration.Ticks * fraction)));
    }

    private async void MusicPrevious_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RunMusicControl(MusicControl.Previous);
    }

    private async void MusicToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RunMusicControl(MusicControl.Toggle);
    }

    private async void MusicNext_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RunMusicControl(MusicControl.Next);
    }

    private async Task RunMusicControl(MusicControl command)
    {
        if (!await _musicMonitor.ControlAsync(command) && _currentKind == NoticeKind.Music)
            DetailText.Text = "播放器未响应，请检查播放器快捷键";
    }

    private enum PickerOperation { Format, Style, Compress }

    private string[] PickerItems => _pickerOperation switch
    {
        PickerOperation.Style => ClipboardImageEffects.Styles,
        PickerOperation.Compress => _pickerOptionIndices.Select(index => ClipboardImageEffects.Sizes[index]).ToArray(),
        _ => ClipboardImageConverter.Formats
    };

    private bool PickerEnabled(PickerOperation operation) => operation switch
    {
        PickerOperation.Style => _settings.ImageStyleEnabled,
        PickerOperation.Compress => _settings.ImageCompressionEnabled,
        _ => _settings.ImageConversionEnabled
    };

    private void OpenPicker(PickerOperation operation)
    {
        if (_imageEditing || !PickerEnabled(operation) || CurrentVisualSettings.DisplayMode != IslandDisplayMode.Automatic)
            return;
        try
        {
            BitmapSource? image = ClipboardMonitor.ReadCurrentImage();
            if (image is null)
            {
                if (_pickerOpen) ClosePicker(showPrevious: false);
                ShowNotice(new IslandNotice(NoticeKind.ClipboardImage, "先复制图片", $"然后按 Ctrl+{(int)operation} 打开选择条"));
                return;
            }

            if (operation == PickerOperation.Compress)
            {
                long originalBytes = ClipboardMonitor.GetImageByteLength(image);
                _pickerOptionIndices = ClipboardImageEffects.SmallerSizeOptions(originalBytes);
                if (_pickerOptionIndices.Length == 0)
                {
                    if (_pickerOpen) ClosePicker(showPrevious: false);
                    ShowNotice(new IslandNotice(NoticeKind.ClipboardImage,
                        "图片已经很小", "没有比当前图片更小的压缩档位"));
                    return;
                }
            }

            if (!_pickerOpen) _focusBeforePicker = GetForegroundWindow();
            CancelFormatMovement();
            _pickerOpen = false;
            _pickerImage = image;
            _pickerOperation = operation;
            _pickerIndex = operation == PickerOperation.Format ? 1 : 0;
            string title = operation switch
            {
                PickerOperation.Style => "调整风格",
                PickerOperation.Compress => "压缩图片",
                _ => "转换图片"
            };
            ShowNoticeCore(new IslandNotice(NoticeKind.ClipboardImage, title, "选择图片操作", image), true);
            _pickerOpen = true;
            ContentText.Visibility = Visibility.Collapsed;
            FormatPicker.Visibility = Visibility.Visible;
            PickerHint.Text = operation switch
            {
                PickerOperation.Style => "调整风格 · A/D 或滚轮",
                PickerOperation.Compress => "压缩大小 · A/D 或滚轮",
                _ => "选择格式 · A/D 或滚轮"
            };
            UpdateFormatCarouselLayout();
            RenderFormatButtons();
            SetFormatRestPosition();
            _closeTimer.Stop();
            Activate();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_pickerOpen) _formatButtons[3].Focus();
            }), DispatcherPriority.Input);
        }
        catch (Exception exception)
        {
            _pickerOpen = false;
            _pickerImage = null;
            ShowNotice(new IslandNotice(NoticeKind.ClipboardImage, "无法读取图片", exception.Message));
        }
    }

    private void CreateFormatButtons()
    {
        var style = (Style)FormatPicker.FindResource("FormatTextButton");
        _formatButtons = new Button[7];
        for (int slot = 0; slot < _formatButtons.Length; slot++)
        {
            var button = new Button
            {
                Style = style,
                Height = 27,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                Foreground = new SolidColorBrush()
            };
            button.Click += FormatButton_Click;
            FormatTrack.Children.Add(button);
            _formatButtons[slot] = button;
        }
    }

    private void UpdateFormatCarouselLayout()
    {
        CancelFormatMovement();
        AppSettings visual = CurrentVisualSettings;
        _pickerItemWidth = _pickerOperation switch
        {
            PickerOperation.Style => visual.FormatItemWidth + 16,
            PickerOperation.Compress => visual.FormatItemWidth + 24,
            _ => visual.FormatItemWidth
        };
        _formatStep = _pickerItemWidth + visual.FormatSpacing;
        int visible = _pickerOperation == PickerOperation.Format ? visual.FormatVisibleItems : 3;
        FormatViewport.Width = visible * _formatStep;
        FormatTrack.Width = _formatButtons.Length * _formatStep;
        for (int slot = 0; slot < _formatButtons.Length; slot++)
        {
            Button button = _formatButtons[slot];
            button.Width = _pickerItemWidth;
            button.FontSize = visual.FormatFontSize;
            Canvas.SetLeft(button, slot * _formatStep);
            Canvas.SetTop(button, 0);
        }
        RenderFormatButtons();
        SetFormatRestPosition();
    }

    private void RenderFormatButtons()
    {
        if (_formatButtons.Length == 0) return;
        AppSettings visual = CurrentVisualSettings;
        string[] items = PickerItems;
        int count = items.Length;
        for (int slot = 0; slot < _formatButtons.Length; slot++)
        {
            Button button = _formatButtons[slot];
            int linearIndex = _pickerIndex + slot - 3;
            if (_pickerOperation == PickerOperation.Compress && (linearIndex < 0 || linearIndex >= count))
            {
                button.Visibility = Visibility.Collapsed;
                continue;
            }
            button.Visibility = Visibility.Visible;
            int index = _pickerOperation == PickerOperation.Compress ? linearIndex
                : (linearIndex + count * 2) % count;
            bool selected = slot == 3;
            button.Tag = index;
            button.Content = items[index];
            button.FontWeight = FontWeights.Normal;
            button.BeginAnimation(Button.FontSizeProperty, null);
            button.FontSize = selected ? visual.FormatSelectedFontSize : visual.FormatFontSize;
            var brush = (SolidColorBrush)button.Foreground;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = selected ? MediaColor.FromRgb(85, 233, 171)
                                   : MediaColor.FromRgb(168, 175, 187);
        }
    }

    private void FormatViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_formatMoving)
        {
            CancelFormatMovement();
            RenderFormatButtons();
        }
        SetFormatRestPosition();
    }

    private void SetFormatRestPosition()
    {
        if (_formatMoving || _formatStep <= 0) return;
        double viewportWidth = FormatViewport.ActualWidth > 0
            ? FormatViewport.ActualWidth : FormatViewport.Width;
        _formatRestX = viewportWidth / 2 - (3 * _formatStep + _pickerItemWidth / 2);
        FormatShift.BeginAnimation(TranslateTransform.XProperty, null);
        FormatShift.X = _formatRestX;
    }

    private void CancelFormatMovement()
    {
        _formatAnimationToken++;
        _formatMoving = false;
        _convertAfterMove = false;
        FormatShift.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void MoveFormat(int direction)
    {
        if (!_pickerOpen || _formatMoving) return;
        StartFormatMove(direction);
    }

    private void StartFormatMove(int direction)
    {
        if (_pickerOperation == PickerOperation.Compress &&
            (_pickerIndex + direction < 0 || _pickerIndex + direction >= PickerItems.Length)) return;
        _formatMoving = true;
        int token = ++_formatAnimationToken;
        TimeSpan duration = TimeSpan.FromMilliseconds(CurrentVisualSettings.FormatScrollMilliseconds);
        AnimateFormatEmphasis(_formatButtons[3], false, duration);
        AnimateFormatEmphasis(_formatButtons[3 + direction], true, duration);
        var scroll = new DoubleAnimation(_formatRestX, _formatRestX - direction * _formatStep, duration);
        scroll.Completed += (_, _) =>
        {
            if (!_pickerOpen || token != _formatAnimationToken) return;
            int count = PickerItems.Length;
            _pickerIndex = _pickerOperation == PickerOperation.Compress
                ? _pickerIndex + direction : (_pickerIndex + direction + count) % count;
            _formatMoving = false;
            RenderFormatButtons();
            SetFormatRestPosition();
            if (_convertAfterMove)
            {
                _convertAfterMove = false;
                ApplyPickerSelection(_pickerIndex);
            }
        };
        FormatShift.BeginAnimation(TranslateTransform.XProperty, scroll);
    }

    private void AnimateFormatEmphasis(Button button, bool selected, TimeSpan duration)
    {
        double fontSize = selected
            ? CurrentVisualSettings.FormatSelectedFontSize : CurrentVisualSettings.FormatFontSize;
        button.BeginAnimation(Button.FontSizeProperty,
            new DoubleAnimation(fontSize, duration));
        ((SolidColorBrush)button.Foreground).BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(selected ? MediaColor.FromRgb(85, 233, 171)
                                        : MediaColor.FromRgb(168, 175, 187), duration));
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
                if (_formatMoving) _convertAfterMove = true;
                else ApplyPickerSelection(_pickerIndex);
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
        if (!_pickerOpen || _formatMoving || sender is not Button button || button.Tag is not int index)
            return;
        ApplyPickerSelection(index);
    }

    private void ApplyPickerSelection(int index)
    {
        if (!_pickerOpen || _pickerImage is null) return;
        string item = PickerItems[index];
        int optionIndex = _pickerOperation == PickerOperation.Compress ? _pickerOptionIndices[index] : index;
        try
        {
            _ignoreIslandClickUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
            string detail;
            if (_pickerOperation == PickerOperation.Style)
            {
                ClipboardImageEffects.ApplyStyleAndCopy(_pickerImage, index, _clipboardMonitor.SuppressNextUpdate);
                detail = $"已应用{item}风格 · PNG 可粘贴";
            }
            else if (_pickerOperation == PickerOperation.Compress)
            {
                var compressed = ClipboardImageEffects.CompressAndCopy(_pickerImage, optionIndex,
                    _clipboardMonitor.SuppressNextUpdate);
                detail = $"{ClipboardImageEffects.SizeDescriptions[optionIndex]} 档 · 实际 {compressed.Bytes / 1024.0:0.#} KB";
            }
            else
            {
                ClipboardImageConverter.ConvertAndCopy(_pickerImage, item,
                    _clipboardMonitor.SuppressNextUpdate);
                detail = $"已转换为 {item} · 可粘贴";
            }
            ClosePicker(showPrevious: false);
            ShowNoticeCore(new IslandNotice(NoticeKind.ImageConverted,
                $"{(_pickerOperation == PickerOperation.Compress ? ClipboardImageEffects.SizeDescriptions[optionIndex] : item)} 复制成功", detail), false);
        }
        catch (Exception exception)
        {
            _clipboardMonitor.ClearSuppression();
            PickerHint.Text = $"处理失败：{exception.Message}";
        }
    }

    private void ClosePicker(bool showPrevious = true)
    {
        CancelFormatMovement();
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
        if (_imageEditing) return;
        if (_pickerOpen && !preview) return;
        if (!preview && CurrentVisualSettings.DisplayMode != IslandDisplayMode.Automatic) return;
        HideImagePreview();
        _closeTimer.Stop();
        _hoverExpanded = false;
        _isPersistentMusicNotice = ReferenceEquals(notice, _musicNotice) && _musicSnapshot?.IsPlaying == true;
        ContentText.Visibility = Visibility.Visible;
        FormatPicker.Visibility = Visibility.Collapsed;
        _currentKind = notice.Kind;
        TitleText.Text = notice.Title;
        DetailText.Text = notice.Detail;
        bool isImage = notice.Image is not null;
        IconBadge.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        PreviewImageFrame.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        bool music = notice.Kind == NoticeKind.Music && _musicSnapshot is not null;
        MusicControls.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        MusicProgressTrack.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        if (music) UpdateMusicProgress();
        PreviewImageBrush.ImageSource = notice.Image;
        _imagePreviewSource = notice.FullImage ?? (notice.ImagePath is null ? notice.Image : null);
        _imagePreviewPath = notice.ImagePath;
        IdleHoverTarget.Visibility = Visibility.Collapsed;
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
        if (_pickerOpen || _imageEditing) return;
        _closeTimer.Stop();
        HideImagePreview();
        if (!_isOpen) return;
        _isOpen = false;
        _currentKind = null;
        _hoverExpanded = false;
        _imagePreviewSource = null;
        _imagePreviewPath = null;
        _isPersistentMusicNotice = false;
        IdleHoverTarget.Visibility = CurrentVisualSettings.DisplayMode == IslandDisplayMode.Automatic &&
                                     CurrentVisualSettings.HoverExpandEnabled
            ? Visibility.Visible : Visibility.Collapsed;
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
            NoticeKind.Idle => MediaColor.FromRgb(128, 184, 255),
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
            NoticeKind.Idle => "M 5,9 C 5,6 7,5 10,5 H 14 C 17,5 19,6 19,9 V 15 C 19,18 17,19 14,19 H 10 C 7,19 5,18 5,15 Z",
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
        _idleHoverTimer.Stop();
        foreach (int id in _registeredHotkeys.Keys) UnregisterHotKey(_hotkeyHandle, id);
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
