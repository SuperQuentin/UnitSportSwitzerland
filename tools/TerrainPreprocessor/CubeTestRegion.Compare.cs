using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.TestRegion;

namespace UnitSport.Tools.Preprocessor;

public static partial class CubeTestRegion
{
    /// <summary>A record compared, at its LV95 position (E, N, height above sea level), with what must be equal in both builds.</summary>
    private readonly record struct Mark(double E, double N, double H, string Look);

    /// <summary>
    /// The bars (#1647, B3's restated bar: <c>roadgen-units</c>, #1546): every count equal, every record matched within
    /// <see cref="MatchM"/> with the same look (a signal's canton look and plan size, a pole's flags, an approach's lanes, a
    /// canton sign's variant). Positions: signals, stop lines and poles within <see cref="PlaceBarM"/> horizontally (the
    /// junctions are 1 km tiles apart and the planner's geometry is exact on the plane; what differs is the smoothing and the
    /// walls' raster cut where each build cuts, reported as percentiles), heights within <see cref="HeightBarM"/> (the leaf
    /// mesh's 1 cm quantum and its 1.2 m lattice under the town lowering).
    /// </summary>
    public const double MatchM = 5, PlaceBarM = 0.5, HeightBarM = 0.1;

    /// <summary>
    /// A canton sign stands past the street's side (<c>RoadSide.Reach</c>): the LV95 build stops a street's sidewalks ~8 m
    /// short of a km line, and the borders are km lines, so its signs stand 5 m nearer the carriageway than on the leaves.
    /// </summary>
    public const double SignMatchM = 8;

    /// <summary>Compares the leaves in <paramref name="region"/> with the LV95 build in <paramref name="chunks"/>; the number of failed bars.</summary>
    public static int Compare(string chunks, string region, Action<string> log)
    {
        var lv = new Dictionary<string, List<Mark>>();
        var cube = new Dictionary<string, List<Mark>>();
        foreach (var id in Tiles())
        {
            string path = Path.Combine(chunks, RoadFormat.FileName(id));
            if (!File.Exists(path)) continue;
            RoadTile tile;
            using (var fs = File.OpenRead(path)) tile = RoadCodec.Decode(fs);
            Collect(tile, lv, (x, y, z) => (id.MinE + x, id.MaxN - z, y));
        }
        var heights = new RegionHeights(region);
        foreach (var leaf in heights.Leaves)
        {
            if (heights.ReadKind(leaf, LeafKind.Roads) is not { } sec) continue;
            var anchor = leaf.Anchor;
            Collect(LeafRoads.Decode(sec), cube, (x, y, z) => SwissGeodesy.FromEcef(anchor.ToEcef(x, y, z)));
        }

        int bad = 0;
        log("");
        log($"cube test region against the LV95 build (bars: counts equal, matched within {MatchM} m with the same look, "
            + string.Create(CultureInfo.InvariantCulture, $"signals, stop lines and poles within {PlaceBarM} m, heights {HeightBarM} m):"));
        foreach (string kind in lv.Keys.Union(cube.Keys).Order(StringComparer.Ordinal))
        {
            var a = lv.GetValueOrDefault(kind) ?? [];
            var b = cube.GetValueOrDefault(kind) ?? [];
            var used = new bool[b.Count];
            var dist = new List<double>();
            var dh = new List<double>();
            int unmatched = 0, otherLook = 0;
            string? worst = null;
            var detail = new List<string>();
            bool placed = kind is "signal" or "pole" or "stop line";
            foreach (var m in a)
            {
                int best = -1;
                double bestD = kind == "canton sign" ? SignMatchM : MatchM;
                for (int k = 0; k < b.Count; k++)
                {
                    if (used[k]) continue;
                    double d = Math.Sqrt((b[k].E - m.E) * (b[k].E - m.E) + (b[k].N - m.N) * (b[k].N - m.N));
                    if (d < bestD) (best, bestD) = (k, d);
                }
                if (best < 0)
                {
                    unmatched++;
                    worst ??= string.Create(CultureInfo.InvariantCulture, $"alone at {m.E:F1},{m.N:F1}");
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"LV95 alone at {m.E:F1},{m.N:F1} ({m.Look}), {Cuts(m.E, m.N)}"));
                    continue;
                }
                used[best] = true;
                dist.Add(bestD);
                dh.Add(Math.Abs(b[best].H - m.H));
                if (b[best].Look != m.Look)
                {
                    otherLook++;
                    worst ??= string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: LV95 {m.Look}, leaves {b[best].Look}");
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: LV95 {m.Look}, leaves {b[best].Look}; {Cuts(m.E, m.N)}"));
                }
                else if (placed && bestD > PlaceBarM)
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: {bestD:F2} m apart; {Cuts(m.E, m.N)}"));
            }
            for (int k = 0; k < b.Count; k++)
                if (!used[k]) detail.Add(string.Create(CultureInfo.InvariantCulture, $"leaves alone at {b[k].E:F1},{b[k].N:F1} ({b[k].Look}), {Cuts(b[k].E, b[k].N)}"));
            dist.Sort();
            dh.Sort();
            double P(List<double> v, double q) => v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)Math.Floor(q * v.Count))];
            bool ok = a.Count == b.Count && unmatched == 0 && otherLook == 0
                && (!placed || P(dist, 1) <= PlaceBarM && P(dh, 1) <= HeightBarM);
            if (!ok) bad++;
            log(string.Create(CultureInfo.InvariantCulture,
                $"  {(ok ? "ok  " : "FAIL")} {kind}: LV95 {a.Count}, leaves {b.Count}, unmatched {unmatched}, other look {otherLook}; "
                + $"distance p50 {P(dist, 0.5) * 100:F2} cm, p95 {P(dist, 0.95) * 100:F2} cm, max {P(dist, 1) * 100:F2} cm; height max {P(dh, 1) * 100:F2} cm")
                + (worst != null ? $" ({worst})" : ""));
            if (Verbose) foreach (string d in detail) log("      " + d);
        }
        log(bad == 0 ? "[cubetestregion] RESULT: ok" : $"[cubetestregion] RESULT: FAILED {bad} kinds");
        return bad == 0 ? 0 : 2;
    }

    /// <summary>A diagnostic: every segment with a point within <paramref name="r"/> m of (e, n), in both builds, as LV95.</summary>
    public static void DumpNear(string chunks, string region, double e, double n, double r, Action<string> log)
    {
        void Seg(string build, RoadSegment s, Func<double, double, double, (double E, double N, double H)> lv95)
        {
            var pts = Enumerable.Range(0, s.PointCount).Select(i => lv95(s.Points[i * 3], s.Points[i * 3 + 1], s.Points[i * 3 + 2])).ToList();
            if (!pts.Any(p => Math.Abs(p.E - e) < r && Math.Abs(p.N - n) < r)) return;
            var a = pts[0];
            var b = pts[^1];
            log(string.Create(CultureInfo.InvariantCulture, $"  {build} {s.Class} w {s.Width:F1} {s.Flags} {a.E:F1},{a.N:F1} -> {b.E:F1},{b.N:F1} ({pts.Count} pts) {s.Attributes}"));
        }
        foreach (var id in Tiles())
        {
            string path = Path.Combine(chunks, RoadFormat.FileName(id));
            if (!File.Exists(path)) continue;
            RoadTile tile;
            using (var fs = File.OpenRead(path)) tile = RoadCodec.Decode(fs);
            foreach (var s in tile.Segments) Seg("LV95  ", s, (x, y, z) => (id.MinE + x, id.MaxN - z, y));
        }
        var heights = new RegionHeights(region);
        foreach (var leaf in heights.Leaves)
        {
            if (heights.ReadKind(leaf, LeafKind.Roads) is not { } sec) continue;
            var anchor = leaf.Anchor;
            foreach (var s in LeafRoads.Decode(sec).Segments) Seg("leaves", s, (x, y, z) => SwissGeodesy.FromEcef(anchor.ToEcef(x, y, z)));
        }
    }

    /// <summary>Every mismatch listed under its kind's line.</summary>
    public static bool Verbose { get; set; }

    /// <summary>How far an LV95 point is from the nearest LV95 km line and leaf edge (where each build cuts), metres.</summary>
    private static string Cuts(double e, double n)
    {
        double km = Math.Min(Math.Min(e - Math.Floor(e / 1000) * 1000, Math.Ceiling(e / 1000) * 1000 - e), Math.Min(n - Math.Floor(n / 1000) * 1000, Math.Ceiling(n / 1000) * 1000 - n));
        var g = SwissGeodesy.ToWgs84(e, n, SignalTestRegion.Ground);
        var (_, u, v) = CubeSphere.FromLatLon(g.LatDeg, g.LonDeg);
        var key = CubeKey.FromLatLon(g.LatDeg, g.LonDeg, LeafLevel);
        double fu = (u - key.U0) / key.Size, fv = (v - key.V0) / key.Size;
        double leaf = Math.Min(Math.Min(fu, 1 - fu), Math.Min(fv, 1 - fv)) * 610;
        return string.Create(CultureInfo.InvariantCulture, $"{km:F0} m from a km line, ~{leaf:F0} m from a leaf edge");
    }

    /// <summary>A tile's records compared, by kind, at LV95 positions through <paramref name="lv95"/> (a record's x, y, z).</summary>
    private static void Collect(RoadTile tile, Dictionary<string, List<Mark>> into, Func<double, double, double, (double E, double N, double H)> lv95)
    {
        void Add(string kind, double x, double y, double z, string look)
        {
            var (e, n, h) = lv95(x, y, z);
            if (!into.TryGetValue(kind, out var list)) into[kind] = list = new();
            list.Add(new Mark(e, n, h, look));
        }
        foreach (var j in tile.Junctions)
        {
            double x = 0, y = 0, z = 0;
            int count = j.Vertices.Length / 3;
            if (count == 0) continue;
            for (int k = 0; k < j.Vertices.Length; k += 3) { x += j.Vertices[k]; y += j.Vertices[k + 1]; z += j.Vertices[k + 2]; }
            Add("junction", x / count, y / count, z / count, j.Class.ToString());
        }
        foreach (var s in tile.Signals)
        {
            var p = s.Plan;
            Add("signal", s.X, s.Y, s.Z, $"{p.Arms.Count} arms {p.Groups.Count} groups, peds {(p.PedestrianAmber ? "3" : "2")}-lens, "
                + $"plates {p.ArrowPlates}, tram T {p.TramTHead}, poles {p.PoleColour}, {s.Overheads.Count} overheads, {s.Gantries.Count} gantries");
            for (int k = 0; k + 2 < s.Stops.Length; k += 3)
                if (!float.IsNaN(s.Stops[k])) Add("stop line", s.Stops[k], s.Stops[k + 1], s.Stops[k + 2], "");
            foreach (var pole in s.Poles) Add("pole", pole.X, pole.Y, pole.Z, $"arm {pole.Arm} {pole.Flags}");
        }
        foreach (var a in tile.Approaches)
            Add("approach", a.X, a.Y, a.Z, string.Join(" | ", a.Lanes.Select(l => $"{l.Kind}:{l.Moves}")) + (a.Signal >= 0 ? $" lit arm {a.SignalArm}" : ""));
        foreach (var pp in tile.PointProps)
            if (pp.Type == PointPropType.CantonSign) Add("canton sign", pp.X, pp.Y, pp.Z, $"variant {pp.Variant}");
        foreach (var paint in tile.Paint)
            if (paint.Rgba == SignalTestRegion.BorderRgba && paint.Vertices.Length >= 6)
            {
                var v = paint.Vertices;
                Add("border stripe", (v[0] + v[^3]) / 2, (v[1] + v[^2]) / 2, (v[2] + v[^1]) / 2, "");
            }
    }
}
