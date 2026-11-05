namespace UnitSport.Terrain.Format;

/// <summary>
/// An inclusive rectangle of LV95 kilometre tiles, by their keys' E and N: a range of the tile key
/// (#1443), so code outside the terrain format asks it for its tiles instead of reading its edges.
/// With the global tiling a range of cube keys answers the same questions.
/// </summary>
public readonly record struct TileRect(int MinE, int MinN, int MaxE, int MaxN)
{
    public bool Contains(TileId id) => id.E >= MinE && id.E <= MaxE && id.N >= MinN && id.N <= MaxN;
    public TileRect Grow(int by) => new(MinE - by, MinN - by, MaxE + by, MaxN + by);
    public TileRect Union(TileRect o) =>
        new(Math.Min(MinE, o.MinE), Math.Min(MinN, o.MinN), Math.Max(MaxE, o.MaxE), Math.Max(MaxN, o.MaxN));
    /// <summary>The box <paramref name="radius"/> tiles round one tile.</summary>
    /// <summary>The tiles an LV95 box reaches, edges included (#1509: the LV95 tiles under a cube leaf).</summary>
    public static TileRect Covering(double minE, double minN, double maxE, double maxN)
    {
        var lo = TileId.FromLv95(minE, minN);
        var hi = TileId.FromLv95(maxE, maxN);
        return new TileRect(lo.E, lo.N, hi.E, hi.N);
    }

    public static TileRect Around(TileId id, int radius) => new TileRect(id.E, id.N, id.E, id.N).Grow(radius);

    /// <summary>The smallest rectangle holding every tile; null for none.</summary>
    public static TileRect? Bounding(IEnumerable<TileId> tiles)
    {
        int minE = int.MaxValue, minN = int.MaxValue, maxE = int.MinValue, maxN = int.MinValue;
        foreach (var t in tiles)
        {
            minE = Math.Min(minE, t.E);
            maxE = Math.Max(maxE, t.E);
            minN = Math.Min(minN, t.N);
            maxN = Math.Max(maxN, t.N);
        }
        return minE > maxE ? null : new TileRect(minE, minN, maxE, maxN);
    }

    /// <summary>Every tile of the rectangle, a row at a time from the south, each row from the west.</summary>
    public IEnumerable<TileId> Tiles()
    {
        for (int n = MinN; n <= MaxN; n++)
            for (int e = MinE; e <= MaxE; e++)
                yield return new TileId(e, n);
    }
}
