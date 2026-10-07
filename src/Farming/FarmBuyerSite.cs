using UnitSport.Terrain.Format;

// Plain C#, no Godot: linked into the unit tests.

namespace UnitSport.Farming;

/// <summary>Where a specialty buyer's weighbridge office stands: LV95, its altitude, and the yaw that turns its front (+Z) to the road.</summary>
public readonly record struct YardSite(double E, double N, float Altitude, float Yaw);

/// <summary>
/// Picks the spot for a specialty buyer's weighbridge office and sign (#494): beside an access road
/// inside the buyer's yard, as near the buyer's point as possible, clear of every real building. A
/// buyer's point is the centroid of its factory in OSM, so it usually lies inside the factory itself:
/// a sign put there would stand in a wall.
/// </summary>
public static class FarmBuyerSite
{
    /// <summary>Half the square kept clear of buildings, m: the office and the sign beside it, and a margin.</summary>
    public const float Clearance = 6.5f;
    /// <summary>The office's centre from the road's edge, m (its front 3.7 m back, room for a lorry).</summary>
    public const float Setback = 5f;
    /// <summary>How far along a road the candidates are spaced, m.</summary>
    private const double Step = 4.0;

    /// <summary>
    /// The best site from the roads and buildings of the tiles round <paramref name="b"/>, or null
    /// when no usable road runs through its yard (the caller then stands it on the buyer's point).
    /// </summary>
    public static YardSite? Pick(FarmBuyer b, IEnumerable<(TileId Tile, RoadSegment Seg)> roads, IEnumerable<(TileId Tile, Building House)> houses) =>
        Candidates(b, roads, houses) is { Count: > 0 } all ? all[0] : null;

    /// <summary>
    /// Every usable site, nearest the buyer's point first (at most <paramref name="max"/>): the caller,
    /// which knows the terrain, takes the first one level enough.
    /// </summary>
    public static List<YardSite> Candidates(FarmBuyer b, IEnumerable<(TileId Tile, RoadSegment Seg)> roads, IEnumerable<(TileId Tile, Building House)> houses, int max = 60)
    {
        double reach = b.Reach * 0.85, near = reach + 60;
        var footprints = Footprints(b, houses, near);
        var found = new List<(double D, YardSite Site)>();
        foreach (var (tile, seg) in roads)
        {
            if (!Usable(seg.Class)) continue;
            var p = seg.Points;
            for (int i = 0; i + 5 < p.Length; i += 3)
            {
                double ae = tile.MinE + p[i], an = tile.MaxN - p[i + 2], be = tile.MinE + p[i + 3], bn = tile.MaxN - p[i + 5];
                double ve = be - ae, vn = bn - an, len = Math.Sqrt(ve * ve + vn * vn);
                if (len < 0.5) continue;
                // the left normal; both sides are tried
                double ne = -vn / len, nn = ve / len, off = seg.Width * 0.5 + Setback;
                for (double s = 0; s <= len; s += Step)
                {
                    double u = s / len, re = ae + ve * u, rn = an + vn * u;
                    if (Dist(re, rn, b.E, b.N) > near) continue;
                    float alt = p[i + 1] + (p[i + 4] - p[i + 1]) * (float)u;
                    foreach (int side in new[] { 1, -1 })
                    {
                        double ce = re + ne * off * side, cn = rn + nn * off * side;
                        double d = Dist(ce, cn, b.E, b.N);
                        if (d > reach || !Clear(ce, cn, footprints)) continue;
                        // the front faces the road: (-n * side) in LV95, world (e, -n)
                        found.Add((d, new YardSite(ce, cn, alt, (float)Math.Atan2(-ne * side, nn * side))));
                    }
                }
            }
        }
        return found.OrderBy(f => f.D).Take(max).Select(f => f.Site).ToList();
    }

    /// <summary>A road a lorry delivers by: not a motorway, a track, a path or a railway.</summary>
    public static bool Usable(RoadClass c) => c is RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane or RoadClass.Square;

    private static double Dist(double ae, double an, double be, double bn) => Math.Sqrt((ae - be) * (ae - be) + (an - bn) * (an - bn));

    /// <summary>Every building triangle near the buyer, flattened to LV95 (E, N) triples, walls (no area) left out.</summary>
    private static List<double[]> Footprints(FarmBuyer b, IEnumerable<(TileId Tile, Building House)> houses, double near)
    {
        var list = new List<double[]>();
        foreach (var (tile, house) in houses)
        {
            var t = house.Triangles;
            for (int i = 0; i + 8 < t.Length; i += 9)
            {
                var tri = new[]
                {
                    tile.MinE + t[i], tile.MaxN - t[i + 2],
                    tile.MinE + t[i + 3], tile.MaxN - t[i + 5],
                    tile.MinE + t[i + 6], tile.MaxN - t[i + 8],
                };
                double area = (tri[2] - tri[0]) * (tri[5] - tri[1]) - (tri[4] - tri[0]) * (tri[3] - tri[1]);
                if (Math.Abs(area) < 0.01) continue;
                if (Dist(tri[0], tri[1], b.E, b.N) > near + 200) continue;   // a factory hall can be 200 m long
                list.Add(tri);
            }
        }
        return list;
    }

    /// <summary>True when the square of <see cref="Clearance"/> round the point touches no footprint (its centre, corners and edge middles).</summary>
    private static bool Clear(double e, double n, List<double[]> footprints)
    {
        const float c = Clearance;
        foreach (var tri in footprints)
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (Inside(e + dx * c, n + dy * c, tri)) return false;
        return true;
    }

    private static bool Inside(double e, double n, double[] t)
    {
        double d1 = (e - t[2]) * (t[1] - t[3]) - (t[0] - t[2]) * (n - t[3]);
        double d2 = (e - t[4]) * (t[3] - t[5]) - (t[2] - t[4]) * (n - t[5]);
        double d3 = (e - t[0]) * (t[5] - t[1]) - (t[4] - t[0]) * (n - t[1]);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
