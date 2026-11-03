using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Farming;
using UnitSport.Terrain.Format;

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>
/// The Désalpe's models (#1269 part 7), built once and shared by every dressed herd: three flower headdresses in six colours (a wreath round the
/// horns, a straw hat with a band, a tall fan crown with a rosette), the leather collar with the big Treichel, and the alp sign and the fair's ring
/// that the festival dresses its ground with. A headdress is authored in the cow head's own frame (origin at the neck joint, the poll 0.2 m
/// above it), so it rides the head's transform in the herd's MultiMesh and never needs a rebuild: nothing here is built per frame or per pose.
/// </summary>
public static class DesalpeModels
{
    private static Color M(float r, float g, float b) => new(r, g, b, 0f);

    public static readonly string[] ShapeNames = ["bare", "wreath", "straw hat", "fan crown"];
    public static readonly string[] ColourNames = ["red", "yellow", "white", "pink", "blue", "orange"];

    private static readonly Color[] Flower =
    [
        M(0.86f, 0.12f, 0.14f), M(0.98f, 0.82f, 0.15f), M(0.95f, 0.95f, 0.91f), M(0.95f, 0.45f, 0.62f), M(0.25f, 0.45f, 0.86f), M(0.96f, 0.52f, 0.12f),
    ];

    private static readonly Color Leaf = M(0.22f, 0.5f, 0.2f), Straw = M(0.86f, 0.74f, 0.42f), StrawDark = M(0.7f, 0.58f, 0.3f);
    private static readonly Color Leather = M(0.34f, 0.18f, 0.1f), Brass = M(0.84f, 0.66f, 0.24f), BrassDark = M(0.6f, 0.44f, 0.14f), White = M(0.95f, 0.95f, 0.91f);

    /// <summary>The colour a dress shows in the HUD and the vote board.</summary>
    public static Color Accent(int colour) => Flower[Math.Clamp(colour, 0, Flower.Length - 1)] with { A = 1f };

    public static string Describe(byte dress) => dress == 0 ? "bare" : $"{ColourNames[DesalpeRules.ColourOf(dress)]} {ShapeNames[DesalpeRules.ShapeOf(dress)]}";

    private static readonly Dictionary<byte, ArrayMesh> Headdresses = new();
    private static ArrayMesh? _treichel, _sign, _ring;

    /// <summary>The headdress of a dress byte (not 0), in the head's frame.</summary>
    public static ArrayMesh Headdress(byte dress)
    {
        if (!Headdresses.TryGetValue(dress, out var mesh))
            Headdresses[dress] = mesh = BuildHeaddress(DesalpeRules.ShapeOf(dress), Flower[DesalpeRules.ColourOf(dress)]);
        return mesh;
    }

    private static ArrayMesh BuildHeaddress(int shape, Color accent)
    {
        // authored facing +Z like the head itself (Build turns it); the horns stand at x = ±0.14..0.26, y up to 0.36, the poll at (0, 0.08, 0.18)
        var s = new MeshScratch();
        switch (shape)
        {
            case 1:
                for (int k = 0; k < 12; k++)
                {
                    float a = k / 12f * Mathf.Tau, a2 = (k + 1) / 12f * Mathf.Tau;
                    var p = new Vector3(Mathf.Cos(a) * 0.27f, 0.34f, 0.19f + Mathf.Sin(a) * 0.20f);
                    var q = new Vector3(Mathf.Cos(a2) * 0.27f, 0.34f, 0.19f + Mathf.Sin(a2) * 0.20f);
                    s.Tube(p with { Y = 0.325f }, q with { Y = 0.325f }, 0.02f, 0.02f, Leaf, 5);
                    if (k % 2 == 0) s.Box(p, new Vector3(0.08f, 0.075f, 0.08f), accent, new Basis(Vector3.Up, -a));
                    else s.Box(p + new Vector3(0, 0.012f, 0), new Vector3(0.05f, 0.04f, 0.10f), Leaf, new Basis(Vector3.Up, -a));
                }
                // two ribbons hanging behind the horns over the neck
                s.Box(new Vector3(-0.1f, 0.2f, -0.02f), new Vector3(0.05f, 0.32f, 0.012f), accent);
                s.Box(new Vector3(0.1f, 0.19f, -0.025f), new Vector3(0.05f, 0.34f, 0.012f), White);
                break;
            case 2:
                s.Tube(new Vector3(0, 0.345f, 0.19f), new Vector3(0, 0.365f, 0.19f), 0.30f, 0.30f, Straw, 10);
                s.Tube(new Vector3(0, 0.36f, 0.19f), new Vector3(0, 0.46f, 0.19f), 0.15f, 0.10f, StrawDark, 10);
                s.Tube(new Vector3(0, 0.365f, 0.19f), new Vector3(0, 0.40f, 0.19f), 0.158f, 0.152f, accent, 10);
                for (int k = 0; k < 4; k++)
                {
                    float a = k / 4f * Mathf.Tau + 0.4f;
                    s.Box(new Vector3(Mathf.Cos(a) * 0.2f, 0.385f, 0.19f + Mathf.Sin(a) * 0.2f), new Vector3(0.07f, 0.07f, 0.07f), k % 2 == 0 ? White : accent, new Basis(Vector3.Up, -a));
                }
                break;
            default:
                s.Box(new Vector3(0, 0.36f, 0.17f), new Vector3(0.30f, 0.05f, 0.12f), Leather);
                for (int j = -2; j <= 2; j++)
                {
                    var top = new Vector3(j * 0.17f, 0.80f - Mathf.Abs(j) * 0.08f, 0.17f);
                    s.Tube(new Vector3(j * 0.06f, 0.37f, 0.17f), top, 0.022f, 0.018f, j % 2 == 0 ? Straw : White, 5);
                    s.Box(top + new Vector3(0, 0.03f, 0), new Vector3(0.10f, 0.10f, 0.06f), j % 2 == 0 ? accent : White);
                }
                s.Box(new Vector3(0, 0.60f, 0.20f), new Vector3(0.20f, 0.20f, 0.07f), accent);
                s.Box(new Vector3(0, 0.60f, 0.20f), new Vector3(0.09f, 0.09f, 0.09f), White);
                break;
        }
        return s.Build();
    }

    /// <summary>The leather collar with the big Treichel under it, hung from its top (the neck): scale it for the small and the middle bells.</summary>
    public static ArrayMesh Treichel() => _treichel ??= BuildTreichel();

    private static ArrayMesh BuildTreichel()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0, 0), new Vector3(0.58f, 0.12f, 0.26f), Leather);
        s.Box(new Vector3(0, 0, 0.13f), new Vector3(0.14f, 0.06f, 0.03f), White);
        s.Box(new Vector3(0, 0, 0.145f), new Vector3(0.05f, 0.03f, 0.03f), Flower[0]);
        s.Tube(new Vector3(-0.17f, -0.03f, 0), new Vector3(-0.07f, -0.07f, 0), 0.012f, 0.012f, Leather, 4);
        s.Tube(new Vector3(0.17f, -0.03f, 0), new Vector3(0.07f, -0.07f, 0), 0.012f, 0.012f, Leather, 4);
        s.Tube(new Vector3(0, -0.05f, 0), new Vector3(0, -0.29f, 0), 0.085f, 0.145f, Brass, 8);
        s.Tube(new Vector3(0, -0.25f, 0), new Vector3(0, -0.325f, 0), 0.152f, 0.158f, BrassDark, 8);
        s.Tube(new Vector3(0, -0.2f, 0), new Vector3(0, -0.37f, 0), 0.016f, 0.025f, BrassDark, 5);
        return s.Build();
    }

    // ---- the ground's props (placed by the festival's occasion) ------------------------------------

    private static readonly Color Wood = M(0.5f, 0.38f, 0.24f), WoodDark = M(0.38f, 0.28f, 0.18f), Red = M(0.82f, 0.10f, 0.12f);

    /// <summary>The Alpabzug sign at the foot of the alp: a signpost with a painted board and a Treichel hung from it, the place E starts the drive.</summary>
    public static ArrayMesh Sign() => _sign ??= BuildSign();

    private static ArrayMesh BuildSign()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(-0.9f, 0, 0), new Vector3(-0.9f, 2.99f, 0), 0.09f, 0.08f, WoodDark, 6);
        s.Tube(new Vector3(0.9f, 0, 0), new Vector3(0.9f, 2.99f, 0), 0.09f, 0.08f, WoodDark, 6);
        s.Box(new Vector3(0, 2.55f, 0), new Vector3(2.2f, 0.7f, 0.07f), Wood);
        s.Box(new Vector3(0, 2.55f, 0.04f), new Vector3(2.0f, 0.5f, 0.03f), White);
        // an arrow down the valley and a cow's head in red
        s.Box(new Vector3(-0.35f, 2.55f, 0.06f), new Vector3(0.9f, 0.1f, 0.03f), Red);
        s.Box(new Vector3(0.18f, 2.62f, 0.06f), new Vector3(0.1f, 0.1f, 0.03f), Red, new Basis(Vector3.Forward, 0.8f));
        s.Box(new Vector3(0.18f, 2.48f, 0.06f), new Vector3(0.1f, 0.1f, 0.03f), Red, new Basis(Vector3.Forward, -0.8f));
        s.Box(new Vector3(0.6f, 2.55f, 0.06f), new Vector3(0.34f, 0.28f, 0.03f), Red);
        // a big Treichel hanging under the board
        s.Tube(new Vector3(0, 2.2f, 0), new Vector3(0, 1.8f, 0), 0.14f, 0.24f, Brass, 8);
        s.Tube(new Vector3(0, 1.81f, 0), new Vector3(0, 1.74f, 0), 0.25f, 0.26f, BrassDark, 8);
        // flower garlands along the board's top
        for (int k = 0; k < 9; k++)
            s.Box(new Vector3(-1.0f + k * 0.25f, 2.93f, 0), new Vector3(0.12f, 0.1f, 0.1f), k % 3 == 0 ? Flower[0] : k % 3 == 1 ? Flower[1] : White);
        return s.Build();
    }

    /// <summary>The fair's ring: a round wooden fence with two gaps and a notice board of the herds, the place E votes.</summary>
    public static ArrayMesh Ring() => _ring ??= BuildRing();

    private static ArrayMesh BuildRing()
    {
        var s = new MeshScratch();
        const int Posts = 24;
        const float R = 11f;
        for (int k = 0; k < Posts; k++)
        {
            if (k % 12 is 0 or 1) continue;   // the gaps the herds walk in by
            float a = k / (float)Posts * Mathf.Tau, a2 = (k + 1) / (float)Posts * Mathf.Tau;
            var p = new Vector3(Mathf.Cos(a) * R, 0, Mathf.Sin(a) * R);
            var q = new Vector3(Mathf.Cos(a2) * R, 0, Mathf.Sin(a2) * R);
            s.Tube(p, p + new Vector3(0, 1.25f, 0), 0.07f, 0.06f, WoodDark, 5);
            if (k % 12 == 11 || k % 12 == 0) continue;
            s.Tube(p + new Vector3(0, 0.55f, 0), q + new Vector3(0, 0.55f, 0), 0.035f, 0.035f, Wood, 4);
            s.Tube(p + new Vector3(0, 0.98f, 0), q + new Vector3(0, 0.98f, 0), 0.035f, 0.035f, Wood, 4);
            if (k % 3 == 0) s.Box(p + new Vector3(0, 1.32f, 0), new Vector3(0.14f, 0.12f, 0.14f), Flower[k % Flower.Length]);
        }
        // the notice board at the middle: two posts, a board with a row of coloured rosettes
        s.Tube(new Vector3(-1.4f, 0, 0), new Vector3(-1.4f, 2.4f, 0), 0.07f, 0.065f, WoodDark, 5);
        s.Tube(new Vector3(1.4f, 0, 0), new Vector3(1.4f, 2.4f, 0), 0.07f, 0.065f, WoodDark, 5);
        s.Box(new Vector3(0, 1.9f, 0), new Vector3(2.5f, 0.9f, 0.06f), Wood);
        s.Box(new Vector3(0, 1.9f, 0.03f), new Vector3(2.3f, 0.7f, 0.03f), White);
        for (int k = 0; k < 6; k++)
            s.Box(new Vector3(-1.0f + k * 0.4f, 1.9f, 0.05f), new Vector3(0.22f, 0.22f, 0.03f), Flower[k]);
        return s.Build();
    }

    /// <summary>The alp sign as a prop of the occasion's tile decoration.</summary>
    public static readonly PropKind SignProp = new("FestivalDesalpeSign", () => Sign(), candle: false);

    /// <summary>The fair's ring as a prop of the occasion's tile decoration.</summary>
    public static readonly PropKind RingProp = new("FestivalDesalpeRing", () => Ring(), candle: false);

    /// <summary>Dresses the tile that owns the alp sign (at the route's start, facing up the alp) and the one that owns the fair's ring.</summary>
    public static void Decorate(ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        var route = DesalpeGround.RouteOf(site, FestivalCatalog.TestRequested ? DesalpeRules.TestRouteMeters : DesalpeRules.RouteMeters);
        var (se, sn) = DesalpeGround.Sign(site, route);
        var (tx, ty) = route.Tangent(0);
        // its board turned up the alp, towards the herd
        if (TileId.FromLv95(se, sn) == tile.Id && tile.HeightAt(tile.Local(se, sn)) is { } sh)
        {
            var local = tile.Local(se, sn);
            local.Y = sh;
            into.Add(SignProp, new Transform3D(new Basis(Vector3.Up, (float)Math.Atan2(tx, -ty)), local));
        }
        var (re, rn) = DesalpeGround.Ring(site);
        if (TileId.FromLv95(re, rn) == tile.Id && tile.HeightAt(tile.Local(re, rn)) is { } rh)
        {
            var local = tile.Local(re, rn);
            local.Y = rh;
            into.Add(RingProp, new Transform3D(Basis.Identity, local));
        }
    }

    // ---- the model viewer --------------------------------------------------------------------------

    /// <summary>Headdresses, the Treichel, dressed cows, the sign and the ring in the model viewer (--models).</summary>
    [Showcase("Festivals", "Désalpe")]
    private static IEnumerable<(string, Func<Node3D>)> Showcase()
    {
        var list = new List<(string, Func<Node3D>)>();
        for (int shape = 1; shape <= DesalpeRules.Shapes; shape++)
        {
            int sh = shape;
            list.Add(($"Dressed cow: {ShapeNames[sh]}", () => DressedCow(DesalpeRules.Dress(sh, sh * 2 - 2), bell: sh)));
        }
        list.Add(("Dressed Simmental, blue fan crown, Leitkuh", () => DressedCow(DesalpeRules.Dress(3, 4), bell: 3, CowBreed.Simmental)));
        list.Add(("Headdresses, all colours", HeaddressRow));
        list.Add(("Treichel", () => Mesh(Treichel(), 3f)));
        list.Add(("Alpabzug sign", () => Mesh(Sign())));
        list.Add(("Fair ring", () => Mesh(Ring())));
        return list;
    }

    private static MeshInstance3D Mesh(ArrayMesh mesh, float scale = 1f) =>
        new() { Mesh = mesh, MaterialOverride = Herds.Material(), Scale = Vector3.One * scale };

    /// <summary>A whole cow with its headdress on the head and its Treichel (size 1-3) at the neck: what a drawn herd shows, as nodes.</summary>
    public static Node3D DressedCow(byte dress, int bell, CowBreed breed = CowBreed.Brown)
    {
        var root = new Node3D { Name = $"Dressed{breed}" };
        var material = Herds.Material();
        root.AddChild(new MeshInstance3D { Name = "Body", Mesh = CowMeshBuilder.BodyWithoutBell(breed), MaterialOverride = material });
        var head = new MeshInstance3D { Name = "Head", Mesh = CowMeshBuilder.Head(breed), MaterialOverride = material, Position = CowMeshBuilder.Neck };
        root.AddChild(head);
        if (dress != 0) head.AddChild(new MeshInstance3D { Name = "Headdress", Mesh = Headdress(dress), MaterialOverride = material });
        root.AddChild(new MeshInstance3D
        {
            Name = "Treichel", Mesh = Treichel(), MaterialOverride = material, Position = TreichelAt,
            Basis = Basis.Identity.Scaled(Vector3.One * BellScale(bell)),
        });
        return root;
    }

    /// <summary>Where the collar hangs on the body, node space (the neck, a little forward of the withers).</summary>
    public static readonly Vector3 TreichelAt = new(0, 0.98f, -1.02f);

    public static float BellScale(int size) => size switch { 3 => 1.0f, 2 => 0.82f, _ => 0.66f };

    private static Node3D HeaddressRow()
    {
        var row = new Node3D { Name = "Headdresses" };
        var material = Herds.Material();
        for (int shape = 1; shape <= DesalpeRules.Shapes; shape++)
            for (int colour = 0; colour < DesalpeRules.Colours; colour++)
                row.AddChild(new MeshInstance3D
                {
                    Mesh = Headdress(DesalpeRules.Dress(shape, colour)), MaterialOverride = material,
                    Position = new Vector3((colour - 2.5f) * 1.5f, 0, (shape - 2) * 1.8f), Scale = Vector3.One * 1.6f,
                });
        return row;
    }
}
