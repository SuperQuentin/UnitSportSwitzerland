namespace UnitSport.Tools.RoadGen.Geometry;

using UnitSport.Terrain.Format;

/// <summary>
/// The frame instance of <see cref="IUnits"/> (#1507, G2 B3 4b): the plane is a work frame's
/// (<see cref="WorkPlane"/>, true metres within 3 ppm), the stage's tiles are its leaves. A leaf is named by a
/// <see cref="TileId"/> (<see cref="Tile"/>: E = face × 2^level + x, N = y) so the stage's dictionaries and its
/// km caches stay as they are, and its tile-local frame is a <see cref="TileSizeM"/> square on the plane,
/// plane-aligned and centred on the leaf (its centre rounded to <see cref="Snap"/> metres, so the km caches'
/// 0.5 m and 2 m lattices line up across leaves). A leaf is a rotated, slightly curved square on the plane
/// (~610 m at level 14): its own edges come from its <see cref="LeafTransform"/>, which the frame loop also
/// uses to turn the finished records into the leaf's anchor frame (<c>LeafRecords</c>).
/// </summary>
public sealed class FrameUnits : IUnits
{
    /// <summary>A leaf's square on the plane: room for a level-14 leaf turned by any angle (its diagonal ~870 m).</summary>
    public const double Box = 1000.0;

    /// <summary>The squares' centres are whole multiples of this (the urban field's 2 m lattice).</summary>
    public const double Snap = 2.0;

    private readonly WorkFrame _frame;
    private readonly Func<CubeKey, LeafTransform> _transform;
    private readonly int _level;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TileId, (double West, double North, double Spacing)> _boxes = new();

    public FrameUnits(WorkFrame frame, Func<CubeKey, LeafTransform> transform, int level = 14)
    {
        if (level > 14) throw new ArgumentOutOfRangeException(nameof(level), "a TileId names leaves up to level 14");
        _frame = frame;
        _transform = transform;
        _level = level;
    }

    public WorkFrame Frame => _frame;

    public int Level => _level;

    /// <summary>The leaf a tile names.</summary>
    public CubeKey Leaf(TileId id) => new(id.E >> _level, _level, id.E & ((1 << _level) - 1), id.N);

    /// <summary>The tile naming a leaf.</summary>
    public TileId Tile(CubeKey leaf) => new((leaf.Face << _level) | leaf.X, leaf.Y);

    public LeafTransform TransformOf(TileId id) => _transform(Leaf(id));

    public double TileSizeM => Box;

    public TileId TileAt(double x, double y)
    {
        var (f, u, v) = CubeSphere.FromNormal(_frame.Plane.NormalAt(x, y));
        return Tile(CubeKey.FromFace(f, u, v, _level));
    }

    private (double West, double North, double Spacing) BoxOf(TileId id) => _boxes.GetOrAdd(id, k =>
    {
        var t = TransformOf(k);
        int cells = t.Cells;
        var (cx, cy) = t.LatticeToPlane(cells * 0.5, cells * 0.5);
        double x0 = Snap * Math.Round(cx / Snap), y0 = Snap * Math.Round(cy / Snap);
        double west = x0 - Box / 2, north = y0 + Box / 2;
        foreach (var (i, j) in (ReadOnlySpan<(int, int)>)[(0, 0), (cells, 0), (0, cells), (cells, cells)])
        {
            var (px, py) = t.LatticeToPlane(i, j);
            if (px < west || px > west + Box || py > north || py < north - Box)
                throw new InvalidOperationException($"leaf {Leaf(k)} does not fit its {Box} m square");
        }
        var (ax, ay) = t.LatticeToPlane(0, 0);
        var (bx, by) = t.LatticeToPlane(cells, 0);
        double spacing = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) / cells;
        return (west, north, spacing);
    });

    public double West(TileId id) => BoxOf(id).West;
    public double South(TileId id) => BoxOf(id).North - Box;
    public double North(TileId id) => BoxOf(id).North;

    /// <summary>Plane-aligned tiles: the identity (the frame loop turns headings into each leaf's anchor).</summary>
    public Vec2 DirectionToTile(TileId id, Vec2 at, Vec2 direction) => direction;
    public double AngleToTile(TileId id, Vec2 at, double angle) => angle;

    /// <summary>A tile-local point as the leaf's fractional lattice point.</summary>
    private (double I, double J, int Cells, double Spacing) Lattice(TileId id, double x, double z)
    {
        var (west, north, spacing) = BoxOf(id);
        var t = TransformOf(id);
        var (i, j) = t.PlaneToLattice(west + x, north - z);
        return (i, j, t.Cells, spacing);
    }

    public bool OnEdge(TileId id, double x, double z, double eps)
    {
        var (i, j, cells, spacing) = Lattice(id, x, z);
        double e = eps / spacing, slack = e + 1e-9;
        if (i < -slack || j < -slack || i > cells + slack || j > cells + slack) return false;
        return Math.Abs(i) < e || Math.Abs(j) < e || Math.Abs(i - cells) < e || Math.Abs(j - cells) < e;
    }

    public bool Inside(TileId id, double x, double z)
    {
        var (west, north, _) = BoxOf(id);
        if (TileAt(west + x, north - z) == id) return true;
        var (i, j, cells, _) = Lattice(id, x, z);
        const double slack = 1e-6;
        return i >= -slack && j >= -slack && i <= cells + slack && j <= cells + slack;
    }

    public bool OnAnyEdge(double x, double y, double eps)
    {
        var id = TileAt(x, y);
        return OnEdge(id, x - West(id), North(id) - y, eps);
    }

    /// <summary>The leaf, then ring after ring of its neighbours (<see cref="CubeKey.Neighbours"/>, across face edges and round cube corners).</summary>
    public IEnumerable<TileId> Around(TileId id, int ring)
    {
        var seen = new HashSet<CubeKey> { Leaf(id) };
        var order = new List<CubeKey> { Leaf(id) };
        var front = new List<CubeKey> { Leaf(id) };
        for (int r = 0; r < ring; r++)
        {
            var next = new List<CubeKey>();
            foreach (var k in front)
                foreach (var n in k.Neighbours())
                    if (seen.Add(n)) { next.Add(n); order.Add(n); }
            front = next;
        }
        return order.Select(Tile);
    }
}
