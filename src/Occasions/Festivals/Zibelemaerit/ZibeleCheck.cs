using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Zibelemaerit;

/// <summary>
/// <c>--zibelecheck --festival test --date 2027-11-22 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c> (quick tier, #1373):
/// offline, this client plays the server's part, on its own scratch folder (<c>test_output/zibelecheck</c>, never the real adventure or festival saves).
/// The Zibelemärit (held at the fixture's start under <c>--festival test</c>) is on its one day. The check walks the stalls through the real game: away
/// from them there is no prompt and a forged use is refused; at the first stall E tastes for free (the stamp pays Batzen and points, once a day) and then
/// buys the braid; the other three stalls complete the Marktgang (the stall-hop's whole day cap); wares are bought at their prices and refused without the
/// Batzen; the confetti eggs bought are thrown with the item's Use, and every fifth throw on the square pays, up to the game's day cap; a throw from afar
/// counts for nothing; a bot is counted and earns nothing; the market closes with its dates. Windowed it saves <c>test_output/zibele_*.png</c>.
/// </summary>
public partial class ZibeleCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--zibelecheck");

    private readonly Func<FootPlayer?> _me;

    public ZibeleCheck(ItemController items, Func<FootPlayer?> me) : base(items, "zibelecheck", "", "zibele_") => _me = me;
    public ZibeleCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Svc => FestivalService.Instance!;

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    /// <summary>In front of a stall: 2 m from its middle towards the lane, where the nearest stall is that one.</summary>
    private void AtStall(ResolvedSite site, int stall)
    {
        var st = ZibeleRules.Layout(site)[stall];
        Teleport(st.E, st.N - 2.0);
    }

    /// <summary>Game time, not the engine's timer: the server's throw gap reads <see cref="GameClock"/>, which runs fast under --fixed-fps.</summary>
    private async Task Gap(double s)
    {
        double end = GameClock.Now + s;
        await Until(() => GameClock.Now >= end, s + 2);
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("zibelemaerit-2027") ?? new FestivalInstance();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && ZibeleGame.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(ZibeleRules.FestivalId) != null, 90))
        {
            Fail($"no Zibelemärit (service {FestivalService.Instance != null}, game {ZibeleGame.Instance != null}, body {Me?.Global})");
            return;
        }
        var game = ZibeleGame.Instance!;
        const string Id = ZibeleRules.FestivalId;
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var local = AdventureService.LocalPeer;

        Expect(run.Day == 1 && run.Length == 1, $"day 1 of 1 of the market ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(FestivalOccasion.SiteOf(Id) != null, "the occasion dresses the site (the stalls)");
        Expect(FestivalCatalog.Get(Id)!.Problems().Count == 0, "the festival definition is sound");
        var layout = ZibeleRules.Layout(site);
        Expect(layout.Length == 4 && layout.Select(s => s.Kind).Distinct().Count() == 4, "four stalls, all four wares");
        Expect(Mine.Batzen == 0, "a fresh player holds no Batzen");
        foreach (var id in new[] { ItemId.Zwiebelzopf, ItemId.Zibelechueche, ItemId.Mehlsuppe, ItemId.Konfettiei })
            Expect(ItemDefs.Get(id) != null && (int)id is >= 525 and <= 529, $"{id} is defined, in the Zibelemärit's block 525-529");
        Expect(ItemDefs.Get(ItemId.Konfettiei)!.Use == ItemUse.Confetti, "the confetti egg throws confetti like the Räppli");

        // ---- away from the stalls: nothing offered, a forged use refused ---------------------------------
        Teleport(site.E + 100, site.N + 100);
        await Seconds(0.4);
        string? refused = null;
        Svc.Refused += why => refused = why;
        Expect(ZibeleGame.InteractionFor(Me!) == null && !ZibeleGame.TryInteract(Me!), "no prompt away from the stalls, E does nothing");
        Expect(Svc.ServeZibele(local, 0) != null && refused?.Contains("Go to the stall") == true, $"a use from afar is refused ({refused})");
        Expect(Svc.ServeZibele(local, 55) != null, "a stall that does not exist is refused");
        int throws = Svc.ZibeleThrows;
        Expect(Svc.ServeZibele(local, FestivalService.ZibeleThrow) != null && Svc.ZibeleThrows == throws, "a throw from 140 m out of the square does not count");
        Expect(Mine.Batzen == 0, "nothing was paid");

        // ---- the first stall: a free taste, then the ware --------------------------------------------------
        AtStall(site, 0);
        Expect(await Until(() => game.NearStall == 0, 5), $"stall 0 is at hand ({game.NearStall})");
        Expect(ZibeleGame.InteractionFor(Me!) is { } taste && taste.Text.Contains("Taste at the Onion braid stall") && taste.Target.IsSome, $"the taste is offered with an outline ({ZibeleGame.InteractionFor(Me!)?.Text})");
        Expect(ZibeleGame.TryInteract(Me!), "E uses the stall");
        Expect(await Until(() => Mine.Batzen == ZibeleRules.StampBatzen, 5), $"the taste paid {ZibeleRules.StampBatzen} Batzen ({Mine.Batzen})");
        Expect(Mine.Points == ZibeleRules.StampPoints && Svc.CrewPoints(local, Id) == ZibeleRules.StampPoints, $"and the party points ({Mine.Points} / crew {Svc.CrewPoints(local, Id)})");
        Expect(ZibeleRules.Stamped(Svc.ZibeleStampsToday(1), 0) && Svc.ZibeleStampsToday(2) == 0, "stall 0 is stamped today and not tomorrow");
        Expect(await Until(() => ZibeleGame.InteractionFor(Me!) is { } buy && buy.Text.Contains("Buy Zwiebelzopf (6 Batzen)"), 5), $"now it offers the braid ({ZibeleGame.InteractionFor(Me!)?.Text})");
        Expect(Svc.ServeZibele(local, 0) == null && Mine.Batzen == 0, $"the braid cost 6 Batzen ({Mine.Batzen})");
        Expect(await Until(() => Count(ItemId.Zwiebelzopf) == 1, 5), "and is in the pack");
        Expect(Mine.Earned == ZibeleRules.StampBatzen, "what the cap counts is only what the games paid");
        refused = null;
        Expect(Svc.ServeZibele(local, 0) != null && refused?.Contains("costs 6 Batzen; you hold 0") == true, $"no Batzen, no ware ({refused})");

        // ---- the other three stalls: the Marktgang ----------------------------------------------------------
        for (int i = 1; i < ZibeleRules.Stalls; i++)
        {
            AtStall(site, i);
            Expect(await Until(() => game.NearStall == i, 5), $"stall {i} is at hand");
            Expect(ZibeleGame.TryInteract(Me!), $"E tastes at stall {i}");
            Expect(await Until(() => ZibeleRules.Stamped(Svc.ZibeleStampsToday(1), i), 5), $"stall {i} is stamped");
        }
        // the first two milestones (50 and 120 points) were crossed on the way: 3 + 4 Batzen, outside the cap
        Expect(Mine.Batzen == ZibeleRules.DayMax - 6 + 7 && Mine.Earned == ZibeleRules.DayMax && Mine.Milestones.Count == 2, $"four tastes and the Marktgang earned {ZibeleRules.DayMax} Batzen, a braid spent 6, two milestones paid 7 ({Mine.Batzen} held, {Mine.Earned} earned, {Mine.Milestones.Count} milestones)");
        Expect(Mine.Paid.GetValueOrDefault("zibelemarkt:1") == ZibeleRules.DayMax, "that is the stall-hop's whole day cap");
        int points = ZibeleRules.StampPoints * ZibeleRules.Stalls + ZibeleRules.MarktgangPoints;
        Expect(Mine.Points == points && Svc.ZibeleStampsToday(1) == ZibeleRules.AllStamps, $"{points} points, every stall stamped ({Mine.Points})");

        // ---- the wares at their prices ------------------------------------------------------------------------
        var wares = new (int Stall, ItemId Item, int Price, int Got)[] { (1, ItemId.Zibelechueche, 4, 1), (2, ItemId.Mehlsuppe, 3, 1), (3, ItemId.Konfettiei, 3, 6) };
        foreach (var (stall, item, price, got) in wares)
        {
            AtStall(site, stall);
            await Until(() => game.NearStall == stall, 5);
            int held = Mine.Batzen, had = Count(item);
            Expect(ZibeleGame.TryInteract(Me!), $"E at stall {stall}");
            Expect(await Until(() => Count(item) == had + got, 5) && Mine.Batzen == held - price, $"{got} x {item} for {price} Batzen ({Mine.Batzen} held)");
        }
        Expect(Mine.Earned == ZibeleRules.DayMax, "buying never raises what the cap counts");
        if (DisplayServer.GetName() != "headless")
        {
            AtStall(site, 1);
            await Seconds(1.2);
            GD.Print($"[zibelecheck] shot {Shot("stalls")}");
        }

        // ---- the confetti fight: eggs thrown with Use, every fifth throw pays -----------------------------------
        Expect(Count(ItemId.Konfettiei) == 6, "six confetti eggs in the pack");
        int egg = Enumerable.Range(0, Inventory.Size).First(i => _items.Inventory[i].Id == ItemId.Konfettiei);
        _items.Inventory.Select(Math.Min(egg, Inventory.HotbarSize - 1));
        int batzen = Mine.Batzen;
        for (int k = 0; k < 5; k++)
        {
            _items.UseSlot(Me!, egg);
            await Gap(0.4);
        }
        Expect(Count(ItemId.Konfettiei) == 1, $"five eggs thrown ({Count(ItemId.Konfettiei)} left)");
        Expect(await Until(() => Mine.Paid.GetValueOrDefault("konfetti:1") == ZibeleRules.ThrowBatzen, 5) && Mine.Batzen == batzen + ZibeleRules.ThrowBatzen,
            $"the fifth throw paid {ZibeleRules.ThrowBatzen} Batzen ({Mine.Batzen})");
        Expect(Svc.ZibeleThrows >= 5 && Mine.Points == points + ZibeleRules.ThrowPoints, $"and {ZibeleRules.ThrowPoints} points ({Mine.Points})");
        // more paid rounds than the day allows (the throws' own pay is Award, so the game's cap is what holds; a long loop of throws would run the game clock past midnight)
        for (int k = 0; k < 10; k++)
            Svc.Award(local, Id, ZibeleRules.ThrowBatzen, "a round of confetti", ZibeleRules.ConfettiGame, ZibeleRules.ThrowPoints);
        Expect(Mine.Paid.GetValueOrDefault("konfetti:1") == ZibeleRules.ConfettiDayCap, $"the confetti game's day cap holds ({Mine.Paid.GetValueOrDefault("konfetti:1")} of {ZibeleRules.ConfettiDayCap})");
        Expect(Mine.Earned == ZibeleRules.TokenCap, $"the festival's cap is reached, not passed ({Mine.Earned} of {ZibeleRules.TokenCap})");
        // two throws in the same instant: the second is too fast
        refused = null;
        Svc.ServeZibele(local, FestivalService.ZibeleThrow);
        Expect(Svc.ServeZibele(local, FestivalService.ZibeleThrow) == "Too fast.", "two throws in one instant: the second is refused");

        // ---- a bot: counted, earns nothing ---------------------------------------------------------------------------
        const long Bot = -9;
        int acts = Svc.ZibeleBotActs, sold = Svc.ZibeleSold, granted = Svc.GrantedBatzen;
        Expect(Svc.ServeZibele(Bot, 0) == null && Svc.ServeZibele(Bot, FestivalService.ZibeleThrow) == null && Svc.ZibeleBotActs == acts + 2, "a bot tastes and throws, counted");
        Expect(Svc.GrantedBatzen == granted && Svc.ZibeleSold == sold && Svc.BatzenOf(Bot, Id) == 0, "no Batzen granted, nothing sold");

        // ---- the dates --------------------------------------------------------------------------------------------------
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 11, 23));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "the market is over on the Tuesday");
        refused = null;
        Expect(Svc.ServeZibele(local, 0) != null && refused?.Contains("not on") == true, $"nothing to buy then ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 11, 15));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "and not open on the third Monday");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 11, 22));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 1, 15), "the fourth Monday of November 2027, the 22nd, is the day");
        await Finish(0);
    }
}
