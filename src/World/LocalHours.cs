using System;
using System.Globalization;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

// Plain C# (linked into the unit tests).

/// <summary>
/// A clock face <c>/time</c> reads and sets in (#1801): Swiss civil time, or a fixed offset from UTC.
/// The world keeps one clock, on Swiss time (<see cref="WorldClock"/>); a zone only turns an hour
/// typed or shown into that clock's hour and back.
/// </summary>
public readonly record struct ClockZone(double? UtcOffset)
{
    /// <summary>Swiss civil time (CET / CEST): the world clock's own face.</summary>
    public static readonly ClockZone Swiss = new((double?)null);

    public bool IsSwiss => UtcOffset is null;

    /// <summary>"Swiss time", "UTC", "UTC+8", "UTC-3:30".</summary>
    public string Label
    {
        get
        {
            if (UtcOffset is not { } h) return "Swiss time";
            if (h == 0) return "UTC";
            int minutes = (int)Math.Round(Math.Abs(h) * 60);
            string sign = h < 0 ? "-" : "+";
            return minutes % 60 == 0 ? $"UTC{sign}{minutes / 60}" : $"UTC{sign}{minutes / 60}:{minutes % 60:00}";
        }
    }

    /// <summary>Swiss civil time's offset from UTC on <paramref name="date"/>: 1, or 2 in summer time (read at noon).</summary>
    public static int SwissOffset(DateOnly date) => SwissTime.Offset(SwissTime.ToUtc(date, 12));

    /// <summary>This zone's hour as the world clock's (Swiss) hour on <paramref name="date"/>.</summary>
    public double ToSwiss(double hour, DateOnly date) =>
        UtcOffset is { } h ? TimeCommand.Wrap(hour - h + SwissOffset(date)) : TimeCommand.Wrap(hour);

    /// <summary>The world clock's (Swiss) hour as this zone's on <paramref name="date"/>.</summary>
    public double FromSwiss(double swissHour, DateOnly date) =>
        UtcOffset is { } h ? TimeCommand.Wrap(swissHour + h - SwissOffset(date)) : TimeCommand.Wrap(swissHour);

    /// <summary>
    /// A zone word: <c>swiss</c> / <c>ch</c> / <c>cet</c> / <c>cest</c>, <c>utc</c> / <c>gmt</c>,
    /// <c>utc+8</c>, <c>utc-3:30</c>, <c>gmt+5.5</c>. <c>here</c> / <c>local</c> parse to null:
    /// the zone the player is in, which only the caller knows.
    /// </summary>
    public static bool TryParse(string word, out ClockZone? zone)
    {
        zone = null;
        string w = word.Trim().ToLowerInvariant();
        switch (w)
        {
            case "here" or "local": return true;
            case "swiss" or "ch" or "cet" or "cest" or "switzerland": zone = Swiss; return true;
            case "utc" or "gmt" or "z": zone = new ClockZone(0); return true;
        }
        string rest = w.StartsWith("utc") || w.StartsWith("gmt") ? w[3..] : "";
        if (rest.Length < 2 || (rest[0] != '+' && rest[0] != '-')) return false;
        double sign = rest[0] == '-' ? -1 : 1;
        string body = rest[1..];
        double hours;
        int colon = body.IndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(body[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int hh)
                || !int.TryParse(body[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int mm) || mm > 59)
                return false;
            hours = hh + mm / 60.0;
        }
        else if (!double.TryParse(body, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out hours))
            return false;
        // the Earth's zones run UTC-12 to UTC+14
        if (sign * hours < -12 || sign * hours > 14) return false;
        zone = new ClockZone(sign * hours);
        return true;
    }

    /// <summary>The words the chat completer offers.</summary>
    public static readonly string[] Words = ["here", "swiss", "utc"];
}

/// <summary>
/// What <c>/time</c>'s words mean where the player stands (#1801). The world has one clock, on Swiss
/// time; the sun is real for each place (<see cref="SunPath"/>), so far from Switzerland
/// <c>/time set day</c> on Swiss time could be night. Here the named hours are read off the sun at
/// the place, and the zone a player is in is the face their hours are typed and shown in.
///
/// <para>
/// <b>Home</b> (<see cref="HomeKm"/> of Bern, all of Switzerland and its border towns): the Swiss
/// clock's hours, exactly as before — sunrise and sunset are today's at Bern, day 08:00, noon 12:00,
/// night 21:00, midnight 00:00, and the zone is Swiss time. So is everywhere under the fixed sun,
/// which is the same sun wherever one stands.
/// </para>
///
/// <para>
/// <b>Away</b>: noon is the local solar noon (the sun on the meridian), midnight twelve hours after,
/// sunrise and sunset the real ones at that latitude and date; <c>day</c> is two hours after sunrise
/// and <c>night</c> three after sunset, as the fixed day's 06 / 08 and 18 / 21, each held to at most
/// half the way to noon or midnight so a short winter day still puts <c>day</c> in the morning. The
/// zone is the nautical one: UTC + the longitude / 15°, rounded, with no summer time.
/// </para>
/// </summary>
public static class LocalHours
{
    /// <summary>How far from Bern the Swiss clock's hours still hold, km. Geneva is 130 km away, Müstair 190.</summary>
    public const double HomeKm = 250;

    /// <summary>Bern, where the sky stands when nobody's place is known (the console, a fresh sky).</summary>
    public static GlobalPos Bern { get; } = GlobalPos.FromLv95(SunPath.BernE, SunPath.BernN, 540);

    /// <summary>Bern's latitude and longitude (WGS84), degrees.</summary>
    public const double BernLat = 46.9511, BernLon = 7.4386;

    /// <summary>Hours after sunrise <c>day</c> is, and after sunset <c>night</c> is, as the fixed day's 6 -> 8 and 18 -> 21.</summary>
    public const double DayAfterRise = 2, NightAfterSet = 3;

    /// <summary>The place's latitude and longitude (WGS84), degrees.</summary>
    public static (double Lat, double Lon) LatLon(GlobalPos at)
    {
        var g = at.Geodetic;
        return (g.LatDeg, g.LonDeg);
    }

    /// <summary>Great-circle distance between two places, km.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0, D = Math.PI / 180;
        double dLat = (lat2 - lat1) * D, dLon = (lon2 - lon1) * D;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * D) * Math.Cos(lat2 * D) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary><paramref name="at"/> is home: Switzerland, where the Swiss clock's hours hold.</summary>
    public static bool IsHome(GlobalPos at)
    {
        var (lat, lon) = LatLon(at);
        return DistanceKm(lat, lon, BernLat, BernLon) <= HomeKm;
    }

    /// <summary>
    /// The zone a player at <paramref name="at"/> is in: Swiss time at home or under the fixed sun
    /// (<paramref name="realSun"/> false), otherwise UTC + the longitude / 15°.
    /// </summary>
    public static ClockZone ZoneAt(GlobalPos at, bool realSun)
    {
        if (!realSun || IsHome(at)) return ClockZone.Swiss;
        var (_, lon) = LatLon(at);
        double offset = Math.Round(lon / 15.0);
        if (offset > 12) offset -= 24;
        if (offset < -12) offset += 24;
        return new ClockZone(offset);
    }

    /// <summary>
    /// The world clock's (Swiss) hour of a named hour at <paramref name="at"/> on <paramref name="date"/>.
    /// False, with a reason, when the sun does not rise or set there today.
    /// </summary>
    public static bool TryNamed(string name, GlobalPos at, DateOnly date, bool realSun,
        out double swissHour, out string error)
    {
        error = "";
        swissHour = 0;
        name = name.ToLowerInvariant();
        double fixedHour = -1;
        foreach (var (nm, h) in TimeCommand.Named)
            if (nm == name) fixedHour = h;
        if (fixedHour < 0)
        {
            error = $"'{name}' is not a named hour.";
            return false;
        }

        if (!realSun)
        {
            swissHour = fixedHour;
            return true;
        }
        if (IsHome(at))
        {
            swissHour = name switch
            {
                "sunrise" => SunPath.RiseOrSet(date, rise: true),
                "sunset" => SunPath.RiseOrSet(date, rise: false),
                _ => fixedHour,
            };
            return true;
        }

        double noon = SolarNoon(date, at);
        double midnight = TimeCommand.Wrap(noon + 12);
        double? rise = SunPath.RiseOrSet(date, true, at), set = SunPath.RiseOrSet(date, false, at);
        string polar = PolarWord(date, noon, at);
        switch (name)
        {
            case "noon": swissHour = noon; return true;
            case "midnight": swissHour = midnight; return true;
            case "sunrise" when rise is { } r: swissHour = r; return true;
            case "sunset" when set is { } s: swissHour = s; return true;
            case "sunrise" or "sunset":
                error = $"The sun does not {(name == "sunrise" ? "rise" : "set")} here today ({polar}). Try noon or midnight.";
                return false;
            case "day":
                // a sunless day's brightest hour is noon
                swissHour = rise is { } dr
                    ? TimeCommand.Wrap(dr + Math.Min(DayAfterRise, TimeCommand.Wrap(noon - dr) / 2)) : noon;
                return true;
            default: // night
                swissHour = set is { } ns
                    ? TimeCommand.Wrap(ns + Math.Min(NightAfterSet, TimeCommand.Wrap(midnight - ns) / 2)) : midnight;
                return true;
        }
    }

    /// <summary>"polar night" or "midnight sun", from the sun's height at noon.</summary>
    private static string PolarWord(DateOnly date, double noon, GlobalPos at) =>
        SunPath.ElevationAt(date, noon, at) < -0.833f ? "polar night" : "midnight sun";

    /// <summary>
    /// The world clock's (Swiss) hour the sun crosses the meridian at <paramref name="at"/> on
    /// <paramref name="date"/>: Bern's transit, found from its highest sun, moved by
    /// the longitude (four minutes a degree). Bern's sun is never circumpolar, so the search is sound
    /// even where the local one is.
    /// </summary>
    public static double SolarNoon(DateOnly date, GlobalPos at)
    {
        var (_, lon) = LatLon(at);
        return TimeCommand.Wrap(BernNoon(date) - (lon - BernLon) / 15.0);
    }

    /// <summary>The hour (Swiss) of Bern's highest sun on <paramref name="date"/>, by golden-section search over 10..16.</summary>
    public static double BernNoon(DateOnly date)
    {
        double El(double h)
        {
            SunPath.Toward(SkyBody.Sun, date, h, SunPath.BernE, SunPath.BernN, out float el);
            return el;
        }
        double a = 10, b = 16;
        const double g = 0.6180339887498949;
        double c = b - g * (b - a), d = a + g * (b - a), fc = El(c), fd = El(d);
        for (int i = 0; i < 40; i++)
        {
            if (fc > fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = El(c); }
            else { a = c; c = d; fc = fd; d = a + g * (b - a); fd = El(d); }
        }
        return (a + b) / 2;
    }
}
