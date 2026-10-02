namespace UnitSport.Terrain.Format;

/// <summary>
/// Dash splitting of a <see cref="PaintShape.Polyline"/> paint primitive, shared by the game
/// (which ribbons each run) and RoadGen (which reports the runtime cost). The pattern starts with
/// a dash at the first vertex; the network stage phases it by where it starts the polyline.
/// Distances are horizontal (x, z), like the stage's stations.
/// </summary>
public static class RoadPaintGeometry
{
    /// <summary>Mirror of <c>RoadMeshBuilder.BridgeLift</c>: a deck is drawn this far above its line.</summary>
    public const float BridgeLift = 0.15f;

    /// <summary>
    /// A paint line may leave the ribbon's vertices by this much (3D): the centrelines are dense
    /// (5 cm chords, draped heights) and copying every vertex cost +7.5 % of the tile.
    /// </summary>
    public const float SimplifyTolerance = 0.02f;

    /// <summary>
    /// The vertices of a line that lies along a segment (<see cref="RoadPaint.Segment"/>): the
    /// segment's line <paramref name="offset"/> m to its right, cut between two horizontal
    /// distances along it, simplified. The network stage and the decoder both build a referenced
    /// line through here, so the game draws exactly what the stage made.
    /// </summary>
    public static float[] Along(RoadSegment seg, float offset, float from, float to) =>
        Simplify(Cut(Offset(seg, offset), from, to));

    /// <summary>
    /// The line <paramref name="offset"/> metres to the right of the centreline, as the ribbon's
    /// edges are built (per-vertex bisector, no miter), on the deck of a bridge. A point whose
    /// offset edge runs backwards against the centreline (inside a bend tighter than the offset)
    /// is dropped: a gap is honest, a bow-tie is not. Past the carriageway's edge the line lies on
    /// that side's street profile (#120: a bike path's symbols and its line to the sidewalk).
    /// </summary>
    public static float[] Offset(RoadSegment seg, float offset)
    {
        var p = seg.Points;
        int n = seg.PointCount;
        float lift = (seg.Flags & RoadFlags.Bridge) != 0 ? BridgeLift : 0f;
        float beyond = MathF.Abs(offset) - seg.Width * 0.5f;
        if (beyond > 0) lift += RoadStreetSection.HeightAt(offset > 0 ? seg.Attributes.Right : seg.Attributes.Left, beyond);
        var result = new List<float>(n * 3);
        for (int i = 0; i < n; i++)
        {
            int i0 = Math.Max(0, i - 1), i1 = Math.Min(n - 1, i + 1);
            float fx = p[i1 * 3] - p[i0 * 3], fz = p[i1 * 3 + 2] - p[i0 * 3 + 2];
            float fl = MathF.Sqrt(fx * fx + fz * fz);
            if (fl < 1e-4f) { fx = 0; fz = -1; } else { fx /= fl; fz /= fl; }
            float x = p[i * 3] - fz * offset, z = p[i * 3 + 2] + fx * offset;

            if (result.Count >= 3 && i > 0)
            {
                float cx = p[i * 3] - p[i * 3 - 3], cz = p[i * 3 + 2] - p[i * 3 - 1];
                if ((x - result[^3]) * cx + (z - result[^1]) * cz <= 0) continue;
            }
            result.Add(x); result.Add(p[i * 3 + 1] + lift); result.Add(z);
        }
        return result.ToArray();
    }

    /// <summary>Horizontal length of an xyz polyline.</summary>
    public static double Length(float[] v)
    {
        double s = 0;
        for (int i = 3; i < v.Length; i += 3)
        {
            double dx = v[i] - v[i - 3], dz = v[i + 2] - v[i - 1];
            s += Math.Sqrt(dx * dx + dz * dz);
        }
        return s;
    }

    /// <summary>The piece of a polyline between two horizontal distances along it; <paramref name="to"/> past the end keeps the end.</summary>
    public static float[] Cut(float[] v, double from, double to)
    {
        var result = new List<float>();
        double s = 0;
        for (int i = 3; i < v.Length; i += 3)
        {
            double dx = v[i] - v[i - 3], dz = v[i + 2] - v[i - 1];
            double len = Math.Sqrt(dx * dx + dz * dz);
            double s1 = s + len;
            if (s1 >= from && s <= to && len > 1e-9)
            {
                if (result.Count == 0) Lerp(result, v, i - 3, Math.Max(0, (from - s) / len));
                if (s1 < to) { result.Add(v[i]); result.Add(v[i + 1]); result.Add(v[i + 2]); }
                else { Lerp(result, v, i - 3, (to - s) / len); break; }
            }
            s = s1;
        }
        return result.ToArray();
    }

    private static void Lerp(List<float> into, float[] v, int a, double t)
    {
        for (int k = 0; k < 3; k++) into.Add((float)(v[a + k] + (v[a + 3 + k] - v[a + k]) * t));
    }

    /// <summary>Douglas-Peucker in 3D to <see cref="SimplifyTolerance"/>; the ends are kept exactly.</summary>
    public static float[] Simplify(float[] v)
    {
        int n = v.Length / 3;
        if (n <= 2) return v;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float worst = SimplifyTolerance * SimplifyTolerance;
            int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistanceSquared(v, i, a, b);
                if (d > worst) { worst = d; at = i; }
            }
            if (at < 0) continue;
            keep[at] = true;
            stack.Push((a, at));
            stack.Push((at, b));
        }
        var result = new List<float>();
        for (int i = 0; i < n; i++)
            if (keep[i]) { result.Add(v[i * 3]); result.Add(v[i * 3 + 1]); result.Add(v[i * 3 + 2]); }
        return result.ToArray();
    }

    private static float DistanceSquared(float[] v, int p, int a, int b)
    {
        float ax = v[a * 3], ay = v[a * 3 + 1], az = v[a * 3 + 2];
        float dx = v[b * 3] - ax, dy = v[b * 3 + 1] - ay, dz = v[b * 3 + 2] - az;
        float px = v[p * 3] - ax, py = v[p * 3 + 1] - ay, pz = v[p * 3 + 2] - az;
        float len = dx * dx + dy * dy + dz * dz;
        float t = len < 1e-12f ? 0 : Math.Clamp((px * dx + py * dy + pz * dz) / len, 0, 1);
        px -= dx * t; py -= dy * t; pz -= dz * t;
        return px * px + py * py + pz * pz;
    }

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
        : p.Type == PaintType.BikeSymbol ? (p.Vertices.Length >= 6 ? BikeGlyphTriangles : 0)
        : Runs(p).Sum(r => Math.Max(0, r.Length / 3 - 1) * 2);

    /// <summary><see cref="PaintType.BikeSymbol"/> variant bit: the rider travels from the last vertex to the first.</summary>
    public const byte BikeReversed = 1;

    private const int WheelSegments = 8;

    /// <summary>
    /// The bicycle of a <see cref="PaintType.BikeSymbol"/> (#120) in side view, x along the bike
    /// (rear wheel's outer edge -0.5, front's +0.5), y up from the ground (0..<see cref="GlyphTop"/>):
    /// two wheel rings and the frame's tubes as bars (from, to, thickness).
    /// </summary>
    private static readonly (float X0, float Y0, float X1, float Y1, float T)[] GlyphBars =
    [
        (-0.28f, 0.22f, -0.02f, 0.22f, 0.045f),   // chain stay
        (-0.28f, 0.22f, -0.10f, 0.50f, 0.04f),    // seat stay
        (-0.02f, 0.22f, -0.10f, 0.50f, 0.045f),   // seat tube
        (-0.10f, 0.50f, 0.20f, 0.48f, 0.045f),    // top tube
        (-0.02f, 0.22f, 0.20f, 0.48f, 0.05f),     // down tube
        (0.20f, 0.48f, 0.28f, 0.22f, 0.045f),     // fork
        (-0.10f, 0.50f, -0.12f, 0.58f, 0.035f),   // seat post
        (-0.20f, 0.59f, -0.04f, 0.59f, 0.05f),    // saddle
        (0.20f, 0.48f, 0.17f, 0.60f, 0.035f),     // stem
        (0.12f, 0.62f, 0.24f, 0.60f, 0.045f),     // handlebar
    ];

    private const float GlyphTop = 0.64f, WheelX = 0.28f, WheelY = 0.22f, WheelOuter = 0.22f, WheelInner = 0.17f;

    private static readonly int BikeGlyphTriangles = GlyphBars.Length * 2 + 2 * WheelSegments * 2;

    /// <summary>
    /// A <see cref="PaintType.BikeSymbol"/> as triangles, xyz × 3 each. Stored as a polyline whose
    /// first and last vertices are the symbol's back and front along the travel direction (its
    /// length) and <c>Width</c> its size across: read upright by a rider coming along it, so the
    /// bicycle's length lies across the lane, its front wheel on the rider's right.
    /// </summary>
    public static List<float[]> BikeSymbol(RoadPaint p)
    {
        var tris = new List<float[]>();
        var v = p.Vertices;
        if (v.Length < 6) return tris;
        bool reversed = (p.Variant & BikeReversed) != 0;
        float ax = v[0], ay = v[1], az = v[2], bx = v[^3], by = v[^2], bz = v[^1];
        if (reversed) (ax, ay, az, bx, by, bz) = (bx, by, bz, ax, ay, az);
        float fx = bx - ax, fz = bz - az, len = MathF.Sqrt(fx * fx + fz * fz);
        if (len < 1e-3f) return tris;
        fx /= len; fz /= len;
        float rx = -fz, rz = fx;   // the rider's right (x east, z south)

        // side view (x along the bike, y up) onto the ground: x across to the right, y forward
        float[] At(float x, float y)
        {
            float along = y / GlyphTop, across = x * p.Width;
            return [ax + fx * along * len + rx * across, ay + (by - ay) * along, az + fz * along * len + rz * across];
        }
        void Quad(float[] a, float[] b, float[] c, float[] d)
        {
            tris.Add([.. a, .. b, .. c]);
            tris.Add([.. a, .. c, .. d]);
        }

        foreach (var (x0, y0, x1, y1, t) in GlyphBars)
        {
            float dx = x1 - x0, dy = y1 - y0, l = MathF.Sqrt(dx * dx + dy * dy);
            float nx = -dy / l * t * 0.5f, ny = dx / l * t * 0.5f;
            Quad(At(x0 + nx, y0 + ny), At(x1 + nx, y1 + ny), At(x1 - nx, y1 - ny), At(x0 - nx, y0 - ny));
        }
        foreach (float cx in (ReadOnlySpan<float>)[-WheelX, WheelX])
            for (int k = 0; k < WheelSegments; k++)
            {
                float a0 = MathF.Tau * k / WheelSegments, a1 = MathF.Tau * (k + 1) / WheelSegments;
                Quad(At(cx + MathF.Cos(a0) * WheelOuter, WheelY + MathF.Sin(a0) * WheelOuter),
                    At(cx + MathF.Cos(a1) * WheelOuter, WheelY + MathF.Sin(a1) * WheelOuter),
                    At(cx + MathF.Cos(a1) * WheelInner, WheelY + MathF.Sin(a1) * WheelInner),
                    At(cx + MathF.Cos(a0) * WheelInner, WheelY + MathF.Sin(a0) * WheelInner));
            }
        return tris;
    }

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
