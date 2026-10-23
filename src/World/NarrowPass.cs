namespace UnitSport.World;

/// <summary>
/// Two cars meeting on a two-way carriageway too narrow for two side by side (#1180, the user: "have the two oncoming cars slow
/// down a lot and shift a bit out of the road to pass each other (doesn't need much)"): each slows to a crawl and moves over to
/// its own right edge, its wheels past the tarmac onto the verge or kerb by as much as it needs (<see cref="Overhang"/> at most),
/// until the two bodies are <see cref="Gap"/> apart; then back to its line. Only where even that does not fit (a road under
/// <see cref="MinWidth"/>) does one wait for the other at the stretch's end (#1134's one car at a time). Plain numbers, no Godot:
/// <c>Traffic.StepCar</c> steers by it and tier 0 checks it.
/// </summary>
public static class NarrowPass
{
    /// <summary>Half a car's body (m): 1.9 m wide.</summary>
    public const float HalfBody = 0.95f;
    /// <summary>The room the two bodies keep between them as they pass (m).</summary>
    public const float Gap = 0.5f;
    /// <summary>How far past the tarmac's edge a car's side may go to make room (m): onto the verge or the kerb.</summary>
    public const float Overhang = 0.8f;
    /// <summary>Under this the two never fit side by side, however far over (m): one waits at the stretch's end.</summary>
    public const float MinWidth = 3f;
    /// <summary>The crawl the two pass at (m/s): 15 km/h with room to spare, down to 8 km/h with none.</summary>
    public const float FastCrawl = 15f / 3.6f, SlowCrawl = 8f / 3.6f;
    /// <summary>The spare room (m) at which a car passes at <see cref="FastCrawl"/>.</summary>
    public const float EasyRoom = 0.6f;
    /// <summary>How far ahead (m) a car coming the other way starts the slowing and moving over.</summary>
    public const float Look = 45f;

    /// <summary>Whether two cars pass each other on a carriageway <paramref name="width"/> m wide, moving over; else one at a time.</summary>
    public static bool Fits(float width) => width >= MinWidth;

    /// <summary>How far right of the centreline a car's middle may go at most (m): its side <see cref="Overhang"/> past the edge.</summary>
    public static float MostRight(float width) => width * 0.5f + Overhang - HalfBody;

    /// <summary>
    /// Where a car's middle goes, right of the centreline (m), to pass one coming whose middle is <paramref name="otherLeft"/> m left
    /// of it (the other's own right: it may not have moved over, a car standing at a red does not): far enough for <see cref="Gap"/>
    /// between the bodies, never less than its usual line <paramref name="keep"/>, at most <see cref="MostRight"/>.
    /// </summary>
    public static float Centre(float width, float keep, float otherLeft) =>
        Math.Clamp(2f * HalfBody + Gap - otherLeft, keep, Math.Max(keep, MostRight(width)));

    /// <summary>
    /// The crawl a car passes at (m/s) with its middle at <paramref name="centre"/>: the less room it has left before
    /// <see cref="MostRight"/>, the slower.
    /// </summary>
    public static float Crawl(float width, float centre)
    {
        float spare = Math.Clamp((MostRight(width) - centre) / EasyRoom, 0f, 1f);
        return SlowCrawl + (FastCrawl - SlowCrawl) * spare;
    }

    /// <summary>
    /// The most a car coming up to the meeting <paramref name="ahead"/> m off drives (m/s): down from its speed to the crawl, braking
    /// gently (2 m/s²) to reach it by the time the two are 10 m apart.
    /// </summary>
    public static float Approach(float width, float centre, float ahead) =>
        MathF.Sqrt(Crawl(width, centre) * Crawl(width, centre) + 2f * 2f * Math.Max(0f, ahead - 10f));
}
