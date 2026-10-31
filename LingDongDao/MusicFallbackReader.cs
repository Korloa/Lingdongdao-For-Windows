using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LingDongDao;

internal sealed class MusicFallbackReader
{
    private readonly NetEasePlaybackReader _netEase = new();
    private readonly KugouProgressReader _kugouProgress = new();
    private readonly Dictionary<int, DateTime> _lastKugouAudio = new();

    public string StatusText { get; private set; } = "未检查播放器";

    public MusicSnapshot? Read()
    {
        // Native playback status is available even when the player does not publish SMTC.
        MusicSnapshot? netEase = _netEase.Read();
        if (netEase?.IsPlaying == true)
        {
            StatusText = netEase.IsPlaying ? $"网易云本地播放状态：{netEase.Title}" : "网易云已暂停";
            return netEase;
        }

        var kugou = Process.GetProcessesByName("KuGou");
        try
        {
            if (kugou.Length == 0)
            {
                StatusText = netEase is null ? _netEase.StatusText : "网易云已暂停";
                return netEase;
            }

            HashSet<int> audible = AudibleProcessIds();
            foreach (Process process in kugou)
            {
                string? title = PlayerWindowTitles.Find(process.Id, "酷狗音乐");
                var track = ParseKugouTitle(title);
                if (track is null) continue;
                if (audible.Contains(process.Id)) _lastKugouAudio[process.Id] = DateTime.UtcNow;
                bool playing = _lastKugouAudio.TryGetValue(process.Id, out DateTime lastAudio) &&
                               DateTime.UtcNow - lastAudio <= TimeSpan.FromSeconds(5);
                StatusText = playing ? $"酷狗本地音频：{track.Value.Title}" : "酷狗已暂停";
                var progress = _kugouProgress.Read(process.Id, track.Value.Artist + "|" + track.Value.Title);
                return new MusicSnapshot("KuGou.exe", track.Value.Title, track.Value.Artist,
                    playing, progress.Position, progress.Duration);
            }
            StatusText = netEase is null ? "酷狗已运行，未检测到正在发声的歌曲" : "网易云已暂停";
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
        const string suffix = "- 酷狗音乐";
        if (!title.EndsWith(suffix, StringComparison.Ordinal)) return null;
        title = title[..^suffix.Length].Trim();
        if (title.Length == 0) return null;
        int separator = title.IndexOf(" - ", StringComparison.Ordinal);
        if (separator >= 0 && string.IsNullOrWhiteSpace(title[(separator + 3)..])) return null;
        return separator < 0
            ? (string.Empty, title)
            : (title[..separator].Trim(), title[(separator + 3)..].Trim());
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
            if (!title.Contains(contains, StringComparison.Ordinal)) return true;
            found = title;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
