namespace UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// Polyline utilities. The important one is <see cref="Simplify"/>: swissTLM3D lines are
/// already densified to ≤4 m by the existing pipeline, so every straight run carries a
/// vertex every few metres. Rounding a corner at each of those would round nothing —
/// the design intent has to be recovered first, and only then can the real corners be
/// found and smoothed.
/// </summary>
public static class Polyline
{
    public static double Length(IReadOnlyList<Vec2> pts)
    {
        double total = 0;
        for (int i = 1; i < pts.Count; i++) total += pts[i].DistanceTo(pts[i - 1]);
        return total;
    }

    /// <summary>
    /// Ramer–Douglas–Peucker. Drops vertices that lie within <paramref name="tolerance"/>
    /// of the chord they span, recovering the handful of vertices that actually describe
    /// the road's shape from the many that describe its sampling.
    /// </summary>
    public static List<Vec2> Simplify(IReadOnlyList<Vec2> pts, double tolerance)
    {
        if (pts.Count < 3) return new List<Vec2>(pts);

        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        SimplifyRange(pts, 0, pts.Count - 1, tolerance, keep);

        var result = new List<Vec2>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
            if (keep[i]) result.Add(pts[i]);
        return result;
    }

    private static void SimplifyRange(IReadOnlyList<Vec2> pts, int first, int last,
        double tolerance, bool[] keep)
    {
        if (last <= first + 1) return;

        double worst = -1;
        int worstIndex = -1;
        for (int i = first + 1; i < last; i++)
        {
            double d = PointSegmentDistance(pts[i], pts[first], pts[last]);
            if (d > worst) { worst = d; worstIndex = i; }
        }

        if (worst <= tolerance || worstIndex < 0) return;

        keep[worstIndex] = true;
        SimplifyRange(pts, first, worstIndex, tolerance, keep);
        SimplifyRange(pts, worstIndex, last, tolerance, keep);
    }

    public static double PointSegmentDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double lenSq = ab.LengthSquared;
        if (lenSq < 1e-18) return p.DistanceTo(a);

        double t = Math.Clamp((p - a).Dot(ab) / lenSq, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>Inserts vertices so no segment is longer than <paramref name="maxStep"/>.</summary>
    public static List<Vec2> Densify(IReadOnlyList<Vec2> pts, double maxStep)
    {
        var result = new List<Vec2>(pts.Count * 2);
        if (pts.Count == 0) return result;

        result.Add(pts[0]);
        for (int i = 1; i < pts.Count; i++)
        {
            double d = pts[i].DistanceTo(pts[i - 1]);
            int steps = (int)Math.Ceiling(d / maxStep);
            for (int s = 1; s <= steps; s++)
                result.Add(Vec2.Lerp(pts[i - 1], pts[i], (double)s / steps));
        }
        return result;
    }

    /// <summary>Removes consecutive duplicates, which otherwise produce zero-length tangents.</summary>
    public static List<Vec2> Dedupe(IReadOnlyList<Vec2> pts, double epsilon = 1e-4)
    {
        var result = new List<Vec2>(pts.Count);
        foreach (var p in pts)
            if (result.Count == 0 || result[^1].DistanceSquaredTo(p) > epsilon * epsilon)
                result.Add(p);
        return result;
    }

    /// <summary>Cumulative arc length at each vertex; <c>result[0]</c> is always 0.</summary>
    public static double[] ArcLengths(IReadOnlyList<Vec2> pts)
    {
        var s = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++) s[i] = s[i - 1] + pts[i].DistanceTo(pts[i - 1]);
        return s;
    }

    /// <summary>
    /// Point at a given distance along the polyline, clamped at both ends.
    /// </summary>
    public static Vec2 PointAt(IReadOnlyList<Vec2> pts, double[] arc, double distance)
    {
        if (pts.Count == 0) return Vec2.Zero;
        if (distance <= 0) return pts[0];
        if (distance >= arc[^1]) return pts[^1];

        int i = Array.BinarySearch(arc, distance);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, pts.Count - 2);

        double span = arc[i + 1] - arc[i];
        double t = span < 1e-12 ? 0 : (distance - arc[i]) / span;
        return Vec2.Lerp(pts[i], pts[i + 1], t);
    }

    /// <summary>Cuts <paramref name="fromStart"/> metres off the front and <paramref name="fromEnd"/> off the back.</summary>
    public static List<Vec2> Trim(IReadOnlyList<Vec2> pts, double fromStart, double fromEnd)
    {
        var arc = ArcLengths(pts);
        double total = arc[^1];
        double a = Math.Clamp(fromStart, 0, total);
        double b = Math.Clamp(total - fromEnd, 0, total);
        if (b <= a) return new List<Vec2>();

        var result = new List<Vec2> { PointAt(pts, arc, a) };
        for (int i = 0; i < pts.Count; i++)
            if (arc[i] > a + 1e-6 && arc[i] < b - 1e-6)
                result.Add(pts[i]);
        result.Add(PointAt(pts, arc, b));
        return Dedupe(result);
    }
}
