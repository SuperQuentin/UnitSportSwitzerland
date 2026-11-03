using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// <c>--versteckischeck --chunks fixture:hamlet --systems ui,physics,farming,adventure</c> (quick tier, #1268): offline,
/// this client is handed a game at the hamlet's chapel as a server would send it (no game runs offline). Checks the hide
/// plan drawn from the hamlet's tiles (decor, the boundary posts, the objectives' props), every disguise on the local body
/// (the shared mesh swapped in, never a mesh built, the figure hidden, the walk capped, no emote), the round kit on the
/// hotbar, the Disguise radial, the seeker's blind screen, the round-end and results tables, the Activities tab, and the
/// body back to itself and the pack given back at the end. Windowed, it saves <c>test_output/versteckis_*.png</c>:
/// a disguised hider in Cartoon, PS1 and Realistic, the radial, the seeker's view, the results and the Activities tab.
/// </summary>
public partial class VersteckisCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--versteckischeck");

    private readonly Func<FootPlayer?> _me;

    public VersteckisCheck(ItemController items, Func<FootPlayer?> me) : base(items, "versteckischeck", "", "versteckis_") => _me = me;
    public VersteckisCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static bool Windowed => DisplayServer.GetName() != "headless";
    private static VersteckisManager Vs => VersteckisManager.Instance!;

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => VersteckisManager.Instance != null && Me is { } me && me.IsOnFloor(), 90))
        {
            Fail("no Versteckis node or no player on the ground");
            return;
        }
        // the hamlet's chapel (PoiCatalogue.Fixture): the game's centre
        double ce = PoiCatalogue.FixtureE - 150, cn = PoiCatalogue.FixtureN - 120;
        var game = new VsState
        {
            Id = 1, Opener = Vs.Me, Place = "Fixture hamlet", Canton = "VS", E = ce, N = cn, Ground = 500, Radius = VsRules.Radius(4),
            Seed = 1268, Rounds = 3, Round = 1, Phase = VsPhase.Hide, PhaseStarted = Net.ClockSync.SimNow, PhaseEnds = Net.ClockSync.SimNow + 600,
            Seeker = 2, DoorE = ce, DoorN = cn + 8,
        };
        var mine = new VsEntrant { Peer = Vs.Me, Name = "Probe", Role = VsRole.Hider, Prop = VsProp.HayBale, Played = 1 };
        game.Entrants.Add(mine);
        game.Entrants.Add(new VsEntrant { Peer = 2, Name = "Seeker", Role = VsRole.Seeker, Played = 1 });
        Vs.ProbeEnter();
        VersteckisManager.GiveKit(_items.Inventory, VsRole.Hider);
        Expect(SlotOf(ItemId.VsDisguise) == 0 && SlotOf(ItemId.VsMoo) >= 0 && SlotOf(ItemId.VsDecoy) >= 0 && SlotOf(ItemId.VsSprint) >= 0,
            "the hiders' kit is on the hotbar");
        Expect(Permissions.InMatch, "the round is lent: no ride menu, no fly camera, no admin tools");
        Vs.Teleport(ce + 12, cn - 15, null);
        Vs.Apply(new List<VsState> { game });

        // the area: read from the hamlet's tiles, its decor drawn once
        if (!await Until(() => Vs.Layer is { Built: true } && Vs.Site != null, 60)) { Fail("the area was never read"); return; }
        var site = Vs.Site!;
        game.Kinds = site.Kinds;
        Vs.Apply(new List<VsState> { game });
        Expect(site.Buildings.Count >= 8, $"the hamlet's buildings are read ({site.Buildings.Count})");
        Expect(site.Buildings.Any(b => b.Kind == Terrain.Format.BuildingKind.Sacral), "the chapel is among them");
        Expect(site.Facts.Decor >= VsArea.MinDecor && VsArea.Reject(site.Facts) == null, $"the area passes ({site.Facts.Decor} decor, {site.Facts.Buildings} buildings)");
        Expect(Vs.Layer!.DecorCount >= VsArea.MinDecor, $"the decor is drawn ({Vs.Layer.DecorCount} instances)");
        Expect(Vs.Layer.PostCount >= 60, $"the boundary posts are drawn ({Vs.Layer.PostCount})");
        Expect(site.Kinds.Contains(VsProp.HayBale) && site.Kinds.Contains(VsProp.MilkCan) && site.Kinds.Contains(VsProp.Woodpile),
            $"the kinds the village holds: {string.Join(", ", site.Kinds)}");
        Expect(site.Kinds.Contains(VsProp.Cow), "the pasture's herd lets a hider be a cow");
        Expect(site.Sites.Count == 3, "the bell, the fire and the milk can have their sites");
        GD.Print(FormattableString.Invariant($"{Log} decor: {string.Join(", ", site.Decor.GroupBy(d => d.Kind).Select(g => $"{g.Key} {g.Count()}"))}"));

        // every disguise: the shared mesh in, no mesh built, the figure hidden, the walk capped
        var me = Me!;
        await Until(() => VsDisguise.Of(me) != null, 3);
        foreach (var kind in site.Kinds)
        {
            mine.Prop = kind;
            Vs.Apply(new List<VsState> { game });
            var shared = VsPropMeshes.Of(kind);
            await Until(() => VsDisguise.Of(me)?.Kind == kind, 2);
            var d = VsDisguise.Of(me);
            Expect(d != null && d.Kind == kind && ReferenceEquals(d.PropMesh, shared) && ReferenceEquals(VsPropMeshes.Of(kind), shared),
                $"{VsProps.A(kind)}: the shared mesh swapped in, none built");
            await Until(() => me.Visual is { Visible: false }, 1);
            Expect(me.Visual is not { Visible: true }, $"{VsProps.A(kind)}: the figure is hidden");
            Expect(FootPlayer.PropWalk?.Invoke(me) is float cap && Math.Abs(cap - VsProps.Of(kind).WalkCap) < 0.01f && !me.CanEmote,
                $"{VsProps.A(kind)}: walks at {VsProps.Of(kind).WalkCap} m/s, no emote");
        }
        mine.Prop = VsProp.HayBale;
        Vs.Apply(new List<VsState> { game });
        await Until(() => VsDisguise.Of(me)?.Kind == VsProp.HayBale, 2);
        if (Windowed)
        {
            await Seconds(1.5);
            foreach (var (style, name) in new[] { (Styles.VisualStyle.Cartoon, "cartoon"), (Styles.VisualStyle.Ps1, "ps1"), (Styles.VisualStyle.RealisticLow, "realistic") })
            {
                Styles.StyleKit.Choose(style);
                await Seconds(1.5);
                GD.Print($"{Log} shot {Shot($"hider_{name}")}");
            }
            Styles.StyleKit.Choose(Styles.VisualStyle.Cartoon);
        }

        // the Disguise radial: the village's kinds
        _items.UseSlot(me, SlotOf(ItemId.VsDisguise));
        Expect(await Until(() => Vs.Hud?.RadialOpen == true, 2), "the Disguise tool opens the radial");
        if (Windowed) { await Seconds(0.5); GD.Print($"{Log} shot {Shot("radial")}"); }
        Vs.Hud!.CloseRadial();
        Expect(!Vs.Hud.RadialOpen, "the radial closes");

        // the seeker in Hide: the blind screen
        mine.Role = VsRole.Seeker;
        game.Seeker = Vs.Me;
        game.Entrants[1].Role = VsRole.Hider;
        game.Entrants[1].Prop = VsProp.Gnome;
        Vs.Apply(new List<VsState> { game });
        VersteckisManager.GiveKit(_items.Inventory, VsRole.Seeker);
        Expect(SlotOf(ItemId.VsTag) == 0 && SlotOf(ItemId.VsSniff) == 1, "the seeker's kit is on the hotbar");
        Expect(await Until(() => Vs.Hud.Blind, 2), "the seeker counts behind a black screen in Hide");
        Expect(await Until(() => VsDisguise.Of(me) is not { Kind: not VsProp.None }, 2) && me.Visual is { Visible: true }, "the seeker is itself again");
        if (Windowed) { await Seconds(0.5); GD.Print($"{Log} shot {Shot("seeker_blind")}"); }
        game.Phase = VsPhase.Seek;
        Vs.Apply(new List<VsState> { game });
        Expect(await Until(() => !Vs.Hud.Blind, 2), "the screen opens in Seek");
        Expect(Vs.TagPoint(me) is { } tag && VsRules.InReach(me.Global.E, me.Global.N, tag.E, tag.N), "a tag lands within reach");
        if (Windowed) { await Seconds(1); GD.Print($"{Log} shot {Shot("seeker_view")}"); }

        // the results table
        game.Phase = VsPhase.Results;
        mine.Score = 9;
        game.Entrants[1].Score = 6;
        game.Winners = new List<long> { Vs.Me };
        Vs.Apply(new List<VsState> { game });
        Expect(await Until(() => Vs.Hud.TableShown, 2), "the results table shows");
        if (Windowed) { await Seconds(0.5); GD.Print($"{Log} shot {Shot("results")}"); }
        Expect(VsDisguise.Of(me) is not { Kind: not VsProp.None }, "nobody is a prop in the results");

        // the Activities tab
        Ui.ActivitiesScreen.Open(Ui.ActivitiesScreen.Tab.Versteckis);
        Expect(await Until(() => GameShell.Instance?.Top is Ui.ActivitiesScreen, 3), "the Activities screen opens on the Versteckis tab");
        if (Windowed) { await Seconds(1); GD.Print($"{Log} shot {Shot("activities")}"); }
        GameShell.Instance?.Back();

        // the end: the pack back, the area gone
        Vs.ProbeRelease();
        Vs.Apply(new List<VsState>());
        Expect(!Permissions.InMatch && SlotOf(ItemId.VsTag) < 0, "released: the free-roam pack is back");
        Expect(await Until(() => Vs.Layer is { Built: false }, 3), "the area is cleared");
        await Finish(0);
    }
}
