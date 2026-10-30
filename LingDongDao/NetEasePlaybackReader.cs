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
    private static readonly byte?[] PlayerSignature = ParsePattern(PlayerPattern);
    private readonly Dictionary<string, (DateTime Stamp, int Rva)> _playerRvas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, Dictionary<string, (string Title, string Artist)> Tracks)> _playlists = new(StringComparer.OrdinalIgnoreCase);
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
                    int rva = GetPlayerRva(module.FileName);
                    if (rva < 0)
                    {
                        issue = "网易云版本的播放状态特征未匹配";
                        probes.Add($"PID {process.Id}: 特征未匹配");
                        continue;
                    }
                    using var memory = new ReadOnlyProcessMemory(process.Id);
                    long playerAddress = module.BaseAddress.ToInt64() + rva;
                    int displacement = memory.ReadInt32(playerAddress + 3);
                    long player = playerAddress + 7 + displacement;
                    int state = memory.ReadInt32(player + 0x60);
                    probes.Add($"PID {process.Id}: 状态={state}");
                    if (state == 2) paused = true;
                    if (state == 2)
                    {
                        string pausedId = ReadSongId(memory, player);
                        probes.Add($"PID {process.Id}: 暂停歌曲元数据{(pausedId.Length > 0 && FindTrack(pausedId) is not null ? "已匹配" : "未匹配")}");
                    }
                    if (state != 1) continue;
                    string id = ReadSongId(memory, player);
                    if (string.IsNullOrEmpty(id))
                    {
                        issue = "网易云正在播放，但未读到歌曲标识";
                        probes.Add($"PID {process.Id}: 正在播放但无歌曲标识");
                        continue;
                    }
                    var track = FindTrack(id);
                    if (track is null)
                    {
                        probes.Add($"PID {process.Id}: 歌曲标识未匹配本地列表");
                        issue = "网易云正在播放，歌名未在本地列表中找到";
                        continue;
                    }
                    StatusText = $"网易云本地播放状态：{track.Value.Title}";
                    ProbeDetails = string.Join("；", probes);
                    return new MusicSnapshot("cloudmusic.exe", track.Value.Title, track.Value.Artist);
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

    private int GetPlayerRva(string modulePath)
    {
        DateTime stamp = File.GetLastWriteTimeUtc(modulePath);
        if (_playerRvas.TryGetValue(modulePath, out var cached) && cached.Stamp == stamp)
            return cached.Rva;
        byte[] image = File.ReadAllBytes(modulePath);
        int pe = BitConverter.ToInt32(image, 0x3c);
        int sectionCount = BitConverter.ToUInt16(image, pe + 6);
        int optionalSize = BitConverter.ToUInt16(image, pe + 20);
        int sectionTable = pe + 24 + optionalSize;
        int rva = -1;
        for (int i = 0; i < sectionCount; i++)
        {
            int section = sectionTable + i * 40;
            string name = Encoding.ASCII.GetString(image, section, 8).TrimEnd('\0');
            if (name != ".text") continue;
            int virtualAddress = BitConverter.ToInt32(image, section + 12);
            int rawSize = BitConverter.ToInt32(image, section + 16);
            int rawPointer = BitConverter.ToInt32(image, section + 20);
            int offset = FindPattern(image, rawPointer, rawSize, PlayerSignature);
            if (offset >= 0) rva = virtualAddress + offset - rawPointer;
            break;
        }
        _playerRvas[modulePath] = (stamp, rva);
        return rva;
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

    private (string Title, string Artist)? FindTrack(string id)
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

    private static Dictionary<string, (string Title, string Artist)> ReadPlaylist(string path, string listKey)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
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
            result[id] = (title, artist);
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
    public void Dispose() => CloseHandle(_handle);
}
