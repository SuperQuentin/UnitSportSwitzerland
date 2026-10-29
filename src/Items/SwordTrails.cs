using Godot;
using UnitSport.Avatar;
using UnitSport.Combat;

namespace UnitSport.Items;

/// <summary>
/// The sword's swing trails (#1371): for each move, set and seed one ribbon mesh along the path the blade really sweeps, built once
/// (the first time that swing is drawn) and never rebuilt. The blade's base and tip are sampled through the move's keyframe tables
/// (the spline poses the figure is drawn in, the same hand mount the sword is held by, the blade's own whip), in the character's space
/// as it stands when the strike begins, with the body's spin and lunge added, so the ribbon lies in the air exactly where the blade cut.
/// <c>HeldItemVisual</c> pins the ribbon in the world at the strike's start (it stays there while the body follows through), reveals it
/// along the arc with a shader parameter over the active frames and fades it out over 0.15 s.
/// </summary>
public static class SwordTrails
{
    /// <summary>Seconds the ribbon fades out over once the strike is done.</summary>
    public const float FadeSeconds = 0.15f;

    private const int Samples = 16;
    private static readonly Dictionary<int, ArrayMesh> Cache = new();
    private static ShaderMaterial? _material;

    private const string Code = @"
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;
uniform float reveal = 0.0;
uniform float fade = 1.0;
/* brighter on the finisher and the charged cut */
uniform float gain = 1.0;
uniform vec3 tint = vec3(1.0, 0.96, 0.84);
void fragment() {
    float u = UV.x;
    float v = UV.y;
    float inside = 1.0 - smoothstep(reveal - 0.03, reveal, u);
    float age = clamp((reveal - u) / 0.75, 0.0, 1.0);
    float tail = 1.0 - age * age;
    float width = smoothstep(0.0, 0.4, v) * (1.0 - 0.5 * smoothstep(0.88, 1.0, v));
    float edge = smoothstep(0.90, 0.985, v);
    ALBEDO = tint * (1.0 + edge * 0.6);
    ALPHA = clamp(inside * tail * (width + edge * 0.7) * fade * 0.85 * gain, 0.0, 1.0);
}";

    /// <summary>A material of its own for one fighter's ribbon (the parameters are set per frame, only when they change).</summary>
    public static ShaderMaterial NewMaterial()
    {
        _material ??= new ShaderMaterial { Shader = new Shader { Code = Code } };
        return (ShaderMaterial)_material.Duplicate();
    }

    /// <summary>The ribbon of <paramref name="move"/> in <paramref name="variant"/> with <paramref name="seed"/>'s jitter (cached).</summary>
    public static ArrayMesh Get(SwordMove move, int variant, int seed)
    {
        int key = (int)move | (variant << 4) | (seed << 6);
        if (Cache.TryGetValue(key, out var mesh)) return mesh;
        return Cache[key] = Build(move, variant, seed);
    }

    private static ArrayMesh Build(SwordMove move, int variant, int seed)
    {
        // the same samples the hits are decided on (SwordBlade), pinned in the world: the body's spin and lunge since the strike began are added
        var track = SwordBlade.Get(move, variant, seed);
        int n = SwordBlade.Count;
        var verts = new Vector3[n * 2];
        var uvs = new Vector2[n * 2];
        var idx = new int[(n - 1) * 6];
        float spin0 = SwordPoses.Sample(move, variant, seed, track.From).Spin;
        float lunge = SwordPoses.Lunge(move, variant);
        float lunge0 = SwordPoses.LungeDone(move, track.From);
        for (int i = 0; i < n; i++)
        {
            float p = Mathf.Lerp(track.From, track.To, i / (float)(n - 1));
            var turn = new Basis(Vector3.Up, SwordPoses.Sample(move, variant, seed, p).Spin - spin0);
            var step = Vector3.Forward * (lunge * (SwordPoses.LungeDone(move, p) - lunge0));
            // a wider ribbon (it starts nearer the hilt) on the finisher and the charged cut
            float inner = move is SwordMove.Slash5 or SwordMove.Charged ? 0.18f : 0.42f;
            verts[i * 2] = turn * track.Hilt[i].Lerp(track.Tip[i], inner) + step;
            verts[i * 2 + 1] = turn * track.Tip[i] + step;
            float u = i / (float)(n - 1);
            uvs[i * 2] = new Vector2(u, 0f);
            uvs[i * 2 + 1] = new Vector2(u, 1f);
        }
        for (int i = 0; i < n - 1; i++)
        {
            int a = i * 2, o = i * 6;
            idx[o] = a; idx[o + 1] = a + 1; idx[o + 2] = a + 2;
            idx[o + 3] = a + 1; idx[o + 4] = a + 3; idx[o + 5] = a + 2;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return m;
    }
}
