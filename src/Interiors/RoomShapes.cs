using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// Plane polygons in an interior's plan frame (x across the front, z from front to back), for rooms
/// that follow a building's real outline (#1290). Counter-clockwise means a positive
/// <see cref="Area"/>: walking a ring, its inside is on the left. Pure, so the unit tier runs it.
/// </summary>
public static class RoomShapes
{
    /// <summary>Signed area: positive for a counter-clockwise ring.</summary>
    public static float Area(IReadOnlyList<Vector2> p)
    {
        float a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += p[j].X * p[i].Y - p[i].X * p[j].Y;
        return a / 2;
    }

    /// <summary>Even-odd test, so a set of rings with holes answers as one shape.</summary>
    public static bool Contains(IReadOnlyList<Vector2> p, Vector2 q)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].Y > q.Y) != (p[j].Y > q.Y)
                && q.X < (p[j].X - p[i].X) * (q.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X)
                inside = !inside;
        return inside;
    }

    public static bool Contains(IReadOnlyList<Vector2[]> rings, Vector2 q)
    {
        bool inside = false;
        foreach (var r in rings) if (Contains(r, q)) inside = !inside;
        return inside;
    }

    /// <summary>The distance from <paramref name="q"/> to the nearest edge of the ring.</summary>
    public static float EdgeDistance(IReadOnlyList<Vector2> p, Vector2 q)
    {
        float best = float.MaxValue;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            best = Math.Min(best, SegmentDistance(p[j], p[i], q));
        return best;
    }

    public static float SegmentDistance(Vector2 a, Vector2 b, Vector2 q)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-12f ? 0 : Math.Clamp((q - a).Dot(ab) / len2, 0, 1);
        return (a + ab * t).DistanceTo(q);
    }

    // ---- an outline from a covered grid -----------------------------------------------------

    /// <summary>
    /// The boundary of a covered grid as rings in the plan frame (cell (i, j) spans
    /// x = i·cell − width/2 and z = j·cell − depth/2 onward), outer rings counter-clockwise, holes
    /// clockwise. The cells' staircase is simplified to within <paramref name="tolerance"/>, so a
    /// facade at an angle comes back as one straight edge. Corners within a cell and a half of the
    /// box's edge are put on it, as <see cref="PlanOutline.Peel"/> does with its wings.
    /// </summary>
    public static List<Vector2[]> Trace(bool[,] grid, float cell, float width, float depth, float tolerance)
    {
        int nx = grid.GetLength(0), nz = grid.GetLength(1);
        bool At(int i, int j) => i >= 0 && j >= 0 && i < nx && j < nz && grid[i, j];
        // directed boundary edges, the covered cell on their left
        var next = new Dictionary<(int, int), List<(int, int)>>();
        void Edge(int ax, int az, int bx, int bz)
        {
            if (!next.TryGetValue((ax, az), out var l)) next[(ax, az)] = l = new List<(int, int)>(2);
            l.Add((bx, bz));
        }
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                if (!grid[i, j]) continue;
                if (!At(i, j - 1)) Edge(i, j, i + 1, j);
                if (!At(i + 1, j)) Edge(i + 1, j, i + 1, j + 1);
                if (!At(i, j + 1)) Edge(i + 1, j + 1, i, j + 1);
                if (!At(i - 1, j)) Edge(i, j + 1, i, j);
            }

        var rings = new List<Vector2[]>();
        while (next.Count > 0)
        {
            var start = next.Keys.First();
            var ring = new List<(int X, int Z)> { start };
            var at = start;
            (int X, int Z) dir = (0, 0);
            while (true)
            {
                var outs = next[at];
                // where two cells touch only at a corner, keep to the left: each stays its own ring
                int pick = 0;
                if (outs.Count > 1)
                {
                    int best = int.MinValue;
                    for (int k = 0; k < outs.Count; k++)
                    {
                        var d = (outs[k].Item1 - at.Item1, outs[k].Item2 - at.Item2);
                        int turn = dir.X * d.Item2 - dir.Z * d.Item1;
                        if (turn > best) { best = turn; pick = k; }
                    }
                }
                var to = outs[pick];
                outs.RemoveAt(pick);
                if (outs.Count == 0) next.Remove(at);
                dir = (to.Item1 - at.Item1, to.Item2 - at.Item2);
                at = to;
                if (at == start) break;
                ring.Add(at);
                if (!next.ContainsKey(at)) break; // an open chain never happens on a grid; be safe
            }
            if (ring.Count < 4) continue;
            var pts = ring.Select(p => new Vector2(p.X * cell - width / 2, p.Z * cell - depth / 2)).ToList();
            var simple = Simplify(pts, tolerance);
            if (simple.Count < 3) continue;
            float hw = width / 2, hd = depth / 2, snap = cell * 1.5f;
            for (int k = 0; k < simple.Count; k++)
            {
                var p = simple[k];
                float x = p.X <= -hw + snap ? -hw : p.X >= hw - snap ? hw : p.X;
                float z = p.Y <= -hd + snap ? -hd : p.Y >= hd - snap ? hd : p.Y;
                simple[k] = new Vector2(x, z);
            }
            simple = Tidy(simple);
            if (simple.Count >= 3 && Math.Abs(Area(simple)) >= cell * cell * 4) rings.Add(simple.ToArray());
        }
        return rings;
    }

    /// <summary>Douglas-Peucker on a closed ring, anchored at its two points farthest apart.</summary>
    public static List<Vector2> Simplify(List<Vector2> ring, float tolerance)
    {
        int n = ring.Count;
        if (n <= 3) return new List<Vector2>(ring);
        int a = 0, b = 0;
        float far = -1;
        for (int i = 0; i < n; i++)
        {
            float d = ring[i].DistanceSquaredTo(ring[0]);
            if (d > far) { far = d; b = i; }
        }
        far = -1;
        for (int i = 0; i < n; i++)
        {
            float d = ring[i].DistanceSquaredTo(ring[b]);
            if (d > far) { far = d; a = i; }
        }
        if (a > b) (a, b) = (b, a);
        var keep = new bool[n];
        keep[a] = keep[b] = true;
        void Run(int from, int to)
        {
            // from..to round the ring
            int len = (to - from + n) % n;
            if (len < 2) return;
            var p0 = ring[from];
            var p1 = ring[to];
            float worst = -1;
            int at = -1;
            for (int s = 1; s < len; s++)
            {
                int k = (from + s) % n;
                float d = SegmentDistance(p0, p1, ring[k]);
                if (d > worst) { worst = d; at = k; }
            }
            if (worst <= tolerance) return;
            keep[at] = true;
            Run(from, at);
            Run(at, to);
        }
        Run(a, b);
        Run(b, a);
        var o = new List<Vector2>();
        for (int i = 0; i < n; i++) if (keep[i]) o.Add(ring[i]);
        return o;
    }

    /// <summary>Repeated and collinear corners dropped.</summary>
    public static List<Vector2> Tidy(List<Vector2> ring, float eps = 0.01f)
    {
        var p = new List<Vector2>(ring);
        bool changed = true;
        while (changed && p.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < p.Count && p.Count >= 3; i++)
            {
                var prev = p[(i - 1 + p.Count) % p.Count];
                var cur = p[i];
                var nxt = p[(i + 1) % p.Count];
                if (cur.DistanceTo(prev) < eps || SegmentDistance(prev, nxt, cur) < eps && (cur - prev).Dot(nxt - cur) > 0)
                {
                    p.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }
        return p;
    }

    // ---- clipping ----------------------------------------------------------------------------

    /// <summary>
    /// A ring cut by an axis-aligned rectangle (Sutherland-Hodgman, the rectangle convex). Where the
    /// ring leaves the rectangle and comes back, the cut runs along its edge; a piece that runs
    /// back over itself there (the ring met the rectangle twice, apart) is reported by
    /// <paramref name="split"/>.
    /// </summary>
    public static List<Vector2> ClipToRect(IReadOnlyList<Vector2> ring, float x0, float z0, float x1, float z1, out bool split)
    {
        var p = new List<Vector2>(ring);
        if (Area(p) < 0) p.Reverse();
        p = ClipHalf(p, q => q.X - x0, (a, b) => Lerp(a, b, (x0 - a.X) / (b.X - a.X)));
        p = ClipHalf(p, q => x1 - q.X, (a, b) => Lerp(a, b, (x1 - a.X) / (b.X - a.X)));
        p = ClipHalf(p, q => q.Y - z0, (a, b) => Lerp(a, b, (z0 - a.Y) / (b.Y - a.Y)));
        p = ClipHalf(p, q => z1 - q.Y, (a, b) => Lerp(a, b, (z1 - a.Y) / (b.Y - a.Y)));
        p = Tidy(p);
        split = false;
        // a counter-clockwise result runs along each edge of the rectangle one way only
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            const float e = 0.01f;
            if (Math.Abs(a.Y - z0) < e && Math.Abs(b.Y - z0) < e && b.X < a.X - e) split = true;
            if (Math.Abs(a.X - x1) < e && Math.Abs(b.X - x1) < e && b.Y < a.Y - e) split = true;
            if (Math.Abs(a.Y - z1) < e && Math.Abs(b.Y - z1) < e && b.X > a.X + e) split = true;
            if (Math.Abs(a.X - x0) < e && Math.Abs(b.X - x0) < e && b.Y > a.Y + e) split = true;
        }
        return p;
    }

    private static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * t;

    private static List<Vector2> ClipHalf(List<Vector2> p, Func<Vector2, float> inside, Func<Vector2, Vector2, Vector2> cross)
    {
        var o = new List<Vector2>(p.Count + 4);
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            bool ia = inside(a) >= 0, ib = inside(b) >= 0;
            if (ia) o.Add(a);
            if (ia != ib) o.Add(cross(a, b));
        }
        return o;
    }

    // ---- rays ---------------------------------------------------------------------------------

    /// <summary>
    /// How far a ray from <paramref name="o"/> along <paramref name="d"/> (unit) runs before it
    /// crosses an edge of any of the rings, or <paramref name="max"/>; which ring and edge it met.
    /// </summary>
    public static float Cast(IReadOnlyList<IReadOnlyList<Vector2>> rings, Vector2 o, Vector2 d, float max, out int ring, out int edge)
    {
        float best = max;
        ring = edge = -1;
        for (int r = 0; r < rings.Count; r++)
        {
            var p = rings[r];
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                if (RayHit(o, d, a, b) is { } t && t > 1e-4f && t < best) { best = t; ring = r; edge = i; }
            }
        }
        return best;
    }

    /// <summary>Distance along a ray to a segment, or null if it misses (parallel counts as a miss).</summary>
    public static float? RayHit(Vector2 o, Vector2 d, Vector2 a, Vector2 b)
    {
        var e = b - a;
        float den = d.X * e.Y - d.Y * e.X;
        if (Math.Abs(den) < 1e-9f) return null;
        var w = a - o;
        float t = (w.X * e.Y - w.Y * e.X) / den;
        float s = (w.X * d.Y - w.Y * d.X) / den;
        if (s < -1e-5f || s > 1 + 1e-5f) return null;
        return t;
    }

    // ---- drawing -----------------------------------------------------------------------------

    /// <summary>
    /// A ring moved <paramref name="by"/> inward, each edge parallel to its own (mitred corners, a
    /// sharp one capped at four times the offset). Counter-clockwise in and out.
    /// </summary>
    public static Vector2[] Inset(IReadOnlyList<Vector2> p, float by)
    {
        int n = p.Count;
        var o = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var prev = p[(i - 1 + n) % n];
            var cur = p[i];
            var nxt = p[(i + 1) % n];
            var d0 = (cur - prev).Normalized();
            var d1 = (nxt - cur).Normalized();
            // the inside is on the left: (-dz, dx)
            var n0 = new Vector2(-d0.Y, d0.X);
            var n1 = new Vector2(-d1.Y, d1.X);
            var bis = n0 + n1;
            float len = bis.Length();
            if (len < 1e-4f) { o[i] = cur + n1 * by; continue; }
            bis /= len;
            float cos = bis.Dot(n1);
            float k = cos < 0.25f ? 4f : 1f / cos;
            o[i] = cur + bis * (by * k);
        }
        return o;
    }

    /// <summary>Ear clipping of a simple ring: triangles as index triples, counter-clockwise.</summary>
    public static List<int> Triangulate(IReadOnlyList<Vector2> p)
    {
        var tris = new List<int>();
        int n = p.Count;
        if (n < 3) return tris;
        var idx = Enumerable.Range(0, n).ToList();
        if (Area(p) < 0) idx.Reverse();
        static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        int guard = 0;
        while (idx.Count > 3 && guard++ < n * n)
        {
            bool cut = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i - 1 + idx.Count) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                var a = p[ia];
                var b = p[ib];
                var c = p[ic];
                float cr = Cross(a, b, c);
                if (cr < -1e-6f) continue; // reflex
                if (cr <= 1e-6f) { idx.RemoveAt(i); cut = true; break; } // collinear: drop, no triangle
                bool empty = true;
                foreach (int k in idx)
                {
                    if (k == ia || k == ib || k == ic) continue;
                    var q = p[k];
                    if (Cross(a, b, q) >= -1e-6f && Cross(b, c, q) >= -1e-6f && Cross(c, a, q) >= -1e-6f
                        && q.DistanceSquaredTo(a) > 1e-8f && q.DistanceSquaredTo(b) > 1e-8f && q.DistanceSquaredTo(c) > 1e-8f)
                    { empty = false; break; }
                }
                if (!empty) continue;
                tris.Add(ia); tris.Add(ib); tris.Add(ic);
                idx.RemoveAt(i);
                cut = true;
                break;
            }
            if (!cut) break;
        }
        if (idx.Count == 3 && Cross(p[idx[0]], p[idx[1]], p[idx[2]]) > 1e-6f) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
        return tris;
    }
}
