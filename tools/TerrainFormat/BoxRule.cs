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
}
