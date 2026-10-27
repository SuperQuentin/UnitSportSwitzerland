using System.Text.Json.Nodes;
using UnitSport.Adventure;
using UnitSport.Adventure.Versteckis;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Versteckis (#1268, src/Adventure/Versteckis, linked in): the rules (phases, rotation, points, the tag, the server's
/// checks, the payout), the area, the hide plan, the bots' tactics and the progress section.
/// </summary>
public class VersteckisTests
{
    // ---- VsRules ------------------------------------------------------------------------------

    [Fact]
    public void Phase_lengths_follow_the_head_count()
    {
        Assert.Equal(35, VsRules.HideSeconds(4));
        Assert.Equal(45, VsRules.HideSeconds(5));
        Assert.Equal(225, VsRules.SeekSeconds(3));
        Assert.Equal(300, VsRules.SeekSeconds(7));
        Assert.Equal(150, VsRules.SeekSeconds(0));
        Assert.Equal(3, VsRules.RoundsFor(1));
        Assert.Equal(5, VsRules.RoundsFor(5));
        Assert.Equal(6, VsRules.RoundsFor(8));
        Assert.Equal(2, VsRules.RoundsFor(4, 1));
        Assert.Equal(8, VsRules.RoundsFor(4, 12));
    }

    [Fact]
    public void Each_human_seeks_once_in_join_order_before_bots_take_the_rest()
    {
        Assert.Equal(new long[] { 10, 20, 30, -1001, -1002 }, VsRules.SeekerOrder([10, 20, 30], [-1001, -1002], 5));
        // one human: it seeks round 1, bots the others
        Assert.Equal(new long[] { 10, -1001, -1002, -1001 }, VsRules.SeekerOrder([10], [-1001, -1002], 4));
        // no bots: the humans again, in order
        Assert.Equal(new long[] { 10, 20, 10 }, VsRules.SeekerOrder([10, 20], [], 3));
        // more humans than rounds: the first ones seek
        Assert.Equal(new long[] { 1, 2 }, VsRules.SeekerOrder([1, 2, 3], [], 2));
    }

    [Fact]
    public void The_radius_grows_with_the_players_within_its_bounds()
    {
        Assert.Equal(160f, VsRules.Radius(4));
        Assert.Equal(220f, VsRules.Radius(8));
        Assert.Equal(130f, VsRules.Radius(2));
        Assert.Equal(220f, VsRules.Radius(20));
    }

    [Fact]
    public void Points_caps_and_ties()
    {
        Assert.Equal(0, VsRules.HiddenPoints(29));
        Assert.Equal(4, VsRules.HiddenPoints(125));
        Assert.Equal(2 + 3, VsRules.AllCaughtPoints(61));
        Assert.Equal(2, VsRules.AllCaughtPoints(-5));
        Assert.True(VsRules.MooScores(4, 60));
        Assert.False(VsRules.MooScores(5, 60));          // five a round at most
        Assert.False(VsRules.MooScores(0, 19));          // never in the last 20 s
        Assert.Equal((3, 1), VsRules.ObjectivePoints(VsSite.Bell));
        Assert.Equal((4, 1), VsRules.ObjectivePoints(VsSite.Can));
        var ties = VsRules.Winners([new VsEntrant { Peer = 1, Score = 9 }, new VsEntrant { Peer = 2, Score = 9 }, new VsEntrant { Peer = 3, Score = 4 }]);
        Assert.Equal(new long[] { 1, 2 }, ties);
    }

    [Fact]
    public void A_tag_catches_inside_the_turned_footprint_only()
    {
        // a bench (1.6 wide, 0.6 deep) facing north at the origin
        Assert.True(VsRules.TagHits(VsProp.Bench, 0, 0, 0f, 0.9, 0));        // 0.8 + 0.3 slack across
        Assert.False(VsRules.TagHits(VsProp.Bench, 0, 0, 0f, 0, 0.7));       // 0.3 + 0.3 along: out at 0.7
        // turned a quarter (facing west): the width now runs north-south
        float west = MathF.PI / 2;
        Assert.True(VsRules.TagHits(VsProp.Bench, 0, 0, west, 0, 0.9));
        Assert.False(VsRules.TagHits(VsProp.Bench, 0, 0, west, 0.7, 0));
        // a cow is long along its facing
        Assert.True(VsRules.TagHits(VsProp.Cow, 100, 200, 0f, 100, 201.3));
        Assert.False(VsRules.TagHits(VsProp.Cow, 100, 200, 0f, 100.8, 200));
        // reach: 2.5 m and the aim's slack, never 5 m
        Assert.True(VsRules.InReach(0, 0, 2.9, 0));
        Assert.False(VsRules.InReach(0, 0, 5, 0));
    }

    [Fact]
    public void The_server_checks_speed_bounds_and_ground()
    {
        Assert.True(VsRules.SpeedOk(0, 0, 0.8, 0, 0.25, VsProps.WalkCap, false));     // 3.2 m/s < 2.5 × 1.3
        Assert.False(VsRules.SpeedOk(0, 0, 2.0, 0, 0.25, VsProps.WalkCap, false));    // 8 m/s
        Assert.True(VsRules.SpeedOk(0, 0, 2.0, 0, 0.25, VsProps.WalkCap, true));      // a sprint's burst
        Assert.False(VsRules.SpeedOk(0, 0, 1.0, 0, 0.25, VsProps.CowWalk, false));    // a cow is slower
        Assert.True(VsRules.SpeedOk(0, 0, 0.5, 0, 0.01, VsProps.WalkCap, false));     // jitter
        Assert.True(VsRules.Inside(0, 0, 100, 60, 79));
        Assert.False(VsRules.Inside(0, 0, 100, 80, 80));
        Assert.Equal(10, VsRules.ToLine(0, 0, 100, 90, 0), 6);
        Assert.True(VsRules.GroundOk(501.5f, 500f));
        Assert.False(VsRules.GroundOk(510f, 500f));
        Assert.True(VsRules.GroundOk(510f, null));
    }

    [Fact]
    public void Every_human_who_played_is_paid_the_same_way()
    {
        // two friends, three rounds each, neither on top: the same francs, host or not
        Assert.Equal(VsRules.Payout(3, false, 0), VsRules.Payout(3, false, 2));
        Assert.Equal(38, VsRules.Payout(3, false, 0));
        Assert.Equal(48, VsRules.Payout(3, true, 0));    // the top scorer's small bonus
        Assert.Equal(0, VsRules.Payout(3, false, 5));    // five paid games a day
        Assert.Equal(0, VsRules.Payout(0, true, 0));     // never played a round
    }

    [Fact]
    public void The_item_ids_are_in_the_plans_block()
    {
        foreach (var id in new[] { ItemId.VsTag, ItemId.VsSniff, ItemId.VsDecoy, ItemId.VsSprint, ItemId.VsMoo, ItemId.VsDisguise, ItemId.Zipfelmuetze, ItemId.Treichel })
            Assert.InRange((int)id, 460, 479);
        Assert.Equal(8, Enum.GetValues<ItemId>().Count(i => (int)i is >= 460 and <= 479));
    }

    [Fact]
    public void The_state_round_trips_as_json()
    {
        var s = new VsState { Id = 3, Place = "Ernen", Canton = "VS", E = 2652000.5, N = 1138000.25, Radius = 160, Phase = VsPhase.Seek, Round = 2, Rounds = 4 };
        s.Entrants.Add(new VsEntrant { Peer = 7, Name = "Lea", Prop = VsProp.MilkCan, Score = 5, SurvivedAs = [0, 0, 2, 0, 0, 0, 0] });
        s.Entrants.Add(new VsEntrant { Peer = -1001, Name = "Bot", Bot = true, Role = VsRole.Seeker });
        s.Objectives.Add(new VsObjective { Site = VsSite.Fire, E = 1, N = 2, Done = true, By = 7 });
        var back = VsState.FromJson(s.ToJson())!;
        Assert.Equal(VsPhase.Seek, back.Phase);
        Assert.Equal(VsProp.MilkCan, back.Find(7)!.Prop);
        Assert.Equal(2, back.Find(7)!.SurvivedAs[2]);
        Assert.True(back.Find(-1001)!.Bot);
        Assert.Equal(2652000.5, back.E);
        Assert.True(back.Objectives[0].Done);
        Assert.Single(VsState.FromListJson(VsState.ListJson([s])));
        Assert.Null(VsState.FromJson("{nope"));
    }

    // ---- VsArea -------------------------------------------------------------------------------

    private static readonly VsTown[] Towns =
    [
        new("Ernen", 2652000, 1138000, 120),
        new("Zürich", 2683000, 1248000, 40000),     // a city: never
        new("Weiler", 2600000, 1200000, 6),         // too small
    ];

    [Fact]
    public void Candidates_are_churches_and_chapels_near_villages()
    {
        var pois = new (string, string, PoiKind, string, double, double)[]
        {
            ("church:ernen", "Kirche", PoiKind.Church, "VS", 2652100, 1138050),
            ("chapel:far", "Kapelle", PoiKind.Chapel, "VS", 2652000, 1139000),     // 1 km out
            ("church:zh", "Grossmünster", PoiKind.Church, "ZH", 2683100, 1248000),  // in a city
            ("peak:x", "Peak", PoiKind.Peak, "VS", 2652050, 1138000),
            ("chapel:weiler", "Kapelle", PoiKind.Chapel, "BE", 2600010, 1200000),
        };
        var c = VsArea.Candidates(pois, Towns);
        Assert.Single(c);
        Assert.Equal("Ernen", c[0].Name);
        Assert.True(c[0].Church);
        Assert.Equal("church:ernen", VsArea.Nearest(c, 2652000, 1137000)[0].Id);
        Assert.Empty(VsArea.Nearest(c, 2600000, 1100000));
        Assert.Equal("Ernen", VsArea.Named(c, "ern", 0, 0)!.Name);
        // here: snaps to the candidate within 1 km, else the point itself
        Assert.Equal("church:ernen", VsArea.Here(c, Towns, 2652500, 1138200).Id);
        var farm = VsArea.Here(c, Towns, 2655000, 1138000);
        Assert.StartsWith("here:", farm.Id);
        Assert.Equal(2655000, farm.E);
    }

    [Fact]
    public void Random_skips_the_recent_centres_and_the_history_keeps_five()
    {
        var c = new List<VsCandidate> { new("a", "A", "VS", 0, 0, true), new("b", "B", "VS", 1000, 0, true) };
        for (int seed = 0; seed < 20; seed++)
            Assert.Equal("b", VsArea.Random(c, new Random(seed), [(0, 0)])!.Id);
        var h = new List<(double, double)>();
        for (int i = 0; i < 8; i++) h = VsArea.Remember(h, i, i);
        Assert.Equal(5, h.Count);
        Assert.Equal((3.0, 3.0), h[0]);
        Assert.True(VsArea.FarFromOthers(0, 0, [(700, 0)]));
        Assert.False(VsArea.FarFromOthers(0, 0, [(500, 0)]));
    }

    [Fact]
    public void An_area_is_rejected_for_each_rule()
    {
        Assert.Null(VsArea.Reject(new VsAreaFacts(20, 120, 0.05f, 0.1f)));
        Assert.Contains("buildings", VsArea.Reject(new VsAreaFacts(5, 120, 0, 0)));
        Assert.Contains("hide", VsArea.Reject(new VsAreaFacts(20, 30, 0, 0)));
        Assert.Contains("water", VsArea.Reject(new VsAreaFacts(20, 120, 0.3f, 0)));
        Assert.Contains("steep", VsArea.Reject(new VsAreaFacts(20, 120, 0, 0.4f)));
    }

    // ---- HidePlan -----------------------------------------------------------------------------

    /// <summary>A hamlet: houses and barns on a grid each side of an east-west road, a church at the centre.</summary>
    private static (List<VsBuilding> B, List<(double, double)[]> Roads) Hamlet()
    {
        var b = new List<VsBuilding>();
        for (int i = 0; i < 6; i++)
        {
            double x = -100 + i * 40;
            // doors face the road (south side faces north, north side faces south)
            b.Add(new VsBuilding(x, 22, 1, 0, 12, 9, i % 3 == 0 ? BuildingKind.Agricultural : BuildingKind.House, x, 17.5, 0, -1));
            b.Add(new VsBuilding(x + 20, -22, 1, 0, 11, 8, i % 3 == 1 ? BuildingKind.Agricultural : BuildingKind.House, x + 20, -18, 0, 1));
        }
        b.Add(new VsBuilding(0, 60, 1, 0, 14, 22, BuildingKind.Sacral, 0, 49, 0, -1));
        var roads = new List<(double, double)[]> { new[] { (-200.0, 0.0), (200.0, 0.0) }, new[] { (145.0, -150.0), (145.0, 150.0) } };
        return (b, roads);
    }

    private static List<VsDecor> Plan(int seed = 42)
    {
        var (b, roads) = Hamlet();
        return HidePlan.For(0, 0, 160, seed, b, roads, (e, n) => Math.Abs(n) > 40 && !(Math.Abs(e) < 20 && n > 40),
            [(-120.0, -100.0), (-125.0, -95.0), (0.0, -500.0)], (0, 49));
    }

    [Fact]
    public void The_plan_is_the_same_from_the_same_inputs_and_moves_with_the_seed()
    {
        var a = Plan();
        Assert.Equal(a, Plan());
        Assert.NotEqual(a, Plan(43));
    }

    [Fact]
    public void Each_kind_stands_where_its_rule_says_and_never_on_a_road_or_in_a_building()
    {
        var (b, roads) = Hamlet();
        var plan = Plan();
        Assert.InRange(plan.Count, 30, HidePlan.MaxDecor);
        foreach (var d in plan)
        {
            Assert.True(VsRules.Inside(0, 0, 160, d.E, d.N), $"{d} outside");
            if (d.Kind == VsProp.Cow) continue;   // the herd's cows are where the herd is
            Assert.DoesNotContain(b, x => x.Contains(d.E, d.N, 0.1f));
            // a prop against a wall may stand by a pavement, never on the carriageway
            Assert.True(HidePlan.RoadDistance(roads, d.E, d.N) >= 0.79, $"{d} on a road");
            if (d.Kind is VsProp.Bench) Assert.True(HidePlan.RoadDistance(roads, d.E, d.N) >= 3.2, $"{d}: a bench on the road");
        }
        foreach (var d in plan.Where(d => d.Kind == VsProp.HayBale))
            Assert.True(HidePlan.RoadDistance(roads, d.E, d.N) >= HidePlan.HayRoadClear - 0.01, "a bale near a road");
        foreach (var d in plan.Where(d => d.Kind == VsProp.MilkCan))
            Assert.Contains(b, x => x.Kind == BuildingKind.Agricultural && Math.Abs(x.DoorE - d.E) < 3 && Math.Abs(x.DoorN - d.N) < 3);
        foreach (var d in plan.Where(d => d.Kind == VsProp.Gnome))
            Assert.Contains(b, x => x.Kind == BuildingKind.House && Math.Sqrt((x.DoorE - d.E) * (x.DoorE - d.E) + (x.DoorN - d.N) * (x.DoorN - d.N)) <= 6.5);
        foreach (var d in plan.Where(d => d.Kind == VsProp.Woodpile))
            Assert.Contains(b, x => x.Kind is BuildingKind.House or BuildingKind.Agricultural && x.Contains(d.E, d.N, 1.2f));
        // two cows inside the circle, the far one left out
        Assert.Equal(2, plan.Count(d => d.Kind == VsProp.Cow));
        // a bench faces the church door
        Assert.Contains(plan, d => d.Kind == VsProp.Bench && Math.Abs(d.E) < 0.5 && Math.Abs(d.N - 43) < 0.5);
        Assert.Equal(VsProps.All.Select(p => p.Kind).Where(k => plan.Any(d => d.Kind == k)), HidePlan.Kinds(plan));
    }

    [Fact]
    public void No_herd_no_cow_in_the_radial()
    {
        var (b, roads) = Hamlet();
        var plan = HidePlan.For(0, 0, 160, 7, b, roads, (_, _) => true, [], null);
        Assert.DoesNotContain(VsProp.Cow, HidePlan.Kinds(plan));
    }

    [Fact]
    public void The_objectives_sit_at_the_church_the_highest_grass_and_the_road()
    {
        var (b, roads) = Hamlet();
        var sites = VsSites.Plan(0, 0, 160, 0, 49, b, roads, (e, n) => n < -40, (e, n) => (float)(500 - n * 0.1));
        Assert.Equal(new[] { VsSite.Bell, VsSite.Fire, VsSite.Can }, sites.Select(s => s.Site));
        Assert.Equal((0.0, 49.0), (sites[0].E, sites[0].N));
        Assert.True(sites[1].N < -100, "the fire on the high (south) grass");
        Assert.InRange(HidePlan.RoadDistance(roads, sites[2].E, sites[2].N), 3.5, 5.5);
        double carry = Math.Sqrt((sites[2].FromE - sites[2].E) * (sites[2].FromE - sites[2].E) + (sites[2].FromN - sites[2].N) * (sites[2].FromN - sites[2].N));
        Assert.True(carry <= VsRules.CanCarryMax, $"carry {carry:F0} m");
    }

    // ---- VsTactics ----------------------------------------------------------------------------

    [Fact]
    public void A_hider_bot_hides_by_its_kind_away_from_the_door_and_the_others()
    {
        var plan = Plan();
        for (int seed = 0; seed < 10; seed++)
        {
            var spot = VsTactics.HiderSpot(plan, VsProp.HayBale, 0, 49, [(0, -60)], null, 1, new Random(seed), 0, 0, 160);
            Assert.NotNull(spot);
            var (e, n, _) = spot!.Value;
            Assert.True(Math.Sqrt(e * e + (n - 49) * (n - 49)) >= VsTactics.SpotFromDoor);
            Assert.True(Math.Sqrt(e * e + (n + 60) * (n + 60)) >= VsTactics.SpotFromHider);
            Assert.True(HidePlan.InPlace(plan, VsProp.HayBale, e, n, VsTactics.SpotNearKind));
            Assert.True(VsRules.Inside(0, 0, 160, e, n));
        }
        // cover from the door wins when it is on offer
        var covered = VsTactics.HiderSpot(plan, VsProp.HayBale, 0, 49, [], (e, n) => n < 0, 2, new Random(3), 0, 0, 160);
        Assert.True(covered!.Value.N < 0);
        Assert.Null(VsTactics.HiderSpot(plan, VsProp.Cow, 0, 49, [], null, 1, new Random(1), 0, 0, 160) is { } c && c.N > 0 ? c : null);
    }

    [Fact]
    public void Hider_moves_follow_the_seeker()
    {
        Assert.True(VsTactics.Facing(0, 0, 0, 1, 0.5, 10));
        Assert.False(VsTactics.Facing(0, 0, 0, 1, 10, -1));
        Assert.True(VsTactics.Flee(5, true));
        Assert.False(VsTactics.Flee(5, false));
        Assert.False(VsTactics.Flee(9, true));
        Assert.True(VsTactics.Moo(50, false, 1f, 0.2));
        Assert.False(VsTactics.Moo(30, false, 1f, 0));
        Assert.False(VsTactics.Moo(50, true, 1f, 0));
        Assert.True(VsTactics.GoForObjective(100, false, false));
        Assert.False(VsTactics.GoForObjective(50, false, false));
        Assert.False(VsTactics.GoForObjective(100, true, false));
        Assert.False(VsTactics.GoForObjective(100, false, true));
    }

    [Fact]
    public void A_seeker_suspects_what_moved_what_is_out_of_place_and_what_it_heard()
    {
        var s = new VsSuspicion(1);
        s.Look("d1", VsProp.HayBale, 0, 0, 0, inPlace: true);
        s.Look("b7", VsProp.HayBale, 10, 0, 0, inPlace: true);
        s.Look("b8", VsProp.Gnome, 50, 0, 0, inPlace: false);          // a gnome in a field
        Assert.Equal("b8", s.Best(0, 0, 0.3f)!.Key);
        s.Look("b7", VsProp.HayBale, 12, 0, 1, inPlace: true);          // the bale moved 2 m
        Assert.Equal("b7", s.Best(0, 0, 0.3f)!.Key);
        s.Miss("b7");
        Assert.Equal("b8", s.Best(0, 0, 0.3f)!.Key);
        s.Heard(1, 1);                                                 // a moo by d1
        Assert.True(s.All.First(x => x.Key == "d1").Suspicion > 0.3f);
        s.Puff(50, 3);
        Assert.Equal("b8", s.Best(0, 0, 0.3f)!.Key);
        s.Forget("b8");
        Assert.DoesNotContain(s.All, x => x.Key == "b8");
        // the base: easy bots suspect everything a little more, so they miss more
        Assert.True(VsTactics.BaseSuspicion(0) > VsTactics.BaseSuspicion(2));
    }

    // ---- VersteckisProgress -------------------------------------------------------------------

    private static VsGameTally Tally(int rounds = 3, int catches = 1, int survived = 2, VsProp asWhat = VsProp.Gnome, string canton = "VS")
    {
        var by = new int[VsProps.Count];
        by[(int)asWhat - 1] = survived;
        return new VsGameTally(rounds, 1, catches, survived, by, 1, 0, 0, 2, 120, canton);
    }

    [Fact]
    public void The_section_round_trips()
    {
        var p = new VersteckisProgress();
        VersteckisRewards.Fold(p, Tally(), false, "2026-10-27");
        var node = VersteckisProgress.Section.Encode(p);
        var back = VersteckisProgress.Section.DecodeTyped(JsonNode.Parse(node!.ToJsonString()), 1);
        Assert.Equal(1, back.Games);
        Assert.Equal(3, back.Rounds);
        Assert.Equal(2, back.SurvivedAs(VsProp.Gnome));
        Assert.Equal(["VS"], back.Cantons);
        Assert.Equal(120, back.BestHideSeconds);
    }

    [Fact]
    public void The_francs_count_for_five_games_a_day_and_the_progress_always()
    {
        var p = new VersteckisProgress();
        int paid = 0;
        for (int g = 0; g < 7; g++) paid += VersteckisRewards.Fold(p, Tally(), false, "2026-10-27").Francs;
        Assert.Equal(5 * 38, paid);
        Assert.Equal(7, p.Games);
        Assert.Equal(21, p.Rounds);
        Assert.Equal(38, VersteckisRewards.Fold(p, Tally(), false, "2026-10-28").Francs);   // a new day
    }

    [Fact]
    public void Titles_and_cosmetics_come_from_playing()
    {
        var p = new VersteckisProgress();
        var first = VersteckisRewards.Fold(p, Tally(catches: 0, survived: 0), false, "d");
        Assert.Equal([VersteckisRewards.Dorfchind], first.Titles);
        for (int i = 1; i < VersteckisRewards.GamesForZipfelmuetze - 1; i++) VersteckisRewards.Fold(p, Tally(catches: 0, survived: 0), false, "d");
        var tenth = VersteckisRewards.Fold(p, Tally(catches: 0, survived: 0), false, "d");
        Assert.Contains(ItemId.Zipfelmuetze, tenth.Items);
        var q = new VersteckisProgress();
        for (int i = 0; i < 8; i++) VersteckisRewards.Fold(q, Tally(catches: 4, survived: 2, asWhat: VsProp.Gnome, canton: i < 5 ? "C" + i : "VS"), false, "d" + i);
        Assert.Contains(VersteckisRewards.Spuernase, VersteckisRewards.Titles(q));          // 32 catches
        Assert.Contains(VersteckisRewards.Gartenzwerg, VersteckisRewards.Titles(q));        // 16 gnome rounds
        Assert.Contains(VersteckisRewards.Dorftour, VersteckisRewards.Titles(q));           // 6 cantons
        Assert.DoesNotContain(ItemId.Treichel, VersteckisRewards.Cosmetics(q));             // 16 of 30 rounds survived
        for (int i = 0; i < 7; i++) VersteckisRewards.Fold(q, Tally(survived: 2), false, "x");
        Assert.Contains(ItemId.Treichel, VersteckisRewards.Cosmetics(q));
    }

    [Fact]
    public void The_quest_step_counts_a_played_game()
    {
        var step = new VersteckisStep();
        Assert.True(step.Matches(new VersteckisPlayed(1, "k", 3, 10, false, "VS")));
        Assert.False(step.Matches(new VersteckisPlayed(1, "k", 0, 0, false, "VS")));
        Assert.Contains(QuestCatalog.All, q => q.Id == "versteckis-im-dorf" && q.Steps[0] is VersteckisStep);
    }
}
