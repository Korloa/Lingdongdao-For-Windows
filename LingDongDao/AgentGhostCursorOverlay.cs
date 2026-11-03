using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
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
    private double _x, _y, _startX, _startY, _targetX, _targetY;
    private const double DurationMs = 620;

    public AgentGhostCursorOverlay()
    {
        Width = 230;
        Height = 78;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        var canvas = new Canvas();
        var pointer = new Path
        {
            Data = Geometry.Parse("M 4,3 L 4,35 L 12,28 L 19,43 L 26,40 L 19,25 L 30,25 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(59, 184, 255)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(20, 162, 255), BlurRadius = 22,
                ShadowDepth = 0, Opacity = 0.95
            }
        };
        canvas.Children.Add(pointer);
        var chip = new Border
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
        chip.Child = _label;
        Canvas.SetLeft(chip, 36);
        Canvas.SetTop(chip, 25);
        canvas.Children.Add(chip);
        Content = canvas;
        _motion.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            nint style = GetWindowLongPtr(handle, -20);
            SetWindowLongPtr(handle, -20, style | 0x20 | 0x80 | 0x08000000);
        };
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
        double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / DurationMs);
        // Smooth start and stop, with an exact endpoint and no continued drift.
        double progress = t * t * (3 - 2 * t);
        _x = _startX + (_targetX - _startX) * progress;
        _y = _startY + (_targetY - _startY) * progress;
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
        var screen = Forms.SystemInformation.VirtualScreen;
        int left = Math.Clamp((int)Math.Round(_x - 4 * scaleX), screen.Left,
            Math.Max(screen.Left, screen.Right - width));
        int top = Math.Clamp((int)Math.Round(_y - 3 * scaleY), screen.Top,
            Math.Max(screen.Top, screen.Bottom - height));
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
