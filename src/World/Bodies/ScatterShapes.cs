using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Bodies;

/// <summary>
/// The shapes of the stones on other bodies' ground (#2016, <see cref="ScatterField"/>, laid by <see cref="BodyScatter"/>):
/// <see cref="Variants"/> each of an angular rock (an icosphere cut by a few seeded planes: Mars' vesicular basalt, the
/// regolith's broken clods) and a block of water ice (a box with jittered corners and a chipped edge: Europa's chaos
/// blocks); Titan's rounded cobbles are <see cref="RingRocks"/>' lumps. Radius ~1, the base at about −0.6, built once and
/// shared by every stone (each is one of them, turned and sized by its seed). Faceted, flat normals: the low poly look. In
/// the model viewer under "Space".
/// </summary>
public static class ScatterShapes
{
    /// <summary>How many shapes a kind has.</summary>
    public const int Variants = 4;

    private static readonly ArrayMesh?[] Rocks = new ArrayMesh?[Variants], Blocks = new ArrayMesh?[Variants];

    /// <summary>Shape <paramref name="i"/> of a kind, built at the first ask.</summary>
    public static ArrayMesh Mesh(ScatterField.Kind kind, int i) => kind switch
    {
        ScatterField.Kind.IceBlock => Blocks[i] ??= Block(i),
        ScatterField.Kind.Cobble => RingRocks.Mesh(i % RingRocks.Variants),
        _ => Rocks[i] ??= Rock(i),
    };

    [Showcase("Space", "Rocks and ice blocks on other bodies")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> Showcase()
    {
        for (int i = 0; i < Variants; i++)
        {
            int k = i;
            yield return ($"rock {k + 1}", () => new MeshInstance3D
            {
                Mesh = Mesh(ScatterField.Kind.Rock, k),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.3f, 0.22f), Roughness = 0.95f },
            });
        }
        for (int i = 0; i < Variants; i++)
        {
            int k = i;
            yield return ($"ice block {k + 1}", () => new MeshInstance3D
            {
                Mesh = Mesh(ScatterField.Kind.IceBlock, k),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.9f, 0.95f), Roughness = 0.5f },
            });
        }
    }

    /// <summary>An angular rock: an icosphere (one subdivision) squashed, then cut flat by five seeded planes.</summary>
    private static ArrayMesh Rock(int seed)
    {
        var (verts, faces) = Icosphere(1);
        var rng = new RandomNumberGenerator { Seed = (ulong)(seed * 6007 + 2016) };
        var squash = new Vector3(rng.RandfRange(0.85f, 1.15f), rng.RandfRange(0.55f, 0.8f), rng.RandfRange(0.8f, 1.1f));
        var pos = new Vector3[verts.Count];
        for (int v = 0; v < verts.Count; v++) pos[v] = verts[v] * squash * rng.RandfRange(0.92f, 1.08f);
        // the cuts: each plane at a seeded direction and depth, every point past it brought back onto it (a flat facet)
        for (int c = 0; c < 5; c++)
        {
            var n = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-0.6f, 1), rng.RandfRange(-1, 1)).Normalized();
            float d = rng.RandfRange(0.45f, 0.75f);
            for (int v = 0; v < pos.Length; v++)
            {
                float over = pos[v].Dot(n) - d;
                if (over > 0) pos[v] -= n * over;
            }
        }
        // a flat base (it sits in the ground)
        for (int v = 0; v < pos.Length; v++) pos[v].Y = Mathf.Max(pos[v].Y, -0.6f * squash.Y);
        return Faceted(pos, faces);
    }

    /// <summary>A block of ice: a box with jittered corners, one edge chipped off, a little taller than wide.</summary>
    private static ArrayMesh Block(int seed)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)(seed * 4049 + 2016) };
        var size = new Vector3(rng.RandfRange(0.8f, 1.1f), rng.RandfRange(0.6f, 0.95f), rng.RandfRange(0.6f, 0.9f));
        var corners = new Vector3[8];
        for (int c = 0; c < 8; c++)
        {
            var s = new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1);
            corners[c] = s * size + new Vector3(rng.RandfRange(-0.15f, 0.15f), (c & 2) == 0 ? 0 : rng.RandfRange(-0.25f, 0.1f), rng.RandfRange(-0.15f, 0.15f));
        }
        // the six faces as quads (outward winding fixed by the face normal later)
        int[][] quads = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
        var faces = new List<(int, int, int)>();
        foreach (var q in quads)
        {
            faces.Add((q[0], q[1], q[2]));
            faces.Add((q[0], q[2], q[3]));
        }
        // a chipped top edge: the corner nearest a seeded direction pulled in and down
        int chip = 2 + 4 * rng.RandiRange(0, 1) + rng.RandiRange(0, 1);
        corners[chip] = corners[chip] * new Vector3(0.7f, 0.6f, 0.7f);
        return Faceted(corners, faces);
    }

    private static (List<Vector3> Verts, List<(int, int, int)> Faces) Icosphere(int subdivisions)
    {
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
        for (int s = 0; s < subdivisions; s++)
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
        return (verts, faces);
    }

    /// <summary>Each face its own normal, facing out (from the shape's centre); degenerate faces (a cut flattened them) left out.</summary>
    private static ArrayMesh Faceted(IReadOnlyList<Vector3> pos, List<(int, int, int)> faces)
    {
        var centre = Vector3.Zero;
        foreach (var p in pos) centre += p;
        centre /= pos.Count;
        var outV = new List<Vector3>(faces.Count * 3);
        var outN = new List<Vector3>(faces.Count * 3);
        foreach (var (a, b, c) in faces)
        {
            var cross = (pos[c] - pos[a]).Cross(pos[b] - pos[a]);
            if (cross.LengthSquared() < 1e-10f) continue;
            var n = cross.Normalized();
            bool flip = n.Dot((pos[a] + pos[b] + pos[c]) / 3 - centre) < 0;
            if (flip) n = -n;
            // Godot's front faces wind clockwise seen from outside: (b − a) × (c − a) pointing in
            outV.Add(pos[a]); outV.Add(flip ? pos[c] : pos[b]); outV.Add(flip ? pos[b] : pos[c]);
            outN.Add(n); outN.Add(n); outN.Add(n);
        }
        var colors = new Color[outV.Count];
        Array.Fill(colors, Colors.White);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = outV.ToArray();
        arrays[(int)Godot.Mesh.ArrayType.Normal] = outN.ToArray();
        arrays[(int)Godot.Mesh.ArrayType.Color] = colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
