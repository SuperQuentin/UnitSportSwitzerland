namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using System.IO.Compression;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

/// <summary>
/// <c>--compare-v2 V2DIR --chunks V3DIR</c>: a v3 region against a v2 build of the same region.
/// <list type="bullet">
/// <item>geometry: every tile's segments and junction caps are the same multiset (so v3 draws what v2 drew);</item>
/// <item>one-way: the stored direction of each divided carriageway against a literal port of the
/// runtime inference (<c>LaneGraph.OrientDivided</c>, 30 m cells from a Riddes origin) run on the v2 tiles;</item>
/// <item>bytes per tile, raw and deflated as <c>ChunkStreamer</c> sends them.</item>
/// </list>
/// </summary>
public static class V2Compare
{
    public static int Run(string v2Dir, string v3Dir, Action<string> log)
    {
        var c = CultureInfo.InvariantCulture;
        var v3Files = Directory.EnumerateFiles(v3Dir, "roads_*.road").OrderBy(f => f, StringComparer.Ordinal).ToList();
        int tiles = 0, sameGeometry = 0;
        long v2Bytes = 0, v3Bytes = 0, v2Wire = 0, v3Wire = 0;
        double worstGrowth = 0; string worstTile = "";
        var v2Tiles = new List<RoadTile>();
        var stored = new Dictionary<string, sbyte>();   // geometry key -> v3 OneWay, divided car roads
        var mismatches = new List<string>();

        foreach (var v3Path in v3Files)
        {
            string v2Path = Path.Combine(v2Dir, Path.GetFileName(v3Path));
            if (!File.Exists(v2Path)) continue;
            var b2 = File.ReadAllBytes(v2Path);
            var b3 = File.ReadAllBytes(v3Path);
            var t2 = Decode(b2);
            var t3 = Decode(b3);
            tiles++;
            v2Bytes += b2.Length; v3Bytes += b3.Length;
            v2Wire += Wire(b2); v3Wire += Wire(b3);
            double growth = (double)b3.Length / Math.Max(1, b2.Length);
            if (growth > worstGrowth) { worstGrowth = growth; worstTile = Path.GetFileName(v3Path); }

            var k2 = t2.Segments.Select(Key).Order(StringComparer.Ordinal).ToList();
            var k3 = t3.Segments.Select(Key).Order(StringComparer.Ordinal).ToList();
            var j2 = t2.Junctions.Select(JKey).Order(StringComparer.Ordinal).ToList();
            var j3 = t3.Junctions.Select(JKey).Order(StringComparer.Ordinal).ToList();
            if (k2.SequenceEqual(k3) && j2.SequenceEqual(j3)) sameGeometry++;
            else if (mismatches.Count < 10)
                mismatches.Add($"{Path.GetFileName(v3Path)}: segments {k2.Count}/{k3.Count} ({k2.Except(k3).Count()} differ), junctions {j2.Count}/{j3.Count}");

            v2Tiles.Add(t2);
            foreach (var s in t3.Segments)
                if (TileRewriter.IsDividedCarRoad(s)) stored[$"{t3.Id.E}_{t3.Id.N}:{Key(s)}"] = s.Attributes.OneWay;
        }

        log(string.Create(c, $"tiles compared: {tiles}, same geometry (segments + junction caps): {sameGeometry}"));
        foreach (var m in mismatches) log("  differs: " + m);
        log(string.Create(c, $"bytes/tile  v2 {v2Bytes / 1024.0 / Math.Max(1, tiles):F1} KB  v3 {v3Bytes / 1024.0 / Math.Max(1, tiles):F1} KB  "
            + $"(+{100.0 * (v3Bytes - v2Bytes) / Math.Max(1, v2Bytes):F1}%), worst tile x{worstGrowth:F3} {worstTile}"));
        log(string.Create(c, $"on the wire (deflate) v2 {v2Wire / 1024.0 / Math.Max(1, tiles):F1} KB  v3 {v3Wire / 1024.0 / Math.Max(1, tiles):F1} KB  "
            + $"(+{100.0 * (v3Wire - v2Wire) / Math.Max(1, v2Wire):F1}%)"));

        // runtime inference on the v2 tiles, whole region at once
        var inferred = RuntimeOrient(v2Tiles, originE: 2582988, originN: 1113598);
        int total = 0, agree = 0, motorways = 0, motorwayAgree = 0, bothSet = 0, bothSetAgree = 0;
        int motorwayRuntime = 0, motorwayRuntimeAgree = 0, storedOnly = 0;
        foreach (var (key, cls, oneWay) in inferred)
        {
            if (!stored.TryGetValue(key, out sbyte s)) continue;
            total++;
            if (s == oneWay) agree++;
            if (s != 0 && oneWay != 0) { bothSet++; if (s == oneWay) bothSetAgree++; }
            if (cls == RoadClass.Motorway) { motorways++; if (s == oneWay) motorwayAgree++; }
            if (cls == RoadClass.Motorway && oneWay != 0) { motorwayRuntime++; if (s == oneWay) motorwayRuntimeAgree++; }
            if (s != 0 && oneWay == 0) storedOnly++;
        }
        log(string.Create(c, $"one-way vs runtime inference on v2: {agree}/{total} divided carriageways agree ({100.0 * agree / Math.Max(1, total):F1}%), "
            + $"motorway {motorwayAgree}/{motorways} ({100.0 * motorwayAgree / Math.Max(1, motorways):F1}%), "
            + $"where both have a direction {bothSetAgree}/{bothSet} ({100.0 * bothSetAgree / Math.Max(1, bothSet):F1}%)"));
        log(string.Create(c, $"  motorway carriageways the runtime oriented: {motorwayRuntimeAgree}/{motorwayRuntime} stored the same way "
            + $"({100.0 * motorwayRuntimeAgree / Math.Max(1, motorwayRuntime):F1}%); stored where the runtime found no partner: {storedOnly}"));
        return sameGeometry == tiles ? 0 : 1;
    }

    private static RoadTile Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return RoadCodec.Decode(ms);
    }

    private static long Wire(byte[] bytes)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) d.Write(bytes);
        return Math.Min(ms.Length, bytes.Length);
    }

    private static string Key(RoadSegment s)
    {
        var bytes = new byte[s.Points.Length * 4];
        Buffer.BlockCopy(s.Points, 0, bytes, 0, bytes.Length);
        return $"{(int)s.Class}/{(int)s.Surface}/{(int)s.Flags}/{BitConverter.SingleToInt32Bits(s.Width)}/{Convert.ToBase64String(bytes)}";
    }

    private static string JKey(RoadJunction j)
    {
        var bytes = new byte[j.Vertices.Length * 4];
        Buffer.BlockCopy(j.Vertices, 0, bytes, 0, bytes.Length);
        return $"{(int)j.Class}/{j.Layer}/{Convert.ToBase64String(bytes)}/{string.Join(',', j.Indices)}";
    }

    /// <summary>
    /// <c>LaneGraph.Build</c> + <c>OrientDivided</c>, line for line, in doubles instead of Godot
    /// floats: world X = E - origin, Z = -(N - origin), 3D arc length, 30 m cells.
    /// </summary>
    private static List<(string Key, RoadClass Class, sbyte OneWay)> RuntimeOrient(
        List<RoadTile> tiles, double originE, double originN)
    {
        var edges = new List<(string Key, RoadClass Class, double[] X, double[] Y, double[] Z, double[] Cum)>();
        foreach (var tile in tiles)
            foreach (var seg in tile.Segments)
            {
                if (seg.PointCount < 2 || !TileRewriter.IsDividedCarRoad(seg)) continue;
                int n = seg.PointCount;
                double[] x = new double[n], y = new double[n], z = new double[n], cum = new double[n];
                for (int i = 0; i < n; i++)
                {
                    x[i] = (float)(tile.Id.MinE + seg.Points[i * 3] - originE);
                    y[i] = seg.Points[i * 3 + 1];
                    z[i] = (float)-(tile.Id.MaxN - seg.Points[i * 3 + 2] - originN);
                    if (i > 0)
                        cum[i] = cum[i - 1] + Math.Sqrt((x[i] - x[i - 1]) * (x[i] - x[i - 1])
                            + (y[i] - y[i - 1]) * (y[i] - y[i - 1]) + (z[i] - z[i - 1]) * (z[i] - z[i - 1]));
                }
                if (cum[^1] < 1) continue;
                edges.Add(($"{tile.Id.E}_{tile.Id.N}:{Key(seg)}", seg.Class, x, y, z, cum));
            }

        const double Cell = 30;
        var grid = new Dictionary<(long, long), List<(int Edge, double X, double Y, double Z)>>();
        for (int e = 0; e < edges.Count; e++)
            for (int i = 0; i < edges[e].X.Length; i++)
            {
                var k = ((long)Math.Floor(edges[e].X[i] / Cell), (long)Math.Floor(edges[e].Z[i] / Cell));
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new();
                l.Add((e, edges[e].X[i], edges[e].Y[i], edges[e].Z[i]));
            }

        var result = new List<(string, RoadClass, sbyte)>();
        for (int e = 0; e < edges.Count; e++)
        {
            var (key, cls, x, y, z, cum) = edges[e];
            // Sample(Length / 2): position lerped on the span, tangent of that span
            double s = cum[^1] * 0.5;
            int lo = 0, hi = x.Length - 1;
            while (lo < hi - 1) { int mid = (lo + hi) / 2; if (cum[mid] <= s) lo = mid; else hi = mid; }
            double span = cum[hi] - cum[lo], t = span > 1e-5 ? (s - cum[lo]) / span : 0;
            double mx = x[lo] + (x[hi] - x[lo]) * t, my = y[lo] + (y[hi] - y[lo]) * t, mz = z[lo] + (z[hi] - z[lo]) * t;
            double tx = x[lo + 1] - x[lo], ty = y[lo + 1] - y[lo], tz = z[lo + 1] - z[lo];
            double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tl > 1e-4) { tx /= tl; ty /= tl; tz /= tl; } else { tx = 0; ty = 0; tz = -1; }
            double rx = -tz, rz = tx, rl = Math.Sqrt(rx * rx + rz * rz);
            if (rl > 0) { rx /= rl; rz /= rl; }

            double votes = 0;
            long cx = (long)Math.Floor(mx / Cell), cz = (long)Math.Floor(mz / Cell);
            for (long dx = -1; dx <= 1; dx++)
            for (long dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue((cx + dx, cz + dz), out var l)) continue;
                foreach (var (other, px, py, pz) in l)
                {
                    if (other == e) continue;
                    double ddx = px - mx, ddy = py - my, ddz = pz - mz;
                    double along = Math.Abs(ddx * tx + ddy * ty + ddz * tz), side = ddx * rx + ddz * rz;
                    if (along > 25 || Math.Abs(side) < 3 || Math.Abs(side) > 30) continue;
                    votes += side > 0 ? -1 : 1;
                }
            }
            result.Add((key, cls, (sbyte)(votes > 0 ? 1 : votes < 0 ? -1 : 0)));
        }
        return result;
    }
}
