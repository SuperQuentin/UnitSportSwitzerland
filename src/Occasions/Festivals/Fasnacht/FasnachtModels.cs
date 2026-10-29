using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals.Fasnacht;

/// <summary>
/// The Fasnacht's props (#1357), dressed by <see cref="FestivalOccasion.Decorate"/> like the other festivals': the Larven stall (a counter under a striped
/// awning with masks on pegs and sacks of Räppli), the Guggenmusik stage (a low stage with a bass drum, a snare, a trumpet's stand and a sousaphone) and, at Basel's,
/// the Vorlaarve lantern (a three-metre painted lantern on a cart, lit from within). One mesh each, built once and instanced by the tile, never rebuilt; the lanterns
/// are <see cref="Lantern"/> parts: they shine at night like a <see cref="PropColors.Lamp"/> but, unlike the street lamps, they stay lit at the Morgestraich.
/// </summary>
public static class FasnachtModels
{
    private static readonly Color Wood = Matte(0.50f, 0.36f, 0.22f), WoodDark = Matte(0.32f, 0.22f, 0.14f), Red = Matte(0.82f, 0.12f, 0.14f);
    private static readonly Color White = Matte(0.94f, 0.94f, 0.92f), Blue = Matte(0.14f, 0.30f, 0.72f), Gold = Matte(0.92f, 0.74f, 0.22f), Black = Matte(0.10f, 0.09f, 0.11f);
    private static readonly Color Skin = Matte(0.92f, 0.70f, 0.58f), Green = Matte(0.22f, 0.58f, 0.30f), Brass = Matte(0.86f, 0.66f, 0.18f), Cloth = Matte(0.78f, 0.62f, 0.38f);

    /// <summary>
    /// A lantern's glass: lit at night like <see cref="PropColors.Lamp"/> (the prop shader's lamp mask), but a hair under a full mask so the world's street
    /// lamps going out at the Morgestraich (<c>world_lamps</c>) leave it alone: the shader tells them apart by it.
    /// </summary>
    public static Color Lantern(float r, float g, float b) => new(r, g, b, LanternMask);

    /// <summary>The lamp mask of a lantern; <c>shaders/body/prop.gdshaderinc</c> keeps anything under 0.99 lit through the Morgestraich.</summary>
    public const float LanternMask = 0.97f;

    private static ArrayMesh? _stall, _stage, _lantern;

    public static readonly PropKind StallProp = new("FasnachtLarvenStall", () => Stall(), candle: false);
    public static readonly PropKind StageProp = new("FasnachtGuggenStage", () => Stage(), candle: false);
    public static readonly PropKind LanternProp = new("FasnachtVorlaarve", () => VorlaarveLantern(), candle: false);

    // ---- the Larven stall ---------------------------------------------------------------------------------------

    /// <summary>The Larven stall: a counter 3.2 m wide under a red-and-white striped awning, six papier-mache masks on pegs behind it, sacks of Räppli on the counter.</summary>
    [Showcase("Festivals", "Fasnacht Larven stall")]
    public static ArrayMesh Stall() => _stall ??= BuildStall();

    private static ArrayMesh BuildStall()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.5f, 0), new Vector3(3.2f, 1.0f, 0.9f), Wood);
        s.Box(new Vector3(0, 1.02f, 0), new Vector3(3.3f, 0.06f, 1.0f), WoodDark);
        foreach (float x in new[] { -1.55f, 1.55f })
            s.Tube(new Vector3(x, 0, -0.5f), new Vector3(x, 2.7f, -0.5f), 0.06f, 0.05f, WoodDark, 6);
        s.Box(new Vector3(0, 2.0f, -0.5f), new Vector3(3.2f, 1.8f, 0.05f), Cloth);   // the back wall
        // the awning: eight stripes sloping forward
        for (int k = 0; k < 8; k++)
        {
            float x = -1.4f + k * 0.4f;
            s.Box(new Vector3(x, 2.65f, 0.0f), new Vector3(0.4f, 0.05f, 1.5f), k % 2 == 0 ? Red : White, new Basis(Vector3.Right, 0.18f));
        }
        // six masks on pegs: a face shell, two dark eyes, a nose and a colour each
        Color[] tones = [Skin, Matte(0.92f, 0.82f, 0.72f), White, Red, Matte(0.93f, 0.62f, 0.52f), Matte(0.94f, 0.80f, 0.66f)];
        for (int k = 0; k < 6; k++)
        {
            float x = -1.25f + k * 0.5f;
            s.Box(new Vector3(x, 1.95f, -0.44f), new Vector3(0.28f, 0.34f, 0.08f), tones[k]);
            s.Box(new Vector3(x - 0.06f, 2.0f, -0.39f), new Vector3(0.06f, 0.04f, 0.03f), Black);
            s.Box(new Vector3(x + 0.06f, 2.0f, -0.39f), new Vector3(0.06f, 0.04f, 0.03f), Black);
            s.Box(new Vector3(x, 1.93f, -0.37f), new Vector3(0.05f, 0.09f, 0.07f), tones[k]);
            s.Box(new Vector3(x, 2.17f, -0.42f), new Vector3(0.22f, 0.07f, 0.07f), k % 2 == 0 ? Blue : Gold);
        }
        // sacks of Räppli on the counter, a little paper confetti spilled in front
        foreach (float x in new[] { -1.1f, -0.45f, 0.2f })
        {
            s.Box(new Vector3(x, 1.2f, 0.1f), new Vector3(0.4f, 0.32f, 0.3f), Cloth);
            s.Box(new Vector3(x, 1.38f, 0.1f), new Vector3(0.3f, 0.05f, 0.22f), x < -1f ? Red : x < 0f ? Gold : Blue);
        }
        Color[] paper = [Red, Gold, Blue, Green, White];
        for (int k = 0; k < 18; k++)
            s.Box(new Vector3(0.7f + (k % 6) * 0.17f, 1.07f, -0.2f + (k / 6) * 0.2f), new Vector3(0.06f, 0.01f, 0.06f), paper[k % paper.Length], new Basis(Vector3.Up, k * 0.8f));
        return s.Build();
    }

    // ---- the Guggenmusik stage -------------------------------------------------------------------------------------

    /// <summary>The Guggenmusik's stage: a plank stage a hand high with a bass drum and a snare on the left, a trumpet's music stand in the middle and a sousaphone on the right, bunting overhead.</summary>
    [Showcase("Festivals", "Fasnacht Guggenmusik stage")]
    public static ArrayMesh Stage() => _stage ??= BuildStage();

    private static ArrayMesh BuildStage()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.1f, 0), new Vector3(7f, 0.2f, 4f), Wood);
        s.Box(new Vector3(0, 0.22f, 0), new Vector3(6.6f, 0.04f, 3.6f), Matte(0.60f, 0.46f, 0.30f));
        // the bass drum, standing on its rim, and a snare on a stand
        s.Tube(new Vector3(-2.3f, 0.7f, -0.2f), new Vector3(-2.3f, 0.7f, 0.1f), 0.62f, 0.62f, Red, 12);
        s.Tube(new Vector3(-2.3f, 0.7f, 0.08f), new Vector3(-2.3f, 0.7f, 0.14f), 0.55f, 0.55f, White, 12);
        s.Box(new Vector3(-2.3f, 0.7f, -0.2f), new Vector3(1.3f, 0.06f, 0.06f), Gold);
        s.Tube(new Vector3(-1.2f, 0.25f, 0.6f), new Vector3(-1.2f, 0.78f, 0.6f), 0.03f, 0.03f, Black, 5);
        s.Tube(new Vector3(-1.2f, 0.78f, 0.6f), new Vector3(-1.2f, 0.92f, 0.6f), 0.26f, 0.26f, Red, 10);
        s.Tube(new Vector3(-1.2f, 0.91f, 0.6f), new Vector3(-1.2f, 0.94f, 0.6f), 0.24f, 0.24f, White, 10);
        // the trumpet's stand: a music stand with a banner, a trumpet leaning on it
        s.Tube(new Vector3(0.2f, 0.22f, 0.3f), new Vector3(0.2f, 1.5f, 0.3f), 0.03f, 0.03f, Black, 5);
        s.Box(new Vector3(0.2f, 1.6f, 0.3f), new Vector3(0.7f, 0.5f, 0.04f), White);
        s.Box(new Vector3(0.2f, 1.6f, 0.33f), new Vector3(0.5f, 0.06f, 0.02f), Red);
        s.Tube(new Vector3(0.7f, 0.9f, 0.4f), new Vector3(1.2f, 1.15f, 0.4f), 0.02f, 0.02f, Brass, 6);
        s.Tube(new Vector3(1.15f, 1.12f, 0.4f), new Vector3(1.4f, 1.3f, 0.4f), 0.03f, 0.12f, Brass, 8);
        // the sousaphone: a coiled brass body and a wide bell over the player's head
        s.Tube(new Vector3(2.3f, 0.4f, -0.1f), new Vector3(2.3f, 1.1f, -0.1f), 0.42f, 0.4f, Brass, 12);
        s.Tube(new Vector3(2.3f, 1.05f, -0.1f), new Vector3(2.3f, 1.5f, -0.1f), 0.4f, 0.26f, Brass, 12);
        s.Tube(new Vector3(2.3f, 1.45f, -0.1f), new Vector3(2.3f, 1.9f, -0.1f), 0.14f, 0.12f, Brass, 8);
        s.Tube(new Vector3(2.3f, 1.88f, -0.1f), new Vector3(2.3f, 2.2f, 0.35f), 0.12f, 0.5f, Brass, 12);
        // bunting along the front and the back
        for (int k = 0; k < 14; k++)
        {
            float x = -3.2f + k * 0.5f, sag = 0.2f * MathF.Sin(MathF.PI * k / 13f);
            Color c = new[] { Red, Gold, Blue, Green }[k % 4];
            s.Box(new Vector3(x, 2.5f - sag, 2.0f), new Vector3(0.3f, 0.3f, 0.02f), c, new Basis(Vector3.Forward, 0.8f));
            s.Box(new Vector3(x, 2.5f - sag, -2.0f), new Vector3(0.3f, 0.3f, 0.02f), c, new Basis(Vector3.Forward, 0.8f));
        }
        foreach (float x in new[] { -3.4f, 3.4f })
            foreach (float z in new[] { -2.0f, 2.0f })
                s.Tube(new Vector3(x, 0.2f, z), new Vector3(x, 2.7f, z), 0.05f, 0.04f, WoodDark, 5);
        return s.Build();
    }

    // ---- the Vorlaarve lantern -----------------------------------------------------------------------------------------

    /// <summary>Basel's Vorlaarve lantern: a 2.4 m box of painted glass panes (a Waggis on the front, blue-and-white chequers behind) on a two-wheeled cart, lit from within.</summary>
    [Showcase("Festivals", "Fasnacht Vorlaarve lantern")]
    public static ArrayMesh VorlaarveLantern() => _lantern ??= BuildLantern();

    private static ArrayMesh BuildLantern()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.42f, 0), new Vector3(1.8f, 0.14f, 1.4f), Wood);
        foreach (float x in new[] { -0.95f, 0.95f })
            s.Tube(new Vector3(x - 0.04f, 0.36f, 0), new Vector3(x + 0.04f, 0.36f, 0), 0.36f, 0.36f, WoodDark, 10);
        s.Box(new Vector3(0, 0.9f, 0), new Vector3(1.5f, 0.82f, 1.15f), Wood);                               // the cart's chest
        // the lantern: a black frame round four lit panes, a roof with a point
        foreach (float x in new[] { -0.7f, 0.7f })
            foreach (float z in new[] { -0.55f, 0.55f })
                s.Tube(new Vector3(x, 1.3f, z), new Vector3(x, 3.5f, z), 0.04f, 0.04f, Black, 5);
        s.Box(new Vector3(0, 2.4f, 0.54f), new Vector3(1.32f, 2.0f, 0.04f), Lantern(1f, 0.72f, 0.30f));      // the front pane, warm
        s.Box(new Vector3(0, 2.4f, -0.54f), new Vector3(1.32f, 2.0f, 0.04f), Lantern(0.5f, 0.66f, 1f));       // the back pane, blue and white
        s.Box(new Vector3(-0.68f, 2.4f, 0), new Vector3(0.04f, 2.0f, 1.04f), Lantern(1f, 0.55f, 0.35f));
        s.Box(new Vector3(0.68f, 2.4f, 0), new Vector3(0.04f, 2.0f, 1.04f), Lantern(1f, 0.55f, 0.35f));
        // the painting on the front: a Waggis's face, black lines on the glow
        s.Box(new Vector3(0, 2.5f, 0.57f), new Vector3(0.7f, 0.8f, 0.02f), Skin);
        s.Box(new Vector3(-0.15f, 2.65f, 0.585f), new Vector3(0.14f, 0.08f, 0.01f), Black);
        s.Box(new Vector3(0.15f, 2.65f, 0.585f), new Vector3(0.14f, 0.08f, 0.01f), Black);
        s.Box(new Vector3(0, 2.4f, 0.64f), new Vector3(0.14f, 0.34f, 0.16f), Red);
        s.Box(new Vector3(0, 2.1f, 0.585f), new Vector3(0.5f, 0.07f, 0.01f), Black);
        for (int k = 0; k < 4; k++)                                                                        // the chequers on the back
            for (int j = 0; j < 5; j++)
                if ((k + j) % 2 == 0) s.Box(new Vector3(-0.45f + k * 0.3f, 1.62f + j * 0.34f, -0.57f), new Vector3(0.28f, 0.32f, 0.01f), Blue);
        s.Tube(new Vector3(0, 3.4f, 0), new Vector3(0, 3.55f, 0), 0.95f, 0.95f, Black, 4);
        s.Tube(new Vector3(0, 3.5f, 0), new Vector3(0, 3.95f, 0), 0.9f, 0.05f, Red, 4);
        s.Box(new Vector3(0, 0.75f, 0.6f), new Vector3(1.1f, 0.3f, 0.02f), White);                           // the cart's lettering board
        return s.Build();
    }

    // ---- the tiles -----------------------------------------------------------------------------------------------------

    /// <summary>Dresses the tile that owns each prop: the stall, the stage and, at Basel's, the lantern.</summary>
    public static void Decorate(string festival, ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        var (se, sn) = FasnachtRules.StallAt(site);
        Place(StallProp, se, sn, MathF.PI / 2, tile, into);
        var (ge, gn) = FasnachtRules.StageAt(site);
        Place(StageProp, ge, gn, -MathF.PI / 2, tile, into);
        if (festival == FasnachtRules.BaselId)
        {
            var (le, ln) = FasnachtRules.LanternAt(site);
            Place(LanternProp, le, ln, 0f, tile, into);
        }
    }

    private static void Place(PropKind kind, double e, double n, float yaw, TileContext tile, DecorBuilder into)
    {
        if (TileId.FromLv95(e, n) != tile.Id) return;   // each prop belongs to one tile
        var local = tile.Local(e, n);
        if (tile.HeightAt(local) is not { } h) return;
        local.Y = h;
        into.Add(kind, new Transform3D(new Basis(Vector3.Up, yaw), local));
    }
}
