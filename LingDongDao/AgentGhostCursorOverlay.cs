using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LingDongDao.Perception;
using Forms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LingDongDao;

// A click-through visual cursor. The real pointer is restored after SendInput.
internal sealed class AgentGhostCursorOverlay : Window
{
    private readonly DispatcherTimer _motion = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private readonly TextBlock _label;
    private readonly Canvas _canvas;
    private readonly Path _pointer;
    private readonly DropShadowEffect _glow;
    private readonly Border _chip;
    private double _x, _y, _startX, _startY, _targetX, _targetY;
    private double _size = 40;
    private int _durationMs = 620;
    private int _sparkCount = 8;
    private string _curve = "smooth";
    private Color _color = Color.FromRgb(59, 184, 255);

    public AgentGhostCursorOverlay()
    {
        Width = 300;
        Height = 110;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        _canvas = new Canvas();
        _glow = new DropShadowEffect
        {
            Color = Color.FromRgb(20, 162, 255), BlurRadius = 22,
            ShadowDepth = 0, Opacity = 0.95
        };
        _pointer = new Path
        {
            Data = Geometry.Parse("M 4,3 L 4,35 L 12,28 L 19,43 L 26,40 L 19,25 L 30,25 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(59, 184, 255)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Effect = _glow,
            RenderTransformOrigin = new System.Windows.Point(0, 0),
            RenderTransform = new ScaleTransform(1, 1)
        };
        _canvas.Children.Add(_pointer);
        _chip = new Border
        {
            CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 5, 9, 5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 71, 180, 247)),
            Background = new SolidColorBrush(Color.FromArgb(235, 18, 37, 48))
        };
        _label = new TextBlock
        {
            Text = "AI", Foreground = Brushes.White, FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 175
        };
        _chip.Child = _label;
        Canvas.SetLeft(_chip, 36);
        Canvas.SetTop(_chip, 25);
        _canvas.Children.Add(_chip);
        Content = _canvas;
        _motion.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            nint style = GetWindowLongPtr(handle, -20);
            SetWindowLongPtr(handle, -20, style | 0x20 | 0x80 | 0x08000000);
        };
    }

    public void ApplyOptions(PerceptionOptions options)
    {
        _size = options.AgentCursorSize;
        _durationMs = options.AgentCursorMoveMs;
        _curve = options.AgentCursorCurve;
        _sparkCount = options.AgentClickSparkCount;
        _color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(
            options.AgentCursorColor)!;
        _pointer.Fill = new SolidColorBrush(_color);
        _pointer.Data = Geometry.Parse(options.AgentCursorShape switch
        {
            "slim" => "M 4,3 L 4,37 L 10,28 L 16,43 L 22,40 L 16,25 L 27,25 Z",
            "diamond" => "M 4,3 L 26,20 L 16,23 L 20,40 L 15,42 L 10,26 L 4,33 Z",
            _ => "M 4,3 L 4,35 L 12,28 L 19,43 L 26,40 L 19,25 L 30,25 Z"
        });
        var scale = (ScaleTransform)_pointer.RenderTransform;
        scale.ScaleX = scale.ScaleY = _size / 40;
        _glow.Color = _color;
        _glow.BlurRadius = options.AgentCursorGlowRadius;
        _glow.Opacity = options.AgentCursorGlowOpacity;
        _chip.BorderBrush = new SolidColorBrush(Color.FromArgb(180, _color.R, _color.G, _color.B));
        Canvas.SetLeft(_chip, Math.Max(36, _size * 0.82));
        Canvas.SetTop(_chip, Math.Max(25, _size * 0.55));
        Place();
    }

    public void ClickSpark()
    {
        if (!IsVisible || _sparkCount == 0) return;
        for (int i = 0; i < _sparkCount; i++)
        {
            double angle = 2 * Math.PI * i / _sparkCount;
            double distance = 16 + _size * 0.22;
            var spark = new Ellipse
            {
                Width = i % 3 == 0 ? 5 : 3,
                Height = i % 3 == 0 ? 5 : 3,
                Fill = new SolidColorBrush(i % 2 == 0 ? Colors.White : _color),
                Opacity = 0.95,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(spark, 4);
            Canvas.SetTop(spark, 3);
            _canvas.Children.Add(spark);
            var duration = TimeSpan.FromMilliseconds(330);
            spark.BeginAnimation(Canvas.LeftProperty,
                new DoubleAnimation(4, 4 + Math.Cos(angle) * distance, duration)
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            spark.BeginAnimation(Canvas.TopProperty,
                new DoubleAnimation(3, 3 + Math.Sin(angle) * distance, duration)
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            var fade = new DoubleAnimation(0.95, 0, duration);
            fade.Completed += (_, _) => _canvas.Children.Remove(spark);
            spark.BeginAnimation(OpacityProperty, fade);
        }
    }

    public void Start(int x, int y)
    {
        _motion.Stop();
        _x = _targetX = x;
        _y = _targetY = y;
        _label.Text = "AI";
        if (!IsVisible) Show();
        Place();
    }

    public void MoveTo(AgentActionVisual cue)
    {
        _label.Text = cue.Kind == AgentToolKind.Navigate
            ? $"访问网页 · {cue.X}, {cue.Y}"
            : $"{cue.Label} · {cue.X}, {cue.Y}";
        if (!IsVisible) Show();
        _startX = _x;
        _startY = _y;
        _targetX = cue.X;
        _targetY = cue.Y;
        _clock.Restart();
        _motion.Start();
    }

    public void HideForCapture() { _motion.Stop(); if (IsVisible) Hide(); }
    public void Resume() { if (!IsVisible) Show(); Place(); }
    public void Stop() { _motion.Stop(); if (IsVisible) Hide(); }

    private void Tick()
    {
        double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _durationMs);
        double progress = _curve switch
        {
            "linear" => t,
            "easeOut" => 1 - Math.Pow(1 - t, 3),
            _ => t * t * (3 - 2 * t)
        };
        _x = _startX + (_targetX - _startX) * progress;
        _y = _startY + (_targetY - _startY) * progress;
        if (_curve == "human")
        {
            double dx = _targetX - _startX, dy = _targetY - _startY;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 1)
            {
                double arc = Math.Min(65, length * 0.08) * Math.Sin(Math.PI * progress);
                _x += -dy / length * arc;
                _y += dx / length * arc;
            }
        }
        Place();
        if (t >= 1) _motion.Stop();
    }

    private void Place()
    {
        if (!IsVisible) return;
        double scaleX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        int width = (int)Math.Ceiling(Width * scaleX);
        int height = (int)Math.Ceiling(Height * scaleY);
        int left = (int)Math.Round(_x - 4 * scaleX);
        int top = (int)Math.Round(_y - 3 * scaleY);
        SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), left, top,
            width, height, 0x0010 | 0x0040);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y,
        int width, int height, uint flags);
}
