using System.Globalization;
using System.Text.Json;

namespace LingDongDao;

internal static class CurrentWeather
{
    public static string Format(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement current = document.RootElement.GetProperty("current");
        double temperature = current.GetProperty("temperature_2m").GetDouble();
        int code = current.GetProperty("weather_code").GetInt32();
        bool isDay = current.GetProperty("is_day").GetInt32() == 1;
        string observed = current.GetProperty("time").GetString() ?? "";
        DateTime timestamp = DateTime.ParseExact(observed, "yyyy-MM-ddTHH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None);
        string condition = code switch
        {
            0 => isDay ? "晴" : "晴夜",
            1 => "大部晴朗",
            2 => "局部多云",
            3 => "阴",
            45 or 48 => "有雾",
            51 or 53 or 55 or 56 or 57 => "毛毛雨",
            61 or 66 => "小雨",
            63 or 67 => "中雨",
            65 => "大雨",
            71 or 77 => "小雪",
            73 => "中雪",
            75 => "大雪",
            80 => "小阵雨",
            81 => "阵雨",
            82 => "强阵雨",
            85 or 86 => "阵雪",
            95 => "雷雨",
            96 or 99 => "雷雨伴冰雹",
            _ => $"天气代码 {code}"
        };
        return $"郫都区 · {temperature:0.#}°C · {condition} · 更新 {timestamp:HH:mm}";
    }
}
