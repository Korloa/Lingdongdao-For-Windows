using LingDongDao;
using System.IO;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Ink;
using System.Windows.Input;
using System.Xml.Linq;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--image-clipboard-test"))
        {
            byte[] pixels = new byte[64 * 64 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 64; pixels[i + 1] = 144; pixels[i + 2] = 220; pixels[i + 3] = 255;
            }
            var source = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
            System.Windows.IDataObject? oldClipboard = null;
            try { oldClipboard = System.Windows.Clipboard.GetDataObject(); } catch { }
            try
            {
                string styled = ClipboardImageEffects.ApplyStyleAndCopy(source, 2);
                VerifyImageClipboard(styled, ".png");
                (string compressed, int bytes) = ClipboardImageEffects.CompressAndCopy(source, 0);
                VerifyImageClipboard(compressed, ".jpg");
                if (bytes > 200 * 1024 || bytes != new FileInfo(compressed).Length)
                    throw new Exception("压缩文件大小或字节数不正确。");
                Console.WriteLine("图片风格与压缩剪贴板写入检查通过。");
            }
            finally
            {
                if (oldClipboard is not null) System.Windows.Clipboard.SetDataObject(oldClipboard, true);
            }
            return;
        }
        if (args.Contains("--hover-self-test"))
        {
            var settings = new AppSettings { TopOffset = 3, LineWidth = 58, LineThickness = 4,
                HoverHorizontalPadding = 0, HoverVerticalPadding = 2 };
            const double width = 360, center = width / 2;
            if (!IdleHoverZone.Contains(settings, width, center, 0) ||
                !IdleHoverZone.Contains(settings, width, center, 7) ||
                IdleHoverZone.Contains(settings, width, center + 40, 0) ||
                IdleHoverZone.Contains(settings, width, center, 12))
                throw new Exception("顶边横线感应区边界错误。");
            settings.TopOffset = 20;
            if (!IdleHoverZone.Contains(settings, width, center, 0) ||
                !IdleHoverZone.Contains(settings, width, center, 21) ||
                IdleHoverZone.Contains(settings, width, center, 12))
                throw new Exception("顶部距离改变后的感应区边界错误。");
            Console.WriteLine("顶边横线感应区检查通过。");
            return;
        }
        if (args.Contains("--image-self-test"))
        {
            byte[] pixels = new byte[16 * 16 * 4];
            for (int index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 32;
                pixels[index + 1] = 100;
                pixels[index + 2] = 240;
                pixels[index + 3] = 255;
            }
            var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            foreach (string format in ClipboardImageConverter.Formats)
            {
                byte[] bytes = ClipboardImageConverter.EncodeImage(image, format);
                if (format == "SVG")
                {
                    XDocument svg = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                    if (svg.Root?.Name.LocalName != "svg" ||
                        !svg.ToString().Contains("data:image/png;base64,"))
                        throw new Exception("SVG 包装检查失败。");
                }
                else
                {
                    using var stream = new MemoryStream(bytes);
                    BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    if (decoder.Frames[0].PixelWidth != 16 || decoder.Frames[0].PixelHeight != 16)
                        throw new Exception($"{format} 编码尺寸检查失败。");
                }
                Console.WriteLine($"{format} 编码检查通过：{bytes.Length} 字节");
            }
            for (int style = 0; style < ClipboardImageEffects.Styles.Length; style++)
            {
                BitmapSource styled = ClipboardImageEffects.ApplyStyle(image, style);
                byte[] pixel = new byte[4];
                styled.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                if (styled.PixelWidth != 16 || styled.PixelHeight != 16 || pixel[3] != 255)
                    throw new Exception($"{ClipboardImageEffects.Styles[style]} 尺寸或透明度错误。");
                if (style >= 2 && (pixel[0] != pixel[1] || pixel[1] != pixel[2]))
                    throw new Exception($"{ClipboardImageEffects.Styles[style]} 灰度处理错误。");
                if (style < 2 && pixel[0] == 32 && pixel[1] == 100 && pixel[2] == 240)
                    throw new Exception($"{ClipboardImageEffects.Styles[style]} 没有改变像素。");
                Console.WriteLine($"{ClipboardImageEffects.Styles[style]} 风格检查通过。");
            }
            if (ClipboardImageEffects.SmallerSizeOptions(190 * 1024).Length != 0 ||
                !ClipboardImageEffects.SmallerSizeOptions(300 * 1024).SequenceEqual([0]) ||
                !ClipboardImageEffects.SmallerSizeOptions(800 * 1024).SequenceEqual([0, 1]))
                throw new Exception("压缩档位筛选边界错误。");
            BitmapSource cropped = ImageEditOperations.Crop(image,
                new System.Windows.Rect(4, 4, 8, 8), 16, 16);
            if (cropped.PixelWidth != 8 || cropped.PixelHeight != 8)
                throw new Exception("截图裁剪尺寸错误。");
            var stroke = new Stroke(new StylusPointCollection
            {
                new StylusPoint(1, 1), new StylusPoint(14, 14)
            })
            {
                DrawingAttributes = new DrawingAttributes
                {
                    Color = Colors.Red, Width = 4, Height = 4
                }
            };
            var strokes = new StrokeCollection { stroke };
            BitmapSource drawn = ImageEditOperations.Draw(image, strokes, 16, 16);
            byte[] drawnPixel = new byte[4];
            drawn.CopyPixels(new System.Windows.Int32Rect(8, 8, 1, 1), drawnPixel, 4, 0);
            if (drawnPixel[0] == 32 && drawnPixel[1] == 100 && drawnPixel[2] == 240)
                throw new Exception("画笔未写入图片。");
            Console.WriteLine("压缩档位筛选、截图裁剪、画笔绘制检查通过。");
            var random = new Random(42);
            byte[] noisyPixels = new byte[1200 * 800 * 4];
            random.NextBytes(noisyPixels);
            for (int i = 3; i < noisyPixels.Length; i += 4) noisyPixels[i] = 255;
            var noisy = BitmapSource.Create(1200, 800, 96, 96, PixelFormats.Bgra32,
                null, noisyPixels, 1200 * 4);
            for (int size = 0; size < ClipboardImageEffects.Sizes.Length; size++)
            {
                (byte[] compressed, BitmapSource result) = ClipboardImageEffects.Compress(noisy, size);
                if (compressed.Length > ClipboardImageEffects.SizeLimits[size] ||
                    compressed.Length < 4 || compressed[0] != 0xFF || compressed[1] != 0xD8 ||
                    result.PixelWidth <= 0 || result.PixelHeight <= 0)
                    throw new Exception($"{ClipboardImageEffects.Sizes[size]} 压缩检查失败。");
                Console.WriteLine($"{ClipboardImageEffects.Sizes[size]} 压缩检查通过：{compressed.Length} 字节，{result.PixelWidth}×{result.PixelHeight}");
            }
            return;
        }
        if (args.Contains("--weather-self-test"))
        {
            const string sample = """
                {"timezone":"Asia/Shanghai","hourly":{"time":["2026-10-23T09:00","2026-10-23T10:00","2026-10-23T11:00","2026-10-23T12:00"],"weather_code":[3,95,96,99]}}
                """;
            DateTime now = new(2026, 10, 23, 9, 30, 0);
            if (ThunderstormForecast.FindFirst(sample, now, 6) != new DateTime(2026, 10, 23, 10, 0, 0) ||
                ThunderstormForecast.FindFirst(sample, now, 0) is not null ||
                ThunderstormForecast.FindFirst(sample, now.AddHours(4), 6) is not null)
                throw new Exception("雷雨时段解析或时间范围判断错误。");
            Console.WriteLine("雷雨预报解析检查通过。");
            return;
        }
        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
        };
        if (args.Contains("--music-live"))
        {
            using var music = new MusicMonitor(() => new AppSettings());
            var musicTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            musicTimer.Tick += (_, _) =>
            {
                musicTimer.Stop();
                Console.WriteLine($"音乐媒体会话：{music.StatusText}");
                app.Shutdown();
            };
            music.Start();
            musicTimer.Start();
            app.Run();
            return;
        }
        if (args.Contains("--weather-live"))
        {
            using var weather = new WeatherMonitor(() => new AppSettings());
            var weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            weatherTimer.Tick += (_, _) =>
            {
                weatherTimer.Stop();
                Console.WriteLine($"郫都区雷雨预报：{weather.StatusText}");
                app.Shutdown();
            };
            weather.Start();
            weatherTimer.Start();
            app.Run();
            return;
        }
        using var monitor = new StatusMonitor(() => new AppSettings());
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Console.WriteLine(monitor.GetStatusSummary());
            app.Shutdown();
        };
        timer.Start();
        app.Run();
    }

    private static void VerifyImageClipboard(string path, string extension)
    {
        if (!File.Exists(path) || Path.GetExtension(path) != extension ||
            !System.Windows.Clipboard.ContainsImage() || !System.Windows.Clipboard.ContainsFileDropList() ||
            !System.Windows.Clipboard.GetFileDropList().Cast<string>().Contains(path))
            throw new Exception($"剪贴板图片或文件缺失：{path}");
    }
}
