using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Turns road polylines into flat ribbon meshes. Runs on worker threads, so it only
/// produces arrays. Colour is baked per vertex so every road in a tile shares one
/// material and one draw call.
/// </summary>
public static class RoadMeshBuilder
{
    public sealed record MeshData(Vector3[] Vertices, Color[] Colors, Vector2[] Uvs, Vector2[] Uv2s, int[] Indices);


    public static MeshData? Build(RoadTile tile, ChunkGrid? grid = null)
    {
        int quadCount = 0;
        foreach (var s in tile.Segments)
            quadCount += Math.Max(0, s.PointCount - 1);
        if (quadCount == 0 && tile.Junctions.Count == 0) return null;

        var vertices = new List<Vector3>(quadCount * 4);
        var colors = new List<Color>(quadCount * 4);
        // uv  = (metres along the line, lateral position across it in [-1,1])
        // uv2 = (surface style id, unused) — see MarkingStyle
        var uvs = new List<Vector2>(quadCount * 4);
        var uv2s = new List<Vector2>(quadCount * 4);
        var indices = new List<int>(quadCount * 6);

        foreach (var junction in tile.Junctions)
            AppendJunction(junction, vertices, colors, uvs, uv2s, indices);

        foreach (var seg in tile.Segments)
        {
            AppendSegment(seg, vertices, colors, uvs, uv2s, indices);
            if (seg.Class == RoadClass.Railway)
                AppendRails(seg, vertices, colors, uvs, uv2s, indices);
            if ((seg.Flags & RoadFlags.Tunnel) != 0)
                AppendTunnelBore(seg, tile.Id, grid, vertices, colors, uvs, uv2s, indices);
            if ((seg.Flags & RoadFlags.Bridge) != 0)
                AppendBridgeStructure(seg, tile.Id, grid, vertices, colors, uvs, uv2s, indices);
        }

        return vertices.Count == 0
            ? null
            : new MeshData(vertices.ToArray(), colors.ToArray(), uvs.ToArray(), uv2s.ToArray(), indices.ToArray());
    }

    /// <summary>
    /// Draws the paved area where roads meet, pre-triangulated by <c>tools/RoadGen</c>.
    ///
    /// <para>
    /// The carriageways arriving here have been trimmed back to this polygon's edge, so nothing
    /// overlaps and the cap is what fills the middle. It carries no lane markings on purpose:
    /// the whole reason lane lines used to cross each other in the middle of an intersection is
    /// that the ribbons ran straight through it, and painting the cap would put them back.
    /// </para>
    ///
    /// <para>
    /// Present only in format v2 tiles. A region built before the rewrite simply has none, and
    /// renders exactly as it did.
    /// </para>
    /// </summary>
    private static void AppendJunction(RoadJunction junction, List<Vector3> vertices,
        List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = junction.VertexCount;
        if (n < 3 || junction.Indices.Length < 3) return;

        // a bridge deck's junction has to ride at the same lift as the deck it sits on, or the
        // cap sinks into the soffit
        float lift = junction.Layer > 0 ? BridgeLift : 0f;

        // the cap borrows the dominant arm's tint via a stand-in segment, so a junction between
        // farm tracks stays dirt-coloured instead of turning into a slab of asphalt
        var probe = new RoadSegment
        {
            Class = junction.Class,
            Surface = junction.Class is RoadClass.Track or RoadClass.Path
                ? RoadSurface.Natural : RoadSurface.Paved,
            Flags = RoadFlags.None,
            Width = 0,
            Points = Array.Empty<float>(),
        };
        var color = ColorFor(probe).SrgbToLinear();

        int baseIndex = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(new Vector3(
                junction.Vertices[i * 3],
                junction.Vertices[i * 3 + 1] + lift,
                junction.Vertices[i * 3 + 2]));
            colors.Add(color);
            uvs.Add(new Vector2(0f, 0f));
            uv2s.Add(new Vector2((float)MarkingStyle.None, 0f));
        }

        for (int i = 0; i + 2 < junction.Indices.Length; i += 3)
        {
            indices.Add(baseIndex + junction.Indices[i]);
            indices.Add(baseIndex + junction.Indices[i + 1]);
            indices.Add(baseIndex + junction.Indices[i + 2]);
        }
    }

    private static void AppendSegment(RoadSegment seg, List<Vector3> vertices,
        List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2) return;

        var pts = new Vector3[n];
        for (int i = 0; i < n; i++)
            pts[i] = new Vector3(seg.Points[i * 3], seg.Points[i * 3 + 1], seg.Points[i * 3 + 2]);

        float lift = (seg.Flags & RoadFlags.Bridge) != 0 ? BridgeLift : 0f;
        float half = seg.Width * 0.5f;
        // Colours below are authored in sRGB (what they should look like on screen).
        // Shader uniforms marked ": source_color" get this conversion automatically, but
        // raw vertex colours do not — without it dark asphalt renders washed-out grey.
        var color = ColorFor(seg).SrgbToLinear();

        // Per-vertex offset direction = bisector of adjacent segment directions, so the
        // ribbon stays continuous through corners instead of tearing at each joint.
        int baseIndex = vertices.Count;
        float style = (float)MarkingStyleFor(seg);
        float travelled = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 forward;
            if (i == 0) forward = pts[1] - pts[0];
            else if (i == n - 1) forward = pts[n - 1] - pts[n - 2];
            else forward = (pts[i + 1] - pts[i - 1]);

            forward.Y = 0;
            if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
            forward = forward.Normalized();
            var side = new Vector3(-forward.Z, 0, forward.X) * half;

            if (i > 0) travelled += pts[i].DistanceTo(pts[i - 1]);

            var p = pts[i] + new Vector3(0, lift, 0);
            vertices.Add(p - side);
            vertices.Add(p + side);
            colors.Add(color);
            colors.Add(color);
            uvs.Add(new Vector2(travelled, -1f));
            uvs.Add(new Vector2(travelled, 1f));
            uv2s.Add(new Vector2(style, 0f));
            uv2s.Add(new Vector2(style, 0f));
        }

        for (int i = 0; i < n - 1; i++)
        {
            int a = baseIndex + i * 2;
            indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
            indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
        }
    }

    /// <summary>
    /// Surface pattern id baked into uv.y and drawn by the shader.
    ///
    /// Switzerland publishes no lane-marking dataset, so these are inferred from what
    /// swissTLM3D does record: width class, surface, and whether the carriageway is
    /// direction-separated. A divided carriageway carries no centre line because both
    /// sides run the same way.
    /// </summary>
    private enum MarkingStyle
    {
        None = 0,
        CentreDashed = 1,  // ordinary two-way road
        EdgeOnly = 2,      // divided carriageway: edge lines, no centre
        Motorway = 3,      // edge lines plus a dashed lane divider
        RailBallast = 4,   // sleeper stripes
        RailSteel = 5,     // the rails themselves
    }

    private static MarkingStyle MarkingStyleFor(RoadSegment seg)
    {
        if (seg.Class == RoadClass.Railway) return MarkingStyle.RailBallast;
        // unpaved surfaces and anything narrower than a lane are never marked
        if (seg.Surface != RoadSurface.Paved) return MarkingStyle.None;
        if (seg.Class >= RoadClass.Track) return MarkingStyle.None;

        if (seg.Class is RoadClass.Motorway or RoadClass.Expressway) return MarkingStyle.Motorway;
        if ((seg.Flags & RoadFlags.Divided) != 0) return MarkingStyle.EdgeOnly;
        // below ~4 m Swiss roads are generally unmarked
        return seg.Class <= RoadClass.Minor ? MarkingStyle.CentreDashed : MarkingStyle.None;
    }

    /// <summary>
    /// Lays the running rails on top of the ballast ribbon. Sleepers are drawn by the
    /// shader as stripes rather than modelled — at real 0.65 m spacing they would add
    /// tens of thousands of triangles per kilometre for detail a few pixels wide.
    /// </summary>
    private static void AppendRails(RoadSegment seg, List<Vector3> vertices,
        List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2 || (seg.Flags & RoadFlags.Funicular) != 0) return;

        float gauge = RoadFormat.RailGauge(seg.Flags);
        float trackOffset = RoadFormat.TrackOffset(seg.Flags);
        var centres = trackOffset > 0f
            ? new[] { -trackOffset, trackOffset }
            : new[] { 0f };

        var railColour = (seg.Flags & RoadFlags.Disused) != 0
            ? new Color(0.38f, 0.30f, 0.24f)   // rusted
            : new Color(0.55f, 0.55f, 0.58f);  // polished steel
        const float RailHeight = 0.18f;
        const float RailHalfWidth = 0.075f;

        foreach (float centre in centres)
            foreach (int side in new[] { -1, 1 })
            {
                float offset = centre + side * gauge * 0.5f;
                int baseIndex = vertices.Count;

                for (int i = 0; i < n; i++)
                {
                    var p = Point(seg, i);
                    Vector3 forward = i == 0 ? Point(seg, 1) - Point(seg, 0)
                        : i == n - 1 ? Point(seg, n - 1) - Point(seg, n - 2)
                        : Point(seg, i + 1) - Point(seg, i - 1);
                    forward.Y = 0;
                    if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
                    forward = forward.Normalized();
                    var lateral = new Vector3(-forward.Z, 0, forward.X);

                    var mid = p + lateral * offset + new Vector3(0, RailHeight, 0);
                    vertices.Add(mid - lateral * RailHalfWidth);
                    vertices.Add(mid + lateral * RailHalfWidth);
                    var linear = railColour.SrgbToLinear();
                    colors.Add(linear); colors.Add(linear);
                    uvs.Add(new Vector2(0f, -1f));
                    uvs.Add(new Vector2(0f, 1f));
                    uv2s.Add(new Vector2((float)MarkingStyle.RailSteel, 0f));
                    uv2s.Add(new Vector2((float)MarkingStyle.RailSteel, 0f));
                }

                for (int i = 0; i < n - 1; i++)
                {
                    int a = baseIndex + i * 2;
                    indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
                    indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
                }
            }
    }

    private static readonly Color DeckColor = new Color(0.44f, 0.43f, 0.41f);   // concrete fascia
    private static readonly Color SoffitColor = new Color(0.30f, 0.29f, 0.28f); // shaded underside
    private static readonly Color ParapetColor = new Color(0.52f, 0.51f, 0.49f);
    private static readonly Color PierColor = new Color(0.38f, 0.37f, 0.36f);

    /// <summary>
    /// Gives a bridge deck real thickness, edge parapets, and piers down to the ground.
    /// Without this a bridge is a ribbon hanging in mid-air with nothing underneath.
    /// <paramref name="grid"/> supplies the terrain height for pier footings; when it is
    /// unavailable the piers are simply skipped.
    /// </summary>
    private static void AppendBridgeStructure(RoadSegment seg, TileId tile, ChunkGrid? grid,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2) return;

        float half = seg.Width * 0.5f;
        float thickness = seg.Class <= RoadClass.Major ? 1.2f : 0.7f;
        const float ParapetHeight = 1.0f;

        // deck edge rails, computed the same way as the road ribbon so they line up
        var left = new Vector3[n];
        var right = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var p = Point(seg, i) + new Vector3(0, BridgeLift, 0);
            Vector3 forward = i == 0 ? Point(seg, 1) - Point(seg, 0)
                : i == n - 1 ? Point(seg, n - 1) - Point(seg, n - 2)
                : Point(seg, i + 1) - Point(seg, i - 1);
            forward.Y = 0;
            if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
            forward = forward.Normalized();
            var side = new Vector3(-forward.Z, 0, forward.X) * half;
            left[i] = p - side;
            right[i] = p + side;
        }

        var down = new Vector3(0, -thickness, 0);
        var up = new Vector3(0, ParapetHeight, 0);

        for (int i = 0; i < n - 1; i++)
        {
            // soffit (underside), seen from below
            AddQuad(vertices, colors, uvs, uv2s, indices, SoffitColor,
                left[i] + down, right[i] + down, left[i + 1] + down, right[i + 1] + down);
            // fascia beams down each side
            AddQuad(vertices, colors, uvs, uv2s, indices, DeckColor,
                left[i], left[i] + down, left[i + 1], left[i + 1] + down);
            AddQuad(vertices, colors, uvs, uv2s, indices, DeckColor,
                right[i], right[i] + down, right[i + 1], right[i + 1] + down);
            // parapets
            AddQuad(vertices, colors, uvs, uv2s, indices, ParapetColor,
                left[i], left[i] + up, left[i + 1], left[i + 1] + up);
            AddQuad(vertices, colors, uvs, uv2s, indices, ParapetColor,
                right[i], right[i] + up, right[i + 1], right[i + 1] + up);
        }

        if (grid == null) return;
        AppendPiers(seg, tile, grid, left, right, thickness, vertices, colors, uvs, uv2s, indices);
    }

    /// <summary>
    /// Small lift off the surveyed deck height, purely to stop the carriageway z-fighting
    /// with the terrain at abutments where the gap goes to zero.
    /// </summary>
    private const float BridgeLift = 0.15f;

    private const float PierSpacing = 25f;
    private const float MinPierGap = 4f;

    /// <summary>
    /// Above this the crossing is almost certainly a single span (suspension or arch) —
    /// TLM3D does not record bridge type, and stamping columns under a footbridge over a
    /// 130 m gorge looks far worse than leaving the deck unsupported.
    /// </summary>
    private const float MaxPierHeight = 35f;

    private static void AppendPiers(RoadSegment seg, TileId tile, ChunkGrid grid,
        Vector3[] left, Vector3[] right, float thickness,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = seg.PointCount;
        float sinceLast = PierSpacing; // place one at the first eligible point
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sinceLast += left[i].DistanceTo(left[i - 1]);
            if (sinceLast < PierSpacing) continue;

            var centre = (left[i] + right[i]) * 0.5f;
            double e = tile.MinE + centre.X;
            double nn = tile.MaxN - centre.Z;
            float ground = (float)grid.SampleHeight(e, nn);

            float deckBottom = centre.Y - thickness;
            float gap = deckBottom - ground;
            if (gap < MinPierGap || gap > MaxPierHeight) continue;

            sinceLast = 0;
            float w = Math.Min(2.5f, seg.Width * 0.35f);
            var axis = (right[i] - left[i]).Normalized() * w * 0.5f;
            var perp = new Vector3(-axis.Z, 0, axis.X);

            // four sides of a simple column, sunk slightly into the ground
            var top = new Vector3(centre.X, deckBottom, centre.Z);
            var bot = new Vector3(centre.X, ground - 0.5f, centre.Z);
            for (int k = 0; k < 4; k++)
            {
                var o1 = k switch { 0 => axis + perp, 1 => axis - perp, 2 => -axis - perp, _ => -axis + perp };
                var o2 = k switch { 0 => axis - perp, 1 => -axis - perp, 2 => -axis + perp, _ => axis + perp };
                AddQuad(vertices, colors, uvs, uv2s, indices, PierColor, top + o1, bot + o1, top + o2, bot + o2);
            }
        }
    }

    /// <summary>Adds a quad from four corners (a,b / c,d form the two edges).</summary>
    private static void AddQuad(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices, Color color, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int i0 = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        var linear = color.SrgbToLinear();
        // structural geometry carries no surface markings
        for (int k = 0; k < 4; k++) { colors.Add(linear); uvs.Add(Vector2.Zero); uv2s.Add(Vector2.Zero); }
        // cull_disabled, so winding only needs to be consistent
        indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
        indices.Add(i0 + 1); indices.Add(i0 + 3); indices.Add(i0 + 2);
    }

    /// <summary>
    /// Arch cross-section of a tunnel bore as (lateral, up) fractions of half-width and
    /// clear height. Deliberately few segments — a faceted bore is the right look here.
    /// </summary>
    private static readonly (float X, float Y)[] BoreProfile =
    {
        (-1.00f, 0.00f),
        (-1.00f, 0.35f),
        (-0.92f, 0.62f),
        (-0.55f, 0.90f),
        (0.00f, 1.00f),
        (0.55f, 0.90f),
        (0.92f, 0.62f),
        (1.00f, 0.35f),
        (1.00f, 0.00f),
    };

    /// <summary>
    /// Extrudes the arch profile along a tunnel centreline. The road ribbon already
    /// provides the carriageway, so this adds only walls and crown. Rendered with
    /// cull_disabled, so the faces read correctly from inside the bore.
    /// </summary>
    /// <summary>
    /// Distance the bore is pushed out past each end of the tunnel centreline. Without
    /// it the arch begins exactly where the rock begins, so from outside the road simply
    /// stops at a notch in the hillside with nothing to drive into.
    /// </summary>
    private const float PortalExtension = 5f;

    /// <summary>How far the headwall face stands proud of the bore opening.</summary>
    private const float HeadwallMargin = 1.15f;

    private static readonly Color PortalColor = new Color(0.50f, 0.49f, 0.47f);

    /// <summary>
    /// Extrudes the arch profile along a tunnel centreline, extended past both ends and
    /// capped with a headwall so the mouth reads as a built portal rather than a hole in
    /// the dirt. The road ribbon already provides the carriageway, so this adds walls,
    /// crown and facing only. Rendered with cull_disabled, so it reads from inside too.
    /// </summary>
    private static void AppendTunnelBore(RoadSegment seg, TileId tile, ChunkGrid? grid,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices)
    {
        int n = seg.PointCount;
        if (n < 2) return;

        // extend the centreline outward at both ends so the arch breaks the surface
        var path = new List<Vector3>(n + 2);
        var firstDir = (Point(seg, 1) - Point(seg, 0)) with { Y = 0 };
        var lastDir = (Point(seg, n - 1) - Point(seg, n - 2)) with { Y = 0 };
        if (firstDir.LengthSquared() > 1e-8f)
            path.Add(Point(seg, 0) - firstDir.Normalized() * PortalExtension);
        for (int i = 0; i < n; i++) path.Add(Point(seg, i));
        if (lastDir.LengthSquared() > 1e-8f)
            path.Add(Point(seg, n - 1) + lastDir.Normalized() * PortalExtension);

        int m = path.Count;
        float halfWidth = RoadFormat.TunnelWidth(seg.Class) * 0.5f;

        // Fit the bore to the cover that actually exists. The nominal clear height is a
        // guess by road class; an underpass beneath a rail embankment may have only two
        // or three metres over it, and a bore taller than its own cover pokes out through
        // the ground above — which is exactly what a tunnel must never do.
        float height = RoadFormat.TunnelHeight(seg.Class);
        float cover = MinCover(seg, tile, grid);
        if (cover > 0f)
            height = Mathf.Clamp(cover - 0.4f, 2.4f, height);
        int ring = BoreProfile.Length;
        int baseIndex = vertices.Count;

        for (int i = 0; i < m; i++)
        {
            var p = path[i];
            Vector3 forward = i == 0 ? path[1] - path[0]
                : i == m - 1 ? path[m - 1] - path[m - 2]
                : path[i + 1] - path[i - 1];
            forward.Y = 0;
            if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
            forward = forward.Normalized();
            var side = new Vector3(-forward.Z, 0, forward.X);

            for (int k = 0; k < ring; k++)
            {
                var (px, py) = BoreProfile[k];
                vertices.Add(p + side * (px * halfWidth) + new Vector3(0, py * height, 0));
                // crown darker than the walls so the bore reads as depth, not a flat band
                float shade = Mathf.Lerp(0.34f, 0.16f, py);
                colors.Add(new Color(shade, shade * 0.97f, shade * 0.92f).SrgbToLinear());
                uvs.Add(Vector2.Zero);
                uv2s.Add(Vector2.Zero);
            }
        }

        for (int i = 0; i < m - 1; i++)
            for (int k = 0; k < ring - 1; k++)
            {
                int a = baseIndex + i * ring + k;
                int b = a + 1;
                int c = a + ring;
                int d = c + 1;
                indices.Add(a); indices.Add(c); indices.Add(b);
                indices.Add(b); indices.Add(c); indices.Add(d);
            }

        AppendHeadwall(path[0], path[1], halfWidth, height, tile, grid,
            vertices, colors, uvs, uv2s, indices);
        AppendHeadwall(path[m - 1], path[m - 2], halfWidth, height, tile, grid,
            vertices, colors, uvs, uv2s, indices);
    }

    /// <summary>
    /// Smallest gap between the carriageway and the ground above it, over the segment's
    /// interior. Returns 0 when no terrain data is available.
    /// </summary>
    private static float MinCover(RoadSegment seg, TileId tile, ChunkGrid? grid)
    {
        if (grid == null) return 0f;
        float min = float.MaxValue;
        int n = seg.PointCount;
        for (int i = 0; i < n; i++)
        {
            var p = Point(seg, i);
            double e = tile.MinE + p.X;
            double nn = tile.MaxN - p.Z;
            min = Mathf.Min(min, (float)grid.SampleHeight(e, nn) - p.Y);
        }
        return min == float.MaxValue ? 0f : Mathf.Max(min, 0f);
    }

    private static float TerrainAbove(Vector3 local, TileId tile, ChunkGrid? grid)
    {
        if (grid == null) return 0f;
        return (float)grid.SampleHeight(tile.MinE + local.X, tile.MaxN - local.Z) - local.Y;
    }

    /// <summary>
    /// Builds the portal face at one mouth: a broad wall standing in the hillside with the
    /// bore's arch cut out of it, plus wing walls raking back into the slope.
    ///
    /// This is what joins the tunnel to the terrain. Carving alone leaves the ground mesh
    /// with raw open edges and the bore floating inside the gap; the wall spans wider and
    /// taller than the carved opening, so those edges end up behind it and the mouth reads
    /// as a built structure set into the hill.
    /// </summary>
    private static void AppendHeadwall(Vector3 mouth, Vector3 inward, float halfWidth, float height,
        TileId tile, ChunkGrid? grid,
        List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices)
    {
        var forward = (inward - mouth) with { Y = 0 };
        if (forward.LengthSquared() < 1e-8f) return;
        forward = forward.Normalized();
        var side = new Vector3(-forward.Z, 0, forward.X);

        // The face has to cover the carved opening, but must not stand proud of the
        // ground it is set into — a wall towering over a low rail embankment reads as a
        // monolith dropped on a field. Clamp it to the cover just inside the mouth.
        float faceHalf = halfWidth * 2.1f;
        float faceTop = height * 1.7f;
        float aboveMouth = TerrainAbove(inward, tile, grid);
        if (aboveMouth > 0f)
            faceTop = Mathf.Clamp(aboveMouth + 0.4f, height * 1.02f, faceTop);
        float faceBottom = -3.0f;   // sunk below the road so no gap opens under it

        int ring = BoreProfile.Length;
        int baseIndex = vertices.Count;
        var linear = PortalColor.SrgbToLinear();
        var shadow = (PortalColor * 0.72f).SrgbToLinear();

        // Pair each arch vertex with a point on the enclosing rectangle, found by pushing
        // outward from the arch centre until the rectangle bound is met. Connecting the
        // two rings fills the wall around the opening.
        for (int k = 0; k < ring; k++)
        {
            var (px, py) = BoreProfile[k];
            var inner = mouth + side * (px * halfWidth) + new Vector3(0, py * height, 0);

            float dx = px, dy = py - 0.25f;   // splay about the springing line
            if (Mathf.Abs(dx) < 1e-4f && Mathf.Abs(dy) < 1e-4f) dy = 1f;
            float scale = Mathf.Min(
                Mathf.Abs(dx) < 1e-4f ? float.MaxValue : faceHalf / (Mathf.Abs(dx) * halfWidth),
                dy > 0 ? faceTop / (dy * height) : Mathf.Abs(faceBottom) / (Mathf.Abs(dy) * height));
            var outer = mouth + side * (dx * halfWidth * scale)
                        + new Vector3(0, 0.25f * height + dy * height * scale, 0);

            vertices.Add(inner); vertices.Add(outer);
            colors.Add(linear); colors.Add(linear);
            uvs.Add(Vector2.Zero); uvs.Add(Vector2.Zero);
            uv2s.Add(Vector2.Zero); uv2s.Add(Vector2.Zero);
        }

        for (int k = 0; k < ring - 1; k++)
        {
            int a = baseIndex + k * 2;
            indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
            indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
        }

        // No wing walls: the sides of the cut are now lined by TerrainMeshBuilder from the
        // hole mask, which shares the terrain's own grid. Slabs generated here from the
        // road centreline could never meet a hole quantised to that lattice.
    }

    private static Vector3 Point(RoadSegment s, int i) =>
        new(s.Points[i * 3], s.Points[i * 3 + 1], s.Points[i * 3 + 2]);

    /// <summary>
    /// PS1-palette colour per road kind. Surface (paved vs natural) dominates, because
    /// that is what actually reads at a distance; class then shifts the tone.
    /// </summary>
    public static Color ColorFor(RoadSegment seg)
    {
        if (seg.Class == RoadClass.Railway)
            return (seg.Flags & RoadFlags.Funicular) != 0
                ? new Color(0.34f, 0.32f, 0.30f)   // concrete funicular bed
                : new Color(0.36f, 0.32f, 0.29f);  // crushed-stone ballast

        if ((seg.Flags & RoadFlags.Stairs) != 0)
            return new Color(0.55f, 0.52f, 0.48f);

        bool natural = seg.Surface == RoadSurface.Natural;

        // hiking trails read as trodden earth whatever their nominal surface
        if ((seg.Flags & (RoadFlags.Hiking | RoadFlags.MountainHiking)) != 0 && seg.Class >= RoadClass.Track)
            return natural ? new Color(0.60f, 0.50f, 0.36f) : new Color(0.55f, 0.50f, 0.43f);

        if (natural)
            return seg.Class switch
            {
                RoadClass.Path => new Color(0.62f, 0.53f, 0.39f),   // pale trodden dirt
                RoadClass.Track => new Color(0.52f, 0.44f, 0.32f),  // gravel/farm track
                _ => new Color(0.46f, 0.40f, 0.31f),                // graded dirt road
            };

        // neutral-to-warm greys; a blue bias here reads as purple once dithered
        return seg.Class switch
        {
            RoadClass.Motorway or RoadClass.Expressway => new Color(0.26f, 0.26f, 0.25f),
            RoadClass.Ramp or RoadClass.Major => new Color(0.30f, 0.30f, 0.29f),
            RoadClass.Road or RoadClass.Minor => new Color(0.34f, 0.34f, 0.33f),
            RoadClass.Path => new Color(0.46f, 0.45f, 0.43f),
            _ => new Color(0.37f, 0.37f, 0.36f),
        };
    }
}
