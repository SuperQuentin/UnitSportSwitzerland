namespace UnitSport.World.Snow;

/// <summary>
/// One ski day's virtual weather (#887). A ski day runs 06:00 to 06:00 (<see cref="SnowClock"/>):
/// the groomers have been out when it starts. Temperatures are at 2000 m; other altitudes follow
/// the lapse rate (<see cref="TempAt"/>).
/// </summary>
/// <param name="NewSnowCm">Snow that fell overnight, before 06:00.</param>
/// <param name="DaySnowCm">Snow that falls during the day, evenly between <paramref name="SnowFrom"/> and <paramref name="SnowTo"/> (hours).</param>
/// <param name="TMin2000">The coldest, at 06:00 (°C at 2000 m).</param>
/// <param name="TMax2000">The warmest, at 14:00.</param>
/// <param name="Cloud">0 clear to 1 overcast: the sun's share of melting is (1 - cloud).</param>
public readonly record struct DayWeather(long Day, float NewSnowCm, float DaySnowCm, float SnowFrom, float SnowTo,
    float TMin2000, float TMax2000, float Cloud)
{
    /// <summary>°C lost per metre climbed (standard atmosphere).</summary>
    public const float Lapse = 0.0065f;

    /// <summary>Air temperature at an hour of the ski day (6..30) and an altitude.</summary>
    public float TempAt(double hour, float altitude)
    {
        double h = hour % 24;
        // coldest at 06:00, warmest at 14:00, a cosine between and back
        double t = h >= 6 && h <= 14 ? (h - 6) / 8 : h > 14 ? 1 - (h - 14) / 16 : 1 - (h + 10) / 16;
        float warm = (float)(0.5 - 0.5 * Math.Cos(Math.PI * Math.Clamp(t, 0, 1)));
        return TMin2000 + (TMax2000 - TMin2000) * warm - Lapse * (altitude - 2000);
    }

    /// <summary>Snow falling now, cm per hour.</summary>
    public float SnowRate(double hour) =>
        DaySnowCm > 0 && hour >= SnowFrom && hour < SnowTo ? DaySnowCm / Math.Max(0.5f, SnowTo - SnowFrom) : 0;

    /// <summary>Day snow fallen between 06:00 and <paramref name="hour"/>.</summary>
    public float DaySnowSoFar(double hour) =>
        DaySnowCm <= 0 ? 0 : DaySnowCm * (float)Math.Clamp((hour - SnowFrom) / Math.Max(0.5, SnowTo - SnowFrom), 0, 1);

    public bool Snowing(double hour) => SnowRate(hour) > 0;
}

/// <summary>
/// The admin's <c>/weather</c> override (#887): replaces parts of the generated day until the
/// next 06:00, or for good with <see cref="Sticky"/>. Null fields keep the generated value.
/// </summary>
public sealed record WeatherOverride(float? NewSnowCm = null, float? DaySnowCm = null, float? TMax2000 = null,
    float? Cloud = null, bool Sticky = false, long Day = 0, int Reroll = 0);

/// <summary>Deterministic daily weather: every peer computes the same day alone, from the day number and the world seed.</summary>
public static class SnowWeather
{
    public static DayWeather ForDay(long day, int seed, WeatherOverride? ov = null)
    {
        var rng = new Rng((ulong)day * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)seed * 0xD1B54A32D192ED03UL
                          ^ (ulong)(ov != null && (ov.Sticky || ov.Day == day) ? ov.Reroll : 0) * 0x94D049BB133111EBUL);
        // a storm one night in four or five, mostly 5-25 cm, now and then a dump of 40
        float newSnow = rng.Next() < 0.22 ? 5 + 35 * MathF.Pow(rng.Next(), 1.8f) : 0;
        // snow during the day one day in seven
        float daySnow = 0, from = 0, to = 0;
        if (rng.Next() < 0.14)
        {
            from = 8 + 6 * rng.Next();
            to = Math.Min(20, from + 2 + 5 * rng.Next());
            daySnow = 2 + 12 * rng.Next();
        }
        float cloud = newSnow > 0 ? 0.3f + 0.4f * rng.Next() : daySnow > 0 ? 0.8f + 0.2f * rng.Next() : 0.6f * MathF.Pow(rng.Next(), 1.5f);
        // −10 to +6 °C at 2000 m in the afternoon; a clear night cools further
        float tMax = -10 + 16 * rng.Next() - (daySnow > 0 ? 3 : 0);
        float tMin = tMax - (3 + 7 * (1 - cloud));
        var w = new DayWeather(day, newSnow, daySnow, from, to, tMin, tMax, cloud);
        if (ov == null || (!ov.Sticky && ov.Day != day)) return w;
        float spread = w.TMax2000 - w.TMin2000;
        return w with
        {
            NewSnowCm = ov.NewSnowCm ?? w.NewSnowCm,
            DaySnowCm = ov.DaySnowCm ?? w.DaySnowCm,
            SnowFrom = ov.DaySnowCm is > 0 && w.DaySnowCm <= 0 ? 6 : w.SnowFrom,
            SnowTo = ov.DaySnowCm is > 0 && w.DaySnowCm <= 0 ? 30 : w.SnowTo,
            TMax2000 = ov.TMax2000 ?? w.TMax2000,
            TMin2000 = ov.TMax2000 is { } t ? t - spread : w.TMin2000,
            Cloud = ov.Cloud ?? w.Cloud,
        };
    }

    /// <summary>SplitMix64: tiny, stable across .NET versions (System.Random's sequence is not promised).</summary>
    private struct Rng(ulong state)
    {
        private ulong _s = state;

        public float Next()
        {
            ulong z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 40) / (float)(1 << 24);
        }
    }
}

/// <summary>The ski day and hour from the environment clock (<c>WorldClock.EnvNow + HourShift</c>, seconds).</summary>
public static class SnowClock
{
    /// <summary>The groomers' hour: a ski day starts here.</summary>
    public const double ResetHour = 6;

    /// <summary>The ski day number and the hour in it, 6 (06:00) up to 30 (06:00 next morning).</summary>
    public static (long Day, double Hour) Split(double envSeconds)
    {
        double shifted = envSeconds - ResetHour * 3600;
        long day = (long)Math.Floor(shifted / 86400);
        return (day, ResetHour + (shifted - day * 86400.0) / 3600);
    }
}
