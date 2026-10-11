namespace UnitSport.Movie;

/// <summary>
/// The world's state while the studio replays a recording (#775): the sky at the recorded hour, the
/// water at the recorded sea state and the recorded snowfall, then the game's own back when the
/// studio stops showing it.
/// </summary>
public static class MovieEnvironment
{
    private static float? _liveSea;

    /// <summary>The recording's hour, sea state and snowfall on screen now.</summary>
    public static void Show(double hour, float sea, float snow)
    {
        if (Occasions.OccasionPrecip.SnowfallOverride != snow) Occasions.OccasionPrecip.SnowfallOverride = snow;
        if (World.DayNight.Instance is { } sky) sky.HourOverride = hour;
        _liveSea ??= World.WaterField.SeaState;
        World.WaterField.SetSeaState(sea);
    }

    /// <summary>The game's own clock and sea state again.</summary>
    public static void Restore()
    {
        if (World.DayNight.Instance is { } sky) sky.HourOverride = null;
        Occasions.OccasionPrecip.SnowfallOverride = null;
        if (_liveSea is { } sea) World.WaterField.SetSeaState(sea);
        _liveSea = null;
    }
}
