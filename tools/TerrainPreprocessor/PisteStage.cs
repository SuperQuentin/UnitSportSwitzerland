using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Ski slopes from OpenStreetMap (#887): ways and relations tagged <c>piste:type</c> downhill,
/// connection, snow_park, nordic or sled (skitour and hike are routes over unprepared snow and are
/// left out). Line slopes are widened to <see cref="DefaultWidth"/> (an OSM width tag wins), areas
/// are taken as drawn, and both are rasterised on each manifest tile's 1 m grid into
/// <c>pistes_E_N.pst</c> (<see cref="PisteFormat"/>), plus <c>pistes_report.txt</c>. Only those files
/// are written. OSM is ODbL (<c>tools/osm-odbl-licence</c>). See docs/notes/tools/ski-pistes.md.
/// </summary>
public static class PisteStage
{
    /// <summary>A slope in LV95: a centreline, or outline rings (first outer) for an area.</summary>
    public sealed record Slope(ulong Id, PisteType Type, PisteDifficulty Difficulty, PisteGrooming Grooming,
        float WidthM, string Name, string Ref, double[]? Line, List<double[]>? Rings)
    {
        public bool IsArea => Rings != null;
    }

    private static readonly HashSet<string> Keys =
        ["piste:type", "piste:difficulty", "piste:grooming", "piste:name", "piste:ref", "name", "ref", "piste:width", "width", "area", "type"];

    // ---- tags --------------------------------------------------------------------------------

    public static PisteType? TypeOf(string? value)
    {
        if (value == null) return null;
        foreach (var v in value.Split(';'))
            switch (v.Trim())
            {
                case "downhill": return PisteType.Downhill;
                case "connection": return PisteType.Connection;
                case "snow_park": return PisteType.SnowPark;
                case "nordic": return PisteType.Nordic;
                case "sled": return PisteType.Sled;
            }
        return null;
    }

    public static PisteDifficulty DifficultyOf(string? v) => v switch
    {
        "novice" => PisteDifficulty.Novice, "easy" => PisteDifficulty.Easy, "intermediate" => PisteDifficulty.Intermediate,
        "advanced" => PisteDifficulty.Advanced, "expert" => PisteDifficulty.Expert, "freeride" => PisteDifficulty.Freeride,
        "extreme" => PisteDifficulty.Extreme, _ => PisteDifficulty.Unknown,
    };

    public static PisteGrooming GroomingOf(string? v)
    {
        if (v == null) return PisteGrooming.Unknown;
        if (v.Contains("backcountry")) return PisteGrooming.Backcountry;
        if (v.Contains("mogul")) return PisteGrooming.Mogul;
        if (v.Contains("skating")) return PisteGrooming.Skating;
        return v.Contains("classic") ? PisteGrooming.Classic : PisteGrooming.Unknown;
    }

    /// <summary>The width of a line slope with no width tag, by type and difficulty (OSM tags one in 250).</summary>
    public static float DefaultWidth(PisteType type, PisteDifficulty diff) => type switch
    {
        PisteType.Connection => 8f,
        PisteType.Nordic => 6f,
        PisteType.Sled => 4f,
        PisteType.SnowPark => 40f,
        _ => diff switch
        {
            PisteDifficulty.Novice or PisteDifficulty.Easy => 40f,
            PisteDifficulty.Intermediate or PisteDifficulty.Unknown => 30f,
            PisteDifficulty.Advanced => 25f,
            _ => 20f,
        },
    };

    public static float? ParseWidth(string? v)
    {
        if (v == null) return null;
        var s = v.Replace("m", "").Trim();
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float w) && w is >= 2 and <= 200 ? w : null;
    }

    private static Slope? FromTags(ulong id, Dictionary<string, string> t, double[]? line, List<double[]>? rings)
    {
        var type = TypeOf(t.GetValueOrDefault("piste:type"));
        if (type == null) return null;
        var diff = DifficultyOf(t.GetValueOrDefault("piste:difficulty"));
        float width = ParseWidth(t.GetValueOrDefault("piste:width")) ?? ParseWidth(t.GetValueOrDefault("width")) ?? DefaultWidth(type.Value, diff);
        return new Slope(id, type.Value, diff, GroomingOf(t.GetValueOrDefault("piste:grooming")), width,
            t.GetValueOrDefault("piste:name") ?? t.GetValueOrDefault("name") ?? "",
            t.GetValueOrDefault("piste:ref") ?? t.GetValueOrDefault("ref") ?? "", line, rings);
    }

    // ---- OSM ---------------------------------------------------------------------------------

    /// <summary>
    /// Every slope whose nodes lie in the lat/lon box. Closed ways with <c>area=yes</c> and
    /// multipolygon relations are areas; other ways are lines; a <c>route=piste</c> relation lends
    /// its tags to member ways that carry no <c>piste:type</c> of their own.
    /// </summary>
    public static List<Slope> Load(string pbf, int jobs, (double Lat0, double Lat1, double Lon0, double Lon1) box)
    {
        var rels = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (_, _) => false, KeepWay = _ => false, WayTags = new HashSet<string>(),
            KeepRelation = t => TypeOf(t.GetValueOrDefault("piste:type")) != null && t.GetValueOrDefault("type") is "multipolygon" or "route",
            RelationTags = Keys,
        }).Relations;
        var memberWays = new HashSet<long>();
        foreach (var r in rels)
            foreach (var m in r.Members) if (m.Type == PbfReader.MemberType.Way) memberWays.Add(m.Ref);

        var data = PbfReader.Read(pbf, jobs, new PbfReader.Filter
        {
            KeepNode = (lat, lon) => lat >= box.Lat0 && lat <= box.Lat1 && lon >= box.Lon0 && lon <= box.Lon1,
            KeepWay = t => TypeOf(t.GetValueOrDefault("piste:type")) != null, WayTags = Keys, KeepWayId = memberWays.Contains,
        });
        var nodes = data.Nodes;
        var byId = new Dictionary<long, PbfReader.Way>(data.Ways.Count);
        foreach (var w in data.Ways) byId[w.Id] = w;

        double[]? Points(long[] ids, bool closed)
        {
            int count = closed ? ids.Length - 1 : ids.Length;
            var pts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                if (!nodes.TryGetValue(ids[i], out var ll)) return null;
                var (e, n) = SwissProjection.ToLv95(ll.Lat, ll.Lon);
                pts[i * 2] = e; pts[i * 2 + 1] = n;
            }
            return pts;
        }

        var result = new List<Slope>();
        foreach (var w in data.Ways)
        {
            if (TypeOf(w.Tags.GetValueOrDefault("piste:type")) == null || w.Refs.Length < 2) continue;
            bool closed = w.Refs[0] == w.Refs[^1] && w.Refs.Length >= 4;
            bool area = closed && w.Tags.GetValueOrDefault("area") == "yes";
            var pts = Points(w.Refs, area);
            if (pts == null) continue;
            var s = FromTags((ulong)w.Id, w.Tags, area ? null : pts, area ? [pts] : null);
            if (s != null) result.Add(s);
        }
        foreach (var rel in rels)
        {
            ulong id = (ulong)rel.Id | 1UL << 63;
            if (rel.Tags.GetValueOrDefault("type") == "route")
            {
                foreach (var m in rel.Members)
                {
                    if (m.Type != PbfReader.MemberType.Way || !byId.TryGetValue(m.Ref, out var way)) continue;
                    if (TypeOf(way.Tags.GetValueOrDefault("piste:type")) != null) continue; // the way stands on its own
                    var pts = Points(way.Refs, false);
                    if (pts == null) continue;
                    // one id per relation: the route is one slope, its member ways share its state
                    var s = FromTags(id, rel.Tags, pts, null);
                    if (s != null) result.Add(s);
                }
                continue;
            }
            var outers = new List<long[]>();
            var inners = new List<long[]>();
            foreach (var m in rel.Members)
                if (m.Type == PbfReader.MemberType.Way && byId.TryGetValue(m.Ref, out var way))
                    (m.Role == "inner" ? inners : outers).Add(way.Refs);
            var outerRings = OsmFields.JoinRings(outers).Select(r => Points(r, true)).Where(r => r != null).Select(r => r!).ToList();
            var innerRings = OsmFields.JoinRings(inners).Select(r => Points(r, true)).Where(r => r != null).Select(r => r!).ToList();
            foreach (var rings in OsmFields.Assemble(outerRings, innerRings))
            {
                var s = FromTags(id, rel.Tags, null, rings);
                if (s != null) result.Add(s);
            }
        }
        return result;
    }

    // ---- rasterising -------------------------------------------------------------------------

    /// <summary>The best slope found so far at each grid vertex of one tile.</summary>
    private sealed class TileBuf
    {
        public readonly int[] Slope = new int[PisteFormat.Size * PisteFormat.Size]; // index into the slope list + 1
        public readonly float[] Inside = new float[PisteFormat.Size * PisteFormat.Size]; // metres inside the edge
        public readonly float[] Angle = new float[PisteFormat.Size * PisteFormat.Size];
        public readonly bool[] FromLine = new bool[PisteFormat.Size * PisteFormat.Size];
    }

    private static (double MinE, double MinN, double MaxE, double MaxN) Bounds(Slope s)
    {
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        void Add(double[] pts)
        {
            for (int i = 0; i < pts.Length; i += 2)
            {
                minE = Math.Min(minE, pts[i]); maxE = Math.Max(maxE, pts[i]);
                minN = Math.Min(minN, pts[i + 1]); maxN = Math.Max(maxN, pts[i + 1]);
            }
        }
        if (s.Line != null) Add(s.Line);
        if (s.Rings != null) foreach (var r in s.Rings) Add(r);
        double pad = s.IsArea ? 0 : s.WidthM / 2;
        return (minE - pad, minN - pad, maxE + pad, maxN + pad);
    }

    /// <summary>
    /// Keeps a candidate at vertex <paramref name="i"/> when it beats what is there: deeper inside
    /// (up to the edge ramp, beyond which all are equal), then a line over an area (lines carry the
    /// run's name and difficulty), then the first one.
    /// </summary>
    private static void Offer(TileBuf b, int i, int slope, float inside, float angle, bool line)
    {
        float ramp = PisteFormat.EdgeRampM;
        float have = Math.Min(b.Inside[i], ramp), want = Math.Min(inside, ramp);
        bool better = b.Slope[i] == 0 || want > have + 1e-3f || (Math.Abs(want - have) <= 1e-3f && line && !b.FromLine[i]);
        if (b.Slope[i] != 0 && b.Slope[i] == slope + 1) better = inside > b.Inside[i];
        if (!better) return;
        b.Slope[i] = slope + 1; b.Inside[i] = inside; b.Angle[i] = angle; b.FromLine[i] = line;
    }

    private static void RasteriseLine(TileId tile, TileBuf b, int index, Slope s)
    {
        var pts = s.Line!;
        double half = s.WidthM / 2;
        int size = PisteFormat.Size;
        for (int k = 0; k + 3 < pts.Length; k += 2)
        {
            double ax = pts[k], ay = pts[k + 1], bx = pts[k + 2], by = pts[k + 3];
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            float angle = (float)Math.Atan2(dy, dx);
            int c0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - half - tile.MinE));
            int c1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(ax, bx) + half - tile.MinE));
            int r0 = Math.Max(0, (int)Math.Floor(tile.MaxN - (Math.Max(ay, by) + half)));
            int r1 = Math.Min(size - 1, (int)Math.Ceiling(tile.MaxN - (Math.Min(ay, by) - half)));
            for (int row = r0; row <= r1; row++)
            {
                double y = tile.MaxN - row;
                for (int col = c0; col <= c1; col++)
                {
                    double x = tile.MinE + col;
                    double t = len2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1) : 0;
                    double ex = x - (ax + t * dx), ey = y - (ay + t * dy);
                    double d = Math.Sqrt(ex * ex + ey * ey);
                    if (d > half) continue;
                    Offer(b, row * size + col, index, (float)(half - d), angle, true);
                }
            }
        }
    }

    private static void RasteriseArea(TileId tile, TileBuf b, int index, Slope s, float angle)
    {
        int size = PisteFormat.Size;
        var rings = s.Rings!;
        double minN = double.MaxValue, maxN = double.MinValue;
        foreach (var ring in rings)
            for (int k = 1; k < ring.Length; k += 2) { minN = Math.Min(minN, ring[k]); maxN = Math.Max(maxN, ring[k]); }
        int r0 = Math.Max(0, (int)Math.Floor(tile.MaxN - maxN)), r1 = Math.Min(size - 1, (int)Math.Ceiling(tile.MaxN - minN));
        var xs = new List<double>();
        var inside = new List<int>();
        for (int row = r0; row <= r1; row++)
        {
            double y = tile.MaxN - row;
            xs.Clear();
            foreach (var ring in rings)
            {
                int n = ring.Length / 2;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double yi = ring[i * 2 + 1], yj = ring[j * 2 + 1];
                    if ((yi > y) == (yj > y)) continue;
                    xs.Add(ring[i * 2] + (y - yi) / (yj - yi) * (ring[j * 2] - ring[i * 2]));
                }
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int c0 = Math.Max(0, (int)Math.Ceiling(xs[k] - tile.MinE)), c1 = Math.Min(size - 1, (int)Math.Floor(xs[k + 1] - tile.MinE));
                for (int col = c0; col <= c1; col++) inside.Add(row * size + col);
            }
        }
        if (inside.Count == 0) return;
        // distance to the outline, only within the edge ramp: everything deeper is "far inside"
        float ramp = PisteFormat.EdgeRampM;
        var depth = new Dictionary<int, float>(inside.Count);
        foreach (int i in inside) depth[i] = ramp * 4;
        foreach (var ring in rings)
        {
            int n = ring.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double ax = ring[j * 2], ay = ring[j * 2 + 1], bx = ring[i * 2], by = ring[i * 2 + 1];
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                int c0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - ramp - tile.MinE)), c1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(ax, bx) + ramp - tile.MinE));
                int rr0 = Math.Max(0, (int)Math.Floor(tile.MaxN - (Math.Max(ay, by) + ramp))), rr1 = Math.Min(size - 1, (int)Math.Ceiling(tile.MaxN - (Math.Min(ay, by) - ramp)));
                for (int row = rr0; row <= rr1; row++)
                    for (int col = c0; col <= c1; col++)
                    {
                        int idx = row * size + col;
                        if (!depth.TryGetValue(idx, out float have)) continue;
                        double x = tile.MinE + col, y = tile.MaxN - row;
                        double t = len2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1) : 0;
                        double ex = x - (ax + t * dx), ey = y - (ay + t * dy);
                        float d = (float)Math.Sqrt(ex * ex + ey * ey);
                        if (d < have) depth[idx] = d;
                    }
            }
        }
        foreach (var (i, d) in depth) Offer(b, i, index, d, angle, false);
    }

    /// <summary>An area's groom direction: its long axis (principal axis of the outline's points).</summary>
    public static float AxisAngle(double[] ring)
    {
        int n = ring.Length / 2;
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += ring[i * 2]; my += ring[i * 2 + 1]; }
        mx /= n; my /= n;
        double sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            double x = ring[i * 2] - mx, y = ring[i * 2 + 1] - my;
            sxx += x * x; syy += y * y; sxy += x * y;
        }
        return (float)(0.5 * Math.Atan2(2 * sxy, sxx - syy));
    }

    /// <summary>
    /// Rasterises every slope touching <paramref name="tile"/>; null when none covers a vertex of it.
    /// With the tile's heights, each record gets its altitude range, sun exposure and steepness.
    /// </summary>
    public static PisteTile? BuildTile(TileId tile, IReadOnlyList<Slope> slopes, IReadOnlyList<int> touching, out int dropped,
        ChunkGrid? heights = null)
    {
        dropped = 0;
        var b = new TileBuf();
        foreach (int s in touching)
        {
            var slope = slopes[s];
            if (slope.IsArea) RasteriseArea(tile, b, s, slope, AxisAngle(slope.Rings![0]));
            else RasteriseLine(tile, b, s, slope);
        }
        // the slopes that won at least one vertex, in a stable order (by id), at most 255
        var used = new Dictionary<int, int>();
        for (int i = 0; i < b.Slope.Length; i++)
            if (b.Slope[i] != 0) used[b.Slope[i] - 1] = used.GetValueOrDefault(b.Slope[i] - 1) + 1;
        if (used.Count == 0) return null;
        var order = used.Keys.OrderByDescending(k => used[k]).Take(PisteFormat.MaxRecords).OrderBy(k => slopes[k].Id).ThenBy(k => k).ToList();
        dropped = used.Count - order.Count;
        var terrain = heights != null && heights.Stride == 1 ? Terrain(b, heights) : null;
        var local = new Dictionary<int, byte>();
        var records = new List<PisteRecord>(order.Count);
        foreach (int k in order)
        {
            var s = slopes[k];
            var path = s.IsArea ? s.Rings![0] : s.Line!;
            var rel = new float[path.Length];
            for (int i = 0; i < path.Length; i += 2) { rel[i] = (float)(path[i] - tile.MinE); rel[i + 1] = (float)(path[i + 1] - tile.MinN); }
            local[k] = (byte)(records.Count + 1);
            var t = terrain != null && terrain.TryGetValue(k, out var tv) ? tv : (2000f, 2000f, 0.5f, 15f);
            records.Add(new PisteRecord(s.Id, s.Type, s.Difficulty, s.Grooming, s.IsArea, s.WidthM, s.Name, s.Ref, rel,
                t.Item1, t.Item2, t.Item3, t.Item4));
        }
        var slot = new byte[b.Slope.Length];
        var aux = new ushort[b.Slope.Length];
        for (int i = 0; i < slot.Length; i++)
        {
            if (b.Slope[i] == 0 || !local.TryGetValue(b.Slope[i] - 1, out byte l)) continue;
            slot[i] = l;
            aux[i] = PisteFormat.PackAux(Math.Min(1f, b.Inside[i] / PisteFormat.EdgeRampM), b.Angle[i]);
        }
        return new PisteTile(tile, records, slot, aux);
    }

    /// <summary>Per slope: altitude 10th and 90th percentiles, sun exposure and mean steepness, from every 4th vertex it owns.</summary>
    private static Dictionary<int, (float, float, float, float)> Terrain(TileBuf b, ChunkGrid g)
    {
        int size = PisteFormat.Size;
        var alts = new Dictionary<int, List<float>>();
        var sums = new Dictionary<int, (double Sun, double Steep, int N)>();
        for (int row = 1; row < size - 1; row += 4)
            for (int col = 1; col < size - 1; col += 4)
            {
                int s = b.Slope[row * size + col] - 1;
                if (s < 0) continue;
                if (!alts.TryGetValue(s, out var list)) alts[s] = list = new List<float>();
                list.Add((float)g.HeightMetersAt(col, row));
                // gradient: east is +col, north is -row; 1 m spacing
                double ge = (g.HeightMetersAt(col + 1, row) - g.HeightMetersAt(col - 1, row)) / 2;
                double gn = (g.HeightMetersAt(col, row - 1) - g.HeightMetersAt(col, row + 1)) / 2;
                double norm = Math.Sqrt(1 + ge * ge + gn * gn);
                // higher to the north = the ground faces south, toward the sun
                var have = sums.GetValueOrDefault(s);
                sums[s] = (have.Sun + gn / norm, have.Steep + Math.Atan(Math.Sqrt(ge * ge + gn * gn)) * 180 / Math.PI, have.N + 1);
            }
        var result = new Dictionary<int, (float, float, float, float)>();
        foreach (var (s, list) in alts)
        {
            list.Sort();
            var (sun, steep, n) = sums[s];
            result[s] = (list[(int)(list.Count * 0.1)], list[Math.Min(list.Count - 1, (int)(list.Count * 0.9))],
                (float)Math.Clamp(0.5 + sun / n, 0, 1), (float)(steep / n));
        }
        return result;
    }

    // ---- the stage ---------------------------------------------------------------------------

    public static int Run(string osmPbf, string outDir, IReadOnlySet<TileId> region, int jobs)
    {
        var clock = Stopwatch.StartNew();
        if (region.Count == 0) { Console.Error.WriteLine("pistes: no manifest tiles (--tiles-file or <out>/manifest.json)"); return 2; }
        // a lat/lon box round the region, 3 km wider, so slopes crossing its edge come whole
        double minE = region.Min(t => t.MinE) - 3000, maxE = region.Max(t => t.MinE) + 4000;
        double minN = region.Min(t => t.MinN) - 3000, maxN = region.Max(t => t.MaxN) + 3000;
        var (la0, lo0) = SwissProjection.ToWgs84(minE, minN);
        var (la1, lo1) = SwissProjection.ToWgs84(maxE, maxN);
        var (la2, lo2) = SwissProjection.ToWgs84(minE, maxN);
        var (la3, lo3) = SwissProjection.ToWgs84(maxE, minN);
        var box = (Math.Min(Math.Min(la0, la1), Math.Min(la2, la3)), Math.Max(Math.Max(la0, la1), Math.Max(la2, la3)),
                   Math.Min(Math.Min(lo0, lo1), Math.Min(lo2, lo3)), Math.Max(Math.Max(lo0, lo1), Math.Max(lo2, lo3)));
        var slopes = Load(osmPbf, jobs, box);
        Console.WriteLine($"Pistes: {slopes.Count} slopes read from {Path.GetFileName(osmPbf)} in {clock.Elapsed.TotalSeconds:F1}s");

        var byTile = new Dictionary<TileId, List<int>>();
        for (int s = 0; s < slopes.Count; s++)
        {
            var (e0, n0, e1, n1) = Bounds(slopes[s]);
            for (int te = (int)Math.Floor(e0 / 1000); te <= (int)Math.Floor(e1 / 1000); te++)
                for (int tn = (int)Math.Floor(n0 / 1000); tn <= (int)Math.Floor(n1 / 1000); tn++)
                {
                    var id = new TileId(te, tn);
                    if (!region.Contains(id)) continue;
                    if (!byTile.TryGetValue(id, out var list)) byTile[id] = list = new List<int>();
                    list.Add(s);
                }
        }

        Directory.CreateDirectory(outDir);
        var written = new HashSet<TileId>();
        long bytes = 0, vertices = 0;
        int dropped = 0;
        var areaByDiff = new Dictionary<PisteDifficulty, long>();
        Parallel.ForEach(byTile, new ParallelOptions { MaxDegreeOfParallelism = jobs }, kv =>
        {
            ChunkGrid? heights = null;
            string terr = Path.Combine(outDir, ChunkFormat.ChunkFileName(kv.Key));
            if (File.Exists(terr)) { using var fs = File.OpenRead(terr); heights = ChunkCodec.Decode(fs); }
            var tile = BuildTile(kv.Key, slopes, kv.Value, out int drop, heights);
            if (tile == null) return;
            using var ms = new MemoryStream();
            PisteFormat.Encode(tile, ms);
            File.WriteAllBytes(Path.Combine(outDir, PisteFormat.FileName(kv.Key)), ms.ToArray());
            var count = new long[8];
            foreach (byte s in tile.Slot) if (s != 0) count[(int)tile.Records[s - 1].Difficulty]++;
            lock (written)
            {
                written.Add(kv.Key);
                bytes += ms.Length;
                dropped += drop;
                for (int d = 0; d < 8; d++) { vertices += count[d]; areaByDiff[(PisteDifficulty)d] = areaByDiff.GetValueOrDefault((PisteDifficulty)d) + count[d]; }
            }
        });
        int stale = 0;
        foreach (var tile in region)
        {
            if (written.Contains(tile)) continue;
            string path = Path.Combine(outDir, PisteFormat.FileName(tile));
            if (File.Exists(path)) { File.Delete(path); stale++; }
        }

        var report = new StringBuilder();
        report.AppendLine($"# pistes report: {region.Count} manifest tiles, osm={osmPbf}");
        report.AppendLine($"slopes read (lat/lon box round the region): {slopes.Count}");
        foreach (var g in slopes.GroupBy(s => s.Type).OrderBy(g => g.Key))
            report.AppendLine($"  {g.Key,-12} {g.Count(),6} ({g.Count(s => s.IsArea)} areas)");
        report.AppendLine("slope ground in the region by difficulty (ha):");
        foreach (var (d, v) in areaByDiff.Where(p => p.Value > 0).OrderBy(p => p.Key))
            report.AppendLine($"  {d,-12} {v / 10000.0,8:F1}");
        if (dropped > 0) report.AppendLine($"WARNING: {dropped} slopes dropped over the {PisteFormat.MaxRecords} per tile limit");
        string summary = $"{written.Count} of {region.Count} tiles with slopes, {vertices / 10000.0:F0} ha, {bytes / 1048576.0:F1} MB, {stale} stale files removed, {clock.Elapsed.TotalSeconds:F0}s";
        report.AppendLine(summary);
        File.WriteAllText(Path.Combine(outDir, "pistes_report.txt"), report.ToString());
        Console.WriteLine("Pistes: " + summary);
        return 0;
    }

    // ---- self-check --------------------------------------------------------------------------

    /// <summary>--pistes-check: tags, widths, a line and an area rasterised, overlaps, codec round trip; 1 on failure.</summary>
    public static int SelfCheck()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }

        Check(TypeOf("downhill") == PisteType.Downhill && TypeOf("hike;nordic") == PisteType.Nordic && TypeOf("skitour") == null && TypeOf(null) == null, "tags: piste:type");
        Check(DifficultyOf("advanced") == PisteDifficulty.Advanced && DifficultyOf("x") == PisteDifficulty.Unknown, "tags: difficulty");
        Check(GroomingOf("classic;skating") == PisteGrooming.Skating && GroomingOf("mogul") == PisteGrooming.Mogul && GroomingOf("classic") == PisteGrooming.Classic, "tags: grooming");
        Check(ParseWidth("35") == 35 && ParseWidth("12 m") == 12 && ParseWidth("wide") == null && ParseWidth("0.5") == null, "tags: width");
        Check(DefaultWidth(PisteType.Downhill, PisteDifficulty.Easy) == 40 && DefaultWidth(PisteType.Downhill, PisteDifficulty.Advanced) == 25 && DefaultWidth(PisteType.Sled, PisteDifficulty.Easy) == 4, "widths");

        var tile = new TileId(2580, 1100);
        double e0 = tile.MinE, n0 = tile.MinN;
        // a 30 m red line running east along N 500 from E 100 to E 900, a black one crossing it north-south at E 500,
        // and a blue area square E 600..800, N 100..300
        var slopes = new List<Slope>
        {
            new(1, PisteType.Downhill, PisteDifficulty.Intermediate, PisteGrooming.Classic, 30, "Mont-Fort", "12", [e0 + 100, n0 + 500, e0 + 900, n0 + 500], null),
            new(2, PisteType.Downhill, PisteDifficulty.Advanced, PisteGrooming.Mogul, 20, "", "", [e0 + 500, n0 + 900, e0 + 500, n0 + 100], null),
            new(3UL | 1UL << 63, PisteType.Downhill, PisteDifficulty.Easy, PisteGrooming.Unknown, 40, "", "", null,
                [[e0 + 600, n0 + 100, e0 + 800, n0 + 100, e0 + 800, n0 + 300, e0 + 600, n0 + 300]]),
        };
        var built = BuildTile(tile, slopes, [0, 1, 2], out int dropped);
        Check(built != null && built.Records.Count == 3 && dropped == 0, "raster: three slopes kept");
        if (built != null)
        {
            var red = built.At(e0 + 300, n0 + 505);
            Check(red?.Id == 1 && red.Name == "Mont-Fort", "raster: inside the red line");
            Check(built.At(e0 + 300, n0 + 520) == null, "raster: outside the red line (20 m off a 30 m slope)");
            Check(built.At(e0 + 50, n0 + 500) == null, "raster: past the line's end");
            int mid = built.IndexAt(e0 + 300, n0 + 500), edge = built.IndexAt(e0 + 300, n0 + 514);
            Check(PisteFormat.MaskOf(built.Aux[mid]) > 0.99f && PisteFormat.MaskOf(built.Aux[edge]) is > 0.2f and < 0.5f, "raster: edge mask ramps");
            Check(Math.Abs(PisteFormat.AngleOf(built.Aux[mid])) < 0.02f || Math.Abs(PisteFormat.AngleOf(built.Aux[mid]) - MathF.PI) < 0.02f, "raster: east-west groom direction");
            int black = built.IndexAt(e0 + 500, n0 + 300);
            Check(Math.Abs(PisteFormat.AngleOf(built.Aux[black]) - MathF.PI / 2) < 0.02f, "raster: north-south groom direction");
            Check(built.At(e0 + 500, n0 + 300)?.Id == 2, "raster: the black line");
            Check(built.At(e0 + 700, n0 + 200)?.Id == (3UL | 1UL << 63), "raster: inside the area");
            Check(built.At(e0 + 700, n0 + 310) == null, "raster: outside the area");
            int areaEdge = built.IndexAt(e0 + 700, n0 + 101);
            Check(PisteFormat.MaskOf(built.Aux[areaEdge]) is > 0.2f and < 0.5f, "raster: area edge mask");
            Check(built.Records[0].Groomed && !built.Records[1].Groomed && built.Records[2].Groomed, "records: groomed");

            using var ms = new MemoryStream();
            PisteFormat.Encode(built, ms);
            ms.Position = 0;
            var back = PisteFormat.Decode(ms);
            Check(back.Id == tile && back.Records.Count == 3 && back.Records[0].Name == "Mont-Fort" && back.Records[0].Ref == "12"
                  && back.Records[2].IsArea && back.Records[2].Path.Length == 8 && back.Slot.AsSpan().SequenceEqual(built.Slot)
                  && back.Aux.AsSpan().SequenceEqual(built.Aux), "codec: round trip");
            Check(ms.Length < 100_000, $"codec: {ms.Length} bytes for three slopes");
        }
        Check(BuildTile(new TileId(2590, 1100), slopes, [0], out _) == null, "raster: a tile the slope does not reach");
        // a face falling 1 m per metre southward (45 degrees, facing south): 2500 m at the north edge
        var hs = new ushort[PisteFormat.Size * PisteFormat.Size];
        for (int row = 0; row < PisteFormat.Size; row++)
            for (int col = 0; col < PisteFormat.Size; col++)
                hs[row * PisteFormat.Size + col] = (ushort)Math.Round((2500 - row) / ChunkFormat.HeightScale);
        var face = BuildTile(tile, slopes, [0], out _, new ChunkGrid(tile, hs, 1500, 2500));
        var fr = face?.Records[0];
        Check(fr != null && fr.AltLow < fr.AltHigh && Math.Abs(fr.AltLow - 2000) < 20 && Math.Abs(fr.AltHigh - 2000) < 20 && fr.Sun > 0.95f && Math.Abs(fr.SteepDeg - 45) < 1,
            $"terrain: alt {fr?.AltLow}-{fr?.AltHigh}, sun {fr?.Sun}, steep {fr?.SteepDeg}");
        Check(Math.Abs(AxisAngle([0, 0, 100, 0, 100, 10, 0, 10])) < 0.01f, "area axis");

        foreach (var f in fails) Console.Error.WriteLine("FAIL " + f);
        Console.WriteLine(fails.Count == 0 ? "pistes-check: OK" : $"pistes-check: {fails.Count} failures");
        return fails.Count == 0 ? 0 : 1;
    }
}
