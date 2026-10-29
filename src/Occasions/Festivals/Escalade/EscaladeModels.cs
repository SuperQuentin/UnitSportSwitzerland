using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals.Escalade;

/// <summary>
/// The Escalade's props (#1351), dressed by <see cref="FestivalOccasion.Decorate"/> like the other festivals': the Marmite (a cast-iron cauldron of
/// chocolate on three legs, a red-and-white sugar band and marzipan vegetables on top), the soup pot (a copper cauldron over a fire), the start arch
/// of the Course and a torch post at each of its six gates. Each is one mesh built once and added to the tile's decoration as a transform (instanced by
/// the tile, never rebuilt); the fire, the torches and the arch's lanterns are <see cref="PropColors.Lamp"/> parts, so they shine at night.
/// </summary>
public static class EscaladeModels
{
    private static readonly Color Iron = Matte(0.20f, 0.19f, 0.20f), IronLight = Matte(0.30f, 0.29f, 0.30f), Chocolate = Matte(0.30f, 0.17f, 0.10f);
    private static readonly Color Cocoa = Matte(0.40f, 0.24f, 0.14f), Red = Matte(0.80f, 0.12f, 0.14f), White = Matte(0.95f, 0.94f, 0.90f), Gold = Matte(0.92f, 0.76f, 0.20f);
    private static readonly Color Carrot = Matte(0.92f, 0.50f, 0.14f), Leaf = Matte(0.28f, 0.60f, 0.22f), Beet = Matte(0.62f, 0.14f, 0.34f), Pea = Matte(0.50f, 0.74f, 0.28f);
    private static readonly Color Copper = Matte(0.72f, 0.42f, 0.22f), Wood = Matte(0.45f, 0.32f, 0.20f), WoodDark = Matte(0.32f, 0.22f, 0.14f), Soup = Matte(0.84f, 0.56f, 0.24f);

    private static ArrayMesh? _marmite, _pot, _arch, _torch;

    public static readonly PropKind MarmiteProp = new("EscaladeMarmite", () => Marmite(), candle: false);
    public static readonly PropKind PotProp = new("EscaladeSoupPot", () => SoupPot(), candle: false);
    public static readonly PropKind ArchProp = new("EscaladeStartArch", () => StartArch(), candle: false);
    public static readonly PropKind TorchProp = new("EscaladeGateTorch", () => GateTorch(), candle: false);

    // ---- the Marmite -------------------------------------------------------------------------------------------

    /// <summary>The Marmite de l'Escalade: a 1.5 m cast-iron cauldron on three legs, a chocolate lid with a red-and-white sugar band, marzipan vegetables on top.</summary>
    [Showcase("Festivals", "Escalade Marmite")]
    public static ArrayMesh Marmite() => _marmite ??= BuildMarmite();

    private static ArrayMesh BuildMarmite()
    {
        var s = new MeshScratch();
        for (int k = 0; k < 3; k++)
        {
            float a = k * MathF.Tau / 3f;
            var foot = new Vector3(MathF.Cos(a) * 0.62f, -0.03f, MathF.Sin(a) * 0.62f);
            s.Tube(foot, foot + new Vector3(0, 0.45f, 0), 0.07f, 0.06f, Iron, 6);
        }
        s.Tube(new Vector3(0, 0.42f, 0), new Vector3(0, 0.95f, 0), 0.62f, 0.86f, Iron, 14);          // the belly
        s.Tube(new Vector3(0, 0.95f, 0), new Vector3(0, 1.22f, 0), 0.86f, 0.92f, Iron, 14);          // the shoulder
        s.Tube(new Vector3(0, 1.1f, 0), new Vector3(0, 1.17f, 0), 0.95f, 0.95f, IronLight, 14);      // the rim, proud of the shoulder
        s.Tube(new Vector3(0, 1.2f, 0), new Vector3(0, 1.45f, 0), 0.84f, 0.5f, Chocolate, 14);       // the chocolate lid
        s.Tube(new Vector3(0, 1.43f, 0), new Vector3(0, 1.56f, 0), 0.5f, 0.2f, Cocoa, 12);           // its crown
        // the sugar band: alternating red and white blocks round the lid
        for (int k = 0; k < 12; k++)
        {
            float a = k * MathF.Tau / 12f;
            s.Box(new Vector3(MathF.Cos(a) * 0.82f, 1.27f, MathF.Sin(a) * 0.82f), new Vector3(0.2f, 0.1f, 0.16f), k % 2 == 0 ? Red : White, new Basis(Vector3.Up, -a));
        }
        // the two ring handles
        s.Box(new Vector3(-1.0f, 1.0f, 0), new Vector3(0.14f, 0.22f, 0.1f), Iron);
        s.Box(new Vector3(1.0f, 1.0f, 0), new Vector3(0.14f, 0.22f, 0.1f), Iron);
        // marzipan vegetables on the crown: carrots, a beet, peas, a leaf
        s.Box(new Vector3(0.08f, 1.68f, 0.05f), new Vector3(0.1f, 0.3f, 0.1f), Carrot, new Basis(Vector3.Forward, 0.3f));
        s.Box(new Vector3(-0.1f, 1.66f, -0.05f), new Vector3(0.1f, 0.26f, 0.1f), Carrot, new Basis(Vector3.Forward, -0.35f));
        s.Box(new Vector3(0.0f, 1.66f, 0.12f), new Vector3(0.16f, 0.16f, 0.16f), Beet);
        s.Box(new Vector3(-0.12f, 1.62f, 0.12f), new Vector3(0.09f, 0.09f, 0.09f), Pea);
        s.Box(new Vector3(0.14f, 1.62f, -0.1f), new Vector3(0.09f, 0.09f, 0.09f), Pea);
        s.Box(new Vector3(0.0f, 1.8f, 0.0f), new Vector3(0.22f, 0.04f, 0.1f), Leaf, new Basis(Vector3.Up, 0.6f));
        // the little flag of Geneva on a stick: a red and yellow half-and-half (an eagle and a key are beyond a low-poly flag)
        s.Tube(new Vector3(0.0f, 1.56f, -0.3f), new Vector3(0.0f, 2.3f, -0.3f), 0.025f, 0.02f, WoodDark, 5);
        s.Box(new Vector3(0.0f, 2.1f, -0.31f), new Vector3(0.5f, 0.32f, 0.02f), Gold);
        s.Box(new Vector3(-0.125f, 2.1f, -0.325f), new Vector3(0.25f, 0.32f, 0.02f), Red);
        return s.Build();
    }

    // ---- the soup pot --------------------------------------------------------------------------------------------

    /// <summary>The soup pot: a copper cauldron hung from a wooden tripod over a fire (which shines at night), a ladle on its rim.</summary>
    [Showcase("Festivals", "Escalade soup pot")]
    public static ArrayMesh SoupPot() => _pot ??= BuildPot();

    private static ArrayMesh BuildPot()
    {
        var s = new MeshScratch();
        for (int k = 0; k < 3; k++)
        {
            float a = k * MathF.Tau / 3f + 0.4f;
            s.Tube(new Vector3(MathF.Cos(a) * 1.1f, -0.03f, MathF.Sin(a) * 1.1f), new Vector3(MathF.Cos(a) * 0.08f, 2.1f, MathF.Sin(a) * 0.08f), 0.06f, 0.05f, WoodDark, 5);
        }
        s.Tube(new Vector3(0, 1.55f, 0), new Vector3(0, 2.15f, 0), 0.025f, 0.025f, Iron, 4);        // the chain
        // the fire: stacked logs and flames
        for (int k = 0; k < 5; k++)
        {
            float a = k * MathF.Tau / 5f;
            s.Tube(new Vector3(MathF.Cos(a) * 0.5f, 0.12f, MathF.Sin(a) * 0.5f), new Vector3(MathF.Cos(a) * 0.08f, 0.28f, MathF.Sin(a) * 0.08f), 0.06f, 0.05f, Wood, 5);
        }
        s.Box(new Vector3(0, 0.38f, 0), new Vector3(0.34f, 0.3f, 0.34f), Lamp(1f, 0.55f, 0.18f), new Basis(Vector3.Up, 0.5f));
        s.Box(new Vector3(0.1f, 0.5f, 0.06f), new Vector3(0.18f, 0.28f, 0.18f), Lamp(1f, 0.78f, 0.28f), new Basis(Vector3.Up, 1.1f));
        // the cauldron
        s.Tube(new Vector3(0, 0.78f, 0), new Vector3(0, 1.3f, 0), 0.5f, 0.68f, Copper, 12);
        s.Tube(new Vector3(0, 1.28f, 0), new Vector3(0, 1.34f, 0), 0.72f, 0.72f, IronLight, 12);   // the rim
        s.Tube(new Vector3(0, 1.3f, 0), new Vector3(0, 1.345f, 0), 0.62f, 0.62f, Soup, 12);       // the soup, a skin proud of the rim
        s.Box(new Vector3(0.18f, 1.36f, 0.1f), new Vector3(0.1f, 0.06f, 0.1f), Carrot);
        s.Box(new Vector3(-0.2f, 1.36f, -0.08f), new Vector3(0.1f, 0.06f, 0.1f), Leaf);
        // the ladle laid across the rim
        s.Tube(new Vector3(-0.2f, 1.42f, 0.7f), new Vector3(0.5f, 1.55f, 0.1f), 0.02f, 0.02f, Iron, 4);
        s.Tube(new Vector3(0.5f, 1.52f, 0.1f), new Vector3(0.55f, 1.46f, 0.05f), 0.07f, 0.05f, Iron, 6);
        return s.Build();
    }

    // ---- the Course ---------------------------------------------------------------------------------------------

    /// <summary>The start arch: two wooden posts, a beam with a red-and-white chequered banner and a lantern at each post. Authored with the run through the +Z axis.</summary>
    [Showcase("Festivals", "Escalade start arch")]
    public static ArrayMesh StartArch() => _arch ??= BuildArch();

    private static ArrayMesh BuildArch()
    {
        var s = new MeshScratch();
        foreach (float x in new[] { -2.4f, 2.4f })
        {
            s.Tube(new Vector3(x, -0.03f, 0), new Vector3(x, 3.6f, 0), 0.12f, 0.1f, Wood, 6);
            s.Box(new Vector3(x, 3.0f, 0.2f), new Vector3(0.18f, 0.24f, 0.18f), Lamp(1f, 0.78f, 0.42f));
        }
        s.Box(new Vector3(0, 3.7f, 0), new Vector3(5.2f, 0.28f, 0.2f), WoodDark);
        for (int k = 0; k < 13; k++)
        {
            float x = -2.4f + k * 0.4f;
            s.Box(new Vector3(x, 3.7f, 0.12f), new Vector3(0.2f, 0.2f, 0.03f), k % 2 == 0 ? Red : White);
            s.Box(new Vector3(x + 0.2f, 3.5f, 0.12f), new Vector3(0.2f, 0.2f, 0.03f), k % 2 == 0 ? White : Red);
        }
        s.Box(new Vector3(0, 0.01f, 0), new Vector3(4.8f, 0.03f, 0.22f), White);   // the line on the ground, a hair above it
        return s.Build();
    }

    /// <summary>A gate's torch post: a 2.6 m pole with an iron basket and a flame that shines at night.</summary>
    [Showcase("Festivals", "Escalade gate torch")]
    public static ArrayMesh GateTorch() => _torch ??= BuildTorch();

    private static ArrayMesh BuildTorch()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, -0.03f, 0), new Vector3(0, 2.4f, 0), 0.07f, 0.05f, Wood, 6);
        s.Tube(new Vector3(0, 2.3f, 0), new Vector3(0, 2.6f, 0), 0.1f, 0.2f, Iron, 8);
        s.Box(new Vector3(0, 2.7f, 0), new Vector3(0.2f, 0.34f, 0.2f), Lamp(1f, 0.55f, 0.18f), new Basis(Vector3.Up, 0.5f));
        s.Box(new Vector3(0, 2.82f, 0), new Vector3(0.12f, 0.26f, 0.12f), Lamp(1f, 0.82f, 0.32f), new Basis(Vector3.Up, 1.1f));
        s.Box(new Vector3(0, 0.45f, 0.07f), new Vector3(0.14f, 0.3f, 0.02f), Red);   // a pennant
        return s.Build();
    }

    // ---- the tiles -------------------------------------------------------------------------------------------

    /// <summary>Dresses the tile that owns each prop: the Marmite, the pot, the arch (its opening towards the first gate's side) and the six gates' torches.</summary>
    public static void Decorate(ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        var (me, mn) = EscaladeRules.MarmiteAt(site);
        Place(MarmiteProp, me, mn, 0f, tile, into);
        var (pe, pn) = EscaladeRules.PotAt(site);
        Place(PotProp, pe, pn, 0f, tile, into);
        var (se, sn) = EscaladeRules.StartAt(site);
        Place(ArchProp, se, sn, MathF.PI / 2, tile, into);   // the run goes along the east-west axis through it
        var path = EscaladeRules.Path(site);
        for (int k = 0; k < EscaladeRules.Gates; k++) Place(TorchProp, path[k].E, path[k].N, 0f, tile, into);
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
