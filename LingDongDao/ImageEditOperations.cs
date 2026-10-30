using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LingDongDao;

internal static class ImageEditOperations
{
    public static BitmapSource Crop(BitmapSource source, Rect selection, double displayWidth, double displayHeight)
    {
        if (displayWidth <= 0 || displayHeight <= 0 || selection.Width < 2 || selection.Height < 2)
            throw new ArgumentException("请拖动鼠标框选截图区域。");
        int x = Math.Clamp((int)Math.Floor(selection.Left * source.PixelWidth / displayWidth), 0, source.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Floor(selection.Top * source.PixelHeight / displayHeight), 0, source.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(selection.Right * source.PixelWidth / displayWidth), x + 1, source.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(selection.Bottom * source.PixelHeight / displayHeight), y + 1, source.PixelHeight);
        var result = new CroppedBitmap(source, new Int32Rect(x, y, right - x, bottom - y));
        result.Freeze();
        return result;
    }

    public static BitmapSource Draw(BitmapSource source, StrokeCollection strokes,
        double displayWidth, double displayHeight)
    {
        if (strokes.Count == 0) return source;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.PushTransform(new ScaleTransform(source.PixelWidth / displayWidth,
                source.PixelHeight / displayHeight));
            foreach (Stroke stroke in strokes) stroke.Draw(context);
            context.Pop();
        }
        var result = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    public static BitmapSource DecodeJpeg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        BitmapSource image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }
}
