using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;

namespace LingDongDao;

internal static class ClipboardImageConverter
{
    public static readonly string[] Formats = ["SVG", "PNG", "JPG", "GIF", "BMP"];

    public static string ConvertAndCopy(BitmapSource image, string format, Action? beforeClipboardWrite = null)
    {
        if (!Formats.Contains(format)) throw new ArgumentOutOfRangeException(nameof(format));
        string folder = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "LingDongDao", "Converted");
        Directory.CreateDirectory(folder);
        DeleteOldFiles(folder);

        byte[] bytes = EncodeImage(image, format);
        string path = Path.Combine(folder,
            $"image_{DateTime.Now:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.{format.ToLowerInvariant()}");
        File.WriteAllBytes(path, bytes);
        try
        {
            var data = new System.Windows.DataObject();
            var files = new StringCollection { path };
            data.SetFileDropList(files);
            data.SetImage(image);
            beforeClipboardWrite?.Invoke();
            Clipboard.SetDataObject(data, true);
            if (!Clipboard.ContainsFileDropList() ||
                !Clipboard.GetFileDropList().Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("无法确认转换文件已复制到剪贴板。");
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    internal static byte[] EncodeImage(BitmapSource image, string format) =>
        format == "SVG" ? EncodeSvg(image) : EncodeBitmap(image, format);

    private static byte[] EncodeBitmap(BitmapSource image, string format)
    {
        BitmapSource source = format == "JPG" ? FlattenOnWhite(image) : image;
        BitmapEncoder encoder = format switch
        {
            "PNG" => new PngBitmapEncoder(),
            "JPG" => new JpegBitmapEncoder { QualityLevel = 90 },
            "GIF" => new GifBitmapEncoder(),
            "BMP" => new BmpBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            context.DrawRectangle(System.Windows.Media.Brushes.White, null, bounds);
            context.DrawImage(source, bounds);
        }
        var bitmap = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodeSvg(BitmapSource image)
    {
        string png = Convert.ToBase64String(EncodeBitmap(image, "PNG"));
        string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                     $"xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                     $"width=\"{image.PixelWidth}\" height=\"{image.PixelHeight}\" " +
                     $"viewBox=\"0 0 {image.PixelWidth} {image.PixelHeight}\">" +
                     $"<image width=\"{image.PixelWidth}\" height=\"{image.PixelHeight}\" " +
                     $"xlink:href=\"data:image/png;base64,{png}\"/></svg>";
        return Encoding.UTF8.GetBytes(svg);
    }

    private static void DeleteOldFiles(string folder)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "image_*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7)) File.Delete(path);
            }
            catch { }
        }
    }
}
