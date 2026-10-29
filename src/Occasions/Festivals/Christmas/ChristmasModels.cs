using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals.Christmas;

/// <summary>
/// The Christmas market's props (#1348), dressed by <see cref="FestivalOccasion.Decorate"/> like the flag poles: four kinds of stall (a wooden hut with a
/// gabled roof, a striped awning, a counter and the ware it sells), the Samichlaus and the market's tree. Each is one mesh built once and added to the tile's
/// decoration as a transform (instanced by the tile, never rebuilt); the lanterns and baubles are <see cref="PropColors.Lamp"/> parts, so they shine at night.
/// </summary>
public static class ChristmasModels
{
    private static readonly Color Wood = Matte(0.50f, 0.36f, 0.22f), WoodDark = Matte(0.34f, 0.23f, 0.14f), Roof = Matte(0.36f, 0.30f, 0.27f);
    private static readonly Color Red = Matte(0.80f, 0.12f, 0.14f), White = Matte(0.95f, 0.94f, 0.90f), Green = Matte(0.14f, 0.40f, 0.20f), Gold = Matte(0.86f, 0.68f, 0.22f);
    private static readonly Color Skin = Matte(0.88f, 0.66f, 0.54f), Cheese = Matte(0.96f, 0.80f, 0.34f), Bread = Matte(0.76f, 0.52f, 0.28f), Copper = Matte(0.72f, 0.42f, 0.22f);
    private static readonly Color Mug = Matte(0.92f, 0.90f, 0.84f), Wine = Matte(0.50f, 0.08f, 0.14f);

    private static readonly Color[] Sign =
    [
        Matte(0.55f, 0.10f, 0.16f), Matte(0.92f, 0.76f, 0.28f), Matte(0.80f, 0.60f, 0.36f), Matte(0.22f, 0.40f, 0.72f),
    ];

    private static readonly ArrayMesh?[] Stalls = new ArrayMesh?[4];
    private static ArrayMesh? _samichlaus, _tree;

    /// <summary>The stall of a kind, built once.</summary>
    public static ArrayMesh Stall(StallKind kind) => Stalls[(int)kind] ??= BuildStall(kind);

    public static readonly PropKind[] StallProps =
    [
        new("FestivalStallGluehwein", () => Stall(StallKind.Gluehwein), candle: false),
        new("FestivalStallRaclette", () => Stall(StallKind.Raclette), candle: false),
        new("FestivalStallBakery", () => Stall(StallKind.Gritibaenz), candle: false),
        new("FestivalStallOrnaments", () => Stall(StallKind.Ornaments), candle: false),
    ];

    public static readonly PropKind SamichlausProp = new("FestivalSamichlaus", () => Samichlaus(), candle: false);
    public static readonly PropKind TreeProp = new("FestivalMarketTree", () => Tree(), candle: false);

    // ---- a stall ---------------------------------------------------------------------------------------------

    private static ArrayMesh BuildStall(StallKind kind)
    {
        // authored with the counter on the +Z side (the front), the hut 3.2 m wide and 2.2 m deep
        var s = new MeshScratch();
        foreach (float x in new[] { -1.55f, 1.55f })
            foreach (float z in new[] { -1.0f, 1.0f })
                s.Tube(new Vector3(x, -0.03f, z), new Vector3(x, 2.45f, z), 0.07f, 0.07f, WoodDark, 5);   // a little foot below the walls' bottom faces
        s.Box(new Vector3(0, 1.25f, -1.0f), new Vector3(3.1f, 2.5f, 0.08f), Wood);
        s.Box(new Vector3(-1.55f, 1.25f, 0), new Vector3(0.08f, 2.5f, 2.0f), Wood);
        s.Box(new Vector3(1.55f, 1.25f, 0), new Vector3(0.08f, 2.5f, 2.0f), Wood);
        // the counter and its front
        s.Box(new Vector3(0, 1.02f, 0.95f), new Vector3(3.3f, 0.08f, 0.7f), WoodDark);
        s.Box(new Vector3(0, 0.5f, 1.22f), new Vector3(3.1f, 1.0f, 0.08f), Wood);
        s.Box(new Vector3(0, 0.5f, 0.8f), new Vector3(3.0f, 1.0f, 0.06f), WoodDark);
        // the gabled roof and the sign on its front
        s.Box(new Vector3(-0.8f, 2.8f, 0.1f), new Vector3(1.9f, 0.08f, 2.9f), Roof, new Basis(Vector3.Back, 0.38f));
        s.Box(new Vector3(0.8f, 2.8f, 0.1f), new Vector3(1.9f, 0.08f, 2.9f), Roof, new Basis(Vector3.Back, -0.38f));
        s.Box(new Vector3(0, 3.12f, 1.55f), new Vector3(1.9f, 0.4f, 0.06f), Sign[(int)kind]);
        s.Box(new Vector3(0, 3.12f, 1.59f), new Vector3(1.7f, 0.12f, 0.03f), White);
        // the striped awning over the counter
        for (int k = 0; k < 8; k++)
            s.Box(new Vector3(-1.5f + (k + 0.5f) * 0.4f, 2.42f, 1.4f), new Vector3(0.4f, 0.04f, 1.0f), k % 2 == 0 ? Red : White, new Basis(Vector3.Right, 0.3f));
        // two lanterns at the awning's corners and one inside: lit at night
        foreach (float x in new[] { -1.45f, 1.45f })
            s.Box(new Vector3(x, 2.15f, 1.78f), new Vector3(0.14f, 0.2f, 0.14f), Lamp(1f, 0.78f, 0.42f));
        s.Box(new Vector3(0, 2.1f, 0.2f), new Vector3(0.16f, 0.22f, 0.16f), Lamp(1f, 0.78f, 0.42f));
        // a fir garland along the front beam
        for (int k = 0; k < 9; k++)
        {
            float x = -1.5f + k * 0.375f;
            s.Box(new Vector3(x, 2.3f - 0.05f * MathF.Sin(MathF.PI * k / 8f), 1.86f), new Vector3(0.28f, 0.1f, 0.1f), Green);
            if (k % 2 == 0) s.Box(new Vector3(x, 2.22f, 1.9f), new Vector3(0.07f, 0.07f, 0.07f), Lamp(0.95f, 0.2f, 0.18f));
        }
        Ware(s, kind);
        return s.Build();
    }

    private static void Ware(MeshScratch s, StallKind kind)
    {
        switch (kind)
        {
            case StallKind.Gluehwein:
                // the copper pot over a warmer with its steam, and a row of mugs
                s.Tube(new Vector3(0, 1.06f, 0.85f), new Vector3(0, 1.5f, 0.85f), 0.3f, 0.28f, Copper, 10);
                s.Tube(new Vector3(0, 1.5f, 0.85f), new Vector3(0, 1.52f, 0.85f), 0.26f, 0.26f, Wine, 10);
                s.Box(new Vector3(0.3f, 1.42f, 0.85f), new Vector3(0.1f, 0.05f, 0.05f), Copper);
                for (int k = 0; k < 6; k++)
                {
                    float x = -1.35f + k * 0.2f;
                    s.Tube(new Vector3(x, 1.06f, 0.95f), new Vector3(x, 1.2f, 0.95f), 0.05f, 0.055f, Mug, 7);
                }
                for (int k = 0; k < 4; k++) s.Tube(new Vector3(1.0f + k * 0.0f, 1.06f + k * 0.0f, 0.95f), new Vector3(1.0f + (k % 2) * 0.16f, 1.2f, 0.95f - (k / 2) * 0.16f), 0.05f, 0.055f, Mug, 7);
                break;
            case StallKind.Raclette:
                // half a wheel of cheese on the grill, a stack of whole ones beside it
                s.Box(new Vector3(-0.5f, 1.12f, 0.95f), new Vector3(0.9f, 0.1f, 0.4f), WoodDark);
                s.Tube(new Vector3(-0.5f, 1.17f, 0.95f), new Vector3(-0.5f, 1.3f, 0.95f), 0.26f, 0.25f, Cheese, 10);
                s.Box(new Vector3(-0.5f, 1.4f, 0.95f), new Vector3(0.9f, 0.04f, 0.04f), Lamp(1f, 0.45f, 0.2f));
                for (int k = 0; k < 3; k++)
                    s.Tube(new Vector3(0.7f, 1.06f + k * 0.14f, 0.9f), new Vector3(0.7f, 1.18f + k * 0.14f, 0.9f), 0.3f, 0.3f, Cheese, 10);
                for (int k = 0; k < 4; k++) s.Box(new Vector3(1.3f, 1.14f, 0.7f + k * 0.0f), new Vector3(0.16f, 0.12f, 0.16f), Matte(0.6f, 0.4f, 0.22f));
                break;
            case StallKind.Gritibaenz:
                // a tray of little bread men, a ladder of loaves behind
                for (int k = 0; k < 6; k++)
                {
                    float x = -1.2f + k * 0.42f;
                    s.Box(new Vector3(x, 1.2f, 0.95f), new Vector3(0.14f, 0.26f, 0.08f), Bread);
                    s.Box(new Vector3(x, 1.38f, 0.95f), new Vector3(0.11f, 0.11f, 0.08f), Bread);
                    s.Box(new Vector3(x, 1.27f, 0.95f), new Vector3(0.3f, 0.07f, 0.07f), Bread);
                }
                s.Box(new Vector3(0, 1.7f, -0.8f), new Vector3(2.6f, 0.05f, 0.4f), WoodDark);
                for (int k = 0; k < 5; k++) s.Box(new Vector3(-1.0f + k * 0.5f, 1.82f, -0.8f), new Vector3(0.36f, 0.18f, 0.24f), Bread);
                break;
            default:
                // baubles hanging from the awning in red, gold, green and blue, a few of them catching the light
                for (int k = 0; k < 10; k++)
                {
                    float x = -1.4f + k * 0.31f, drop = 0.18f + 0.1f * (k % 3);
                    s.Tube(new Vector3(x, 2.4f, 1.15f), new Vector3(x, 2.4f - drop, 1.15f), 0.004f, 0.004f, Gold, 3);
                    var c = (k % 4) switch { 0 => Red, 1 => Gold, 2 => Green, _ => Matte(0.25f, 0.42f, 0.8f) };
                    s.Box(new Vector3(x, 2.4f - drop - 0.06f, 1.15f), new Vector3(0.11f, 0.11f, 0.11f), k % 3 == 0 ? Lamp(c.R, c.G, c.B) : c);
                }
                for (int k = 0; k < 5; k++) s.Box(new Vector3(-1.1f + k * 0.5f, 1.14f, 0.95f), new Vector3(0.22f, 0.16f, 0.22f), k % 2 == 0 ? Red : Gold);
                break;
        }
    }

    // ---- the Samichlaus --------------------------------------------------------------------------------------

    /// <summary>The Samichlaus: a red robe with white fur, a white beard, the bishop's mitre, a staff and the jute sack. Authored facing +Z.</summary>
    [Showcase("Festivals", "Samichlaus")]
    public static ArrayMesh Samichlaus() => _samichlaus ??= BuildSamichlaus(true);

    private static ArrayMesh BuildSamichlaus(bool unused = true)
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, 0.1f, 0), new Vector3(0, 1.35f, 0), 0.41f, 0.26f, Red, 10);
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 0.1f, 0), 0.44f, 0.44f, White, 10);   // the fur hem
        s.Box(new Vector3(0, 1.0f, 0.22f), new Vector3(0.12f, 0.9f, 0.05f), White);       // the fur front
        s.Tube(new Vector3(0, 0.95f, 0), new Vector3(0, 1.0f, 0), 0.34f, 0.34f, WoodDark, 10);   // the belt
        s.Tube(new Vector3(0, 1.35f, 0), new Vector3(0, 1.45f, 0), 0.27f, 0.2f, White, 10);       // the collar
        s.Box(new Vector3(0, 1.58f, 0.02f), new Vector3(0.24f, 0.26f, 0.22f), Skin);
        s.Box(new Vector3(0, 1.42f, 0.14f), new Vector3(0.3f, 0.34f, 0.12f), White);              // the beard
        s.Box(new Vector3(0, 1.72f, 0.02f), new Vector3(0.28f, 0.06f, 0.26f), White);            // the brows' fur
        s.Tube(new Vector3(0, 1.74f, 0), new Vector3(0, 2.15f, 0), 0.15f, 0.07f, Red, 8);         // the mitre
        s.Box(new Vector3(0, 1.95f, 0.1f), new Vector3(0.05f, 0.24f, 0.02f), Gold);
        s.Box(new Vector3(0, 1.97f, 0.1f), new Vector3(0.16f, 0.05f, 0.02f), Gold);
        s.Tube(new Vector3(-0.38f, 1.3f, 0.05f), new Vector3(-0.5f, 0.82f, 0.18f), 0.07f, 0.06f, Red, 6);   // the arms
        s.Tube(new Vector3(0.38f, 1.3f, 0.05f), new Vector3(0.5f, 0.82f, 0.18f), 0.07f, 0.06f, Red, 6);
        s.Box(new Vector3(-0.5f, 0.82f, 0.18f), new Vector3(0.15f, 0.15f, 0.15f), White);   // a cuff round each arm's end, so no cap is flush with a face
        s.Box(new Vector3(0.5f, 0.82f, 0.18f), new Vector3(0.15f, 0.15f, 0.15f), White);
        // the crook in his right hand
        s.Tube(new Vector3(0.55f, 0, 0.22f), new Vector3(0.55f, 2.0f, 0.22f), 0.03f, 0.03f, Gold, 5);
        s.Tube(new Vector3(0.55f, 2.0f, 0.22f), new Vector3(0.43f, 2.12f, 0.22f), 0.03f, 0.03f, Gold, 5);
        // the sack at his feet, tied with a cord, a lantern at its top
        s.Tube(new Vector3(-0.7f, 0.02f, 0.1f), new Vector3(-0.7f, 0.7f, 0.1f), 0.32f, 0.16f, Matte(0.62f, 0.5f, 0.34f), 9);
        s.Tube(new Vector3(-0.7f, 0.68f, 0.1f), new Vector3(-0.7f, 0.74f, 0.1f), 0.1f, 0.1f, Red, 6);
        s.Box(new Vector3(0.0f, 1.05f, 0.4f), new Vector3(0.12f, 0.16f, 0.12f), Lamp(1f, 0.78f, 0.42f));
        return s.Build();
    }

    // ---- the market's tree -----------------------------------------------------------------------------------

    /// <summary>A 7 m fir in four tiers with gold and red baubles (which shine at night) and a star: the market's landmark.</summary>
    [Showcase("Festivals", "Christmas market tree")]
    public static ArrayMesh Tree() => _tree ??= BuildTree(true);

    private static ArrayMesh BuildTree(bool unused = true)
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 1.4f, 0), 0.22f, 0.18f, WoodDark, 6);
        for (int tier = 0; tier < 4; tier++)
        {
            float y0 = 1.0f + tier * 1.45f, r = 2.1f - tier * 0.42f;
            s.Tube(new Vector3(0, y0, 0), new Vector3(0, y0 + 2.0f, 0), r, 0.05f, Green, 10);
            for (int k = 0; k < 6 + 2 * (3 - tier); k++)
            {
                float a = k * 2.399f + tier, h = 0.35f + (k % 4) * 0.28f, rr = r * (1f - h / 2.0f) * 0.9f;
                var c = k % 3 == 0 ? Lamp(1f, 0.82f, 0.4f) : k % 3 == 1 ? Lamp(0.95f, 0.16f, 0.16f) : Lamp(0.9f, 0.7f, 0.2f);
                s.Box(new Vector3(MathF.Cos(a) * rr, y0 + h, MathF.Sin(a) * rr), new Vector3(0.16f, 0.16f, 0.16f), c);
            }
        }
        s.Box(new Vector3(0, 6.95f, 0), new Vector3(0.3f, 0.3f, 0.3f), Lamp(1f, 0.9f, 0.4f), new Basis(Vector3.Up, 0.78f));
        s.Box(new Vector3(0, 6.95f, 0), new Vector3(0.3f, 0.3f, 0.3f), Lamp(1f, 0.9f, 0.4f), new Basis(Vector3.Forward, 0.78f));
        return s.Build();
    }

    // ---- the tiles -------------------------------------------------------------------------------------------

    /// <summary>Dresses the tile that owns each stall (counter towards the lane), the Samichlaus (facing the lane) and the tree.</summary>
    public static void Decorate(ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        foreach (var st in ChristmasRules.Layout(site))
            Place(StallProps[(int)st.Kind], st.E, st.N, st.Yaw, tile, into);
        var (se, sn) = ChristmasRules.SamichlausAt(site);
        Place(SamichlausProp, se, sn, -MathF.PI / 2, tile, into);   // his front towards the west, the lane
        var (te, tn) = ChristmasRules.TreeAt(site);
        Place(TreeProp, te, tn, 0f, tile, into);
    }

    private static void Place(PropKind kind, double e, double n, float yaw, TileContext tile, DecorBuilder into)
    {
        if (TileId.FromLv95(e, n) != tile.Id) return;   // each prop belongs to one tile
        var local = tile.Local(e, n);
        if (tile.HeightAt(local) is not { } h) return;
        local.Y = h;
        into.Add(kind, new Transform3D(new Basis(Vector3.Up, yaw), local));
    }

    // ---- the model viewer ------------------------------------------------------------------------------------

    [Showcase("Festivals", "Christmas stall: Glühwein")]
    private static ArrayMesh GluehweinStall() => Stall(StallKind.Gluehwein);

    [Showcase("Festivals", "Christmas stall: Raclette")]
    private static ArrayMesh RacletteStall() => Stall(StallKind.Raclette);

    [Showcase("Festivals", "Christmas stall: Bakery")]
    private static ArrayMesh BakeryStall() => Stall(StallKind.Gritibaenz);

    [Showcase("Festivals", "Christmas stall: Ornaments")]
    private static ArrayMesh OrnamentsStall() => Stall(StallKind.Ornaments);
}
