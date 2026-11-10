namespace UnitSport.Tools.RoadGen.Network;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// How built-up a place is, from the building walls around it alone (#119): on a lattice of
/// <see cref="Cell"/> nodes on the plane, a node is <i>near</i> a building when a wall stands within
/// <see cref="NearM"/> of it (a square). Two shares of near nodes are taken: within
/// <see cref="WindowM"/> (local: a village street) and within <see cref="CityWindowM"/> (a city:
/// a quay along a river, a street past a park or a station square has no wall at hand, but is in
/// town all the same). The density is the larger, the city share scaled so that
/// <see cref="CityAt"/> of it counts as <see cref="UrbanAt"/>. The value at a point depends on nothing but the position and
/// the walls around it, so both sides of a tile seam, every arm of a junction and every piece of a
/// split line see the same number at the same place. The street planner calls a street urban at
/// <see cref="UrbanAt"/>, and the network stage lowers roads by <see cref="Weight"/> (the road at
/// the ground less a kerb in town, a little above the ground outside).
/// A box average of a 0/1 mask changes by at most about 1/WindowM per metre, so a weight ramp
/// over 0.25 of density takes at least ~12 m of road: the transition grade stays gentle.
/// </summary>
public sealed class UrbanField(Facades facades)
{
    private readonly IUnits _units = facades.Units;   // the walls' tiles (#1507)
    public const double Cell = 2.0;
    public const double NearM = 18.0, WindowM = 25.0, CityWindowM = 150.0;
    /// <summary>The city share at which a place is urban, whatever is at hand.</summary>
    public const double CityAt = 0.45;
    /// <summary>Urban at this density (a street gets sidewalks, the road is fully lowered).</summary>
    public const double UrbanAt = 0.4;
    /// <summary>The road starts to lower toward the town's height at this density.</summary>
    public const double RuralBelow = 0.15;

    private readonly int PerTile = (int)(facades.Units.TileSizeM / Cell);   // cells per tile side
    private const int Near = (int)(NearM / Cell);            // 9
    private const int Window = (int)(WindowM / Cell);       // 12: +-24 m
    private const int City = (int)(CityWindowM / Cell);       // 75: +-150 m
    private const int Margin = Near + City;
    private readonly Dictionary<TileId, float[]> _density = new();

    /// <summary>Density at a plane point, bilinear between lattice nodes.</summary>
    public double Density(double e, double n)
    {
        double gx = e / Cell, gy = n / Cell;   // global lattice coordinates, y north
        long x0 = (long)Math.Floor(gx), y0 = (long)Math.Floor(gy);
        double fx = gx - x0, fy = gy - y0;
        double v00 = Node(x0, y0), v10 = Node(x0 + 1, y0), v01 = Node(x0, y0 + 1), v11 = Node(x0 + 1, y0 + 1);
        return (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
    }

    /// <summary>0 rural, 1 urban: a smoothstep of the density between <see cref="RuralBelow"/> and <see cref="UrbanAt"/>.</summary>
    public double Weight(double e, double n)
    {
        double t = Math.Clamp((Density(e, n) - RuralBelow) / (UrbanAt - RuralBelow), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The density at global lattice node (x east, y north), each node computed in the tile that holds it.</summary>
    private double Node(long x, long y)
    {
        // node (x, y) sits at E = x * Cell, N = y * Cell; its tile: the one whose cell [x, x+1) x [y, y+1) it opens
        var id = _units.TileAt(x * Cell, y * Cell);
        if (!_density.TryGetValue(id, out var d)) _density[id] = d = Build(id);
        int c = (int)(x - WestNode(id)), r = (int)(y - SouthNode(id));   // r counts north
        return d[r * PerTile + c];
    }

    /// <summary>Densities of a tile's nodes (row r north of its south edge, column c east of its west edge).</summary>
    private float[] Build(TileId id)
    {
        int size = PerTile + 2 * Margin;
        // wall[] over the tile and its margin; index (row north, column east)
        var wall = new int[(size + 1) * (size + 1)];   // summed-area table, one row/column of zeros
        for (int r = 0; r < size; r++)
        {
            long gy = SouthNode(id) - Margin + r;
            for (int c = 0; c < size; c++)
            {
                long gx = WestNode(id) - Margin + c;
                int w = WallAt(gx, gy) ? 1 : 0;
                wall[(r + 1) * (size + 1) + c + 1] = w + wall[r * (size + 1) + c + 1] + wall[(r + 1) * (size + 1) + c] - wall[r * (size + 1) + c];
            }
        }
        int Box(int[] sat, int stride, int r0, int c0, int r1, int c1) =>
            sat[(r1 + 1) * stride + c1 + 1] - sat[r0 * stride + c1 + 1] - sat[(r1 + 1) * stride + c0] + sat[r0 * stride + c0];

        // near over the tile and a City margin
        int inner = PerTile + 2 * City;
        var near = new int[(inner + 1) * (inner + 1)];
        for (int r = 0; r < inner; r++)
            for (int c = 0; c < inner; c++)
            {
                int rr = r + Near, cc = c + Near;   // in wall[] coordinates (Margin - City = Near)
                int v = Box(wall, size + 1, rr - Near, cc - Near, rr + Near, cc + Near) > 0 ? 1 : 0;
                near[(r + 1) * (inner + 1) + c + 1] = v + near[r * (inner + 1) + c + 1] + near[(r + 1) * (inner + 1) + c] - near[r * (inner + 1) + c];
            }

        var density = new float[PerTile * PerTile];
        float all = (2 * Window + 1) * (2 * Window + 1), allCity = (2 * City + 1) * (2 * City + 1);
        float scale = (float)(UrbanAt / CityAt);
        for (int r = 0; r < PerTile; r++)
            for (int c = 0; c < PerTile; c++)
            {
                int rr = r + City, cc = c + City;
                float local = Box(near, inner + 1, rr - Window, cc - Window, rr + Window, cc + Window) / all;
                float city = Box(near, inner + 1, rr - City, cc - City, rr + City, cc + City) / allCity;
                density[r * PerTile + c] = Math.Max(local, city * scale);
            }
        return density;
    }

    /// <summary>The global lattice column of a tile's west edge and the row of its south edge (tiles are a whole number of cells).</summary>
    private long WestNode(TileId id) => (long)Math.Round(_units.West(id) / Cell);
    private long SouthNode(TileId id) => (long)Math.Round(_units.South(id) / Cell);

    /// <summary>
    /// Whether a wall stands in the global 2 m cell [gx, gx+1) x [gy, gy+1) (gy counting north), memoised in blocks of
    /// 64 x 64 cells (#1699): neighbouring tiles' margins read the same cells, and on a frame's plane (FrameUnits) the
    /// tile holding a cell costs a cube projection, every leaf's 1 km square overlapping its neighbours'.
    /// </summary>
    private bool WallAt(long gx, long gy)
    {
        var key = (gx >> BlockBits, gy >> BlockBits);
        if (!_walls.TryGetValue(key, out var bits))
        {
            bits = new ulong[BlockCells];
            long x0 = key.Item1 << BlockBits, y0 = key.Item2 << BlockBits;
            for (int r = 0; r < BlockCells; r++)
            {
                ulong row = 0;
                for (int c = 0; c < BlockCells; c++)
                    if (ComputeWallAt(x0 + c, y0 + r)) row |= 1UL << c;
                bits[r] = row;
            }
            _walls[key] = bits;
        }
        return (bits[(int)(gy & (BlockCells - 1))] & (1UL << (int)(gx & (BlockCells - 1)))) != 0;
    }

    private const int BlockBits = 6, BlockCells = 1 << BlockBits;
    private readonly Dictionary<(long, long), ulong[]> _walls = new();

    private bool ComputeWallAt(long gx, long gy)
    {
        var id = _units.TileAt(gx * Cell, gy * Cell);
        if (facades.Coarse(id) is not { } mask) return false;
        int c = (int)(gx - WestNode(id));
        int rNorth = (int)(gy - SouthNode(id));
        int r = PerTile - 1 - rNorth;   // Coarse rows count south from the north edge
        return mask[r * PerTile + c];
    }
}
