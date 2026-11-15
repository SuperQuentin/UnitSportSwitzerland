using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;

namespace UnitSport.World.Bodies;

/// <summary>
/// The shapes of Saturn's ring chunks (#1854): <see cref="Variants"/> lumpy ice rocks of radius 1, an icosphere twice
/// subdivided and pushed in and out by a few seeded lobes, built once and shared by every chunk (each is one of them,
/// turned and sized by its seed: <see cref="RingChunks"/>). In the model viewer under "Space".
/// </summary>
public static class RingRocks
{
    /// <summary>How many shapes the chunks share.</summary>
    public const int Variants = 4;

    private static ArrayMesh?[]? _meshes;

    /// <summary>Shape <paramref name="i"/> (0 to <see cref="Variants"/> − 1), built at the first ask.</summary>
    public static ArrayMesh Mesh(int i)
    {
        _meshes ??= new ArrayMesh?[Variants];
        return _meshes[i] ??= Build(i);
    }

    [Showcase("Space", "Saturn ring ice chunks")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> Showcase()
    {
        for (int i = 0; i < Variants; i++)
        {
            int k = i;
            yield return ($"chunk {k + 1}", () => new MeshInstance3D
            {
                Mesh = Mesh(k), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.84f, 0.80f), Roughness = 0.9f },
            });
        }
    }

    /// <summary>A lumpy ice rock of radius ~1: an icosphere (2 subdivisions, 162 vertices) pushed by six seeded lobes.</summary>
    private static ArrayMesh Build(int seed)
    {
        // the icosahedron
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        var verts = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (int v = 0; v < verts.Count; v++) verts[v] = verts[v].Normalized();
        var faces = new List<(int, int, int)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };
        for (int s = 0; s < 2; s++)
        {
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out int m)) return m;
                verts.Add(((verts[a] + verts[b]) * 0.5f).Normalized());
                return mid[key] = verts.Count - 1;
            }
            var next = new List<(int, int, int)>(faces.Count * 4);
            foreach (var (a, b, c) in faces)
            {
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.Add((a, ab, ca)); next.Add((b, bc, ab)); next.Add((c, ca, bc)); next.Add((ab, bc, ca));
            }
            faces = next;
        }
        // six lobes, in and out, from the seed: no two shapes alike, none a ball
        var rng = new RandomNumberGenerator { Seed = (ulong)(seed * 7919 + 17) };
        var lobes = new (Vector3 Dir, float Amp, float Width)[6];
        for (int l = 0; l < lobes.Length; l++)
            lobes[l] = (new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)).Normalized(),
                rng.RandfRange(-0.28f, 0.32f), rng.RandfRange(1.5f, 4f));
        var squash = new Vector3(rng.RandfRange(0.75f, 1.1f), rng.RandfRange(0.6f, 1f), rng.RandfRange(0.8f, 1.15f));
        var pos = new Vector3[verts.Count];
        for (int v = 0; v < verts.Count; v++)
        {
            float r = 1f;
            foreach (var (dir, amp, width) in lobes) r += amp * Mathf.Exp(-width * (1f - verts[v].Dot(dir)));
            pos[v] = verts[v] * r * squash;
        }
        // faceted: each face its own normal, so the low poly reads as chipped ice
        var outV = new Vector3[faces.Count * 3];
        var outN = new Vector3[faces.Count * 3];
        int w = 0;
        foreach (var (a, b, c) in faces)
        {
            var n = (pos[c] - pos[a]).Cross(pos[b] - pos[a]).Normalized();
            if (n.Dot(pos[a] + pos[b] + pos[c]) < 0) n = -n;
            outV[w] = pos[a]; outN[w++] = n;
            outV[w] = pos[c]; outN[w++] = n;
            outV[w] = pos[b]; outN[w++] = n;
        }
        var colors = new Color[outV.Length];
        Array.Fill(colors, Colors.White);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = outV;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = outN;
        arrays[(int)Godot.Mesh.ArrayType.Color] = colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
