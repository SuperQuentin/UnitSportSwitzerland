using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Navigation;
using UnitSport.Adventure.Places;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Ui;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The travel network's fares (#1226, navigation plan 5.7): tariffs, rounding, cards, the shipped data file.</summary>
public class TravelFareTests
{
    private static readonly TravelRules Rules = TravelRules.Parse(File.ReadAllText(Path.Combine(Root(), "assets", "adventure", "fares.json")));

    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "project.godot"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("no project.godot above the tests");
    }

    [Fact]
    public void The_shipped_file_reads_with_its_card_prices()
    {
        Assert.Equal(1.25, Rules.Detour);
        Assert.Equal(190, TravelRules.CardPrice(Rules.HalfFareCard, new DateTime(2026, 10, 26)));
        Assert.Equal(195, TravelRules.CardPrice(Rules.HalfFareCard, new DateTime(2026, 12, 13)));
        Assert.Equal(3995, TravelRules.CardPrice(Rules.GaCard, new DateTime(2026, 12, 12)));
        Assert.Equal(4095, TravelRules.CardPrice(Rules.GaCard, new DateTime(2027, 1, 1)));
    }

    [Fact]
    public void Short_hops_cost_the_minimum_fare()
    {
        var f = Rules.Price(TravelKind.Rail, 0, TravelKind.Bus, 0, 500, ga: false, halfFare: false);
        Assert.Equal((int)Math.Ceiling(Rules.Rail.MinFare), f.Chf);
    }

    [Fact]
    public void Fares_grow_with_distance_and_less_per_km_far_away()
    {
        double Per(double km) => Rules.Rail.Price(km) / km;
        var near = Rules.Price(TravelKind.Rail, 0, TravelKind.Rail, 0, 20_000, false, false);
        var far = Rules.Price(TravelKind.Rail, 0, TravelKind.Rail, 0, 200_000, false, false);
        Assert.True(far.Chf > near.Chf);
        Assert.True(Per(250) < Per(25));
        // the detour: 100 km straight is 125 tariff km
        Assert.Equal(125, Rules.Price(TravelKind.Rail, 0, TravelKind.Rail, 0, 100_000, false, false).Km, 6);
    }

    [Fact]
    public void Zurich_Bern_is_near_the_published_fare()
    {
        // about 95 km as the crow flies; the SBB's 2nd class full fare is about CHF 56
        var f = Rules.Price(TravelKind.Rail, 0, TravelKind.Rail, 0, 95_500, false, false);
        Assert.InRange(f.Chf, 50, 62);
    }

    [Fact]
    public void Half_fare_halves_and_GA_frees_the_ride_but_halves_the_cable_car()
    {
        var full = Rules.Price(TravelKind.Rail, 0, TravelKind.CableCar, 42, 30_000, false, false);
        var half = Rules.Price(TravelKind.Rail, 0, TravelKind.CableCar, 42, 30_000, false, true);
        var ga = Rules.Price(TravelKind.Rail, 0, TravelKind.CableCar, 42, 30_000, true, false);
        double ride = Rules.Round(Rules.Rail.Price(37.5)), lift = Rules.CableCarDefault;
        Assert.Equal((int)Math.Ceiling(ride + lift - 1e-9), full.Chf);
        Assert.Equal((int)Math.Ceiling(Rules.Round(ride / 2) + Rules.Round(lift / 2) - 1e-9), half.Chf);
        Assert.Equal((int)Math.Ceiling(Rules.Round(lift / 2) - 1e-9), ga.Chf);
        Assert.Equal(0, Rules.Price(TravelKind.Bus, 0, TravelKind.Ship, 0, 30_000, true, true).Chf);
    }

    [Fact]
    public void Ships_use_the_lake_tariff_and_one_line_costs_its_price()
    {
        var ship = Rules.Price(TravelKind.Ship, 0, TravelKind.Ship, 0, 10_000, false, false);
        var rail = Rules.Price(TravelKind.Rail, 0, TravelKind.Rail, 0, 10_000, false, false);
        Assert.Equal("ship", ship.Basis);
        Assert.True(ship.Chf > rail.Chf);
        var sameLine = Rules.Price(TravelKind.CableCar, 7, TravelKind.CableCar, 7, 3_000, false, false);
        Assert.Equal((int)Rules.CableCarDefault, sameLine.Chf);
    }

    [Fact]
    public void Parsing_keeps_defaults_and_reads_lines_whatever_the_locale()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-CH");
            var r = TravelRules.Parse("{ \"detour\": 1.5, \"cableCar\": { \"default\": 20, \"lines\": { \"12345\": { \"name\": \"Test\", \"price\": 44.5 } } } }");
            Assert.Equal(1.5, r.Detour);
            Assert.Equal(44.5, r.CableCarPrice(12345));
            Assert.Equal(20, r.CableCarPrice(1));
            Assert.Equal(TravelRules.Default.Rail.MinFare, r.Rail.MinFare);
        }
        finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void Rounding_goes_up_to_twenty_centimes() => Assert.Equal(3.2, Rules.Round(3.01), 6);
}

/// <summary>The travel network's rules (#1226): kinds, radii, the two-hit unlock, every refusal, each mode.</summary>
public class TravelNetworkTests
{
    private static readonly PoiCatalogue Cat = PoiCatalogue.Fixture();
    private static Poi Of(PoiKind k) => Cat.OfKind(k)[0];
    private static readonly TravelRules Rules = TravelRules.Default;

    [Theory]
    [InlineData(PoiKind.RailStation, TravelKind.Rail, 60)]
    [InlineData(PoiKind.BusStop, TravelKind.Bus, 30)]
    [InlineData(PoiKind.ShipLanding, TravelKind.Ship, 40)]
    [InlineData(PoiKind.CableCarStation, TravelKind.CableCar, 60)]
    [InlineData(PoiKind.Peak, TravelKind.None, 0)]
    public void Kinds_and_radii(PoiKind poi, TravelKind kind, float radius)
    {
        Assert.Equal(kind, TravelNetwork.KindOf(poi));
        Assert.Equal(radius, TravelNetwork.UnlockRadius(kind));
    }

    [Fact]
    public void Two_ticks_in_range_unlock_one_does_not()
    {
        var book = new TravelBook();
        var t = new UnlockTracker();
        var stop = Of(PoiKind.BusStop);
        var near = new List<Poi>();
        Cat.Near(stop.E + 10, stop.N, 80, near);
        Assert.Null(TravelNetwork.CheckUnlock(book, stop.E + 10, stop.N, near, t));
        Assert.Equal(stop, TravelNetwork.CheckUnlock(book, stop.E + 10, stop.N, near, t));
        // a pass straight through (one tick in range, then out) does not
        var t2 = new UnlockTracker();
        Assert.Null(TravelNetwork.CheckUnlock(book, stop.E + 10, stop.N, near, t2));
        Assert.Null(TravelNetwork.CheckUnlock(book, stop.E + 500, stop.N, new List<Poi>(), t2));
        Assert.Null(TravelNetwork.CheckUnlock(book, stop.E + 10, stop.N, near, t2));
        // out of the bus stop's 30 m
        var t3 = new UnlockTracker();
        TravelNetwork.CheckUnlock(book, stop.E + 35, stop.N, near, t3);
        Assert.Null(TravelNetwork.CheckUnlock(book, stop.E + 35, stop.N, near, t3));
        // already held: nothing
        book.Unlocked.Add(stop.Id);
        var t4 = new UnlockTracker();
        TravelNetwork.CheckUnlock(book, stop.E, stop.N, near, t4);
        Assert.Null(TravelNetwork.CheckUnlock(book, stop.E, stop.N, near, t4));
    }

    private static TravelAsk Ask(TravelBook book, Poi? to, Poi? sign, TravelMode mode = TravelMode.Network, bool admin = false,
        bool active = true, bool match = false, bool down = false, bool driving = false, bool racing = false, long now = 1_000_000, long balance = 1000)
    {
        double e = sign?.E ?? 0, n = sign?.N ?? 0;
        return new TravelAsk(mode, admin, active, match, down, driving, racing, book, to, sign, e, n, now, balance);
    }

    [Fact]
    public void Network_travels_from_an_unlocked_sign_to_an_unlocked_point_for_the_fare()
    {
        var station = Of(PoiKind.RailStation);
        var stop = Of(PoiKind.BusStop);
        var book = new TravelBook { Unlocked = { station.Id, stop.Id } };
        var (why, fare) = TravelNetwork.MayTravel(Ask(book, stop, station), Rules);
        Assert.Equal(TravelRefusal.None, why);
        Assert.True(fare.Chf >= (int)Rules.Rail.MinFare);
    }

    [Fact]
    public void Every_refusal()
    {
        var station = Of(PoiKind.RailStation);
        var stop = Of(PoiKind.BusStop);
        var cable = Of(PoiKind.CableCarStation);
        var peak = Of(PoiKind.Peak);
        var book = new TravelBook { Unlocked = { station.Id, stop.Id } };
        TravelRefusal Why(TravelAsk a) => TravelNetwork.MayTravel(a, Rules).Why;
        Assert.Equal(TravelRefusal.Off, Why(Ask(book, stop, station, TravelMode.Off)));
        Assert.Equal(TravelRefusal.NoAdventure, Why(Ask(book, stop, station, active: false)));
        Assert.Equal(TravelRefusal.InMatch, Why(Ask(book, stop, station, match: true)));
        Assert.Equal(TravelRefusal.Down, Why(Ask(book, stop, station, down: true)));
        Assert.Equal(TravelRefusal.Driving, Why(Ask(book, stop, station, driving: true)));
        Assert.Equal(TravelRefusal.Racing, Why(Ask(book, stop, station, racing: true)));
        Assert.Equal(TravelRefusal.NotAPoint, Why(Ask(book, peak, station)));
        Assert.Equal(TravelRefusal.NotAPoint, Why(Ask(book, null, station)));
        Assert.Equal(TravelRefusal.Locked, Why(Ask(book, cable, station)));
        Assert.Equal(TravelRefusal.NotAtSign, Why(Ask(book, stop, null)));
        Assert.Equal(TravelRefusal.NotAtSign, Why(Ask(book, station, cable)));
        Assert.Equal(TravelRefusal.SamePoint, Why(Ask(book, station, station)));
        book.LastTravel = 1_000_000 - 5;
        Assert.Equal(TravelRefusal.Cooldown, Why(Ask(book, stop, station)));
        book.LastTravel = 0;
        Assert.Equal(TravelRefusal.CantAfford, Why(Ask(book, stop, station, balance: 0)));
        // a sign too far from the body
        var far = new TravelAsk(TravelMode.Network, false, true, false, false, false, false, book, stop, station, station.E + 50, station.N, 1_000_000, 1000);
        Assert.Equal(TravelRefusal.NotAtSign, Why(far));
    }

    [Fact]
    public void The_GA_travels_from_anywhere_free_and_Free_mode_and_admins_pay_nothing()
    {
        var station = Of(PoiKind.RailStation);
        var stop = Of(PoiKind.BusStop);
        var book = new TravelBook { Unlocked = { stop.Id }, HasGa = true };
        var (why, fare) = TravelNetwork.MayTravel(Ask(book, stop, null), Rules);
        Assert.Equal(TravelRefusal.None, why);
        Assert.Equal(0, fare.Chf);
        var plain = new TravelBook { Unlocked = { stop.Id } };
        Assert.Equal((TravelRefusal.None, 0), Pick(TravelNetwork.MayTravel(Ask(plain, stop, null, TravelMode.Free), Rules)));
        // an admin: locked points too, from anywhere, nothing to pay, even Off
        var admin = TravelNetwork.MayTravel(Ask(new TravelBook(), station, null, TravelMode.Off, admin: true, balance: 0), Rules);
        Assert.Equal((TravelRefusal.None, 0), Pick(admin));
        static (TravelRefusal, int) Pick((TravelRefusal Why, Fare Fare) r) => (r.Why, r.Fare.Chf);
    }

    [Theory]
    [InlineData(RideKind.OnFoot, true)]
    [InlineData(RideKind.Skis, true)]
    [InlineData(RideKind.Paraglider, true)]
    [InlineData(RideKind.RoadBike, true)]
    [InlineData(RideKind.EBike, true)]
    [InlineData(RideKind.Helicopter, false)]
    [InlineData(RideKind.Speedboat, false)]
    [InlineData((RideKind)8, false)]
    public void Who_may_travel_as_they_are(RideKind ride, bool may) => Assert.Equal(may, TravelNetwork.MayTravelAs((int)ride));

    [Fact]
    public void Aboard_someone_elses_vehicle_is_driving() => Assert.False(TravelNetwork.MayTravelAs(PlacesRules.Aboard));

    [Fact]
    public void Clusters_show_a_station_before_a_village_stop_before_a_stop()
    {
        var station = Of(PoiKind.RailStation);
        var stop = Of(PoiKind.BusStop);
        var landing = Of(PoiKind.ShipLanding);
        var into = new List<(Poi Shown, int Count)>();
        var cells = new Dictionary<(int, int), int>();
        // everything in one cell
        TravelNetwork.Cluster(new[] { stop, station, landing }, _ => (5f, 5f), 30, into, cells);
        Assert.Single(into);
        Assert.Equal((station, 3), into[0]);
        // two cells
        TravelNetwork.Cluster(new[] { stop, landing }, p => p == stop ? (5f, 5f) : (100f, 5f), 30, into, cells);
        Assert.Equal(2, into.Count);
        Assert.Contains((stop, 1), into);
        Assert.Equal(3, TravelNetwork.Rank(stop));   // the fixture's stop is its village's (VillageStop)
    }

    [Fact]
    public void Tags_are_the_events_kinds()
    {
        Assert.Equal("station", TravelNetwork.Tag(TravelKind.Rail));
        Assert.Equal("busstop", TravelNetwork.Tag(TravelKind.Bus));
        Assert.Equal("landing", TravelNetwork.Tag(TravelKind.Ship));
        Assert.Equal("cablecar", TravelNetwork.Tag(TravelKind.CableCar));
    }
}

/// <summary>The travel and pins sections (#1226): limits, ids, notes, sharing, a round trip through the progress store.</summary>
public class PinBookTests
{
    [Fact]
    public void Thirty_pins_at_most_ids_never_reused()
    {
        var b = new PinBook();
        for (int i = 0; i < PinBook.Max; i++) Assert.NotNull(b.Add(i, i, 0, null, false, 0, out _));
        Assert.Null(b.Add(1, 1, 0, null, false, 0, out string? why));
        Assert.Contains("30", why);
        Assert.True(b.Remove(30));
        var again = b.Add(5, 5, 1, "x", false, 0, out _);
        Assert.Equal(31, again!.Id);
        Assert.False(b.Remove(30));
    }

    [Fact]
    public void Notes_are_trimmed_cut_and_cleaned()
    {
        Assert.Equal("Picnic spot", PinBook.Sanitize("  Picnic\u0007 spot \n"));
        Assert.Equal(PinBook.NoteMax, PinBook.Sanitize(new string('a', 40)).Length);
        Assert.Equal("", PinBook.Sanitize(null));
    }

    [Fact]
    public void Bad_icons_are_refused_and_edits_apply()
    {
        var b = new PinBook();
        Assert.Null(b.Add(0, 0, PinBook.Icons, "", false, 0, out _));
        var p = b.Add(0, 0, 2, "a", false, 0, out _)!;
        Assert.True(b.Edit(p.Id, 4, "b", true));
        Assert.False(b.Edit(p.Id, 9, "c", true));
        Assert.False(b.Edit(99, 1, "c", true));
        var shared = new List<Pin>();
        b.Add(1, 1, 0, "private", false, 0, out _);
        b.SharedPins(shared);
        Assert.Single(shared);
        Assert.Equal("b", shared[0].Note);
    }

    [Fact]
    public void Both_sections_round_trip_through_the_progress_file()
    {
        var p = new PlayerProgress("k");
        var travel = p.Get(TravelBook.Section);
        travel.Unlocked.Add("station:x.1234567890");
        travel.HasGa = true;
        travel.Trips = 3;
        travel.Flag = new PortableFlag(2600000, 1200000, 500, 7);
        p.Touch(TravelBook.Section);
        p.Get(PinBook.Section).Add(1, 2, 3, "note", true, 9, out _);
        p.Touch(PinBook.Section);
        var back = AdventureBook.FromJson(AdventureBook.ToNode(p).ToJsonString());
        var t = back.Get(TravelBook.Section);
        Assert.True(t.Has("station:x.1234567890") && t.HasGa && !t.HasHalfFare && t.Trips == 3);
        Assert.Equal(2600000, t.Flag!.E);
        var pins = back.Get(PinBook.Section);
        Assert.Equal(2, pins.NextId);
        Assert.Equal("note", pins.Pins[0].Note);
        Assert.True(pins.Pins[0].Shared);
    }
}

/// <summary>The compass strip's and the marker's maths, and the HUD's numbers (#1226).</summary>
public class CompassMathTests
{
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 90)]
    [InlineData(0, -1, 180)]
    [InlineData(-1, 0, 270)]
    [InlineData(-1, 1, 315)]
    public void Bearings(float e, float n, float deg) => Assert.Equal(deg, CompassMath.Bearing(new Vector2(e, n)), 3);

    [Fact]
    public void Relative_bearings_wrap_round_north()
    {
        Assert.Equal(20, CompassMath.Relative(10, 350), 3);
        Assert.Equal(-20, CompassMath.Relative(350, 10), 3);
        Assert.Equal(-180, CompassMath.Relative(180, 0), 3);
    }

    [Fact]
    public void Markers_land_on_the_strip_and_pin_behind()
    {
        var (mid, pinned) = CompassMath.Place(0, 560, 80);
        Assert.Equal(280, mid, 3);
        Assert.False(pinned);
        var (right, p2) = CompassMath.Place(40, 560, 80);
        Assert.Equal(420, right, 3);
        Assert.False(p2);
        var (edge, p3) = CompassMath.Place(150, 560, 80);
        Assert.Equal(552, edge, 3);
        Assert.True(p3);
        Assert.Equal(8, CompassMath.Place(-170, 560, 80).X, 3);
    }

    [Fact]
    public void Declutter_drops_crowded_labels_and_draws_eight_at_most()
    {
        Span<float> xs = stackalloc float[] { 100, 105, 200, 300, 301, 400, 410, 420, 430, 440 };
        Span<bool> drawn = stackalloc bool[10], labelled = stackalloc bool[10];
        CompassMath.Declutter(xs, drawn, labelled);
        Assert.True(drawn[0] && labelled[0]);
        Assert.True(drawn[1] && !labelled[1]);
        Assert.True(labelled[2] && labelled[3] && !labelled[4]);
        Assert.False(drawn[8] || drawn[9]);
    }

    [Fact]
    public void Off_screen_markers_sit_on_the_inset_ellipse()
    {
        var screen = new Vector2(1920, 1080);
        var right = NavMath.EdgePoint(screen, new Vector2(1, 0));
        Assert.Equal(1920 - 48, right.X, 2);
        Assert.Equal(540, right.Y, 2);
        var down = NavMath.EdgePoint(screen, new Vector2(0, 5));
        Assert.Equal(1080 - 48, down.Y, 2);
        var diag = NavMath.EdgePoint(screen, new Vector2(1, 1));
        var u = (diag - screen / 2) / new Vector2(960 - 48, 540 - 48);
        Assert.Equal(1, u.Length(), 3);
    }

    [Fact]
    public void Behind_the_camera_the_direction_flips()
    {
        var screen = new Vector2(800, 600);
        var d = NavMath.OffscreenDir(new Vector2(500, 300), screen, behind: true);
        Assert.True(d.X < 0);
        Assert.Equal(Vector2.Down, NavMath.OffscreenDir(screen / 2, screen, behind: true));
        Assert.True(NavMath.Size(5000) > NavMath.Size(10));
        // straight behind: down; behind and to the right: down and right
        Assert.Equal(Vector2.Down, NavMath.BehindDir(0, 50));
        var br = NavMath.BehindDir(30, 50);
        Assert.True(br.X > 0 && br.Y > 0);
    }
}

/// <summary>Distances and climbs as the HUD writes them, whatever the locale (#1226).</summary>
public class NavFormatTests
{
    [Theory]
    [InlineData(4, "0 m")]
    [InlineData(846, "850 m")]
    [InlineData(3420, "3.4 km")]
    [InlineData(26_600, "27 km")]
    public void Distances(double m, string text) => Assert.Equal(text, NavFormat.Distance(m));

    [Fact]
    public void Climbs_only_above_thirty_metres_and_invariant_under_fr_CH()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-CH");
            Assert.Equal("", NavFormat.Climb(25));
            Assert.Equal("▲1,210 m", NavFormat.Climb(1208));
            Assert.Equal("▼80 m", NavFormat.Climb(-81));
            Assert.Equal("3.4 km", NavFormat.Distance(3420));
            Assert.Equal("Piz Palü · 3.4 km ▲1,210 m", NavFormat.Label("Piz Palü", 3420, 1208, 0));
            Assert.Equal("Area · in the area", NavFormat.Label("Area", 20, 0, 50));
        }
        finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void A_label_changes_only_on_its_step()
    {
        Assert.Equal(NavFormat.Step(841), NavFormat.Step(844));
        Assert.NotEqual(NavFormat.Step(841), NavFormat.Step(856));
        Assert.Equal(NavFormat.Step(3410), NavFormat.Step(3440));
        Assert.NotEqual(NavFormat.Step(999), NavFormat.Step(1001));
    }
}
