using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using LingDongDao.Perception;

namespace LingDongDao;

internal enum IslandDisplayMode
{
    Automatic,
    Expanded,
    Hidden
}

internal sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IslandDisplayMode DisplayMode { get; set; } = IslandDisplayMode.Automatic;
    public double LineWidth { get; set; } = 58;
    public double LineThickness { get; set; } = 4;
    public double IslandWidth { get; set; } = 322;
    public double IslandHeight { get; set; } = 68;
    public double CornerRadius { get; set; } = 24;
    public double HorizontalPadding { get; set; } = 14;
    public double IconSize { get; set; } = 40;
    public double TitleFontSize { get; set; } = 15;
    public double DetailFontSize { get; set; } = 11;
    public double BackgroundOpacityPercent { get; set; } = 100;
    public double AnimationMilliseconds { get; set; } = 300;
    public double TopOffset { get; set; } = 1;
    public double NoticeSeconds { get; set; } = 3.5;
    public string Hotkey { get; set; } = "Ctrl+Alt+D";
    public string ImageFormatHotkey { get; set; } = "Ctrl+0";
    public string ImageStyleHotkey { get; set; } = "Ctrl+1";
    public string ImageCompressHotkey { get; set; } = "Ctrl+2";
    public bool StartWithWindows { get; set; }

    public bool ChargingEnabled { get; set; } = true;
    public bool ClipboardEnabled { get; set; } = true;
    public bool MusicEnabled { get; set; } = true;
    public PerceptionOptions Perception { get; set; } = new();
    public bool ImageConversionEnabled { get; set; } = true;
    public bool ImageStyleEnabled { get; set; } = true;
    public bool ImageCompressionEnabled { get; set; } = true;
    public bool HoverExpandEnabled { get; set; } = true;
    public double HoverHorizontalPadding { get; set; } = 0;
    public double HoverVerticalPadding { get; set; } = 2;
    public double ImageCornerRadius { get; set; } = 8;
    public double ImagePreviewSize { get; set; } = 260;
    public int FormatVisibleItems { get; set; } = 5;
    public double FormatItemWidth { get; set; } = 36;
    public double FormatSpacing { get; set; } = 4;
    public double FormatFontSize { get; set; } = 12;
    public double FormatSelectedFontSize { get; set; } = 14;
    public double FormatScrollMilliseconds { get; set; } = 230;
    public bool WifiConnectedEnabled { get; set; } = true;
    public bool WifiSlowEnabled { get; set; } = true;
    public bool NoInternetEnabled { get; set; } = true;
    public bool MemoryEnabled { get; set; } = true;
    public bool TemperatureEnabled { get; set; } = true;
    public bool BatteryLowEnabled { get; set; } = true;
    public bool WeatherEnabled { get; set; } = true;
    public int WeatherLookaheadHours { get; set; } = 24;

    public int WifiSlowMbps { get; set; } = 30;
    public int MemoryPercent { get; set; } = 85;
    public int CpuTemperatureC { get; set; } = 90;
    public int GpuTemperatureC { get; set; } = 88;
    public int BatteryLowPercent { get; set; } = 20;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Perception = Perception.Clone();
        return copy;
    }

    public void Normalize()
    {
        Perception ??= new PerceptionOptions();
        Perception.Normalize();
        if (!Enum.IsDefined(DisplayMode)) DisplayMode = IslandDisplayMode.Automatic;
        LineWidth = Math.Clamp(LineWidth, 20, 140);
        LineThickness = Math.Clamp(LineThickness, 1, 10);
        IslandWidth = Math.Clamp(IslandWidth, 220, 420);
        IslandHeight = Math.Clamp(IslandHeight, 60, 110);
        CornerRadius = Math.Clamp(CornerRadius, 10, 55);
        HorizontalPadding = Math.Clamp(HorizontalPadding, 8, 24);
        IconSize = Math.Clamp(IconSize, 28, 48);
        TitleFontSize = Math.Clamp(TitleFontSize, 12, 18);
        DetailFontSize = Math.Clamp(DetailFontSize, 9, 14);
        BackgroundOpacityPercent = Math.Clamp(BackgroundOpacityPercent, 70, 100);
        AnimationMilliseconds = Math.Clamp(AnimationMilliseconds, 120, 700);
        TopOffset = Math.Clamp(TopOffset, 0, 20);
        NoticeSeconds = Math.Clamp(NoticeSeconds, 1.5, 10);
        ImageCornerRadius = Math.Clamp(ImageCornerRadius, 0, 24);
        HoverHorizontalPadding = Math.Clamp(HoverHorizontalPadding, 0, 16);
        HoverVerticalPadding = Math.Clamp(HoverVerticalPadding, 0, 12);
        ImagePreviewSize = Math.Clamp(ImagePreviewSize, 140, 420);
        FormatVisibleItems = FormatVisibleItems <= 3 ? 3 : 5;
        FormatItemWidth = Math.Clamp(FormatItemWidth, 32, 52);
        FormatSpacing = Math.Clamp(FormatSpacing, 0, 14);
        FormatFontSize = Math.Clamp(FormatFontSize, 10, 16);
        FormatSelectedFontSize = Math.Clamp(FormatSelectedFontSize, FormatFontSize, 20);
        FormatScrollMilliseconds = Math.Clamp(FormatScrollMilliseconds, 100, 600);
        WifiSlowMbps = Math.Clamp(WifiSlowMbps, 5, 100);
        MemoryPercent = Math.Clamp(MemoryPercent, 60, 98);
        CpuTemperatureC = Math.Clamp(CpuTemperatureC, 70, 105);
        GpuTemperatureC = Math.Clamp(GpuTemperatureC, 65, 105);
        BatteryLowPercent = Math.Clamp(BatteryLowPercent, 5, 40);
        WeatherLookaheadHours = Math.Clamp(WeatherLookaheadHours, 6, 48);
        Hotkey = GlobalHotkey.CanonicalOrDefault(Hotkey, "Ctrl+Alt+D");
        ImageFormatHotkey = GlobalHotkey.CanonicalOrDefault(ImageFormatHotkey, "Ctrl+0");
        ImageStyleHotkey = GlobalHotkey.CanonicalOrDefault(ImageStyleHotkey, "Ctrl+1");
        ImageCompressHotkey = GlobalHotkey.CanonicalOrDefault(ImageCompressHotkey, "Ctrl+2");
    }
}

internal static class SettingsStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingDongDao", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        settings.Normalize();
        string path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporary, path, true);
    }
}

internal static class GlobalHotkey
{
    private const uint NoRepeat = 0x4000;

    public static string CanonicalOrDefault(string? text, string fallback) =>
        TryParse(text, out _, out string canonical) ? canonical : fallback;

    public static bool TryParse(string? text, out (uint Modifiers, uint Key) chord,
        out string canonical)
    {
        chord = default;
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        uint modifiers = NoRepeat;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in parts[..^1])
        {
            if (!names.Add(part)) return false;
            uint flag = part.ToLowerInvariant() switch
            {
                "ctrl" => 0x0002,
                "alt" => 0x0001,
                "shift" => 0x0004,
                "win" => 0x0008,
                _ => 0
            };
            if (flag == 0) return false;
            modifiers |= flag;
        }
        string keyName = parts[^1].ToUpperInvariant();
        uint key = keyName switch
        {
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "SPACE" => 0x20,
            _ when keyName.Length == 1 && char.IsAsciiLetterOrDigit(keyName[0]) => keyName[0],
            _ when keyName.Length is 2 or 3 && keyName[0] == 'F' &&
                   int.TryParse(keyName[1..], out int number) && number is >= 1 and <= 12 =>
                (uint)(0x70 + number - 1),
            _ => 0
        };
        if (key == 0) return false;
        chord = (modifiers, key);
        canonical = string.Join('+', new[]
        {
            (modifiers & 0x0002) != 0 ? "Ctrl" : null,
            (modifiers & 0x0001) != 0 ? "Alt" : null,
            (modifiers & 0x0004) != 0 ? "Shift" : null,
            (modifiers & 0x0008) != 0 ? "Win" : null,
            keyName.Length == 1 ? keyName : keyName[0] == 'F' ? keyName :
                char.ToUpperInvariant(keyName[0]) + keyName[1..].ToLowerInvariant()
        }.Where(part => part is not null));
        return true;
    }

    public static bool TryCapture(Key key, ModifierKeys modifiers, out string text)
    {
        text = string.Empty;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;
        string keyName = key switch
        {
            >= Key.A and <= Key.Z => key.ToString().ToUpperInvariant(),
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            >= Key.F1 and <= Key.F12 => key.ToString().ToUpperInvariant(),
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
            Key.Space => "Space",
            _ => string.Empty
        };
        if (keyName.Length == 0 || modifiers == ModifierKeys.None) return false;
        string value = string.Join('+', new[]
        {
            modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl" : null,
            modifiers.HasFlag(ModifierKeys.Alt) ? "Alt" : null,
            modifiers.HasFlag(ModifierKeys.Shift) ? "Shift" : null,
            modifiers.HasFlag(ModifierKeys.Windows) ? "Win" : null,
            keyName
        }.Where(part => part is not null));
        return TryParse(value, out _, out text);
    }

    public static bool TryFormatNative(uint modifiers, uint key, out string text)
    {
        text = string.Empty;
        string keyName = key switch
        {
            >= 0x30 and <= 0x39 => ((char)key).ToString(),
            >= 0x41 and <= 0x5A => ((char)key).ToString(),
            >= 0x70 and <= 0x7B => $"F{key - 0x70 + 1}",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x20 => "Space",
            _ => string.Empty
        };
        if (keyName.Length == 0) return false;
        string value = string.Join('+', new[]
        {
            (modifiers & 0x0002) != 0 ? "Ctrl" : null,
            (modifiers & 0x0001) != 0 ? "Alt" : null,
            (modifiers & 0x0004) != 0 ? "Shift" : null,
            (modifiers & 0x0008) != 0 ? "Win" : null,
            keyName
        }.Where(part => part is not null));
        return TryParse(value, out _, out text);
    }
}
