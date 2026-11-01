using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// The road-to-facade strip (#1419): the lattice cells between a road ribbon's edge and the nearest
/// building facade, and what the ground in them is like to walk or drive on. Plain C# (no Godot) so a
/// unit test and <c>--groundcheck</c> share it.
///
/// <para>
/// A cell is in the strip when it is outside every ground ribbon (the road's level cross-section out to
/// <see cref="RoadEmbankment.EdgeOffset"/>, sidewalk and verge included) and outside every footprint, and
/// its distance to the nearest ribbon edge plus its distance to the nearest facade is at most
/// <see cref="MaxGapM"/>: every cell on a straight line from a road's edge to a facade within 25 m of it.
/// </para>
/// </summary>
public static class GroundStrip
{
    /// <summary>Metres: a facade farther than this from a road's edge has no strip.</summary>
    public const double MaxGapM = 25;

    /// <summary>Metres: a lattice edge higher than this is a step.</summary>
    public const float StepM = 0.3f;

    /// <summary>Degrees: the upper bounds of the first four slope bins; the fifth is everything steeper.</summary>
    public static readonly double[] SlopeBinsDeg = [15, 30, 45, 52];

    public static readonly string[] SlopeNames = ["<15", "15-30", "30-45", "45-52", ">52"];

    /// <summary>A facade or a ribbon edge point in tile-local plan (X east, Z south).</summary>
    public readonly record struct Seg(float X0, float Z0, float X1, float Z1);

    /// <summary>What a strip measures at one stride.</summary>
    public sealed record Measure(int Cells, int[] Slope, int Edges, int Steps, float WorstStep);

    /// <summary>
    /// Per lattice vertex (row * GridSize + col): the distance outside the nearest ground ribbon's edge
    /// (<see cref="float.PositiveInfinity"/> when none within reach), and whether the vertex is on a ribbon.
    /// </summary>
    public static (float[] Dist, bool[] Inside) RoadEdgeDistance(RoadTile roads)
    {
        int n = ChunkFormat.GridSize;
        var dist = new float[n * n];
        Array.Fill(dist, float.PositiveInfinity);
        var inside = new bool[n * n];
        foreach (var seg in roads.Segments)
        {
            if (!IsGroundRibbon(seg)) continue;
            double el = RoadEmbankment.EdgeOffset(seg, false), er = RoadEmbankment.EdgeOffset(seg, true);
            double reach = Math.Max(el, er) + MaxGapM;
            for (int i = 0; i < seg.PointCount - 1; i++)
            {
                double x0 = seg.Points[i * 3], z0 = seg.Points[i * 3 + 2], x1 = seg.Points[i * 3 + 3], z1 = seg.Points[i * 3 + 5];
                double dx = x1 - x0, dz = z1 - z0, len2 = dx * dx + dz * dz;
                if (len2 < 1e-6) continue;
                int c0 = Math.Max(0, (int)Math.Floor((Math.Min(x0, x1) - reach) / ChunkFormat.SpacingM));
                int c1 = Math.Min(n - 1, (int)Math.Ceiling((Math.Max(x0, x1) + reach) / ChunkFormat.SpacingM));
                int r0 = Math.Max(0, (int)Math.Floor((Math.Min(z0, z1) - reach) / ChunkFormat.SpacingM));
                int r1 = Math.Min(n - 1, (int)Math.Ceiling((Math.Max(z0, z1) + reach) / ChunkFormat.SpacingM));
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        double px = c * ChunkFormat.SpacingM, pz = r * ChunkFormat.SpacingM;
                        double t = Math.Clamp(((px - x0) * dx + (pz - z0) * dz) / len2, 0, 1);
                        double fx = x0 + dx * t - px, fz = z0 + dz * t - pz;
                        double d = Math.Sqrt(fx * fx + fz * fz);
                        // z points south, so (dx, dz) x (p - a) < 0 is the left of the direction of travel seen from above
                        double cross = dx * (pz - z0) - dz * (px - x0);
                        double gap = d - (cross < 0 ? el : er);
                        int k = r * n + c;
                        if (gap <= 0) inside[k] = true;
                        else if (gap < dist[k]) dist[k] = (float)gap;
                    }
            }
        }
        return (dist, inside);
    }

    /// <summary>Whether a segment is a ground surface a body walks or drives on beside a building.</summary>
    public static bool IsGroundRibbon(RoadSegment seg) =>
        RoadEmbankment.IsAtGrade(seg) && seg.PointCount >= 2 && seg.Class != RoadClass.Railway
        && (seg.Flags & (RoadFlags.Ford | RoadFlags.Stairs)) == 0;

    /// <summary>
    /// Per lattice vertex: the distance to the nearest facade (the wall bases in <paramref name="facades"/>)
    /// and whether it is under a footprint (rasterised from <paramref name="footprints"/>, the buildings'
    /// triangles in plan as x,z * 3 floats each).
    /// </summary>
    public static (float[] Dist, bool[] Inside) FacadeDistance(IReadOnlyList<Seg> facades, IReadOnlyList<float[]> footprints)
    {
        int n = ChunkFormat.GridSize;
        var dist = new float[n * n];
        Array.Fill(dist, float.PositiveInfinity);
        var inside = new bool[n * n];
        foreach (var f in facades)
        {
            int c0 = Math.Max(0, (int)Math.Floor((Math.Min(f.X0, f.X1) - MaxGapM) / ChunkFormat.SpacingM));
            int c1 = Math.Min(n - 1, (int)Math.Ceiling((Math.Max(f.X0, f.X1) + MaxGapM) / ChunkFormat.SpacingM));
            int r0 = Math.Max(0, (int)Math.Floor((Math.Min(f.Z0, f.Z1) - MaxGapM) / ChunkFormat.SpacingM));
            int r1 = Math.Min(n - 1, (int)Math.Ceiling((Math.Max(f.Z0, f.Z1) + MaxGapM) / ChunkFormat.SpacingM));
            double dx = f.X1 - f.X0, dz = f.Z1 - f.Z0, len2 = dx * dx + dz * dz;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double px = c * ChunkFormat.SpacingM, pz = r * ChunkFormat.SpacingM;
                    double t = len2 < 1e-9 ? 0 : Math.Clamp(((px - f.X0) * dx + (pz - f.Z0) * dz) / len2, 0, 1);
                    double fx = f.X0 + dx * t - px, fz = f.Z0 + dz * t - pz;
                    float d = (float)Math.Sqrt(fx * fx + fz * fz);
                    int k = r * n + c;
                    if (d < dist[k]) dist[k] = d;
                }
        }
        foreach (var t in footprints)
        {
            float ax = t[0], az = t[1], bx = t[2], bz = t[3], cx = t[4], cz = t[5];
            float area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            if (MathF.Abs(area) < 0.01f) continue;
            int c0 = Math.Max(0, (int)MathF.Ceiling(MathF.Min(ax, MathF.Min(bx, cx)) / (float)ChunkFormat.SpacingM));
            int c1 = Math.Min(n - 1, (int)MathF.Floor(MathF.Max(ax, MathF.Max(bx, cx)) / (float)ChunkFormat.SpacingM));
            int r0 = Math.Max(0, (int)MathF.Ceiling(MathF.Min(az, MathF.Min(bz, cz)) / (float)ChunkFormat.SpacingM));
            int r1 = Math.Min(n - 1, (int)MathF.Floor(MathF.Max(az, MathF.Max(bz, cz)) / (float)ChunkFormat.SpacingM));
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    float x = (float)(c * ChunkFormat.SpacingM), z = (float)(r * ChunkFormat.SpacingM);
                    float d0 = (bx - ax) * (z - az) - (bz - az) * (x - ax);
                    float d1 = (cx - bx) * (z - bz) - (cz - bz) * (x - bx);
                    float d2 = (ax - cx) * (z - cz) - (az - cz) * (x - cx);
                    if (area > 0 ? d0 >= 0 && d1 >= 0 && d2 >= 0 : d0 <= 0 && d1 <= 0 && d2 <= 0)
                        inside[r * n + c] = true;
                }
        }
        return (dist, inside);
    }

    /// <summary>The strip's membership per lattice vertex.</summary>
    public static bool[] Members(float[] roadDist, bool[] onRoad, float[] facadeDist, bool[] underBuilding)
    {
        var m = new bool[roadDist.Length];
        for (int k = 0; k < m.Length; k++)
            m[k] = !onRoad[k] && !underBuilding[k] && roadDist[k] + facadeDist[k] <= MaxGapM;
        return m;
    }

    /// <summary>
    /// The strip's slope histogram and steps on a ring of <paramref name="stride"/>: its vertices are every
    /// stride-th lattice vertex; a vertex's slope is its forward gradient (east and south) and a step is a
    /// ring edge between two strip vertices that rises more than <see cref="StepM"/>.
    /// </summary>
    public static Measure Of(float[] heights, bool[] strip, int stride)
    {
        int n = ChunkFormat.GridSize;
        var slope = new int[SlopeNames.Length];
        int cells = 0, edges = 0, steps = 0;
        float worst = 0;
        for (int r = 0; r + stride < n; r += stride)
            for (int c = 0; c + stride < n; c += stride)
            {
                int k = r * n + c;
                if (!strip[k]) continue;
                int ke = k + stride, ks = k + stride * n;
                float h = heights[k], he = heights[ke], hs = heights[ks];
                double gx = (he - h) / (stride * ChunkFormat.SpacingM), gz = (hs - h) / (stride * ChunkFormat.SpacingM);
                double deg = Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180.0 / Math.PI;
                int bin = 0;
                while (bin < SlopeBinsDeg.Length && deg >= SlopeBinsDeg[bin]) bin++;
                slope[bin]++;
                cells++;
                if (strip[ke]) { edges++; Step(he - h); }
                if (strip[ks]) { edges++; Step(hs - h); }
            }
        return new Measure(cells, slope, edges, steps, worst);

        void Step(float dh)
        {
            float a = MathF.Abs(dh);
            if (a > StepM) steps++;
            if (a > worst) worst = a;
        }
    }
}
