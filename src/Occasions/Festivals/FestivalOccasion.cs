using Godot;
using UnitSport.Avatar;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// A festival as an occasion (#1269 section 1.1): one instance per <see cref="FestivalDef"/>, registered through
/// <see cref="OccasionRegistry"/> by the def's id. When and with which facets it runs is its <see cref="OccasionEntry"/>; where it is
/// held is <see cref="FestivalService"/>'s resolved site, which the server sends and which is set here with
/// <see cref="SetSite"/>. The content hooks are the shared ones: part 1 dresses the site with a ring of flag poles
/// (<see cref="FlagPole"/>), hashed per tile like every occasion prop. A player's "Off" hides these (the Decorations facet is
/// cosmetic) and never the score (the <see cref="OccasionFacets.Games"/> facet is gameplay).
/// </summary>
public sealed class FestivalOccasion : Occasion
{
    private static readonly Dictionary<string, ResolvedSite> Sites = new(StringComparer.OrdinalIgnoreCase);

    public FestivalOccasion(string id) => Id = id;

    public override string Id { get; }

    public override string Title => FestivalCatalog.Get(Id)?.Title ?? (Id.Length == 0 ? Id : char.ToUpperInvariant(Id[0]) + Id[1..]);

    // ---- the site ------------------------------------------------------------------------------

    /// <summary>The place this festival is held, once resolved (null: not held in this region).</summary>
    public static ResolvedSite? SiteOf(string id) => Sites.GetValueOrDefault(id);

    /// <summary>Records the resolved site (or forgets it). Returns true when it changed, so the world dresses itself again.</summary>
    public static bool SetSite(string id, ResolvedSite? site)
    {
        if (site == null) return Sites.Remove(id);
        if (Sites.TryGetValue(id, out var was) && was == site) return false;
        Sites[id] = site;
        return true;
    }

    /// <summary>Drops every site a world left behind (<see cref="Core.WorldStatics"/>).</summary>
    public static void ResetSites() => Sites.Clear();

    // ---- props ---------------------------------------------------------------------------------

    private static readonly Color Pole = Matte(0.55f, 0.45f, 0.32f);
    private static readonly Color Red = Matte(0.82f, 0.10f, 0.12f);
    private static readonly Color White = Matte(0.94f, 0.94f, 0.92f);

    /// <summary>A 6 m flag pole with a pennant: red for the Swiss one, white beside it. Seen in the model viewer (Occasions › Prop).</summary>
    public static readonly PropKind FlagPole = new("FestivalFlagPole", () =>
    {
        var s = new MeshScratch();
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 6.2f, 0), 0.07f, 0.045f, Pole, 6);
        s.Box(new Vector3(0, 6.35f, 0), new Vector3(0.16f, 0.16f, 0.16f), White);
        s.Box(new Vector3(0.62f, 5.35f, 0), new Vector3(1.2f, 1.2f, 0.03f), Red);
        // the white cross (an upright and a bar, a little proud of the cloth)
        s.Box(new Vector3(0.62f, 5.35f, 0), new Vector3(0.7f, 0.22f, 0.05f), White);
        s.Box(new Vector3(0.62f, 5.35f, 0), new Vector3(0.22f, 0.7f, 0.05f), White);
        return s.Build();
    }, candle: false);

    private static readonly Color Tube = Matte(0.30f, 0.27f, 0.25f);
    private static readonly Color Wood = Matte(0.50f, 0.38f, 0.24f);
    private static readonly Color Fuse = Matte(0.85f, 0.62f, 0.15f);

    /// <summary>
    /// The mortar rack the closing show is fired from (#1269 part 2): two rows of seven cardboard tubes leaning away from the audience on a
    /// plank frame, a fuse box at one end. Seen in the model viewer (Occasions › Prop). It is the show's launch point; the shells fan out ahead.
    /// </summary>
    public static readonly PropKind MortarRack = new("FestivalMortarRack", () =>
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.12f, 0), new Vector3(3.4f, 0.14f, 1.2f), Wood);
        s.Box(new Vector3(0, 0.05f, 0), new Vector3(3.6f, 0.1f, 1.4f), Wood);
        var lean = new Vector3(0, 0.96f, 0.28f).Normalized();
        for (int row = 0; row < 2; row++)
            for (int k = 0; k < 7; k++)
            {
                var foot = new Vector3(-1.5f + k * 0.5f, 0.2f, -0.28f + row * 0.56f);
                s.Tube(foot, foot + lean * (row == 0 ? 1.1f : 1.4f), 0.13f, 0.13f, Tube, 8);
                s.Box(foot + lean * (row == 0 ? 1.12f : 1.42f), new Vector3(0.2f, 0.04f, 0.2f), Fuse);
            }
        s.Box(new Vector3(1.95f, 0.35f, 0), new Vector3(0.5f, 0.5f, 0.5f), Matte(0.2f, 0.25f, 0.3f));
        s.Box(new Vector3(1.95f, 0.64f, 0), new Vector3(0.56f, 0.06f, 0.56f), Red);
        return s.Build();
    }, candle: false);

    private const double RackOffset = 14;
    private const int Poles = 16;
    private const uint Salt = 0xFE57A;

    /// <summary>The poles of a site: a ring half the site's radius out (at least 12 m, at most 60 m), LV95.</summary>
    public static IEnumerable<(double E, double N, float Yaw)> PolesOf(ResolvedSite site)
    {
        double r = Math.Clamp(site.RadiusM * 0.5, 12, 60);
        for (int k = 0; k < Poles; k++)
        {
            double a = k * Math.Tau / Poles;
            yield return (site.E + Math.Cos(a) * r, site.N + Math.Sin(a) * r, (float)(a + Math.PI / 2));
        }
    }

    public override void Decorate(TileContext tile, DecorBuilder into)
    {
        if (SiteOf(Id) is not { } site) return;
        int salt = (int)(Salt ^ (uint)Id.GetHashCode());
        foreach (var (e, n, yaw) in PolesOf(site))
        {
            if (TileId.FromLv95(e, n) != tile.Id) continue;   // each pole belongs to one tile
            var local = tile.Local(e, n);
            if (tile.HeightAt(local) is not { } h) continue;
            local.Y = h;
            // a slight lean and a quarter-turn jitter so the ring does not read as a fence
            float jitter = OccasionHash.Unit((int)e, (int)n, salt) * 0.5f - 0.25f;
            into.Add(FlagPole, new Transform3D(new Basis(Vector3.Up, yaw + jitter), local));
        }
        // the game grounds: the stone put's stone and lane (#1269 part 3)
        if (FestivalCatalog.Get(Id) is { } stein && stein.Game(SteinRules.GameId) != null) SteinProps.Decorate(site, tile, into);
        Horn.HornGround.Decorate(Id, site, tile, into);   // the alphorn row of the alphorn and yodel game
        if (string.Equals(Id, FestivalCatalog.DesalpeId, StringComparison.OrdinalIgnoreCase)) Desalpe.DesalpeModels.Decorate(site, tile, into);   // the alp sign and the fair's ring
        if (string.Equals(Id, FestivalCatalog.ChristmasMarketId, StringComparison.OrdinalIgnoreCase)) Christmas.ChristmasModels.Decorate(site, tile, into);   // the stalls, the Samichlaus and the tree
        if (string.Equals(Id, FestivalCatalog.EscaladeId, StringComparison.OrdinalIgnoreCase)) Escalade.EscaladeModels.Decorate(site, tile, into);   // the marmite, the soup pot, the start arch and the gates' torches
        if (string.Equals(Id, FestivalCatalog.SechselaeutenId, StringComparison.OrdinalIgnoreCase)) Sechselaeuten.SechselaeutenModels.Decorate(site, tile, into);   // the Zunft's Weibel and the guild banners (the pyre is the stage's)
        // the closing show's launch point: the rack at the site's middle, its tubes leaning away from where the shells fan out
        if (FestivalCatalog.Get(Id) is { } def && FestivalShows.HasFireworks(def.Show) && TileId.FromLv95(site.E - RackOffset, site.N) == tile.Id)
        {
            // 14 m west of the middle, so a crowd standing there is not inside the tubes
            var local = tile.Local(site.E - RackOffset, site.N);
            if (tile.HeightAt(local) is { } h)
            {
                local.Y = h;
                into.Add(MortarRack, new Transform3D(new Basis(Vector3.Up, 0f), local));
            }
        }
        // the flag swingers' ground (#1269 part 5): a low stage with the flag stand, where the Fahnenschwingen game is played
        if (FestivalCatalog.Get(Id) is { } fd && fd.Game(FestivalCatalog.FahneGame) != null)
        {
            var (ge, gn) = FestivalCatalog.FahneGround(site);
            if (TileId.FromLv95(ge, gn) == tile.Id && tile.HeightAt(tile.Local(ge, gn)) is { } gh)
            {
                var local = tile.Local(ge, gn);
                local.Y = gh;
                into.Add(FlagStage, new Transform3D(new Basis(Vector3.Up, 0f), local));
            }
        }
    }

    private static readonly Color Bunting = Matte(0.98f, 0.82f, 0.18f);

    /// <summary>
    /// The flag swingers' stage (#1269 part 5): a square of planks a hand high, a flag stand with a Swiss flag at each corner and a
    /// red-and-white swag between them. Seen in the model viewer (Occasions › Prop); the ground the game is played on.
    /// </summary>
    public static readonly PropKind FlagStage = new("FestivalFlagStage", () =>
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.1f, 0), new Vector3(6f, 0.2f, 6f), Wood);
        s.Box(new Vector3(0, 0.22f, 0), new Vector3(5.6f, 0.04f, 5.6f), Matte(0.62f, 0.5f, 0.34f));
        foreach (var (x, z) in new[] { (-2.8f, -2.8f), (2.8f, -2.8f), (-2.8f, 2.8f), (2.8f, 2.8f) })
        {
            s.Tube(new Vector3(x, 0.2f, z), new Vector3(x, 3.2f, z), 0.05f, 0.035f, Pole, 6);
            s.Box(new Vector3(x + 0.4f, 2.7f, z), new Vector3(0.8f, 0.8f, 0.03f), Red);
            s.Box(new Vector3(x + 0.4f, 2.7f, z), new Vector3(0.46f, 0.14f, 0.05f), White);
            s.Box(new Vector3(x + 0.4f, 2.7f, z), new Vector3(0.14f, 0.46f, 0.05f), White);
        }
        // a swag of pennants along the front and back edges
        for (int k = 0; k < 10; k++)
        {
            float x = -2.5f + k * 0.55f, sag = 0.18f * MathF.Sin(MathF.PI * k / 9f);
            s.Box(new Vector3(x, 2.95f - sag, -2.8f), new Vector3(0.3f, 0.3f, 0.02f), k % 2 == 0 ? Red : White);
            s.Box(new Vector3(x, 2.95f - sag, 2.8f), new Vector3(0.3f, 0.3f, 0.02f), k % 2 == 0 ? Bunting : Red);
        }
        return s.Build();
    }, candle: false);
}
