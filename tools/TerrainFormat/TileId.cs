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

    // ---- the tile key's own frame (#1443, G0c) ----------------------------------------------------
    // Code outside the terrain format asks the tile for these instead of doing LV95 kilometre
    // arithmetic (MinE, MaxN, E + 1): today they are that arithmetic, exactly; with the global tiling
    // the cube-sphere key answers the same questions. A tile's local axes are its own grid's: x east
    // and z south here (z = MaxN - n), y up; its local origin is its north-west corner.

    /// <summary>An LV95 point in this tile's local x and z (metres from its north-west corner, z southward).</summary>
    public (double X, double Z) Local(double e, double n) => (e - MinE, MaxN - n);

    /// <summary>The inverse of <see cref="Local"/>: a local x and z as LV95.</summary>
    public (double E, double N) Global(double x, double z) => (MinE + x, MaxN - z);

    /// <summary>
    /// The LV95 box the tile covers: what to ask an LV95 source (the generator's lines, plans and lattices)
    /// for what lies on the tile. Points on the tile still go through <see cref="Local"/> / <see cref="Global"/>;
    /// with the global tiling this is the box round a cube tile.
    /// </summary>
    public (double West, double South, double East, double North) Lv95Box => (MinE, MinN, MinE + 1000.0, MaxN);

    /// <summary>The middle of the tile, LV95.</summary>
    public (double E, double N) Centre => (MinE + 500.0, MaxN - 500.0);

    /// <summary>Whether an LV95 point is on this tile (west and south edges in, east and north out).</summary>
    public bool Contains(double e, double n) => FromLv95(e, n) == this;

    /// <summary>Whether an LV95 box reaches onto this tile: the two touch, edges and corners included.</summary>
    public bool Touches(double minE, double minN, double maxE, double maxN) =>
        maxE >= MinE && minE <= MinE + 1000.0 && maxN >= MinN && minN <= MaxN;

    /// <summary>The tile <paramref name="dx"/> along the local x axis and <paramref name="dz"/> along the local z axis from this one (z is southward).</summary>
    public TileId Neighbour(int dx, int dz) => new(E + dx, N - dz);

    /// <summary>How many tiles apart two tiles are, the larger of the two axes (0 for the same tile, 1 for any of the eight around it).</summary>
    public int RingDistance(TileId other) => Math.Max(Math.Abs(E - other.E), Math.Abs(N - other.N));

    public static TileId FromLv95(double e, double n) =>
        new((int)Math.Floor(e / 1000.0), (int)Math.Floor(n / 1000.0));

    public override string ToString() => $"{E}_{N}";

    /// <summary>A tiles file (--tiles-file of MapSetup, RoadGen, TerrainPreprocessor): one "E-N"
    /// ("E_N", "E,N") per line, "#" starts a comment.</summary>
    public static List<TileId> ReadList(string path)
    {
        var ids = new List<TileId>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('-', '_', ',');
            ids.Add(new TileId(int.Parse(parts[0]), int.Parse(parts[1])));
        }
        return ids;
    }
}
