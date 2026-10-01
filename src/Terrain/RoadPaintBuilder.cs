using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Road paint (#116): the <c>.road</c> v3 paint layer as one extra surface of the road mesh, with
/// the same <c>ps1_road</c> material (vertex snap, dither, fog, snow) and colour from the vertex.
/// Polylines are ribboned here, dashed by <see cref="RoadPaintGeometry.Runs"/>; triangle lists
/// (teeth, arrows, symbols) are drawn as stored. The shader lifts the surface toward the camera
/// by a depth bias and dissolves a line thinner than a pixel into the road under it.
/// A v1/v2 tile has no paint: its markings stay the shader's stripes.
/// </summary>
public static class RoadPaintBuilder
{
    /// <summary><c>ps1_road.gdshader</c> style id of paint (uv2.x).</summary>
    public const float Style = 6f;

    /// <summary>Covers the 2 cm the network stage lets a line leave the ribbon (PaintEmitter); the shader's depth bias does the rest.</summary>
    private const float Lift = 0.02f;

    public static RoadMeshBuilder.MeshData? Build(RoadTile tile)
    {
        if (tile.Paint.Count == 0) return null;

        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        // uv = (metres along, lateral across the line in [-1,1]) for the legibility fade
        var uvs = new List<Vector2>();
        var uv2s = new List<Vector2>();
        var indices = new List<int>();

        foreach (var paint in tile.Paint)
        {
            // raw vertex colours are linear (vertex-colours-raw-linear-shader); the file holds sRGB
            var colour = new Color(paint.Rgba).SrgbToLinear();
            if (paint.Shape == PaintShape.Triangles)
            {
                int start = vertices.Count;
                for (int i = 0; i + 2 < paint.Vertices.Length; i += 3)
                {
                    vertices.Add(new Vector3(paint.Vertices[i], paint.Vertices[i + 1] + Lift, paint.Vertices[i + 2]));
                    colors.Add(colour);
                    uvs.Add(Vector2.Zero);
                    uv2s.Add(new Vector2(Style, 0f));
                }
                foreach (ushort index in paint.Indices) indices.Add(start + index);
                continue;
            }

            if (paint.Type == PaintType.SharkTooth)   // a Wartelinie row stored as its base line (#121)
            {
                foreach (var t in RoadPaintGeometry.Teeth(paint))
                    for (int k = 0; k < 9; k += 3)
                    {
                        indices.Add(vertices.Count);
                        vertices.Add(new Vector3(t[k], t[k + 1] + Lift, t[k + 2]));
                        colors.Add(colour);
                        uvs.Add(Vector2.Zero);
                        uv2s.Add(new Vector2(Style, 0f));
                    }
                continue;
            }

            float half = paint.Width * 0.5f;
            foreach (var run in RoadPaintGeometry.Runs(paint))
                AppendRibbon(run, half, colour, vertices, colors, uvs, uv2s, indices);
        }

        return vertices.Count == 0
            ? null
            : new RoadMeshBuilder.MeshData(vertices.ToArray(), colors.ToArray(), uvs.ToArray(), uv2s.ToArray(), indices.ToArray());
    }

    /// <summary>A flat ribbon along the run, per-vertex bisector like the road ribbon under it.</summary>
    private static void AppendRibbon(float[] run, float half, Color colour, List<Vector3> vertices,
        List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        int n = run.Length / 3;
        if (n < 2) return;
        int start = vertices.Count;
        float along = 0;
        for (int i = 0; i < n; i++)
        {
            var p = new Vector3(run[i * 3], run[i * 3 + 1] + Lift, run[i * 3 + 2]);
            int i0 = Math.Max(0, i - 1), i1 = Math.Min(n - 1, i + 1);
            var forward = new Vector3(run[i1 * 3] - run[i0 * 3], 0, run[i1 * 3 + 2] - run[i0 * 3 + 2]);
            forward = forward.LengthSquared() < 1e-8f ? Vector3.Forward : forward.Normalized();
            var side = new Vector3(-forward.Z, 0, forward.X) * half;
            if (i > 0) along += new Vector2(run[i * 3] - run[i * 3 - 3], run[i * 3 + 2] - run[i * 3 - 1]).Length();

            vertices.Add(p - side);
            vertices.Add(p + side);
            colors.Add(colour);
            colors.Add(colour);
            uvs.Add(new Vector2(along, -1f));
            uvs.Add(new Vector2(along, 1f));
            uv2s.Add(new Vector2(Style, 0f));
            uv2s.Add(new Vector2(Style, 0f));
        }
        for (int i = 0; i < n - 1; i++)
        {
            int a = start + i * 2;
            indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
            indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
        }
    }
}
