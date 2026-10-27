using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using UnitSport.Adventure;
using UnitSport.Adventure.Places;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The travel rule (#1224, <c>docs/plans/adventure-overview.md</c> "Decisions taken").</summary>
public class PlacesRulesTests
{
    [Theory]
    [InlineData(RideKind.OnFoot, true)]
    [InlineData(RideKind.Skis, true)]
    [InlineData(RideKind.Snowboard, true)]
    [InlineData(RideKind.Paraglider, true)]
    [InlineData(RideKind.Wingsuit, true)]
    [InlineData(RideKind.Parachute, true)]
    [InlineData(RideKind.Pigeon, true)]
    [InlineData(RideKind.Helicopter, true)]
    [InlineData(RideKind.Plane, true)]
    [InlineData(RideKind.A320, false)]
    [InlineData(RideKind.Freighter, false)]
    [InlineData(RideKind.An124, false)]
    [InlineData(RideKind.RoadBike, false)]
    [InlineData(RideKind.EBike, false)]
    [InlineData(RideKind.Jetski, false)]
    [InlineData(RideKind.Steamer, false)]
    [InlineData(RideKind.Ufo, false)]
    [InlineData(RideKind.Forklift, false)]
    public void Only_on_foot_or_in_a_light_aircraft_reveals(RideKind kind, bool reveals) =>
        Assert.Equal(reveals, PlacesRules.Reveals((int)kind));

    [Fact]
    public void Every_car_motorbike_truck_and_boat_and_aboard_reveal_nothing()
    {
        for (int k = 8; k <= 120; k++) Assert.False(PlacesRules.Reveals(k), $"ride {k}");
        for (int k = 129; k <= 192; k++) Assert.False(PlacesRules.Reveals(k), $"ride {k}");
        Assert.False(PlacesRules.Reveals(PlacesRules.Aboard));
    }

    [Fact]
    public void Ground_vehicles_and_boats_clear_fog_airliners_do_not()
    {
        for (int k = 0; k <= 120; k++) Assert.True(PlacesRules.ClearsFog(k) || k == (int)RideKind.Trailer, $"ride {k}");
        for (int k = 121; k <= 123; k++) Assert.True(PlacesRules.ClearsFog(k), $"boat {k}");
        for (int k = 129; k <= 192; k++) Assert.True(PlacesRules.ClearsFog(k), $"motorbike {k}");
        Assert.True(PlacesRules.ClearsFog((int)RideKind.EBike));
        Assert.True(PlacesRules.ClearsFog((int)RideKind.Forklift));
        Assert.False(PlacesRules.ClearsFog((int)RideKind.A320));
        Assert.False(PlacesRules.ClearsFog((int)RideKind.Freighter));
        Assert.False(PlacesRules.ClearsFog((int)RideKind.An124));
        Assert.False(PlacesRules.ClearsFog((int)RideKind.Ufo));
        Assert.False(PlacesRules.ClearsFog(PlacesRules.Aboard));
    }

    [Fact]
    public void A_car_clears_fog_but_finds_no_place()
    {
        var t = new DiscoveryTracker();
        var found = new List<Poi>();
        var car = new PlacesBody(2_600_000, 1_200_000, 500, 0.5f, 8);
        Assert.True(DiscoveryRules.Step(t, car, 1, Array.Empty<Poi>(), _ => false, found));
        var airliner = new PlacesBody(2_600_010, 1_200_000, 3_000, 2_500f, (int)RideKind.A320);
        Assert.False(DiscoveryRules.Step(t, airliner, 1, Array.Empty<Poi>(), _ => false, found));
    }

    [Fact]
    public void The_stamp_says_how()
    {
        Assert.Equal(StampMode.OnFoot, PlacesRules.ModeOf((int)RideKind.OnFoot));
        Assert.Equal(StampMode.Snow, PlacesRules.ModeOf((int)RideKind.Skis));
        Assert.Equal(StampMode.Wings, PlacesRules.ModeOf((int)RideKind.Paraglider));
        Assert.Equal(StampMode.Aircraft, PlacesRules.ModeOf((int)RideKind.Helicopter));
        Assert.Equal("on foot", StampDay.ModeText((byte)StampMode.OnFoot));
    }
}

/// <summary>Discovery (#1224, places plan 3): radii, heights, two hits, jumps and arrivals.</summary>
public class DiscoveryRulesTests
{
    private static readonly PoiCatalogue Cat = PoiCatalogue.Fixture();
    private static Poi Of(PoiKind k) => Cat.OfKind(k)[0];
    private const double Ground = PoiCatalogue.FixtureGround;

    private static PlacesBody At(Poi p, double dx = 0, double dy = 0, double alt = double.NaN, RideKind ride = RideKind.OnFoot, float agl = 1) =>
        new(p.E + dx, p.N + dy, double.IsNaN(alt) ? p.Elevation + 1 : alt, agl, (int)ride);

    [Fact]
    public void A_peak_is_found_within_its_radius_and_height_on_foot_only()
    {
        var peak = Of(PoiKind.Peak);
        Assert.True(DiscoveryRules.InRange(peak, At(peak, 30)));
        Assert.False(DiscoveryRules.InRange(peak, At(peak, 45)));
        Assert.False(DiscoveryRules.InRange(peak, At(peak, 0, 0, peak.Elevation + 40)));
        Assert.False(DiscoveryRules.InRange(peak, At(peak, ride: RideKind.Helicopter)));
        Assert.False(DiscoveryRules.InRange(peak, At(peak, ride: RideKind.RoadBike)));
        Assert.True(DiscoveryRules.InRange(peak, At(peak, ride: RideKind.Skis)));
    }

    [Fact]
    public void A_body_on_the_fixture_ground_beside_the_peak_finds_it()
    {
        var peak = Of(PoiKind.Peak);
        var body = new PlacesBody(peak.E, peak.N - 5, Ground, 0, (int)RideKind.OnFoot);
        Assert.Equal(Ground + 10, peak.Elevation);
        Assert.True(DiscoveryRules.InRange(peak, body));
        var t = new DiscoveryTracker();
        var near = new List<Poi>();
        Cat.Near(body.E, body.N, 200, near);
        Assert.Contains(peak, near);
        var got = new List<Poi>();
        DiscoveryRules.Step(t, body, 1, near, _ => false, got);
        DiscoveryRules.Step(t, body, 1, near, _ => false, got);
        Assert.Contains(peak, got);
    }

    [Fact]
    public void A_castle_counts_from_a_light_aircraft_low_enough_but_never_from_a_car()
    {
        var castle = Of(PoiKind.Castle);
        Assert.True(DiscoveryRules.InRange(castle, At(castle, 60, 0, Ground + 100, RideKind.Helicopter, 100)));
        Assert.False(DiscoveryRules.InRange(castle, At(castle, 60, 0, Ground + 300, RideKind.Helicopter, 300)));
        Assert.False(DiscoveryRules.InRange(castle, At(castle, 10, 0, Ground + 1, (RideKind)8)));
        Assert.False(DiscoveryRules.InRange(castle, At(castle) with { Out = true }));
    }

    [Fact]
    public void A_lake_counts_inside_its_outline_and_near_its_shore()
    {
        var lake = Of(PoiKind.Lake);
        Assert.True(DiscoveryRules.InRange(lake, At(lake)));
        // the west shore is 40 m east of the fixture start: the start is within the 60 m buffer
        Assert.True(DiscoveryRules.InRange(lake, new PlacesBody(PoiCatalogue.FixtureE, PoiCatalogue.FixtureN, Ground + 1, 1, 0)));
        Assert.False(DiscoveryRules.InRange(lake, new PlacesBody(PoiCatalogue.FixtureE - 200, PoiCatalogue.FixtureN, Ground + 1, 1, 0)));
    }

    [Fact]
    public void Two_consecutive_hits_discover_and_a_found_place_is_not_found_again()
    {
        var peak = Of(PoiKind.Peak);
        var t = new DiscoveryTracker();
        var near = new List<Poi> { peak };
        var found = new HashSet<Poi>();
        var got = new List<Poi>();
        Assert.True(DiscoveryRules.Step(t, At(peak, 35), 1, near, found.Contains, got));
        Assert.Empty(got);
        DiscoveryRules.Step(t, At(peak, 30), 1, near, found.Contains, got);
        Assert.Equal(new[] { peak }, got);
        found.Add(peak);
        DiscoveryRules.Step(t, At(peak, 25), 1, near, found.Contains, got);
        Assert.Empty(got);
    }

    [Fact]
    public void A_hit_then_a_miss_then_a_hit_is_not_two_in_a_row()
    {
        var peak = Of(PoiKind.Peak);
        var t = new DiscoveryTracker();
        var near = new List<Poi> { peak };
        var got = new List<Poi>();
        DiscoveryRules.Step(t, At(peak, 30), 1, near, _ => false, got);
        DiscoveryRules.Step(t, At(peak, 60), 1, near, _ => false, got);
        DiscoveryRules.Step(t, At(peak, 30), 1, near, _ => false, got);
        Assert.Empty(got);
    }

    [Fact]
    public void After_a_jump_nothing_counts_until_the_body_has_walked_300_m_away()
    {
        var pass = Of(PoiKind.Pass);
        var t = new DiscoveryTracker();
        var near = new List<Poi> { pass };
        var got = new List<Poi>();
        DiscoveryRules.Step(t, At(pass, 2000), 1, near, _ => false, got);
        // a 2 km step in a second on foot: a jump onto the pass
        Assert.False(DiscoveryRules.Step(t, At(pass), 1, near, _ => false, got));
        Assert.False(DiscoveryRules.Step(t, At(pass, 10), 1, near, _ => false, got));
        Assert.Empty(got);
        Assert.Equal(1, t.Jumps);
        // walk away, 30 m a second, past 300 m, and back
        double x = 10;
        while (x < 330) { x += 30; DiscoveryRules.Step(t, At(pass, x), 1, near, _ => false, got); }
        Assert.False(t.Anchored);
        while (x > 0 && got.Count == 0) { x -= 30; DiscoveryRules.Step(t, At(pass, Math.Max(x, 0)), 1, near, _ => false, got); }
        Assert.Equal(new[] { pass }, got);
    }

    [Fact]
    public void A_sanctioned_arrival_lands_like_a_jump()
    {
        var station = Of(PoiKind.RailStation);
        var t = new DiscoveryTracker();
        var near = new List<Poi> { station };
        var got = new List<Poi>();
        DiscoveryRules.Arrived(t, station.E, station.N);
        Assert.False(DiscoveryRules.Step(t, At(station), 1, near, _ => false, got));
        DiscoveryRules.Step(t, At(station, 5), 1, near, _ => false, got);
        Assert.Empty(got);
        Assert.Equal(0, t.Jumps);
    }

    [Fact]
    public void A_car_fast_on_the_road_is_no_jump_but_a_runner_at_that_speed_is()
    {
        Assert.False(DiscoveryRules.IsJump(85, 1, 8));
        Assert.True(DiscoveryRules.IsJump(85, 1, (int)RideKind.OnFoot));
        Assert.False(DiscoveryRules.IsJump(300, 1, (int)RideKind.Plane));
    }

    [Fact]
    public void A_vehicle_finds_no_place_but_clears_fog()
    {
        var peak = Of(PoiKind.Peak);
        var t = new DiscoveryTracker();
        var got = new List<Poi>();
        var near = new List<Poi> { peak };
        // Step's result is "clears fog" (PlacesRules.ClearsFog): true in a car; finding places stays off
        Assert.True(DiscoveryRules.Step(t, At(peak, ride: (RideKind)8), 1, near, _ => false, got));
        Assert.True(DiscoveryRules.Step(t, At(peak, ride: (RideKind)8), 1, near, _ => false, got));
        Assert.Empty(got);
    }
}

/// <summary>The fog of war's mask (#1224, places plan 4).</summary>
public class FogMaskTests
{
    [Fact]
    public void A_reveal_clears_the_cells_whose_centre_is_in_the_disc()
    {
        var fog = new FogMask();
        var changed = new HashSet<int>();
        int added = fog.Reveal(2_600_125, 1_200_125, 300, changed);
        Assert.True(added is > 0 and <= 9 + 4 * 2 + 4);
        Assert.True(fog.Revealed(2_600_125, 1_200_125));
        Assert.True(fog.Revealed(2_600_125 + 250, 1_200_125));
        Assert.False(fog.Revealed(2_600_125 + 500, 1_200_125));
        Assert.NotEmpty(changed);
        Assert.Equal(0, fog.Reveal(2_600_125, 1_200_125, 300));
        Assert.Equal(added, fog.Count);
    }

    [Fact]
    public void The_mask_round_trips_and_deltas_set_tiles()
    {
        var fog = new FogMask();
        fog.Reveal(2_600_000, 1_200_000, 1000);
        fog.Reveal(2_700_000, 1_150_000, 300);
        var back = FogMask.Decode(fog.Encode());
        Assert.Equal(fog.Count, back.Count);
        Assert.Equal(fog.Tiles.OrderBy(t => t.Key), back.Tiles.OrderBy(t => t.Key));
        var mirror = new FogMask();
        foreach (var (tile, mask) in fog.Tiles) mirror.Set(tile, mask);
        Assert.Equal(fog.Count, mirror.Count);
        Assert.Equal("", new FogMask().Encode());
        Assert.Equal(0, FogMask.Decode("").Count);
    }

    [Fact]
    public void The_whole_country_is_under_5_KB()
    {
        var fog = new FogMask();
        for (int t = 0; t < FogMask.TilesE * FogMask.TilesN; t++) fog.Set(t, FogMask.Full);
        Assert.True(fog.ToBytes().Length < 5000, $"{fog.ToBytes().Length} bytes");
        Assert.Equal(FogMask.Width * FogMask.Height, FogMask.FromBytes(fog.ToBytes()).Count);
    }

    [Fact]
    public void Off_the_lattice_is_never_revealed()
    {
        var fog = new FogMask();
        Assert.Equal(0, fog.Reveal(2_000_000, 1_000_000, 300));
        Assert.False(fog.Revealed(2_000_000, 1_000_000));
        Assert.Equal(-1, FogMask.TileIndex(100, 100));
        Assert.Equal((2600, 1200), FogMask.TileOf(FogMask.TileIndex(2600, 1200)));
    }
}

/// <summary>The Atlas's completion, milestones and chapters (#1224, places plan 7.3).</summary>
public class AtlasProgressTests
{
    [Fact]
    public void Landmarks_count_tiers_1_and_2_only()
    {
        var cat = PoiCatalogue.Fixture();
        var p = new PlacesProgress();
        var (found, total) = AtlasProgress.Landmarks(cat, p, "VS");
        Assert.Equal(0, found);
        Assert.Equal(cat.All.Count(x => x.Tier <= 2), total);
        p.Found[cat.OfKind(PoiKind.Chapel)[0].Id] = new Stamp(1, 0);   // tier 3
        Assert.Equal(0, AtlasProgress.Landmarks(cat, p, "VS").Found);
    }

    [Fact]
    public void Each_milestone_is_raised_once_and_the_chapter_at_100()
    {
        var cat = PoiCatalogue.Fixture();
        var p = new PlacesProgress();
        var marks = cat.All.Where(x => x.Tier <= 2).ToList();
        var raised = new List<int>();
        string? chapter = null;
        foreach (var poi in marks)
        {
            p.Found[poi.Id] = new Stamp(1, 0);
            var (steps, ch) = AtlasProgress.Check(cat, p, "VS");
            raised.AddRange(steps);
            chapter ??= ch;
        }
        Assert.Equal(new[] { 25, 50, 75, 100 }, raised);
        Assert.Equal("Alps", chapter);
        Assert.Equal(100, p.Milestones["VS"]);
        var (again, ch2) = AtlasProgress.Check(cat, p, "VS");
        Assert.Empty(again);
        Assert.Null(ch2);
    }

    [Fact]
    public void Chapters_cover_the_26_cantons_once()
    {
        var all = AtlasChapters.All.SelectMany(c => c.Cantons).ToList();
        Assert.Equal(26, all.Count);
        Assert.Equal(26, all.Distinct().Count());
        Assert.Equal("Ticino", AtlasChapters.Of("TI"));
        Assert.Equal("Alps", AtlasChapters.Of("VS"));
    }

    [Fact]
    public void Revealed_land_is_counted_per_canton_and_lakes_are_water()
    {
        var cat = PoiCatalogue.Fixture();
        var fog = new FogMask();
        fog.Reveal(PoiCatalogue.FixtureE - 300, PoiCatalogue.FixtureN, 300);
        var land = AtlasProgress.RevealedLand(fog, cat);
        Assert.True(land.GetValueOrDefault("VS") > 0);
        var water = new FogMask();
        water.Reveal(PoiCatalogue.FixtureE + 1340, PoiCatalogue.FixtureN, 200);   // the lake's middle
        Assert.Equal(0, AtlasProgress.RevealedLand(water, cat).GetValueOrDefault("VS"));
    }
}

/// <summary>The Atlas's generated texts and the curated cards (#1224, places plan 1.4, 7.2).</summary>
public class AtlasFactsTests
{
    [Fact]
    public void Numbers_are_invariant_with_a_thin_space_under_a_French_locale()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-CH");
            Assert.Equal("1 797", AtlasFacts.Num(1797.2));
            Assert.Equal("2.6", AtlasFacts.Dec(2.6));
            var cat = PoiCatalogue.Fixture();
            string text = AtlasFacts.For(cat.OfKind(PoiKind.Peak)[0], cat);
            Assert.Contains("510 m", text);
            Assert.Contains("Valais", text);
            Assert.Contains("A cross stands on top", text);
            Assert.DoesNotContain(",5", AtlasFacts.For(cat.OfKind(PoiKind.Lake)[0], cat));
        }
        finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void A_lake_has_its_area_and_a_station_its_nearest_peak()
    {
        var cat = PoiCatalogue.Fixture();
        // the fixture lake: an ellipse of 1 300 x 1 000 m, ~4 km²
        Assert.Matches("A lake of 4(\\.[0-9])? km²", AtlasFacts.For(cat.OfKind(PoiKind.Lake)[0], cat));
        Assert.Contains("Fixture Peak", AtlasFacts.For(cat.OfKind(PoiKind.RailStation)[0], cat));
        Assert.Equal("3rd", AtlasFacts.Ordinal(3));
        Assert.Equal("11th", AtlasFacts.Ordinal(11));
    }

    [Fact]
    public void Wikidata_facts_are_read_by_tag_and_hash_and_added()
    {
        var cat = PoiCatalogue.Fixture();
        var castle = cat.OfKind(PoiKind.Castle)[0];
        string renamed = $"castle:another-name.{castle.Id[(castle.Id.LastIndexOf('.') + 1)..]}";
        var facts = AtlasFacts.ParseFacts($"{{\"{renamed}\": {{\"qid\": \"Q1\", \"year\": 1200, \"distance\": 3}}, \"bad\": {{}}}}");
        Assert.True(facts.TryGetValue((castle.Kind, castle.Hash), out var f));
        Assert.Contains("It dates from 1200.", AtlasFacts.For(castle, cat, f));
    }

    [Fact]
    public void Card_problems_are_found()
    {
        var ok = new AtlasCard("peak:rigi-kulm", "peak:rigi-kulm.3fa9c01b2e", "Rigi Kulm", "A fact.");
        Assert.Empty(AtlasFacts.Problems(new[] { ok }));
        Assert.NotEmpty(AtlasFacts.Problems(new[] { ok, ok }));
        Assert.NotEmpty(AtlasFacts.Problems(new[] { ok with { Alias = "Peak:Rigi" } }));
        Assert.NotEmpty(AtlasFacts.Problems(new[] { ok with { Alias = "tower:rigi" } }));
        Assert.NotEmpty(AtlasFacts.Problems(new[] { ok with { Id = "peak:rigi" } }));
        Assert.NotEmpty(AtlasFacts.Problems(new[] { ok with { Fact = "" } }));
    }

    /// <summary>The shipped cards: every alias unique and well formed, every id canonical and of the alias's kind.</summary>
    [Fact]
    public void The_shipped_cards_are_well_formed()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "project.godot"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        string path = Path.Combine(dir!, "assets", "adventure", "atlas_cards.json");
        Assert.True(File.Exists(path), path);
        var cards = AtlasFacts.ParseCards(File.ReadAllText(path));
        Assert.True(cards.Count >= 20, $"{cards.Count} cards");
        Assert.Empty(AtlasFacts.Problems(cards));
    }
}
