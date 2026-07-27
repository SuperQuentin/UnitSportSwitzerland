namespace UnitSport.Terrain.Format;

/// <summary>
/// LV95 kilometer tile coordinates, e.g. (2579, 1109) covers
/// E [2579000, 2580000) x N [1109000, 1110000) meters.
/// </summary>
public readonly record struct TileId(int E, int N)
{
    /// <summary>West edge of the tile in LV95 meters.</summary>
    public double MinE => E * 1000.0;

    /// <summary>South edge of the tile in LV95 meters.</summary>
    public double MinN => N * 1000.0;

    /// <summary>North edge of the tile in LV95 meters.</summary>
    public double MaxN => (N + 1) * 1000.0;

    public static TileId FromLv95(double e, double n) =>
        new((int)Math.Floor(e / 1000.0), (int)Math.Floor(n / 1000.0));

    public override string ToString() => $"{E}_{N}";
}
