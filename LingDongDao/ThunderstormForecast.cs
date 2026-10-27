using System.Globalization;
using System.Text.Json;

namespace LingDongDao;

internal static class ThunderstormForecast
{
    public static DateTime? FindFirst(string json, DateTime nowInChina, int lookaheadHours)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("timezone").GetString() != "Asia/Shanghai")
            throw new FormatException("天气预报时区不正确。");

        JsonElement hourly = root.GetProperty("hourly");
        JsonElement times = hourly.GetProperty("time");
        JsonElement codes = hourly.GetProperty("weather_code");
        if (times.GetArrayLength() != codes.GetArrayLength())
            throw new FormatException("天气预报数据不完整。");

        DateTime lastHour = nowInChina.AddHours(lookaheadHours);
        for (int index = 0; index < times.GetArrayLength(); index++)
        {
            int code = codes[index].GetInt32();
            if (code is not (95 or 96 or 99)) continue;
            DateTime hour = DateTime.ParseExact(times[index].GetString()!, "yyyy-MM-ddTHH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None);
            // A forecast entry describes the hour starting at its timestamp.
            if (hour.AddHours(1) > nowInChina && hour <= lastHour) return hour;
        }
        return null;
    }
}
