using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The rewards' models (#1227, rewards plan "Models and sounds"), each built once and shared: the four chests (a
/// plain larch chest, iron bands, painted Bauernmalerei flowers, gilt with a Swiss cross), each a body and a lid
/// as two meshes so the lid turns as its own node (<c>general/no-mesh-rebuild-per-frame</c>); the seal band; an
/// edelweiss; a cairn and its top stone; the Höhenfeuer stack and its flame; a pressure plate; the cowbell rail
/// and a bell; a ring; a burrow mound and a vulture's perch; an offering box; a trial's start post and gate.
/// Authored facing +Z, feet on y = 0 (<see cref="MeshScratch"/> turns them to −Z); vertex alpha 0 (the prop
/// shader reads alpha as a lamp) except the flame's. Every one is in the model viewer.
/// </summary>
public static class RewardMeshes
{
    private static Color C(string hex) { var c = new Color(hex); c.A = 0; return c; }

    private static readonly Color Larch = C("b5834a"), LarchDark = C("8a5f30"), Iron = C("3a3c40"), Gold = C("e2b23a"), GoldDark = C("a87a1c"),
        Red = C("c8202a"), White = C("f2efe6"), Green = C("3f7a3a"), Blue = C("2f5f9e"), Yellow = C("f0c020"), Rock = C("8a8780"),
        RockDark = C("6a6862"), Soil = C("6e5434"), Grass = C("5e8a3e"), Brass = C("c99a3a"), Petal = C("f4f2ea"), Wood = C("7a5230");

    public const float ChestW = 0.9f, ChestD = 0.56f, ChestH = 0.44f, LidH = 0.2f;

    /// <summary>The lid's hinge in node space (the back top edge; the front, with its lock, faces node +Z): the lid node goes here and opens by a negative turn about X.</summary>
    public static readonly Vector3 LidHinge = new(0, ChestH, -ChestD / 2);

    private static readonly ArrayMesh?[] Bodies = new ArrayMesh?[4], Lids = new ArrayMesh?[4];

    public static ArrayMesh ChestBody(ChestTier t) => Bodies[(int)t] ??= BuildBody(t);
    public static ArrayMesh ChestLid(ChestTier t) => Lids[(int)t] ??= BuildLid(t);

    /// <summary>A tier's colour: the light shaft, the reveal card's header (amber stays the UI's own).</summary>
    public static Color TierColor(ChestTier t) => t switch
    {
        ChestTier.Common => new Color(0.86f, 0.80f, 0.68f),
        ChestTier.Fine => new Color(0.45f, 0.75f, 1.0f),
        ChestTier.Precious => new Color(0.78f, 0.45f, 1.0f),
        _ => new Color(1.0f, 0.82f, 0.25f),
    };

    public static string TierName(ChestTier t) => t switch
    {
        ChestTier.Common => "Common chest",
        ChestTier.Fine => "Fine chest",
        ChestTier.Precious => "Precious chest",
        _ => "Luxurious chest",
    };

    private static (Color Wood, Color Trim) Woods(ChestTier t) => t switch
    {
        ChestTier.Common => (Larch, LarchDark),
        ChestTier.Fine => (LarchDark, Iron),
        ChestTier.Precious => (Blue, Gold),
        _ => (Gold, GoldDark),
    };

    private static ArrayMesh BuildBody(ChestTier t)
    {
        var s = new MeshScratch();
        var (wood, trim) = Woods(t);
        s.Box(new Vector3(0, ChestH / 2, 0), new Vector3(ChestW, ChestH, ChestD), wood);
        // the feet, and the planks' seams as thin dark strips
        foreach (float x in new[] { -ChestW / 2 + 0.06f, ChestW / 2 - 0.06f })
            foreach (float z in new[] { -ChestD / 2 + 0.06f, ChestD / 2 - 0.06f })
                s.Box(new Vector3(x, 0.02f, z), new Vector3(0.1f, 0.04f, 0.1f), trim);
        if (t == ChestTier.Common)
            for (int i = 1; i < 3; i++) s.Box(new Vector3(0, ChestH * i / 3, -ChestD / 2 - 0.004f), new Vector3(ChestW, 0.012f, 0.01f), trim);
        else
        {
            // bands round the body (iron, gilt) and the lock plate on the front
            foreach (float x in new[] { -ChestW * 0.32f, ChestW * 0.32f })
                s.Box(new Vector3(x, ChestH / 2, 0), new Vector3(0.05f, ChestH + 0.01f, ChestD + 0.012f), trim);
            s.Box(new Vector3(0, ChestH - 0.08f, -ChestD / 2 - 0.01f), new Vector3(0.12f, 0.14f, 0.02f), t == ChestTier.Fine ? Iron : Gold);
        }
        if (t == ChestTier.Precious)
        {
            // Bauernmalerei: a red and white flower each side of the lock, on green stems
            foreach (float x in new[] { -0.2f, 0.2f })
            {
                var at = new Vector3(x, ChestH * 0.45f, -ChestD / 2 - 0.006f);
                s.Box(at + new Vector3(0, -0.06f, 0), new Vector3(0.012f, 0.1f, 0.006f), Green);
                foreach (var (dx, dy) in new[] { (0.03f, 0f), (-0.03f, 0f), (0f, 0.03f), (0f, -0.03f) })
                    s.Box(at + new Vector3(dx, dy, 0), new Vector3(0.03f, 0.03f, 0.008f), x < 0 ? Red : White);
                s.Box(at, new Vector3(0.025f, 0.025f, 0.012f), Yellow);
            }
        }
        if (t == ChestTier.Luxurious)
        {
            // the red front panel and its white cross, standing proud of each other (no z-fight)
            var c = new Vector3(0, ChestH * 0.42f, -ChestD / 2 - 0.008f);
            s.Box(c, new Vector3(0.3f, 0.22f, 0.012f), Red);
            s.Box(c + new Vector3(0, 0, -0.008f), new Vector3(0.16f, 0.05f, 0.008f), White);
            s.Box(c + new Vector3(0, 0, -0.008f), new Vector3(0.05f, 0.16f, 0.008f), White);
        }
        return s.Build();
    }

    private static ArrayMesh BuildLid(ChestTier t)
    {
        var s = new MeshScratch();
        var (wood, trim) = Woods(t);
        // a gently domed lid: two stacked boxes, the hinge at the back top edge of the body
        s.Box(new Vector3(0, ChestH + 0.07f, 0), new Vector3(ChestW + 0.02f, 0.14f, ChestD + 0.02f), wood);
        s.Box(new Vector3(0, ChestH + 0.17f, 0), new Vector3(ChestW + 0.02f, 0.06f, ChestD * 0.7f), wood);
        if (t != ChestTier.Common)
            foreach (float x in new[] { -ChestW * 0.32f, ChestW * 0.32f })
                s.Box(new Vector3(x, ChestH + 0.1f, 0), new Vector3(0.05f, 0.21f, ChestD + 0.03f), trim);
        s.Box(new Vector3(0, ChestH + 0.03f, -ChestD / 2 - 0.02f), new Vector3(0.08f, 0.07f, 0.03f), t == ChestTier.Common ? LarchDark : Gold);
        return s.Build(new Vector3(0, LidHinge.Y, -LidHinge.Z));
    }

    /// <summary>The seal's band of light round a sealed chest (drawn with <see cref="GlowMaterial"/>).</summary>
    public static ArrayMesh SealBand() => _band ??= Build(s =>
    {
        float y = ChestH * 0.55f;
        s.Box(new Vector3(0, y, -ChestD / 2 - 0.03f), new Vector3(ChestW + 0.06f, 0.05f, 0.01f), White);
        s.Box(new Vector3(0, y, ChestD / 2 + 0.03f), new Vector3(ChestW + 0.06f, 0.05f, 0.01f), White);
        s.Box(new Vector3(-ChestW / 2 - 0.03f, y, 0), new Vector3(0.01f, 0.05f, ChestD + 0.06f), White);
        s.Box(new Vector3(ChestW / 2 + 0.03f, y, 0), new Vector3(0.01f, 0.05f, ChestD + 0.06f), White);
    });

    /// <summary>The light shaft a chest sends up when it opens (an open tube, scaled and faded by a tween).</summary>
    public static ArrayMesh Shaft() => _shaft ??= Build(s => s.Tube(new Vector3(0, 0, 0), new Vector3(0, 6f, 0), 0.32f, 0.5f, White, 10));

    private static ArrayMesh? _band, _shaft, _edelweiss, _cairn, _stone, _stack, _flame, _plate, _rail, _bell, _ring, _mound, _perch, _box, _post, _gate;

    [Showcase("Adventure", "Edelweiss")]
    public static ArrayMesh Edelweiss() => _edelweiss ??= Build(s =>
    {
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 0.12f, 0), 0.006f, Grass, 4);
        // the woolly white star of bracts round a yellow head
        for (int i = 0; i < 7; i++)
        {
            float a = i * Mathf.Tau / 7;
            var dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            s.Box(new Vector3(0, 0.125f, 0) + dir * 0.035f, new Vector3(0.022f, 0.008f, 0.05f), Petal, new Basis(Vector3.Up, a));
        }
        s.Box(new Vector3(0, 0.132f, 0), new Vector3(0.028f, 0.014f, 0.028f), Yellow);
        foreach (float a in new[] { 0.6f, 2.8f, 4.6f })
            s.Box(new Vector3(Mathf.Sin(a) * 0.03f, 0.03f, Mathf.Cos(a) * 0.03f), new Vector3(0.012f, 0.006f, 0.05f), Grass, new Basis(Vector3.Up, a));
    });

    [Showcase("Adventure", "Cairn")]
    public static ArrayMesh Cairn() => _cairn ??= Build(s =>
    {
        float y = 0;
        var sizes = new[] { 0.62f, 0.5f, 0.42f, 0.34f, 0.26f };
        for (int i = 0; i < sizes.Length; i++)
        {
            float h = sizes[i] * 0.42f;
            s.RoundedBox(new Vector3(0.02f * (i % 2 == 0 ? 1 : -1), y + h / 2, 0.015f * i), new Vector3(sizes[i], h, sizes[i] * 0.85f),
                i % 2 == 0 ? Rock : RockDark, new Basis(Vector3.Up, i * 0.7f));
            y += h * 0.92f;
        }
    });

    /// <summary>The height of the cairn's top, where a player's stone goes.</summary>
    public const float CairnTop = 0.74f;

    [Showcase("Adventure", "Cairn stone")]
    public static ArrayMesh CairnStone() => _stone ??= Build(s => s.RoundedBox(new Vector3(0, 0.05f, 0), new Vector3(0.2f, 0.1f, 0.17f), RockDark));

    [Showcase("Adventure", "Höhenfeuer stack")]
    public static ArrayMesh WoodStack() => _stack ??= Build(s =>
    {
        // logs leaning into a cone over a ring of stones, as the 1 August fires are built
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.Tau / 8;
            var foot = new Vector3(Mathf.Sin(a) * 0.55f, 0, Mathf.Cos(a) * 0.55f);
            s.Tube(foot, new Vector3(0, 1.3f, 0), 0.06f, 0.04f, i % 2 == 0 ? Wood : LarchDark, 5);
            s.RoundedBox(foot * 1.25f + new Vector3(0, 0.07f, 0), new Vector3(0.18f, 0.14f, 0.16f), Rock);
        }
    });

    /// <summary>The fire of a lit stack: a lamp (vertex alpha 1), drawn with <see cref="GlowMaterial"/>.</summary>
    [Showcase("Adventure", "Höhenfeuer flame")]
    public static ArrayMesh Flame() => _flame ??= Build(s =>
    {
        var core = new Color(1f, 0.85f, 0.3f, 1f);
        var outer = new Color(1f, 0.45f, 0.08f, 1f);
        s.Tube(new Vector3(0, 0.2f, 0), new Vector3(0, 2.2f, 0), 0.5f, 0.02f, outer, 7);
        s.Tube(new Vector3(0, 0.3f, 0), new Vector3(0, 1.6f, 0), 0.28f, 0.02f, core, 6);
    });

    [Showcase("Adventure", "Pressure plate")]
    public static ArrayMesh Plate() => _plate ??= Build(s =>
    {
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 0.12f, 0), 1.25f, 1.15f, RockDark, 8);
        s.Tube(new Vector3(0, 0.12f, 0), new Vector3(0, 0.16f, 0), 0.9f, 0.85f, Rock, 8);
        // the carved cross on its face
        s.Box(new Vector3(0, 0.165f, 0), new Vector3(0.5f, 0.012f, 0.12f), RockDark);
        s.Box(new Vector3(0, 0.165f, 0), new Vector3(0.12f, 0.012f, 0.5f), RockDark);
    });

    [Showcase("Adventure", "Cowbell rail")]
    public static ArrayMesh BellRail() => _rail ??= Build(s =>
    {
        foreach (float x in new[] { -2.1f, 2.1f })
            s.Box(new Vector3(x, 0.9f, 0), new Vector3(0.12f, 1.8f, 0.12f), Wood);
        s.Box(new Vector3(0, 1.74f, 0), new Vector3(4.4f, 0.12f, 0.14f), Wood);
    });

    /// <summary>A cowbell hanging from its strap; its origin at the strap's top (the rail), so it swings as a node.</summary>
    [Showcase("Adventure", "Cowbell")]
    public static ArrayMesh Bell() => _bell ??= Build(s =>
    {
        s.Box(new Vector3(0, -0.12f, 0), new Vector3(0.09f, 0.24f, 0.02f), C("5a2c18"));
        s.Tube(new Vector3(0, -0.22f, 0), new Vector3(0, -0.5f, 0), 0.08f, 0.14f, Brass, 8);
        s.Box(new Vector3(0, -0.52f, 0), new Vector3(0.04f, 0.05f, 0.04f), Iron);
    });

    /// <summary>The height a bell hangs from (the rail's underside).</summary>
    public const float BellHang = 1.68f;

    /// <summary>A seal's ring (radius 1: its node is scaled to the ring's), standing upright, facing +Z.</summary>
    [Showcase("Adventure", "Ring")]
    public static ArrayMesh Ring() => _ring ??= Build(s => s.Ring(Vector3.Zero, Vector3.Forward, 0.88f, 1f, 0.08f, C("f0c020"), 20));

    [Showcase("Adventure", "Marmot burrow")]
    public static ArrayMesh Mound() => _mound ??= Build(s =>
    {
        s.RoundedBox(new Vector3(0, 0.15f, 0), new Vector3(1.4f, 0.3f, 1.2f), Soil);
        s.RoundedBox(new Vector3(0.1f, 0.12f, 0.45f), new Vector3(0.36f, 0.24f, 0.2f), C("2a2018"));
        s.RoundedBox(new Vector3(-0.4f, 0.32f, -0.2f), new Vector3(0.3f, 0.12f, 0.3f), Grass);
    });

    [Showcase("Adventure", "Vulture perch")]
    public static ArrayMesh Perch() => _perch ??= Build(s =>
    {
        s.RoundedBox(new Vector3(0, 0.5f, 0), new Vector3(1.6f, 1f, 1.3f), Rock);
        s.RoundedBox(new Vector3(0.3f, 1.05f, -0.1f), new Vector3(0.9f, 0.3f, 0.8f), RockDark);
        s.Box(new Vector3(0.2f, 1.21f, 0.1f), new Vector3(0.3f, 0.02f, 0.2f), White);   // what a vulture leaves
    });

    [Showcase("Adventure", "Offering box")]
    public static ArrayMesh OfferingBox() => _box ??= Build(s =>
    {
        s.Box(new Vector3(0, 0.5f, 0), new Vector3(0.1f, 1f, 0.1f), Wood);
        s.Box(new Vector3(0, 1.1f, 0), new Vector3(0.4f, 0.26f, 0.28f), LarchDark);
        s.Box(new Vector3(0, 1.27f, 0), new Vector3(0.46f, 0.06f, 0.34f), Wood);
        // the edelweiss carved on its front
        s.Box(new Vector3(0, 1.1f, -0.145f), new Vector3(0.12f, 0.12f, 0.01f), Petal);
        s.Box(new Vector3(0, 1.1f, -0.152f), new Vector3(0.04f, 0.04f, 0.01f), Yellow);
    });

    [Showcase("Adventure", "Trial start post")]
    public static ArrayMesh StartPost() => _post ??= Build(s =>
    {
        s.Box(new Vector3(0, 0.75f, 0), new Vector3(0.14f, 1.5f, 0.14f), Wood);
        // the waymark: a yellow diamond, and a red-white-red band for the mountain path
        s.Box(new Vector3(0, 1.35f, -0.075f), new Vector3(0.24f, 0.24f, 0.01f), Yellow, new Basis(Vector3.Forward, Mathf.Pi / 4));
        s.Box(new Vector3(0, 1.0f, -0.075f), new Vector3(0.15f, 0.05f, 0.012f), Red);
        s.Box(new Vector3(0, 0.95f, -0.075f), new Vector3(0.15f, 0.05f, 0.012f), White);
        s.Box(new Vector3(0, 0.9f, -0.075f), new Vector3(0.15f, 0.05f, 0.012f), Red);
        s.Box(new Vector3(0, 0.05f, 0), new Vector3(0.5f, 0.1f, 0.5f), Rock);
    });

    /// <summary>A trial's gate: a ring of radius 1 on two flag poles, scaled to the gate's radius.</summary>
    [Showcase("Adventure", "Trial gate")]
    public static ArrayMesh Gate() => _gate ??= Build(s =>
    {
        s.Ring(Vector3.Zero, Vector3.Forward, 0.94f, 1f, 0.02f, C("ff7a1a"), 24);
        s.Ring(Vector3.Zero, Vector3.Forward, 0.86f, 0.88f, 0.02f, White, 24);
    });

    private static IEnumerable<ChestTier> Tiers => new[] { ChestTier.Common, ChestTier.Fine, ChestTier.Precious, ChestTier.Luxurious };

    /// <summary>The four chests, closed, sealed and open, in the model viewer (--models).</summary>
    [Showcase("Adventure", "Chest")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseChests() =>
        Tiers.SelectMany(t => new (string, Func<Node3D>)[]
        {
            ($"{t}", () => ChestNode.Build(t, sealedBand: false, lidAngle: 0)),
            ($"{t} sealed", () => ChestNode.Build(t, sealedBand: true, lidAngle: 0)),
            ($"{t} open", () => ChestNode.Build(t, sealedBand: false, lidAngle: 1.9f)),
        });

    private static ShaderMaterial? _material;
    private static StandardMaterial3D? _glow;

    /// <summary>The props' material: the style kit's prop shader (Cartoon first), the fog applied.</summary>
    public static ShaderMaterial Material()
    {
        if (_material != null) return _material;
        _material = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        _material.SetShaderParameter("flicker", 0f);
        FogUniforms.Apply(_material);
        return _material;
    }

    /// <summary>What shines by itself: a seal's band, a flame, a lit ring (vertex colour, unshaded).</summary>
    public static StandardMaterial3D GlowMaterial() => _glow ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoColor = new Color(0.55f, 0.9f, 1f),
    };

    private static readonly StandardMaterial3D?[] Shafts = new StandardMaterial3D?[4];

    /// <summary>A tier's light shaft: additive and unshaded in the tier's colour; a tween of the instance's <c>Transparency</c> fades it.</summary>
    public static StandardMaterial3D ShaftMaterial(ChestTier t) => Shafts[(int)t] ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoColor = TierColor(t) with { A = 0.5f },
    };

    private static ArrayMesh Build(Action<MeshScratch> parts)
    {
        var s = new MeshScratch();
        parts(s);
        return s.Build();
    }
}
