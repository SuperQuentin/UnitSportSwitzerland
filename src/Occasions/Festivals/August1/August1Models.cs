using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals.August1;

/// <summary>
/// The 1 August's props (#1355), dressed by <see cref="FestivalOccasion.Decorate"/> like the other festivals': the lampion stand (a wooden stall with a rack of
/// paper lanterns), the fireworks stand (crates of boxes and rockets under a dark roof), the lampion post (the walk's start and finish: a tall pole with a string of
/// lanterns and a Swiss flag), the village fire (a pyramid of logs with a flame) and a stop lantern at each of the loop's ten stops. Each is one mesh built once and
/// added to the tile's decoration as a transform (instanced by the tile, never rebuilt); the lanterns and the flame are <see cref="PropColors.Lamp"/> parts, so they
/// shine at night. The Höhenfeuer on the summits are not props: <see cref="Hoehenfeuer"/> draws them as one MultiMesh of billboards.
/// </summary>
public static class August1Models
{
    private static readonly Color Wood = Matte(0.45f, 0.32f, 0.20f), WoodDark = Matte(0.32f, 0.22f, 0.14f), Log = Matte(0.40f, 0.27f, 0.16f);
    private static readonly Color Red = Matte(0.82f, 0.10f, 0.12f), White = Matte(0.95f, 0.94f, 0.90f), Black = Matte(0.14f, 0.13f, 0.14f), Crate = Matte(0.58f, 0.44f, 0.26f);
    private static readonly Color Blue = Matte(0.20f, 0.34f, 0.70f), Gold = Matte(0.92f, 0.76f, 0.20f);

    private static ArrayMesh? _lampionStand, _fireworkStand, _post, _fire, _stop;

    public static readonly PropKind LampionStandProp = new("August1LampionStand", () => LampionStand(), candle: false);
    public static readonly PropKind FireworkStandProp = new("August1FireworkStand", () => FireworkStand(), candle: false);
    public static readonly PropKind PostProp = new("August1LampionPost", () => LampionPost(), candle: false);
    public static readonly PropKind FireProp = new("August1VillageFire", () => VillageFire(), candle: false);
    public static readonly PropKind StopProp = new("August1StopLantern", () => StopLantern(), candle: false);

    /// <summary>A Swiss flag of <paramref name="size"/> m facing +Z at <paramref name="at"/>: a red square with a white cross.</summary>
    private static void Flag(MeshScratch s, Vector3 at, float size)
    {
        s.Box(at, new Vector3(size, size, 0.02f), Red);
        s.Box(at + new Vector3(0, 0, 0.012f), new Vector3(size * 0.62f, size * 0.2f, 0.012f), White);
        s.Box(at + new Vector3(0, 0, 0.012f), new Vector3(size * 0.2f, size * 0.62f, 0.012f), White);
    }

    /// <summary>A paper lantern hanging from <paramref name="top"/>: a thread, a ribbed paper body and a glowing candle.</summary>
    private static void Lantern(MeshScratch s, Vector3 top, float size, float hue)
    {
        s.Tube(top, top + new Vector3(0, -0.12f, 0), 0.006f, 0.006f, Black, 3);
        var body = hue < 0.5f ? Matte(0.85f, 0.22f, 0.14f) : Matte(0.95f, 0.66f, 0.18f);
        s.Tube(top + new Vector3(0, -0.12f - size, 0), top + new Vector3(0, -0.12f, 0), size * 0.5f, size * 0.62f, body, 8);
        s.Box(top + new Vector3(0, -0.12f - size * 0.5f, 0), new Vector3(size * 0.5f, size * 0.7f, size * 0.5f), hue < 0.5f ? Lamp(1f, 0.55f, 0.28f) : Lamp(1f, 0.78f, 0.34f));
    }

    // ---- the lampion stand ---------------------------------------------------------------------------------------------

    /// <summary>The lampion stand: a 2.6 m wooden stall under a red-and-white striped roof, with a string of paper lanterns along its front and a Swiss flag on a pole.</summary>
    [Showcase("Festivals", "1 August lampion stand")]
    public static ArrayMesh LampionStand() => _lampionStand ??= BuildLampionStand();

    private static ArrayMesh BuildLampionStand()
    {
        var s = new MeshScratch();
        foreach (float x in new[] { -1.2f, 1.2f })
            foreach (float z in new[] { -0.7f, 0.7f })
                s.Tube(new Vector3(x, -0.03f, z), new Vector3(x, 2.2f, z), 0.06f, 0.05f, Wood, 6);
        s.Box(new Vector3(0, 0.55f, 0.55f), new Vector3(2.5f, 0.12f, 0.8f), Wood);         // the counter
        s.Box(new Vector3(0, 0.28f, 0.55f), new Vector3(2.4f, 0.55f, 0.7f), WoodDark);     // its front
        s.Box(new Vector3(0, 1.15f, -0.72f), new Vector3(2.4f, 2.1f, 0.05f), WoodDark);    // the back wall
        for (int k = 0; k < 8; k++)                                                          // the striped roof
            s.Box(new Vector3(-1.4f + k * 0.4f, 2.32f, 0), new Vector3(0.4f, 0.06f, 1.9f), k % 2 == 0 ? Red : White, new Basis(Vector3.Forward, 0.08f));
        for (int k = 0; k < 6; k++)                                                          // the string of lanterns along the front
            Lantern(s, new Vector3(-1.0f + k * 0.4f, 2.15f, 0.8f), 0.2f, k % 2);
        for (int k = 0; k < 5; k++)                                                          // lanterns for sale on the counter
            s.Tube(new Vector3(-0.9f + k * 0.45f, 0.6f, 0.5f), new Vector3(-0.9f + k * 0.45f, 0.86f, 0.5f), 0.07f, 0.09f, k % 2 == 0 ? Matte(0.85f, 0.22f, 0.14f) : Matte(0.95f, 0.66f, 0.18f), 8);
        s.Tube(new Vector3(1.5f, -0.03f, 0.2f), new Vector3(1.5f, 2.7f, 0.2f), 0.03f, 0.025f, WoodDark, 5);
        Flag(s, new Vector3(1.8f, 2.4f, 0.2f), 0.55f);
        return s.Build();
    }

    // ---- the fireworks stand -----------------------------------------------------------------------------------------

    /// <summary>The fireworks stand: a 2.6 m stall with a dark roof, crates of boxes and rockets on the counter and a red sign.</summary>
    [Showcase("Festivals", "1 August fireworks stand")]
    public static ArrayMesh FireworkStand() => _fireworkStand ??= BuildFireworkStand();

    private static ArrayMesh BuildFireworkStand()
    {
        var s = new MeshScratch();
        foreach (float x in new[] { -1.2f, 1.2f })
            foreach (float z in new[] { -0.7f, 0.7f })
                s.Tube(new Vector3(x, -0.03f, z), new Vector3(x, 2.2f, z), 0.06f, 0.05f, WoodDark, 6);
        s.Box(new Vector3(0, 0.55f, 0.55f), new Vector3(2.5f, 0.12f, 0.8f), Wood);
        s.Box(new Vector3(0, 0.28f, 0.55f), new Vector3(2.4f, 0.55f, 0.7f), Blue);
        s.Box(new Vector3(0, 1.15f, -0.72f), new Vector3(2.4f, 2.1f, 0.05f), WoodDark);
        s.Box(new Vector3(0, 2.3f, 0), new Vector3(2.8f, 0.08f, 1.9f), Black, new Basis(Vector3.Forward, 0.06f));
        s.Box(new Vector3(0, 2.0f, 0.78f), new Vector3(1.6f, 0.34f, 0.04f), Red);          // the sign
        s.Box(new Vector3(0, 2.0f, 0.8f), new Vector3(1.0f, 0.1f, 0.02f), Gold);
        for (int k = 0; k < 4; k++)                                                          // crates and boxes
        {
            s.Box(new Vector3(-0.9f + k * 0.55f, 0.74f, 0.45f), new Vector3(0.4f, 0.24f, 0.3f), k % 2 == 0 ? Crate : Red);
            s.Box(new Vector3(-0.9f + k * 0.55f, 0.74f, 0.45f + 0.16f), new Vector3(0.2f, 0.12f, 0.01f), Gold);
        }
        for (int k = 0; k < 5; k++)                                                          // rockets standing in a bucket
            s.Tube(new Vector3(0.65f + (k % 3) * 0.06f, 0.62f, 0.3f + (k / 3) * 0.06f), new Vector3(0.65f + (k % 3) * 0.06f, 1.2f + k * 0.04f, 0.3f + (k / 3) * 0.06f), 0.015f, 0.012f, k % 2 == 0 ? Red : White, 5);
        s.Box(new Vector3(1.05f, 1.55f, 0.2f), new Vector3(0.12f, 0.12f, 0.12f), Lamp(1f, 0.7f, 0.3f));   // a work lamp
        return s.Build();
    }

    // ---- the lampion post ---------------------------------------------------------------------------------------------------

    /// <summary>The lampion post, the walk's start and finish: a 3.6 m pole with a crossbar, a string of seven lanterns, a Swiss flag and a white line on the ground.</summary>
    [Showcase("Festivals", "1 August lampion post")]
    public static ArrayMesh LampionPost() => _post ??= BuildPost();

    private static ArrayMesh BuildPost()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, -0.03f, 0), new Vector3(0, 3.6f, 0), 0.09f, 0.07f, Wood, 6);
        s.Box(new Vector3(0, 3.3f, 0), new Vector3(2.6f, 0.1f, 0.1f), WoodDark);
        for (int k = 0; k < 7; k++) Lantern(s, new Vector3(-1.2f + k * 0.4f, 3.25f, 0), 0.24f, k % 2);
        Flag(s, new Vector3(0.35f, 3.2f + 0.45f, 0.07f), 0.5f);
        s.Box(new Vector3(0, 0.01f, 0), new Vector3(3.0f, 0.03f, 0.22f), White);
        s.Box(new Vector3(0, 0.9f, 0.1f), new Vector3(0.5f, 0.3f, 0.03f), Red);    // a plaque
        s.Box(new Vector3(0, 0.9f, 0.115f), new Vector3(0.3f, 0.06f, 0.01f), White);
        return s.Build();
    }

    // ---- the village fire --------------------------------------------------------------------------------------------------

    /// <summary>The village fire: a 2.2 m pyramid of logs with a flame that shines at night, the village's own Höhenfeuer.</summary>
    [Showcase("Festivals", "1 August village fire")]
    public static ArrayMesh VillageFire() => _fire ??= BuildFire();

    private static ArrayMesh BuildFire()
    {
        var s = new MeshScratch();
        for (int layer = 0; layer < 5; layer++)
        {
            float r = 1.0f - layer * 0.16f, y = 0.12f + layer * 0.3f;
            for (int k = 0; k < 4; k++)
            {
                float a = layer * 0.7f + k * MathF.PI / 2f;
                var along = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                var side = new Vector3(-along.Z, 0, along.X) * (k % 2 == 0 ? 0.4f : -0.4f);
                s.Tube(side - along * r + new Vector3(0, y, 0), side + along * r + new Vector3(0, y, 0), 0.1f, 0.1f, layer % 2 == 0 ? Log : WoodDark, 6);
            }
        }
        s.Box(new Vector3(0, 1.75f, 0), new Vector3(0.7f, 0.9f, 0.7f), Lamp(1f, 0.52f, 0.14f), new Basis(Vector3.Up, 0.5f));
        s.Box(new Vector3(0.05f, 2.15f, 0.03f), new Vector3(0.4f, 0.7f, 0.4f), Lamp(1f, 0.78f, 0.28f), new Basis(Vector3.Up, 1.1f));
        return s.Build();
    }

    // ---- the stops ----------------------------------------------------------------------------------------------------------

    /// <summary>A stop of the loop: a 1.8 m stake with a lantern hanging from a bent arm, so the way round is lit.</summary>
    [Showcase("Festivals", "1 August stop lantern")]
    public static ArrayMesh StopLantern() => _stop ??= BuildStop();

    private static ArrayMesh BuildStop()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, -0.03f, 0), new Vector3(0, 1.8f, 0), 0.04f, 0.035f, Wood, 5);
        s.Box(new Vector3(0.18f, 1.8f, 0), new Vector3(0.4f, 0.04f, 0.04f), WoodDark);
        Lantern(s, new Vector3(0.34f, 1.78f, 0), 0.2f, 0);
        s.Box(new Vector3(0, 0.3f, 0.04f), new Vector3(0.1f, 0.14f, 0.01f), Red);
        return s.Build();
    }

    // ---- the tiles -------------------------------------------------------------------------------------------------------------

    /// <summary>Dresses the tile that owns each prop: the two stands (facing south, to the post), the village fire, the post and the ten stops.</summary>
    public static void Decorate(ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        var (le, ln) = August1Rules.LampionStandAt(site);
        Place(LampionStandProp, le, ln, 0f, tile, into);
        var (fe, fn) = August1Rules.FireworkStandAt(site);
        Place(FireworkStandProp, fe, fn, 0f, tile, into);
        var (ve, vn) = August1Rules.VillageFireAt(site);
        Place(FireProp, ve, vn, 0f, tile, into);
        var (pe, pn) = August1Rules.PostAt(site);
        Place(PostProp, pe, pn, 0f, tile, into);
        var path = August1Rules.Path(site);
        for (int k = 0; k < August1Rules.Waypoints; k++) Place(StopProp, path[k].E, path[k].N, 0f, tile, into);
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
