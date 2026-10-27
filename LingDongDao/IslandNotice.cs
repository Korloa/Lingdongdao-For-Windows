using System.Windows.Media.Imaging;

namespace LingDongDao;

internal enum NoticeKind
{
    Charging,
    ClipboardText,
    ClipboardImage,
    WifiConnected,
    WifiSlow,
    NoInternet,
    MemoryHigh,
    TemperatureHigh,
    BatteryLow,
    Thunderstorm
}

internal sealed record IslandNotice(
    NoticeKind Kind,
    string Title,
    string Detail,
    BitmapSource? Image = null,
    int? BatteryPercent = null);
