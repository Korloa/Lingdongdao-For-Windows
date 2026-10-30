using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LingDongDao;

internal static class ClipboardImageEffects
{
    public static readonly string[] Styles = ["艳丽", "高冷", "黑白", "扫描"];
    public static readonly string[] Sizes = ["<200K", "200–500K", "500K–1M", "1–2M", "2–5M"];
    public static readonly string[] SizeDescriptions = ["<200KB", "200–500KB", "500KB–1MB", "1MB–2MB", "2MB–5MB"];
    public static readonly int[] SizeLimits = [200 * 1024, 500 * 1024, 1024 * 1024, 2 * 1024 * 1024, 5 * 1024 * 1024];

    public static BitmapSource ApplyStyle(BitmapSource image, int styleIndex)
    {
        if ((uint)styleIndex >= Styles.Length) throw new ArgumentOutOfRangeException(nameof(styleIndex));
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            switch (styleIndex)
            {
                case 0: // More contrast and saturation, with a gentle warm lift.
                    double mid = (r + g + b) / 3;
                    r = (r - mid) * 1.27 + mid;
                    g = (g - mid) * 1.27 + mid;
                    b = (b - mid) * 1.27 + mid;
                    r = (r - 128) * 1.08 + 135;
                    g = (g - 128) * 1.08 + 130;
                    b = (b - 128) * 1.08 + 128;
                    break;
                case 1: // Cool muted tones.
                    double grey = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = grey + (r - grey) * 0.72 - 15;
                    g = grey + (g - grey) * 0.72 + 2;
                    b = grey + (b - grey) * 0.72 + 20;
                    break;
                case 2:
                    r = g = b = 0.299 * r + 0.587 * g + 0.114 * b;
                    break;
                case 3: // A high contrast document scan.
                    double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = g = b = luminance >= 165 ? 255 : luminance <= 90 ? 0 : (luminance - 90) * 255 / 75;
                    break;
            }
            pixels[i] = ToByte(b);
            pixels[i + 1] = ToByte(g);
            pixels[i + 2] = ToByte(r);
        }
        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public static string ApplyStyleAndCopy(BitmapSource image, int styleIndex, Action? beforeClipboardWrite = null)
    {
        BitmapSource result = ApplyStyle(image, styleIndex);
        return ClipboardImageConverter.CopyEncoded(result, "png", ClipboardImageConverter.EncodeImage(result, "PNG"),
            beforeClipboardWrite);
    }

    public static (byte[] Bytes, BitmapSource Image) Compress(BitmapSource image, int sizeIndex)
    {
        if ((uint)sizeIndex >= SizeLimits.Length) throw new ArgumentOutOfRangeException(nameof(sizeIndex));
        int limit = SizeLimits[sizeIndex];
        BitmapSource current = FlattenOnWhite(image);
        for (int pass = 0; pass < 25; pass++)
        {
            byte[]? best = null;
            int low = 20, high = 95;
            while (low <= high)
            {
                int quality = (low + high) / 2;
                byte[] candidate = EncodeJpeg(current, quality);
                if (candidate.Length <= limit)
                {
                    best = candidate;
                    low = quality + 1;
                }
                else high = quality - 1;
            }
            if (best is not null) return (best, current);
            if (current.PixelWidth == 1 && current.PixelHeight == 1) break;
            current = Resize(current, Math.Max(1, (int)(current.PixelWidth * 0.78)),
                Math.Max(1, (int)(current.PixelHeight * 0.78)));
        }
        throw new InvalidOperationException("无法压缩到所选大小，请尝试更大的档位。");
    }

    public static (string Path, int Bytes) CompressAndCopy(BitmapSource image, int sizeIndex,
        Action? beforeClipboardWrite = null)
    {
        (byte[] bytes, BitmapSource result) = Compress(image, sizeIndex);
        string path = ClipboardImageConverter.CopyEncoded(result, "jpg", bytes, beforeClipboardWrite);
        return (path, bytes.Length);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static byte[] EncodeJpeg(BitmapSource image, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource FlattenOnWhite(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            context.DrawRectangle(System.Windows.Media.Brushes.White, null, bounds);
            context.DrawImage(image, bounds);
        }
        var result = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private static BitmapSource Resize(BitmapSource image, int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
            context.DrawImage(image, new Rect(0, 0, width, height));
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
