using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Builds water surfaces from the ground-cover raster.
///
/// swissALTI3D already models lakes and rivers as flat surfaces at water level, so the
/// terrain height at a water cell *is* the water level. That means we can lay the surface
/// directly on the existing heights instead of shipping separate water geometry, and a
/// river automatically keeps its downstream gradient rather than being forced flat.
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

    public static MeshData? Build(ChunkGrid grid, byte[] cover)
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

        return indices.Count == 0
            ? null
            : new MeshData(vertices.ToArray(), indices.ToArray());
    }
}
