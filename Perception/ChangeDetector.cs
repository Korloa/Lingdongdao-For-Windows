using System.Drawing;
using System.Drawing.Imaging;

namespace LingDongDao.Perception;

public sealed class ChangeDetector
{
    private sealed record Preview(byte[] Gray, int Width, int Height, long WindowHandle);
    private readonly Dictionary<string, Preview> _last = new();
    private readonly object _sync = new();
    private const int Width = 480;

    public ScreenChange Analyze(CapturedFrame frame, IReadOnlyList<ActivityEvent> recent,
        bool forceScan)
    {
        var reasons = new List<string>();
        if (forceScan) reasons.Add("periodic");
        if (recent.Any(item => item.Kind == "mouse_click")) reasons.Add("mouse_click");
        using var stream = new MemoryStream(frame.Jpeg);
        using var source = new Bitmap(stream);
        int height = Math.Max(1, (int)Math.Round(source.Height * (double)Width / source.Width));
        using var small = new Bitmap(Width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(small))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            graphics.DrawImage(source, 0, 0, Width, height);
        }
        var gray = new byte[Width * height];
        var area = new Rectangle(0, 0, Width, height);
        BitmapData data = small.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < Width; x++)
                {
                    int offset = x * 3;
                    gray[y * Width + x] = (byte)((row[offset] * 29 + row[offset + 1] * 150 +
                        row[offset + 2] * 77) >> 8);
                }
            }
        }
        finally { small.UnlockBits(data); }

        lock (_sync)
        {
            _last.TryGetValue(frame.Display.Id, out Preview? prior);
            _last[frame.Display.Id] = new Preview(gray, Width, height, frame.Foreground?.Handle ?? 0);
            if (prior is null || prior.Height != height)
                return new ScreenChange(1, [new ChangeRegion(frame.Display.Bounds, 1)], ["first_frame"], true);
            if (prior.WindowHandle != (frame.Foreground?.Handle ?? 0)) reasons.Add("window_switch");
            const int tileWidth = 40, tileHeight = 30;
            var regions = new List<ChangeRegion>();
            long globalSum = 0;
            for (int top = 0; top < height; top += tileHeight)
            for (int left = 0; left < Width; left += tileWidth)
            {
                long tileSum = 0;
                int count = 0;
                for (int y = top; y < Math.Min(height, top + tileHeight); y += 2)
                for (int x = left; x < Math.Min(Width, left + tileWidth); x += 2)
                {
                    int diff = Math.Abs(gray[y * Width + x] - prior.Gray[y * Width + x]);
                    tileSum += diff;
                    globalSum += diff;
                    count++;
                }
                double mean = count == 0 ? 0 : tileSum / (255.0 * count);
                if (mean < 0.035) continue;
                double sx = frame.Width / (double)Width, sy = frame.Height / (double)height;
                regions.Add(new ChangeRegion(new PixelRect(
                    frame.Display.Bounds.X + (int)(left * sx),
                    frame.Display.Bounds.Y + (int)(top * sy),
                    (int)Math.Ceiling(Math.Min(tileWidth, Width - left) * sx),
                    (int)Math.Ceiling(Math.Min(tileHeight, height - top) * sy)), mean));
            }
            double global = globalSum / (255.0 * Math.Ceiling(Width / 2.0) * Math.Ceiling(height / 2.0));
            if (global >= 0.025) reasons.Add("global_change");
            if (regions.Count > 0) reasons.Add("local_change");
            bool analyze = reasons.Count > 0;
            return new ScreenChange(global, regions.OrderByDescending(item => item.MeanDifference)
                .Take(24).ToArray(), reasons, analyze);
        }
    }

    public void Clear() { lock (_sync) _last.Clear(); }
}
