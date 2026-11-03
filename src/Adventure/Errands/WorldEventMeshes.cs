using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// The world events' models (#1339), each built once and shared: the avalanche's debris cone (a heap of snow blocks, 28 m across)
/// and the probe pole that marks a place dug out; the fire's flame and smoke quads with their shaders. The flames and the smoke are
/// camera-facing quads a vertex shader moves by the clock (<c>no-mesh-rebuild-per-frame</c>: a cell burning or going out only
/// sets one instance transform); the cone and the pole stand still. Authored facing +Z, feet on y = 0. All in the model viewer.
/// </summary>
public static class WorldEventMeshes
{
    private static Color C(string hex) { var c = new Color(hex); c.A = 0; return c; }

    private static readonly Color Snow = C("eef2f6"), Ice = C("b9cfe2"), Grey = C("9aa3ad"), Dirt = C("7d6a58"), Red = C("c8202a"), Yellow = C("f0c020");

    /// <summary>The debris cone's radius, m: the same as <see cref="AvalancheSim.ConeM"/>.</summary>
    public const float ConeRadius = AvalancheSim.ConeM;

    private static ArrayMesh? _debris, _pole;
    private static QuadMesh? _flame, _smoke;
    private static ShaderMaterial? _flameMaterial, _smokeMaterial;

    /// <summary>A heap of snow blocks and a few dirty stones, three rings of them climbing to a ridge, sunk 1.5 m so a slope never shows its underside.</summary>
    [Showcase("World events", "Avalanche debris")]
    public static ArrayMesh Debris() => _debris ??= BuildDebris();

    private static ArrayMesh BuildDebris()
    {
        var s = new MeshScratch();
        var rings = new (float R, float Y, int N, float Size)[] { (ConeRadius - 2.5f, 0.5f, 20, 3.4f), (ConeRadius * 0.55f, 1.9f, 12, 3.4f), (ConeRadius * 0.2f, 3.2f, 5, 3.0f) };
        int k = 0;
        foreach (var (r, y, n, size) in rings)
        {
            for (int i = 0; i < n; i++, k++)
            {
                double u = ErrandHash.Unit(ErrandHash.Of(0xDEB, k)), v = ErrandHash.Unit(ErrandHash.Of(0xDEC, k)), w = ErrandHash.Unit(ErrandHash.Of(0xDED, k));
                float a = (i + (float)u * 0.7f) * Mathf.Tau / n;
                float rr = r * (0.8f + 0.35f * (float)v);
                float sz = size * (0.6f + 0.7f * (float)w);
                var c = k % 7 == 0 ? Dirt : k % 3 == 0 ? Ice : k % 5 == 0 ? Grey : Snow;
                s.RoundedBox(new Vector3(Mathf.Sin(a) * rr, y + sz * 0.2f - 1.0f, Mathf.Cos(a) * rr), new Vector3(sz, sz * 0.7f, sz * 0.85f), c,
                    new Basis(Vector3.Up, (float)u * Mathf.Tau) * new Basis(Vector3.Right, ((float)v - 0.5f) * 0.5f));
            }
        }
        // a flat skirt of loose snow round the heap
        s.Box(new Vector3(0, -0.9f, 0), new Vector3(ConeRadius * 1.5f, 2.0f, ConeRadius * 1.5f), Snow);
        return s.Build();
    }

    /// <summary>An avalanche probe stuck in the snow: a red and yellow pole, 2.4 m, with a flag. It marks where somebody was dug out.</summary>
    [Showcase("World events", "Avalanche probe")]
    public static ArrayMesh Pole() => _pole ??= BuildPole();

    private static ArrayMesh BuildPole()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 2.4f, 0), 0.035f, 0.03f, Yellow, 5);
        s.Tube(new Vector3(0, 1.2f, 0), new Vector3(0, 1.7f, 0), 0.045f, 0.045f, Red, 5);
        s.Box(new Vector3(0.18f, 2.2f, 0), new Vector3(0.36f, 0.22f, 0.02f), Red);
        return s.Build();
    }

    // ---- the fire --------------------------------------------------------------------------------------------

    /// <summary>One flame: a quad 1.7 m wide and 2.8 m tall, standing on its centre point, turned to the camera by <see cref="FlameMaterial"/>.</summary>
    public static QuadMesh Flame() => _flame ??= new QuadMesh { Size = new Vector2(1.7f, 2.8f), CenterOffset = new Vector3(0, 1.4f, 0) };

    /// <summary>One puff of smoke: a quad 4 m square, rising and fading through <see cref="SmokeMaterial"/>.</summary>
    public static QuadMesh Smoke() => _smoke ??= new QuadMesh { Size = new Vector2(4f, 4f), CenterOffset = new Vector3(0, 2f, 0) };

    private const string Billboard =
        "MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);\n";

    /// <summary>Additive, unshaded; the flicker and the sway are the vertex shader's, from <c>TIME</c> and the instance number.</summary>
    public static ShaderMaterial FlameMaterial() => _flameMaterial ??= new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = "shader_type spatial;\nrender_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;\n"
                + "void vertex() {\n" + Billboard
                + "  float t = TIME * 7.0 + float(INSTANCE_ID) * 1.7;\n"
                + "  VERTEX.x += sin(t + VERTEX.y * 2.2) * 0.14 * VERTEX.y;\n"
                + "  VERTEX.y *= 0.85 + 0.2 * sin(t * 0.8);\n}\n"
                + "void fragment() {\n"
                + "  float x = abs(UV.x - 0.5) * 2.0;\n  float h = 1.0 - UV.y;\n"
                + "  float shape = 1.0 - smoothstep(0.0, 1.0, x / max(1.0 - h * 0.92, 0.06));\n"
                + "  float a = shape * smoothstep(0.0, 0.12, h) * (1.0 - smoothstep(0.75, 1.0, h));\n"
                + "  ALBEDO = mix(vec3(1.0, 0.86, 0.3), vec3(1.0, 0.32, 0.04), h);\n  ALPHA = a;\n}\n",
        },
    };

    /// <summary>Grey, unshaded, blended; each puff climbs 7 m on its own phase and thins out as it goes.</summary>
    public static ShaderMaterial SmokeMaterial() => _smokeMaterial ??= new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = "shader_type spatial;\nrender_mode unshaded, blend_mix, cull_disabled, depth_draw_never, fog_disabled;\n"
                + "varying float life;\n"
                + "void vertex() {\n" + Billboard
                + "  life = fract(TIME * 0.11 + float(INSTANCE_ID) * 0.37);\n"
                + "  VERTEX.y += life * 7.0 + 2.5;\n  VERTEX.xz *= 0.6 + life * 0.9;\n  VERTEX.x += sin(TIME * 0.6 + float(INSTANCE_ID)) * life * 1.5;\n}\n"
                + "void fragment() {\n"
                + "  vec2 d = UV - vec2(0.5);\n  float disc = 1.0 - smoothstep(0.2, 0.5, length(d));\n"
                + "  ALBEDO = mix(vec3(0.22, 0.2, 0.19), vec3(0.5, 0.5, 0.52), life);\n"
                + "  ALPHA = disc * (1.0 - life) * smoothstep(0.0, 0.1, life) * 0.55;\n}\n",
        },
    };

    [Showcase("World events", "Fire and smoke")]
    private static IEnumerable<(string, Func<Node3D>)> ShowFire() =>
    [
        ("Flames", () => Burn(withSmoke: false)),
        ("Flames and smoke", () => Burn(withSmoke: true)),
    ];

    /// <summary>A puff stands a little behind its flame: two quads on one plane would fight.</summary>
    public static readonly Vector3 SmokeShift = new(0.3f, 0f, 0.3f);

    private static Node3D Burn(bool withSmoke)
    {
        var root = new Node3D();
        for (int i = 0; i < 5; i++)
        {
            var at = new Vector3((i - 2) * 1.6f, 0, (i % 2) * 1.2f);
            root.AddChild(new MeshInstance3D { Mesh = Flame(), MaterialOverride = FlameMaterial(), Position = at, Name = "Flame" + i });
            if (withSmoke) root.AddChild(new MeshInstance3D { Mesh = Smoke(), MaterialOverride = SmokeMaterial(), Position = at + SmokeShift, Name = "Smoke" + i });
        }
        return root;
    }

    [Showcase("World events", "Avalanche site")]
    private static IEnumerable<(string, Func<Node3D>)> ShowSite() =>
    [
        ("Debris and probes", () =>
        {
            var root = new Node3D();
            root.AddChild(new MeshInstance3D { Mesh = Debris(), MaterialOverride = Rewards.RewardMeshes.Material(), Name = "Debris" });
            foreach (var p in new[] { new Vector3(-4, 1.2f, 2), new Vector3(3, 1.6f, -3) })
                root.AddChild(new MeshInstance3D { Mesh = Pole(), MaterialOverride = Rewards.RewardMeshes.Material(), Position = p, Name = "Probe" });
            return root;
        }),
    ];
}
