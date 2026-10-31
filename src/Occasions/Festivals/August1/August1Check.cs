using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.World;

namespace UnitSport.Occasions.Festivals.August1;

/// <summary>
/// <c>--august1check --august1fast --festival test --date 2027-07-31 --world fixture --chunks fixture:flat --pois fixture --systems ui,physics,occasions,adventure</c>
/// (quick tier, #1355): offline, this client plays the server's part, on its own scratch folder (<c>test_output/august1check</c>, never the real adventure or festival
/// saves). The 1 August (held at the fixture's start under <c>--festival test</c>) is on its day 1 of 2. Through the real game: away from the stands a forged use is
/// refused; the lampion stand gives one lampion a day and the lampion lights up in the hand; the fireworks stand sells a box for Batzen, three a day; a box is lit
/// only at night, once in 25 s, and every peer plays the same volley from the same seed; the sky crossing 22:00 starts the lake show on the books, and watching it
/// pays once; the post starts the lampion walk at night only, the village children make up the three walkers, ten stops in order and the post pay by the party's size
/// (a walk at the sprint limit is no walk); a bot is counted and never paid; the Höhenfeuer burn from 21:00 to 05:00 on the summits the hash lights, drawn in one
/// MultiMesh and lifting gliders; the festival opens on 31 July for two days. Windowed it saves <c>test_output/august1_*.png</c>.
/// </summary>
public partial class August1Check : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--august1check");

    private readonly Func<FootPlayer?> _me;

    public August1Check(ItemController items, Func<FootPlayer?> me) : base(items, "august1check", "", "august1_") => _me = me;
    public August1Check() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Svc => FestivalService.Instance!;

    private void Teleport((double E, double N) at, double dn = 0)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(at.E, at.N + dn, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("august1-2027") ?? new FestivalInstance();

    /// <summary>Moves the first stack of <paramref name="id"/> to the last hotbar slot and selects it.</summary>
    private int Hold(ItemId id)
    {
        var inv = _items.Inventory;
        int slot = Inventory.HotbarSize - 1;
        for (int i = 0; i < Inventory.Size; i++)
        {
            if (inv[i].IsEmpty || inv[i].Id != id || i == slot) continue;
            var other = inv[slot];
            inv.Put(slot, inv[i]);
            inv.Put(i, other);
            break;
        }
        inv.Select(slot);
        return slot;
    }

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && August1Game.Instance != null && Hoehenfeuer.Instance != null && Fireworks.Instance != null
                && Me is { } me && me.IsOnFloor() && FestivalService.Instance.RunningOf(August1Rules.FestivalId) != null, 90))
        {
            Fail($"no 1 August (service {FestivalService.Instance != null}, game {August1Game.Instance != null}, fires {Hoehenfeuer.Instance != null}, node {Fireworks.Instance != null}, body {Me?.Global})");
            return;
        }
        var game = August1Game.Instance!;
        var fires = Hoehenfeuer.Instance!;
        var fw = Fireworks.Instance!;
        const string Id = August1Rules.FestivalId;
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var local = AdventureService.LocalPeer;
        var told = new List<(FestivalService.August1Event What, int A, int Ms, string Text)>();
        Svc.August1Told += (w, a, ms, t) => told.Add((w, a, ms, t));
        string? refused = null;
        Svc.Refused += why => refused = why;
        double hour = 12;
        Svc.Hour = () => hour;

        Expect(run.Day == 1 && run.Length == 2, $"day 1 of 2 of the 1 August ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(FestivalOccasion.SiteOf(Id) != null, "the occasion dresses the site (stands, post, fire and stops)");
        Expect(FestivalCatalog.Get(Id)!.Problems().Count == 0, "the festival definition is sound");
        Expect(Mine.Batzen == 0, "a fresh player holds no Batzen");
        var lampStand = August1Rules.LampionStandAt(site);
        var boxStand = August1Rules.FireworkStandAt(site);
        var post = August1Rules.PostAt(site);

        // ---- away from everything: nothing offered, forged uses refused ---------------------------------------
        Teleport((site.E, site.N), 70);
        await Seconds(0.4);
        Expect(August1Game.InteractionFor(Me!) == null && !August1Game.TryInteract(Me!), "no prompt away from the ground, E does nothing");
        foreach (int target in new[] { FestivalService.August1LampionStand, FestivalService.August1FireworkStand, FestivalService.August1Post })
        {
            refused = null;
            Expect(Svc.ServeAugust1(local, target) != null && refused?.Contains("Go to the") == true, $"target {target} from afar is refused ({refused})");
        }
        Expect(Svc.ServeAugust1(local, 9) != null, "a target that does not exist is refused");
        Expect(Mine.Batzen == 0 && Count(ItemId.Lampion) == 0, "nothing was paid or given");

        // ---- the lampion stand ---------------------------------------------------------------------------------------
        Teleport(lampStand, 2.0);
        Expect(await Until(() => game.NearTarget == FestivalService.August1LampionStand, 5), $"the lampion stand is at hand ({game.NearTarget})");
        Expect(August1Game.InteractionFor(Me!) is { } take && take.Text.Contains("Take a lampion"), $"the lampion is offered ({August1Game.InteractionFor(Me!)?.Text})");
        Expect(August1Game.TryInteract(Me!), "E takes a lampion");
        Expect(await Until(() => Count(ItemId.Lampion) == 1, 5), "a lampion is in the pack");
        refused = null;
        Expect(Svc.ServeAugust1(local, FestivalService.August1LampionStand) != null && refused?.Contains("today") == true, $"only one a day ({refused})");
        Expect(ItemDefs.Get(ItemId.Lampion) is { Use: ItemUse.Readout } && (int)ItemId.Lampion is >= 520 and < 525 && (int)ItemId.FireworkBox is >= 520 and < 525, "the items are in the 1 August's id block");
        Hold(ItemId.Lampion);
        await Seconds(0.4);
        var light = Me!.GetNodeOrNull<HeldItemVisual>("HeldItem")?.GetNodeOrNull<OmniLight3D>(HeldItemVisual.TorchLightName);
        Expect(light is { Visible: true }, "the lampion in the hand is a light");
        Expect(light != null && light.LightEnergy < 1.2f, "a softer light than the torch's");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(1.0);
            GD.Print($"[august1check] shot {Shot("lampion")}");
        }

        // ---- the fireworks stand ------------------------------------------------------------------------------------------
        Teleport(boxStand, 2.0);
        Expect(await Until(() => game.NearTarget == FestivalService.August1FireworkStand, 5), "the fireworks stand is at hand");
        Expect(August1Game.InteractionFor(Me!) is { } buy && buy.Text.Contains("Buy a box of fireworks"), $"the box is offered ({August1Game.InteractionFor(Me!)?.Text})");
        refused = null;
        Expect(Svc.ServeAugust1(local, FestivalService.August1FireworkStand) != null && refused?.Contains("costs") == true, $"with no Batzen it is refused ({refused})");
        Expect(Svc.Award(local, Id, 100, "check money") == 100 && Mine.Batzen == 100, "100 Batzen from the check");
        for (int k = 1; k <= August1Rules.BoxesPerDay; k++)
        {
            int before = Count(ItemId.FireworkBox);
            Expect(August1Game.TryInteract(Me!) && await Until(() => Count(ItemId.FireworkBox) == before + 1, 5), $"box {k} bought");
        }
        Expect(Mine.Batzen == 100 - 3 * August1Rules.BoxPrice, $"for {August1Rules.BoxPrice} Batzen each ({Mine.Batzen} left)");
        refused = null;
        Expect(Svc.ServeAugust1(local, FestivalService.August1FireworkStand) != null && refused?.Contains("a day") == true && Count(ItemId.FireworkBox) == 3, $"a fourth is refused ({refused})");

        // ---- the sky crossing 22:00 starts the lake show; watching it pays once -------------------------------------------
        Teleport((site.E, site.N), 0);
        hour = 21.0;
        await Seconds(7);
        Expect(Svc.RunningOf(Id)!.ShowStart == 0 && fw.ShowSeed == 0, "at 21:00 no show is on the books");
        double lead0 = ClockSync.SimNow;
        hour = 22.1;
        Expect(await Until(() => Svc.RunningOf(Id)!.ShowStart > 0, 15), "the sky crossing 22:00 starts a lake show");
        run = Svc.RunningOf(Id)!;
        Expect(run.ShowStart - lead0 is > 8 and < 30, $"a few seconds ahead ({run.ShowStart - lead0:F1} s)");
        Expect(run.ShowSeed == FestivalShows.SeedOf(run.Instance, 1), "the seed is the instance's first");
        Expect(await Until(() => fw.ShowSeed == run.ShowSeed, 5), "the node on the client planned the same show");
        var launch = August1Rules.LaunchFor(site, site.E, site.N);
        Expect(launch.E == site.E && launch.N == site.N + 200 && launch.Bearing == 0, "away from a lake town the shells leave 200 m north of the site's middle");
        int batzenBefore = Mine.Batzen;
        Expect(await Until(() => Svc.WatchersOf(run.Instance) == 1, 90), "standing at the site for 20 s of the show counts as watching it");
        Expect(await Until(() => Mine.Batzen == batzenBefore + August1Rules.ShowBatzen, 5), $"and pays {August1Rules.ShowBatzen} Batzen ({Mine.Batzen - batzenBefore})");
        Expect(told.Any(t => t.What == FestivalService.August1Event.Watched), "she is told");
        fw.Seek(FireworkShow.Plan(run.ShowSeed).Duration + 20);
        await Seconds(8);   // the lake show is over and gone: nothing is playing
        Expect(await Until(() => fw.ShowSeed == 0 || Svc.RunningOf(Id)!.ShowStart == 0, 40), "the lake show ends");
        fw.Skew = 0;   // Seek skewed the node's clock: the volley below is on the server's

        // ---- lighting a box: night only, once in 25 s, every peer plays the same volley ------------------------------------
        hour = 12;
        Hold(ItemId.FireworkBox);
        Expect(!August1Game.TryLight(Me!, out string? why) && why?.Contains("night") == true, $"by day a box is not lit ({why})");
        Expect(Count(ItemId.FireworkBox) == 3, "and is not used up");
        hour = 22.5;
        Expect(August1Game.TryLight(Me!, out _), "at night the box is lit");
        Expect(await Until(() => Svc.August1Volleys == 1, 5), "the server counted a volley");
        Expect(await Until(() => fw.PlayingOwn, 15), "the node plays the volley");
        Expect(fw.ShowSeed == August1Rules.OwnSeed(run.Instance, 1) && FireworkShow.Volley(fw.ShowSeed).Shells.Length == 15, $"from the volley's seed ({fw.ShowSeed})");
        Expect(await Until(() => fw.BurstsFired >= 1, 15), $"its first shell bursts ({fw.BurstsFired})");
        Expect(!August1Game.TryLight(Me!, out why) && why?.Contains("Wait") == true, $"a second box within 25 s waits ({why})");
        refused = null;
        Expect(Svc.ServeAugust1(local, FestivalService.August1Light) != null && refused?.Contains("Wait") == true, $"and a forged second light is refused ({refused})");
        Expect(Svc.August1Volleys == 1, "one volley");
        var volley = FireworkShow.Volley(fw.ShowSeed);
        Expect(volley.Duration == FireworkShow.VolleySeconds && volley.Shells.All(s => s.Height is >= 55 and <= 95), "a volley is 40 s of low shells");
        fw.Seek(volley.Duration + 7);
        Expect(await Until(() => !fw.PlayingOwn, 15), "the volley ends");
        fw.Skew = 0;

        // ---- the lampion walk -----------------------------------------------------------------------------------------------
        var path = August1Rules.Path(site);
        hour = 12;
        Teleport(post, 1.5);
        Expect(await Until(() => game.NearTarget == FestivalService.August1Post, 5), "the post is at hand");
        refused = null;
        Expect(Svc.ServeAugust1(local, FestivalService.August1Post) != null && refused?.Contains("after dark") == true, $"by day there is no walk ({refused})");
        hour = 22.5;
        Expect(August1Game.InteractionFor(Me!) is { } start && start.Text.Contains("lampion walk"), $"the walk is offered ({August1Game.InteractionFor(Me!)?.Text})");
        Expect(August1Game.TryInteract(Me!) && await Until(() => game.Walking, 5), "E starts the walk");
        Expect(game.Walkers == August1Rules.Needed && told.Any(t => t.What == FestivalService.August1Event.WalkStart && t.A == 1 && t.Ms == 2), $"one person and two village children make three walkers ({game.Walkers})");
        await Seconds(0.8);
        Expect(game.Walking && game.NextStop == 0 && Svc.August1Walks == 0, "standing at the post is not a finish: the first stop comes first");
        for (int k = 0; k < August1Rules.Waypoints; k++)
        {
            Teleport(path[k]);
            Expect(await Until(() => game.NextStop == k + 1, 8), $"stop {k + 1} is passed ({game.NextStop})");
        }
        Teleport(path[August1Rules.Waypoints]);
        int earned = Mine.Earned, held = Mine.Batzen;
        Expect(await Until(() => told.Any(t => t.What == FestivalService.August1Event.WalkDone), 8), "the post finishes the walk");
        Expect(!game.Walking && Svc.August1Walks == 1, "the walk is over");
        int pay = August1Rules.WalkPay(1);
        Expect(pay == August1Rules.WalkBatzen, $"alone, the pay is the base ({pay})");
        Expect(await Until(() => Mine.Earned == earned + pay, 5), $"the walk earned {pay} Batzen ({Mine.Earned - earned})");
        // the walk's 75 points on top of the show's 60 take the party past the first milestone (120), which pays its 10 Batzen outside the cap
        Expect(Mine.Batzen == held + pay + 10 && Mine.Milestones.SequenceEqual([0]) && Mine.Paid.GetValueOrDefault("lampion:1") == pay, $"inside the game's day cap, and the first milestone on top ({Mine.Batzen - held})");
        Expect(August1Rules.WalkPay(2) == pay + August1Rules.FriendBatzen && August1Rules.WalkPay(3) == pay + 2 * August1Rules.FriendBatzen && August1Rules.WalkPay(9) == August1Rules.WalkPay(3), "each human friend adds 10, up to three");

        // a second walk the same day is not paid again
        Teleport(post, 1.5);
        await Until(() => game.NearTarget == FestivalService.August1Post, 5);
        August1Game.TryInteract(Me!);
        await Until(() => game.Walking, 5);
        for (int k = 0; k <= August1Rules.Waypoints; k++)
        {
            Teleport(path[k]);
            await Until(() => game.NextStop == k + 1 || !game.Walking, 8);
        }
        held = Mine.Batzen;
        Expect(await Until(() => told.Count(t => t.What == FestivalService.August1Event.WalkDone) == 2, 8), "the second walk is told");
        Expect(Mine.Batzen == held && told.Last().Text.Contains("paid for today"), $"and pays nothing more ({told.Last().Text})");

        // a walk too fast for a body is no walk
        FestivalService.August1SpeedLimit = August1Rules.RunSpeed;
        Teleport(post, 1.5);
        await Until(() => game.NearTarget == FestivalService.August1Post, 5);
        August1Game.TryInteract(Me!);
        await Until(() => game.Walking, 5);
        for (int k = 0; k <= August1Rules.Waypoints; k++)
        {
            Teleport(path[k]);
            await Until(() => game.NextStop == k + 1 || !game.Walking, 8);
            await Seconds(0.5);
        }
        Expect(await Until(() => told.Any(t => t.What == FestivalService.August1Event.WalkLost && t.Text.Contains("not a walk")), 8), "a teleport at the run limit is told it was not a walk");
        FestivalService.August1SpeedLimit = 1e9;

        // ---- a bot is counted, never paid ------------------------------------------------------------------------------------
        const long Bot = -9;
        int bots = Svc.August1BotActs, granted = Svc.GrantedBatzen;
        Expect(Svc.ServeAugust1(Bot, FestivalService.August1Post) == null && Svc.ServeAugust1(Bot, FestivalService.August1LampionStand) == null && Svc.ServeAugust1(Bot, FestivalService.August1Light) == null, "a bot starts, takes and lights");
        Expect(Svc.August1BotActs == bots + 3 && Svc.GrantedBatzen == granted && Svc.BatzenOf(Bot, Id) == 0 && Svc.August1Volleys == 1, "counted, no Batzen granted, no volley");

        // ---- the Höhenfeuer -----------------------------------------------------------------------------------------------------
        hour = 12;
        Expect(await Until(() => !fires.Burning && fires.Count == 0 && fires.Lifts == 0, 5), "by day nothing burns");
        var catalogue = PoiCatalogueHost.Current;
        Expect(await Until(() => (PoiCatalogueHost.Current).Count > 0, 30), $"the fixture's places are loaded ({PoiCatalogueHost.Current.Count})");
        catalogue = PoiCatalogueHost.Current;
        var peaks = catalogue.OfKind(PoiKind.Peak);
        Expect(peaks.Count > 0 && peaks.All(p => p.Elevation < August1Rules.FireCeiling), $"the fixture has peaks, all under 2 500 m ({peaks.Count})");
        int salt = -1;
        for (int s = 0; s < 200 && salt < 0; s++)
        {
            fires.Salt = s;
            if (peaks.Any(p => August1Rules.Lit(p.Hash, p.Elevation, s))) salt = s;
        }
        Expect(salt >= 0, $"the hash lights a fixture summit with a salt ({salt})");
        fires.Salt = Math.Max(0, salt);
        hour = 22.5;
        Expect(await Until(() => fires.Burning && fires.Count > 0, 8), $"at 22:30 the lit summits burn ({fires.Count})");
        Expect(fires.VisibleCount == fires.Count && fires.Count <= fires.Capacity, $"in one MultiMesh, {fires.VisibleCount} of {fires.Capacity} instances");
        Expect(fires.Fires.All(p => August1Rules.Lit(p.Hash, p.Elevation, fires.Salt)), "only the lit summits are drawn");
        Expect(fires.Lifts == fires.Count && AirLift.Volumes.Count >= fires.Lifts, $"and they lift gliders ({fires.Lifts} volumes)");
        var first = fires.Fires[0];
        long key = 0x4800000000L | (long)(first.Hash & 0xFFFFFFFFUL);
        Expect(AirLift.Volumes.Contains(key), "under the hash's key");
        Expect(August1Rules.Burning(21) && August1Rules.Burning(4.9) && !August1Rules.Burning(5) && !August1Rules.Burning(20.9), "lit from 21:00 to 05:00");
        if (DisplayServer.GetName() != "headless")
        {
            Teleport((first.E, first.N), -300);
            await Seconds(1.5);
            GD.Print($"[august1check] shot {Shot("hoehenfeuer")}");
        }
        hour = 12;
        Expect(await Until(() => fires.Count == 0 && fires.Lifts == 0 && !AirLift.Volumes.Contains(key), 8), "at noon they are out and lift nothing");

        // ---- the dates --------------------------------------------------------------------------------------------------------------
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 8, 1));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 2, 15), "1 August is day 2");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 8, 2));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "the festival is over on 2 August");
        refused = null;
        Expect(Svc.ServeAugust1(local, FestivalService.August1LampionStand) != null && refused?.Contains("not on") == true, $"nothing to take then ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 7, 30));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "and not yet open on 30 July");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 7, 31));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 1, 15), "day 1 is 31 July");
        FestivalService.August1SpeedLimit = August1Rules.RunSpeed;
        await Finish(0);
    }
}
