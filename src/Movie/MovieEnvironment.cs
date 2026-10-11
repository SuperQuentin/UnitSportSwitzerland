namespace UnitSport.Movie;

/// <summary>
/// The world's state while the studio replays a recording (#775): the sky at the recorded hour and
/// the water at the recorded sea state, then the game's own back when the studio stops showing it.
/// </summary>
public static class MovieEnvironment
{
    private static float? _liveSea;

    /// <summary>The recording's hour and sea state on screen now.</summary>
    public static void Show(double hour, float sea)
    {
        if (World.DayNight.Instance is { } sky) sky.HourOverride = hour;
        _liveSea ??= World.WaterField.SeaState;
        World.WaterField.SetSeaState(sea);
    }

    /// <summary>The game's own clock and sea state again.</summary>
    public static void Restore()
    {
        if (World.DayNight.Instance is { } sky) sky.HourOverride = null;
        if (_liveSea is { } sea) World.WaterField.SetSeaState(sea);
        _liveSea = null;
    }
}
