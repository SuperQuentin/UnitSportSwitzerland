namespace UnitSport.Tools.RoadGen.Junctions;

using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// Ear clipping for simple polygons. Only the fallback path — junction rings are star-shaped
/// about their node almost always, and the fan is both faster and tidier — but a node where
/// six roads arrive at awkward angles can produce a ring the fan cannot handle, and silently
/// emitting inverted triangles there would be worse than the cost of carrying this.
/// </summary>
public static class EarClip
{
    public static List<int> Triangulate(IReadOnlyList<Vec2> polygon)
    {
        var indices = new List<int>();
        int n = polygon.Count;
        if (n < 3) return indices;

        var remaining = new List<int>(n);
        bool ccw = SignedArea(polygon) > 0;
        for (int i = 0; i < n; i++) remaining.Add(ccw ? i : n - 1 - i);

        int guard = 0;
        while (remaining.Count > 3 && guard++ < n * n)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int prev = remaining[(i - 1 + remaining.Count) % remaining.Count];
                int current = remaining[i];
                int next = remaining[(i + 1) % remaining.Count];

                if (!IsEar(polygon, remaining, prev, current, next)) continue;

                indices.Add(prev);
                indices.Add(current);
                indices.Add(next);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }

            // a ring that offers no ear is self-intersecting; take the fan and move on rather
            // than spinning, because one ugly junction beats a hung preprocessor
            if (!clipped) break;
        }

        if (remaining.Count == 3)
        {
            indices.Add(remaining[0]);
            indices.Add(remaining[1]);
            indices.Add(remaining[2]);
        }

        return indices;
    }

    /// <summary>
    /// <see cref="Triangulate"/>, then every inner diagonal flipped while the vertex across it lies inside a triangle's
    /// circumcircle (Lawson's flips: the constrained Delaunay triangulation of the ring, no point added). Ear clipping takes the
    /// first ear it meets, so a ring with a dense kerb arc facing a long straight kerb came out as fans of slivers from one
    /// vertex across the whole cap, which shade in facets (#1771); the flips spread the triangles between the two sides.
    /// </summary>
    public static List<int> TriangulateDelaunay(IReadOnlyList<Vec2> polygon)
    {
        var tris = Triangulate(polygon);
        int count = tris.Count / 3;
        if (count < 2) return tris;
        // every triangle counter-clockwise, so the circumcircle test has one sign
        for (int t = 0; t < count; t++)
            if ((polygon[tris[t * 3 + 1]] - polygon[tris[t * 3]]).Cross(polygon[tris[t * 3 + 2]] - polygon[tris[t * 3]]) < 0)
                (tris[t * 3 + 1], tris[t * 3 + 2]) = (tris[t * 3 + 2], tris[t * 3 + 1]);
        // the ring's own edges never flip
        int n = polygon.Count;
        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
        var boundary = new HashSet<(int, int)>();
        for (int i = 0; i < n; i++) boundary.Add(Key(i, (i + 1) % n));
        var edgeTris = new Dictionary<(int, int), List<int>>();
        void Index(int t)
        {
            for (int e = 0; e < 3; e++)
            {
                var k = Key(tris[t * 3 + e], tris[t * 3 + (e + 1) % 3]);
                if (!edgeTris.TryGetValue(k, out var l)) edgeTris[k] = l = new List<int>(2);
                l.Add(t);
            }
        }
        void Unindex(int t)
        {
            for (int e = 0; e < 3; e++)
                if (edgeTris.TryGetValue(Key(tris[t * 3 + e], tris[t * 3 + (e + 1) % 3]), out var l)) l.Remove(t);
        }
        for (int t = 0; t < count; t++) Index(t);
        int Opposite(int t, int a, int b)
        {
            for (int e = 0; e < 3; e++)
                if (tris[t * 3 + e] != a && tris[t * 3 + e] != b) return tris[t * 3 + e];
            return -1;
        }
        var queue = new Queue<(int, int)>(edgeTris.Keys.Where(k => !boundary.Contains(k)));
        int guard = 0, limit = 50 * count * count + 100;
        while (queue.Count > 0 && guard++ < limit)
        {
            var (a, b) = queue.Dequeue();
            if (!edgeTris.TryGetValue((a, b), out var pair) || pair.Count != 2) continue;
            int t0 = pair[0], t1 = pair[1];
            int c = Opposite(t0, a, b), d = Opposite(t1, a, b);
            if (c < 0 || d < 0 || c == d) continue;
            // t0 as (a, b, c) counter-clockwise: d inside its circumcircle and the quad a-d-b-c convex, then flip a-b to c-d
            var (pa, pb, pc, pd) = (polygon[a], polygon[b], polygon[c], polygon[d]);
            if ((pb - pa).Cross(pc - pa) < 0) (pa, pb, a, b) = (pb, pa, b, a);
            if (!InCircle(pa, pb, pc, pd)) continue;
            // convex: c and d on either side of a-b, a and b on either side of c-d
            if ((pd - pc).Cross(pa - pc) * (pd - pc).Cross(pb - pc) >= 0) continue;
            Unindex(t0);
            Unindex(t1);
            (tris[t0 * 3], tris[t0 * 3 + 1], tris[t0 * 3 + 2]) = (a, d, c);   // a, d, c counter-clockwise: d is right of a->b
            (tris[t1 * 3], tris[t1 * 3 + 1], tris[t1 * 3 + 2]) = (d, b, c);
            Index(t0);
            Index(t1);
            foreach (var k in new[] { Key(a, c), Key(c, b), Key(b, d), Key(d, a) })
                if (!boundary.Contains(k)) queue.Enqueue(k);
        }
        return tris;
    }

    /// <summary>Whether <paramref name="d"/> lies strictly inside the circle through the counter-clockwise <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/>.</summary>
    private static bool InCircle(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        double ax = a.X - d.X, ay = a.Y - d.Y, bx = b.X - d.X, by = b.Y - d.Y, cx = c.X - d.X, cy = c.Y - d.Y;
        double det = (ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay) + (cx * cx + cy * cy) * (ax * by - bx * ay);
        double scale = (ax * ax + ay * ay + bx * bx + by * by + cx * cx + cy * cy);
        return det > 1e-9 * scale * scale;
    }

    private static bool IsEar(IReadOnlyList<Vec2> polygon, List<int> remaining, int prev, int current, int next)
    {
        var a = polygon[prev];
        var b = polygon[current];
        var c = polygon[next];

        if ((b - a).Cross(c - a) <= 1e-12) return false;   // reflex or degenerate

        foreach (int index in remaining)
        {
            if (index == prev || index == current || index == next) continue;
            if (InTriangle(polygon[index], a, b, c)) return false;
        }
        return true;
    }

    private static bool InTriangle(Vec2 p, Vec2 a, Vec2 b, Vec2 c)
    {
        double d1 = (b - a).Cross(p - a);
        double d2 = (c - b).Cross(p - b);
        double d3 = (a - c).Cross(p - c);
        bool negative = d1 < -1e-12 || d2 < -1e-12 || d3 < -1e-12;
        bool positive = d1 > 1e-12 || d2 > 1e-12 || d3 > 1e-12;
        return !(negative && positive);
    }

    public static double SignedArea(IReadOnlyList<Vec2> polygon)
    {
        double sum = 0;
        for (int i = 0; i < polygon.Count; i++)
            sum += polygon[i].Cross(polygon[(i + 1) % polygon.Count]);
        return sum * 0.5;
    }
}
