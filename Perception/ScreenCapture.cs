using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace LingDongDao.Perception;

public sealed record CapturedFrame(Guid Id, DateTimeOffset Time, DisplayInfo Display,
    WindowInfo? Foreground, byte[] Jpeg, int Width, int Height);

public sealed class ScreenCapture
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    public IReadOnlyList<CapturedFrame> Capture(WindowInfo? foreground, int quality = 68)
    {
        var time = DateTimeOffset.UtcNow;
        var frames = new List<CapturedFrame>();
        foreach (Screen screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size,
                    CopyPixelOperation.SourceCopy);
            using var output = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 35, 90));
            bitmap.Save(output, JpegCodec, parameters);
            frames.Add(new CapturedFrame(Guid.NewGuid(), time,
                new DisplayInfo(screen.DeviceName,
                    new PixelRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height), screen.Primary),
                foreground, output.ToArray(), bounds.Width, bounds.Height));
        }
        return frames;
    }
}

public sealed class FrameRingBuffer
{
    private readonly LinkedList<CapturedFrame> _frames = new();
    private readonly object _sync = new();
    private long _bytes;

    public int Count { get { lock (_sync) return _frames.Count; } }
    public long Bytes { get { lock (_sync) return _bytes; } }

    public void Add(CapturedFrame frame, int seconds, int maximumMb)
    {
        lock (_sync)
        {
            _frames.AddLast(frame);
            _bytes += frame.Jpeg.LongLength;
            DateTimeOffset oldest = frame.Time.AddSeconds(-seconds);
            long limit = maximumMb * 1024L * 1024L;
            while (_frames.First is { } first &&
                   (first.Value.Time < oldest || _bytes > limit))
            {
                _bytes -= first.Value.Jpeg.LongLength;
                _frames.RemoveFirst();
            }
        }
    }

    public CapturedFrame? Find(Guid id)
    {
        lock (_sync) return _frames.FirstOrDefault(frame => frame.Id == id);
    }

    public CapturedFrame? Before(DateTimeOffset time, string displayId, TimeSpan age)
    {
        lock (_sync) return _frames.LastOrDefault(frame =>
            frame.Display.Id == displayId && frame.Time < time && time - frame.Time <= age);
    }

    public void Clear()
    {
        lock (_sync) { _frames.Clear(); _bytes = 0; }
    }
}
