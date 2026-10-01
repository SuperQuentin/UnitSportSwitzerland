namespace UnitSport.Terrain.Format;

/// <summary>
/// Dash splitting of a <see cref="PaintShape.Polyline"/> paint primitive, shared by the game
/// (which ribbons each run) and RoadGen (which reports the runtime cost). The pattern starts with
/// a dash at the first vertex; the network stage phases it by where it starts the polyline.
/// Distances are horizontal (x, z), like the stage's stations.
/// </summary>
public static class RoadPaintGeometry
{
    /// <summary>The painted runs, xyz triples: the whole line when solid, else one per dash.</summary>
    public static List<float[]> Runs(RoadPaint p)
    {
        var v = p.Vertices;
        int n = v.Length / 3;
        var runs = new List<float[]>();
        if (n < 2) return runs;
        if (p.Dash <= 0 || p.Gap <= 0) { runs.Add(v); return runs; }

        var cur = new List<float> { v[0], v[1], v[2] };
        bool inDash = true;
        double s = 0, next = p.Dash;   // distance at vertex i, and of the next dash/gap boundary
        for (int i = 0; i < n - 1; i++)
        {
            double dx = v[i * 3 + 3] - v[i * 3], dz = v[i * 3 + 5] - v[i * 3 + 2];
            double len = Math.Sqrt(dx * dx + dz * dz);
            while (next <= s + len)
            {
                double t = len < 1e-9 ? 1 : (next - s) / len;
                float x = (float)(v[i * 3] + dx * t);
                float y = (float)(v[i * 3 + 1] + (v[i * 3 + 4] - v[i * 3 + 1]) * t);
                float z = (float)(v[i * 3 + 2] + dz * t);
                if (inDash)
                {
                    cur.Add(x); cur.Add(y); cur.Add(z);
                    Flush(runs, cur);
                    next += p.Gap;
                }
                else
                {
                    cur.Clear();
                    cur.Add(x); cur.Add(y); cur.Add(z);
                    next += p.Dash;
                }
                inDash = !inDash;
            }
            if (inDash) { cur.Add(v[i * 3 + 3]); cur.Add(v[i * 3 + 4]); cur.Add(v[i * 3 + 5]); }
            s += len;
        }
        if (inDash) Flush(runs, cur);
        return runs;
    }

    /// <summary>Triangles the game draws for this primitive: two per run edge, one per tooth, or the list as stored.</summary>
    public static int TriangleCount(RoadPaint p) =>
        p.Shape == PaintShape.Triangles ? p.Indices.Length / 3
        : p.Type == PaintType.SharkTooth ? Runs(p).Count
        : Runs(p).Sum(r => Math.Max(0, r.Length / 3 - 1) * 2);

    /// <summary>
    /// A <see cref="PaintType.SharkTooth"/> polyline (#121, the Swiss Wartelinie 6.13) as triangles,
    /// xyz × 3 each: <c>Dash</c> is a tooth's base, <c>Gap</c> the space between two, <c>Width</c> its
    /// height; each dash run is a base and its apex lies <c>Width</c> to the RIGHT of the line's
    /// direction (x east, z south: right of (dx, dz) is (-dz, dx)), toward the approaching driver.
    /// Two vertices store a whole row.
    /// </summary>
    public static List<float[]> Teeth(RoadPaint p)
    {
        var teeth = new List<float[]>();
        foreach (var run in Runs(p))
        {
            float ax = run[0], ay = run[1], az = run[2], bx = run[^3], by = run[^2], bz = run[^1];
            float dx = bx - ax, dz = bz - az, len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 1e-4f) continue;
            float rx = -dz / len * p.Width, rz = dx / len * p.Width;
            teeth.Add([ax, ay, az, bx, by, bz, (ax + bx) * 0.5f + rx, (ay + by) * 0.5f, (az + bz) * 0.5f + rz]);
        }
        return teeth;
    }

    private static void Flush(List<float[]> runs, List<float> cur)
    {
        if (cur.Count >= 6)
        {
            double dx = cur[^3] - cur[0], dz = cur[^1] - cur[2];
            if (cur.Count > 6 || dx * dx + dz * dz > 1e-6) runs.Add(cur.ToArray());
        }
        cur.Clear();
    }
}
