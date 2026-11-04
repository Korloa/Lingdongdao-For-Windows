using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LingDongDao.Perception;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Forms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;

namespace LingDongDao;

internal sealed class AgentVisualizationWindow : Window
{
    private readonly WebView2CompositionControl _browser = new();
    private readonly TextBlock _title = new();
    private readonly DispatcherTimer _closeTimer = new();
    private CoreWebView2Environment? _environment;
    private bool _initialized;
    private string? _blockedNavigation;
    private string? _allowedDocumentUri;

    public AgentVisualizationWindow()
    {
        Title = "灵动岛可视化";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(11, 19, 32));
        ShowInTaskbar = false;
        Topmost = true;
        Width = 820;
        Height = 520;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 29, 45))
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var marker = new Border
        {
            Width = 5, Height = 18, CornerRadius = new CornerRadius(2.5),
            Margin = new Thickness(16, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(92, 206, 242))
        };
        header.Children.Add(marker);
        _title.Foreground = new SolidColorBrush(Color.FromRgb(230, 242, 250));
        _title.FontSize = 13;
        _title.FontWeight = FontWeights.SemiBold;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.Text = "AI 可视化";
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);
        var close = new Button
        {
            Content = "×", Width = 42, Height = 42, FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(195, 215, 226)),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, ToolTip = "关闭动画 (Esc)"
        };
        close.Click += (_, _) => Close();
        close.MouseEnter += (_, _) => close.Background =
            new SolidColorBrush(Color.FromRgb(52, 69, 83));
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Source == close) return;
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        layout.Children.Add(header);
        Grid.SetRow(_browser, 1);
        layout.Children.Add(_browser);
        Content = layout;
        _closeTimer.Tick += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            _browser.Dispose();
        };
    }

    public async Task<AgentVisualizationResult> ShowContentAsync(
        AgentVisualizationRequest request, CancellationToken cancellationToken)
    {
        if (!AgentVisualizationDocument.TryValidate(request, out string reason))
            return new(false, reason);
        try
        {
            Width = request.Width;
            Height = request.Height;
            Title = "灵动岛可视化 · " + request.Title;
            _title.Text = request.Title;
            PositionOnCursorScreen();
            if (!IsVisible) Show();
            PositionOnCursorScreen();
            Activate();
            if (!_initialized)
            {
                string profile = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "LingDongDao", "VisualizationWebView2");
                _environment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: profile);
                await _browser.EnsureCoreWebView2Async(_environment);
                ConfigureBrowser();
                _initialized = true;
            }

            var loaded = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CoreWebView2WebErrorStatus? webError = null;
            void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                webError = e.WebErrorStatus;
                loaded.TrySetResult(e.IsSuccess);
            }
            _browser.NavigationCompleted += Completed;
            try
            {
                string document = AgentVisualizationDocument.Build(request);
                _allowedDocumentUri = "data:text/html;charset=utf-8;base64," +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(document));
                _blockedNavigation = null;
                _browser.NavigateToString(document);
                bool success = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (!success)
                {
                    Close();
                    return new(false, "动画页面加载失败：" + webError +
                        (_blockedNavigation is null ? "" : " / 已阻止跳转"));
                }
            }
            finally { _browser.NavigationCompleted -= Completed; }
            _closeTimer.Stop();
            if (request.DurationSeconds > 0)
            {
                _closeTimer.Interval = TimeSpan.FromSeconds(request.DurationSeconds);
                _closeTimer.Start();
            }
            return new(true, "动画面板已显示；下一帧截图可核对结果");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Close();
            return new(false, exception.GetType().Name == "WebView2RuntimeNotFoundException" ?
                "未安装 Microsoft Edge WebView2 Runtime" :
                "动画面板启动失败：" + exception.GetType().Name);
        }
    }

    private void ConfigureBrowser()
    {
        CoreWebView2 core = _browser.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme is "about" or "data") return;
            e.Response = _environment!.CreateWebResourceResponse(
                new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase) &&
                !e.Uri.Equals(_allowedDocumentUri, StringComparison.Ordinal))
            {
                _blockedNavigation = e.Uri;
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
    }

    private void PositionOnCursorScreen()
    {
        Forms.Screen screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = Math.Min(Width, Math.Max(320, (screen.WorkingArea.Width - 32) / dpi.DpiScaleX));
        Height = Math.Min(Height, Math.Max(240, (screen.WorkingArea.Height - 32) / dpi.DpiScaleY));
        Left = (screen.WorkingArea.Left + (screen.WorkingArea.Width - Width * dpi.DpiScaleX) / 2)
            / dpi.DpiScaleX;
        Top = (screen.WorkingArea.Top + (screen.WorkingArea.Height - Height * dpi.DpiScaleY) / 2)
            / dpi.DpiScaleY;
    }
}
