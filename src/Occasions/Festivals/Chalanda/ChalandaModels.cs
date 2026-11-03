using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals.Chalanda;

/// <summary>
/// The Chalandamarz's props (#1356), dressed by <see cref="FestivalOccasion.Decorate"/> like the other festivals': the Treichel stand (a wooden frame
/// with a row of hanging Treicheln of three sizes, the Désalpe's bell shapes, and a painted board with a winter-chasing straw doll) and a station banner
/// (a pole with a swallow-tailed red-and-white pennant and a blue band) at each of the loop's seven stations. Each is one mesh built once and added to the
/// tile's decoration as a transform (instanced by the tile, never rebuilt).
/// </summary>
public static class ChalandaModels
{
    private static readonly Color Wood = Matte(0.45f, 0.32f, 0.20f), WoodDark = Matte(0.32f, 0.22f, 0.14f), Leather = Matte(0.30f, 0.18f, 0.11f);
    private static readonly Color Brass = Matte(0.78f, 0.60f, 0.23f), BrassDark = Matte(0.55f, 0.40f, 0.14f), Straw = Matte(0.86f, 0.72f, 0.32f);
    private static readonly Color Red = Matte(0.80f, 0.12f, 0.14f), White = Matte(0.95f, 0.94f, 0.90f), Blue = Matte(0.16f, 0.30f, 0.62f), Snow = Matte(0.92f, 0.95f, 0.98f);

    private static ArrayMesh? _stand, _banner;

    public static readonly PropKind StandProp = new("ChalandaTreichelStand", () => Stand(), candle: false);
    public static readonly PropKind BannerProp = new("ChalandaStationBanner", () => Banner(), candle: false);

    // ---- the stand ---------------------------------------------------------------------------------------------

    /// <summary>The Treichel stand: a 3.4 m wooden frame with five hanging Treicheln (two big, two middle, one small), a blue board and a straw doll of the winter at its foot. Faces +Z.</summary>
    [Showcase("Festivals", "Chalanda Treichel stand")]
    public static ArrayMesh Stand() => _stand ??= BuildStand();

    private static ArrayMesh BuildStand()
    {
        var s = new MeshScratch();
        foreach (float x in new[] { -1.9f, 1.9f })
            s.Tube(new Vector3(x, -0.03f, 0), new Vector3(x, 3.1f, 0), 0.1f, 0.09f, Wood, 6);
        s.Box(new Vector3(0, 3.1f, 0), new Vector3(4.1f, 0.2f, 0.2f), WoodDark);
        s.Box(new Vector3(0, 1.0f, 0), new Vector3(3.8f, 0.14f, 0.12f), WoodDark);    // the low rail
        // the bells hang from the beam on leather straps: big, middle, small, middle, big
        float[] x2 = [-1.4f, -0.7f, 0f, 0.7f, 1.4f], r = [0.16f, 0.13f, 0.1f, 0.13f, 0.16f], h = [0.46f, 0.38f, 0.3f, 0.38f, 0.46f];
        for (int k = 0; k < 5; k++)
        {
            float top = 3.0f, x = x2[k];
            s.Box(new Vector3(x, top, 0.0f), new Vector3(0.06f, 0.36f, 0.04f), Leather);
            s.Tube(new Vector3(x, top - 0.18f, 0), new Vector3(x, top - 0.18f - h[k], 0), r[k] * 0.62f, r[k], Brass, 8);
            s.Tube(new Vector3(x, top - 0.18f - h[k] + 0.06f, 0), new Vector3(x, top - 0.18f - h[k] - 0.04f, 0), r[k] * 1.25f, r[k] * 1.25f, BrassDark, 8);   // the lip, proud below the body
            s.Tube(new Vector3(x, top - 0.19f - h[k], 0), new Vector3(x, top - 0.3f - h[k], 0), 0.015f, 0.025f, BrassDark, 5);   // the clapper
        }
        // the board
        s.Box(new Vector3(0, 1.75f, 0.02f), new Vector3(2.6f, 0.7f, 0.06f), Blue);
        s.Box(new Vector3(0, 1.75f, 0.06f), new Vector3(2.3f, 0.12f, 0.02f), White);
        s.Box(new Vector3(-1.0f, 1.75f, 0.07f), new Vector3(0.14f, 0.5f, 0.02f), Red);
        s.Box(new Vector3(1.0f, 1.75f, 0.07f), new Vector3(0.14f, 0.5f, 0.02f), Red);
        // the straw doll of the winter at the foot of the left post: a bundle with a head, a scarf and a snow cap
        s.Tube(new Vector3(-1.25f, 0.0f, 0.5f), new Vector3(-1.25f, 0.8f, 0.5f), 0.26f, 0.2f, Straw, 8);
        s.Box(new Vector3(-1.25f, 0.95f, 0.5f), new Vector3(0.28f, 0.28f, 0.28f), Straw);
        s.Box(new Vector3(-1.25f, 0.8f, 0.5f), new Vector3(0.34f, 0.07f, 0.34f), Red);
        s.Box(new Vector3(-1.25f, 1.12f, 0.5f), new Vector3(0.3f, 0.08f, 0.3f), Snow);
        s.Tube(new Vector3(-1.25f, 0.5f, 0.5f), new Vector3(-1.25f, 0.5f, 0.5f) + new Vector3(0.4f, 0.12f, 0), 0.04f, 0.03f, Straw, 4);
        return s.Build();
    }

    // ---- the stations ------------------------------------------------------------------------------------------

    /// <summary>A station's banner: a 4 m pole, a swallow-tailed red-and-white pennant on its top and a blue band (the Engadine's colours of the procession).</summary>
    [Showcase("Festivals", "Chalanda station banner")]
    public static ArrayMesh Banner() => _banner ??= BuildBanner();

    private static ArrayMesh BuildBanner()
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, -0.03f, 0), new Vector3(0, 4.0f, 0), 0.06f, 0.04f, Wood, 6);
        s.Box(new Vector3(0, 4.05f, 0), new Vector3(0.14f, 0.1f, 0.14f), Brass);
        // the pennant: three blocks stepping down and in, the red and the white, the tail a notch
        s.Box(new Vector3(0.45f, 3.7f, 0), new Vector3(0.9f, 0.34f, 0.025f), Red);
        s.Box(new Vector3(0.45f, 3.4f, 0), new Vector3(0.9f, 0.26f, 0.025f), White);
        s.Box(new Vector3(0.95f, 3.7f, 0), new Vector3(0.2f, 0.2f, 0.025f), Red);
        s.Box(new Vector3(0.95f, 3.4f, 0), new Vector3(0.2f, 0.14f, 0.025f), White);
        s.Box(new Vector3(0, 2.3f, 0.05f), new Vector3(0.14f, 0.5f, 0.04f), Blue);
        return s.Build();
    }

    // ---- the tiles -------------------------------------------------------------------------------------------

    /// <summary>Dresses the tile that owns each prop: the stand (facing the loop's middle) and the seven stations' banners.</summary>
    public static void Decorate(ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        var (se, sn) = ChalandaRules.StandAt(site);
        Place(StandProp, se, sn, MathF.PI / 2, tile, into);   // the stand's front looks east, to the loop's middle
        var path = ChalandaRules.Path(site);
        for (int k = 0; k < ChalandaRules.Stations; k++) Place(BannerProp, path[k].E, path[k].N, 0f, tile, into);
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
