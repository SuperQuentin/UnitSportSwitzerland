using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.TestRegion;
using Mark = UnitSport.Tools.RoadGen.Diagnostics.RecordMarks.Mark;

namespace UnitSport.Tools.Preprocessor;

public static partial class CubeTestRegion
{
    /// <summary>
    /// The bars (#1647, B3's restated bar: <c>roadgen-units</c>, #1546): every count equal, every record matched within
    /// <see cref="MatchM"/> with the same look (a signal's canton look and plan size, a pole's flags, an approach's lanes, a
    /// canton sign's variant). Positions: signals, stop lines and poles within <see cref="PlaceBarM"/> horizontally,
    /// heights within <see cref="HeightBarM"/> (the leaf mesh's 1 cm quantum and its 1.2 m lattice).
    /// </summary>
    public const double MatchM = 5, PlaceBarM = 0.5, HeightBarM = 0.1;

    /// <summary>
    /// A canton sign stands past the street's side (<c>RoadSide.Reach</c>): the LV95 build stops a street's sidewalks ~8 m
    /// short of a km line, and the borders are km lines, so its signs stand 5 m nearer the carriageway than on the leaves.
    /// </summary>
    public const double SignMatchM = 8;

    /// <summary>Every mismatch listed under its kind's line.</summary>
    public static bool Verbose { get; set; }

    /// <summary>Compares the leaves in <paramref name="region"/> with the LV95 build in <paramref name="chunks"/>; the number of failed bars.</summary>
    /// <param name="real">
    /// A real region (#1647's measure of the leaf cut): every <c>.road</c> tile of <paramref name="chunks"/>, records kept only
    /// where both builds hold the ground all round (the LV95 tile and its eight neighbours, the leaf and its neighbours), and
    /// each kind's matches split by how far the LV95 record lies from the nearest leaf edge. Reported, not barred.
    /// </param>
    public static int Compare(string chunks, string region, Action<string> log, bool real = false, (double MinE, double MinN, double MaxE, double MaxN)? box = null)
    {
        var lv = new Dictionary<string, List<Mark>>();
        var cube = new Dictionary<string, List<Mark>>();
        var tiles = real ? RoadTiles(chunks) : Tiles();
        var tileSet = tiles.ToHashSet();
        var heights = new RegionHeights(region);
        var leafSet = heights.Leaves.ToHashSet();
        bool Covered(double e, double n)
        {
            if (box is { } bx && (e < bx.MinE || e > bx.MaxE || n < bx.MinN || n > bx.MaxN)) return false;
            var id = TileId.FromLv95(e, n);
            for (int de = -1; de <= 1; de++)
                for (int dn = -1; dn <= 1; dn++)
                    if (!tileSet.Contains(new TileId(id.E + de, id.N + dn))) return false;
            var g = SwissGeodesy.ToWgs84(e, n, 500);
            var leaf = CubeKey.FromLatLon(g.LatDeg, g.LonDeg, LeafLevel);
            return leafSet.Contains(leaf) && leaf.Neighbours().All(leafSet.Contains);
        }
        foreach (var id in tiles)
        {
            string path = Path.Combine(chunks, RoadFormat.FileName(id));
            if (!File.Exists(path)) continue;
            RoadTile tile;
            using (var fs = File.OpenRead(path)) tile = RoadCodec.Decode(fs);
            UnitSport.Tools.RoadGen.Diagnostics.RecordMarks.Collect(tile, lv, (x, y, z) => (id.MinE + x, id.MaxN - z, y));
        }
        foreach (var leaf in heights.Leaves)
        {
            if (heights.ReadKind(leaf, LeafKind.Roads) is not { } sec) continue;
            var anchor = leaf.Anchor;
            UnitSport.Tools.RoadGen.Diagnostics.RecordMarks.Collect(LeafRoads.Decode(sec), cube, (x, y, z) => SwissGeodesy.FromEcef(anchor.ToEcef(x, y, z)));
        }
        if (real)
            foreach (var into in new[] { lv, cube })
                foreach (var key in into.Keys.ToList())
                    into[key] = into[key].Where(m => Covered(m.E, m.N)).ToList();

        int bad = 0;
        log("");
        log(real
            ? $"leaves against the LV95 build where both hold the ground all round ({tiles.Count} LV95 tiles, {heights.Leaves.Count} leaves); "
                + "split by the LV95 record's distance to the nearest leaf edge:"
            : $"cube test region against the LV95 build (bars: counts equal, matched within {MatchM} m with the same look, "
                + string.Create(CultureInfo.InvariantCulture, $"signals, stop lines and poles within {PlaceBarM} m, heights {HeightBarM} m):"));
        double[] bands = [30, 100, double.PositiveInfinity];
        foreach (string kind in lv.Keys.Union(cube.Keys).Order(StringComparer.Ordinal))
        {
            var a = lv.GetValueOrDefault(kind) ?? [];
            var b = cube.GetValueOrDefault(kind) ?? [];
            var used = new bool[b.Count];
            var grid = new Dictionary<(long, long), List<int>>();
            for (int k = 0; k < b.Count; k++)
            {
                var cell = ((long)Math.Floor(b[k].E / 10), (long)Math.Floor(b[k].N / 10));
                if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new();
                list.Add(k);
            }
            var dist = new List<double>();
            var dh = new List<double>();
            int unmatched = 0, otherLook = 0;
            var bandCounts = new int[bands.Length, 3];   // same, other look, alone
            var clearCounts = new int[3];   // the same, for records with no cut of either build (leaf edge, km line) within 100 m
            string? worst = null;
            var detail = new List<string>();
            bool placed = kind is "signal" or "pole" or "stop line";
            double reach = kind == "canton sign" ? SignMatchM : MatchM;
            foreach (var m in a)
            {
                int best = -1;
                double bestD = reach;
                long ce = (long)Math.Floor(m.E / 10), cn = (long)Math.Floor(m.N / 10);
                for (long ie = ce - 1; ie <= ce + 1; ie++)
                    for (long jn = cn - 1; jn <= cn + 1; jn++)
                        if (grid.TryGetValue((ie, jn), out var list))
                            foreach (int k in list)
                            {
                                if (used[k]) continue;
                                double d = Math.Sqrt((b[k].E - m.E) * (b[k].E - m.E) + (b[k].N - m.N) * (b[k].N - m.N));
                                if (d < bestD) (best, bestD) = (k, d);
                            }
                int band = real ? Array.FindIndex(bands, x => LeafEdgeM(m.E, m.N) < x) : 0;
                bool clear = real && LeafEdgeM(m.E, m.N) >= 100 && KmLineM(m.E, m.N) >= 100;
                if (best < 0)
                {
                    unmatched++;
                    bandCounts[band, 2]++;
                    if (clear) clearCounts[2]++;
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
                    bandCounts[band, 1]++;
                    if (clear) clearCounts[1]++;
                    worst ??= string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: LV95 {m.Look}, leaves {b[best].Look}");
                    detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: LV95 {m.Look}, leaves {b[best].Look}; {Cuts(m.E, m.N)}"));
                }
                else
                {
                    bandCounts[band, 0]++;
                    if (clear) clearCounts[0]++;
                    if (placed && bestD > PlaceBarM)
                        detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {m.E:F1},{m.N:F1}: {bestD:F2} m apart; {Cuts(m.E, m.N)}"));
                }
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
                $"  {(real ? "" : ok ? "ok   " : "FAIL ")}{kind}: LV95 {a.Count}, leaves {b.Count}, unmatched {unmatched}, other look {otherLook}; "
                + $"distance p50 {P(dist, 0.5) * 100:F2} cm, p95 {P(dist, 0.95) * 100:F2} cm, max {P(dist, 1) * 100:F2} cm; height max {P(dh, 1) * 100:F2} cm")
                + (worst != null && !real ? $" ({worst})" : ""));
            if (real)
                for (int k = 0; k < bands.Length; k++)
                {
                    int total = bandCounts[k, 0] + bandCounts[k, 1] + bandCounts[k, 2];
                    string from = k == 0 ? "0" : bands[k - 1].ToString(CultureInfo.InvariantCulture);
                    string to = double.IsPositiveInfinity(bands[k]) ? "" : bands[k].ToString(CultureInfo.InvariantCulture);
                    log(string.Create(CultureInfo.InvariantCulture,
                        $"      {from}-{to} m from a leaf edge: {total}, same {bandCounts[k, 0]}, other look {bandCounts[k, 1]} "
                        + $"({(total == 0 ? 0 : 100.0 * bandCounts[k, 1] / total):F1} %), alone {bandCounts[k, 2]}"));
                }
            if (real)
            {
                int total = clearCounts[0] + clearCounts[1] + clearCounts[2];
                log(string.Create(CultureInfo.InvariantCulture,
                    $"      no cut of either build within 100 m: {total}, same {clearCounts[0]}, other look {clearCounts[1]} "
                    + $"({(total == 0 ? 0 : 100.0 * clearCounts[1] / total):F1} %), alone {clearCounts[2]}"));
            }
            if (Verbose) foreach (string d in detail) log("      " + d);
        }
        if (real) return 0;
        log(bad == 0 ? "[cubetestregion] RESULT: ok" : $"[cubetestregion] RESULT: FAILED {bad} kinds");
        return bad == 0 ? 0 : 2;
    }

    /// <summary>Every LV95 tile with a <c>.road</c> file in <paramref name="chunks"/>.</summary>
    private static List<TileId> RoadTiles(string chunks)
    {
        var tiles = new List<TileId>();
        foreach (string path in Directory.EnumerateFiles(chunks, "roads_*.road"))
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('_');
            if (parts.Length == 3 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int e)
                && int.TryParse(parts[2], CultureInfo.InvariantCulture, out int n)) tiles.Add(new TileId(e, n));
        }
        return tiles;
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

    /// <summary>How far an LV95 point lies from the nearest edge of its level-14 leaf, metres (the side taken as 610 m).</summary>
    private static double LeafEdgeM(double e, double n)
    {
        var g = SwissGeodesy.ToWgs84(e, n, SignalTestRegion.Ground);
        var (_, u, v) = CubeSphere.FromLatLon(g.LatDeg, g.LonDeg);
        var key = CubeKey.FromLatLon(g.LatDeg, g.LonDeg, LeafLevel);
        double fu = (u - key.U0) / key.Size, fv = (v - key.V0) / key.Size;
        return Math.Min(Math.Min(fu, 1 - fu), Math.Min(fv, 1 - fv)) * 610;
    }

    /// <summary>How far an LV95 point lies from the nearest LV95 km line (where the LV95 build cuts), metres.</summary>
    private static double KmLineM(double e, double n) =>
        Math.Min(Math.Min(e - Math.Floor(e / 1000) * 1000, Math.Ceiling(e / 1000) * 1000 - e), Math.Min(n - Math.Floor(n / 1000) * 1000, Math.Ceiling(n / 1000) * 1000 - n));

    /// <summary>How far an LV95 point is from the nearest LV95 km line and leaf edge (where each build cuts), metres.</summary>
    private static string Cuts(double e, double n)
    {
        double km = KmLineM(e, n);
        return string.Create(CultureInfo.InvariantCulture, $"{km:F0} m from a km line, ~{LeafEdgeM(e, n):F0} m from a leaf edge");
    }
}
