using LingDongDao;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
