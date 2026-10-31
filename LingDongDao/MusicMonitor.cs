using System.Windows.Threading;
using Windows.Media.Control;

namespace LingDongDao;

internal sealed record MusicSnapshot(string Source, string Title, string Artist,
    bool IsPlaying = true, TimeSpan Position = default, TimeSpan Duration = default)
{
    public string PlayerName => MusicMonitor.PlayerName(Source);
}

internal enum MusicControl { Previous, Toggle, Next }

internal sealed class MusicMonitor : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _activeSession;
    private readonly MusicFallbackReader _fallback = new();
    private MusicSnapshot? _current;
    private bool _busy;
    private bool _disposed;

    public event Action<MusicSnapshot>? Updated;
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

    public async Task<bool> ControlAsync(MusicControl command)
    {
        MusicSnapshot? current = _current;
        if (current is null) return false;
        if (_activeSession is not null)
        {
            try
            {
                bool result = command switch
                {
                    MusicControl.Previous => await _activeSession.TrySkipPreviousAsync(),
                    MusicControl.Next => await _activeSession.TrySkipNextAsync(),
                    _ => await _activeSession.TryTogglePlayPauseAsync()
                };
                if (result) return true;
            }
            catch (Exception) { _activeSession = null; }
        }
        bool sent = await Task.Run(() => PlayerTransport.TrySend(current.Source, command));
        if (!sent) return false;
        if (!current.Source.Contains("kugou", StringComparison.OrdinalIgnoreCase))
        {
            _ = CheckAsync();
            return true;
        }
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(400);
            await CheckAsync();
            MusicSnapshot? updated = _current;
            if (updated is null) continue;
            bool samePlayer = updated.Source.Contains("kugou", StringComparison.OrdinalIgnoreCase);
            if (command == MusicControl.Toggle && samePlayer &&
                updated.IsPlaying != current.IsPlaying) return true;
            if (command != MusicControl.Toggle && samePlayer &&
                (updated.Title != current.Title || updated.Artist != current.Artist ||
                 updated.Position + TimeSpan.FromSeconds(5) < current.Position)) return true;
        }
        return false;
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        if (_activeSession is null || _current?.Duration <= TimeSpan.Zero) return false;
        try { return await _activeSession.TryChangePlaybackPositionAsync(position.Ticks); }
        catch (Exception) { return false; }
    }

    public bool CanSeek => _activeSession is not null && _current?.Duration > TimeSpan.Zero;

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
                foreach (GlobalSystemMediaTransportControlsSession session in _manager.GetSessions()
                    .OrderByDescending(item => item.GetPlaybackInfo()?.PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing))
                {
                    string source = session.SourceAppUserModelId ?? "";
                    var playbackStatus = session.GetPlaybackInfo()?.PlaybackStatus;
                    if (!IsSupportedSource(source) ||
                        playbackStatus is not (GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused))
                        continue;

                    GlobalSystemMediaTransportControlsSessionMediaProperties? properties =
                        await session.TryGetMediaPropertiesAsync();
                    if (_disposed || !_settings().MusicEnabled) return;
                    string title = string.IsNullOrWhiteSpace(properties?.Title) ? "正在播放音乐" : properties.Title.Trim();
                    string artist = properties?.Artist?.Trim() ?? "";
                    TimeSpan position = TimeSpan.Zero, duration = TimeSpan.Zero;
                    try
                    {
                        var timeline = session.GetTimelineProperties();
                        position = timeline.Position;
                        duration = timeline.EndTime - timeline.StartTime;
                    }
                    catch (Exception) { /* Some sessions have no timeline. */ }
                    _activeSession = session;
                    bool playing = playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    SetCurrent(new MusicSnapshot(source, title, artist, playing, position, duration),
                        playing ? $"Windows 媒体会话：{title}" : "播放器已暂停");
                    return;
                }
            }
            catch (Exception) { _manager = null; }

            _activeSession = null;
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
        _current = snapshot;
        Updated?.Invoke(snapshot);
    }

    private void ClearCurrent()
    {
        if (_current is null) return;
        _current = null;
        _activeSession = null;
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
