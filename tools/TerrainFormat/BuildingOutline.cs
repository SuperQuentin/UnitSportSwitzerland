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
    /// The base of <paramref name="b"/>: the boundary of its floor triangles, chained into rings. The
    /// floor is every face at its lowest height, plus, for a building stepped down a slope, every face
    /// that bottoms a higher part (<see cref="RaisedFloor"/>). A solid with no clean floor falls back to
    /// the convex hull of every vertex, which is still a fair outline for a house.
    /// </summary>
    public static BuildingOutline Compute(Building b, TileId tile)
    {
        var t = b.Triangles;
        var edges = new Dictionary<(long, long, long, long), int>();
        var floor = new List<(double X, double Z)[]>();
        bool raised = false;
        double lowArea = 0, allArea = 0;
        var pts = new List<(double X, double Z)>(t.Length / 3);
        for (int i = 0; i + 8 < t.Length; i += 9)
        {
            for (int k = 0; k < 9; k += 3) pts.Add((t[i + k], t[i + k + 2]));
            float ax = t[i], az = t[i + 2], bx = t[i + 3], bz = t[i + 5], cx = t[i + 6], cz = t[i + 8];
            float area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            if (MathF.Abs(area) < 0.01f) continue; // a wall, edge-on from above
            bool lowest = MathF.Max(t[i + 1], MathF.Max(t[i + 4], t[i + 7])) <= b.MinY + 0.3f;
            if (!lowest && !(area < 0 && RaisedFloor(t, i))) continue; // a roof or a soffit, not a floor
            floor.Add([(ax, az), (bx, bz), (cx, cz)]);
            allArea += MathF.Abs(area) / 2;
            if (!lowest) { raised = true; continue; }
            lowArea += MathF.Abs(area) / 2;
            AddEdge(edges, ax, az, bx, bz);
            AddEdge(edges, bx, bz, cx, cz);
            AddEdge(edges, cx, cz, ax, az);
        }

        var local = Chain(edges.Where(kv => kv.Value == 1).Select(kv => kv.Key));
        if (raised)
        {
            // a building stepped down a slope has floors at several heights, whose triangles need not share
            // corners and may overlap a little in plan: their union. Its area must lie between the lowest
            // floor's and all the floors' together, else the lowest floor stays the outline, as before.
            var union = UnionRings(floor);
            double net = Math.Abs(union.Sum(r => Area(r)));
            if (net >= lowArea * 0.99 && net <= allArea * 1.01) local = union;
        }
        if (local.Count == 0 || local.Sum(r => Math.Abs(Area(r))) < 1) local = [Hull(pts).ToArray()];

        var all = local.SelectMany(r => r).ToList();
        var (ce, cn, yaw, len, wid) = MinRect(Hull(all), tile);
        var rings = local.Select(r => r.Select(p => (tile.MinE + p.X, tile.MaxN - p.Z)).ToArray()).ToList();
        return new BuildingOutline { Rings = rings, CenterE = ce, CenterN = cn, YawDeg = yaw, Length = len, Width = wid };
    }

    /// <summary>
    /// Whether triangle <paramref name="i"/>, already known to face down (its plan winding is negative in
    /// tile-local X east, Z south: the tiles' solids turn their faces outward), is the floor of a part
    /// standing higher than the rest: nothing of the solid lies under its centre, and at least 2 m of it
    /// lies over. A soffit or an eave under a roof is thinner, and a part cantilevered over a lower one
    /// has that one's floor beneath it already.
    /// </summary>
    private static bool RaisedFloor(float[] t, int i)
    {
        float gx = (t[i] + t[i + 3] + t[i + 6]) / 3, gy = (t[i + 1] + t[i + 4] + t[i + 7]) / 3, gz = (t[i + 2] + t[i + 5] + t[i + 8]) / 3;
        float over = float.MaxValue;
        for (int j = 0; j + 8 < t.Length; j += 9)
        {
            if (j == i || HeightAt(t, j, gx, gz) is not { } h) continue;
            if (h < gy - 0.05f) return false;
            if (h > gy + 0.05f) over = MathF.Min(over, h - gy);
        }
        return over >= 2 && over < float.MaxValue;
    }

    /// <summary>The height of triangle <paramref name="j"/> over the plan point, or null when it does not cover it.</summary>
    private static float? HeightAt(float[] t, int j, float x, float z)
    {
        float ax = t[j], az = t[j + 2], bx = t[j + 3], bz = t[j + 5], cx = t[j + 6], cz = t[j + 8];
        float d = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
        if (MathF.Abs(d) < 0.01f) return null; // a wall, edge-on from above
        float l1 = ((bx - x) * (cz - z) - (bz - z) * (cx - x)) / d, l2 = ((cx - x) * (az - z) - (cz - z) * (ax - x)) / d, l3 = 1 - l1 - l2;
        if (l1 < 0 || l2 < 0 || l3 < 0) return null;
        return l1 * t[j + 1] + l2 * t[j + 4] + l3 * t[j + 7];
    }

    private static long Q(float v) => (long)MathF.Round(v * 100); // centimetres, so shared vertices meet

    /// <summary>
    /// The rings of the union of plan triangles. Every triangle edge is cut where another edge meets it, and a
    /// piece is kept, turned with the floor on its left, when the floor lies on one side of it only. The pieces
    /// are joined end to the nearest start: floors at two heights rarely share corners exactly, so the ends
    /// may miss each other by a few centimetres.
    /// </summary>
    private static List<(double X, double Z)[]> UnionRings(List<(double X, double Z)[]> tris)
    {
        // corners within 5 cm of one met before are that corner, and the slivers this flattens are dropped
        var corners = new List<(double X, double Z)>();
        (double X, double Z) Weld((double X, double Z) p)
        {
            foreach (var c in corners)
                if (Dist2(c, p) < 0.0025) return c;
            corners.Add(p);
            return p;
        }
        tris = tris.Select(p => new[] { Weld(p[0]), Weld(p[1]), Weld(p[2]) })
            .Where(p => Math.Abs((p[1].X - p[0].X) * (p[2].Z - p[0].Z) - (p[1].Z - p[0].Z) * (p[2].X - p[0].X)) >= 0.01).ToList();
        var segs = tris.SelectMany(p => new[] { (p[0], p[1]), (p[1], p[2]), (p[2], p[0]) }).ToList();
        var pieces = new List<((double X, double Z) A, (double X, double Z) B)>();
        var seen = new HashSet<(double, double, double, double)>();
        foreach (var (a, c) in segs)
        {
            double rx = c.X - a.X, rz = c.Z - a.Z, len = Math.Sqrt(rx * rx + rz * rz);
            if (len < 0.01) continue;
            var cuts = new List<double> { 0, 1 };
            foreach (var (p, q) in segs)
            {
                double sx = q.X - p.X, sz = q.Z - p.Z, slen = Math.Sqrt(sx * sx + sz * sz), den = rx * sz - rz * sx;
                double wx = p.X - a.X, wz = p.Z - a.Z;
                if (slen < 0.01) continue;
                // a corner within 5 cm of this edge cuts it where it falls, even when it misses the edge
                if (Math.Abs(wx * rz - wz * rx) <= 0.05 * len) cuts.Add((wx * rx + wz * rz) / (len * len));
                if (Math.Abs(den) < 1e-3 * len * slen)
                {
                    // parallel: where the ends of an edge running along this one (within 1 cm) fall on it
                    if (Math.Abs(wx * rz - wz * rx) > 0.01 * len) continue;
                    cuts.Add((wx * rx + wz * rz) / (len * len));
                    cuts.Add(((q.X - a.X) * rx + (q.Z - a.Z) * rz) / (len * len));
                    continue;
                }
                // a crossing, or an edge ending on this one (within 1 cm)
                double s = (wx * sz - wz * sx) / den, u = (wx * rz - wz * rx) / den;
                if (u >= -0.01 / slen && u <= 1 + 0.01 / slen) cuts.Add(s);
            }
            var at = cuts.Where(s => s >= 0 && s <= 1).Order().ToList();
            double nx = -rz / len * 0.02, nz = rx / len * 0.02; // 2 cm to the left
            for (int k = 0; k + 1 < at.Count; k++)
            {
                if ((at[k + 1] - at[k]) * len < 0.005) continue;
                double m = (at[k] + at[k + 1]) / 2, mx = a.X + rx * m, mz = a.Z + rz * m;
                bool left = Covered(tris, mx + nx, mz + nz), right = Covered(tris, mx - nx, mz - nz);
                if (left == right) continue;
                var p0 = (a.X + rx * at[k], a.Z + rz * at[k]);
                var p1 = (a.X + rx * at[k + 1], a.Z + rz * at[k + 1]);
                var piece = left ? (p0, p1) : (p1, p0);
                // an edge two triangles share on the same side (a sliver's) gives the same piece twice
                if (seen.Add((Math.Round(piece.Item1.Item1, 2), Math.Round(piece.Item1.Item2, 2), Math.Round(piece.Item2.Item1, 2), Math.Round(piece.Item2.Item2, 2))))
                    pieces.Add(piece);
            }
        }

        var rings = new List<(double X, double Z)[]>();
        var used = new bool[pieces.Count];
        for (int first = 0; first < pieces.Count; first++)
        {
            if (used[first]) continue;
            used[first] = true;
            var ring = new List<(double X, double Z)> { pieces[first].A };
            var end = pieces[first].B;
            while (true)
            {
                // the nearest unused start, or the ring's own start, within 1 m: a sliver the pieces miss
                // leaves a gap, bridged straight
                int best = -1;
                double bestD = Dist2(end, pieces[first].A);
                for (int j = 0; j < pieces.Count; j++)
                    if (!used[j] && Dist2(end, pieces[j].A) < bestD) { best = j; bestD = Dist2(end, pieces[j].A); }
                if (bestD > 1) { ring.Clear(); break; } // an open chain: dropped, as Chain does
                if (best < 0) break;
                used[best] = true;
                ring.Add(pieces[best].A);
                end = pieces[best].B;
            }
            var r = ring.Select(p => (Math.Round(p.X, 2), Math.Round(p.Z, 2))).ToArray();
            if (r.Length >= 3 && Math.Abs(Area(r)) >= 1) rings.Add(r);
        }
        return rings;
    }

    private static double Dist2((double X, double Z) a, (double X, double Z) b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    private static bool Covered(List<(double X, double Z)[]> tris, double x, double z)
    {
        foreach (var p in tris)
        {
            double d0 = (p[1].X - p[0].X) * (z - p[0].Z) - (p[1].Z - p[0].Z) * (x - p[0].X);
            double d1 = (p[2].X - p[1].X) * (z - p[1].Z) - (p[2].Z - p[1].Z) * (x - p[1].X);
            double d2 = (p[0].X - p[2].X) * (z - p[2].Z) - (p[0].Z - p[2].Z) * (x - p[2].X);
            if (d0 >= 0 && d1 >= 0 && d2 >= 0 || d0 <= 0 && d1 <= 0 && d2 <= 0) return true;
        }
        return false;
    }

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
