using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A cube region's leaves as the region-wide stages read a built region (#1711, G2 D1): those stages (places, POIs, landings,
/// airports, the OSM overlay, tram stops, SITG signs, lifts) work in LV95 notation, the swisstopo sources' own, and read of the
/// built region its extent, its ground, its still water, its roads and its buildings. An LV95 build gave those from its
/// <c>manifest.json</c>, <c>.terr</c>, <c>.water</c>, <c>.road</c> and <c>.bldg</c>; this gives them from the leaves, so the cube
/// build needs no LV95 build first:
/// <list type="bullet">
/// <item><see cref="Tiles"/>: the LV95 km cells any leaf with heights reaches (where a stage keeps a source's point);</item>
/// <item><see cref="Ground"/> and <see cref="Level"/> (<see cref="IShoreSampler"/>): the leaf mesh above sea level at an LV95 point
/// (bilinear on the lattice, the bed under a lake; <see cref="FrameGround"/> on the level-9 frame's plane), the still level of
/// the nearest water sample; NaN off the leaves;</item>
/// <item><see cref="Geo"/>: <see cref="GeoInput"/> on that ground (a latitude and longitude onto LV95 at the leaves' height);</item>
/// <item><see cref="RoadsAround"/>: the network's segments near a point, their points in an LV95 km tile's local frame;</item>
/// <item><see cref="Buildings"/>: the buildings stage's (<see cref="RegionBuildings"/>).</item>
/// </list>
/// Safe across threads.
/// </summary>
public sealed class BuiltLeaves : IShoreSampler
{
    private readonly ConcurrentDictionary<CubeKey, FrameGround> _grounds = new();
    private readonly ConcurrentDictionary<CubeKey, Lazy<(LeafTransform T, CubeWater W)?>> _water = new();
    private readonly Lazy<HashSet<TileId>> _tiles;
    private readonly HashSet<CubeKey> _leafSet;

    public string Dir { get; }
    public RegionHeights Heights { get; }
    public int LeafLevel { get; }
    public RegionBuildings Buildings { get; }
    public GeoInput Geo { get; }

    public BuiltLeaves(string region)
    {
        Dir = region;
        Heights = new RegionHeights(region);
        _leafSet = Heights.Leaves.ToHashSet();
        LeafLevel = Heights.Leaves.Count > 0 ? Heights.Leaves[0].Level : 14;
        Buildings = new RegionBuildings(Heights);
        Geo = GeoInput.On(Ground);
        _tiles = new Lazy<HashSet<TileId>>(ReachedTiles);
    }

    /// <summary>The LV95 km cells any leaf with heights reaches (its lattice every eighth of a side).</summary>
    public HashSet<TileId> Tiles => _tiles.Value;

    private HashSet<TileId> ReachedTiles()
    {
        var set = new ConcurrentDictionary<TileId, byte>();
        Parallel.ForEach(Heights.Leaves, key =>
        {
            const int n = 8;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    var p = CubeSphere.OnEllipsoid(CubeSphere.Normal(key.Face, key.U0 + key.Size * i / n, key.V0 + key.Size * j / n), 0);
                    var (e, nn, _) = SwissGeodesy.FromEcef(p);
                    set.TryAdd(TileId.FromLv95(e, nn), 0);
                }
        });
        return set.Keys.ToHashSet();
    }

    /// <summary>Whether a leaf with heights holds the LV95 point.</summary>
    public bool Contains(double e, double n) => _leafSet.Contains(LeafAt(e, n, SwissGeodesy.GroundH - 50));

    private CubeKey LeafAt(double e, double n, double hLhn95)
    {
        var g = SwissGeodesy.ToWgs84(e, n, hLhn95);
        return CubeKey.FromLatLon(g.LatDeg, g.LonDeg, LeafLevel);
    }

    private FrameGround FrameOf(CubeKey leaf) => _grounds.GetOrAdd(leaf.AncestorAt(WorkFrame.Level),
        cell => new FrameGround(new WorkFrame(cell, SwissSeaLevel.Instance), Heights.Read, LeafLevel));

    /// <summary>The leaf mesh above sea level at an LV95 point (the bed under water), NaN off the leaves.</summary>
    public double Ground(double e, double n)
    {
        // the point's latitude and longitude at the Plateau's height, then once more at the ground found (a few centimetres)
        double h = Sample(e, n, SwissGeodesy.GroundH - 50);
        if (double.IsNaN(h)) return h;
        double again = Sample(e, n, h);
        return double.IsNaN(again) ? h : again;
    }

    private double Sample(double e, double n, double hLhn95)
    {
        var leaf = LeafAt(e, n, hLhn95);
        var ground = FrameOf(leaf);
        var (x, y, _) = ground.Frame.FromSource(Lv95Source.Instance, e, n, hLhn95);
        return ground.Sample(x, y) ?? double.NaN;
    }

    /// <summary>The still water level above sea level at an LV95 point (the nearest water sample), NaN dry or off the leaves.</summary>
    public double Level(double e, double n)
    {
        double g = Ground(e, n);
        double h = double.IsNaN(g) ? SwissGeodesy.GroundH - 50 : g;
        var leaf = LeafAt(e, n, h);
        if (_water.GetOrAdd(leaf, k => new Lazy<(LeafTransform, CubeWater)?>(() =>
            Heights.Has(k) && Heights.ReadKind(k, LeafKind.Water) is { } sec
                ? (new LeafTransform(FrameOf(k).Frame, k), LeafWater.Decode(sec)) : null)).Value is not var (t, w))
            return double.NaN;
        var (x, y, _) = t.Frame.FromSource(Lv95Source.Instance, e, n, h);
        var (i, j) = t.PlaneToLattice(x, y);
        int si = Math.Clamp((int)Math.Round(i / CubeWater.Stride), 0, w.Side - 1), sj = Math.Clamp((int)Math.Round(j / CubeWater.Stride), 0, w.Side - 1);
        float level = w.Level[sj * w.Side + si];
        return float.IsNaN(level) ? double.NaN : level - t.UndulationAt(si * CubeWater.Stride, sj * CubeWater.Stride);
    }

    /// <summary>
    /// The network's segments of the leaves within <paramref name="reach"/> metres of an LV95 point, each with the LV95 km tile its
    /// first point is in and its points in that tile's local frame (X east of its west edge, Y the LHN95 height, Z south of its
    /// north edge), as a <c>.road</c> tile holds them.
    /// </summary>
    public IEnumerable<(TileId Tile, RoadSegment Segment)> RoadsAround(double e, double n, double reach)
    {
        var keys = new HashSet<CubeKey>();
        foreach (var (de, dn) in (ReadOnlySpan<(double, double)>)[(0, 0), (-reach, -reach), (reach, -reach), (-reach, reach), (reach, reach)])
        {
            var k = LeafAt(e + de, n + dn, SwissGeodesy.GroundH - 50);
            keys.Add(k);
        }
        var list = new List<(TileId, RoadSegment)>();
        foreach (var key in keys.OrderBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X))
        {
            if (!Heights.Has(key) || Heights.ReadKind(key, LeafKind.Roads) is not { } sec) continue;
            var anchor = key.Anchor;
            foreach (var seg in LeafRoads.Decode(sec).Segments)
            {
                if (seg.PointCount == 0) continue;
                var p = seg.Points;
                var lv = new (double E, double N, double H)[seg.PointCount];
                for (int i = 0; i < lv.Length; i++) lv[i] = SwissGeodesy.FromEcef(anchor.ToEcef(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]));
                var tile = TileId.FromLv95(lv[0].E, lv[0].N);
                var pts = new float[lv.Length * 3];
                for (int i = 0; i < lv.Length; i++)
                    (pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]) = ((float)(lv[i].E - tile.MinE), (float)lv[i].H, (float)(tile.MaxN - lv[i].N));
                list.Add((tile, new RoadSegment
                {
                    Class = seg.Class, Surface = seg.Surface, Flags = seg.Flags, Width = seg.Width, Points = pts, Attributes = seg.Attributes,
                }));
            }
        }
        return list;
    }
}
