namespace UnitSport.Terrain.Format;

/// <summary>
/// Don't block the box (#1134, the user: "cars shouldn't enter the intersection if the exit isn't clear, aka enough space to store
/// it"): a car crosses its stop line only where its exit has room for it past every car already on it or committed to it (in the
/// core heading the same way), up to the next stop line, junction or <see cref="Reach"/> m. An approach or an exit arm flagged
/// <c>AllowBlock</c> (<see cref="RoadApproach.AllowBlock"/>, <see cref="SignalArm.AllowBlock"/>) turns the rule off for its cars.
/// </summary>
public static class BoxRule
{
    /// <summary>A car and the gap behind it as a queue stands (m, the cars' 7.5 m middle to middle).</summary>
    public const float CarRoom = 7.5f;
    /// <summary>An exit free this far is free (m): a car looks no further.</summary>
    public const float Reach = 150f;

    /// <summary>
    /// Whether a car may enter: its exit's room (<paramref name="storageM"/> m, at most <see cref="Reach"/>), less
    /// <see cref="CarRoom"/> for each of the <paramref name="ahead"/> cars on it or committed to it, still holds one more; or a flag allows it.
    /// </summary>
    public static bool Fits(float storageM, int ahead, bool approachAllows = false, bool exitAllows = false) =>
        approachAllows || exitAllows || Math.Min(storageM, Reach) - ahead * CarRoom >= CarRoom;

    /// <summary>
    /// Leg by leg from the core (the user: "look forward through all nodes until it either finds enough space or the stop line"): each
    /// span's room (m) less <see cref="CarRoom"/> for each car on it, added up in order; it fits as soon as the room so far holds one more,
    /// or (<paramref name="openBeyond"/>: its way is planned no further than these spans and the road goes on) at their end, where it is
    /// free. Cars on a later span take no room from an earlier one; a span over-full borrows from the room before it.
    /// </summary>
    public static bool Fits(IReadOnlyList<(float Room, int Cars)> spans, bool openBeyond)
    {
        float free = 0f, walked = 0f;
        foreach (var (room, cars) in spans)
        {
            free += room - cars * CarRoom;
            walked += room;
            if (free >= CarRoom) return true;
            if (walked >= Reach) return false;
        }
        return openBeyond && free > -CarRoom * 0.5f;
    }
}
