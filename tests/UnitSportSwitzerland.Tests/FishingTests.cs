using UnitSport.Crafting;
using UnitSport.Items;
using UnitSport.Items.Fishing;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Fishing (#493): the species table, where they live, the law on them, the fight and the cooking.</summary>
public class FishingTests
{
    // ---- the waters ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Basel", 2_611_500, 1_267_500, Basin.Rhine)]
    [InlineData("Zurich", 2_683_000, 1_247_000, Basin.Rhine)]
    [InlineData("Bern", 2_600_000, 1_199_500, Basin.Rhine)]
    [InlineData("Interlaken", 2_632_000, 1_170_000, Basin.Rhine)]
    [InlineData("Gstaad (the Saane)", 2_588_000, 1_146_000, Basin.Rhine)]
    [InlineData("Neuchâtel", 2_561_000, 1_205_000, Basin.Rhine)]
    [InlineData("Biel", 2_585_000, 1_221_000, Basin.Rhine)]
    [InlineData("Delémont (the Birs)", 2_593_000, 1_245_000, Basin.Rhine)]
    [InlineData("Andermatt (the Reuss)", 2_688_000, 1_165_000, Basin.Rhine)]
    [InlineData("Chur", 2_759_000, 1_191_000, Basin.Rhine)]
    [InlineData("Davos", 2_783_000, 1_186_000, Basin.Rhine)]
    [InlineData("Splügen (the Hinterrhein)", 2_745_000, 1_159_000, Basin.Rhine)]
    [InlineData("Geneva", 2_500_000, 1_118_000, Basin.Rhone)]
    [InlineData("Lausanne", 2_538_000, 1_152_000, Basin.Rhone)]
    [InlineData("Montreux", 2_560_000, 1_142_000, Basin.Rhone)]
    [InlineData("Sion", 2_594_000, 1_120_000, Basin.Rhone)]
    [InlineData("Zermatt", 2_624_000, 1_097_000, Basin.Rhone)]
    [InlineData("Oberwald (the Goms)", 2_669_000, 1_154_000, Basin.Rhone)]
    [InlineData("Airolo", 2_689_000, 1_153_000, Basin.Ticino)]
    [InlineData("Bellinzona", 2_722_000, 1_117_000, Basin.Ticino)]
    [InlineData("Locarno", 2_705_000, 1_114_000, Basin.Ticino)]
    [InlineData("Lugano", 2_717_000, 1_096_000, Basin.Ticino)]
    [InlineData("Vicosoprano (the Bergell)", 2_766_000, 1_135_000, Basin.Ticino)]
    [InlineData("Poschiavo", 2_802_000, 1_127_000, Basin.Ticino)]
    [InlineData("Sils", 2_778_000, 1_144_000, Basin.Inn)]
    [InlineData("St. Moritz", 2_784_500, 1_152_500, Basin.Inn)]
    [InlineData("Zernez", 2_803_000, 1_175_000, Basin.Inn)]
    [InlineData("Scuol", 2_817_000, 1_186_000, Basin.Inn)]
    [InlineData("St-Ursanne", 2_578_000, 1_246_000, Basin.Doubs)]
    [InlineData("Saignelégier", 2_567_000, 1_234_000, Basin.Doubs)]
    [InlineData("La Chaux-de-Fonds", 2_554_000, 1_217_000, Basin.Doubs)]
    [InlineData("Porrentruy", 2_573_000, 1_251_000, Basin.Doubs)]
    public void Towns_drain_to_their_real_basin(string town, double e, double n, Basin basin) =>
        Assert.True(FishWaters.BasinAt(e, n) == basin, $"{town}: {FishWaters.BasinAt(e, n)}, not {basin}");

    [Fact]
    public void Lakes_are_found_by_place_and_level()
    {
        Assert.Equal("Lake Geneva", FishWaters.LakeAt(2_530_000, 1_140_000, 372)?.Name);
        Assert.Equal("Lake Zurich", FishWaters.LakeAt(2_690_000, 1_236_000, 406)?.Name);
        Assert.Equal("Lake Murten", FishWaters.LakeAt(2_575_000, 1_197_000, 429)?.Name);
        // the south end of Lake Zug lies inside Lake Lucerne's reach: the level tells them apart
        Assert.Equal("Lake Zug", FishWaters.LakeAt(2_680_000, 1_212_000, 413)?.Name);
        Assert.Equal("Lake Sils", FishWaters.LakeAt(2_776_500, 1_143_500, 1797)?.Name);
        Assert.Null(FishWaters.LakeAt(2_690_000, 1_236_000, 700));   // a reservoir above Lake Zurich
        Assert.Null(FishWaters.LakeAt(2_600_000, 1_199_500, 540));   // Bern
        // every lake is in the basin its fish say
        foreach (var l in FishWaters.Lakes)
            Assert.All(l.Known, id => Assert.True((FishCatalog.Of(id)!.Basins & FishWaters.BasinAt(l.E, l.N)) != 0,
                $"{l.Name}: {id} does not live in the {FishWaters.BasinAt(l.E, l.N)}"));
    }

    [Fact]
    public void Water_kinds_come_from_slope_altitude_and_the_lake()
    {
        var geneva = FishWaters.Lakes[0];
        Assert.Equal(WaterKind.LargeLake, FishWaters.Classify(true, 372, 0, geneva));
        Assert.Equal(WaterKind.River, FishWaters.Classify(true, 500, 0.006, null));
        Assert.Equal(WaterKind.SmallLake, FishWaters.Classify(true, 600, 0, null));
        Assert.Equal(WaterKind.AlpineLake, FishWaters.Classify(true, 2100, 0, null));
        Assert.Equal(WaterKind.MountainStream, FishWaters.Classify(false, 1400, 0, null));
        Assert.Equal(WaterKind.River, FishWaters.Classify(false, 500, 0, null));
        Assert.Equal(WaterKind.AlpineLake, FishWaters.Spot(true, 2_776_500, 1_143_500, 1797, 0).Kind);
    }

    // ---- the catalogue ------------------------------------------------------------------------------

    [Fact]
    public void Every_kept_fish_is_one_item_with_a_dish()
    {
        var items = FishCatalog.All.Where(s => s.Item != ItemId.None).Select(s => s.Item).ToList();
        Assert.Equal(items.Count, items.Distinct().Count());
        Assert.All(items, id =>
        {
            Assert.Same(FishCatalog.Of(id), FishCatalog.All.First(s => s.Item == id));
            Assert.NotEqual(Dish.None, FishCatalog.DishOf(id).Dish);
            Assert.InRange((int)id, 200, 299);
        });
        Assert.All(FishCatalog.All, s =>
        {
            Assert.True(s.CmMin < s.CmMax && s.AltMin < s.AltMax && s.Kind > 0, s.Name);
            Assert.NotEqual(WaterKind.None, s.Waters);
            Assert.NotEqual(Basin.None, s.Basins);
        });
        // the critically endangered and the extinct are never kept
        Assert.All(FishCatalog.All.Where(s => s.RedList is "CR" || s.RedList.StartsWith("extinct")), s => Assert.True(s.Protected, s.Name));
    }

    [Theory]
    [InlineData("Brown trout", "Oct–Feb")]
    [InlineData("Grayling", "Feb–Apr")]
    [InlineData("Whitefish", "Dec–Jan")]
    [InlineData("Perch", "May")]
    [InlineData("Carp", "")]
    public void Closed_seasons_read_as_months(string fish, string text) =>
        Assert.Equal(text, FishCatalog.Named(fish)!.ClosedText);

    [Fact]
    public void Weight_follows_length()
    {
        var pike = FishCatalog.Of(ItemId.Pike)!;
        Assert.InRange(pike.KgAt(80), 3f, 4f);   // an 80 cm pike weighs about 3.3 kg
        var perch = FishCatalog.Of(ItemId.Perch)!;
        Assert.InRange(perch.KgAt(25), 0.15f, 0.25f);
    }

    // ---- the law ------------------------------------------------------------------------------------

    private static FishSpot Lake(string name) =>
        new(WaterKind.LargeLake, Basin.Rhine, 400, FishWaters.Lakes.First(l => l.Name == name), "");

    private static readonly FishSpot River = new(WaterKind.River, Basin.Rhine, 400, null, "");

    [Fact]
    public void The_rules_send_fish_back()
    {
        var grayling = FishCatalog.Of(ItemId.Grayling)!;
        Assert.Equal(Verdict.ClosedSeason, FishRules.Judge(grayling, 40, 3, River));
        Assert.Equal(Verdict.Undersized, FishRules.Judge(grayling, 25, 6, River));
        Assert.Equal(Verdict.Keep, FishRules.Judge(grayling, 35, 6, River));
        Assert.Equal(Verdict.Keep, FishRules.Judge(FishCatalog.Of(ItemId.Pike)!, 60, 6, River));
        Assert.Equal(Verdict.Protected, FishRules.Judge(FishCatalog.Named("Apron")!, 15, 6, River));
        Assert.Equal(Verdict.Protected, FishRules.Judge(FishCatalog.Named("Atlantic salmon")!, 80, 6, River));
        Assert.Equal(Verdict.Culled, FishRules.Judge(FishCatalog.Named("Three-spined stickleback")!, 6, 6, Lake("Lake Constance")));
        var whitefish = FishCatalog.Of(ItemId.Whitefish)!;
        Assert.Equal(Verdict.Moratorium, FishRules.Judge(whitefish, 32, 6, Lake("Lake Constance")));
        Assert.Equal(Verdict.Keep, FishRules.Judge(whitefish, 32, 6, Lake("Lake Geneva")));
        Assert.All(new[] { Verdict.Protected, Verdict.ClosedSeason, Verdict.Undersized, Verdict.Moratorium, Verdict.Culled },
            v => Assert.NotEqual("", FishRules.Why(new Catch(whitefish, 20, 0.1f, v))));
    }

    // ---- who bites where ----------------------------------------------------------------------------

    private static Dictionary<ItemId, int> Sample(FishSpot spot, Bait bait, double hour, int n = 3000, int seed = 7)
    {
        var rng = new Random(seed);
        var seen = new Dictionary<ItemId, int>();
        for (int i = 0; i < n; i++)
        {
            var s = FishRules.Pick(spot, bait, hour, rng);
            Assert.NotNull(s);
            var key = s!.Item == ItemId.None ? (ItemId)(-1) : s.Item;
            seen[key] = seen.GetValueOrDefault(key) + 1;
        }
        return seen;
    }

    [Fact]
    public void Each_water_has_its_own_fish()
    {
        var geneva = FishWaters.Spot(true, 2_530_000, 1_140_000, 372, 0);
        Assert.Equal(Basin.Rhone, geneva.Basin);
        var leman = Sample(geneva, Bait.Dough, 12);
        Assert.Contains(ItemId.Whitefish, leman.Keys);
        Assert.DoesNotContain(ItemId.Agone, leman.Keys);       // Ticino only
        Assert.DoesNotContain(ItemId.Namaycush, leman.Keys);   // the Engadin only

        var leman_spinner = Sample(geneva, Bait.Spinner, 12);
        Assert.True(leman_spinner.GetValueOrDefault(ItemId.Perch) > leman_spinner.GetValueOrDefault(ItemId.Roach),
            "a spinner takes perch, not roach");

        var maggiore = Sample(FishWaters.Spot(true, 2_705_000, 1_110_000, 193, 0), Bait.Dough, 12);
        Assert.Contains(ItemId.Agone, maggiore.Keys);
        Assert.DoesNotContain(ItemId.Wels, maggiore.Keys);

        var sils = Sample(FishWaters.Spot(true, 2_776_500, 1_143_500, 1797, 0), Bait.Spinner, 12);
        Assert.Contains(ItemId.Namaycush, sils.Keys);
        Assert.DoesNotContain(ItemId.Pike, sils.Keys);         // too high up
        Assert.DoesNotContain(ItemId.Carp, sils.Keys);

        var brook = Sample(new FishSpot(WaterKind.MountainStream, Basin.Rhine, 1600, null, ""), Bait.Dough, 12);
        Assert.Contains(ItemId.BrownTrout, brook.Keys);
        Assert.DoesNotContain(ItemId.Perch, brook.Keys);
    }

    [Fact]
    public void Night_brings_the_night_fish()
    {
        var neuchatel = FishWaters.Spot(true, 2_554_000, 1_194_000, 429, 0);
        int day = Sample(neuchatel, Bait.Spinner, 13).GetValueOrDefault(ItemId.Wels);
        int night = Sample(neuchatel, Bait.Spinner, 1).GetValueOrDefault(ItemId.Wels);
        // the wels is already a big share of a spinner's catch there: by night it nearly doubles
        Assert.True(night > day * 1.6, $"wels by day {day}, by night {night}");
    }

    [Fact]
    public void Bites_come_sooner_at_dawn_and_with_bait()
    {
        var spot = River;
        Assert.True(FishRules.MeanBiteSeconds(spot, Bait.Dough, 6.5, 0) < FishRules.MeanBiteSeconds(spot, Bait.Dough, 13, 0));
        Assert.True(FishRules.MeanBiteSeconds(spot, Bait.None, 13, 0) > FishRules.MeanBiteSeconds(spot, Bait.Dough, 13, 0));
        var rng = new Random(3);
        double mean = FishRules.MeanBiteSeconds(spot, Bait.Dough, 13, 0);
        for (int i = 0; i < 500; i++)
            Assert.InRange(FishRules.BiteSeconds(spot, Bait.Dough, 13, 0, rng), 3, mean * 4);
    }

    [Fact]
    public void Lengths_stay_in_range_and_some_are_too_small()
    {
        var trout = FishCatalog.Of(ItemId.BrownTrout)!;
        var rng = new Random(11);
        int under = 0;
        for (int i = 0; i < 2000; i++)
        {
            float cm = FishRules.Length(trout, rng);
            Assert.InRange(cm, trout.CmMin * 0.85f - 0.1f, trout.CmMax * 1.15f + 0.1f);
            if (cm < trout.MinCm) under++;
        }
        Assert.InRange(under, 100, 1000);   // undersized trout are part of fishing, not most of it
    }

    // ---- the fight ----------------------------------------------------------------------------------

    private static FishFight Play(float kg, Func<FishFight, bool> reel, int seed = 5, float seconds = 300)
    {
        var f = new FishFight(kg, 20, new Random(seed));
        for (float t = 0; t < seconds && !f.Over; t += 1 / 60f) f.Step(1 / 60f, reel(f));
        return f;
    }

    [Fact]
    public void A_perch_comes_in_on_a_held_reel()
    {
        var f = Play(0.2f, _ => true);
        Assert.True(f.Landed && !f.Snapped);
    }

    [Fact]
    public void A_wels_snaps_a_line_reeled_without_a_break_but_comes_in_when_played()
    {
        var bully = Play(25f, _ => true);
        Assert.True(bully.Snapped, "reeled through every surge, a 25 kg wels breaks the line");
        // played: reel while it is calm, let go before and during each run and when the line strains
        for (int seed = 1; seed <= 5; seed++)
        {
            var played = Play(25f, f => !f.SurgeComing && !f.Surging && f.Tension < 0.8f, seed);
            Assert.True(played.Landed, $"seed {seed}: played well, the wels is landed ({played.Distance:0.0} m, tension {played.Tension:0.00})");
        }
    }

    [Fact]
    public void A_fish_left_to_run_takes_the_line_out()
    {
        var f = Play(8f, _ => false);
        Assert.True(f.Snapped && f.Distance > FishFight.MaxLine - 1, "a big pike never reeled strips the reel");
        var small = Play(0.2f, _ => false, seconds: 30);
        Assert.False(small.Over, "a perch does not run");
    }

    // ---- the kitchen --------------------------------------------------------------------------------

    [Fact]
    public void Every_fish_cooks_and_none_is_crafted()
    {
        foreach (var fish in FishCatalog.Items)
        {
            Assert.Contains(fish, Recipes.NeverCrafted);
            var rows = Recipes.All.Where(r => r.In[0].Id == fish).ToList();
            var cook = Assert.Single(rows);
            Assert.Equal(Station.Fire, cook.Station);
            Assert.True(cook.OnlyWhenHeld);
            Assert.Equal(1, cook.Count);
            Assert.Equal(FishCatalog.ItemOf(FishCatalog.DishOf(fish).Dish), cook.Out);
        }
        Assert.DoesNotContain(Recipes.All, r => Recipes.Outputs(r).Any(o => FishCatalog.Of(o.Id) != null));
        Assert.Equal(Recipes.All.Length, Recipes.All.Select(r => r.Key).Distinct().Count());
    }

    [Fact]
    public void The_rod_and_its_bait_are_made_from_what_is_found()
    {
        var rod = Recipes.All.Single(r => r.Out == ItemId.FishingRod);
        Assert.Equal(Station.Workbench, rod.Station);
        var dough = Recipes.All.Single(r => r.Out == ItemId.DoughBait);
        Assert.Equal(Station.Hands, dough.Station);
        Assert.Equal(4, dough.Count);
        Assert.Equal(new[] { new Ingredient(ItemId.Bread, 1) }, dough.In);
        Assert.Equal(Station.Workbench, Recipes.All.Single(r => r.Out == ItemId.Spinner).Station);
    }
}
