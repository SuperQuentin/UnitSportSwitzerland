using Microsoft.Data.Sqlite;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Roads and buildings for cube tiles (#1443, G1): swissTLM3D (LV95) and BD TOPO (WGS84 degrees) to
/// ECEF through the rigorous geodesy, never <see cref="SwissProjection"/> (whose polynomials put
/// French features 10-276 m off their terrain), each point on the ground the tiles are made of, then
/// into the anchor frame of the tile it falls on. Road centrelines and building footprints only: the
/// full road network moves to cube tiles in G2.
/// </summary>
public sealed class CubeFeatures
{
    /// <summary>A feature point on the ground, and its tile.</summary>
    public readonly record struct Placed(CubeKey Key, EcefPos P);

    /// <summary>One line or ring before it is cut into tiles.</summary>
    public sealed record Raw(CubeSource Source, RoadClass Class, float Width, float Height, bool IsBuilding, Placed[] Points, double OldOffsetM);

    private readonly CubeHeightSource _src;
    private readonly int _level;

    public CubeFeatures(CubeHeightSource src, int level)
    {
        _src = src;
        _level = level;
    }

    /// <summary>An LV95 point on the sources' ground: swissALTI3D's height there, or the other sources' through the normal.</summary>
    public Placed? FromLv95(double e, double n, CubeHeightSource.Cursors cur)
    {
        double h = _src.SwissHeight(e, n, cur);
        EcefPos p;
        if (!double.IsNaN(h)) p = SwissGeodesy.ToEcef(e, n, h);
        else
        {
            var g = SwissGeodesy.ToWgs84(e, n, SwissGeodesy.GroundH - 50);
            if (FromLatLon(g.LatDeg, g.LonDeg, cur) is not { } at) return null;
            p = at.P;
        }
        return new Placed(CubeKey.FromEcef(p, _level), p);
    }

    /// <summary>A WGS84 latitude and longitude on the sources' ground: exactly on its normal.</summary>
    public Placed? FromLatLon(double lat, double lon, CubeHeightSource.Cursors cur)
    {
        var normal = CubeSphere.NormalAt(lat, lon);
        double h = _src.ExactHeight(normal, cur);
        if (double.IsNaN(h)) return null;
        var p = CubeSphere.OnEllipsoid(normal, h);
        return new Placed(CubeKey.FromEcef(p, _level), p);
    }

    // ---- sources ---------------------------------------------------------------------------------

    /// <summary>swissTLM3D road centrelines in an LV95 box.</summary>
    public List<Raw> SwissRoads(SqliteConnection tlm, (double MinE, double MinN, double MaxE, double MaxN) box, CubeHeightSource.Cursors cur)
    {
        var list = new List<Raw>();
        using var cmd = GeoPackageReader.BboxQuery(tlm, "tlm_strassen_strasse", new[] { "objektart", "wanderwege", "kunstbaute", "verkehrsbeschraenkung", "richtungsgetrennt" },
            box.MinE, box.MinN, box.MaxE, box.MaxN);
        var rows = new List<(RoadClass Cls, RoadFlags Flags, GeoPackageReader.Polyline Line)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                string? kind = r.IsDBNull(0) ? null : r.GetString(0);
                if (RoadFormat.IsNotDrivableSurface(kind) || r.IsDBNull(5)) continue;
                var cls = RoadFormat.ParseClass(kind);
                var flags = RoadFormat.ParseFlags(Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4));
                foreach (var line in GeoPackageReader.ParseLines((byte[])r.GetValue(5))) rows.Add((cls, flags, line));
            }
        // in kilometre order, not the R-tree's: the heights come from 16 MB tiles held in an LRU
        foreach (var (cls, flags, line) in rows.OrderBy(x => KmOrder(x.Line.E[0], x.Line.N[0])))
        {
            var pts = new List<Placed>(line.Count);
            for (int i = 0; i < line.Count; i++)
                if (FromLv95(line.E[i], line.N[i], cur) is { } p) pts.Add(p);
            if (pts.Count >= 2) list.Add(new Raw(CubeSource.SwissTlm, cls, RoadFormat.WidthFor(cls, flags), 0, false, pts.ToArray(), 0));
        }
        return list;
    }

    /// <summary>swissTLM3D building footprints in an LV95 box, their height from swissBUILDINGS3D's highest roof over each (0 when none is known).</summary>
    public List<Raw> SwissBuildings(SqliteConnection tlm, SqliteConnection? b3d, (double MinE, double MinN, double MaxE, double MaxN) box, CubeHeightSource.Cursors cur)
    {
        var roofs = new List<(double MinE, double MinN, double MaxE, double MaxN, double Top)>();
        if (b3d != null)
        {
            using var q = b3d.CreateCommand();
            q.CommandText = """
                select r.minx, r.miny, r.maxx, r.maxy, b.DACH_MAX from buildings b
                join rtree_buildings_SHAPE r on b.rowid = r.id
                where r.maxx >= $a and r.minx <= $c and r.maxy >= $b and r.miny <= $d and b.DACH_MAX is not null
                """;
            q.Parameters.AddWithValue("$a", box.MinE);
            q.Parameters.AddWithValue("$b", box.MinN);
            q.Parameters.AddWithValue("$c", box.MaxE);
            q.Parameters.AddWithValue("$d", box.MaxN);
            using var rr = q.ExecuteReader();
            while (rr.Read()) roofs.Add((rr.GetDouble(0), rr.GetDouble(1), rr.GetDouble(2), rr.GetDouble(3), rr.GetDouble(4)));
        }
        var grid = roofs.Select((b, i) => (b, i)).GroupBy(x => ((int)Math.Floor((x.b.MinE + x.b.MaxE) / 200), (int)Math.Floor((x.b.MinN + x.b.MaxN) / 200)))
            .ToDictionary(g => g.Key, g => g.Select(x => x.b).ToList());

        var list = new List<Raw>();
        using var cmd = GeoPackageReader.BboxQuery(tlm, "tlm_bauten_gebaeude_footprint", new[] { "objektart" }, box.MinE, box.MinN, box.MaxE, box.MaxN);
        var rings = new List<GeoPackageReader.Ring>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (!r.IsDBNull(1))
                    rings.AddRange(GeoPackageReader.ParsePolygons((byte[])r.GetValue(1)).Take(1)); // the outer ring
        foreach (var ring in rings.Where(g => g.Count > 0).OrderBy(g => KmOrder(g.Xyz[0], g.Xyz[1])))
        {
            var pts = new List<Placed>(ring.Count);
            double ce = 0, cn = 0, ground = double.PositiveInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                double e = ring.Xyz[3 * i], n = ring.Xyz[3 * i + 1];
                ce += e; cn += n;
                if (FromLv95(e, n, cur) is not { } p) continue;
                pts.Add(p);
                ground = Math.Min(ground, SwissGeodesy.FromEcef(p.P).H);
            }
            if (pts.Count < 3) continue;
            ce /= ring.Count; cn /= ring.Count;
            double top = double.NaN;
            int gx = (int)Math.Floor(ce / 100), gy = (int)Math.Floor(cn / 100);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (grid.TryGetValue((gx + dx, gy + dy), out var near))
                        foreach (var b in near)
                            if (ce >= b.MinE && ce <= b.MaxE && cn >= b.MinN && cn <= b.MaxN) top = double.IsNaN(top) ? b.Top : Math.Max(top, b.Top);
            float height = double.IsNaN(top) ? 0 : (float)Math.Max(0, top - ground);
            list.Add(new Raw(CubeSource.SwissTlm, RoadClass.Road, 0, height, true, pts.ToArray(), 0));
        }
        return list;
    }

    /// <summary>
    /// BD TOPO roads and buildings, placed from their own latitude and longitude. Each keeps how far
    /// <see cref="SwissProjection"/> would have put its first point from where it is (the old path's error).
    /// </summary>
    public List<Raw> French(IEnumerable<BdFeature> roads, IEnumerable<BdFeature> buildings, CubeHeightSource.Cursors cur)
    {
        var list = new List<Raw>();
        static double First(BdFeature f) => f.LatLonRings.Count > 0 && f.LatLonRings[0].Count > 0 ? Math.Floor(f.LatLonRings[0][0].Lat * 100) * 1000 + f.LatLonRings[0][0].Lon : 0;
        roads = roads.OrderBy(First);
        buildings = buildings.OrderBy(First);
        foreach (var f in roads)
        {
            if (FranceRoads.Classify(f) is not { } cls) continue;
            float width = FranceRoads.Width(f, cls, RoadFlags.None);
            foreach (var line in f.LatLonRings)
                if (Place(line, cur, out var first) is { Length: >= 2 } pts)
                    list.Add(new Raw(CubeSource.BdTopo, cls, width, 0, false, pts, OldOffset(first, pts[0])));
        }
        foreach (var f in buildings)
        {
            float height = FranceBuildings.Heights(f) is { } hh ? (float)hh.Ridge : 0;
            // the outer ring: holes are not footprints
            if (f.LatLonRings.Count > 0 && Place(f.LatLonRings[0], cur, out var first) is { Length: >= 3 } pts)
                list.Add(new Raw(CubeSource.BdTopo, RoadClass.Road, 0, height, true, pts, OldOffset(first, pts[0])));
        }
        return list;
    }

    /// <summary>The points that have ground under them; <paramref name="first"/> is the first of those as it came.</summary>
    private Placed[] Place(List<(double Lat, double Lon)> pts, CubeHeightSource.Cursors cur, out (double Lat, double Lon) first)
    {
        var placed = new List<Placed>(pts.Count);
        first = default;
        foreach (var ll in pts)
            if (FromLatLon(ll.Lat, ll.Lon, cur) is { } p)
            {
                if (placed.Count == 0) first = ll;
                placed.Add(p);
            }
        return placed.ToArray();
    }

    /// <summary>How far the LV95 path (SwissProjection, then the point taken as metres on that plane) puts a point from its rigorous place, horizontally.</summary>
    private static double OldOffset((double Lat, double Lon) ll, Placed at)
    {
        var (eOld, nOld) = SwissProjection.ToLv95(ll.Lat, ll.Lon);
        var (e, n, _) = SwissGeodesy.FromEcef(at.P);
        return Math.Sqrt((eOld - e) * (eOld - e) + (nOld - n) * (nOld - n));
    }

    // ---- into tiles ------------------------------------------------------------------------------

    /// <summary>
    /// Cuts each feature into the tiles it crosses (a road run keeps the point before and after it so
    /// neighbouring runs meet; a building goes whole to the tile of its first point) and adds those in
    /// <paramref name="tiles"/> to them, in each tile's anchor frame.
    /// </summary>
    public static void AddTo(IEnumerable<Raw> features, IReadOnlyDictionary<CubeKey, CubeTile> tiles)
    {
        var anchors = new Dictionary<CubeKey, EnuFrame>();
        EnuFrame AnchorOf(CubeKey k) => anchors.TryGetValue(k, out var a) ? a : anchors[k] = k.Anchor;

        foreach (var f in features)
        {
            if (f.IsBuilding)
            {
                if (!tiles.TryGetValue(f.Points[0].Key, out var t)) continue;
                lock (t) t.Buildings.Add(new CubeBuilding(f.Source, f.Height, Local(AnchorOf(t.Key), f.Points, 0, f.Points.Length)));
                continue;
            }
            int start = 0;
            for (int i = 1; i <= f.Points.Length; i++)
            {
                if (i < f.Points.Length && f.Points[i].Key == f.Points[start].Key) continue;
                if (tiles.TryGetValue(f.Points[start].Key, out var t))
                {
                    int a = Math.Max(0, start - 1), b = Math.Min(f.Points.Length, i + 1);
                    lock (t) t.Roads.Add(new CubeRoad(f.Source, f.Class, f.Width, Local(AnchorOf(t.Key), f.Points, a, b)));
                }
                start = i;
            }
        }
    }

    private static float[] Local(EnuFrame anchor, Placed[] pts, int from, int to)
    {
        var xyz = new float[(to - from) * 3];
        for (int i = from; i < to; i++)
        {
            var (x, y, z) = anchor.ToLocal(pts[i].P);
            xyz[(i - from) * 3] = (float)x;
            xyz[(i - from) * 3 + 1] = (float)y;
            xyz[(i - from) * 3 + 2] = (float)z;
        }
        return xyz;
    }

    /// <summary>A sort key that walks kilometre tiles row by row.</summary>
    private static long KmOrder(double e, double n) => (long)Math.Floor(n / 1000) * 100_000 + (long)Math.Floor(e / 1000);

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
