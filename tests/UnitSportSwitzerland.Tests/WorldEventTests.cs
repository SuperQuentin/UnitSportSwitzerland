using UnitSport.Adventure;
using UnitSport.Adventure.Errands;
using UnitSport.Adventure.Navigation;
using UnitSport.Adventure.Places;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The world events (#1339): the rolls, the seasons, the four simulations, the rewards, and the hiker and postcard cards.</summary>
public class WorldEventTests
{
    [Fact]
    public void TheModeSwitchParses()
    {
        Assert.Equal(WorldEventMode.Off, WorldEventRules.ParseMode("off"));
        Assert.Equal(WorldEventMode.Rare, WorldEventRules.ParseMode("RARE"));
        Assert.Equal(WorldEventMode.Normal, WorldEventRules.ParseMode("normal"));
        Assert.Equal(WorldEventMode.Normal, WorldEventRules.ParseMode(null));
    }

    [Fact]
    public void ARollIsTheSameEverywhereAndOffMeansNever()
    {
        int normal = 0, rare = 0;
        for (long slot = 0; slot < 6000; slot++)
        {
            bool a = WorldEventRules.Roll(517, 102, slot, WorldEventMode.Normal, out uint s1);
            bool b = WorldEventRules.Roll(517, 102, slot, WorldEventMode.Normal, out uint s2);
            Assert.Equal(a, b);
            Assert.Equal(s1, s2);
            if (a) normal++;
            if (WorldEventRules.Roll(517, 102, slot, WorldEventMode.Rare, out _)) rare++;
            Assert.False(WorldEventRules.Roll(517, 102, slot, WorldEventMode.Off, out _));
        }
        // one slot in 15 is about 400 of 6000, one in 40 about 150
        Assert.InRange(normal, 300, 500);
        Assert.InRange(rare, 100, 210);
        Assert.True(rare < normal);
    }

    [Fact]
    public void TheKindsComeInAHashOrderAndEveryKindIsThere()
    {
        var order = WorldEventRules.Order(12345);
        Assert.Equal(4, order.Distinct().Count());
        Assert.Equal(order, WorldEventRules.Order(12345));
        Assert.True(Enumerable.Range(0, 40).Select(x => WorldEventRules.Order((uint)x)[0]).Distinct().Count() > 1);
    }

    [Fact]
    public void SeasonsKeepAvalanchesInWinterAndFiresInSummer()
    {
        Assert.True(WorldEventRules.InSeason(WorldEventKind.Avalanche, 1, 2000));
        Assert.False(WorldEventRules.InSeason(WorldEventKind.Avalanche, 7, 2000));
        Assert.True(WorldEventRules.InSeason(WorldEventKind.Avalanche, 7, 2800));   // above 2 600 m all year
        Assert.False(WorldEventRules.InSeason(WorldEventKind.Avalanche, 1, 1200));
        Assert.True(WorldEventRules.InSeason(WorldEventKind.ForestFire, 7, 900));
        Assert.False(WorldEventRules.InSeason(WorldEventKind.ForestFire, 1, 900));
        Assert.False(WorldEventRules.InSeason(WorldEventKind.ForestFire, 7, 2000));
        Assert.True(WorldEventRules.InSeason(WorldEventKind.RunawayCow, 1, 500));
    }

    [Fact]
    public void ALostHikerIsLikelierAtDusk()
    {
        int dusk = 0, noon = 0;
        for (uint seed = 0; seed < 1000; seed++)
        {
            if (WorldEventRules.Passes(WorldEventKind.LostHiker, 18.5, seed)) dusk++;
            if (WorldEventRules.Passes(WorldEventKind.LostHiker, 12, seed)) noon++;
            Assert.True(WorldEventRules.Passes(WorldEventKind.RunawayCow, 3, seed));
        }
        Assert.Equal(1000, dusk);
        Assert.InRange(noon, 450, 550);
    }

    [Fact]
    public void RewardsAndTitlesFollowThePlan()
    {
        Assert.Equal(30, WorldEventRules.Chf(WorldEventKind.RunawayCow));
        Assert.Equal(40, WorldEventRules.Chf(WorldEventKind.LostHiker));
        Assert.Equal(60, WorldEventRules.Chf(WorldEventKind.Avalanche));
        Assert.Equal(40, WorldEventRules.Chf(WorldEventKind.ForestFire));
        var p = new ErrandsProgress();
        for (int i = 1; i < WorldEventRules.TitleAt; i++) Assert.False(WorldEventTally.Add(p, WorldEventKind.Avalanche));
        Assert.True(WorldEventTally.Add(p, WorldEventKind.Avalanche));
        Assert.False(WorldEventTally.Add(p, WorldEventKind.Avalanche));
        Assert.Equal(WorldEventRules.TitleAt + 1, p.Events["Avalanche"]);
        Assert.Equal("Lawinenhund", WorldEventRules.Title(WorldEventKind.Avalanche));
    }

    [Fact]
    public void EventIdsAreStableAndApartFromTheCardHikers()
    {
        int a = WorldEventRules.Id(5, 6, 77, WorldEventKind.RunawayCow);
        Assert.Equal(a, WorldEventRules.Id(5, 6, 77, WorldEventKind.RunawayCow));
        Assert.NotEqual(a, WorldEventRules.Id(5, 6, 77, WorldEventKind.Avalanche));
        Assert.True(a is >= 0 and < 0x40000000);
        Assert.True(WorldEventRules.CardId("pk:20261027:517_102:3") >= 0x40000000);
    }

    // ---- the simulations ------------------------------------------------------------------------------

    [Fact]
    public void ALooseCowWalksOutAndAHelperBehindItDrivesItHome()
    {
        var cow = new CowSim(1000, 1000, 0.25);   // east of home
        var none = new List<Spot>();
        for (int i = 0; i < 10; i++) cow.Step(1, none);
        Assert.True(cow.HomeDistance > 45 + 9, "a loose cow walks away");
        Assert.False(cow.IsHome);
        // a helper on the far side of it pushes it back, at 1.4 m/s
        int steps = 0;
        while (!cow.IsHome && steps++ < 600)
        {
            var helper = new Spot(cow.E + 3, cow.N);   // 3 m east of it (outside), so it walks west
            Assert.True(cow.Step(0.5, new[] { helper }));
        }
        Assert.True(cow.IsHome, $"back after {steps} half-seconds");
    }

    [Fact]
    public void ACowStaysWithinItsStrayAndWalksHomeWhenTheWorldSolvesIt()
    {
        var cow = new CowSim(0, 0, 0);
        for (int i = 0; i < 1000; i++) cow.Step(1, new List<Spot>());
        Assert.InRange(cow.HomeDistance, CowSim.MaxStrayM - 5, CowSim.MaxStrayM + 5);
        for (int i = 0; i < 400 && !cow.IsHome; i++) cow.WalkHome(1);
        Assert.True(cow.IsHome);
    }

    [Fact]
    public void AHikerFollowsTheNearestHelperOnceFoundAndStopsAtTheGoal()
    {
        var hiker = new HikerSim(0, 0, 0, 300);
        var helper = new List<Spot> { new(0, 10) };
        hiker.Step(1, helper);
        Assert.Equal(0, hiker.N);   // not found: sits
        hiker.Found = true;
        hiker.Step(1, new List<Spot> { new(0, 100) });
        Assert.Equal(0, hiker.N);   // the helper is beyond 40 m: waits
        double y = 10;
        for (int i = 0; i < 400 && !hiker.AtGoal; i++)
        {
            y = Math.Min(y + 1.5, 310);
            hiker.Step(1, new List<Spot> { new(0, y) });
        }
        Assert.True(hiker.AtGoal);
    }

    [Fact]
    public void DiggingTakesEightSecondsAlone_AndLessWithHelp()
    {
        var a = new AvalancheSim(0, 0, 2);
        Assert.Equal(1, a.Buried);
        for (int i = 0; i < 7; i++) a.Dig(0, 1, 1);
        Assert.False(a.Done);
        a.Dig(0, 1, 1);
        Assert.True(a.Done);
        var b = new AvalancheSim(0, 0, 3);
        Assert.Equal(2, b.Buried);
        b.Dig(0, 3, 8.0 / 3 + 0.01);
        Assert.True(b.Dug(0));
        b.Dig(1, 9, 8.0 / 3 + 0.01);   // four diggers count as three at most
        Assert.True(b.Done);
    }

    [Fact]
    public void TheTransceiverBeepsQuickerTheNearer()
    {
        Assert.Equal(0f, AvalancheSim.BeepHz(61));
        Assert.True(AvalancheSim.BeepHz(10) > AvalancheSim.BeepHz(50));
        Assert.Equal(4f, AvalancheSim.BeepHz(0), 3);
        Assert.Equal(1f, AvalancheSim.BeepHz(AvalancheSim.DetectM), 3);
        var a = new AvalancheSim(1000, 1000, 5);
        Assert.Equal(0, a.Nearest(a.Spots[0].E, a.Spots[0].N, 20, out double d));
        Assert.Equal(0, d, 3);
        Assert.Equal(-1, a.Nearest(2000, 2000, 60, out _));
    }

    private static bool AllForest(int i, int j) => Math.Abs(i) < 30 && Math.Abs(j) < 30;

    [Fact]
    public void AFireStartsWithSixCellsSpreadsEveryFifteenSecondsAndIsTheSameEverywhere()
    {
        var a = new FireSim(1000, 1000, 77, AllForest);
        var b = new FireSim(1000, 1000, 77, AllForest);
        Assert.Equal(FireSim.Start, a.Count);
        Assert.Equal(a.ToArray(), b.ToArray());
        a.Step(14, AllForest);
        Assert.Equal(FireSim.Start, a.Count);
        Assert.True(a.Step(1, AllForest));
        Assert.Equal(FireSim.Start + 1, a.Count);
        b.Step(15, AllForest);
        Assert.Equal(a.ToArray(), b.ToArray());
        // up to 40, and then the brigade takes over
        for (int i = 0; i < 100 && !a.Overrun; i++) a.Step(15, AllForest);
        Assert.True(a.Overrun);
        Assert.Equal(FireSim.Max, a.Count);
    }

    [Fact]
    public void AFireSpreadsOnlyThroughForest()
    {
        bool Strip(int i, int j) => j == 0 && Math.Abs(i) < 5;
        var f = new FireSim(0, 0, 9, Strip);
        for (int i = 0; i < 40; i++) f.Step(15, Strip);
        foreach (int key in f.Burning) Assert.Equal(0, FireSim.Unpack(key).J);
        Assert.True(f.Count <= 9);
    }

    [Fact]
    public void BeatingPutsACellOutInTwoSecondsAndTheFireEndsWhenNoneBurns()
    {
        var f = new FireSim(0, 0, 1, AllForest);
        foreach (int key in f.ToArray())
        {
            Assert.False(f.Beat(key, 1, 1.9));
            Assert.True(f.Beat(key, 1, 0.2));
        }
        Assert.True(f.IsOut);
        Assert.False(f.Beat(FireSim.Pack(0, 0), 1, 5));   // an extinguished cell is not burning
        var g = new FireSim(0, 0, 1, AllForest);
        int cell = g.ToArray()[0];
        Assert.True(g.Beat(cell, 2, 1.01));   // two beaters take half the time
        Assert.Equal(g.Nearest(0, 0, 100) >= 0, g.Count > 0);
    }

    [Fact]
    public void CellsPackAndUnpack()
    {
        foreach (var (i, j) in new[] { (0, 0), (-58, 58), (58, -58), (7, -3) })
            Assert.Equal((i, j), FireSim.Unpack(FireSim.Pack(i, j)));
    }

    // ---- the steps -------------------------------------------------------------------------------------

    [Fact]
    public void TheNewStepsMatchOnlyTheirEvents()
    {
        var area = new AreaTarget("Hut", 1, 2, 25);
        var escort = new EscortStep("hiker:pk:1", area);
        Assert.True(escort.Matches(new Escorted(1, "k", "hiker:pk:1", area.Id)));
        Assert.False(escort.Matches(new Escorted(1, "k", "hiker:pk:2", area.Id)));
        Assert.Equal("errands", escort.Plan);
        var any = new EventStep();
        var cows = new EventStep(WorldEventKind.RunawayCow);
        Assert.True(any.Matches(new WorldEventHelped(1, "k", 5, WorldEventKind.Avalanche, "VS", true)));
        Assert.False(any.Matches(new WorldEventHelped(1, "k", 5, WorldEventKind.Avalanche, "VS", false)));
        Assert.False(cows.Matches(new WorldEventHelped(1, "k", 5, WorldEventKind.Avalanche, "VS", true)));
        var card = new PostcardStep("ph:chillon", "Château de Chillon");
        Assert.True(card.Matches(new PhotoSeen(1, "k", "ph:chillon")));
        Assert.False(card.Matches(new PhotoSeen(1, "k", "ph:lavaux")));
    }

    // ---- the hiker and postcard cards ----------------------------------------------------------------

    private static readonly PoiCatalogue Pois = PoiCatalogue.Fixture();
    private static readonly GiverCatalogue Givers = GiverCatalogue.Fixture();
    private static readonly (int E, int N) Cell = Postkarte.CellOf(PoiCatalogue.FixtureE, PoiCatalogue.FixtureN);

    private static IEnumerable<Offer> Days(int days, GiverCatalogue givers, PoiCatalogue pois, int cellE, int cellN)
    {
        for (int d = 0; d < days; d++)
            foreach (var o in Postkarte.Offers(20261027 + d, cellE, cellN, givers, pois, TravelRules.Default)) yield return o;
    }

    [Fact]
    public void TheHutWardenOffersALostHikerBy_ThePassAndTheCardBringsThemToTheHut()
    {
        var hiker = Days(40, Givers, Pois, Cell.E, Cell.N).FirstOrDefault(o => o.Template == CardTemplate.LostHiker);
        Assert.NotNull(hiker);
        Assert.Equal(GiverKind.HutWarden, hiker!.Giver.Kind);
        Assert.NotNull(hiker.Hiker);
        Assert.Equal("Fixture Pass", hiker.Hiker!.Near);
        double d = Math.Sqrt(Math.Pow(hiker.Hiker.E - (PoiCatalogue.FixtureE - 200), 2) + Math.Pow(hiker.Hiker.N - (PoiCatalogue.FixtureN + 400), 2));
        Assert.InRange(d, 19.9, 80.1);
        Assert.Equal(2, hiker.Quest.Steps.Length);
        var find = Assert.IsType<InteractStep>(hiker.Quest.Steps[0]);
        Assert.Equal(WorldEventRules.HikerEntity(hiker.Id), find.TargetId);
        var escort = Assert.IsType<EscortStep>(hiker.Quest.Steps[1]);
        Assert.Equal(hiker.Giver.E, escort.At.E);
        Assert.Null(hiker.Cargo);
        Assert.InRange(hiker.Chf, ErrandRewards.Min, ErrandRewards.Max);
        Assert.Equal(QuestStart.Given, hiker.Quest.Start);
    }

    [Fact]
    public void ACardWithAHikerIsTheSameOnEveryPeer()
    {
        var a = Days(30, Givers, Pois, Cell.E, Cell.N).Where(o => o.Template == CardTemplate.LostHiker).ToList();
        var b = Days(30, Givers, Pois, Cell.E, Cell.N).Where(o => o.Template == CardTemplate.LostHiker).ToList();
        Assert.NotEmpty(a);
        Assert.Equal(a.Select(o => (o.Id, o.Hiker)), b.Select(o => (o.Id, o.Hiker)));
    }

    [Fact]
    public void ThePostcardHomeNeedsAFamousViewNearAndAPostOfficeNearIt()
    {
        // Gruyères' castle: a Host and a Postmaster at the village
        var (e, n) = SwissProjection.ToLv95(46.5837, 7.0820);
        Giver G(GiverKind k, string name, double dx) =>
            new($"giver:{GiverCatalogue.Tag(k)}:{ErrandHash.Hex(ErrandHash.Of("t:" + name))}", k, e + dx, n, 0f, "FR", 800, name, "test");
        var givers = new GiverCatalogue(new[] { G(GiverKind.Host, "Beiz Gruyères", 0), G(GiverKind.Postmaster, "Poste Gruyères", 60) });
        var (ce, cn) = Postkarte.CellOf(e, n);
        var cards = Days(30, givers, PoiCatalogue.Empty, ce, cn).Where(o => o.Template == CardTemplate.PostcardHome).ToList();
        Assert.NotEmpty(cards);
        var card = cards[0];
        Assert.Equal(ItemId.Postcard, card.Cargo);
        Assert.Equal(1, card.CargoCount);
        var photo = Assert.IsType<PostcardStep>(card.Quest.Steps[0]);
        Assert.StartsWith("ph:", photo.SpotId);
        var post = Assert.IsType<DeliverStep>(card.Quest.Steps[1]);
        Assert.Equal(ItemId.Postcard, post.Item);
        Assert.Equal("Poste Gruyères", ((AreaTarget)post.At).Name);
        // far from every view: none
        var far = new GiverCatalogue(new[] { G(GiverKind.Host, "Far Beiz", 0), G(GiverKind.Postmaster, "Far Post", 60) });
        var (fe, fn) = (PoiCatalogue.FixtureE, PoiCatalogue.FixtureN);
        Giver F(GiverKind k, string name, double dx) =>
            new($"giver:{GiverCatalogue.Tag(k)}:{ErrandHash.Hex(ErrandHash.Of("f:" + name))}", k, fe + dx, fn + 90000, 0f, "VS", 800, name, "test");
        var lonely = new GiverCatalogue(new[] { F(GiverKind.Host, "Beiz", 0), F(GiverKind.Postmaster, "Post", 60) });
        var (le, ln) = Postkarte.CellOf(fe, fn + 90000);
        Assert.DoesNotContain(Days(30, lonely, PoiCatalogue.Empty, le, ln), o => o.Template == CardTemplate.PostcardHome);
    }
}
