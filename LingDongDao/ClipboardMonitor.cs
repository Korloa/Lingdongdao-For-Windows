using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;

namespace LingDongDao;

internal sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Func<AppSettings> _settings;
    private HwndSource? _source;
    private IntPtr _handle;
    private int _attempts;

    public event Action<IslandNotice>? Copied;

    public ClipboardMonitor(Window window, Func<AppSettings> settings)
    {
        _settings = settings;
        window.SourceInitialized += (_, _) => Start(window);
        _retryTimer.Tick += OnRetry;
    }

    private void Start(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        AddClipboardFormatListener(_handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate)
        {
            if (!_settings().ClipboardEnabled) return IntPtr.Zero;
            _attempts = 0;
            _retryTimer.Stop();
            _retryTimer.Start();
        }
        return IntPtr.Zero;
    }

    private void OnRetry(object? sender, EventArgs e)
    {
        _attempts++;
        try
        {
            IslandNotice? notice = ReadCurrent();
            _retryTimer.Stop();
            if (notice is not null) Copied?.Invoke(notice);
        }
        catch (System.Runtime.InteropServices.COMException) when (_attempts < 4)
        {
            // Another application may still own the clipboard briefly.
        }
        catch (ExternalException) when (_attempts < 4)
        {
        }
        catch
        {
            _retryTimer.Stop();
        }
    }

    private static IslandNotice? ReadCurrent()
    {
        if (Clipboard.ContainsImage())
        {
            BitmapSource? image = Clipboard.GetImage();
            if (image is not null)
                return new IslandNotice(NoticeKind.ClipboardImage, "已复制图片",
                    $"{image.PixelWidth} × {image.PixelHeight}", MakeThumbnail(image));
        }

        if (Clipboard.ContainsFileDropList())
        {
            foreach (string path in Clipboard.GetFileDropList().Cast<string>())
            {
                if (!File.Exists(path) || !ImageExtensions.Contains(Path.GetExtension(path))) continue;
                var source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.DecodePixelWidth = 160;
                source.UriSource = new Uri(path);
                source.EndInit();
                source.Freeze();
                return new IslandNotice(NoticeKind.ClipboardImage, "已复制图片", Path.GetFileName(path),
                    MakeThumbnail(source));
            }
        }

        if (!Clipboard.ContainsText()) return null;
        string text = Clipboard.GetText();
        string preview = string.Join(" ", text.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        if (preview.Length == 0) return null;
        if (preview.Length > 70) preview = preview[..70] + "…";
        return new IslandNotice(NoticeKind.ClipboardText, "已复制文本", preview);
    }

    private static BitmapSource MakeThumbnail(BitmapSource image)
    {
        const int size = 96;
        double scale = Math.Max(size / (double)image.PixelWidth, size / (double)image.PixelHeight);
        double width = image.PixelWidth * scale;
        double height = image.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.PushClip(new RectangleGeometry(new Rect(0, 0, size, size)));
            context.DrawImage(image, new Rect((size - width) / 2, (size - height) / 2, width, height));
            context.Pop();
        }
        var result = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    public void Dispose()
    {
        _retryTimer.Stop();
        if (_handle != IntPtr.Zero) RemoveClipboardFormatListener(_handle);
        _source?.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
