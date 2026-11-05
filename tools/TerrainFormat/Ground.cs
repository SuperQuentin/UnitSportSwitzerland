using System.Collections.Concurrent;

namespace UnitSport.Terrain.Format;

/// <summary>
/// The ground a build stage drapes on (#1507, G2 B3): the height above sea level at a point of the stage's
/// plane, or null where there is none. Two instances: <see cref="Lv95Ground"/> over the LV95 build's chunk
/// grids (x, y = LV95 E, N), what the stages sample today; and <see cref="FrameGround"/> over the leaves of
/// a work frame (x, y on its <see cref="WorkPlane"/>). A stage written against this runs on either.
/// </summary>
public interface IGround
{
    double? Sample(double x, double y);
}

/// <summary>
/// The LV95 ground over loaded chunk grids (TerrainSampler's behaviour, #1507: the same code, moved).
///
/// The naive version — resolve the tile with <see cref="TileId.FromLv95"/>, sample it, give up if it is
/// absent — fails on any position that lands exactly on a tile boundary, because <c>FromLv95</c> can only
/// name one of the two (or four) tiles that share that lattice line. Clipped polylines have a vertex on the
/// boundary by construction, and feature passes run in batches, so the tile it names is regularly the one
/// that is not loaded. Every tile sharing the position is tried instead. Heights along a shared edge are
/// bit-identical (global quantization), so which one answers does not matter.
/// </summary>
public sealed class Lv95Ground(Dictionary<TileId, ChunkGrid> grids) : IGround
{
    public double? Sample(double e, double n)
    {
        int te = (int)Math.Floor(e / ChunkFormat.TileSizeM);
        int tn = (int)Math.Floor(n / ChunkFormat.TileSizeM);

        if (grids.TryGetValue(new TileId(te, tn), out var g)) return g.SampleHeight(e, n);

        // on a boundary the position also belongs to the tile west and/or south of it
        bool onEast = e - te * ChunkFormat.TileSizeM < 1e-6;
        bool onNorth = n - tn * ChunkFormat.TileSizeM < 1e-6;

        if (onEast && grids.TryGetValue(new TileId(te - 1, tn), out g)) return g.SampleHeight(e, n);
        if (onNorth && grids.TryGetValue(new TileId(te, tn - 1), out g)) return g.SampleHeight(e, n);
        if (onEast && onNorth && grids.TryGetValue(new TileId(te - 1, tn - 1), out g))
            return g.SampleHeight(e, n);

        return null;
    }
}

/// <summary>
/// A work frame's ground on the leaf mesh (#1507): the leaf under a plane point (its geodetic normal's
/// cube cell), the point's fractional lattice position there (<see cref="LeafTransform"/>), and the mesh's
/// ellipsoidal height as the game draws it (each lattice quad split along (i, j)–(i+1, j+1), heights
/// linear over each triangle), less the sea level at the point. On a leaf's edge, or where a leaf is not
/// built or has no data at those vertices, the neighbours that hold the point are tried, so a point on a
/// shared edge always finds the ground (the edges are the same bits from either side). Leaves come from
/// <paramref name="leaves"/> (null: not built), each read once; safe to call from several threads.
/// </summary>
public sealed class FrameGround(WorkFrame frame, Func<CubeKey, CubeTile?> leaves, int level = 14) : IGround
{
    /// <summary>How far past a leaf's edge, in lattice steps, a point still counts as on it (rounding of the inverse).</summary>
    public const double EdgeSlack = 1e-6;

    private sealed record Leaf(LeafTransform T, CubeTile Tile);

    private readonly ConcurrentDictionary<CubeKey, Lazy<Leaf?>> _leaves = new();

    public WorkFrame Frame => frame;

    /// <summary>The leaf's transform in this frame, or null when the leaf is not built.</summary>
    public LeafTransform? TransformOf(CubeKey key) => Get(key)?.T;

    private Leaf? Get(CubeKey key) => _leaves.GetOrAdd(key, k => new Lazy<Leaf?>(() =>
        leaves(k) is { } tile ? new Leaf(new LeafTransform(frame, k, tile.Bits), tile) : null)).Value;

    public double? Sample(double x, double y)
    {
        var (f, u, v) = CubeSphere.FromNormal(frame.Plane.NormalAt(x, y));
        var key = CubeKey.FromFace(f, u, v, level);
        if (TryLeaf(key, x, y, out double h)) return h;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dy != 0) && key.Neighbour(dx, dy) is { } n && TryLeaf(n, x, y, out h)) return h;
        return null;
    }

    private bool TryLeaf(CubeKey key, double x, double y, out double hSea)
    {
        hSea = double.NaN;
        if (Get(key) is not { } leaf) return false;
        var t = leaf.T;
        var (i, j) = t.PlaneToLattice(x, y);
        int cells = t.Cells;
        if (i < -EdgeSlack || j < -EdgeSlack || i > cells + EdgeSlack || j > cells + EdgeSlack) return false;
        i = Math.Clamp(i, 0, cells);
        j = Math.Clamp(j, 0, cells);
        double h = MeshHeight(leaf.Tile.Heights, cells, i, j);
        if (double.IsNaN(h)) return false;
        hSea = h - t.UndulationAt(i, j);
        return true;
    }

    /// <summary>The ellipsoidal height of the leaf mesh at fractional lattice point (i, j): the quad split along (i, j)–(i+1, j+1), NaN where a corner has none.</summary>
    public static double MeshHeight(float[] heights, int cells, double i, double j)
    {
        int n = cells + 1;
        int qi = Math.Min((int)i, cells - 1), qj = Math.Min((int)j, cells - 1);
        double fi = i - qi, fj = j - qj;
        int k = qj * n + qi;
        double h00 = heights[k], h11 = heights[k + n + 1];
        return fi >= fj
            ? h00 + (heights[k + 1] - h00) * fi + (h11 - heights[k + 1]) * fj
            : h00 + (h11 - heights[k + n]) * fi + (heights[k + n] - h00) * fj;
    }
}
