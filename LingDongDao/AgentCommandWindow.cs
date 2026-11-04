using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LingDongDao;

internal sealed class AgentCommandWindow : Window
{
    private readonly TextBox _command;
    private readonly Button _microphoneButton;
    private readonly TextBlock _hint;
    private readonly System.Windows.Threading.DispatcherTimer _recordingLimit =
        new() { Interval = TimeSpan.FromSeconds(20) };
    private OfflineVoiceInput? _voice;
    private bool _transcribing;
    public event Action<string>? Submitted;

    public AgentCommandWindow()
    {
        Title = "召唤 Agent";
        Width = 520;
        Height = 176;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        var outer = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(68, 125, 154)),
            Background = new SolidColorBrush(Color.FromRgb(16, 27, 34)),
            Padding = new Thickness(22)
        };
        var scale = new ScaleTransform(0.02, 0.86);
        var slide = new TranslateTransform(0, 18);
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(slide);
        outer.RenderTransformOrigin = new System.Windows.Point(0.5, 1);
        outer.RenderTransform = transform;
        outer.Opacity = 0;
        var layout = new StackPanel();
        layout.Children.Add(new TextBlock
        {
            Text = "让 Agent 帮你做什么？", Foreground = Brushes.White,
            FontSize = 18, FontWeight = FontWeights.SemiBold
        });
        _command = new TextBox
        {
            Height = 42, FontSize = 14,
            Padding = new Thickness(12, 9, 12, 7),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(26, 45, 56)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(57, 107, 133)),
            CaretBrush = Brushes.White,
            ToolTip = "例如：打开资源管理器；打开微信并回复指定消息"
        };
        var commandRow = new Grid { Margin = new Thickness(0, 16, 0, 10) };
        commandRow.ColumnDefinitions.Add(new ColumnDefinition());
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        commandRow.Children.Add(_command);
        _microphoneButton = new Button
        {
            Content = "🎙", FontSize = 17, Height = 42,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(32, 77, 101)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(57, 107, 133)),
            ToolTip = "离线语音输入：点击开始，再点一次结束"
        };
        Grid.SetColumn(_microphoneButton, 1);
        _microphoneButton.Click += async (_, _) => await ToggleVoiceAsync();
        commandRow.Children.Add(_microphoneButton);
        layout.Children.Add(commandRow);
        _hint = new TextBlock
        {
            Text = "Enter 开始  ·  麦克风或 Ctrl+M 离线语音输入  ·  Esc 取消",
            Foreground = new SolidColorBrush(Color.FromRgb(154, 181, 194)), FontSize = 11
        };
        layout.Children.Add(_hint);
        _recordingLimit.Tick += async (_, _) =>
        {
            _recordingLimit.Stop();
            if (_voice is not null) await ToggleVoiceAsync();
        };
        Closed += (_, _) =>
        {
            _recordingLimit.Stop();
            _voice?.Dispose();
            _voice = null;
        };
        outer.Child = layout;
        Content = outer;
        SourceInitialized += (_, _) =>
        {
            var area = System.Windows.Forms.Screen.FromPoint(
                System.Windows.Forms.Cursor.Position).WorkingArea;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Left = (area.Left + area.Width / 2.0) / dpi.DpiScaleX - Width / 2;
            Top = area.Bottom / dpi.DpiScaleY - Height - 16;
        };
        Loaded += (_, _) =>
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(330)) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(330)) { EasingFunction = easing });
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(330)) { EasingFunction = easing });
            outer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            _command.Focus();
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await ToggleVoiceAsync();
            }
            else if (e.Key == Key.Enter && _voice is null && !_transcribing)
            {
                string value = _command.Text.Trim();
                if (value.Length > 0) { Submitted?.Invoke(value); Close(); }
                e.Handled = true;
            }
        };
    }

    private async Task ToggleVoiceAsync()
    {
        if (_transcribing) return;
        if (_voice is null)
        {
            try
            {
                _voice = new OfflineVoiceInput();
                _voice.Start();
                _microphoneButton.Content = "■";
                _hint.Text = "正在录音，再点一次结束（最长 20 秒）";
                _recordingLimit.Start();
            }
            catch (Exception error)
            {
                _voice?.Dispose();
                _voice = null;
                _hint.Text = $"语音输入不可用：{error.Message}";
            }
            return;
        }

        _recordingLimit.Stop();
        _transcribing = true;
        _microphoneButton.IsEnabled = false;
        _hint.Text = "正在本机识别语音…";
        OfflineVoiceInput recording = _voice;
        try
        {
            string recognized = await recording.StopAndTranscribeAsync(CancellationToken.None);
            if (!IsLoaded) return;
            if (recognized.Length > 0)
            {
                _command.Text = string.IsNullOrWhiteSpace(_command.Text)
                    ? recognized : _command.Text.TrimEnd() + " " + recognized;
                _command.CaretIndex = _command.Text.Length;
                _command.Focus();
                _hint.Text = "识别完成，请核对文字后按 Enter";
            }
            else _hint.Text = "没有识别到语音，请重试";
        }
        catch (Exception error)
        {
            if (IsLoaded) _hint.Text = $"识别失败：{error.Message}";
        }
        finally
        {
            recording.Dispose();
            if (ReferenceEquals(_voice, recording)) _voice = null;
            if (IsLoaded)
            {
                _transcribing = false;
                _microphoneButton.IsEnabled = true;
                _microphoneButton.Content = "🎙";
            }
        }
    }
}
