using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A cube region's buildings as the stages that still work in LV95 notation read them (#1711, G2 D1): the buildings stage's
/// <see cref="LeafKind.Buildings"/> sections (anchor frame) taken to LV95 and LHN95 rigorously (<see cref="SwissGeodesy.FromEcef"/>),
/// so the urban field of <c>--cube-cover-network</c> and the region-wide stages (airports' stands, castles, venues, givers) read
/// the leaves instead of an LV95 build's <c>.bldg</c>. An LV95 km tile (<see cref="Lv95Tile"/>) gets every building whose LV95
/// box reaches it, not only the ones whose centre it holds as the LV95 build's tiles did: a wall past a km line is a wall on both
/// sides of it. Each leaf is read once (the last <see cref="Cached"/> kept). Safe across threads.
/// </summary>
public sealed class RegionBuildings
{
    public const int Cached = 400;

    /// <summary>A building in LV95: its triangles' E, LHN95 height, N per vertex, and its LV95 box.</summary>
    public sealed record Lv95Building(Building Source, double[] Points, double MinE, double MinN, double MaxE, double MaxN, double MinH, double MaxH);

    private readonly RegionHeights _region;
    private readonly int _level;
    private readonly Dictionary<CubeKey, List<Lv95Building>> _leaves = new();
    private readonly object _lock = new();

    public RegionBuildings(RegionHeights region)
    {
        _region = region;
        _level = region.Leaves.Count > 0 ? region.Leaves[0].Level : 14;
    }

    /// <summary>Every building whose LV95 box reaches the box (in the order of the leaves' keys, then the leaf's own).</summary>
    public List<Lv95Building> In(double minE, double minN, double maxE, double maxN)
    {
        // the leaves under the box, one ring round them: a leaf owns a building by its centre, and one spans a few hundred metres
        var keys = new HashSet<CubeKey>();
        int te0 = (int)Math.Floor(minE / 1000), te1 = (int)Math.Floor(maxE / 1000), tn0 = (int)Math.Floor(minN / 1000), tn1 = (int)Math.Floor(maxN / 1000);
        var tiles = new List<TileId>();
        for (int te = te0; te <= te1; te++)
            for (int tn = tn0; tn <= tn1; tn++)
                tiles.Add(new TileId(te, tn));
        foreach (var k in FrameDriver.LeavesUnderLv95(tiles, _level, (minE, minN, maxE, maxN)))
        {
            keys.Add(k);
            foreach (var n in k.Neighbours()) keys.Add(n);
        }
        var list = new List<Lv95Building>();
        foreach (var key in keys.OrderBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X))
            foreach (var b in Leaf(key))
                if (b.MaxE >= minE && b.MinE <= maxE && b.MaxN >= minN && b.MinN <= maxN) list.Add(b);
        return list;
    }

    /// <summary>
    /// An LV95 km tile's buildings in its tile-local frame (X east of its west edge, Y the LHN95 height, Z south of its north
    /// edge), as a <c>.bldg</c> holds them: the source a <see cref="RoadGen.Network.Facades"/> reads. Null when none reaches it.
    /// </summary>
    public BuildingTile? Lv95Tile(TileId id)
    {
        var list = new List<Building>();
        foreach (var b in In(id.MinE, id.MinN, id.MinE + 1000, id.MaxN))
        {
            var p = b.Points;
            var tris = new float[p.Length];
            for (int k = 0; k + 2 < p.Length; k += 3)
                (tris[k], tris[k + 1], tris[k + 2]) = ((float)(p[k] - id.MinE), (float)p[k + 1], (float)(id.MaxN - p[k + 2]));
            var s = b.Source;
            list.Add(new Building { Kind = s.Kind, Egid = s.Egid, YearBuilt = s.YearBuilt, Floors = s.Floors, MinY = (float)b.MinH, MaxY = (float)b.MaxH, Triangles = tris });
        }
        return list.Count == 0 ? null : new BuildingTile { Id = id, Buildings = list };
    }

    private List<Lv95Building> Leaf(CubeKey key)
    {
        lock (_lock)
        {
            if (_leaves.TryGetValue(key, out var list)) return list;
            if (_leaves.Count >= Cached) _leaves.Clear();
            list = new List<Lv95Building>();
            if (_region.Has(key) && _region.ReadKind(key, LeafKind.Buildings) is { } sec)
            {
                var anchor = key.Anchor;
                foreach (var b in LeafBuildings.Decode(sec).Buildings)
                {
                    var t = b.Triangles;
                    var pts = new double[t.Length];
                    double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
                    double minH = double.MaxValue, maxH = double.MinValue;
                    for (int k = 0; k + 2 < t.Length; k += 3)
                    {
                        var (e, n, h) = SwissGeodesy.FromEcef(anchor.ToEcef(t[k], t[k + 1], t[k + 2]));
                        (pts[k], pts[k + 1], pts[k + 2]) = (e, h, n);
                        (minE, minN, maxE, maxN) = (Math.Min(minE, e), Math.Min(minN, n), Math.Max(maxE, e), Math.Max(maxN, n));
                        (minH, maxH) = (Math.Min(minH, h), Math.Max(maxH, h));
                    }
                    if (t.Length >= 3) list.Add(new Lv95Building(b, pts, minE, minN, maxE, maxN, minH, maxH));
                }
            }
            _leaves[key] = list;
            return list;
        }
    }
}
