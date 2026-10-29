using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Alcohol;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Christmas;

/// <summary>
/// <c>--christmascheck --festival test --date 2027-11-30 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c> (quick tier, #1348):
/// offline, this client plays the server's part, on its own scratch folder (<c>test_output/christmascheck</c>, never the real adventure or festival saves).
/// The Christmas market (held at the fixture's start under <c>--festival test</c>) is on its day 6 of 29. The check walks the stalls through the real game:
/// away from them there is no prompt and a forged use is refused; at the Glühwein stall E tastes for free (the stamp pays Batzen and points, once a day) and then
/// buys the mug; the mug is drunk (2 dl at 10 % reaches the drink log as 15.8 g, warm for four minutes) and, with alcohol off, carries none; the other seven
/// stalls complete the Marktgang; wares are bought at their prices and refused without the Batzen; the next day stamps again; the Samichlaus refuses before 6
/// December, gives his bag once on it and has gone after; a bot tastes and earns nothing; the market closes with its dates. Windowed it saves
/// <c>test_output/christmas_*.png</c>.
/// </summary>
public partial class ChristmasCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--christmascheck");

    private readonly Func<FootPlayer?> _me;

    public ChristmasCheck(ItemController items, Func<FootPlayer?> me) : base(items, "christmascheck", "", "christmas_") => _me = me;
    public ChristmasCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Svc => FestivalService.Instance!;
    private static List<Shot> DrinkLog => AdventureService.Instance!.Mine.Get(AlcoholProgress.Section).Drinks;

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    /// <summary>In front of a stall: 2 m from its middle towards the lane, where the nearest stall is that one.</summary>
    private void AtStall(ResolvedSite site, int stall)
    {
        var st = ChristmasRules.Layout(site)[stall];
        Teleport(st.E, st.N + (st.Yaw == 0f ? -2.0 : 2.0));
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("christmasmarket-2027") ?? new FestivalInstance();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && ChristmasGame.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(ChristmasRules.FestivalId) != null && AlcoholService.Instance != null, 90))
        {
            Fail($"no Christmas market (service {FestivalService.Instance != null}, game {ChristmasGame.Instance != null}, body {Me?.Global})");
            return;
        }
        var game = ChristmasGame.Instance!;
        const string Id = ChristmasRules.FestivalId;
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var local = AdventureService.LocalPeer;
        AlcoholService.ModeOverride = AlcoholMode.Real;
        DrinkLog.Clear();

        Expect(run.Day == 6 && run.Length == 29, $"day 6 of 29 of the market ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(FestivalOccasion.SiteOf(Id) != null, "the occasion dresses the site (stalls, Samichlaus and tree)");
        Expect(FestivalCatalog.Get(Id)!.Problems().Count == 0, "the festival definition is sound");
        var layout = ChristmasRules.Layout(site);
        Expect(layout.Length == 8 && layout.Select(s => s.Kind).Distinct().Count() == 4, "eight stalls, all four wares");
        Expect(Mine.Batzen == 0, "a fresh player holds no Batzen");

        // ---- away from the stalls: nothing offered, a forged use refused ---------------------------------
        Teleport(site.E + 70, site.N + 70);
        await Seconds(0.4);
        string? refused = null;
        Svc.Refused += why => refused = why;
        Expect(ChristmasGame.InteractionFor(Me!) == null && !ChristmasGame.TryInteract(Me!), "no prompt away from the stalls, E does nothing");
        Expect(Svc.ServeMarket(local, 0) != null && refused?.Contains("Go to the stall") == true, $"a use from afar is refused ({refused})");
        Expect(Svc.ServeMarket(local, 55) != null, "a stall that does not exist is refused");
        Expect(Mine.Batzen == 0, "nothing was paid");

        // ---- the first stall: a free taste, then the ware --------------------------------------------------
        AtStall(site, 0);
        Expect(await Until(() => game.NearStall == 0, 5), $"stall 0 is at hand ({game.NearStall})");
        Expect(layout[0].Kind == StallKind.Gluehwein, "stall 0 sells Glühwein");
        Expect(ChristmasGame.InteractionFor(Me!) is { } taste && taste.Text.Contains("Taste at the Glühwein stall") && taste.Target.IsSome, $"the taste is offered with an outline ({ChristmasGame.InteractionFor(Me!)?.Text})");
        Expect(ChristmasGame.TryInteract(Me!), "E uses the stall");
        Expect(await Until(() => Mine.Batzen == ChristmasRules.StampBatzen, 5), $"the taste paid {ChristmasRules.StampBatzen} Batzen ({Mine.Batzen})");
        Expect(Mine.Points == ChristmasRules.StampPoints && Svc.CrewPoints(local, Id) == ChristmasRules.StampPoints, $"and the party points ({Mine.Points} / crew {Svc.CrewPoints(local, Id)})");
        Expect(ChristmasRules.Stamped(Svc.StampsToday(6), 0) && Svc.StampsToday(7) == 0, "stall 0 is stamped today and not tomorrow");
        Expect(await Until(() => ChristmasGame.InteractionFor(Me!) is { } buy && buy.Text.Contains("Buy Glühwein (4 Batzen)"), 5), $"now it offers the mug ({ChristmasGame.InteractionFor(Me!)?.Text})");
        Expect(Svc.ServeMarket(local, 0) == null && Mine.Batzen == ChristmasRules.StampBatzen - 4, $"the mug cost 4 Batzen ({Mine.Batzen})");
        Expect(await Until(() => Count(ItemId.GluehweinMug) == 1, 5), "and is in the pack");
        Expect(Mine.Earned == ChristmasRules.StampBatzen, "what the cap counts is only what the games paid");

        // ---- the mug: 2 dl at 10 % is 15.8 g, warm for four minutes ---------------------------------------
        var mug = ItemDefs.Get(ItemId.GluehweinMug)!;
        Expect(mug is { DrinkMl: 200f, Abv: 0.10f, Buff: StaminaBuff.Warm } && (int)ItemId.GluehweinMug is >= 500 and < 540, "the mug is 2 dl, 10 %, warm, and id 501 of the festivals block");
        int slot = Enumerable.Range(0, Inventory.Size).First(i => _items.Inventory[i].Id == ItemId.GluehweinMug);
        _items.Inventory.Select(Math.Min(slot, Inventory.HotbarSize - 1));
        _items.UseSlot(Me!, slot);
        Expect(await Until(() => Count(ItemId.GluehweinMug) == 0, 8), "the mug is drunk");
        await Until(() => !_items.UseBusy, 5);
        Expect(DrinkLog.Count == 1 && Math.Abs(DrinkLog.Sum(s => (double)s.G) - AlcoholRules.GluehweinGrams) < 0.3 && Math.Abs(AlcoholRules.GluehweinGrams - 15.8) < 0.1,
            $"the drink log holds the mug's {AlcoholRules.GluehweinGrams:F1} g ({DrinkLog.Sum(s => (double)s.G):F1} g)");
        Expect(Me!.StaminaMeter.BuffLeft(StaminaBuff.Warm) > 200f, $"warm for four minutes ({Me.StaminaMeter.BuffLeft(StaminaBuff.Warm):F0} s)");

        // alcohol off: the same mug carries none
        AlcoholService.ModeOverride = AlcoholMode.Off;
        Expect(Svc.ServeMarket(local, 0) == null && await Until(() => Count(ItemId.GluehweinMug) == 1, 5), "a second mug (the last 4 Batzen)");
        Expect(Mine.Batzen == 0, $"nothing left ({Mine.Batzen})");
        int logged = DrinkLog.Count;
        slot = Enumerable.Range(0, Inventory.Size).First(i => _items.Inventory[i].Id == ItemId.GluehweinMug);
        _items.UseSlot(Me!, slot);
        Expect(await Until(() => Count(ItemId.GluehweinMug) == 0, 8), "drunk again, alcohol off");
        await Until(() => !_items.UseBusy, 5);
        Expect(DrinkLog.Count == logged, "with alcohol off the mug carries no alcohol");
        AlcoholService.ModeOverride = AlcoholMode.Real;

        // ---- no Batzen, no ware ----------------------------------------------------------------------------
        refused = null;
        Expect(Svc.ServeMarket(local, 0) != null && refused?.Contains("costs 4 Batzen; you hold 0") == true, $"without Batzen the mug is refused ({refused})");

        // ---- the other seven stalls: the Marktgang --------------------------------------------------------
        for (int i = 1; i < ChristmasRules.Stalls; i++)
        {
            AtStall(site, i);
            Expect(await Until(() => game.NearStall == i, 5), $"stall {i} is at hand");
            Expect(ChristmasGame.TryInteract(Me!), $"E tastes at stall {i}");
            Expect(await Until(() => ChristmasRules.Stamped(Svc.StampsToday(6), i), 5), $"stall {i} is stamped");
        }
        int hop = ChristmasRules.StampBatzen * ChristmasRules.Stalls + ChristmasRules.MarktgangBatzen;
        Expect(Mine.Batzen == hop - 8 && Mine.Earned == hop, $"eight tastes and the Marktgang earned {hop} Batzen, two mugs spent 8 ({Mine.Batzen} held, {Mine.Earned} earned)");
        Expect(Mine.Paid.GetValueOrDefault("stallhop:6") == ChristmasRules.DayMax && ChristmasRules.DayMax == hop, "that is the game's whole day cap");
        Expect(Svc.StampsToday(6) == ChristmasRules.AllStamps, "every stall stamped");
        int points = ChristmasRules.StampPoints * ChristmasRules.Stalls + ChristmasRules.MarktgangPoints;
        Expect(Mine.Points == points, $"{points} points ({Mine.Points})");
        Expect(Mine.Milestones.Count == 0 && Svc.CrewPoints(local, Id) == points, "the party board holds them, below the first milestone");

        // ---- wares at their prices -------------------------------------------------------------------------
        var wares = new (int Stall, ItemId Item, int Price)[] { (1, ItemId.Raclette, 8), (2, ItemId.Grittibaenz, 3), (3, ItemId.ChristmasBauble, 12) };
        foreach (var (stall, item, price) in wares)
        {
            AtStall(site, stall);
            await Until(() => game.NearStall == stall, 5);
            int held = Mine.Batzen, had = Count(item);
            Expect(ChristmasGame.TryInteract(Me!), $"E at stall {stall}");
            Expect(await Until(() => Count(item) == had + 1, 5) && Mine.Batzen == held - price, $"{item} for {price} Batzen ({Mine.Batzen} held)");
        }
        Expect(ChristmasRules.PriceOf(StallKind.Ornaments) == 12 && ChristmasRules.ItemOf(StallKind.Gluehwein) == ItemId.GluehweinMug, "prices and items are the rules'");
        Expect(Mine.Earned == hop, "buying never raises what the cap counts");
        // not enough: the bauble costs 12
        Mine.Batzen = 5;
        refused = null;
        Expect(Svc.ServeMarket(local, 3) != null && refused?.Contains("costs 12 Batzen; you hold 5") == true, $"a ware too dear is refused ({refused})");
        Expect(Count(ItemId.ChristmasBauble) == 1 && Mine.Batzen == 5, "and costs nothing");
        if (DisplayServer.GetName() != "headless")
        {
            AtStall(site, 4);
            await Seconds(1.2);
            GD.Print($"[christmascheck] shot {Shot("stalls")}");
        }

        // ---- a new day stamps again ---------------------------------------------------------------------------
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 12, 1));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 7, 15), "the date moves to day 7");
        AtStall(site, 0);
        await Until(() => game.NearStall == 0, 5);
        Expect(await Until(() => ChristmasGame.InteractionFor(Me!) is { } t && t.Text.Contains("Taste"), 5), $"stall 0 offers a taste again ({ChristmasGame.InteractionFor(Me!)?.Text})");
        int before = Mine.Batzen;
        Expect(Svc.ServeMarket(local, 0) == null && Mine.Batzen == before + ChristmasRules.StampBatzen, $"and pays it ({Mine.Batzen})");
        Expect(Svc.StampsToday(7) == 1, "one stamp on day 7");

        // ---- the Samichlaus ----------------------------------------------------------------------------------------
        var (se, sn) = ChristmasRules.SamichlausAt(site);
        Teleport(se - 1.5, sn);
        Expect(await Until(() => game.AtSamichlaus, 5), "the Samichlaus is at hand");
        Expect(ChristmasGame.InteractionFor(Me!) is { } greet && greet.Text.Contains("Greet the Samichlaus"), $"on day 7 he only greets ({ChristmasGame.InteractionFor(Me!)?.Text})");
        refused = null;
        Expect(Svc.ServeMarket(local, FestivalService.MarketSamichlaus) != null && refused?.Contains("6 December") == true, $"his bag is refused before 6 December ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 12, 6));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == ChristmasRules.SamichlausDay, 15), "6 December is day 12");
        Expect(await Until(() => ChristmasGame.InteractionFor(Me!) is { } bag && bag.Text.Contains("Take the Samichlaus's bag"), 5), $"he has his bag ({ChristmasGame.InteractionFor(Me!)?.Text})");
        before = Mine.Batzen;
        int mandarins = Count(ItemId.Mandarin), bread = Count(ItemId.Grittibaenz);
        Expect(ChristmasGame.TryInteract(Me!), "E takes the bag");
        Expect(await Until(() => Mine.Batzen == before + ChristmasRules.SamichlausBatzen, 5), $"+{ChristmasRules.SamichlausBatzen} Batzen ({Mine.Batzen})");
        Expect(await Until(() => Count(ItemId.Mandarin) == mandarins + 2 && Count(ItemId.Grittibaenz) == bread + 1, 5), "a Grittibänz and two mandarins");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(1.0);
            GD.Print($"[christmascheck] shot {Shot("samichlaus")}");
        }
        refused = null;
        Expect(Svc.ServeMarket(local, FestivalService.MarketSamichlaus) != null && refused?.Contains("already") == true, $"only once ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 12, 7));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 13, 15), "day 13");
        refused = null;
        Expect(Svc.ServeMarket(local, FestivalService.MarketSamichlaus) != null && refused?.Contains("gone") == true, $"the day after he has gone ({refused})");

        // ---- a bot: it tastes and earns nothing --------------------------------------------------------------------
        const long Bot = -9;
        int sold = Svc.MarketSold, tastes = Svc.MarketBotTastes, granted = Svc.GrantedBatzen;
        Expect(Svc.ServeMarket(Bot, 0) == null && Svc.MarketBotTastes == tastes + 1, "a bot tastes at the Glühwein stall");
        Expect(Svc.ServeMarket(Bot, FestivalService.MarketSamichlaus) != null, "and gets no bag from the Samichlaus");
        Expect(Svc.GrantedBatzen == granted && Svc.MarketSold == sold && Svc.BatzenOf(Bot, Id) == 0, "no Batzen granted, nothing sold");

        // ---- the dates --------------------------------------------------------------------------------------------------
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 12, 24));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "the market is over on 24 December");
        refused = null;
        Expect(Svc.ServeMarket(local, 0) != null && refused?.Contains("not open") == true, $"nothing to buy then ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 11, 24));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "and not yet open on 24 November");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 11, 25));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 1, 15), "day 1 is 25 November");
        AlcoholService.ModeOverride = null;
        await Finish(0);
    }
}
