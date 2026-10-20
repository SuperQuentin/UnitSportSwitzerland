using System.Globalization;

namespace UnitSport.World.Snow;

/// <summary>
/// <c>/weather</c> (#887): shows the ski day's weather, and lets an admin change it.
/// <c>snow &lt;cm&gt;</c> overnight snow, <c>fall &lt;cm&gt;</c> snow through the day,
/// <c>temp &lt;°C&gt;</c> the afternoon at 2000 m, <c>clear</c> / <c>cloudy</c>, <c>reroll</c> another
/// random day, <c>hold</c> keeps the change every day, <c>auto</c> back to the generated weather.
/// A change lasts until the next 06:00 unless held.
/// </summary>
public static class WeatherCommand
{
    public const string Usage = "/weather [snow <cm> | fall <cm> | temp <°C> | clear | cloudy | reroll | hold | auto]";

    /// <summary>The new override from the current one and the words; null with an error when they do not parse; null and no error for <c>auto</c>.</summary>
    public static WeatherOverride? Apply(WeatherOverride? current, long day, string args, out string? error)
    {
        error = null;
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // a held override stays held; otherwise it is for today, and yesterday's is forgotten
        var ov = current != null && (current.Sticky || current.Day == day) ? current with { Day = day } : new WeatherOverride(Day: day);
        bool Number(int i, float min, float max, out float v)
        {
            v = 0;
            if (parts.Length > i && float.TryParse(parts[i].TrimEnd('c', 'm', '°', 'C'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                && v >= min && v <= max) return true;
            return false;
        }
        switch (parts.Length == 0 ? "" : parts[0].ToLowerInvariant())
        {
            case "snow" when Number(1, 0, 150, out float cm): return ov with { NewSnowCm = cm };
            case "fall" when Number(1, 0, 100, out float cm): return ov with { DaySnowCm = cm };
            case "temp" when Number(1, -30, 20, out float t): return ov with { TMax2000 = t };
            case "clear": return ov with { Cloud = 0 };
            case "cloudy": return ov with { Cloud = 0.9f };
            case "reroll": return ov with { Reroll = ov.Reroll + 1, NewSnowCm = null, DaySnowCm = null, TMax2000 = null, Cloud = null };
            case "hold": return ov with { Sticky = true };
            case "auto": return null;
            case "snow" or "fall": error = "Snow is in cm: 0 to 150 overnight, 0 to 100 through the day."; return current;
            case "temp": error = "The afternoon temperature at 2000 m, -30 to 20 °C."; return current;
            default: error = Usage; return current;
        }
    }

    /// <summary>One line for the chat: what the slopes are getting today.</summary>
    public static string Describe(in DayWeather w, double hour, WeatherOverride? ov)
    {
        var inv = CultureInfo.InvariantCulture;
        string sky = w.Snowing(hour) ? "snowing" : w.Cloud < 0.25f ? "clear" : w.Cloud < 0.6f ? "some cloud" : "overcast";
        string snow = w.NewSnowCm > 0 ? string.Format(inv, "{0:F0} cm fell overnight", w.NewSnowCm) : "no new snow overnight";
        if (w.DaySnowCm > 0) snow += string.Format(inv, ", {0:F0} cm through the day", w.DaySnowCm);
        string temp = string.Format(inv, "{0:F0} to {1:F0} °C at 2000 m", w.TMin2000, w.TMax2000);
        string tag = ov == null ? "" : ov.Sticky ? " (held by an admin)" : " (set by an admin until 06:00)";
        return $"Ski weather: {sky}, {snow}, {temp}{tag}.";
    }
}
