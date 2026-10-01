using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Builds mesh/collision arrays from a chunk grid. Everything here is pure C# arrays and
/// worker-thread safe; Godot resources (ArrayMesh, shapes) are created by the caller on
/// the main thread. Local origin is the tile's NW corner: +x east, +z south, y altitude.
/// </summary>
public static partial class TerrainMeshBuilder
{
    public sealed record MeshData(Vector3[] Vertices, Color[] Colors, int[] Indices);

    /// <summary>
    /// Indexed grid mesh at the given stride with skirts on all four edges (skirts hide
    /// cracks at LOD-ring transitions; same-LOD tile seams are exact by construction).
    ///
    /// <para>
    /// <paramref name="blendedHeights"/>, when given, is a full-resolution height array to use
    /// instead of the grid's own. The tile build does not use it: it blends roads into an
    /// existing surface with <see cref="PatchSurface"/>, which only touches corridor cells.
    /// </para>
    /// </summary>
    public static MeshData BuildSurface(ChunkGrid grid, int stride, IReadOnlySet<int>? holes = null,
        byte[]? cover = null, float[]? blendedHeights = null,
        IReadOnlyList<RoadMeshBuilder.TunnelPortal>? portals = null) =>
        FinishSurface(BuildSurfaceCore(grid, stride, holes, cover, blendedHeights), grid, stride, holes, portals);

    /// <summary>
    /// The grid and its skirts, without the cut walls. Kept separately by the tile build so the
    /// road-blended version can be made by <see cref="PatchSurface"/> — moving the few vertices
    /// under road corridors — instead of rebuilding a million vertices a second time.
    /// </summary>
    /// <param name="detail">The visual style's mesh detail; only <see cref="Styles.MeshDetail.Low"/> exists so far.</param>
    public static MeshData BuildSurfaceCore(ChunkGrid grid, int stride, IReadOnlySet<int>? holes = null,
        byte[]? cover = null, float[]? blendedHeights = null, Styles.MeshDetail detail = Styles.MeshDetail.Low)
    {
        int last = ChunkFormat.GridSize - 1;      // 1000
        if (last % stride != 0)
            throw new ArgumentException($"Stride {stride} must divide {last}");
        // A decimated grid can serve any render coarser than itself, and nothing finer — asking
        // for stride 4 from a stride-10 tile would silently repeat every third vertex.
        if (stride % grid.Stride != 0)
            throw new ArgumentException(
                $"Cannot render {grid.Id} at stride {stride} from a stride-{grid.Stride} grid");
        int m = last / stride + 1;                // vertices per side
        float quad = (float)(stride * ChunkFormat.SpacingM);
        float skirtDepth = 2f * quad;

        var vertices = new Vector3[m * m + 4 * m];
        var colors = new Color[m * m + 4 * m];
        var indices = new int[(m - 1) * (m - 1) * 6 + 4 * (m - 1) * 6];

        for (int r = 0; r < m; r++)
            for (int c = 0; c < m; c++)
            {
                int fc = c * stride, fr = r * stride;
                float alt = blendedHeights != null
                    ? blendedHeights[fr * ChunkFormat.GridSize + fc]
                    : (float)grid.HeightMetersAt(fc, fr);
                vertices[r * m + c] = new Vector3(c * quad, alt, r * quad);

                colors[r * m + c] = (cover == null
                    ? CoverPalette.ColorFor(CoverClass.Open, alt, CoverPalette.Hash(fc, fr))
                    : BoundaryBlendedColor(cover, fc, fr, alt, stride, last))
                    .SrgbToLinear();
            }

        int ii = 0;
        for (int r = 0; r < m - 1; r++)
            for (int c = 0; c < m - 1; c++)
            {
                // tunnel portals (only meaningful at fine LODs — see IsHole)
                if (holes != null && stride <= MaxHoleStride && IsHole(holes, c, r, stride)) continue;

                int v00 = r * m + c;
                int v10 = v00 + 1;
                int v01 = v00 + m;
                int v11 = v01 + 1;
                // clockwise seen from above (Godot front face)
                indices[ii++] = v00; indices[ii++] = v10; indices[ii++] = v01;
                indices[ii++] = v10; indices[ii++] = v11; indices[ii++] = v01;
            }

        // skirts: rim vertices duplicated and dropped, quads between rim and dropped rim
        int sv = m * m;
        ii = AddSkirt(vertices, colors, indices, ii, ref sv, Enumerable.Range(0, m).Select(c => c).ToArray(), m, skirtDepth);                       // north row r=0
        ii = AddSkirt(vertices, colors, indices, ii, ref sv, Enumerable.Range(0, m).Select(c => (m - 1) * m + c).ToArray(), m, skirtDepth);         // south row
        ii = AddSkirt(vertices, colors, indices, ii, ref sv, Enumerable.Range(0, m).Select(r => r * m).ToArray(), m, skirtDepth);                   // west col
        ii = AddSkirt(vertices, colors, indices, ii, ref sv, Enumerable.Range(0, m).Select(r => r * m + (m - 1)).ToArray(), m, skirtDepth);         // east col

        // carved quads leave unused slots at the tail; trim so they don't render as
        // degenerate triangles fanning out from vertex 0
        if (ii != indices.Length)
            Array.Resize(ref indices, ii);

        return new MeshData(vertices, colors, indices);
    }

    /// <summary>Adds the walls lining any carved tunnel opening to a surface core.</summary>
    public static MeshData FinishSurface(MeshData core, ChunkGrid grid, int stride,
        IReadOnlySet<int>? holes, IReadOnlyList<RoadMeshBuilder.TunnelPortal>? portals)
    {
        if (holes is not { Count: > 0 } || stride > MaxHoleStride) return core;
        int m = (ChunkFormat.GridSize - 1) / stride + 1;
        return AppendCutWalls(core, grid, holes, stride, m, (float)(stride * ChunkFormat.SpacingM), portals);
    }

    /// <summary>
    /// A copy of <paramref name="core"/> with every vertex under a road corridor moved to its
    /// blended height (and recoloured, since the palette bands by altitude), and the skirts
    /// re-dropped from the moved rim. Byte-for-byte what <see cref="BuildSurfaceCore"/> gives
    /// with the blended height map, at the cost of the corridor cells rather than the tile.
    /// </summary>
    public static MeshData PatchSurface(MeshData core, ChunkGrid grid, int stride, byte[]? cover,
        RoadBlend blend, double clearance)
    {
        int n = ChunkFormat.GridSize, last = n - 1;
        int m = last / stride + 1;
        float skirtDepth = 2f * (float)(stride * ChunkFormat.SpacingM);
        var vertices = (Vector3[])core.Vertices.Clone();
        var colors = (Color[])core.Colors.Clone();

        for (int k = 0; k < blend.Cells.Length; k++)
        {
            int cell = blend.Cells[k];
            int fr = cell / n, fc = cell - fr * n;
            if (fc % stride != 0 || fr % stride != 0) continue;

            float alt = BlendedHeight((float)grid.HeightMetersAt(fc, fr), blend, k, clearance);
            int vi = fr / stride * m + fc / stride;
            vertices[vi].Y = alt;
            colors[vi] = (cover == null
                ? CoverPalette.ColorFor(CoverClass.Open, alt, CoverPalette.Hash(fc, fr))
                : BoundaryBlendedColor(cover, fc, fr, alt, stride, last)).SrgbToLinear();
        }

        // skirts, in the order BuildSurfaceCore laid them out: north, south, west, east
        int sv = m * m;
        for (int side = 0; side < 4; side++)
            for (int i = 0; i < m; i++, sv++)
            {
                int rim = side switch
                {
                    0 => i,
                    1 => (m - 1) * m + i,
                    2 => i * m,
                    _ => i * m + m - 1,
                };
                vertices[sv] = vertices[rim] with { Y = vertices[rim].Y - skirtDepth };
                colors[sv] = colors[rim];
            }

        return new MeshData(vertices, colors, core.Indices);
    }

    /// <summary>Tiles per side of one horizon block (10 x 10 km).</summary>
    public const int HorizonBlockTiles = 10;

    /// <summary>
    /// One 10 x 10 km block of the far horizon, from the region-wide 100 m lattice, in the same
    /// local frame as a tile (origin at the block's NW corner). 101 x 101 vertices, coloured by
    /// the altitude bands alone — at 30 km nobody can tell a vineyard from a meadow, and the
    /// cover raster is a per-tile file this layer exists to avoid reading. Tiles the region
    /// does not hold leave their quads out; blocks share edge vertices exactly, so there are
    /// no cracks between them and no skirts are needed.
    /// </summary>
    public static MeshData? BuildHorizonBlock(HorizonIndex horizon, int blockE, int blockN)
    {
        const int perTile = HorizonFormat.SamplesPerSide - 1;             // 10 quads a tile
        const int m = HorizonBlockTiles * perTile + 1;                    // 101
        const float quad = HorizonFormat.SpacingM;

        var vertices = new Vector3[m * m];
        var colors = new Color[m * m];
        var present = new bool[m * m];
        int any = 0;

        for (int r = 0; r < m; r++)
            for (int c = 0; c < m; c++)
            {
                // the block's last column/row belongs to its last tile's east/south edge, not to
                // the neighbouring block's first tile, so the two blocks read the same sample
                int tc = Math.Min(c / perTile, HorizonBlockTiles - 1);
                int tr = Math.Min(r / perTile, HorizonBlockTiles - 1);
                int ic = c - tc * perTile, ir = r - tr * perTile;
                var id = new TileId(blockE + tc, blockN + HorizonBlockTiles - 1 - tr);
                if (!horizon.TryGet(id, out var samples)) continue;

                float alt = (float)ChunkFormat.Dequantize(samples[ir * HorizonFormat.SamplesPerSide + ic]);
                vertices[r * m + c] = new Vector3(c * quad, alt, r * quad);
                colors[r * m + c] = CoverPalette.ColorFor(CoverClass.Open, alt,
                    CoverPalette.Hash(blockE * 100 + c, blockN * 100 + r)).SrgbToLinear();
                present[r * m + c] = true;
                any++;
            }

        if (any == 0) return null;

        var indices = new int[(m - 1) * (m - 1) * 6];
        int ii = 0;
        for (int r = 0; r < m - 1; r++)
            for (int c = 0; c < m - 1; c++)
            {
                int v00 = r * m + c, v10 = v00 + 1, v01 = v00 + m, v11 = v01 + 1;
                if (!present[v00] || !present[v10] || !present[v01] || !present[v11]) continue;
                indices[ii++] = v00; indices[ii++] = v10; indices[ii++] = v01;
                indices[ii++] = v10; indices[ii++] = v11; indices[ii++] = v01;
            }
        if (ii == 0) return null;
        if (ii != indices.Length) Array.Resize(ref indices, ii);

        return new MeshData(vertices, colors, indices);
    }

    /// <summary>
    /// The cover raster is one hard class per vertex, so a forest/meadow boundary used to be a
    /// dead stop from one triangle to the next — worse at coarse LOD strides, where a single
    /// 20-40 m quad can straddle the whole boundary. Only vertices actually adjacent to a
    /// different class pay for this (checked in the four cardinal directions at THIS stride's
    /// own step, so the blend width scales with quad size rather than always being a fixed 2 m);
    /// a uniform interior costs nothing extra. Averaging colours rather than picking a side is
    /// what keeps a three-way corner (e.g. forest/meadow/rock) from favouring whichever
    /// neighbour happened to be sampled.
    /// </summary>
    private static Color BoundaryBlendedColor(byte[] cover, int fc, int fr, float alt, int stride, int last)
    {
        byte center = cover[fr * ChunkFormat.GridSize + fc];
        Color sum = CoverPalette.ColorFor((CoverClass)center, alt, CoverPalette.Hash(fc, fr));
        int count = 1;

        ReadOnlySpan<(int Dc, int Dr)> neighbors = [(stride, 0), (-stride, 0), (0, stride), (0, -stride)];
        foreach (var (dc, dr) in neighbors)
        {
            int nc = fc + dc, nr = fr + dr;
            if ((uint)nc > (uint)last || (uint)nr > (uint)last) continue;

            byte ncls = cover[nr * ChunkFormat.GridSize + nc];
            if (ncls == center) continue;

            sum += CoverPalette.ColorFor((CoverClass)ncls, alt, CoverPalette.Hash(nc, nr));
            count++;
        }

        return count == 1 ? sum : sum / count;
    }

    private static readonly Color CutWallColor = new Color(0.34f, 0.31f, 0.28f);
    private static readonly Color PortalColor = new Color(0.50f, 0.49f, 0.47f);

    /// <summary>
    /// Matches <c>TunnelCarver.SideMargin</c> (tools/TerrainPreprocessor, a separate
    /// project so the constant can't be shared directly) — the extra radius the offline
    /// carve stamps around a tunnel's half-width when deciding which quads to remove. Reused
    /// here so the portal wall's outer ring lands on the same disc the hole mask was actually
    /// carved to, instead of an independently guessed rectangle. Keep the two in sync.
    /// </summary>
    private const float PortalSideMargin = 1.6f;

    /// <summary>
    /// Lines the sides of a carved opening with vertical walls, so the ground mesh is
    /// closed instead of ending at a raw edge with daylight behind it. At each tunnel
    /// <paramref name="portals"/> end, an arched portal wall (the same profile the bore
    /// itself is extruded from, see <see cref="RoadMeshBuilder.BoreProfile"/>) replaces the
    /// flat wall instead of stacking behind it — see <see cref="AppendPortalWall"/>.
    ///
    /// The walls are derived from the hole mask and the same height grid the surface uses,
    /// which is the whole point: geometry built separately from the road centreline could
    /// never line up with a hole quantised to the lattice.
    /// </summary>
    private static MeshData AppendCutWalls(MeshData mesh, ChunkGrid grid,
        IReadOnlySet<int> holes, int stride, int m, float quad,
        IReadOnlyList<RoadMeshBuilder.TunnelPortal>? portals)
    {
        grid.RequireFull(nameof(AppendCutWalls));
        // floor of the cut: below the lowest ground it touches, so the bore and road hide it
        float floor = float.MaxValue;
        foreach (int cell in holes)
        {
            int c = Math.Min(cell % HoleFormat.QuadsPerSide, ChunkFormat.GridSize - 1);
            int r = Math.Min(cell / HoleFormat.QuadsPerSide, ChunkFormat.GridSize - 1);
            floor = Mathf.Min(floor, (float)grid.HeightMetersAt(c, r));
        }
        if (floor == float.MaxValue) return mesh;
        floor -= 9f;

        var verts = new List<Vector3>(mesh.Vertices);
        var cols = new List<Color>(mesh.Colors);
        var idx = new List<int>(mesh.Indices);
        var linear = CutWallColor.SrgbToLinear();

        bool Carved(int c, int r) =>
            (uint)c < m - 1 && (uint)r < m - 1 && IsHole(holes, c, r, stride);

        // an edge within a portal's own carve radius of its mouth is the disc-shaped cap the
        // offline carve stamped there — the arch wall covers it, so the flat wall must not
        bool InPortalWindow(Vector3 p)
        {
            if (portals == null) return false;
            foreach (var portal in portals)
            {
                float radius = portal.HalfWidth + PortalSideMargin;
                float dx = p.X - portal.Mouth.X, dz = p.Z - portal.Mouth.Z;
                if (dx * dx + dz * dz <= radius * radius) return true;
            }
            return false;
        }

        // for every carved quad, wall off each side that faces uncarved ground
        for (int r = 0; r < m - 1; r++)
            for (int c = 0; c < m - 1; c++)
            {
                if (!Carved(c, r)) continue;

                AddSide(c, r, -1, 0);   // west
                AddSide(c, r, 1, 0);    // east
                AddSide(c, r, 0, -1);   // north
                AddSide(c, r, 0, 1);    // south

                void AddSide(int cc, int rr, int dc, int dr)
                {
                    if (Carved(cc + dc, rr + dr)) return;

                    // shared edge between this quad and its uncarved neighbour
                    int c0 = dc > 0 ? cc + 1 : cc;
                    int r0 = dr > 0 ? rr + 1 : rr;
                    int c1 = dc != 0 ? c0 : cc + 1;
                    int r1 = dr != 0 ? r0 : rr + 1;

                    var a = mesh.Vertices[r0 * m + c0];
                    var b = mesh.Vertices[r1 * m + c1];
                    if (InPortalWindow((a + b) * 0.5f)) return;

                    int i0 = verts.Count;
                    verts.Add(a);
                    verts.Add(b);
                    verts.Add(new Vector3(a.X, floor, a.Z));
                    verts.Add(new Vector3(b.X, floor, b.Z));
                    for (int k = 0; k < 4; k++) cols.Add(linear);
                    // cull_disabled, so winding only needs to be consistent
                    idx.Add(i0); idx.Add(i0 + 1); idx.Add(i0 + 2);
                    idx.Add(i0 + 1); idx.Add(i0 + 3); idx.Add(i0 + 2);
                }
            }

        if (portals != null)
            foreach (var portal in portals)
                AppendPortalWall(portal, grid, floor, verts, cols, idx);

        return new MeshData(verts.ToArray(), cols.ToArray(), idx.ToArray());
    }

    /// <summary>
    /// Closes one tunnel mouth with the same arch profile the bore itself is extruded from
    /// (<see cref="RoadMeshBuilder.BoreProfile"/>), so the inner ring here is bit-identical
    /// to the bore's own end ring — no seam at the arch. The outer ring is sized to the same
    /// disc <see cref="PortalSideMargin"/> the offline carve used, and its height is sampled
    /// straight from the grid within that disc, so the face never stands lower than the
    /// ground actually around it and its footprint always matches the real carved hole
    /// instead of an independently guessed shape.
    /// </summary>
    private static void AppendPortalWall(RoadMeshBuilder.TunnelPortal portal, ChunkGrid grid,
        float floor, List<Vector3> verts, List<Color> cols, List<int> idx)
    {
        var forward = (portal.Inward - portal.Mouth) with { Y = 0 };
        if (forward.LengthSquared() < 1e-8f) return;
        forward = forward.Normalized();
        var side = new Vector3(-forward.Z, 0, forward.X);

        float outerRadius = portal.HalfWidth + PortalSideMargin;
        float outerTopAbs = SampleMaxHeightNear(grid, portal.Mouth, outerRadius) + 0.4f;
        outerTopAbs = Mathf.Max(outerTopAbs, portal.Mouth.Y + portal.Height * 1.02f);
        float faceTopRel = outerTopAbs - portal.Mouth.Y;
        float faceBottomRel = floor - portal.Mouth.Y;

        var ring = RoadMeshBuilder.BoreProfile;
        int baseIndex = verts.Count;
        var linear = PortalColor.SrgbToLinear();

        // Pair each arch vertex with a point on the enclosing disc-derived rectangle, found
        // by pushing outward from the arch centre until that bound is met — same approach
        // AppendHeadwall used to use against a guessed rectangle, now against a real one.
        for (int k = 0; k < ring.Length; k++)
        {
            var (px, py) = ring[k];
            var inner = portal.Mouth + side * (px * portal.HalfWidth) + new Vector3(0, py * portal.Height, 0);

            float dx = px, dy = py - 0.25f;
            if (Mathf.Abs(dx) < 1e-4f && Mathf.Abs(dy) < 1e-4f) dy = 1f;
            float scale = Mathf.Min(
                Mathf.Abs(dx) < 1e-4f ? float.MaxValue : outerRadius / (Mathf.Abs(dx) * portal.HalfWidth),
                dy > 0
                    ? faceTopRel / (dy * portal.Height)
                    : Mathf.Abs(faceBottomRel) / (Mathf.Abs(dy) * portal.Height));
            var outer = portal.Mouth + side * (dx * portal.HalfWidth * scale)
                        + new Vector3(0, 0.25f * portal.Height + dy * portal.Height * scale, 0);

            verts.Add(inner); verts.Add(outer);
            cols.Add(linear); cols.Add(linear);
        }

        for (int k = 0; k < ring.Length - 1; k++)
        {
            int a = baseIndex + k * 2;
            idx.Add(a); idx.Add(a + 1); idx.Add(a + 2);
            idx.Add(a + 1); idx.Add(a + 3); idx.Add(a + 2);
        }
    }

    private static float SampleMaxHeightNear(ChunkGrid grid, Vector3 local, float radius)
    {
        float spacing = (float)ChunkFormat.SpacingM;
        int c0 = Mathf.RoundToInt(local.X / spacing), r0 = Mathf.RoundToInt(local.Z / spacing);
        int cells = Mathf.CeilToInt(radius / spacing);
        int last = ChunkFormat.GridSize - 1;
        float max = float.MinValue;
        for (int dr = -cells; dr <= cells; dr++)
            for (int dc = -cells; dc <= cells; dc++)
            {
                if (dc * dc + dr * dr > cells * cells) continue;
                int c = c0 + dc, r = r0 + dr;
                if ((uint)c > (uint)last || (uint)r > (uint)last) continue;
                max = Mathf.Max(max, (float)grid.HeightMetersAt(c, r));
            }
        return max == float.MinValue ? local.Y : max;
    }

    /// <summary>
    /// Shared near-field threshold for both tunnel holes and the visual road/path corridor
    /// blend in <see cref="BuildSurface"/>: coarser than this and a portal-sized hole would be
    /// inflated to the size of a whole LOD quad (40 m at stride 20), tearing a gash in the
    /// mountain — and a road corridor (a few metres wide) would fall between vertices spaced
    /// tens of metres apart, blending nothing for real cost. Both are only visible up close
    /// anyway, so distant rings simply stay solid/unblended.
    /// </summary>
    public const int MaxHoleStride = 4;

    /// <summary>
    /// A rendered quad is dropped only when *every* full-res cell it covers is carved.
    /// Using "any" instead would grow the opening by up to one LOD quad on each side.
    /// </summary>
    private static bool IsHole(IReadOnlySet<int> holes, int c, int r, int stride)
    {
        int c0 = c * stride, r0 = r * stride;
        for (int rr = r0; rr < r0 + stride; rr++)
            for (int cc = c0; cc < c0 + stride; cc++)
                if (!holes.Contains(rr * HoleFormat.QuadsPerSide + cc))
                    return false;
        return true;
    }

    private static int AddSkirt(Vector3[] vertices, Color[] colors, int[] indices, int ii, ref int sv,
        int[] rim, int m, float skirtDepth)
    {
        int first = sv;
        for (int i = 0; i < m; i++)
        {
            var v = vertices[rim[i]];
            colors[sv] = colors[rim[i]];
            vertices[sv++] = new Vector3(v.X, v.Y - skirtDepth, v.Z);
        }
        for (int i = 0; i < m - 1; i++)
        {
            // rendered with cull_disabled, so winding doesn't matter here
            indices[ii++] = rim[i]; indices[ii++] = rim[i + 1]; indices[ii++] = first + i;
            indices[ii++] = rim[i + 1]; indices[ii++] = first + i + 1; indices[ii++] = first + i;
        }
        return ii;
    }

    /// <summary>Full-resolution heights, straight off the grid, with no road blending.</summary>
    private static float[] DequantizedHeights(ChunkGrid grid)
    {
        grid.RequireFull(nameof(DequantizedHeights));
        var map = new float[ChunkFormat.GridSize * ChunkFormat.GridSize];
        for (int i = 0; i < map.Length; i++)
            map[i] = (float)ChunkFormat.Dequantize(grid.Heights[i]);
        return map;
    }

    /// <summary>
    /// Full-resolution vertex height (column, row) as the surface draws it with
    /// <paramref name="blend"/> applied at <paramref name="clearance"/>: what geometry that has to
    /// meet the terrain edge to edge stands on, and what a road probe checks a body against.
    /// </summary>
    public static Func<int, int, float> GroundHeights(ChunkGrid grid, RoadBlend? blend, double clearance)
    {
        grid.RequireFull(nameof(GroundHeights));
        Dictionary<int, int>? at = null;
        if (blend != null)
        {
            at = new Dictionary<int, int>(blend.Cells.Length);
            for (int k = 0; k < blend.Cells.Length; k++) at[blend.Cells[k]] = k;
        }
        return (c, r) =>
        {
            float h = (float)grid.HeightMetersAt(c, r);
            return at != null && at.TryGetValue(r * ChunkFormat.GridSize + c, out int k) ? BlendedHeight(h, blend!, k, clearance) : h;
        };
    }

    /// <summary>
    /// Absolute heights for HeightMapShape3D: index r*1001+c, x=east=col, z=south=row, with
    /// <paramref name="blend"/> (if any) pulling the floor onto at-grade roads. Carved cells
    /// become NaN, which Jolt treats as a hole in the heightfield — that is what lets a player
    /// actually drive into a tunnel instead of hitting the hillside.
    /// </summary>
    public static float[] BuildCollisionMap(ChunkGrid grid, IReadOnlySet<int>? holes = null,
        RoadBlend? blend = null)
    {
        var map = DequantizedHeights(grid);
        if (blend != null) ApplyRoadBlend(map, blend, 0.0);
        StampHoles(map, holes);
        return map;
    }

    private static void StampHoles(float[] map, IReadOnlySet<int>? holes)
    {
        if (holes == null) return;
        int n = ChunkFormat.GridSize;
        foreach (int cell in holes)
        {
            int c = cell % HoleFormat.QuadsPerSide;
            int r = cell / HoleFormat.QuadsPerSide;
            // a quad is bounded by 4 vertices; NaN on its top-left removes it
            map[r * n + c] = float.NaN;
        }
    }
}
