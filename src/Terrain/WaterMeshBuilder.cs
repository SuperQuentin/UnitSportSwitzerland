using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Builds water surfaces from the tile's still water layer (<see cref="WaterLayer"/>, #299), plus
/// the mapped watercourses.
///
/// On a legacy tile the layer comes from the cover raster: swissALTI3D models lakes and rivers
/// as flat surfaces at water level, so the terrain height at a water cell *is* the water level
/// and a river keeps its downstream gradient. With a source layer (the fixture lake, #298's
/// lake beds) the level is its own and the terrain under it is the bed. Either way the surface
/// is its own mesh with no collision, displaced by the waves in the shader.
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
    /// <param name="Uvs">x: the wave scale (0..1) the shader multiplies the waves by; 0 on channels.</param>
    public sealed record MeshData(Vector3[] Vertices, Vector2[] Uvs, int[] Indices);

    /// <summary>
    /// Waves reach this high and this far sideways at most (the gamey sum plus its chop), so the
    /// mesh's bounds are grown by it: culling must not drop a crest raised above the still box.
    /// </summary>
    public const float WaveMargin = 3f;

    /// <summary>
    /// The water surface of a tile from its still water layer (#299), plus the mapped
    /// watercourses. A quad wherever all four corners of a lattice square are wet, its vertices on
    /// the layer's samples (so a vertex and <see cref="ChunkManager.TryGetWaterLevel"/> agree
    /// exactly) at the still level, with the wave scale in UV.x for the shader's displacement.
    /// LOD: every 2 m sample where the terrain is drawn at stride 1 or 2 (the rings round the
    /// camera, where the waves are looked at), every second one (4 m) further out.
    /// </summary>
    /// <param name="detail">The visual style's mesh detail; only <see cref="Styles.MeshDetail.Low"/> exists so far.</param>
    public static MeshData? Build(WaterLayer? layer, RoadTile? roads, int terrainStride,
        Styles.MeshDetail detail = Styles.MeshDetail.Low)
    {
        int step = terrainStride <= 2 ? 1 : 2;
        const int m = WaterLayer.Size;
        const float quad = WaterLayer.Stride;

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();

        if (layer != null)
        {
            // vertex index per lattice position, -1 until first used
            var lookup = new int[m * m];
            Array.Fill(lookup, -1);

            int VertexAt(int c, int r)
            {
                int key = r * m + c;
                if (lookup[key] >= 0) return lookup[key];
                lookup[key] = vertices.Count;
                vertices.Add(new Vector3(c * quad, layer.LevelAt(c, r), r * quad));
                uvs.Add(new Vector2(layer.ScaleAt(c, r), 0f));
                return lookup[key];
            }

            for (int r = 0; r + step < m; r += step)
                for (int c = 0; c + step < m; c += step)
                {
                    // all four corners must be water, so the surface stops at the bank
                    if (!layer.IsWet(c, r) || !layer.IsWet(c + step, r) || !layer.IsWet(c, r + step) || !layer.IsWet(c + step, r + step))
                        continue;

                    int v00 = VertexAt(c, r);
                    int v10 = VertexAt(c + step, r);
                    int v01 = VertexAt(c, r + step);
                    int v11 = VertexAt(c + step, r + step);

                    indices.Add(v00); indices.Add(v10); indices.Add(v01);
                    indices.Add(v10); indices.Add(v11); indices.Add(v01);
                }
        }

        if (roads != null)
            foreach (var seg in roads.Segments)
                if (RoadFormat.IsWatercourse(seg.Class))
                    AppendChannel(seg, layer, vertices, uvs, indices);

        return indices.Count == 0
            ? null
            : new MeshData(vertices.ToArray(), uvs.ToArray(), indices.ToArray());
    }

    /// <summary>
    /// How far either side of a channel vertex to look for already-mapped water, in water samples (2 m).
    /// </summary>
    private const int WaterProbeSamples = 2;

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
    private static void AppendChannel(RoadSegment seg, WaterLayer? layer,
        List<Vector3> vertices, List<Vector2> uvs, List<int> indices)
    {
        if (seg.Class == RoadClass.DryChannel) return;

        int n = seg.PointCount;
        if (n < 2) return;

        var inMappedWater = new bool[n];
        int mapped = 0;
        for (int i = 0; i < n; i++)
        {
            inMappedWater[i] = layer != null && NearMappedWater(layer, seg.Points[i * 3], seg.Points[i * 3 + 2]);
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

            if (end - start >= 2) AppendRun(seg, start, end, half, vertices, uvs, indices);
            start = end;
        }
    }

    private static void AppendRun(RoadSegment seg, int from, int to, float half,
        List<Vector3> vertices, List<Vector2> uvs, List<int> indices)
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
            // a stream is a ribbon draped down its gorge: no waves
            uvs.Add(Vector2.Zero);
            uvs.Add(Vector2.Zero);
        }

        for (int i = 0; i < to - from - 1; i++)
        {
            int a = baseIndex + i * 2;
            indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
            indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
        }
    }

    /// <summary>
    /// True when the water layer has water at or near this tile-local position. The probe is
    /// widened by a few metres so a channel running just along a river bank counts as inside it —
    /// a centreline is rarely exactly where the raster's edge falls.
    /// </summary>
    private static bool NearMappedWater(WaterLayer layer, float localX, float localZ)
    {
        int col = (int)MathF.Round(localX / WaterLayer.Stride);
        int row = (int)MathF.Round(localZ / WaterLayer.Stride);

        for (int dr = -WaterProbeSamples; dr <= WaterProbeSamples; dr++)
        for (int dc = -WaterProbeSamples; dc <= WaterProbeSamples; dc++)
        {
            int r = row + dr, c = col + dc;
            if ((uint)r >= WaterLayer.Size || (uint)c >= WaterLayer.Size) continue;
            if (layer.IsWet(c, r)) return true;
        }
        return false;
    }

    private static Vector3 Point(RoadSegment seg, int i) =>
        new(seg.Points[i * 3], seg.Points[i * 3 + 1], seg.Points[i * 3 + 2]);
}
