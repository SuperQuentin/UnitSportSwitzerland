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
        int last = ChunkFormat.GridSize - 1;      // 500
        if (last % stride != 0)
            throw new ArgumentException($"Stride {stride} must divide {last}");
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

                var cls = cover == null
                    ? CoverClass.Open
                    : (CoverClass)cover[fr * ChunkFormat.GridSize + fc];
                colors[r * m + c] = CoverPalette
                    .ColorFor(cls, alt, CoverPalette.Hash(fc, fr))
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
    public static float[] BuildCollisionMap(ChunkGrid grid, IReadOnlySet<int>? holes = null)
    {
        int n = ChunkFormat.GridSize;
        var map = new float[n * n];
        for (int i = 0; i < map.Length; i++)
            map[i] = (float)ChunkFormat.Dequantize(grid.Heights[i]);

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
}
