using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The cube-native build's loop (#1507, G2 B3; docs/plans/global-tiling-g2.md): the level-9 cells that hold
/// wanted leaves, in (face, y, x) order, one <see cref="FramePlan"/> each. A stage runs frame by frame: it
/// reads its sources through the frame's query boxes and <see cref="LeafRegion"/>s, works on the frame's
/// <see cref="WorkPlane"/>, and writes the frame's core leaves (one <c>.cubl</c> bundle) through the
/// <see cref="LeafWriter"/>. The margin leaves are what lies within <see cref="MarginM"/> round the cell,
/// across face edges too (<see cref="CubeKey.Neighbour"/>): context a stage reads, never writes.
/// </summary>
public sealed class FrameDriver
{
    public int FrameLevel { get; }

    public int LeafLevel { get; }

    public double MarginM { get; }

    public ISeaLevel SeaLevel { get; }

    public IReadOnlySet<CubeKey> Wanted { get; }

    /// <summary>The frames in the order every stage runs them: face, then y, then x of the cell.</summary>
    public IReadOnlyList<FramePlan> Frames { get; }

    public FrameDriver(IEnumerable<CubeKey> wantedLeaves, double marginM, ISeaLevel seaLevel, int frameLevel = WorkFrame.Level)
    {
        Wanted = wantedLeaves.ToHashSet();
        if (Wanted.Count == 0) throw new ArgumentException("a frame driver needs at least one leaf", nameof(wantedLeaves));
        LeafLevel = Wanted.First().Level;
        if (Wanted.Any(k => k.Level != LeafLevel)) throw new ArgumentException("the wanted leaves are of several levels", nameof(wantedLeaves));
        if (frameLevel > LeafLevel) throw new ArgumentException($"frames at level {frameLevel} under leaves at {LeafLevel}", nameof(frameLevel));
        FrameLevel = frameLevel;
        MarginM = marginM;
        SeaLevel = seaLevel;
        Frames = Wanted.GroupBy(k => k.AncestorAt(frameLevel))
            .OrderBy(g => g.Key.Face).ThenBy(g => g.Key.Y).ThenBy(g => g.Key.X)
            .Select(g => new FramePlan(this, g.Key, g.OrderBy(k => k.Y).ThenBy(k => k.X).ToList()))
            .ToList();
    }

    /// <summary>
    /// The leaves under LV95 kilometre tiles of source data (G1's tile choice): a 100 m lattice of probe
    /// points over each tile, each giving the leaf it falls in, inside <paramref name="box"/> if one is given.
    /// </summary>
    public static HashSet<CubeKey> LeavesUnderLv95(IEnumerable<TileId> tiles, int level, (double MinE, double MinN, double MaxE, double MaxN)? box = null)
    {
        var keys = new HashSet<CubeKey>();
        foreach (var t in tiles)
        {
            if (box is { } b && !(t.MinE + 1000 > b.MinE && t.MinE < b.MaxE && t.MinN + 1000 > b.MinN && t.MinN < b.MaxN)) continue;
            for (int j = 0; j < 10; j++)
                for (int i = 0; i < 10; i++)
                {
                    double e = t.MinE + 50 + 100 * i, n = t.MinN + 50 + 100 * j;
                    if (box is { } c && (e < c.MinE || e > c.MaxE || n < c.MinN || n > c.MaxN)) continue;
                    var g = SwissGeodesy.ToWgs84(e, n, 500);
                    keys.Add(CubeKey.FromLatLon(g.LatDeg, g.LonDeg, level));
                }
        }
        return keys;
    }

    /// <summary>The plane point of a cell's face point (u, v) in its own [0, 1]² (0 at the cell's south-west), on the ellipsoid.</summary>
    public static (double X, double Y) PlanePoint(WorkPlane plane, CubeKey key, double fu, double fv) =>
        plane.ToPlane(CubeSphere.OnEllipsoid(CubeSphere.Normal(key.Face, key.U0 + fu * key.Size, key.V0 + fv * key.Size), 0));

    /// <summary>A cell's outline: <paramref name="perSide"/> points a side, counter-clockwise from the south-west corner.</summary>
    public static (double X, double Y)[] Outline(WorkPlane plane, CubeKey key, int perSide)
    {
        var pts = new (double, double)[4 * perSide];
        for (int s = 0; s < perSide; s++)
        {
            double f = s / (double)perSide;
            pts[s] = PlanePoint(plane, key, f, 0);
            pts[perSide + s] = PlanePoint(plane, key, 1, f);
            pts[2 * perSide + s] = PlanePoint(plane, key, 1 - f, 1);
            pts[3 * perSide + s] = PlanePoint(plane, key, 0, 1 - f);
        }
        return pts;
    }
}

/// <summary>One work frame of a <see cref="FrameDriver"/>: a level-9 cell, its leaves, its margin, its source boxes.</summary>
public sealed class FramePlan
{
    private readonly FrameDriver _driver;
    private readonly Lazy<WorkFrame> _frame;
    private readonly Lazy<IReadOnlyList<CubeKey>> _margin;
    private readonly Lazy<(double X, double Y)[]> _outline;
    private readonly ConcurrentDictionary<CubeKey, LeafTransform> _transforms = new();

    public CubeKey Cell { get; }

    /// <summary>The wanted leaves in the cell, (y, x) order: what the frame writes.</summary>
    public IReadOnlyList<CubeKey> Core { get; }

    internal FramePlan(FrameDriver driver, CubeKey cell, IReadOnlyList<CubeKey> core)
    {
        _driver = driver;
        Cell = cell;
        Core = core;
        _frame = new(() => new WorkFrame(cell, driver.SeaLevel));
        _outline = new(() => FrameDriver.Outline(Frame.Plane, cell, 1 << (driver.LeafLevel - driver.FrameLevel)));
        _margin = new(() => FindMargin());
    }

    public WorkFrame Frame => _frame.Value;

    /// <summary>The cell's outline on the frame's plane, a point at every leaf corner along it.</summary>
    public IReadOnlyList<(double X, double Y)> Outline => _outline.Value;

    /// <summary>The wanted leaves outside the cell within the driver's margin of it, (face, y, x) order.</summary>
    public IReadOnlyList<CubeKey> Margin => _margin.Value;

    /// <summary>A leaf's transform in this frame (core or margin), made once.</summary>
    public LeafTransform Transform(CubeKey leaf) => _transforms.GetOrAdd(leaf, k => new LeafTransform(Frame, k));

    /// <summary>The core leaves and the margin, as a <see cref="LeafRegion"/> in a source's coordinates: what a reader keeps.</summary>
    public LeafRegion Region(ISourceProjection source, double? marginM = null) => new(Core, source, marginM ?? _driver.MarginM);

    /// <summary>
    /// The box a source is asked for, in its own coordinates: the cell's outline pushed out by the margin
    /// (each outline point moved the margin in eight directions on the plane), projected, plus a metre.
    /// </summary>
    public (double MinA, double MinB, double MaxA, double MaxB) QueryBox(ISourceProjection source, double? marginM = null)
    {
        double m = marginM ?? _driver.MarginM;
        double minA = double.MaxValue, minB = double.MaxValue, maxA = double.MinValue, maxB = double.MinValue;
        foreach (var (x, y) in Outline)
            for (int d = 0; d <= 8; d++)
            {
                double ang = d * Math.PI / 4, r = d == 8 ? 0 : m;
                var (lat, lon) = Frame.Plane.FromPlane(x + r * Math.Cos(ang), y + r * Math.Sin(ang));
                var (a, b) = source.FromLatLon(lat, lon);
                (minA, minB, maxA, maxB) = (Math.Min(minA, a), Math.Min(minB, b), Math.Max(maxA, a), Math.Max(maxB, b));
            }
        var c = Frame.Cell.Centre;
        var (sa, sb) = LeafRegion.MetresPerUnit(source, c.LatDeg, c.LonDeg);
        return (minA - 1 / sa, minB - 1 / sb, maxA + 1 / sa, maxB + 1 / sb);
    }

    /// <summary>Breadth-first from the cell's border over the leaf lattice (through face edges), keeping what is within the margin.</summary>
    private IReadOnlyList<CubeKey> FindMargin()
    {
        var found = new List<CubeKey>();
        if (_driver.MarginM <= 0) return found;
        int shift = _driver.LeafLevel - _driver.FrameLevel, side = 1 << shift;
        bool InCell(CubeKey k) => k.Face == Cell.Face && k.AncestorAt(_driver.FrameLevel) == Cell;
        var seen = new HashSet<CubeKey>();
        var queue = new Queue<CubeKey>();
        for (int s = 0; s < side; s++)
            foreach (var (x, y) in new[] { (s, 0), (s, side - 1), (0, s), (side - 1, s) })
            {
                var leaf = new CubeKey(Cell.Face, _driver.LeafLevel, (Cell.X << shift) + x, (Cell.Y << shift) + y);
                foreach (var n in leaf.Neighbours())
                    if (!InCell(n) && seen.Add(n) && Distance(n) <= _driver.MarginM) queue.Enqueue(n);
            }
        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            found.Add(k);
            foreach (var n in k.Neighbours())
                if (!InCell(n) && seen.Add(n) && Distance(n) <= _driver.MarginM) queue.Enqueue(n);
        }
        return found.Where(_driver.Wanted.Contains).OrderBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X).ToList();
    }

    /// <summary>The distance on the plane from a leaf outside the cell to the cell's outline.</summary>
    private double Distance(CubeKey leaf)
    {
        var poly = FrameDriver.Outline(Frame.Plane, leaf, 2);
        var cell = Outline;
        double d = double.MaxValue;
        for (int a = 0; a < poly.Length; a++)
        {
            var p = poly[a];
            var q = poly[(a + 1) % poly.Length];
            for (int b = 0; b < cell.Count; b++)
            {
                var r = cell[b];
                var s = cell[(b + 1) % cell.Count];
                d = Math.Min(d, Math.Min(Geometry2.PointSegment(p.X, p.Y, r.X, r.Y, s.X, s.Y), Geometry2.PointSegment(r.X, r.Y, p.X, p.Y, q.X, q.Y)));
            }
        }
        return d;
    }
}

/// <summary>
/// Cube leaves as a source read sees them (#1507): <see cref="TileRegion"/>'s contract over leaves, in a
/// source's own coordinates, so a reader keeps what it kept for LV95 tiles. Each leaf is its outline (eight
/// points along its edges, projected at sea level), the margin and the query boxes measured in metres by
/// the source's scale at the region's centre (LV95 and Lambert-93 within 0.1 %, latitude and longitude by
/// their degree lengths there; X is the source's first coordinate, so latitude for
/// <see cref="GeographicSource"/>). A height moves an LV95 point 3e-5 m a metre, hence
/// <see cref="SlackM"/>. Reads only once built: <see cref="Touches(double, double, double, double)"/> can be
/// asked from several threads.
/// </summary>
public sealed class LeafRegion : ISourceRegion
{
    /// <summary>Added to the margin: a leaf's outline at 0 m against features up to ~5,000 m high, and its straight edges.</summary>
    public const double SlackM = 0.5;

    private const double BucketM = 1000;

    private readonly double _sa, _sb, _reachM;
    private readonly double[] _polys; // per leaf 8 points (x, y) in metres (source units × scale)
    private readonly double[] _boxes; // per leaf min x, min y, max x, max y in metres, pushed out by the reach
    private readonly Dictionary<(long, long), List<int>> _buckets = new();

    public IReadOnlyList<CubeKey> Leaves { get; }

    public double MinE { get; }
    public double MinN { get; }
    public double MaxE { get; }
    public double MaxN { get; }
    public int Count => Leaves.Count;

    public LeafRegion(IEnumerable<CubeKey> leaves, ISourceProjection source, double marginM = 0)
    {
        Leaves = leaves.ToList();
        if (Leaves.Count == 0) throw new ArgumentException("A leaf region needs at least one leaf.", nameof(leaves));
        var mid = Leaves[Leaves.Count / 2].Centre;
        (_sa, _sb) = MetresPerUnit(source, mid.LatDeg, mid.LonDeg);
        _reachM = marginM + SlackM;
        _polys = new double[Leaves.Count * 16];
        _boxes = new double[Leaves.Count * 4];
        double minA = double.MaxValue, minB = double.MaxValue, maxA = double.MinValue, maxB = double.MinValue;
        for (int k = 0; k < Leaves.Count; k++)
        {
            var key = Leaves[k];
            double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
            for (int p = 0; p < 8; p++)
            {
                // corners and edge middles, counter-clockwise from the south-west
                var (fu, fv) = p switch { 0 => (0.0, 0.0), 1 => (0.5, 0.0), 2 => (1.0, 0.0), 3 => (1.0, 0.5), 4 => (1.0, 1.0), 5 => (0.5, 1.0), 6 => (0.0, 1.0), _ => (0.0, 0.5) };
                var g = CubeSphere.ToGeodetic(key.Face, key.U0 + fu * key.Size, key.V0 + fv * key.Size);
                var (a, b) = source.FromLatLon(g.LatDeg, g.LonDeg);
                double x = a * _sa, y = b * _sb;
                _polys[k * 16 + 2 * p] = x;
                _polys[k * 16 + 2 * p + 1] = y;
                (bx0, by0, bx1, by1) = (Math.Min(bx0, x), Math.Min(by0, y), Math.Max(bx1, x), Math.Max(by1, y));
                (minA, minB, maxA, maxB) = (Math.Min(minA, a), Math.Min(minB, b), Math.Max(maxA, a), Math.Max(maxB, b));
            }
            _boxes[k * 4] = bx0 - _reachM;
            _boxes[k * 4 + 1] = by0 - _reachM;
            _boxes[k * 4 + 2] = bx1 + _reachM;
            _boxes[k * 4 + 3] = by1 + _reachM;
            for (long bx = Bucket(_boxes[k * 4]); bx <= Bucket(_boxes[k * 4 + 2]); bx++)
                for (long by = Bucket(_boxes[k * 4 + 1]); by <= Bucket(_boxes[k * 4 + 3]); by++)
                {
                    if (!_buckets.TryGetValue((bx, by), out var list)) _buckets[(bx, by)] = list = new List<int>();
                    list.Add(k);
                }
        }
        MinE = minA - _reachM / _sa;
        MinN = minB - _reachM / _sb;
        MaxE = maxA + _reachM / _sa;
        MaxN = maxB + _reachM / _sb;
    }

    private static long Bucket(double m) => (long)Math.Floor(m / BucketM);

    /// <summary>Metres per unit of a source's two coordinates at a place (numerically, over ~100 m).</summary>
    public static (double A, double B) MetresPerUnit(ISourceProjection source, double latDeg, double lonDeg)
    {
        var (a0, b0) = source.FromLatLon(latDeg, lonDeg);
        double Along(double da, double db)
        {
            var (la, lo) = source.ToLatLon(a0, b0);
            var (lb, lob) = source.ToLatLon(a0 + da, b0 + db);
            var p = Ellipsoid.Wgs84.ToEcef(la * Math.PI / 180, lo * Math.PI / 180, 0);
            var q = Ellipsoid.Wgs84.ToEcef(lb * Math.PI / 180, lob * Math.PI / 180, 0);
            return p.DistanceTo(q);
        }
        double sa = Along(1e-6, 0) / 1e-6, sb = Along(0, 1e-6) / 1e-6;
        // again over ~100 m: the first guess only sizes the step
        sa = Along(100 / sa, 0) / (100 / sa);
        sb = Along(0, 100 / sb) / (100 / sb);
        return (sa, sb);
    }

    public bool Touches(double minX, double maxX, double minY, double maxY)
    {
        double x0 = minX * _sa, x1 = maxX * _sa, y0 = minY * _sb, y1 = maxY * _sb;
        long bx0 = Bucket(x0), bx1 = Bucket(x1), by0 = Bucket(y0), by1 = Bucket(y1);
        // a box over more buckets than there are leaves (a lake, a canton): ask the leaves instead
        if ((bx1 - bx0 + 1) * (by1 - by0 + 1) > Leaves.Count)
        {
            for (int k = 0; k < Leaves.Count; k++)
                if (Near(k, x0, x1, y0, y1)) return true;
            return false;
        }
        for (long bx = bx0; bx <= bx1; bx++)
            for (long by = by0; by <= by1; by++)
                if (_buckets.TryGetValue((bx, by), out var list))
                    foreach (int k in list)
                        if (Near(k, x0, x1, y0, y1)) return true;
        return false;
    }

    public bool Touches(double x, double y) => Touches(x, x, y, y);

    private bool Near(int k, double x0, double x1, double y0, double y1)
    {
        if (x1 < _boxes[k * 4] || x0 > _boxes[k * 4 + 2] || y1 < _boxes[k * 4 + 1] || y0 > _boxes[k * 4 + 3]) return false;
        return Geometry2.BoxPolygonDistance(x0, y0, x1, y1, _polys.AsSpan(k * 16, 16)) <= _reachM;
    }
}

/// <summary>Plane geometry for the regions: point to segment, and an axis-aligned box to a convex polygon.</summary>
public static class Geometry2
{
    public static double PointSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double ex = ax + t * dx - px, ey = ay + t * dy - py;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>The distance between a box and a convex polygon (x, y pairs in order), 0 when they meet.</summary>
    public static double BoxPolygonDistance(double x0, double y0, double x1, double y1, ReadOnlySpan<double> poly)
    {
        int n = poly.Length / 2;
        if (Overlap(x0, y0, x1, y1, poly)) return 0;
        double d = double.MaxValue;
        for (int k = 0; k < n; k++)
        {
            double px = poly[2 * k], py = poly[2 * k + 1];
            double dx = Math.Max(Math.Max(x0 - px, 0), px - x1), dy = Math.Max(Math.Max(y0 - py, 0), py - y1);
            d = Math.Min(d, Math.Sqrt(dx * dx + dy * dy));
            double qx = poly[2 * ((k + 1) % n)], qy = poly[2 * ((k + 1) % n) + 1];
            d = Math.Min(d, Math.Min(Math.Min(PointSegment(x0, y0, px, py, qx, qy), PointSegment(x1, y0, px, py, qx, qy)),
                Math.Min(PointSegment(x0, y1, px, py, qx, qy), PointSegment(x1, y1, px, py, qx, qy))));
        }
        return d;
    }

    /// <summary>Separating axes: the box's two and every polygon edge's normal.</summary>
    private static bool Overlap(double x0, double y0, double x1, double y1, ReadOnlySpan<double> poly)
    {
        int n = poly.Length / 2;
        double mx0 = double.MaxValue, my0 = double.MaxValue, mx1 = double.MinValue, my1 = double.MinValue;
        for (int k = 0; k < n; k++)
        {
            mx0 = Math.Min(mx0, poly[2 * k]); mx1 = Math.Max(mx1, poly[2 * k]);
            my0 = Math.Min(my0, poly[2 * k + 1]); my1 = Math.Max(my1, poly[2 * k + 1]);
        }
        if (mx1 < x0 || mx0 > x1 || my1 < y0 || my0 > y1) return false;
        for (int k = 0; k < n; k++)
        {
            double ax = poly[2 * k], ay = poly[2 * k + 1], bx = poly[2 * ((k + 1) % n)], by = poly[2 * ((k + 1) % n) + 1];
            double nx = ay - by, ny = bx - ax;
            if (nx == 0 && ny == 0) continue;
            double pMin = double.MaxValue, pMax = double.MinValue;
            for (int q = 0; q < n; q++)
            {
                double v = poly[2 * q] * nx + poly[2 * q + 1] * ny;
                pMin = Math.Min(pMin, v); pMax = Math.Max(pMax, v);
            }
            double c00 = x0 * nx + y0 * ny, c10 = x1 * nx + y0 * ny, c01 = x0 * nx + y1 * ny, c11 = x1 * nx + y1 * ny;
            double bMin = Math.Min(Math.Min(c00, c10), Math.Min(c01, c11)), bMax = Math.Max(Math.Max(c00, c10), Math.Max(c01, c11));
            if (bMax < pMin || bMin > pMax) return false;
        }
        return true;
    }
}
