namespace UnitSport.Terrain.Format;

/// <summary>
/// How fast a car takes a bend (#979): the game's traffic and RoadGen's signal timing share it, so the lights are timed for the cars
/// that drive them.
/// </summary>
public static class TurnSpeed
{
    /// <summary>
    /// The sideways acceleration a car takes a bend at (m/s²): 3, as town drivers turn (the user, Oct 20 2026: the more realistic,
    /// and the same for both): ~5.5 m/s (20 km/h) round a 10 m right turn, ~7.7 m/s (28 km/h) round a 20 m left turn, between
    /// traffic's old comfortable 2.5 and the sporty 4; SN 640 838 times turns at a flat 10 m/s, 7 on tight radii.
    /// </summary>
    public const float Lateral = 3f;

    /// <summary>The speed a bend of radius <paramref name="radius"/> m allows (m/s).</summary>
    public static double Of(double radius) => Math.Sqrt(Lateral * radius);
}
