namespace UnitSport.Terrain.Format;

/// <summary>
/// A building's footprint in plan, for the house layout editor (#1186): the outline rings of its base
/// and the minimum-area rectangle around them, whose long side is the frame a layout is drawn in.
///
/// <para>
/// Frame: x along the long axis, z across it, metres, origin at the rectangle's centre. At yaw 0 the
/// x axis points east and z south, the tiles' own convention; <see cref="ToLv95"/> and
/// <see cref="ToLocal"/> turn by <see cref="YawDeg"/> (counter-clockwise from east).
/// </para>
/// </summary>
public sealed class BuildingOutline
{
    public required List<(double E, double N)[]> Rings { get; init; }
    public double CenterE { get; init; }
    public double CenterN { get; init; }
    public double YawDeg { get; init; }
    /// <summary>The rectangle's side along x.</summary>
    public double Length { get; init; }
    /// <summary>The rectangle's side along z.</summary>
    public double Width { get; init; }

    public (double E, double N) ToLv95(double x, double z) => ToLv95(CenterE, CenterN, YawDeg, x, z);

    public (double X, double Z) ToLocal(double e, double n) => ToLocal(CenterE, CenterN, YawDeg, e, n);

    public static (double E, double N) ToLv95(double ce, double cn, double yawDeg, double x, double z)
    {
        double a = yawDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return (ce + x * c + z * s, cn + x * s - z * c);
    }

    public static (double X, double Z) ToLocal(double ce, double cn, double yawDeg, double e, double n)
    {
        double a = yawDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double de = e - ce, dn = n - cn;
        return (de * c + dn * s, de * s - dn * c);
    }

    /// <summary>The rings in the local frame.</summary>
    public List<(double X, double Z)[]> LocalRings() =>
        Rings.Select(r => r.Select(p => ToLocal(p.E, p.N)).ToArray()).ToList();

    /// <summary>
    /// The base of <paramref name="b"/>: the boundary of its floor triangles (the faces lying flat at
    /// its lowest height), chained into rings. A solid with no clean floor falls back to the convex
    /// hull of every vertex, which is still a fair outline for a house.
    /// </summary>
    public static BuildingOutline Compute(Building b, TileId tile)
    {
        var t = b.Triangles;
        var edges = new Dictionary<(long, long, long, long), int>();
        var pts = new List<(double X, double Z)>(t.Length / 3);
        for (int i = 0; i + 8 < t.Length; i += 9)
        {
            for (int k = 0; k < 9; k += 3) pts.Add((t[i + k], t[i + k + 2]));
            float ax = t[i], az = t[i + 2], bx = t[i + 3], bz = t[i + 5], cx = t[i + 6], cz = t[i + 8];
            float area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            if (MathF.Abs(area) < 0.01f) continue; // a wall, edge-on from above
            float top = MathF.Max(t[i + 1], MathF.Max(t[i + 4], t[i + 7]));
            if (top > b.MinY + 0.3f) continue;     // a roof, not the floor
            AddEdge(edges, ax, az, bx, bz);
            AddEdge(edges, bx, bz, cx, cz);
            AddEdge(edges, cx, cz, ax, az);
        }

        var local = Chain(edges.Where(kv => kv.Value == 1).Select(kv => kv.Key));
        if (local.Count == 0 || local.Sum(r => Math.Abs(Area(r))) < 1) local = [Hull(pts).ToArray()];

        var all = local.SelectMany(r => r).ToList();
        var (ce, cn, yaw, len, wid) = MinRect(Hull(all), tile);
        var rings = local.Select(r => r.Select(p => (tile.MinE + p.X, tile.MaxN - p.Z)).ToArray()).ToList();
        return new BuildingOutline { Rings = rings, CenterE = ce, CenterN = cn, YawDeg = yaw, Length = len, Width = wid };
    }

    private static long Q(float v) => (long)MathF.Round(v * 100); // centimetres, so shared vertices meet

    private static void AddEdge(Dictionary<(long, long, long, long), int> edges, float ax, float az, float bx, float bz)
    {
        var a = (Q(ax), Q(az));
        var c = (Q(bx), Q(bz));
        if (a == c) return;
        var key = a.CompareTo(c) < 0 ? (a.Item1, a.Item2, c.Item1, c.Item2) : (c.Item1, c.Item2, a.Item1, a.Item2);
        edges[key] = edges.GetValueOrDefault(key) + 1;
    }

    /// <summary>Boundary edges (each used by one triangle) joined end to end into closed rings.</summary>
    private static List<(double X, double Z)[]> Chain(IEnumerable<(long, long, long, long)> boundary)
    {
        var next = new Dictionary<(long, long), List<(long, long)>>();
        foreach (var (ax, az, bx, bz) in boundary)
        {
            (next.TryGetValue((ax, az), out var la) ? la : next[(ax, az)] = new()).Add((bx, bz));
            (next.TryGetValue((bx, bz), out var lb) ? lb : next[(bx, bz)] = new()).Add((ax, az));
        }
        var rings = new List<(double X, double Z)[]>();
        while (next.Count > 0)
        {
            var start = next.Keys.First();
            var ring = new List<(long, long)> { start };
            var prev = start;
            var cur = Take(next, start);
            while (cur is { } c && c != start)
            {
                ring.Add(c);
                Drop(next, c, prev);
                prev = c;
                cur = Take(next, c);
            }
            if (cur == start) Drop(next, start, prev);
            if (ring.Count >= 3 && cur == start) rings.Add(ring.Select(p => (p.Item1 / 100.0, p.Item2 / 100.0)).ToArray());
        }
        return rings;
    }

    private static (long, long)? Take(Dictionary<(long, long), List<(long, long)>> next, (long, long) at)
    {
        if (!next.TryGetValue(at, out var l) || l.Count == 0) { next.Remove(at); return null; }
        var n = l[^1];
        l.RemoveAt(l.Count - 1);
        if (l.Count == 0) next.Remove(at);
        return n;
    }

    private static void Drop(Dictionary<(long, long), List<(long, long)>> next, (long, long) at, (long, long) other)
    {
        if (!next.TryGetValue(at, out var l)) return;
        l.Remove(other);
        if (l.Count == 0) next.Remove(at);
    }

    public static double Area(IReadOnlyList<(double X, double Z)> r)
    {
        double a = 0;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) a += r[j].X * r[i].Z - r[i].X * r[j].Z;
        return a / 2;
    }

    /// <summary>Andrew's monotone chain.</summary>
    public static List<(double X, double Z)> Hull(List<(double X, double Z)> p)
    {
        var s = p.Distinct().OrderBy(q => q.X).ThenBy(q => q.Z).ToList();
        if (s.Count < 3) return s;
        static double Cross((double X, double Z) o, (double X, double Z) a, (double X, double Z) b) =>
            (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
        var h = new List<(double X, double Z)>();
        for (int pass = 0; pass < 2; pass++)
        {
            int floor = h.Count;
            foreach (var q in s)
            {
                while (h.Count >= floor + 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(q);
            }
            h.RemoveAt(h.Count - 1);
            s.Reverse();
        }
        return h;
    }

    /// <summary>
    /// The minimum-area rectangle around a convex hull (one of its sides lies on a hull edge), in LV95:
    /// centre, yaw of its long side (degrees counter-clockwise from east, in [0, 180)), length, width.
    /// </summary>
    private static (double E, double N, double Yaw, double Len, double Wid) MinRect(List<(double X, double Z)> hull, TileId tile)
    {
        // to LV95-oriented plan coordinates (E right, N up), so the yaw reads the usual way
        var h = hull.Select(p => (X: tile.MinE + p.X, Y: tile.MaxN - p.Z)).ToList();
        if (h.Count == 0) return (tile.MinE, tile.MinN, 0, 0, 0);
        double best = double.MaxValue, bE = h[0].X, bN = h[0].Y, bYaw = 0, bLen = 0, bWid = 0;
        for (int i = 0; i < h.Count; i++)
        {
            var a = h[i];
            var c = h[(i + 1) % h.Count];
            double ang = Math.Atan2(c.Y - a.Y, c.X - a.X);
            if (h.Count < 2 || (c.X == a.X && c.Y == a.Y)) ang = 0;
            double co = Math.Cos(ang), si = Math.Sin(ang);
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (var p in h)
            {
                double u = p.X * co + p.Y * si, v = -p.X * si + p.Y * co;
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            double area = (u1 - u0) * (v1 - v0);
            if (area >= best - 1e-6) continue;
            best = area;
            double um = (u0 + u1) / 2, vm = (v0 + v1) / 2;
            bE = um * co - vm * si;
            bN = um * si + vm * co;
            if (u1 - u0 >= v1 - v0) { bYaw = ang; bLen = u1 - u0; bWid = v1 - v0; }
            else { bYaw = ang + Math.PI / 2; bLen = v1 - v0; bWid = u1 - u0; }
        }
        double deg = bYaw * 180 / Math.PI % 180;
        if (deg < 0) deg += 180;
        return (bE, bN, Math.Round(deg, 2), bLen, bWid);
    }
}
