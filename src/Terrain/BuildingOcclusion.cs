using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// What occlusion culling needs from a tile's buildings (#553): the building mesh cut into cells
/// small enough for a building to hide one, and the buildings as occluders. Built on the tile's
/// worker, beside the building mesh.
///
/// <para>
/// A tile's buildings are one mesh a kilometre wide, and Godot culls whole instances: no building
/// ever hides a tile, so occluders alone hide nothing of the city. Cut into <see cref="Cells"/>
/// per side, a block behind a row of houses is its own instance, and gone when the row hides it.
/// </para>
/// </summary>
public static class BuildingOcclusion
{
    /// <summary>Cells per tile side: 125 m, about a city block and the streets round it.</summary>
    public const int Cells = 8;

    /// <summary>A building is an occluder when its roof covers at least this share of its plan box.</summary>
    public const float MinFill = 0.85f;

    /// <summary>
    /// How far inside its walls an occluder stands, m: never in front of anything standing against
    /// the wall (a parked car, a sign, a door's leaf), and clear of the PS1 vertex snap.
    /// </summary>
    public const float Inset = 0.5f;

    /// <summary>
    /// The mesh's triangles sorted into <see cref="Cells"/>² cells by centroid (x east, z south,
    /// tile-local). A building across a cell edge is split between two instances, which draw it
    /// whole together. Empty cells are null.
    /// </summary>
    public static BuildingMeshBuilder.MeshData?[] SplitByCell(BuildingMeshBuilder.MeshData data)
    {
        const float size = (float)(ChunkFormat.TileSizeM / Cells);
        var lists = new List<int>?[Cells * Cells];
        var v = data.Vertices;
        for (int t = 0; t + 2 < v.Length; t += 3)
        {
            var mid = (v[t] + v[t + 1] + v[t + 2]) / 3f;
            int cx = Math.Clamp((int)(mid.X / size), 0, Cells - 1), cz = Math.Clamp((int)(mid.Z / size), 0, Cells - 1);
            (lists[cz * Cells + cx] ??= new List<int>()).Add(t);
        }
        var cells = new BuildingMeshBuilder.MeshData?[Cells * Cells];
        for (int c = 0; c < cells.Length; c++)
        {
            if (lists[c] is not { } tris) continue;
            int n = tris.Count * 3;
            var verts = new Vector3[n];
            var colors = new Color[n];
            var uvs = new Vector2[n];
            var uv2s = new Vector2[n];
            var frames = new float[n * 4];
            int o = 0;
            foreach (int t in tris)
                for (int k = 0; k < 3; k++, o++)
                {
                    verts[o] = v[t + k];
                    colors[o] = data.Colors[t + k];
                    uvs[o] = data.Uvs[t + k];
                    uv2s[o] = data.Uv2s[t + k];
                    Array.Copy(data.Frames, (t + k) * 4, frames, o * 4, 4);
                }
            cells[c] = new BuildingMeshBuilder.MeshData(verts, colors, uvs, uv2s, frames);
        }
        return cells;
    }

    /// <summary>
    /// The tile's buildings as occluders, tile-local: for each one whose roof covers most of its
    /// plan box, that box shrunk by <see cref="Inset"/>, from its floor to its eave (walls and a
    /// flat top). An L or a U is left out rather than drawn as the box round it, which would hide
    /// whatever stands in its courtyard. Null when no building qualifies.
    /// </summary>
    public static (Vector3[] Vertices, int[] Indices)? Occluders(BuildingTile tile)
    {
        var boxes = BuildingTypes.For(tile).Boxes;
        var verts = new List<Vector3>();
        var idx = new List<int>();
        for (int i = 0; i < tile.Buildings.Count && i < boxes.Length; i++)
        {
            if (boxes[i] is not { } box) continue;
            var b = tile.Buildings[i];
            float w = box.Width - 2 * Inset, d = box.Depth - 2 * Inset;
            float bottom = b.MinY + Inset, top = box.Eave - Inset;
            if (w < 3f || d < 3f || top - bottom < 2.5f) continue;
            if (RoofArea(b) < MinFill * box.Area) continue;

            var u = box.AxisU * (w / 2);
            var vAxis = box.AxisV * (d / 2);
            var c = box.Center;
            Vector2[] corners = { c - u - vAxis, c + u - vAxis, c + u + vAxis, c - u + vAxis };
            int b0 = verts.Count;
            foreach (var p in corners) verts.Add(new Vector3(p.X, bottom, p.Y));
            foreach (var p in corners) verts.Add(new Vector3(p.X, top, p.Y));
            // four walls and the top: winding does not matter to an occluder
            for (int s = 0; s < 4; s++)
            {
                int a = b0 + s, n = b0 + (s + 1) % 4;
                idx.AddRange(new[] { a, n, n + 4, a, n + 4, a + 4 });
            }
            idx.AddRange(new[] { b0 + 4, b0 + 5, b0 + 6, b0 + 4, b0 + 6, b0 + 7 });
        }
        return verts.Count == 0 ? null : (verts.ToArray(), idx.ToArray());
    }

    /// <summary>The plan area of a building's upward-facing triangles: its roofs, seen from above.</summary>
    private static float RoofArea(Building b)
    {
        var t = b.Triangles;
        float area = 0f;
        for (int i = 0; i + 8 < t.Length; i += 9)
        {
            var p0 = new Vector3(t[i], t[i + 1], t[i + 2]);
            var n = (new Vector3(t[i + 3], t[i + 4], t[i + 5]) - p0).Cross(new Vector3(t[i + 6], t[i + 7], t[i + 8]) - p0);
            float len = n.Length();
            if (len < 1e-6f || Math.Abs(n.Y) / len < BuildingTriangles.RoofNormalY) continue;
            area += Math.Abs(n.Y) * 0.5f;
        }
        return area;
    }
}
