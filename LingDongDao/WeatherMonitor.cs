using System.Net.Http;
using System.Windows.Threading;

namespace LingDongDao;

internal sealed class WeatherMonitor : IDisposable
{
    private const string ForecastUrl =
        "https://api.open-meteo.com/v1/forecast?latitude=30.805&longitude=103.881" +
        "&hourly=weather_code&timezone=Asia%2FShanghai&forecast_hours=";

    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private bool _busy;
    private bool _refreshPending;
    private bool _disposed;
    private DateTime? _lastAlertedHour;

    public event Action<IslandNotice>? Notice;
    public string StatusText { get; private set; } = "尚未查询";

    public WeatherMonitor(Func<AppSettings> settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => _ = CheckAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public void RefreshSettings()
    {
        if (_busy) _refreshPending = true;
        else _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_busy || _disposed) return;
        AppSettings settings = _settings();
        if (!settings.WeatherEnabled)
        {
            StatusText = "已关闭";
            return;
        }

        _busy = true;
        try
        {
            // Include one extra hour so an event near the end of the range is available.
            string json = await _http.GetStringAsync(ForecastUrl + (settings.WeatherLookaheadHours + 1));
            if (_disposed) return;
            settings = _settings();
            if (!settings.WeatherEnabled)
            {
                StatusText = "已关闭";
                return;
            }
            DateTime nowInChina = DateTime.UtcNow.AddHours(8);
            DateTime? forecastHour = ThunderstormForecast.FindFirst(json, nowInChina,
                settings.WeatherLookaheadHours);
            if (forecastHour is null)
            {
                StatusText = $"未来 {settings.WeatherLookaheadHours} 小时未预报雷雨";
                return;
            }

            DateTime hour = forecastHour.Value;
            string when = hour.Date == nowInChina.Date ? "今天" :
                hour.Date == nowInChina.Date.AddDays(1) ? "明天" : $"{hour:M月d日}";
            StatusText = $"预计 {when} {hour:HH}:00 左右有雷雨";
            if (_lastAlertedHour is DateTime last && Math.Abs((hour - last).TotalHours) < 6)
                return;

            _lastAlertedHour = hour;
            Notice?.Invoke(new IslandNotice(NoticeKind.Thunderstorm, "未来可能有雷雨",
                $"郫都区 · {when} {hour:HH}:00 左右"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                           FormatException or System.Text.Json.JsonException or KeyNotFoundException or
                                           InvalidOperationException)
        {
            if (!_disposed) StatusText = $"查询失败：{exception.Message}";
        }
        finally
        {
            _busy = false;
            if (_refreshPending && !_disposed)
            {
                _refreshPending = false;
                _ = CheckAsync();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _http.Dispose();
    }
}
