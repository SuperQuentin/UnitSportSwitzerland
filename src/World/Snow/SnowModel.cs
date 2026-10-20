using UnitSport.Terrain.Format;

namespace UnitSport.World.Snow;

/// <summary>What the HUD calls a slope's snow: the dominant trait of a <see cref="SnowState"/>.</summary>
public enum SnowCondition : byte { Corduroy, Packed, HardPack, Icy, Powder, Chopped, Slush, Moguls }

/// <summary>
/// The snow at one point of a slope (#887), each trait 0..1. They overlap: a warm afternoon on a
/// worn groomed run is a little corduroy left, some bumps and a lot of slush.
/// </summary>
/// <param name="Fresh">Corduroy left from the groomers.</param>
/// <param name="Loose">Loose snow on top (powder, then chopped as it is skied).</param>
/// <param name="Ice">Hard, refrozen or scraped snow.</param>
/// <param name="Wet">Melting snow: slush.</param>
/// <param name="Bumps">Moguls.</param>
/// <param name="LooseCm">Depth of the loose layer.</param>
/// <param name="Wear">Skier traffic since 06:00 (the crowd and the real skiers), 0 fresh, ~1 a busy day.</param>
public readonly record struct SnowState(float Fresh, float Loose, float Ice, float Wet, float Bumps, float LooseCm, float Wear)
{
    public SnowCondition Condition =>
        Bumps > 0.55f ? SnowCondition.Moguls
        : Wet > 0.55f ? SnowCondition.Slush
        : Ice > 0.5f ? SnowCondition.Icy
        : Loose > 0.3f ? (Wear < 0.35f ? SnowCondition.Powder : SnowCondition.Chopped)
        : Fresh > 0.5f ? SnowCondition.Corduroy
        : Ice > 0.25f ? SnowCondition.HardPack
        : SnowCondition.Packed;

    /// <summary>Ski friction against a groomed run's: loose and wet snow drag, ice glides.</summary>
    public float FrictionScale => Math.Max(0.6f, 1 + 0.9f * Loose + 1.2f * Wet + 0.3f * Bumps - 0.3f * Ice);

    /// <summary>Edge grip against a groomed run's: ice and slush let go.</summary>
    public float GripScale => Math.Clamp(1 - 0.55f * Ice - 0.25f * Wet - 0.2f * Loose, 0.3f, 1);
}

/// <summary>
/// The snow state model (#887): pure, the same on every peer. The day's weather and yesterday's,
/// the hour, the point's altitude and sun exposure, the slope's kind and the wear give the state.
/// Every ski day starts at 06:00 with the wear at zero and groomed slopes back to corduroy.
/// </summary>
public static class SnowModel
{
    /// <summary>Extra °C the sun adds to melting at midday on a south face under a clear sky.</summary>
    public const float SunMelt = 3f;

    /// <summary>How busy a slope is, relative to an easy run: beginners crowd the easy ones.</summary>
    public static float Popularity(PisteType type, PisteDifficulty diff) => type switch
    {
        PisteType.Nordic => 0.35f,
        PisteType.Sled => 0.5f,
        PisteType.Connection => 0.6f,
        PisteType.SnowPark => 0.7f,
        _ => diff switch
        {
            PisteDifficulty.Novice => 1.0f,
            PisteDifficulty.Easy => 0.9f,
            PisteDifficulty.Intermediate or PisteDifficulty.Unknown => 0.75f,
            PisteDifficulty.Advanced => 0.5f,
            PisteDifficulty.Expert => 0.35f,
            _ => 0.25f,
        },
    };

    /// <summary>
    /// The virtual crowd's wear by an hour of the ski day: nobody before the lifts open at 08:30,
    /// the most around noon, all done by 16:30. A grey day keeps some at home.
    /// </summary>
    public static float CrowdWear(double hour, float popularity, float cloud)
    {
        double t = Math.Clamp((hour - 8.5) / 8, 0, 1);
        float cumulative = (float)(t * t * (3 - 2 * t));
        return 1.3f * popularity * cumulative * (1 - 0.35f * cloud);
    }

    /// <summary>Degree-hours of melting from 06:00 to <paramref name="hour"/>, sun included.</summary>
    public static float Melt(in DayWeather w, double hour, float altitude, float sun)
    {
        float total = 0;
        const double step = 0.25;
        for (double t = SnowClock.ResetHour; t < hour; t += step)
        {
            double dt = Math.Min(step, hour - t);
            double mid = t + dt / 2;
            // the sun on the snow from 07:00 to 17:00, strongest at noon
            float sunUp = (float)Math.Max(0, Math.Sin(Math.PI * (mid - 7) / 10));
            float heat = w.TempAt(mid, altitude) + SunMelt * sunUp * (sun * 2 - 0.5f) * (1 - w.Cloud);
            if (heat > 0) total += heat * (float)dt;
        }
        return total;
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <param name="realWear">Wear from real players and bots on this slope today (the server's count).</param>
    public static SnowState Evaluate(in DayWeather today, in DayWeather yesterday, double hour, float altitude, float sun,
        PisteRecord slope, float realWear)
    {
        bool groomed = slope.Groomed;
        float wear = CrowdWear(hour, Popularity(slope.Type, slope.Difficulty), today.Cloud) + realWear;
        float melt = Melt(today, hour, altitude, sun);
        float meltYesterday = Melt(yesterday, 18, altitude, sun);
        float wet = Smooth(3f, 18f, melt) * (today.Snowing(hour) ? 0.5f : 1f);

        // loose snow: on a groomed run only what fell after the groomers (from 04:00) and today
        float overnight = groomed ? today.NewSnowCm * (2f / 12f) : today.NewSnowCm;
        float looseCm = (overnight + today.DaySnowSoFar(hour)) * (1 - 0.6f * wet);
        float loose = Math.Clamp(looseCm / 25f, 0, 1) * MathF.Exp(-1.6f * wear);

        // yesterday's slush frozen overnight, tilled by the groomers, buried by new snow, softened by today's sun
        float coldNight = Smooth(0, -4, today.TempAt(SnowClock.ResetHour, altitude));
        float crust = Smooth(2, 12, meltYesterday) * coldNight * (groomed ? 0.45f : 1f) * (1 - Math.Clamp(today.NewSnowCm / 8f, 0, 1));
        float scraped = (today.TempAt(hour, altitude) < -8 ? 0.4f : 0.2f) * Math.Clamp(wear - 0.5f, 0, 1) * (groomed ? 1f : 0.5f);
        float ice = Math.Clamp(crust * (1 - Smooth(0.5f, 5f, melt)) + scraped, 0, 1) * (1 - loose);

        float fresh = groomed ? MathF.Exp(-2.2f * wear) * (1 - Math.Clamp(looseCm / 6f, 0, 1)) * (1 - 0.5f * wet) : 0;
        float steep = Smooth(12, 30, slope.SteepDeg);
        float bumps = slope.Grooming == PisteGrooming.Mogul ? 0.85f
            : (groomed ? 0.4f : 1f) * Smooth(0.4f, 1.6f, wear) * steep;
        return new SnowState(fresh, loose, ice, wet, bumps, looseCm, wear);
    }
}
