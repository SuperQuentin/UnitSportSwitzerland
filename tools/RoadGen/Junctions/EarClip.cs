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
