using System.Windows.Media.Imaging;

namespace LingDongDao;

internal enum NoticeKind
{
    Idle,
    Charging,
    ClipboardText,
    ClipboardImage,
    WifiConnected,
    WifiSlow,
    NoInternet,
    MemoryHigh,
    TemperatureHigh,
    BatteryLow,
    Thunderstorm,
    Music,
    AgentSuggestion,
    ImageConverted
}

internal sealed record IslandNotice(
    NoticeKind Kind,
    string Title,
    string Detail,
    BitmapSource? Image = null,
    int? BatteryPercent = null,
    BitmapSource? FullImage = null,
    string? ImagePath = null);
