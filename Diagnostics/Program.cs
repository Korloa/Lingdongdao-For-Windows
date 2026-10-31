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
        if (args.Contains("--music-sessions"))
        {
            Task.Run(async () =>
            {
                var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var sessions = manager.GetSessions();
                Console.WriteLine($"系统媒体会话数：{sessions.Count}");
                foreach (var session in sessions)
                {
                    var info = session.GetPlaybackInfo();
                    var media = await session.TryGetMediaPropertiesAsync();
                    Console.WriteLine($"来源={session.SourceAppUserModelId} 状态={info?.PlaybackStatus} 歌名={media?.Title} 歌手={media?.Artist}");
                }
            }).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--music-fallback-live"))
        {
            var reader = new MusicFallbackReader();
            MusicSnapshot? snapshot = Task.Run(reader.Read).GetAwaiter().GetResult();
            Console.WriteLine($"本地播放器：{reader.StatusText}");
            Console.WriteLine($"卡片：{(snapshot is null ? "无" : snapshot.PlayerName + " · " + snapshot.Title)}");
            return;
        }
        if (args.Contains("--kugou-title-probe"))
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("KuGou"))
            {
                using (process)
                {
                    string? title = PlayerWindowTitles.Find(process.Id, "酷狗音乐");
                    var track = new KugouTrackReader().Resolve(title);
                    Console.WriteLine($"酷狗 PID={process.Id} 窗口={title ?? "无"} 歌曲={track?.Artist} / {track?.Title}");
                }
            }
            Console.WriteLine("发声 PID：" + string.Join(",", MusicFallbackReader.AudibleProcessIds()));
            return;
        }
        if (args.Contains("--kugou-progress-live"))
        {
            using var process = System.Diagnostics.Process.GetProcessesByName("KuGou")
                .FirstOrDefault(item => PlayerWindowTitles.FindKugouPlayerHandle(item.Id) != IntPtr.Zero);
            if (process is null) throw new Exception("找不到酷狗主窗口");
            var reader = new KugouProgressReader();
            var first = Task.Run(() => reader.Read(process.Id, "live")).GetAwaiter().GetResult();
            Thread.Sleep(3000);
            var second = Task.Run(() => reader.Read(process.Id, "live")).GetAwaiter().GetResult();
            Console.WriteLine($"酷狗进度：{first.Position}/{first.Duration} -> {second.Position}/{second.Duration}");
            return;
        }
        if (args.Contains("--netease-probe"))
        {
            var reader = new NetEasePlaybackReader();
            MusicSnapshot? snapshot = Task.Run(reader.Read).GetAwaiter().GetResult();
            Console.WriteLine($"网易云：{reader.StatusText}");
            Console.WriteLine($"进程状态：{reader.ProbeDetails}");
            Console.WriteLine($"卡片：{(snapshot is null ? "无" : snapshot.Title)}");
            return;
        }
        if (args.Contains("--audio-probe"))
        {
            var audible = Task.Run(MusicFallbackReader.AudibleProcessIds).GetAwaiter().GetResult();
            Console.WriteLine($"正在发声的进程数：{audible.Count}");
            return;
        }
        if (args.Contains("--music-control-live"))
        {
            int index = Array.IndexOf(args, "--music-control-live");
            MusicControl command = index + 1 < args.Length ? args[index + 1].ToLowerInvariant() switch
            {
                "previous" => MusicControl.Previous,
                "toggle" => MusicControl.Toggle,
                "next" => MusicControl.Next,
                _ => throw new ArgumentException("Use previous, toggle, or next.")
            } : throw new ArgumentException("Missing music command.");
            var reader = new NetEasePlaybackReader();
            MusicSnapshot? snapshot = Task.Run(reader.Read).GetAwaiter().GetResult();
            if (snapshot is null) throw new Exception("没有可控制的网易云歌曲。");
            bool sent = PlayerTransport.TrySend(snapshot.Source, command);
            Console.WriteLine($"控制发送：{sent} · {command}");
            return;
        }
        if (args.Contains("--kugou-control-live"))
        {
            int index = Array.IndexOf(args, "--kugou-control-live");
            MusicControl command = index + 1 < args.Length ? args[index + 1].ToLowerInvariant() switch
            {
                "previous" => MusicControl.Previous,
                "toggle" => MusicControl.Toggle,
                "next" => MusicControl.Next,
                _ => throw new ArgumentException("Use previous, toggle, or next.")
            } : throw new ArgumentException("Missing music command.");
            bool sent = PlayerTransport.TrySend("KuGou.exe", command);
            Console.WriteLine($"酷狗控制发送：{sent} · {command} · {PlayerTransport.LastStatus}");
            return;
        }
        if (args.Contains("--music-self-test"))
        {
            byte[] timeline = System.Text.Encoding.Unicode.GetBytes("1:23/4:56\0");
            if (!KugouProgressReader.TryParsePair(timeline, out int position, out int duration) ||
                position != 83 || duration != 296 ||
                KugouProgressReader.TryParsePair(System.Text.Encoding.Unicode.GetBytes("5:00/4:56\0"),
                    out _, out _))
                throw new Exception("酷狗进度文本解析错误");
            var track = MusicFallbackReader.ParseKugouTitle("歌手 - 歌名 - 酷狗音乐");
            var scrolling = MusicFallbackReader.ParseKugouTitle("ey Inspired - Taking Off - 酷狗音乐 Lak");
            if (track != ("歌手", "歌名") ||
                scrolling != ("ey Inspired", "Taking Off") ||
                MusicFallbackReader.ParseKugouTitle("酷狗音乐") is not null ||
                MusicFallbackReader.ParseKugouTitle("桌面歌词 - 酷狗音乐") is not null ||
                MusicFallbackReader.ParseKugouTitle("别的播放器") is not null)
                throw new Exception("酷狗窗口标题识别错误");
            byte[] bytes = [0, 1, 2, 1, 9, 3];
            byte?[] signature = [1, null, 3];
            if (NetEasePlaybackReader.FindPattern(bytes, 0, bytes.Length, signature) != 3 ||
                NetEasePlaybackReader.FindPattern(bytes, 0, 5, signature) != -1)
                throw new Exception("网易云播放状态特征扫描错误");
            Console.WriteLine("音乐标题与特征扫描检查通过。");
            return;
        }
        if (args.Contains("--hotkey-self-test"))
        {
            if (!GlobalHotkey.TryCapture(System.Windows.Input.Key.D5,
                    System.Windows.Input.ModifierKeys.Control, out string value) || value != "Ctrl+5" ||
                !GlobalHotkey.TryParse("shift+ctrl+F12", out var chord, out string normalized) ||
                normalized != "Ctrl+Shift+F12" || chord.Key != 0x7B ||
                !GlobalHotkey.TryFormatNative(0x4002, 0x31, out string native) || native != "Ctrl+1" ||
                GlobalHotkey.TryParse("Ctrl+BadKey", out _, out _) ||
                GlobalHotkey.TryParse("Ctrl+Ctrl+1", out _, out _))
                throw new Exception("快捷键解析或录入错误");
            var oldSettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Hotkey\":\"Ctrl+Alt+D\"}")!;
            oldSettings.Normalize();
            if (oldSettings.ImageFormatHotkey != "Ctrl+0" ||
                oldSettings.ImageStyleHotkey != "Ctrl+1" ||
                oldSettings.ImageCompressHotkey != "Ctrl+2")
                throw new Exception("旧设置迁移快捷键失败");
            Console.WriteLine("快捷键录入与旧设置兼容检查通过。");
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
