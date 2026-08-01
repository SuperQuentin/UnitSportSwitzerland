using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Builds water surfaces from the ground-cover raster, plus the mapped watercourses.
///
/// swissALTI3D already models lakes and rivers as flat surfaces at water level, so the
/// terrain height at a water cell *is* the water level. That means we can lay the surface
/// directly on the existing heights instead of shipping separate water geometry, and a
/// river automatically keeps its downstream gradient rather than being forced flat.
///
/// <para>
/// The raster alone only finds water wide enough to register on a 2 m lattice, which in
/// alpine terrain is almost none of it: every gully has a stream and not one of them is 2 m
/// across. <c>tlm_gewaesser_fliessgewaesser</c> maps them as lines — 465k watercourses, plus
/// the Valais <i>bisses</i> — and those are ribboned here so they share this surface's
/// material rather than being drawn as narrow blue roads.
/// </para>
/// </summary>
public static class WaterMeshBuilder
{
    public sealed record MeshData(Vector3[] Vertices, int[] Indices);

    /// <summary>Lifted just enough to sit clear of the lake bed without visible float.</summary>
    private const float SurfaceLift = 0.12f;

    /// <summary>
    /// Water is drawn at a fixed 4 m grid regardless of terrain LOD — it is flat, so it
    /// needs no detail, and a constant resolution avoids re-tessellating on LOD changes.
    /// </summary>
    private const int Stride = 2;

    public static MeshData? Build(ChunkGrid grid, byte[] cover, RoadTile? roads = null)
    {
        int last = ChunkFormat.GridSize - 1;
        int m = last / Stride + 1;
        float quad = (float)(Stride * ChunkFormat.SpacingM);

        var vertices = new List<Vector3>();
        var indices = new List<int>();
        // vertex index per lattice position, -1 until first used
        var lookup = new int[m * m];
        Array.Fill(lookup, -1);

        int VertexAt(int c, int r)
        {
            int key = r * m + c;
            if (lookup[key] >= 0) return lookup[key];
            int fc = c * Stride, fr = r * Stride;
            float y = (float)grid.HeightMetersAt(fc, fr) + SurfaceLift;
            lookup[key] = vertices.Count;
            vertices.Add(new Vector3(c * quad, y, r * quad));
            return lookup[key];
        }

        bool IsWater(int c, int r)
        {
            int fc = Math.Min(c * Stride, ChunkFormat.GridSize - 1);
            int fr = Math.Min(r * Stride, ChunkFormat.GridSize - 1);
            return (CoverClass)cover[fr * ChunkFormat.GridSize + fc] == CoverClass.Water;
        }

        for (int r = 0; r < m - 1; r++)
            for (int c = 0; c < m - 1; c++)
            {
                // all four corners must be water, so the surface stops at the bank
                if (!IsWater(c, r) || !IsWater(c + 1, r) || !IsWater(c, r + 1) || !IsWater(c + 1, r + 1))
                    continue;

                int v00 = VertexAt(c, r);
                int v10 = VertexAt(c + 1, r);
                int v01 = VertexAt(c, r + 1);
                int v11 = VertexAt(c + 1, r + 1);

                indices.Add(v00); indices.Add(v10); indices.Add(v01);
                indices.Add(v10); indices.Add(v11); indices.Add(v01);
            }

        if (roads != null)
            foreach (var seg in roads.Segments)
                if (RoadFormat.IsWatercourse(seg.Class))
                    AppendChannel(seg, cover, vertices, indices);

        return indices.Count == 0
            ? null
            : new MeshData(vertices.ToArray(), indices.ToArray());
    }

    /// <summary>
    /// How far either side of a channel vertex to look for already-mapped water, in cover cells.
    /// </summary>
    private const int WaterProbeCells = 3;

    /// <summary>
    /// Fraction of a channel's vertices that must sit in mapped water before the whole line is
    /// treated as a river the raster already draws.
    /// </summary>
    private const float MappedRiverShare = 0.35f;

    /// <summary>
    /// Ribbons one watercourse. Heights come straight from the segment, which the preprocessor
    /// already draped, so the channel follows its gorge instead of being flattened to a pond.
    ///
    /// <para>
    /// A dry gully (<see cref="RoadClass.DryChannel"/>) is skipped rather than drawn: TLM maps
    /// 186k of them and they carry water only in spate, so rendering them as water would put
    /// blue ribbons down every scree chute in the Alps.
    /// </para>
    ///
    /// <para>
    /// The line data contains <i>every</i> watercourse, the Rhône included — as a centreline like
    /// any other. Drawn naively that lays a 2.5 m creek down the middle of a 50 m river, and
    /// wherever the raster's river happens to thin out, the great river of the Valais appears to
    /// shrink to a ditch. So the raster wins: it maps everything wide enough to register on the
    /// 2 m lattice, and these lines exist only to supply what it is too coarse to see.
    /// </para>
    /// </summary>
    private static void AppendChannel(RoadSegment seg, byte[] cover,
        List<Vector3> vertices, List<int> indices)
    {
        if (seg.Class == RoadClass.DryChannel) return;

        int n = seg.PointCount;
        if (n < 2) return;

        var inMappedWater = new bool[n];
        int mapped = 0;
        for (int i = 0; i < n; i++)
        {
            inMappedWater[i] = NearMappedWater(cover, seg.Points[i * 3], seg.Points[i * 3 + 2]);
            if (inMappedWater[i]) mapped++;
        }

        // mostly inside a mapped river: this is that river, and the raster is already drawing it
        if (mapped >= n * MappedRiverShare) return;

        float half = Math.Max(seg.Width, 0.5f) * 0.5f;

        // emit each run of vertices that is outside mapped water, so a tributary stops cleanly
        // at the bank of the river it joins instead of running out into the middle of it
        int start = 0;
        while (start < n)
        {
            while (start < n && inMappedWater[start]) start++;
            int end = start;
            while (end < n && !inMappedWater[end]) end++;

            if (end - start >= 2) AppendRun(seg, start, end, half, vertices, indices);
            start = end;
        }
    }

    private static void AppendRun(RoadSegment seg, int from, int to, float half,
        List<Vector3> vertices, List<int> indices)
    {
        int n = seg.PointCount;
        int baseIndex = vertices.Count;

        for (int i = from; i < to; i++)
        {
            var p = Point(seg, i);

            Vector3 forward;
            if (i == 0) forward = Point(seg, 1) - Point(seg, 0);
            else if (i == n - 1) forward = Point(seg, n - 1) - Point(seg, n - 2);
            else forward = Point(seg, i + 1) - Point(seg, i - 1);

            forward.Y = 0;
            if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
            forward = forward.Normalized();
            var lateral = new Vector3(-forward.Z, 0, forward.X) * half;

            vertices.Add(p - lateral);
            vertices.Add(p + lateral);
        }

        for (int i = 0; i < to - from - 1; i++)
        {
            int a = baseIndex + i * 2;
            indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
            indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
        }
    }

    /// <summary>
    /// True when the cover raster maps water at or near this tile-local position. The probe is
    /// widened by a few cells so a channel running just along a river bank counts as inside it —
    /// a centreline is rarely exactly where the raster's edge falls.
    /// </summary>
    private static bool NearMappedWater(byte[] cover, float localX, float localZ)
    {
        int col = (int)MathF.Round(localX / (float)ChunkFormat.SpacingM);
        int row = (int)MathF.Round(localZ / (float)ChunkFormat.SpacingM);

        for (int dr = -WaterProbeCells; dr <= WaterProbeCells; dr++)
        for (int dc = -WaterProbeCells; dc <= WaterProbeCells; dc++)
        {
            int r = row + dr, c = col + dc;
            if (r < 0 || c < 0 || r >= ChunkFormat.GridSize || c >= ChunkFormat.GridSize) continue;
            if ((CoverClass)cover[r * ChunkFormat.GridSize + c] == CoverClass.Water) return true;
        }
        return false;
    }

    private static Vector3 Point(RoadSegment seg, int i) =>
        new(seg.Points[i * 3], seg.Points[i * 3 + 1], seg.Points[i * 3 + 2]);
}
