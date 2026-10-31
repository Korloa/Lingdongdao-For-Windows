using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LingDongDao;

internal sealed class MusicFallbackReader
{
    private readonly NetEasePlaybackReader _netEase = new();
    private readonly KugouProgressReader _kugouProgress = new();
    private readonly KugouTrackReader _kugouTrack = new();
    private readonly Dictionary<int, DateTime> _lastKugouAudio = new();
    private (int ProcessId, string Artist, string Title, DateTime SeenUtc)? _lastKugouTrack;

    public string StatusText { get; private set; } = "未检查播放器";

    public MusicSnapshot? Read()
    {
        // Native playback status is available even when the player does not publish SMTC.
        MusicSnapshot? netEase = _netEase.Read();
        var kugou = Process.GetProcessesByName("KuGou");
        try
        {
            if (kugou.Length == 0)
            {
                StatusText = netEase is null ? _netEase.StatusText :
                    netEase.IsPlaying ? $"网易云本地播放状态：{netEase.Title}" : "网易云已暂停";
                return netEase;
            }

            HashSet<int> audible = AudibleProcessIds();
            MusicSnapshot? pausedKugou = null;
            foreach (Process process in kugou)
            {
                string? title = PlayerWindowTitles.Find(process.Id, "酷狗音乐");
                var track = _kugouTrack.Resolve(title);
                if (track is null && _lastKugouTrack is { } recent &&
                    recent.ProcessId == process.Id &&
                    DateTime.UtcNow - recent.SeenUtc < TimeSpan.FromSeconds(12))
                    track = (recent.Artist, recent.Title);
                if (track is null) continue;
                if (title is not null)
                    _lastKugouTrack = (process.Id, track.Value.Artist, track.Value.Title, DateTime.UtcNow);
                if (audible.Contains(process.Id)) _lastKugouAudio[process.Id] = DateTime.UtcNow;
                var progress = _kugouProgress.Read(process.Id, track.Value.Artist + "|" + track.Value.Title);
                bool playing = (_lastKugouAudio.TryGetValue(process.Id, out DateTime lastAudio) &&
                                DateTime.UtcNow - lastAudio <= TimeSpan.FromSeconds(5)) ||
                               DateTime.UtcNow - _kugouProgress.LastGrowthUtc <= TimeSpan.FromSeconds(5);
                var snapshot = new MusicSnapshot("KuGou.exe", track.Value.Title, track.Value.Artist,
                    playing, progress.Position, progress.Duration);
                if (playing)
                {
                    StatusText = $"酷狗正在播放：{snapshot.Title}";
                    return snapshot;
                }
                pausedKugou ??= snapshot;
            }
            if (netEase?.IsPlaying == true)
            {
                StatusText = $"网易云本地播放状态：{netEase.Title}";
                return netEase;
            }
            if (pausedKugou is not null)
            {
                StatusText = "酷狗已暂停";
                return pausedKugou;
            }
            StatusText = netEase is null ? "酷狗已运行，未检测到正在播放的歌曲" : "网易云已暂停";
            return netEase;
        }
        finally
        {
            foreach (Process process in kugou) process.Dispose();
        }
    }

    internal static (string Artist, string Title)? ParseKugouTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string title = raw.Trim();
        // The player scrolls its caption, so text can follow the brand suffix.
        int suffix = title.IndexOf(" - 酷狗音乐", StringComparison.Ordinal);
        if (suffix < 0) return null;
        title = title[..suffix].Trim();
        if (title.Length == 0) return null;
        // KuGou also names its desktop lyrics window "桌面歌词 - 酷狗音乐".
        // Only the player window has the "artist - song - 酷狗音乐" shape.
        int separator = title.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0 || string.IsNullOrWhiteSpace(title[(separator + 3)..]) ||
            title.Equals("桌面歌词", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("桌面音乐", StringComparison.OrdinalIgnoreCase)) return null;
        return (title[..separator].Trim(), title[(separator + 3)..].Trim());
    }

    internal static HashSet<int> AudibleProcessIds()
    {
        var result = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (MMDevice device in devices)
            {
                using (device)
                {
                    using var sessions = device.AudioSessionManager.Sessions;
                    for (int index = 0; index < sessions.Count; index++)
                    {
                        AudioSessionControl session = sessions[index];
                        using (session)
                        {
                            try
                            {
                                if (session.State == AudioSessionState.AudioSessionStateActive &&
                                    session.AudioMeterInformation.MasterPeakValue > 0.0001f)
                                    result.Add(checked((int)session.GetProcessID));
                            }
                            catch (Exception) { /* Other applications may close a session during enumeration. */ }
                        }
                    }
                }
            }
        }
        catch (Exception) { /* No usable output endpoint. */ }
        return result;
    }
}

internal static class PlayerWindowTitles
{
    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder title, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    public static IntPtr FindKugouPlayerHandle(int processId)
    {
        IntPtr best = IntPtr.Zero, fallback = IntPtr.Zero;
        long bestArea = -1, fallbackArea = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner != processId) return true;
            var className = new StringBuilder(64);
            if (GetClassName(handle, className, className.Capacity) == 0 ||
                !className.ToString().Equals("kugou_ui", StringComparison.Ordinal)) return true;
            if (!GetWindowRect(handle, out var rect)) return true;
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > fallbackArea) { fallback = handle; fallbackArea = area; }
            var title = new StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            if (title.ToString().Contains("酷狗音乐", StringComparison.Ordinal) &&
                !title.ToString().StartsWith("桌面歌词", StringComparison.Ordinal) &&
                area > bestArea) { best = handle; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best != IntPtr.Zero ? best : fallback;
    }

    public static string? Find(int processId, string contains)
    {
        string? found = null;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner != processId) return true;
            var buffer = new StringBuilder(512);
            if (GetWindowText(handle, buffer, buffer.Capacity) == 0) return true;
            string title = buffer.ToString();
            if (!title.Contains(" - " + contains, StringComparison.Ordinal)) return true;
            if (title.StartsWith("桌面歌词", StringComparison.Ordinal) ||
                title.StartsWith("桌面音乐", StringComparison.Ordinal)) return true;
            found = title;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}

internal sealed class KugouTrackReader
{
    private readonly string _iniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KuGou8", "KuGou.ini");
    private string? _mismatch;
    private DateTime _mismatchSinceUtc;

    public (string Artist, string Title)? Resolve(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle) ||
            !windowTitle.Contains(" - 酷狗音乐", StringComparison.Ordinal) ||
            windowTitle.StartsWith("桌面歌词", StringComparison.Ordinal) ||
            windowTitle.StartsWith("桌面音乐", StringComparison.Ordinal)) return null;
        var windowTrack = MusicFallbackReader.ParseKugouTitle(windowTitle);
        if (!File.Exists(_iniPath)) return windowTrack;
        var value = new StringBuilder(512);
        if (GetPrivateProfileString("PlaybackState", "LastPlayingTitleName", "",
                value, (uint)value.Capacity, _iniPath) == 0) return windowTrack;
        string stored = value.ToString().Trim();
        int separator = stored.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0 || separator + 3 >= stored.Length) return windowTrack;
        var full = (Artist: stored[..separator].Trim(), Title: stored[(separator + 3)..].Trim());
        // A saved title can lag behind the actual player; require the moving
        // caption to contain a recognizable part of the same song.
        if (windowTrack is null) return full;
        if (windowTrack.Value.Title.Length >= 3 &&
            full.Title.Contains(windowTrack.Value.Title, StringComparison.OrdinalIgnoreCase))
        {
            _mismatch = null;
            return full;
        }
        string mismatch = windowTrack.Value.Artist + "|" + windowTrack.Value.Title;
        if (_mismatch != mismatch)
        {
            _mismatch = mismatch;
            _mismatchSinceUtc = DateTime.UtcNow;
        }
        if (DateTime.UtcNow - _mismatchSinceUtc < TimeSpan.FromSeconds(3))
            return null; // Wait for KuGou.ini to catch up instead of showing a clipped artist.
        return windowTrack;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileString(string section, string key,
        string defaultValue, StringBuilder value, uint size, string fileName);
}
