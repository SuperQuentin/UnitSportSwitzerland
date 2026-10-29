using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals.Sechselaeuten;

/// <summary>
/// The Sechseläuten's models (#1358): the Böögg's pyre, the Böögg himself in two parts (body and head, so the head can burst alone), what is left of the
/// pyre, the Zunft's Weibel who hands out the purse, and the guild banners round the square. Each is one mesh built once. The pyre, the Böögg and the
/// remains are placed by <see cref="BooggStage"/> (they change with the round), the Weibel and the banners by <see cref="Decorate"/> like every
/// festival's props, one mesh instanced by the tile.
/// </summary>
public static class SechselaeutenModels
{
    private static readonly Color Log = Matte(0.42f, 0.29f, 0.17f), LogDark = Matte(0.30f, 0.20f, 0.12f), LogPale = Matte(0.58f, 0.43f, 0.27f), Straw = Matte(0.82f, 0.70f, 0.32f);
    private static readonly Color Wool = Matte(0.95f, 0.95f, 0.93f), WoolShade = Matte(0.82f, 0.83f, 0.85f), Coal = Matte(0.08f, 0.08f, 0.09f), Carrot = Matte(0.95f, 0.48f, 0.12f);
    private static readonly Color Red = Matte(0.82f, 0.12f, 0.14f), Blue = Matte(0.16f, 0.32f, 0.72f), White = Matte(0.96f, 0.96f, 0.94f), Gold = Matte(0.92f, 0.76f, 0.20f);
    private static readonly Color Cheek = Matte(0.95f, 0.45f, 0.42f), Fuse = Matte(0.88f, 0.15f, 0.12f), BroomStraw = Matte(0.72f, 0.58f, 0.26f), Ash = Matte(0.16f, 0.15f, 0.15f);
    private static readonly Color Skin = Matte(0.88f, 0.69f, 0.55f), Cloth = Matte(0.20f, 0.22f, 0.40f), Pole = Matte(0.40f, 0.30f, 0.20f), Ember = Lamp(1f, 0.38f, 0.08f);

    private static ArrayMesh? _pyre, _body, _head, _remains, _weibel, _banner;

    public static readonly PropKind WeibelProp = new("SechseWeibel", () => Weibel(), candle: false);
    public static readonly PropKind BannerProp = new("SechseGuildBanner", () => GuildBanner(), candle: false);

    // ---- the pyre --------------------------------------------------------------------------------------

    /// <summary>The pyre: a crib of split logs 7.2 m high on a 5 m square (the Böögg's 2.8 m on top make the ten metres), bundles of straw round the foot and a pole through the middle.</summary>
    [Showcase("Festivals", "Böögg pyre")]
    public static ArrayMesh Pyre() => _pyre ??= BuildPyre();

    private static ArrayMesh BuildPyre()
    {
        var s = new MeshScratch();
        const int Layers = 24;
        const float Layer = 0.3f, Half = 2.5f;
        for (int l = 0; l < Layers; l++)
        {
            float y = 0.15f + l * Layer;
            float taper = 1f - 0.18f * l / Layers;   // the crib narrows as it climbs
            float reach = Half * taper;
            bool eastWest = l % 2 == 0;
            for (int k = 0; k < 7; k++)
            {
                float across = -reach + (k + 0.5f) * (2 * reach / 7f);
                var colour = (l + k) % 3 == 0 ? LogDark : (l + k) % 3 == 1 ? Log : LogPale;
                var a = eastWest ? new Vector3(-reach, y, across) : new Vector3(across, y, -reach);
                var b = eastWest ? new Vector3(reach, y, across) : new Vector3(across, y, reach);
                s.Tube(a, b, 0.15f, 0.14f, colour, 6);
            }
        }
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 7.5f, 0), 0.2f, 0.16f, LogDark, 6);   // the pole the Böögg is tied to
        for (int k = 0; k < 10; k++)
        {
            float a = k * MathF.Tau / 10f;
            s.Box(new Vector3(MathF.Cos(a) * 2.9f, 0.3f, MathF.Sin(a) * 2.9f), new Vector3(0.7f, 0.6f, 0.7f), Straw, new Basis(Vector3.Up, -a));
        }
        s.Box(new Vector3(0, 0.02f, 0), new Vector3(6.4f, 0.04f, 6.4f), Ash);
        return s.Build();
    }

    // ---- the Böögg -------------------------------------------------------------------------------------

    /// <summary>The Böögg's body, from the pole's top: a cotton-wool snowman's belly and chest in a blue and white Zunft sash, two stick arms and a broom. 1.9 m, the head sits on its top.</summary>
    [Showcase("Festivals", "Böögg body")]
    public static ArrayMesh BooggBody() => _body ??= BuildBody();

    private static ArrayMesh BuildBody()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 0.9f, 0), 0.95f, 0.8f, Wool, 14);        // the belly
        s.Tube(new Vector3(0, 0.9f, 0), new Vector3(0, 1.5f, 0), 0.8f, 0.55f, WoolShade, 14);   // the chest
        s.Tube(new Vector3(0, 1.5f, 0), new Vector3(0, 1.62f, 0), 0.55f, 0.4f, Wool, 12);       // the neck
        // the sash across the chest, in the city's blue and white
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.Tau / 8f;
            s.Box(new Vector3(MathF.Cos(a) * 0.9f, 0.62f, MathF.Sin(a) * 0.9f), new Vector3(0.16f, 0.5f, 0.3f), k % 2 == 0 ? Blue : White, new Basis(Vector3.Up, -a));
        }
        // buttons of coal
        for (int k = 0; k < 3; k++) s.Box(new Vector3(0, 0.35f + k * 0.35f, 0.9f - k * 0.1f), new Vector3(0.14f, 0.14f, 0.1f), Coal);
        // stick arms, the right one holding a broom
        s.Tube(new Vector3(0.7f, 1.3f, 0), new Vector3(1.6f, 1.9f, 0.1f), 0.07f, 0.04f, LogDark, 5);
        s.Tube(new Vector3(-0.7f, 1.3f, 0), new Vector3(-1.5f, 1.7f, -0.1f), 0.07f, 0.04f, LogDark, 5);
        s.Tube(new Vector3(1.3f, 0.2f, 0.15f), new Vector3(1.7f, 2.3f, 0.1f), 0.04f, 0.04f, Pole, 5);      // the broom's handle
        s.Box(new Vector3(1.3f, 0.35f, 0.15f), new Vector3(0.4f, 0.55f, 0.3f), BroomStraw, new Basis(Vector3.Forward, -0.1f));
        return s.Build();
    }

    /// <summary>The Böögg's head: a 0.9 m ball of cotton wool with coal eyes, a carrot nose, red cheeks, a pointed hat and a string of red firecracker fuses round the crown. It bursts alone.</summary>
    [Showcase("Festivals", "Böögg head")]
    public static ArrayMesh BooggHead() => _head ??= BuildHead();

    private static ArrayMesh BuildHead()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, -0.45f, 0), new Vector3(0, 0.05f, 0), 0.32f, 0.5f, Wool, 14);
        s.Tube(new Vector3(0, 0.05f, 0), new Vector3(0, 0.5f, 0), 0.5f, 0.3f, Wool, 14);        // a ball from two cones
        s.Box(new Vector3(-0.18f, 0.12f, 0.43f), new Vector3(0.1f, 0.1f, 0.06f), Coal);       // eyes
        s.Box(new Vector3(0.18f, 0.12f, 0.43f), new Vector3(0.1f, 0.1f, 0.06f), Coal);
        s.Tube(new Vector3(0, 0.0f, 0.4f), new Vector3(0, -0.04f, 0.78f), 0.07f, 0.01f, Carrot, 6);   // the carrot
        s.Box(new Vector3(-0.3f, -0.08f, 0.38f), new Vector3(0.14f, 0.09f, 0.05f), Cheek);
        s.Box(new Vector3(0.3f, -0.08f, 0.38f), new Vector3(0.14f, 0.09f, 0.05f), Cheek);
        s.Box(new Vector3(0, -0.2f, 0.4f), new Vector3(0.3f, 0.05f, 0.05f), Coal);               // the mouth
        s.Tube(new Vector3(0, 0.38f, 0), new Vector3(0, 1.05f, 0), 0.34f, 0.02f, Blue, 10);     // the pointed hat
        s.Tube(new Vector3(0, 0.36f, 0), new Vector3(0, 0.44f, 0), 0.5f, 0.5f, White, 12);      // its brim
        // the firecracker fuses: red sticks round the head, a few poking out of the hat
        for (int k = 0; k < 9; k++)
        {
            float a = k * MathF.Tau / 9f;
            s.Tube(new Vector3(MathF.Cos(a) * 0.46f, 0.18f, MathF.Sin(a) * 0.46f), new Vector3(MathF.Cos(a) * 0.6f, 0.3f, MathF.Sin(a) * 0.6f), 0.025f, Fuse, 4);
        }
        return s.Build();
    }

    /// <summary>The whole Böögg: body and head as the pole carries him, for the model viewer (the game keeps them apart so the head can burst alone).</summary>
    [Showcase("Festivals", "Böögg (whole)")]
    public static ArrayMesh Boogg()
    {
        var mesh = new ArrayMesh();
        // composed from the two meshes' arrays: the head stands 2.1 m above the body's foot
        var parts = new (ArrayMesh Mesh, Vector3 At)[] { (BooggBody(), Vector3.Zero), (BooggHead(), new Vector3(0, BooggHeadHeight, 0)) };
        foreach (var (m, at) in parts)
            for (int surface = 0; surface < m.GetSurfaceCount(); surface++)
            {
                var arrays = m.SurfaceGetArrays(surface);
                var verts = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                for (int i = 0; i < verts.Length; i++) verts[i] += at;
                arrays[(int)Mesh.ArrayType.Vertex] = verts;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
        return mesh;
    }

    /// <summary>Height of the head's middle above the body's foot, m.</summary>
    public const float BooggHeadHeight = 2.1f;

    // ---- what is left ----------------------------------------------------------------------------------

    /// <summary>What is left of the pyre once the head has burst: charred crossed logs collapsed on the ash, the pole a black stump, embers that shine at night.</summary>
    [Showcase("Festivals", "Böögg embers")]
    public static ArrayMesh Remains() => _remains ??= BuildRemains();

    private static ArrayMesh BuildRemains()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.03f, 0), new Vector3(6.2f, 0.06f, 6.2f), Ash);
        for (int k = 0; k < 16; k++)
        {
            float a = k * MathF.Tau / 16f + 0.3f;
            float r = 0.6f + (k % 4) * 0.55f;
            var c = new Vector3(MathF.Cos(a) * r, 0.14f + (k % 3) * 0.1f, MathF.Sin(a) * r);
            s.Tube(c - new Vector3(MathF.Cos(a + 1.1f) * 0.9f, 0, MathF.Sin(a + 1.1f) * 0.9f), c + new Vector3(MathF.Cos(a + 1.1f) * 0.9f, 0.05f, MathF.Sin(a + 1.1f) * 0.9f), 0.13f, 0.12f, Coal, 6);
        }
        s.Tube(new Vector3(0, 0, 0), new Vector3(0.1f, 1.4f, 0.05f), 0.2f, 0.12f, Coal, 6);   // the stump of the pole
        for (int k = 0; k < 9; k++)
        {
            float a = k * MathF.Tau / 9f + 0.5f;
            float r = 0.4f + (k % 3) * 0.9f;
            s.Box(new Vector3(MathF.Cos(a) * r, 0.2f, MathF.Sin(a) * r), new Vector3(0.5f, 0.12f, 0.4f), Ember, new Basis(Vector3.Up, a));
        }
        return s.Build();
    }

    // ---- the square ------------------------------------------------------------------------------------

    /// <summary>The Zunft's Weibel: a guild servant in a blue and white cloak and a black felt hat, with a staff and the purse on his belt. 1.8 m.</summary>
    [Showcase("Festivals", "Zunft Weibel")]
    public static ArrayMesh Weibel() => _weibel ??= BuildWeibel();

    private static ArrayMesh BuildWeibel()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(-0.1f, 0, 0), new Vector3(-0.1f, 0.85f, 0), 0.08f, 0.07f, Coal, 6);      // legs
        s.Tube(new Vector3(0.1f, 0, 0), new Vector3(0.1f, 0.85f, 0), 0.08f, 0.07f, Coal, 6);
        s.Tube(new Vector3(0, 0.75f, 0), new Vector3(0, 1.45f, 0), 0.3f, 0.2f, Cloth, 10);          // the cloak
        for (int k = 0; k < 6; k++)
        {
            float a = k * MathF.Tau / 6f;
            s.Box(new Vector3(MathF.Cos(a) * 0.27f, 1.0f, MathF.Sin(a) * 0.27f), new Vector3(0.08f, 0.5f, 0.16f), k % 2 == 0 ? Blue : White, new Basis(Vector3.Up, -a));
        }
        s.Box(new Vector3(0.22f, 0.9f, 0.18f), new Vector3(0.14f, 0.16f, 0.1f), Gold);              // the purse
        s.Tube(new Vector3(0, 1.45f, 0), new Vector3(0, 1.7f, 0), 0.12f, 0.1f, Skin, 8);              // the head
        s.Tube(new Vector3(0, 1.66f, 0), new Vector3(0, 1.74f, 0), 0.22f, 0.22f, Coal, 10);          // the hat's brim
        s.Tube(new Vector3(0, 1.72f, 0), new Vector3(0, 1.86f, 0), 0.13f, 0.12f, Coal, 10);
        s.Tube(new Vector3(0.4f, 0, 0.1f), new Vector3(0.4f, 2.0f, 0.1f), 0.03f, 0.03f, Pole, 5);   // the staff
        s.Tube(new Vector3(0.28f, 1.15f, 0.06f), new Vector3(0.4f, 1.3f, 0.1f), 0.05f, 0.04f, Cloth, 5);   // the arm
        s.Box(new Vector3(0.4f, 2.04f, 0.1f), new Vector3(0.1f, 0.1f, 0.1f), Gold);
        return s.Build();
    }

    /// <summary>A guild banner on a 4.5 m pole: a blue and white field split down the middle, a red band and a gold finial. Authored facing +Z.</summary>
    [Showcase("Festivals", "Zunft banner")]
    public static ArrayMesh GuildBanner() => _banner ??= BuildBanner();

    private static ArrayMesh BuildBanner()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, -0.03f, 0), new Vector3(0, 4.5f, 0), 0.06f, 0.045f, Pole, 6);
        s.Box(new Vector3(0, 4.62f, 0), new Vector3(0.14f, 0.14f, 0.14f), Gold);
        s.Box(new Vector3(0.45f, 3.7f, 0), new Vector3(0.82f, 1.4f, 0.03f), Blue);
        s.Box(new Vector3(0.45f, 3.7f, 0.02f), new Vector3(0.4f, 1.4f, 0.03f), White);
        s.Box(new Vector3(0.45f, 3.05f, 0.04f), new Vector3(0.82f, 0.14f, 0.03f), Red);
        s.Box(new Vector3(0.45f, 3.7f, 0.05f), new Vector3(0.14f, 0.3f, 0.02f), Gold);   // the guild's mark
        return s.Build();
    }

    // ---- the tiles -------------------------------------------------------------------------------------

    /// <summary>Dresses the tile that owns each prop: the Zunft's Weibel (turned to the pyre) and the ring of guild banners.</summary>
    public static void Decorate(ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        var (we, wn) = SechselaeutenRules.WeibelAt(site);
        var (pe, pn) = SechselaeutenRules.PyreAt(site);
        Place(WeibelProp, we, wn, (float)Math.Atan2(pe - we, pn - wn), tile, into);
        for (int k = 0; k < SechselaeutenRules.Banners; k++)
        {
            var (be, bn) = SechselaeutenRules.BannerAt(site, k);
            Place(BannerProp, be, bn, k * 0.9f, tile, into);
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
