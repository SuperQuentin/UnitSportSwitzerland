using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Builds mesh/collision arrays from a chunk grid. Everything here is pure C# arrays and
/// worker-thread safe; Godot resources (ArrayMesh, shapes) are created by the caller on
/// the main thread. Local origin is the tile's NW corner: +x east, +z south, y altitude.
/// </summary>
public static class TerrainMeshBuilder
{
    public sealed record MeshData(Vector3[] Vertices, Color[] Colors, int[] Indices);

    /// <summary>
    /// Indexed grid mesh at the given stride with skirts on all four edges (skirts hide
    /// cracks at LOD-ring transitions; same-LOD tile seams are exact by construction).
    /// </summary>
    public static MeshData BuildSurface(ChunkGrid grid, int stride, IReadOnlySet<int>? holes = null,
        byte[]? cover = null)
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
                float alt = (float)grid.HeightMetersAt(fc, fr);
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

        var mesh = new MeshData(vertices, colors, indices);
        if (holes is { Count: > 0 } && stride <= MaxHoleStride)
            mesh = AppendCutWalls(mesh, grid, holes, stride, m, quad);
        return mesh;
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

    /// <summary>
    /// Lines the sides of a carved opening with vertical walls, so the ground mesh is
    /// closed instead of ending at a raw edge with daylight behind it.
    ///
    /// The walls are derived from the hole mask and the same height grid the surface uses,
    /// which is the whole point: geometry built separately from the road centreline could
    /// never line up with a hole quantised to the 2 m lattice.
    /// </summary>
    private static MeshData AppendCutWalls(MeshData mesh, ChunkGrid grid,
        IReadOnlySet<int> holes, int stride, int m, float quad)
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

        return new MeshData(verts.ToArray(), cols.ToArray(), idx.ToArray());
    }

    /// <summary>
    /// Coarser than this and a portal-sized hole would be inflated to the size of a whole
    /// LOD quad (40 m at stride 20), tearing a gash in the mountain. Tunnels are only
    /// visible up close anyway, so distant rings simply stay solid.
    /// </summary>
    private const int MaxHoleStride = 4;

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

    /// <summary>
    /// Absolute heights for HeightMapShape3D: index r*501+c, x=east=col, z=south=row.
    /// Carved cells become NaN, which Jolt treats as a hole in the heightfield — that is
    /// what lets a player actually drive into a tunnel instead of hitting the hillside.
    /// </summary>
    /// <summary>
    /// <paramref name="roadTile"/>, when given, blends the collision floor toward the road
    /// itself under every at-grade corridor — see <see cref="BlendRoadCorridor"/>. Called twice
    /// per tile when both collision and roads are wanted: once early with no road tile, so the
    /// bare-terrain collision can publish immediately and the ground under a streaming player is
    /// never held up waiting for roads to load (see the interim-result comment in
    /// <c>ChunkManager.StartBuild</c>), and again once the road tile has arrived, replacing the
    /// bare version with the corridor-blended one.
    /// </summary>
    public static float[] BuildCollisionMap(ChunkGrid grid, IReadOnlySet<int>? holes = null,
        RoadTile? roadTile = null)
    {
        grid.RequireFull(nameof(BuildCollisionMap));
        int n = ChunkFormat.GridSize;
        var map = new float[n * n];
        for (int i = 0; i < map.Length; i++)
            map[i] = (float)ChunkFormat.Dequantize(grid.Heights[i]);

        if (roadTile != null) BlendRoadCorridor(map, n, roadTile);

        if (holes != null)
            foreach (int cell in holes)
            {
                int c = cell % HoleFormat.QuadsPerSide;
                int r = cell / HoleFormat.QuadsPerSide;
                // a quad is bounded by 4 vertices; NaN on its top-left removes it
                map[r * n + c] = float.NaN;
            }

        return map;
    }

    /// <summary>How far past a road's own half-width the collision blend fades back to bare
    /// terrain — the same "ramp the approach" idea RoadExtractor already applies to the visual
    /// drape (see CLAUDE.md), applied here to the physics floor instead.</summary>
    private const double CorridorFalloffM = 3.0;

    /// <summary>
    /// Raises or lowers the terrain collision floor toward each at-grade road/path/rail's own
    /// surveyed height, so the ground a player actually stands on matches the road they can see
    /// — swissALTI3D does not model the embankment climbing to a grade change, so without this
    /// the physics floor and the visible tread silently disagree by however much grading did.
    ///
    /// <para>
    /// Bridges and tunnels are excluded outright (<c>RoadFlags.Bridge/Tunnel</c>): a heightfield
    /// has one height per (x, z) column, so it cannot represent a deck floating above a valley
    /// floor at the same position — blending toward a bridge's deck height would fill in the
    /// gorge it crosses. Those get dedicated collision geometry instead (bridge decks/piers);
    /// tunnels keep working via the existing hole-carving at the portal. Aerial ropeways,
    /// watercourses and walls are excluded too (<c>RoadFormat.IsAerial/IsWatercourse/IsWall</c>)
    /// — none of them is a surface at ground level, so pulling the terrain up to a cableway's
    /// cable or a retaining wall's coping would be exactly the wrong direction of "seamless."
    /// </para>
    /// </summary>
    private static void BlendRoadCorridor(float[] map, int n, RoadTile roadTile)
    {
        double spacing = ChunkFormat.SpacingM;

        foreach (var seg in roadTile.Segments)
        {
            if ((seg.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) continue;
            if (RoadFormat.IsAerial(seg.Class) || RoadFormat.IsWatercourse(seg.Class)
                || RoadFormat.IsWall(seg.Class)) continue;

            double half = seg.Width * 0.5;
            double radius = half + CorridorFalloffM;
            int cells = (int)Math.Ceiling(radius / spacing);

            for (int i = 0; i < seg.PointCount - 1; i++)
            {
                double ax = seg.Points[i * 3], ay = seg.Points[i * 3 + 1], az = seg.Points[i * 3 + 2];
                double bx = seg.Points[(i + 1) * 3], by = seg.Points[(i + 1) * 3 + 1],
                    bz = seg.Points[(i + 1) * 3 + 2];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                int steps = Math.Max(1, (int)Math.Ceiling(len / spacing));

                for (int st = 0; st <= steps; st++)
                {
                    double t = (double)st / steps;
                    double x = ax + (bx - ax) * t, z = az + (bz - az) * t;
                    // the segment's own stored Y already carries the approach-ramp blend
                    // RoadExtractor baked in at preprocess time - no re-derivation needed
                    double roadY = ay + (by - ay) * t;
                    int c0 = (int)Math.Round(x / spacing), r0 = (int)Math.Round(z / spacing);

                    for (int dr = -cells; dr <= cells; dr++)
                        for (int dc = -cells; dc <= cells; dc++)
                        {
                            int c = c0 + dc, r = r0 + dr;
                            if ((uint)c >= (uint)n || (uint)r >= (uint)n) continue;

                            double dist = Math.Sqrt((double)(dc * dc + dr * dr)) * spacing;
                            if (dist > radius) continue;

                            double w = 1.0 - Smoothstep(half, radius, dist);
                            int idx = r * n + c;
                            map[idx] = (float)(map[idx] + (roadY - map[idx]) * w);
                        }
                }
            }
        }
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / Math.Max(edge1 - edge0, 1e-6), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }
}
