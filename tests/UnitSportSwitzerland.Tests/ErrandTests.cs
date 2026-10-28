using UnitSport.Adventure;
using UnitSport.Adventure.Errands;
using UnitSport.Adventure.Navigation;
using UnitSport.Adventure.Places;
using UnitSport.Items;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The errands (#1267): the Swiss day, the givers, the daily Postkarten, what they pay, reputation.</summary>
public class ErrandTests
{
    private static readonly TravelRules Rules = TravelRules.Default;
    private static readonly PoiCatalogue Pois = PoiCatalogue.Fixture();
    private static readonly GiverCatalogue Givers = GiverCatalogue.Fixture();
    private static readonly (int E, int N) Cell = Postkarte.CellOf(PoiCatalogue.FixtureE, PoiCatalogue.FixtureN);

    private static PostkarteSource Source() => new(() => Givers, () => Pois, () => Rules, () => 20261027);

    [Fact]
    public void TheDayTurnsAtMidnightInSwitzerland()
    {
        // winter: 23:30 UTC is 00:30 the next day in Zurich; summer: 22:30 UTC is already the next day, 21:30 is not
        Assert.Equal(20261203, ErrandClock.Day(new DateTime(2026, 12, 2, 23, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(20261202, ErrandClock.Day(new DateTime(2026, 12, 2, 22, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(20260702, ErrandClock.Day(new DateTime(2026, 7, 1, 22, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(20260701, ErrandClock.Day(new DateTime(2026, 7, 1, 21, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ACardLivesThroughTheNextDay()
    {
        Assert.True(ErrandClock.Alive(20261027, 20261027));
        Assert.True(ErrandClock.Alive(20261027, 20261028));
        Assert.False(ErrandClock.Alive(20261027, 20261029));
        Assert.False(ErrandClock.Alive(20261028, 20261027));   // a card from the future is not alive either
        Assert.Equal(20261101, ErrandClock.AddDays(20261031, 1));
        Assert.Equal(1, ErrandClock.Between(20261231, 20270101));
    }

    [Fact]
    public void EveryCantonHasASpecialty()
    {
        Assert.Equal(26, Specialties.Cantons.Length);
        foreach (string canton in Specialties.Cantons)
            Assert.NotEqual(ItemId.None, Specialties.For(canton));
        Assert.Equal(12, Specialties.Cantons.Select(Specialties.For).Distinct().Count());
        Assert.Equal(ItemId.Cheese, Specialties.For("FR"));
        Assert.Equal(ItemId.RacletteCheese, Specialties.For("VS"));
    }

    [Fact]
    public void ReputationHasFourLevels()
    {
        Assert.Equal(0, Reputation.Level(9));
        Assert.Equal(1, Reputation.Level(10));
        Assert.Equal(2, Reputation.Level(30));
        Assert.Equal(3, Reputation.Level(60));
        Assert.Equal(4, Reputation.Level(100));
        Assert.Equal(4, Reputation.Level(5000));
        Assert.Equal(30, Reputation.NextAt(10));
        Assert.Null(Reputation.NextAt(100));
        Assert.Equal("Ehrenbürger von Valais", Reputation.TitleOf("VS"));
        Assert.Equal(("VS", 5), Reputation.Parse("VS:+5"));
        Assert.Null(Reputation.Parse("VS:0"));
        Assert.Null(Reputation.Parse("Valais:+5"));
        Assert.Null(Reputation.Parse("VS:+x"));
    }

    [Fact]
    public void TheDaysCardsStartAgainAtMidnight()
    {
        var p = new ErrandsProgress();
        Assert.True(p.CanTake(20261027));
        p.Roll(20261027);
        for (int i = 0; i < ErrandsProgress.DailyCards; i++) p.Taken.Add($"pk:20261027:517_222:{i}");
        Assert.False(p.CanTake(20261027));
        Assert.False(p.Roll(20261027));
        Assert.True(p.CanTake(20261028));
        Assert.True(p.Roll(20261028));
        Assert.Empty(p.Taken);
    }

    [Fact]
    public void BonusRecordsArePrunedWithTheirCards()
    {
        var p = new ErrandsProgress { BonusPaid = { "pk:20260101:517_222:1", "pk:20261020:517_222:2" } };
        p.Prune(20261001);
        Assert.Equal(new[] { "pk:20261020:517_222:2" }, p.BonusPaid);
    }

    [Fact]
    public void ACardIdSaysEverything()
    {
        string id = Postkarte.IdOf(20261027, 517, 102, 3);
        Assert.Equal("pk:20261027:517_102:3", id);
        Assert.True(Postkarte.TryParse(id, out int day, out int ce, out int cn, out int slot));
        Assert.Equal((20261027, 517, 102, 3), (day, ce, cn, slot));
        foreach (string bad in new[] { "", "pk:", "pk:20261027:517_102", "pk:20261027:517_102:9", "pk:1:517_102:1", "pk:20261027:x_102:1", "quest:a", "pk:20261027:517_102:1:2" })
            Assert.False(Postkarte.TryParse(bad, out _, out _, out _, out _), bad);
    }

    [Fact]
    public void TheFixtureCellOffersTheSameCardsInAnyOrder()
    {
        var a = Source().Cell(20261027, Cell.E, Cell.N);
        Assert.NotEmpty(a);
        Assert.True(a.Count <= Postkarte.PerCell);
        // another source, another call order: the same cards
        var other = Source();
        other.Cell(20261028, Cell.E, Cell.N);
        var b = other.Cell(20261027, Cell.E, Cell.N);
        Assert.Equal(a.Select(o => (o.Id, o.Giver.Id, o.Template, o.Chf, o.Hash)), b.Select(o => (o.Id, o.Giver.Id, o.Template, o.Chf, o.Hash)));
        Assert.All(other.Cell(20261028, Cell.E, Cell.N), o => Assert.Contains("20261028", o.Id));
    }

    [Fact]
    public void ResolveRebuildsACardFromItsId()
    {
        var src = Source();
        foreach (var offer in src.Cell(20261027, Cell.E, Cell.N))
        {
            var def = src.Resolve(offer.Id);
            Assert.Same(offer.Quest, def);
            Assert.Equal(QuestStart.Given, def!.Start);
            Assert.Equal("postkarte", def.Source);
            Assert.Equal(offer.Chf, def.Rewards.OfType<Cash>().Single().Chf);
        }
        Assert.Null(src.Resolve("pk:20261027:517_102:7"));      // no such slot in an empty cell
        Assert.Null(src.Resolve("pk:garbage"));
        Assert.Null(src.Resolve("tower"));
        Assert.Empty(src.Cell(20261027, 1, 1));                  // a cell with no givers offers none
    }

    [Fact]
    public void TheFixtureOffersTheMailRunWithThreeSacks()
    {
        var src = Source();
        // the post office gives two of its templates in a hash order: look over a few days for the mail run
        Offer? mail = null;
        for (int day = 20261027; day < 20261037 && mail == null; day++)
            mail = src.Cell(day, Cell.E, Cell.N).FirstOrDefault(o => o.Template == CardTemplate.MailRun);
        Assert.NotNull(mail);
        Assert.Equal((ItemId.MailSack, 3), (mail!.Cargo!.Value, mail.CargoCount));
        Assert.Equal(3, mail.Quest.Steps.Length);
        Assert.All(mail.Quest.Steps, s => Assert.Equal(ItemId.MailSack, ((DeliverStep)s).Item));
        Assert.Equal(3, mail.Quest.Steps.Select(s => s.Target!.Id).Distinct().Count());
        Assert.Contains(mail.Quest.Rewards, r => r is Unlock { Kind: "rep", Arg: "VS:+3" });
    }

    [Fact]
    public void EveryCardPaysItsFarePlusFifteenAndStaysInRange()
    {
        var src = Source();
        int cards = 0;
        for (int day = 20261001; day < 20261031; day++)
            foreach (var o in src.Cell(day, Cell.E, Cell.N))
            {
                cards++;
                Assert.InRange(o.Chf, ErrandRewards.Min, ErrandRewards.Max);
                var last = (AreaTarget)o.Quest.Steps.Last().Target!;
                double km = Math.Sqrt(Math.Pow(last.E - o.Giver.E, 2) + Math.Pow(last.N - o.Giver.N, 2)) / 1000;
                Assert.True(o.Chf >= ErrandRewards.FullFare(o.Template, km, Rules) + ErrandRewards.FareMargin, $"{o.Id} {o.Template} pays {o.Chf} for {km:F1} km");
            }
        Assert.True(cards > 60);
    }

    [Fact]
    public void RewardsFollowTheFares()
    {
        // an 8 km mail run: 10 km on the SBB tariff (CHF 5.00) -> 25 + 1.5 x 5 = 32.5 -> 35
        Assert.Equal(35, ErrandRewards.For(CardTemplate.MailRun, 8, 0, null, 0, Rules));
        // a free walk down: only the base
        Assert.Equal(20, ErrandRewards.For(CardTemplate.AlpCheese, 0, 0, null, 0, Rules));
        // the cap
        Assert.Equal(180, ErrandRewards.For(CardTemplate.Specialty, 500, 0, null, 0, Rules));
        Assert.Equal(25, ErrandRewards.Bonus(50));
        Assert.Equal(20, ErrandRewards.Bonus(40));
        // a farmer's request is worth more with dearer goods
        Assert.True(ErrandRewards.For(CardTemplate.FarmerRequest, 0, 0, ItemId.HayBale, 6, Rules) > ErrandRewards.For(CardTemplate.FarmerRequest, 0, 0, ItemId.Potato, 2, Rules));
    }

    [Fact]
    public void ThePoisMakeStationsAndLandingsGivers()
    {
        var fromPois = GiverCatalogue.FromPois(Pois).ToList();
        Assert.Contains(fromPois, g => g.Kind == GiverKind.StationMaster);
        Assert.Contains(fromPois, g => g.Kind == GiverKind.ShipMaster);
        Assert.DoesNotContain(fromPois, g => g.Kind == GiverKind.HutWarden);   // the fixture has no hut
        var cat = new GiverCatalogue(fromPois);
        var station = fromPois.First(g => g.Kind == GiverKind.StationMaster);
        Assert.Equal(station, cat.Resolve(station.Id));
        Assert.Equal(fromPois.Select(g => g.Id), GiverCatalogue.FromPois(Pois).Select(g => g.Id));
    }

    [Fact]
    public void NearAndInCellFindTheFixtureGivers()
    {
        var into = new List<Giver>();
        Givers.Near(PoiCatalogue.FixtureE, PoiCatalogue.FixtureN, 150, into);
        Assert.Contains(into, g => g.Kind == GiverKind.Farmer);
        Assert.DoesNotContain(into, g => g.Kind == GiverKind.StationMaster);   // 320 m away
        Givers.InCell(Cell.E, Cell.N, into);
        Assert.Equal(Givers.Count, into.Count);
        Givers.InCell(Cell.E + 1, Cell.N, into);
        Assert.Empty(into);
        Assert.Null(Givers.Resolve("giver:farm:nope"));
        Assert.Equal(0, GiverCatalogue.Empty.Count);
    }

    [Fact]
    public void NamesAreStableAndFollowTheLanguage()
    {
        string id = "giver:hut:0123456789";
        Assert.Equal(GiverNames.For(id, GiverKind.HutWarden, "UR"), GiverNames.For(id, GiverKind.HutWarden, "UR"));
        Assert.Contains("hut warden", GiverNames.For(id, GiverKind.HutWarden, "UR"));
        Assert.Contains("fromager", GiverNames.For("giver:dairy:1", GiverKind.Dairy, "VD"));
        Assert.Equal("Arrivederci", GiverNames.Farewell("TI", "x"));
        Assert.Equal("Uf Widerluege", GiverNames.Farewell("ZH", "x"));
    }

    [Fact]
    public void AGivenQuestStartsOnlyThroughItsGiver()
    {
        var src = Source();
        var engine = new QuestEngine(src);
        var offer = src.Cell(20261027, Cell.E, Cell.N).First();
        var progress = new QuestsProgress();
        Assert.Equal("Ask the giver.", engine.Start(progress, offer.Id, 1));
        Assert.Empty(progress.Active);
        Assert.Null(engine.Start(progress, offer.Id, 1, seed: offer.Hash, given: true));
        Assert.Equal(offer.Hash, progress.Active[offer.Id].Seed);
        Assert.DoesNotContain(engine.Available(progress, new QuestContext(progress, PoiCatalogue.FixtureE, PoiCatalogue.FixtureN)), q => q.Id == offer.Id);
    }

    [Fact]
    public void ACardCompletesByHandingItsCargoOver()
    {
        var src = Source();
        var engine = new QuestEngine(src);
        var offer = src.Cell(20261027, Cell.E, Cell.N).First(o => o.Template == CardTemplate.FarmerRequest);
        var p = new QuestsProgress();
        Assert.Null(engine.Start(p, offer.Id, 1, given: true));
        var step = (DeliverStep)offer.Quest.Steps[0];
        var changes = engine.OnEvent(p, new ItemDelivered(1, "local", step.Item, step.Count, step.At.Id), 2);
        Assert.Contains(changes, c => c.What == QuestChangeKind.Completed);
        Assert.True(p.Done.ContainsKey(offer.Id));
    }
}
