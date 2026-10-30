using System.Windows.Threading;
using Windows.Media.Control;

namespace LingDongDao;

internal sealed record MusicSnapshot(string Source, string Title, string Artist)
{
    public string PlayerName => MusicMonitor.PlayerName(Source);
}

internal sealed class MusicMonitor : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly MusicFallbackReader _fallback = new();
    private MusicSnapshot? _current;
    private bool _busy;
    private bool _disposed;

    public event Action<MusicSnapshot>? Playing;
    public event Action? Stopped;
    public string StatusText { get; private set; } = "尚未检测";

    public MusicMonitor(Func<AppSettings> settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => _ = CheckAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public void RefreshSettings() => _ = CheckAsync();

    private async Task CheckAsync()
    {
        if (_busy || _disposed) return;
        if (!_settings().MusicEnabled)
        {
            StatusText = "已关闭";
            ClearCurrent();
            return;
        }

        _busy = true;
        try
        {
            try
            {
                _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                foreach (GlobalSystemMediaTransportControlsSession session in _manager.GetSessions())
                {
                    string source = session.SourceAppUserModelId ?? "";
                    if (!IsSupportedSource(source) ||
                        session.GetPlaybackInfo()?.PlaybackStatus !=
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        continue;

                    GlobalSystemMediaTransportControlsSessionMediaProperties? properties =
                        await session.TryGetMediaPropertiesAsync();
                    if (_disposed || !_settings().MusicEnabled) return;
                    string title = string.IsNullOrWhiteSpace(properties?.Title) ? "正在播放音乐" : properties.Title.Trim();
                    string artist = properties?.Artist?.Trim() ?? "";
                    SetCurrent(new MusicSnapshot(source, title, artist), $"Windows 媒体会话：{title}");
                    return;
                }
            }
            catch (Exception) { _manager = null; }

            MusicSnapshot? fallback = await Task.Run(_fallback.Read);
            if (_disposed || !_settings().MusicEnabled) return;
            if (fallback is not null) SetCurrent(fallback, _fallback.StatusText);
            else
            {
                StatusText = _fallback.StatusText;
                ClearCurrent();
            }
        }
        catch (Exception exception)
        {
            if (!_disposed) StatusText = $"检测失败：{exception.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetCurrent(MusicSnapshot snapshot, string status)
    {
        StatusText = status;
        if (snapshot == _current) return;
        _current = snapshot;
        Playing?.Invoke(snapshot);
    }

    private void ClearCurrent()
    {
        if (_current is null) return;
        _current = null;
        Stopped?.Invoke();
    }

    internal static bool IsSupportedSource(string source) =>
        source.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("netease", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("music.163", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("kugou", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("kgmusic", StringComparison.OrdinalIgnoreCase);

    internal static string PlayerName(string source) =>
        source.Contains("kugou", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("kgmusic", StringComparison.OrdinalIgnoreCase)
            ? "酷狗音乐" : "网易云音乐";

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}
