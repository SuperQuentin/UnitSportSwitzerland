using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals.Zibelemaerit;

/// <summary>
/// The Zibelemärit's props (#1373), dressed by <see cref="FestivalOccasion.Decorate"/> like the flag poles: four market stalls (a wooden booth with a striped
/// awning in Bern's red and black, a counter and what the stall sells: braids of onions hung from the awning, onion tarts on a rack, the Mehlsuppe kettle,
/// crates of confetti eggs). Each is one mesh built once and added to the tile's decoration as a transform (instanced by the tile, never rebuilt); the
/// lanterns are <see cref="PropColors.Lamp"/> parts, so they shine at night.
/// </summary>
public static class ZibeleModels
{
    private static readonly Color Wood = Matte(0.50f, 0.36f, 0.22f), WoodDark = Matte(0.34f, 0.23f, 0.14f);
    private static readonly Color Red = Matte(0.78f, 0.12f, 0.14f), Black = Matte(0.12f, 0.11f, 0.11f), White = Matte(0.95f, 0.94f, 0.90f);
    private static readonly Color Onion = Matte(0.78f, 0.55f, 0.22f), OnionRed = Matte(0.62f, 0.2f, 0.28f), Rope = Matte(0.52f, 0.4f, 0.22f);
    private static readonly Color Crust = Matte(0.70f, 0.50f, 0.25f), Filling = Matte(0.92f, 0.82f, 0.5f), Iron = Matte(0.22f, 0.22f, 0.24f), Soup = Matte(0.72f, 0.52f, 0.32f);
    private static readonly Color Shell = Matte(0.94f, 0.88f, 0.70f);

    private static readonly ArrayMesh?[] Stalls = new ArrayMesh?[ZibeleRules.Stalls];

    /// <summary>The stall of a kind, built once.</summary>
    public static ArrayMesh Stall(ZibeleStallKind kind) => Stalls[(int)kind] ??= BuildStall(kind);

    public static readonly PropKind[] StallProps =
    [
        new("FestivalZibeleZopf", () => Stall(ZibeleStallKind.Zopf), candle: false),
        new("FestivalZibeleChueche", () => Stall(ZibeleStallKind.Chueche), candle: false),
        new("FestivalZibeleMehlsuppe", () => Stall(ZibeleStallKind.Mehlsuppe), candle: false),
        new("FestivalZibeleKonfetti", () => Stall(ZibeleStallKind.Konfetti), candle: false),
    ];

    // ---- a stall ---------------------------------------------------------------------------------------------

    private static ArrayMesh BuildStall(ZibeleStallKind kind)
    {
        // authored with the counter on the +Z side (the front), the booth 3.2 m wide and 2.2 m deep
        var s = new MeshScratch();
        foreach (float x in new[] { -1.55f, 1.55f })
            foreach (float z in new[] { -1.0f, 1.0f })
                s.Tube(new Vector3(x, -0.03f, z), new Vector3(x, 2.45f, z), 0.07f, 0.07f, WoodDark, 5);
        s.Box(new Vector3(0, 1.25f, -1.0f), new Vector3(3.1f, 2.5f, 0.08f), Wood);
        s.Box(new Vector3(-1.55f, 1.25f, 0), new Vector3(0.08f, 2.5f, 2.0f), Wood);
        s.Box(new Vector3(1.55f, 1.25f, 0), new Vector3(0.08f, 2.5f, 2.0f), Wood);
        // the counter
        s.Box(new Vector3(0, 1.02f, 0.95f), new Vector3(3.3f, 0.08f, 0.7f), WoodDark);
        s.Box(new Vector3(0, 0.5f, 1.22f), new Vector3(3.1f, 1.0f, 0.08f), Wood);
        s.Box(new Vector3(0, 0.5f, 0.8f), new Vector3(3.0f, 1.0f, 0.06f), WoodDark);
        // a flat roof with a front board carrying the stall's colour
        s.Box(new Vector3(0, 2.55f, 0.1f), new Vector3(3.5f, 0.08f, 2.5f), WoodDark);   // its front stops short of the front board (no coplanar faces)
        s.Box(new Vector3(0, 2.8f, 1.42f), new Vector3(3.3f, 0.45f, 0.06f), kind switch { ZibeleStallKind.Zopf => Onion, ZibeleStallKind.Chueche => Crust, ZibeleStallKind.Mehlsuppe => OnionRed, _ => Red });
        s.Box(new Vector3(0, 2.8f, 1.46f), new Vector3(2.4f, 0.14f, 0.03f), White);
        // the striped awning (red and black, Bern's colours) over the counter
        for (int k = 0; k < 8; k++)
            s.Box(new Vector3(-1.5f + (k + 0.5f) * 0.4f, 2.4f, 1.4f), new Vector3(0.4f, 0.04f, 1.0f), k % 2 == 0 ? Red : Black, new Basis(Vector3.Right, 0.3f));
        // two lanterns at the awning's corners: lit at night
        foreach (float x in new[] { -1.45f, 1.45f })
            s.Box(new Vector3(x, 2.15f, 1.78f), new Vector3(0.14f, 0.2f, 0.14f), Lamp(1f, 0.78f, 0.42f));
        Ware(s, kind);
        return s.Build();
    }

    private static void Onions(MeshScratch s, float x, float y, float z, int count)
    {
        // a braid: a rope with onions tied up it, each a little round, the red ones among the gold
        s.Tube(new Vector3(x, y - 0.1f * count, z), new Vector3(x, y, z), 0.012f, 0.012f, Rope, 4);
        for (int k = 0; k < count; k++)
        {
            float a = k * 1.3f, oy = y - 0.1f * count + 0.05f + k * 0.1f;
            var c = k % 3 == 0 ? OnionRed : Onion;
            s.Tube(new Vector3(x + MathF.Cos(a) * 0.05f, oy, z + MathF.Sin(a) * 0.05f), new Vector3(x + MathF.Cos(a) * 0.05f, oy + 0.11f, z + MathF.Sin(a) * 0.05f), 0.06f, 0.04f, c, 7);
        }
    }

    private static void Ware(MeshScratch s, ZibeleStallKind kind)
    {
        switch (kind)
        {
            case ZibeleStallKind.Zopf:
                // braids hanging from the awning in a row, a heap of loose onions on the counter
                for (int k = 0; k < 9; k++) Onions(s, -1.4f + k * 0.35f, 2.36f, 1.15f, 4 + k % 3);
                for (int k = 0; k < 14; k++)
                    s.Tube(new Vector3(-0.9f + (k % 7) * 0.28f, 1.06f + (k / 7) * 0.1f, 0.95f - (k / 7) * 0.12f), new Vector3(-0.9f + (k % 7) * 0.28f, 1.17f + (k / 7) * 0.1f, 0.95f - (k / 7) * 0.12f), 0.08f, 0.05f, k % 4 == 0 ? OnionRed : Onion, 7);
                break;
            case ZibeleStallKind.Chueche:
                // round onion tarts on the counter and a rack of them behind
                for (int k = 0; k < 4; k++)
                {
                    float x = -1.0f + k * 0.65f;
                    s.Tube(new Vector3(x, 1.06f, 0.95f), new Vector3(x, 1.11f, 0.95f), 0.26f, 0.26f, Crust, 12);
                    s.Tube(new Vector3(x, 1.11f, 0.95f), new Vector3(x, 1.12f, 0.95f), 0.22f, 0.22f, Filling, 12);
                }
                s.Box(new Vector3(0, 1.7f, -0.8f), new Vector3(2.6f, 0.05f, 0.5f), WoodDark);
                for (int k = 0; k < 5; k++) s.Tube(new Vector3(-1.0f + k * 0.5f, 1.73f, -0.8f), new Vector3(-1.0f + k * 0.5f, 1.78f, -0.8f), 0.2f, 0.2f, Crust, 10);
                break;
            case ZibeleStallKind.Mehlsuppe:
                // the big iron kettle on a stand with its steam bar, bowls in a stack beside it
                s.Tube(new Vector3(0, 1.06f, 0.85f), new Vector3(0, 1.55f, 0.85f), 0.38f, 0.34f, Iron, 12);
                s.Tube(new Vector3(0, 1.55f, 0.85f), new Vector3(0, 1.57f, 0.85f), 0.33f, 0.33f, Soup, 12);
                s.Box(new Vector3(0.45f, 1.5f, 0.85f), new Vector3(0.18f, 0.05f, 0.05f), Iron);
                s.Box(new Vector3(0, 1.6f, 0.85f), new Vector3(0.1f, 0.06f, 0.1f), Lamp(1f, 0.45f, 0.2f));   // the embers' glow under the lid
                for (int k = 0; k < 5; k++) s.Tube(new Vector3(1.0f, 1.06f + k * 0.07f, 0.95f), new Vector3(1.0f, 1.12f + k * 0.07f, 0.95f), 0.1f, 0.12f, White, 8);
                break;
            default:
                // crates of confetti eggs on the counter, a bunting of paper along the awning
                for (int k = 0; k < 3; k++)
                {
                    float x = -0.9f + k * 0.9f;
                    s.Box(new Vector3(x, 1.18f, 0.95f), new Vector3(0.6f, 0.2f, 0.4f), WoodDark);
                    for (int e = 0; e < 6; e++)
                        s.Tube(new Vector3(x - 0.2f + (e % 3) * 0.2f, 1.26f, 0.85f + (e / 3) * 0.2f), new Vector3(x - 0.2f + (e % 3) * 0.2f, 1.37f, 0.85f + (e / 3) * 0.2f), 0.06f, 0.07f, Shell, 7);
                }
                for (int k = 0; k < 12; k++)
                {
                    var c = (k % 4) switch { 0 => Red, 1 => Matte(0.95f, 0.8f, 0.2f), 2 => Matte(0.2f, 0.4f, 0.85f), _ => Matte(0.2f, 0.65f, 0.3f) };
                    s.Box(new Vector3(-1.45f + k * 0.26f, 2.28f - 0.04f * MathF.Sin(MathF.PI * k / 11f), 1.82f), new Vector3(0.18f, 0.2f, 0.02f), c);
                }
                break;
        }
    }

    // ---- the tiles -------------------------------------------------------------------------------------------

    /// <summary>Dresses the tile that owns each stall (counter towards the lane).</summary>
    public static void Decorate(ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        foreach (var st in ZibeleRules.Layout(site))
        {
            if (TileId.FromLv95(st.E, st.N) != tile.Id) continue;   // each prop belongs to one tile
            var local = tile.Local(st.E, st.N);
            if (tile.HeightAt(local) is not { } h) continue;
            local.Y = h;
            into.Add(StallProps[(int)st.Kind], new Transform3D(new Basis(Vector3.Up, st.Yaw), local));
        }
    }

    // ---- the model viewer ------------------------------------------------------------------------------------

    [Showcase("Festivals", "Zibelemärit stall: Zwiebelzopf")]
    private static ArrayMesh ZopfStall() => Stall(ZibeleStallKind.Zopf);

    [Showcase("Festivals", "Zibelemärit stall: Zibelechueche")]
    private static ArrayMesh ChuecheStall() => Stall(ZibeleStallKind.Chueche);

    [Showcase("Festivals", "Zibelemärit stall: Mehlsuppe")]
    private static ArrayMesh MehlsuppeStall() => Stall(ZibeleStallKind.Mehlsuppe);

    [Showcase("Festivals", "Zibelemärit stall: Konfetti")]
    private static ArrayMesh KonfettiStall() => Stall(ZibeleStallKind.Konfetti);
}
