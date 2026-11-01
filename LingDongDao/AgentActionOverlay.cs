using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LingDongDao.Perception;
using Forms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LingDongDao;

internal sealed class AgentActionOverlay : Window
{
    private readonly TextBlock _actionText;
    private readonly TextBlock _coordinateText;
    private readonly Ellipse _ring;

    public AgentActionOverlay()
    {
        Width = 300;
        Height = 66;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        var canvas = new Canvas();
        _ring = new Ellipse { Width = 26, Height = 26, StrokeThickness = 2.5,
            Stroke = new SolidColorBrush(Color.FromRgb(108, 221, 255)),
            Fill = new SolidColorBrush(Color.FromArgb(55, 108, 221, 255)) };
        Canvas.SetLeft(_ring, 2);
        Canvas.SetTop(_ring, 19);
        canvas.Children.Add(_ring);
        var card = new Border { Width = 260, Height = 60,
            CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 108, 221, 255)),
            Background = new SolidColorBrush(Color.FromArgb(235, 13, 27, 32)),
            Padding = new Thickness(12, 7, 12, 6) };
        Canvas.SetLeft(card, 36);
        Canvas.SetTop(card, 2);
        var text = new StackPanel();
        _actionText = new TextBlock { Foreground = Brushes.White, FontSize = 12,
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _coordinateText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(146, 191, 197)),
            FontSize = 10, Margin = new Thickness(0, 3, 0, 0) };
        text.Children.Add(_actionText);
        text.Children.Add(_coordinateText);
        card.Child = text;
        canvas.Children.Add(card);
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            nint style = GetWindowLongPtr(handle, -20);
            SetWindowLongPtr(handle, -20, style | 0x20 | 0x80 | 0x08000000);
        };
    }

    public void ShowAction(AgentActionVisual cue)
    {
        _ring.Stroke = cue.Kind == AgentToolKind.RightClick
            ? new SolidColorBrush(Color.FromRgb(255, 196, 127))
            : new SolidColorBrush(Color.FromRgb(108, 221, 255));
        _actionText.Text = $"{(cue.Kind == AgentToolKind.RightClick ? "右键" : "点击")} · {cue.Label}";
        _coordinateText.Text = $"X {cue.X}   Y {cue.Y}";
        if (!IsVisible) Show();
        double scaleX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        int width = (int)Math.Ceiling(Width * scaleX);
        int height = (int)Math.Ceiling(Height * scaleY);
        var virtualScreen = Forms.SystemInformation.VirtualScreen;
        int left = Math.Clamp(cue.X - (int)(15 * scaleX), virtualScreen.Left,
            Math.Max(virtualScreen.Left, virtualScreen.Right - width));
        int top = Math.Clamp(cue.Y - (int)(32 * scaleY), virtualScreen.Top,
            Math.Max(virtualScreen.Top, virtualScreen.Bottom - height));
        SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), left, top,
            width, height, 0x0010 | 0x0040);
    }

    public void HideAction() { if (IsVisible) Hide(); }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y,
        int width, int height, uint flags);
}
