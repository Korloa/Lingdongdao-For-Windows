using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LingDongDao;

internal sealed class AgentCommandWindow : Window
{
    private readonly TextBox _command;
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
            Margin = new Thickness(0, 16, 0, 10), Height = 42, FontSize = 14,
            Padding = new Thickness(12, 9, 12, 7),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(26, 45, 56)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(57, 107, 133)),
            CaretBrush = Brushes.White,
            ToolTip = "例如：打开资源管理器；打开微信并回复指定消息"
        };
        layout.Children.Add(_command);
        layout.Children.Add(new TextBlock
        {
            Text = "Enter 开始  ·  Esc 取消  ·  可用停止 Agent 快捷键中断",
            Foreground = new SolidColorBrush(Color.FromRgb(154, 181, 194)), FontSize = 11
        });
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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                string value = _command.Text.Trim();
                if (value.Length > 0) { Submitted?.Invoke(value); Close(); }
                e.Handled = true;
            }
        };
    }
}
