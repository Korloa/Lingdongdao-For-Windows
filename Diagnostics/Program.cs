using LingDongDao;
using System.IO;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
}
