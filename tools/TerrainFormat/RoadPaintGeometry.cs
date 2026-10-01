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

    /// <summary>Triangles the game draws for this primitive: two per run edge, or the list as stored.</summary>
    public static int TriangleCount(RoadPaint p) =>
        p.Shape == PaintShape.Triangles ? p.Indices.Length / 3 : Runs(p).Sum(r => Math.Max(0, r.Length / 3 - 1) * 2);

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
