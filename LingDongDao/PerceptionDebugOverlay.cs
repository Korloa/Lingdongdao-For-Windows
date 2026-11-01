using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LingDongDao.Perception;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LingDongDao;

// Visualizes only observed OCR and GUI bounds. The window is click-through and
// excluded from Windows screen capture, so it cannot feed its own labels to OCR.
internal sealed class PerceptionDebugOverlay : Window
{
    private readonly Canvas _canvas = new();
    private DisplayInfo? _display;
    public bool CaptureExcluded { get; private set; }

    public PerceptionDebugOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            nint style = GetWindowLongPtr(handle, -20);
            SetWindowLongPtr(handle, -20, style | 0x20 | 0x80 | 0x08000000);
            CaptureExcluded = SetWindowDisplayAffinity(handle, 0x11);
        };
    }

    public void ShowObservation(DesktopObservation observation)
    {
        if (_display != observation.Display)
        {
            _display = observation.Display;
            if (!IsVisible) Show();
            PixelRect bounds = observation.Display.Bounds;
            SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), bounds.X,
                bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);
        }
        else if (!IsVisible) Show();

        _canvas.Children.Clear();
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        double scaleX = transform?.M11 ?? 1, scaleY = transform?.M22 ?? 1;
        PixelRect screen = observation.Display.Bounds;
        foreach (GuiElement item in observation.GuiElements.Where(item => !item.IsPassword).Take(100))
        {
            if (!item.Bounds.Intersects(screen)) continue;
            string name = string.IsNullOrWhiteSpace(item.Name) ? item.Type : $"{item.Type} · {item.Name}";
            string origin = item.Source == "uia" ? "UIA" : "GUI 模型";
            Draw(item.Bounds, $"{origin} {name}", Color.FromRgb(91, 213, 241), screen, scaleX, scaleY);
        }
        foreach (OcrText item in observation.Ocr.Take(100))
        {
            if (!item.Bounds.Intersects(screen)) continue;
            if (observation.GuiElements.Any(control => control.IsPassword &&
                control.Bounds.Intersects(item.Bounds))) continue;
            string value = item.Text.Replace('\n', ' ').Replace('\r', ' ');
            Draw(item.Bounds, $"OCR {value}", Color.FromRgb(255, 190, 105), screen, scaleX, scaleY);
        }
    }

    private void Draw(PixelRect raw, string label, Color color, PixelRect screen,
        double scaleX, double scaleY)
    {
        PixelRect bounds = raw.Intersect(screen);
        if (bounds.Width < 2 || bounds.Height < 2) return;
        double x = (bounds.X - screen.X) / scaleX, y = (bounds.Y - screen.Y) / scaleY;
        double width = bounds.Width / scaleX, height = bounds.Height / scaleY;
        var brush = new SolidColorBrush(color);
        var rect = new Rectangle { Width = width, Height = height, Stroke = brush,
            StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(15, color.R, color.G, color.B)) };
        Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y); _canvas.Children.Add(rect);
        string text = label.Length > 65 ? label[..64] + "…" : label;
        var badge = new Border { Background = new SolidColorBrush(Color.FromArgb(230, 15, 25, 31)),
            BorderBrush = brush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 4, 2), Child = new TextBlock { Text = $"{text}  ({raw.X},{raw.Y})",
                Foreground = Brushes.White, FontSize = 10, MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis } };
        Canvas.SetLeft(badge, x);
        Canvas.SetTop(badge, y >= 24 ? y - 23 : y + height + 2);
        _canvas.Children.Add(badge);
    }

    public void Clear() { _canvas.Children.Clear(); if (IsVisible) Hide(); }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
