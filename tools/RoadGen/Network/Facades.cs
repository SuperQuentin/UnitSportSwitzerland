namespace UnitSport.Tools.RoadGen.Network;

using UnitSport.Terrain.Format;

/// <summary>
/// Where building walls stand, in plan, for the street planner (#119): every wall triangle of a
/// tile's <c>.bldg</c> (normal close to horizontal) rasterised into a <see cref="Cell"/> grid, LV95.
/// Walls only, not roofs: a roof's eave overhangs the facade by up to a metre, and a sidewalk
/// stopping at the eave would leave a strip of grass along every house. A road running through an
/// arcade starts inside a footprint, which is fine: its ray meets the arcade's wall line.
/// Tiles load on first use; a tile with no <c>.bldg</c> has no walls.
/// </summary>
public sealed class Facades(Func<TileId, BuildingTile?> source)
{
    /// <summary>The <c>.bldg</c> files of a chunk directory.</summary>
    public Facades(string chunkDir) : this(id => Read(chunkDir, id)) { }

    /// <summary>Grid spacing, metres.</summary>
    public const double Cell = 0.5;

    private const int Side = (int)(1000 / Cell);
    private readonly Dictionary<TileId, ulong[]?> _tiles = new();

    public int TilesLoaded => _tiles.Count(t => t.Value != null);
    public int Walls { get; private set; }

    /// <summary>Whether a wall stands in the cell holding the LV95 point.</summary>
    public bool Occupied(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        var bits = Tile(id);
        if (bits == null) return false;
        int c = (int)((e - id.MinE) / Cell), r = (int)((id.MaxN - n) / Cell);
        if ((uint)c >= Side || (uint)r >= Side) return false;
        int i = r * Side + c;
        return (bits[i >> 6] & (1UL << (i & 63))) != 0;
    }

    private readonly Dictionary<TileId, bool[]?> _coarse = new();

    /// <summary>
    /// Whether any wall stands in each 2 m cell of a tile (500 x 500, row 0 north, column 0 west),
    /// null if the tile has no buildings: the input of <see cref="UrbanField"/>.
    /// </summary>
    public bool[]? Coarse(TileId id)
    {
        if (_coarse.TryGetValue(id, out var mask)) return mask;
        var bits = Tile(id);
        if (bits != null)
        {
            const int k = 4, n = Side / k;   // 0.5 m cells per 2 m cell, 2 m cells per side
            mask = new bool[n * n];
            for (int r = 0; r < Side; r++)
                for (int c = 0; c < Side; c++)
                {
                    int i = r * Side + c;
                    if ((bits[i >> 6] & (1UL << (i & 63))) != 0) mask[(r / k) * n + c / k] = true;
                }
        }
        _coarse[id] = mask;
        return mask;
    }

    private ulong[]? Tile(TileId id)
    {
        if (_tiles.TryGetValue(id, out var bits)) return bits;
        bits = Load(id);
        _tiles[id] = bits;
        return bits;
    }

    public static BuildingTile? Read(string chunkDir, TileId id)
    {
        string path = Path.Combine(chunkDir, BuildingFormat.FileName(id));
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return BuildingCodec.Decode(stream);
        }
        catch (Exception)
        {
            return null;   // a tile that will not decode is the building stage's problem
        }
    }

    private ulong[]? Load(TileId id)
    {
        if (source(id) is not { } tile) return null;
        var bits = new ulong[(Side * Side + 63) / 64];
        foreach (var b in tile.Buildings)
        {
            var t = b.Triangles;
            for (int k = 0; k + 8 < t.Length; k += 9)
            {
                // tile-local: X east, Y up, Z south
                double ux = t[k + 3] - t[k], uy = t[k + 4] - t[k + 1], uz = t[k + 5] - t[k + 2];
                double vx = t[k + 6] - t[k], vy = t[k + 7] - t[k + 1], vz = t[k + 8] - t[k + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-9 || Math.Abs(ny) / len > 0.3) continue;   // a roof, a floor, a sliver
                Walls++;
                Mark(bits, t[k], t[k + 2], t[k + 3], t[k + 5]);
                Mark(bits, t[k + 3], t[k + 5], t[k + 6], t[k + 8]);
                Mark(bits, t[k + 6], t[k + 8], t[k], t[k + 2]);
            }
        }
        return bits;
    }

    /// <summary>Every cell a plan-view wall edge passes through (sampled at a quarter cell).</summary>
    private static void Mark(ulong[] bits, double ax, double az, double bx, double bz)
    {
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        int steps = Math.Max(1, (int)Math.Ceiling(len / (Cell * 0.25)));
        for (int s = 0; s <= steps; s++)
        {
            double f = (double)s / steps;
            int c = (int)((ax + (bx - ax) * f) / Cell), r = (int)((az + (bz - az) * f) / Cell);
            if ((uint)c >= Side || (uint)r >= Side) continue;
            int i = r * Side + c;
            bits[i >> 6] |= 1UL << (i & 63);
        }
    }
}
