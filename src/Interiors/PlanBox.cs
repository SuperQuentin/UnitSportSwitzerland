using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A building's plan view as the smallest oriented rectangle around its walls, in tile-local
/// (x, z), plus the height its walls stop at. Everything that reasons about a building's shape
/// (doors, interiors, type detection) starts from this, so they agree on where the building is.
/// </summary>
public readonly record struct PlanBox(Vector2 Center, Vector2 AxisU, float Width, float Depth, float Eave)
{
    /// <summary>A face this close to vertical is a wall proper; a steep spire face is not.</summary>
    private const float VerticalNormalY = 0.05f;

    public Vector2 AxisV => new(-AxisU.Y, AxisU.X);
    public float Area => Width * Depth;
    public float Long => Math.Max(Width, Depth);
    public float Short => Math.Min(Width, Depth);
    public Vector2 LongAxis => Width >= Depth ? AxisU : AxisV;

    public Vector2[] Corners()
    {
        var u = AxisU * (Width / 2);
        var v = AxisV * (Depth / 2);
        return new[] { Center - u - v, Center + u - v, Center + u + v, Center - u + v };
    }

    /// <summary>Extent of the box along a unit axis, relative to <paramref name="origin"/>.</summary>
    public (float Min, float Max) Along(Vector2 axis, Vector2 origin)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var c in Corners())
        {
            float t = (c - origin).Dot(axis);
            min = Math.Min(min, t);
            max = Math.Max(max, t);
        }
        return (min, max);
    }

    /// <summary>
    /// Plan-view gap to another box, 0 when they touch or overlap: the largest separation along
    /// any of the four box axes. Exact for boxes side by side, a slight underestimate corner to
    /// corner, which is the forgiving side for "do these two buildings touch".
    /// </summary>
    public float GapTo(PlanBox o)
    {
        float gap = float.MinValue;
        foreach (var axis in new[] { AxisU, AxisV, o.AxisU, o.AxisV })
        {
            var (a0, a1) = Along(axis, Vector2.Zero);
            var (b0, b1) = o.Along(axis, Vector2.Zero);
            gap = Math.Max(gap, Math.Max(b0 - a1, a0 - b1));
        }
        return Math.Max(0, gap);
    }

    /// <summary>Plan distance from a point to the box, 0 inside it.</summary>
    public float DistanceTo(Vector2 p)
    {
        var d = p - Center;
        float du = Math.Max(0, Math.Abs(d.Dot(AxisU)) - Width / 2);
        float dv = Math.Max(0, Math.Abs(d.Dot(AxisV)) - Depth / 2);
        return Mathf.Sqrt(du * du + dv * dv);
    }

    /// <summary>
    /// Minimum-area oriented rectangle (rotating calipers over the hull of the wall vertices).
    /// Buildings with no wall faces at all fall back to every vertex. Null for a degenerate solid.
    /// </summary>
    public static PlanBox? Of(Building b)
    {
        var pts = new List<Vector2>();
        var tops = new List<float>();
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, c, d) = b.Tri(t);
            var n = (c - a).Cross(d - a);
            float len = n.Length();
            if (len < 1e-6f || Mathf.Abs(n.Y / len) >= BuildingTriangles.RoofNormalY) continue;
            if (new Vector2(n.X, n.Z).LengthSquared() < 1e-10f) continue;
            pts.Add(new Vector2(a.X, a.Z)); pts.Add(new Vector2(c.X, c.Z)); pts.Add(new Vector2(d.X, d.Z));
            if (Mathf.Abs(n.Y / len) < VerticalNormalY) tops.Add(Math.Max(a.Y, Math.Max(c.Y, d.Y)));
        }
        if (pts.Count < 3)
            for (int t = 0; t < b.TriangleCount * 3; t++)
                pts.Add(new Vector2(b.Triangles[t * 3], b.Triangles[t * 3 + 2]));
        if (pts.Count < 3) return null;

        var hull = ConvexHull(pts);
        float bestArea = float.MaxValue;
        Vector2 u = Vector2.Right, center = hull[0];
        float w = 0, dpt = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var e = hull[(i + 1) % hull.Count] - hull[i];
            if (e.LengthSquared() < 1e-6f) continue;
            var ax = e.Normalized();
            var ay = new Vector2(-ax.Y, ax.X);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in hull)
            {
                float x = p.Dot(ax), y = p.Dot(ay);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            float area = (maxX - minX) * (maxY - minY);
            if (area < bestArea - 1e-3f)
            {
                bestArea = area;
                u = ax;
                w = maxX - minX;
                dpt = maxY - minY;
                center = ax * ((minX + maxX) * 0.5f) + ay * ((minY + maxY) * 0.5f);
            }
        }

        // The eave is the median top of the vertical wall faces: a gable end reaches the ridge,
        // but most wall faces stop at the wall plate, and a spire's faces are not vertical at all.
        float eave = b.MaxY;
        if (tops.Count > 0)
        {
            tops.Sort();
            eave = tops[tops.Count / 2];
        }
        return new PlanBox(center, u, w, dpt, eave);
    }

    private static List<Vector2> ConvexHull(List<Vector2> points)
    {
        var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        var h = new List<Vector2>();
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var q in p)
        {
            while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(q);
        }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            var q = p[i];
            while (h.Count >= lower && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(q);
        }
        h.RemoveAt(h.Count - 1);
        return h;
    }
}
