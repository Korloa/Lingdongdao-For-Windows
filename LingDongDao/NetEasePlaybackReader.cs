using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LingDongDao;

internal sealed class NetEasePlaybackReader
{
    // Signatures and structure offsets are specific to the current Windows client.
    // They are checked before use; a future client update can fall back to SMTC.
    private const string PlayerPattern = "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 0D ? ? ? ? E8 ? ? ? ? 90 48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 05 ? ? ? ? 48 8D A5 ? ? ? ? 5F 5D C3 CC CC CC CC CC 48 89 4C 24 ? 55 57 48 81 EC ? ? ? ? 48 8D 6C 24 ? 48 8D 7C 24";
    private const string SchedulePattern = "66 0F 2E 0D ? ? ? ? 7A ? 75 ? 66 0F 2E 15";
    private static readonly byte?[] PlayerSignature = ParsePattern(PlayerPattern);
    private static readonly byte?[] ScheduleSignature = ParsePattern(SchedulePattern);
    private readonly Dictionary<string, (DateTime Stamp, int PlayerRva, int ScheduleRva)> _moduleRvas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, Dictionary<string, (string Title, string Artist, TimeSpan Duration)> Tracks)> _playlists = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _playlistFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetEase", "CloudMusic", "WebData", "file");

    public string StatusText { get; private set; } = "未检测到网易云";
    internal string ProbeDetails { get; private set; } = "";

    public MusicSnapshot? Read()
    {
        Process[] processes = Process.GetProcessesByName("cloudmusic");
        try
        {
            var probes = new List<string>();
            bool paused = false;
            string? issue = null;
            if (processes.Length == 0)
            {
                StatusText = "网易云未运行";
                ProbeDetails = StatusText;
                return null;
            }
            foreach (Process process in processes)
            {
                try
                {
                    ProcessModule? module = process.Modules.Cast<ProcessModule>().FirstOrDefault(
                        item => item.ModuleName.Equals("cloudmusic.dll", StringComparison.OrdinalIgnoreCase));
                    if (module is null) continue;
                    var offsets = GetModuleRvas(module.FileName);
                    if (offsets.PlayerRva < 0)
                    {
                        issue = "网易云版本的播放状态特征未匹配";
                        probes.Add($"PID {process.Id}: 特征未匹配");
                        continue;
                    }
                    using var memory = new ReadOnlyProcessMemory(process.Id);
                    long playerAddress = module.BaseAddress.ToInt64() + offsets.PlayerRva;
                    int displacement = memory.ReadInt32(playerAddress + 3);
                    long player = playerAddress + 7 + displacement;
                    int state = memory.ReadInt32(player + 0x60);
                    probes.Add($"PID {process.Id}: 状态={state}");
                    if (state == 2) paused = true;
                    if (state is not 1 and not 2) continue;
                    string id = ReadSongId(memory, player);
                    if (string.IsNullOrEmpty(id))
                    {
                        issue = "网易云未读到歌曲标识";
                        probes.Add($"PID {process.Id}: 无歌曲标识");
                        continue;
                    }
                    var track = FindTrack(id);
                    if (track is null)
                    {
                        probes.Add($"PID {process.Id}: 歌曲标识未匹配本地列表");
                        issue = "网易云歌名未在本地列表中找到";
                        continue;
                    }
                    TimeSpan position = TimeSpan.Zero;
                    if (offsets.ScheduleRva >= 0)
                    {
                        long scheduleAddress = module.BaseAddress.ToInt64() + offsets.ScheduleRva;
                        int scheduleDisplacement = memory.ReadInt32(scheduleAddress + 4);
                        double seconds = memory.ReadDouble(scheduleAddress + 8 + scheduleDisplacement);
                        if (double.IsFinite(seconds) && seconds >= 0 && seconds < 86400)
                            position = TimeSpan.FromSeconds(seconds);
                    }
                    TimeSpan duration = track.Value.Duration;
                    if (duration <= TimeSpan.Zero)
                    {
                        double seconds = memory.ReadDouble(player + 0xa8);
                        if (double.IsFinite(seconds) && seconds > 0 && seconds < 86400)
                            duration = TimeSpan.FromSeconds(seconds);
                    }
                    if (duration > TimeSpan.Zero && position > duration) position = duration;
                    StatusText = state == 1 ? $"网易云本地播放状态：{track.Value.Title}" : "网易云已暂停";
                    probes.Add($"PID {process.Id}: 进度={position.TotalSeconds:0}/{duration.TotalSeconds:0}");
                    ProbeDetails = string.Join("；", probes);
                    return new MusicSnapshot("cloudmusic.exe", track.Value.Title, track.Value.Artist,
                        state == 1, position, duration);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    probes.Add($"PID {process.Id}: {exception.GetType().Name}");
                    issue = $"网易云本地读取失败：{exception.Message}";
                }
            }
            ProbeDetails = string.Join("；", probes);
            StatusText = paused ? "网易云已暂停" : issue ?? "网易云已运行，未检测到播放";
            return null;
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private (int PlayerRva, int ScheduleRva) GetModuleRvas(string modulePath)
    {
        DateTime stamp = File.GetLastWriteTimeUtc(modulePath);
        if (_moduleRvas.TryGetValue(modulePath, out var cached) && cached.Stamp == stamp)
            return (cached.PlayerRva, cached.ScheduleRva);
        byte[] image = File.ReadAllBytes(modulePath);
        int pe = BitConverter.ToInt32(image, 0x3c);
        int sectionCount = BitConverter.ToUInt16(image, pe + 6);
        int optionalSize = BitConverter.ToUInt16(image, pe + 20);
        int sectionTable = pe + 24 + optionalSize;
        int playerRva = -1, scheduleRva = -1;
        for (int i = 0; i < sectionCount; i++)
        {
            int section = sectionTable + i * 40;
            string name = Encoding.ASCII.GetString(image, section, 8).TrimEnd('\0');
            if (name != ".text") continue;
            int virtualAddress = BitConverter.ToInt32(image, section + 12);
            int rawSize = BitConverter.ToInt32(image, section + 16);
            int rawPointer = BitConverter.ToInt32(image, section + 20);
            int playerOffset = FindPattern(image, rawPointer, rawSize, PlayerSignature);
            if (playerOffset >= 0) playerRva = virtualAddress + playerOffset - rawPointer;
            int scheduleOffset = FindPattern(image, rawPointer, rawSize, ScheduleSignature);
            if (scheduleOffset >= 0) scheduleRva = virtualAddress + scheduleOffset - rawPointer;
            break;
        }
        _moduleRvas[modulePath] = (stamp, playerRva, scheduleRva);
        return (playerRva, scheduleRva);
    }

    internal static int FindPattern(byte[] image, int start, int length, byte?[] pattern)
    {
        if (start < 0 || length < pattern.Length || start > image.Length - length) return -1;
        int limit = start + length - pattern.Length;
        byte first = pattern[0] ?? throw new ArgumentException("Pattern needs a fixed first byte.");
        for (int index = start; index <= limit; index++)
        {
            index = Array.IndexOf(image, first, index, limit - index + 1);
            if (index < 0) break;
            bool matched = true;
            for (int j = 1; j < pattern.Length; j++)
            {
                if (pattern[j] is byte expected && image[index + j] != expected)
                {
                    matched = false;
                    break;
                }
            }
            if (matched) return index;
        }
        return -1;
    }

    private static byte?[] ParsePattern(string signature) => signature.Split(' ')
        .Select(part => part == "?" ? (byte?)null : Convert.ToByte(part, 16)).ToArray();

    private static string ReadSongId(ReadOnlyProcessMemory memory, long player)
    {
        long info = memory.ReadInt64(player + 0x50);
        if (info == 0) return string.Empty;
        long value = info + 0x10;
        long length = memory.ReadInt64(value + 0x10);
        if (length is < 1 or > 256) return string.Empty;
        long address = length <= 15 ? value : memory.ReadInt64(value);
        if (address == 0) return string.Empty;
        string raw = Encoding.UTF8.GetString(memory.ReadBytes(address, (int)length)).TrimEnd('\0');
        int separator = raw.IndexOf('_');
        return separator > 0 ? raw[..separator] : raw;
    }

    private (string Title, string Artist, TimeSpan Duration)? FindTrack(string id)
    {
        foreach (string name in new[] { "playingList", "fmPlay" })
        {
            string path = Path.Combine(_playlistFolder, name);
            if (!File.Exists(path)) continue;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (!_playlists.TryGetValue(path, out var cached) || cached.Stamp != stamp)
            {
                cached = (stamp, ReadPlaylist(path, name == "fmPlay" ? "queue" : "list"));
                _playlists[path] = cached;
            }
            if (cached.Tracks.TryGetValue(id, out var track)) return track;
        }
        return null;
    }

    private static Dictionary<string, (string Title, string Artist, TimeSpan Duration)> ReadPlaylist(string path, string listKey)
    {
        var result = new Dictionary<string, (string, string, TimeSpan)>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.TryGetProperty(listKey, out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("id", out JsonElement idValue)) continue;
            string id = idValue.ToString();
            JsonElement track = entry;
            if (listKey == "list" && !entry.TryGetProperty("track", out track)) continue;
            if (track.ValueKind != JsonValueKind.Object) continue;
            if (!track.TryGetProperty("name", out JsonElement titleValue)) continue;
            string title = titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(title)) continue;
            string artist = string.Empty;
            if (track.TryGetProperty("artists", out JsonElement artists) && artists.ValueKind == JsonValueKind.Array)
                artist = string.Join("、", artists.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            TimeSpan duration = TimeSpan.Zero;
            if (track.TryGetProperty("duration", out JsonElement length))
            {
                if (length.ValueKind == JsonValueKind.Object && length.TryGetProperty("dt", out JsonElement dt))
                    length = dt;
                if (length.ValueKind == JsonValueKind.Number && length.TryGetDouble(out double milliseconds) &&
                    double.IsFinite(milliseconds) && milliseconds > 0 && milliseconds < 86400000)
                    duration = TimeSpan.FromMilliseconds(milliseconds);
            }
            result[id] = (title, artist, duration);
        }
        return result;
    }
}

internal sealed class ReadOnlyProcessMemory : IDisposable
{
    private readonly IntPtr _handle;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public ReadOnlyProcessMemory(int processId)
    {
        _handle = OpenProcess(0x0010, false, processId);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("无法读取播放器进程");
    }

    public byte[] ReadBytes(long address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(_handle, new IntPtr(address), buffer, length, out IntPtr read) ||
            read.ToInt64() != length) throw new InvalidOperationException("播放状态暂时不可读");
        return buffer;
    }

    public int ReadInt32(long address) => BitConverter.ToInt32(ReadBytes(address, 4));
    public long ReadInt64(long address) => BitConverter.ToInt64(ReadBytes(address, 8));
    public double ReadDouble(long address) => BitConverter.ToDouble(ReadBytes(address, 8));
    public void Dispose() => CloseHandle(_handle);
}
