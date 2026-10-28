using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>
/// The alphorn row of a festival ground (#1269): three horns on trestles a few metres from the site's middle, with a spot to stand at by their mouthpieces, the place E (Y on a pad, a
/// hand in VR) starts a song. It is a point derived from the site, so the server and every client agree with nothing sent; the prop
/// is dressed by <see cref="FestivalOccasion"/> like the flag poles (hashed per tile, shown in the model viewer under Occasions › Prop).
/// </summary>
public static class HornGround
{
    /// <summary>The game's id in a <see cref="FestivalDef"/>.</summary>
    public const string GameId = AlphornRules.GameId;
    /// <summary>The row stands this far east and north of the site's middle, LV95 metres.</summary>
    public const double OffsetE = 16, OffsetN = 7;
    /// <summary>Within this of the row a player can start a song (the server allows a little more for lag).</summary>
    public const double ReachM = 7, ServerReachM = 12;
    /// <summary>The prop's middle is this far east of the spot a player stands at: the mouthpieces are about a metre from it.</summary>
    public const double HornsAheadM = 3.2;

    public static (double E, double N) SpotOf(ResolvedSite site) => (site.E + OffsetE, site.N + OffsetN);

    public static bool Near(ResolvedSite site, double e, double n, double reach)
    {
        var (re, rn) = SpotOf(site);
        return (e - re) * (e - re) + (n - rn) * (n - rn) <= reach * reach;
    }

    // ---- the prop ---------------------------------------------------------------------------------

    private static readonly Color Brass = Matte(0.62f, 0.48f, 0.18f);
    private static readonly Color Spruce = Matte(0.58f, 0.40f, 0.22f);
    private static readonly Color Strap = Matte(0.20f, 0.18f, 0.16f);
    private static readonly Color Trestle = Matte(0.40f, 0.32f, 0.24f);

    /// <summary>
    /// Three alphorns lying on trestles, bells to the east: a 3.4 m spruce tube widening from a mouthpiece at one end, bound with
    /// bands, its bell turned up. Built once, never per frame.
    /// </summary>
    public static readonly PropKind AlphornRow = new("FestivalAlphornRow", () =>
    {
        var s = new MeshScratch();
        for (int k = -1; k <= 1; k++)
        {
            float z = k * 1.5f;
            s.Tube(new Vector3(-1.9f, 1.05f, z), new Vector3(1.2f, 1.45f, z), 0.025f, 0.075f, Spruce, 8);
            // each part starts a centimetre inside the one before it, so no two caps or sides share a plane (z-fight baseline)
            s.Tube(new Vector3(1.19f, 1.446f, z), new Vector3(1.9f, 1.95f, z), 0.0745f, 0.19f, Spruce, 8);
            s.Tube(new Vector3(1.86f, 1.92f, z), new Vector3(2.02f, 2.14f, z), 0.2f, 0.18f, Brass, 8);   // the bell's rim
            s.Tube(new Vector3(-2.05f, 1.0f, z), new Vector3(-1.85f, 1.07f, z), 0.035f, 0.02f, Brass, 8);   // the mouthpiece
            for (int b = 0; b < 4; b++)
            {
                float x = -1.2f + b * 0.8f;
                float y = 1.05f + (x + 1.9f) / 3.1f * 0.4f;
                s.Box(new Vector3(x, y, z), new Vector3(0.04f, 0.15f, 0.15f), Strap);
            }
            foreach (float tx in new[] { -1.0f, 0.9f })
            {
                float top = 1.05f + (tx + 1.9f) / 3.1f * 0.4f - 0.06f;
                s.Tube(new Vector3(tx - 0.25f, 0f, z - 0.3f), new Vector3(tx, top, z), 0.04f, 0.035f, Trestle, 5);
                s.Tube(new Vector3(tx - 0.25f, 0f, z + 0.3f), new Vector3(tx, top, z), 0.04f, 0.035f, Trestle, 5);
            }
        }
        return s.Build();
    }, candle: false);

    /// <summary>Dresses the row on the tile that owns its spot (called by <see cref="FestivalOccasion.Decorate"/>).</summary>
    public static void Decorate(string festival, ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        if (FestivalCatalog.Get(festival)?.Game(GameId) == null) return;
        var (e, n) = SpotOf(site);
        e += HornsAheadM;   // the horns lie east of where the player stands, mouthpieces towards them
        if (TileId.FromLv95(e, n) != tile.Id) return;
        var local = tile.Local(e, n);
        if (tile.HeightAt(local) is not { } h) return;
        local.Y = h;
        into.Add(AlphornRow, new Transform3D(Basis.Identity, local));
    }

    // ---- the interaction --------------------------------------------------------------------------

    /// <summary>The running festival whose alphorn game is open and whose row this body stands at, or null.</summary>
    public static RunningFestival? At(FootPlayer p)
    {
        if (FestivalService.Instance is not { } svc) return null;
        var g = p.Global;
        foreach (var run in svc.Running)
            if (run.Site is { } site && FestivalCatalog.Get(run.Id) is { } def && def.IsOpen(GameId, run.Day) && Near(site, g.E, g.N, ReachM))
                return run;
        return null;
    }

    private static string? _prompt;
    private static InputDevice _promptDevice = (InputDevice)(-1);

    /// <summary>"[E] Play the alphorn" at the row; null away from it or while a song is on.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (HornGame.Instance is not { Idle: true } || At(p) == null) return null;
        if (_prompt == null || _promptDevice != PlayerInput.HintDevice)
        {
            _promptDevice = PlayerInput.HintDevice;
            _prompt = InputHints.Prompt(PlayerInput.InteractMount, "Play the alphorn and yodel");
        }
        return new Interaction(_prompt, default);
    }

    /// <summary>E / Y / a VR hand at the row: asks the server for a seat in the next song. False when none is in reach.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (HornGame.Instance is not { Idle: true } game || At(p) == null) return false;
        game.Begin();
        return true;
    }
}
