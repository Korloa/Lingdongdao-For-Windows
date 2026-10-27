using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public bool StartWithWindows { get; set; }

    public bool ChargingEnabled { get; set; } = true;
    public bool ClipboardEnabled { get; set; } = true;
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

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
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
        WifiSlowMbps = Math.Clamp(WifiSlowMbps, 5, 100);
        MemoryPercent = Math.Clamp(MemoryPercent, 60, 98);
        CpuTemperatureC = Math.Clamp(CpuTemperatureC, 70, 105);
        GpuTemperatureC = Math.Clamp(GpuTemperatureC, 65, 105);
        BatteryLowPercent = Math.Clamp(BatteryLowPercent, 5, 40);
        WeatherLookaheadHours = Math.Clamp(WeatherLookaheadHours, 6, 48);
        if (!GlobalHotkey.Options.Contains(Hotkey)) Hotkey = GlobalHotkey.Options[0];
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
    public static readonly string[] Options =
    [
        "Ctrl+Alt+D",
        "Ctrl+Alt+I",
        "Ctrl+Shift+D",
        "Alt+Shift+I"
    ];

    public static (uint Modifiers, uint Key) Parse(string text) => text switch
    {
        "Ctrl+Alt+D" => (0x4000 | 0x0002 | 0x0001, 0x44),
        "Ctrl+Alt+I" => (0x4000 | 0x0002 | 0x0001, 0x49),
        "Ctrl+Shift+D" => (0x4000 | 0x0002 | 0x0004, 0x44),
        "Alt+Shift+I" => (0x4000 | 0x0001 | 0x0004, 0x49),
        _ => throw new ArgumentOutOfRangeException(nameof(text))
    };
}
