using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.TestRegion;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The test region built once with the junction planner (#818, <c>--junctions new</c>).</summary>
public sealed class JunctionPlannerFixture : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "unitsport-818-" + Guid.NewGuid().ToString("N"));
    public readonly int Code;
    public readonly List<string> Log = new();

    public JunctionPlannerFixture() => Code = SignalTestRegion.Run(Dir, null, Log.Add, newJunctions: true, writeJunctions: false);

    public void Dispose()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        if (Directory.Exists(Dir + "_temp")) Directory.Delete(Dir + "_temp", recursive: true);
    }
}

/// <summary>
/// The junction planner (#818, <c>docs/plans/junction-layout.md</c>), step 1: an arm's cross-section from its attributes,
/// the mouth section the data wants, shrunk in the user's room order; on the test region every designed approach planned
/// with its designed lanes.
/// </summary>
public class JunctionPlannerTests(JunctionPlannerFixture region) : IClassFixture<JunctionPlannerFixture>
{
    private static readonly RoadSide Town = new(SidewalkDm: 20, KerbCm: 12);
    private static readonly RoadSide BikeLane = new(Bike: BikeKind.Lane, BikeDm: 15);

    /// <summary>A 9 m two-way road, one lane each way, a painted bike lane on its right and a sidewalk on both sides.</summary>
    private static RoadAttributes Street() => new(LanesForward: 1, LanesBackward: 1, WidthCm: 900,
        Left: Town, Right: Town with { Bike = BikeKind.Lane, BikeDm = 15 });

    [Fact]
    public void A_street_section_lists_its_bands_and_lanes_seen_driving_in()
    {
        // the junction at the segment's end: driving in along the drawing, its right is the drawing's right
        var s = Section.Normal(Street(), 9f, junctionAtEnd: true);
        Assert.Equal([StripKind.Sidewalk, StripKind.CarLane, StripKind.CarLane, StripKind.BikeLane, StripKind.Sidewalk],
            s.Strips.Select(x => x.Kind));
        Assert.Equal(-4.5, s.CarriagewayLeft, 6);
        Assert.Equal(9.0, s.CarriagewayWidth, 6);
        Assert.Equal(StripWay.Out, s.Strips[1].Way);
        Assert.Equal(StripWay.In, s.Strips[2].Way);
        // the car lanes share what the bike lane leaves: (9 - 1.5) / 2
        Assert.Equal(3.75, s.Strips[1].Width, 6);
        Assert.Equal(-0.75, s.CentreLine!.Value, 6);

        // at the other end the same street is seen the other way round
        var back = Section.Normal(Street(), 9f, junctionAtEnd: false);
        Assert.Equal([StripKind.Sidewalk, StripKind.BikeLane, StripKind.CarLane, StripKind.CarLane, StripKind.Sidewalk],
            back.Strips.Select(x => x.Kind));
        Assert.Equal(StripWay.Out, back.Strips[1].Way);
    }

    [Fact]
    public void The_mouth_gets_the_pockets_the_data_wants_and_no_turn_that_is_banned()
    {
        var normal = Section.Normal(Street(), 9f, junctionAtEnd: true);
        var all = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
        var wants = new ArmWants(Lights: false, all, SignalMoves.None, [SignalMoves.Left], null, RightPocket: [SignalMoves.Right], BikeBetween: false, LeftTurnBikeLane: false);
        var mouth = MouthPlanner.Wanted(normal, wants);
        Assert.Equal("L|T|R", mouth.CarLanesIn());
        // the street's lanes are wider than a full lane: the mouth's are at most 3.5 m where lanes are added
        Assert.All(mouth.CarLanes(StripWay.In), l => Assert.Equal(MouthPlanner.LaneMax, l.Strip.Width, 6));
        // the bike lane stays kerbside of the right pocket (layout a) and goes straight on or right
        Assert.Equal(StripKind.BikeLane, mouth.Strips[mouth.LastCarriageway].Kind);
        Assert.Equal(SignalMoves.Through | SignalMoves.Right, mouth.Strips[mouth.LastCarriageway].Moves);

        // OSM forbids the left turn: no pocket for it, and no lane shows it
        var banned = MouthPlanner.Wanted(normal, wants with { Banned = SignalMoves.Left });
        Assert.Equal("T|R", banned.CarLanesIn());
    }

    [Fact]
    public void A_through_bike_lane_comes_before_a_left_turn_one_and_survives_when_possible()
    {
        // a 10 m road with no bike lane at lights: the mouth adds the through riders' lane, kerbside, beside the left-turn one
        var normal = Section.Normal(new RoadAttributes(LanesForward: 1, LanesBackward: 1, WidthCm: 1000), 10f, true);
        var all = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
        var mouth = MouthPlanner.Wanted(normal, new ArmWants(true, all, SignalMoves.None, [SignalMoves.Left], null, [], false, LeftTurnBikeLane: true, ThroughBike: true));
        // the straight-on exit carries no bike lane on: none is added (the left-turn one stays, beside nothing)
        var none = MouthPlanner.Wanted(normal, new ArmWants(true, all, SignalMoves.None, [SignalMoves.Left], null, [], false, LeftTurnBikeLane: true));
        Assert.DoesNotContain(none.Strips, x => x.Id == MouthPlanner.AddedBikeId);
        var bikes = mouth.Strips.Where(x => x.Kind == StripKind.BikeLane).ToList();
        Assert.Equal([MouthPlanner.LeftBikeId, MouthPlanner.AddedBikeId], bikes.Select(x => x.Id));
        Assert.Equal(StripKind.BikeLane, mouth.Strips[mouth.LastCarriageway].Kind);

        // short of room: the left-turn bike lane goes first; then the arriving lanes narrow, the through bike lane stays
        var (nl, nr) = MouthPlanner.Reach(normal);
        var steps = new List<string>();
        MouthPlanner.Fit(mouth, normal, ArmRoom.Flat(nl, MouthPlanner.Reach(mouth).Right - 1.4), new Stretch(40, 30), new Stretch(40, 30), steps);
        Assert.Equal(["2 left-turn bike lane"], steps);
        steps.Clear();
        var fit = MouthPlanner.Fit(mouth, normal, ArmRoom.Flat(nl, MouthPlanner.Reach(mouth).Right - 2.9), new Stretch(40, 30), new Stretch(40, 30), steps);
        Assert.Equal(["2 left-turn bike lane", "3 lane widths to their least"], steps);
        Assert.Contains(fit.Strips, x => x.Id == MouthPlanner.AddedBikeId);
        // the departing lane had room: it keeps its width
        Assert.Equal(5.0, fit.CarLanes(StripWay.Out).Single().Strip.Width, 6);
    }

    [Fact]
    public void Between_layout_gives_riders_turning_right_their_own_lane_only_where_there_is_plenty_of_room()
    {
        // layout (b): the bike lane between the through lane and the right pocket goes straight on; a bike right-turn lane
        // at the kerb, right of the pocket
        var normal = Section.Normal(Street(), 9f, junctionAtEnd: true);
        var all = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
        var mouth = MouthPlanner.Wanted(normal, new ArmWants(false, all, SignalMoves.None, [], null, RightPocket: [SignalMoves.Right], BikeBetween: true, LeftTurnBikeLane: false, RightTurnBike: true));
        var inside = mouth.Strips.Skip(mouth.FirstCarriageway).Take(mouth.LastCarriageway - mouth.FirstCarriageway + 1).ToList();
        Assert.Equal([MouthPlanner.RightPocketId, MouthPlanner.RightBikeId], inside.TakeLast(2).Select(x => x.Id));
        Assert.Equal(SignalMoves.Through, inside[^3].Moves);
        Assert.Equal(StripKind.BikeLane, inside[^3].Kind);

        // a little short of room: it is the first thing to go, and riders share the pocket
        var (nl, _) = MouthPlanner.Reach(normal);
        var steps = new List<string>();
        var fit = MouthPlanner.Fit(mouth, normal, ArmRoom.Flat(nl, MouthPlanner.Reach(mouth).Right - 0.5), new Stretch(30, 15), new Stretch(30, 15), steps);
        Assert.Equal(["1 bike right-turn lane"], steps);
        Assert.True(fit.Strips.Single(x => x.Id == MouthPlanner.RightPocketId).SharedWithBikes);
    }

    [Fact]
    public void Without_lights_the_turning_riders_lanes_go_first_right_then_left()
    {
        var normal = Section.Normal(Street(), 9f, junctionAtEnd: true);
        var all = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
        var wants = new ArmWants(false, all, SignalMoves.None, [SignalMoves.Left], null, RightPocket: [SignalMoves.Right], BikeBetween: true,
            LeftTurnBikeLane: true, ThroughBike: true, RightTurnBike: true);
        var mouth = MouthPlanner.Wanted(normal, wants);
        var (nl, _) = MouthPlanner.Reach(normal);
        var steps = new List<string>();
        MouthPlanner.Fit(mouth, normal, ArmRoom.Flat(nl, MouthPlanner.Reach(mouth).Right - 2.0), new Stretch(40, 30), new Stretch(40, 30), steps, lights: false);
        Assert.Equal(["1 bike right-turn lane", "1 left-turn bike lane"], steps);
        // at the lights the left-turn bike lane holds out past the verge and the island
        steps.Clear();
        MouthPlanner.Fit(mouth, normal, ArmRoom.Flat(nl, MouthPlanner.Reach(mouth).Right - 2.0), new Stretch(40, 30), new Stretch(40, 30), steps, lights: true);
        Assert.Equal(["1 bike right-turn lane", "2 left-turn bike lane"], steps);
    }

    [Fact]
    public void Osm_lanes_beyond_the_carriageways_are_the_pockets_taken_from_each_end()
    {
        var all = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
        SignalMoves L = SignalMoves.Left, T = SignalMoves.Through, R = SignalMoves.Right;
        // two lanes toward the junction, the data shows three: the left-only one is a pocket (it used to be folded)
        var d = DataLanes.Of([L, T, T | R], 2, all);
        Assert.Equal([L], d.Left);
        Assert.Equal([T, T | R], d.Own);
        Assert.Empty(d.Right);
        // one lane, the data a double right at any speed and a left
        d = DataLanes.Of([L, T, R, R], 1, all);
        Assert.Equal([L], d.Left);
        Assert.Equal([T], d.Own);
        Assert.Equal([R, R], d.Right);
        // as many lanes as the carriageway holds: no pocket, the lanes in place
        d = DataLanes.Of([L, T | R], 2, all);
        Assert.Empty(d.Left);
        Assert.Equal([L, T | R], d.Own);

        // the room order never drops a pocket the data asks for
        var normal = Section.Normal(Street(), 9f, junctionAtEnd: true);
        var wants = new ArmWants(false, all, SignalMoves.None, [L], [T], [R, R], false, false, FromData: true);
        var mouth = MouthPlanner.Wanted(normal, wants);
        Assert.Equal("L|T|R|R", mouth.CarLanesIn());
        var (nl, nr) = MouthPlanner.Reach(normal);
        var steps = new List<string>();
        var fit = MouthPlanner.Fit(mouth, normal, ArmRoom.Flat(nl, nr), new Stretch(40, 30), new Stretch(40, 30), steps, keepPockets: true);
        Assert.Equal("L|T|R|R", fit.CarLanesIn());
        Assert.Contains("does not fit", steps);
    }

    [Fact]
    public void A_section_shrinks_in_the_users_order_until_it_fits()
    {
        var side = new RoadSide(SidewalkDm: 30, Bike: BikeKind.Track, BikeDm: 20, KerbCm: 12, VergeDm: 10);
        var normal = Section.Normal(new RoadAttributes(LanesForward: 1, LanesBackward: 1, WidthCm: 700, Left: side, Right: side), 7f, true);
        var all = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
        var wanted = MouthPlanner.Wanted(normal, new ArmWants(false, all, SignalMoves.None, [SignalMoves.Left], null, [SignalMoves.Right], false, false));
        var (nl, nr) = MouthPlanner.Reach(normal);
        var (wl, wr) = MouthPlanner.Reach(wanted);
        Assert.True(wr > nr);

        // a little room on the right: the verge goes first, nothing else
        var steps = new List<string>();
        var fit = MouthPlanner.Fit(wanted, normal, ArmRoom.Flat(nl, wr - 0.8), new Stretch(40, 30), new Stretch(40, 30), steps);
        Assert.Equal(["1 verge and buffer"], steps);
        Assert.Equal("L|T|R", fit.CarLanesIn());

        // no room beyond the street's own: the verges go, the lanes narrow, the right pocket goes, the sidewalks narrow; the
        // left pocket fits in what that freed
        steps.Clear();
        fit = MouthPlanner.Fit(wanted, normal, ArmRoom.Flat(nl, nr), new Stretch(40, 30), new Stretch(40, 30), steps);
        Assert.Equal(["1 verge and buffer", "3 lane widths to their least", "4 right pocket", "5 sidewalk to 1.5 m"], steps);
        Assert.Equal("L|TR", fit.CarLanesIn());
        Assert.True(MouthPlanner.Reach(fit).Right <= nr + 1e-9);

        // a street with nothing beside its kerbs: only the left pocket can go, the through lane takes the left turn
        var bare = Section.Normal(new RoadAttributes(LanesForward: 1, LanesBackward: 1, WidthCm: 600), 6f, true);
        var bareWanted = MouthPlanner.Wanted(bare, new ArmWants(false, all, SignalMoves.None, [SignalMoves.Left], null, [], false, false));
        steps.Clear();
        fit = MouthPlanner.Fit(bareWanted, bare, ArmRoom.Flat(3, 3), new Stretch(40, 30), new Stretch(40, 30), steps);
        Assert.Equal(["3 lane widths to their least", "7 left pocket"], steps);
        Assert.Equal("LTR", fit.CarLanesIn());
    }

    [Fact]
    public void Room_far_out_along_the_arm_only_limits_what_the_transition_still_holds_there()
    {
        var normal = Section.Normal(Street(), 9f, junctionAtEnd: true);
        var (nl, nr) = MouthPlanner.Reach(normal);
        // houses beside the kerb 60 m out: the storage (20 m) and taper (20 m) are back to normal by 40 m
        var room = new ArmRoom([0, 20, 40, 60], [nl, nl, nl, nl], [30, 30, 30, nr]);
        var (_, right) = MouthPlanner.MouthRoom(normal, room, new Stretch(20, 20), new Stretch(20, 20));
        Assert.Equal(30, right, 6);
        // a 40 m storage and 30 m taper still hold a third of it beside the houses: no widening at all
        (_, right) = MouthPlanner.MouthRoom(normal, room, new Stretch(40, 30), new Stretch(40, 30));
        Assert.Equal(nr, right, 6);
    }

    [Fact]
    public void Every_designed_approach_is_planned_with_its_lanes()
    {
        Assert.True(region.Code == 0, string.Join("\n", region.Log.Where(l => l.Contains("RESULT", StringComparison.Ordinal))));
        Assert.Contains(region.Log, l => l.Contains("planner (#818): every designed approach as designed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A crossroads of four straight streets (step 2's core): each arm's lane making the moves <paramref name="movesOf"/> gives
    /// it (by its outward heading, east first, counter-clockwise), the street <paramref name="street"/>.
    /// </summary>
    private static JunctionPlan Crossroads(Func<int, SignalMoves> movesOf, RoadAttributes street, float width, Func<int, bool>? major = null)
    {
        var jp = new JunctionPlan { Node = 1, Centre = new UnitSport.Tools.RoadGen.Geometry.Vec2(2600000, 1200000), Lights = false };
        for (int i = 0; i < 4; i++)
        {
            double heading = i * Math.PI / 2;
            var normal = Section.Normal(street, width, junctionAtEnd: true);
            var moves = movesOf(i);
            var wants = new ArmWants(false, moves, SignalMoves.None, [], null, [], false, false);
            var mouth = MouthPlanner.Wanted(normal, wants);
            var arm = new ArmPlan
            {
                Arm = i, LinkId = i, Heading = heading, Normal = normal, Wants = wants, Wanted = mouth, Mouth = mouth,
                Axis = ArmAxis.Straight(jp.Centre, heading), Major = major?.Invoke(i) ?? false,
            };
            // arriving from heading h: the left turn leaves toward h - 90 degrees, the right toward h + 90, straight on h + 180
            if ((moves & SignalMoves.Left) != 0) arm.Toward[SignalMoves.Left] = (i + 3) % 4;
            if ((moves & SignalMoves.Through) != 0) arm.Toward[SignalMoves.Through] = (i + 2) % 4;
            if ((moves & SignalMoves.Right) != 0) arm.Toward[SignalMoves.Right] = (i + 1) % 4;
            jp.Arms.Add(arm);
        }
        return jp;
    }

    private const SignalMoves AllMoves = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;

    [Fact]
    public void The_core_joins_every_allowed_move_and_its_kerbs_start_inside_the_mouths()
    {
        var jp = Crossroads(_ => AllMoves, new RoadAttributes(LanesForward: 1, LanesBackward: 1, WidthCm: 600, Left: Town, Right: Town), 6f);
        var core = CorePlanner.Plan(jp);
        Assert.Equal(12, core.Connectors.Count);
        Assert.Equal(0, core.KerbsNotTangent);
        Assert.Equal(0, core.ConnectorsOutside);
        Assert.All(core.Corners, c => Assert.Equal(CornerShape.Arc, c.Shape));
        // every corner as round as the right turn round it: its kerb at least a car's turning radius (its lane at the kerb)
        Assert.All(core.Corners, c => Assert.True(c.Radius >= CorePlanner.CarInner - 1e-3, $"corner {c.ArmA}-{c.ArmB}: r {c.Radius:F2}"));
        // each kerb leaves its arm's straight edge inside the mouth: the mouth is where the kerb curve starts
        foreach (var c in core.Corners)
        {
            Assert.True(c.StationA <= core.Mouths.Single(m => m.Arm == c.ArmA).Station + 0.02);
            Assert.True(c.StationB <= core.Mouths.Single(m => m.Arm == c.ArmB).Station + 0.02);
        }
        // the turns are as wide as fits: a left turn wider than its least radius
        Assert.All(core.Connectors.Where(c => c.Move == SignalMoves.Left), c => Assert.True(c.Radius > c.MinRadius + 0.5));
    }

    [Fact]
    public void A_turn_between_two_major_roads_is_laid_for_a_truck_where_it_fits_untouched()
    {
        var street = new RoadAttributes(LanesForward: 1, LanesBackward: 1, WidthCm: 600, Left: Town, Right: Town);
        double Inner(Connector c) => c.MinRadius - (c.Width + c.ToWidth) * 0.25;
        // east and west are Major roads, north and south not: only the turns between east and west would be a truck's, and
        // those are through moves; every turn into or out of a side street is a car's (the user: both arms big)
        var mixed = CorePlanner.Plan(Crossroads(_ => AllMoves, street, 6f, i => i % 2 == 0));
        Assert.All(mixed.Connectors.Where(c => c.Turns && c.Kind == ConnectorKind.Car), c => Assert.Equal(CorePlanner.CarInner, Inner(c), 3));

        // four Major roads with room everywhere: every turn a truck's, the mouths further out than a car's
        var truck = CorePlanner.Plan(Crossroads(_ => AllMoves, street, 6f, _ => true));
        var turns = truck.Connectors.Where(c => c.Turns && c.Kind == ConnectorKind.Car).ToList();
        Assert.All(turns, c => Assert.True(c.Truck && Math.Abs(Inner(c) - CorePlanner.TruckInner) < 1e-3));
        Assert.True(truck.Mouths.Min(m => m.Station) > mixed.Mouths.Max(m => m.Station));
        Assert.Equal((0, 0), (truck.KerbsNotTangent, truck.TrucksAsCars));

        // a house in the north-east corner, 11.6 m out along its bisector: the truck's kerb and sidewalk would reach it, so the
        // east arm's right turn (into the north) falls back to a car's; the other turns keep the truck's
        var jp = Crossroads(_ => AllMoves, street, 6f, _ => true);
        var house = jp.Centre + new UnitSport.Tools.RoadGen.Geometry.Vec2(8.2, 8.2);
        var held = CorePlanner.Plan(jp, p => p.DistanceTo(house) < 1.5);
        var east = held.Connectors.Single(c => c.Kind == ConnectorKind.Car && c.FromArm == 0 && c.Move == SignalMoves.Right);
        Assert.False(east.Truck);
        Assert.Equal(1, held.TrucksAsCars);
        Assert.Contains(held.Connectors, c => c.Truck);
        Assert.Equal(0, held.ConnectorsOutside);
    }

    [Fact]
    public void A_corner_no_one_turns_round_stays_tight_by_the_same_rule()
    {
        // the south arm (heading 270) may not turn right: the corner between it and the east arm keeps only the least rounding
        var jp = Crossroads(i => i == 3 ? SignalMoves.Left | SignalMoves.Through : AllMoves,
            new RoadAttributes(LanesForward: 1, LanesBackward: 1, WidthCm: 600, Left: Town, Right: Town), 6f);
        var core = CorePlanner.Plan(jp);
        var tight = core.Corners.Single(c => c.Tight);
        Assert.Equal((3, 0), (tight.ArmA, tight.ArmB));
        Assert.Equal(CorePlanner.TightLeg, tight.Radius, 3);
        Assert.Equal(0, core.ConnectorsOutside);
    }

    [Fact]
    public void A_bike_lane_turning_right_rounds_the_kerb_inside_the_cars_turn()
    {
        // a painted bike lane at the kerb on every arm, turning right with the car lane: the kerb follows the bike connector,
        // concentric inside the car's turn (the user: the car keeps its radius, the bike lane turns within it)
        var side = Town with { Bike = BikeKind.Lane, BikeDm = 15 };
        var jp = Crossroads(_ => AllMoves, new RoadAttributes(LanesForward: 1, LanesBackward: 1, WidthCm: 900, Left: side, Right: side), 9f);
        var core = CorePlanner.Plan(jp);
        Assert.Equal(0, core.ConnectorsOutside);
        foreach (var corner in core.Corners)
        {
            var car = core.Connectors.Single(c => c.Kind == ConnectorKind.Car && c.Move == SignalMoves.Right && c.FromArm == corner.ArmA);
            var bike = core.Connectors.Single(c => c.Kind == ConnectorKind.Bike && c.Move == SignalMoves.Right && c.FromArm == corner.ArmA);
            Assert.Same(bike, corner.Bound);
            Assert.True(corner.Radius < car.Radius - car.Width * 0.5 - 1.0, $"kerb r {corner.Radius:F2}, car's inner edge {car.Radius - car.Width * 0.5:F2}");
        }
    }

    [Fact]
    public void Every_core_of_the_test_region_keeps_its_connectors_and_starts_its_kerbs_inside_the_mouths()
    {
        var line = region.Log.FirstOrDefault(l => l.Contains("junction cores (#818 step 2)", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains("kerbs starting outside their mouth 0,", line);
        Assert.Contains("connectors leaving the outline 0", line);
    }

    /// <summary>An arm of <see cref="Street"/> with a left pocket of (taper, storage), its section moved left by <paramref name="split"/>.</summary>
    private static ArmPlan PocketArm(double taper, double storage, double split = 0, bool atEnd = true)
    {
        var normal = Section.Normal(Street(), 9f, junctionAtEnd: atEnd);
        var wants = new ArmWants(false, AllMoves, SignalMoves.None, [SignalMoves.Left], null, [], false, false);
        var mouth = MouthPlanner.Moved(MouthPlanner.Wanted(normal, wants), -split);
        var a = new ArmPlan { Arm = 0, LinkId = 1, AtEnd = atEnd, Heading = 0, Normal = normal, Wants = wants, Wanted = mouth, Mouth = mouth, Length = 200, TransitionRoom = 200, Split = split };
        a.Openings.Add(new Opening(MouthPlanner.LeftPocketId, taper, storage));
        return a;
    }

    private static int StripIndex(Transition t, int id) => t.Strips.FindIndex(x => x.Strip.Id == id);

    [Fact]
    public void A_left_pocket_is_held_over_its_storage_then_gives_way_to_its_lead_in_hatch_over_the_taper()
    {
        var a = PocketArm(taper: 30, storage: 40, split: 1.5);
        TransitionPlanner.PlanAll([new JunctionPlan { Node = 1, Centre = default, Lights = false, Arms = { a } }]);
        var t = a.Transition!;
        int pocket = StripIndex(t, MouthPlanner.LeftPocketId), hatch = StripIndex(t, TransitionPlanner.LeadInHatchId);
        Assert.True(pocket > hatch && hatch >= t.Anchor, "the lead-in hatch lies between the centre line and the pocket");
        Assert.Equal(a.Mouth.Strips.First(x => x.Id == MouthPlanner.LeftPocketId).Width, t.WidthAt(pocket, 40), 6);
        Assert.Equal(0, t.WidthAt(hatch, 40), 6);
        // the closing line: the pocket narrows to nothing over the slant (10 m) while the hatch takes its place
        Assert.Equal(0, t.WidthAt(pocket, 50), 6);
        Assert.True(t.WidthAt(hatch, 45) > 0 && t.WidthAt(pocket, 45) > 0);
        // the through lane runs on one line: pocket and hatch together close linearly over the taper, the centre line moving
        // back with the split's share
        double Wide(double s) => t.WidthAt(pocket, s) + t.WidthAt(hatch, s);
        Assert.Equal((Wide(40) + Wide(70)) / 2, Wide(55), 6);
        Assert.Equal(0, Wide(70), 6);
        Assert.Equal(a.Mouth.CentreLine!.Value, t.CentreAt(40), 6);
        Assert.Equal(a.Normal.CentreLine!.Value, t.CentreAt(70), 6);
        var through = StripIndex(t, a.Normal.Strips.First(x => x.IsCarLane && x.Way == StripWay.In).Id);
        double Edge(double s) => t.SpanAt(through, s).From;
        Assert.Equal((Edge(40) + Edge(70)) / 2, Edge(55), 6);
        // past the transition the normal section, strip for strip
        var end = t.At(t.Length);
        Assert.Equal(a.Normal.Strips.Select(x => (x.Id, Math.Round(x.Width, 3))), end.Strips.Select(x => (x.Id, Math.Round(x.Width, 3))));
        Assert.Equal(a.Normal.CarriagewayLeft, end.CarriagewayLeft, 6);
    }

    [Fact]
    public void An_exit_hatch_closes_over_thirty_metres_and_every_other_change_comes_after_at_one_in_six()
    {
        var normal = Section.Normal(Street(), 9f, junctionAtEnd: true);
        // the departing lane wider at the mouth, beside an exit hatch
        var mouth = MouthPlanner.WithExitHatch(normal, 2.0);
        var strips = mouth.Strips.Select(x => x.IsCarLane && x.Way == StripWay.Out ? x.WithWidth(x.Width + 0.5) : x).ToList();
        mouth = new Section(strips, mouth.CarriagewayLeft - 0.5);
        var wants = new ArmWants(false, AllMoves, SignalMoves.None, [], null, [], false, false);
        var a = new ArmPlan { Arm = 0, LinkId = 1, AtEnd = true, Heading = 0, Normal = normal, Wants = wants, Wanted = mouth, Mouth = mouth, Length = 200, TransitionRoom = 200 };
        TransitionPlanner.PlanAll([new JunctionPlan { Node = 1, Centre = default, Lights = false, Arms = { a } }]);
        var t = a.Transition!;
        int hatch = StripIndex(t, MouthPlanner.HatchId), lane = StripIndex(t, normal.Strips.First(x => x.IsCarLane && x.Way == StripWay.Out).Id);
        Assert.True(hatch < t.Anchor, "the exit hatch lies on the departing side");
        Assert.Equal(1.0, t.WidthAt(hatch, 15), 6);
        Assert.Equal(0, t.WidthAt(hatch, TransitionPlanner.ExitTaper), 6);
        // the lane's extra 0.5 m goes beyond the hatch, over 6 m (1:6 has a 6 m floor)
        Assert.Equal(mouth.Strips.First(x => x.IsCarLane && x.Way == StripWay.Out).Width, t.WidthAt(lane, TransitionPlanner.ExitTaper), 6);
        Assert.Equal(normal.Strips.First(x => x.IsCarLane && x.Way == StripWay.Out).Width, t.WidthAt(lane, TransitionPlanner.ExitTaper + 6), 6);
        Assert.Equal(TransitionPlanner.ExitTaper + 6, t.Length, 6);
        // the centre line stays where it is: the departing side moves out to the left
        Assert.Equal(normal.CentreLine!.Value, t.CentreAt(10), 6);
    }

    [Fact]
    public void A_line_up_hatch_moves_the_arriving_side_out_and_closes_over_thirty_metres()
    {
        var normal = Section.Normal(Street(), 9f, junctionAtEnd: true);
        var exit = MouthPlanner.WithExitHatch(normal, 2.0);
        var mouth = MouthPlanner.WithLineUpHatch(exit, 1.2);
        // the departing side and the centre line stay; the arriving side moves out by the hatch
        Assert.Equal(exit.CarriagewayLeft, mouth.CarriagewayLeft, 6);
        Assert.Equal(exit.CentreLine!.Value, mouth.CentreLine!.Value, 6);
        Assert.Equal(exit.CarriagewayRight + 1.2, mouth.CarriagewayRight, 6);
        // the exit hatch taken off (the planner lines the arms up again) takes the line-up hatch with it, the left edge back in
        var bare = MouthPlanner.WithExitHatch(mouth, 0);
        Assert.Equal(normal.CarriagewayLeft, bare.CarriagewayLeft, 6);
        Assert.Equal(normal.CarriagewayRight, bare.CarriagewayRight, 6);

        var wants = new ArmWants(false, AllMoves, SignalMoves.None, [], null, [], false, false);
        var a = new ArmPlan { Arm = 0, LinkId = 1, AtEnd = true, Heading = 0, Normal = normal, Wants = wants, Wanted = mouth, Mouth = mouth, Length = 200, TransitionRoom = 200 };
        TransitionPlanner.PlanAll([new JunctionPlan { Node = 1, Centre = default, Lights = false, Arms = { a } }]);
        var t = a.Transition!;
        int hatch = StripIndex(t, MouthPlanner.LineUpHatchId);
        Assert.True(hatch >= t.Anchor, "the line-up hatch lies right of the centre line");
        Assert.Equal(0.6, t.WidthAt(hatch, 15), 6);
        Assert.Equal(0, t.WidthAt(hatch, TransitionPlanner.ExitTaper), 6);
    }

    [Fact]
    public void A_street_too_short_for_two_transitions_holds_one_with_the_median_running_mouth_to_mouth()
    {
        // a pocket merged with the exit of the junction beyond (#325): one transition, the lead-in median meeting that exit's hatch
        var a = PocketArm(taper: 0, storage: 30);
        var normalB = Section.Normal(Street(), 9f, junctionAtEnd: false);
        var wantsB = new ArmWants(false, AllMoves, SignalMoves.None, [], null, [], false, false);
        var mouthB = MouthPlanner.WithExitHatch(normalB, a.Mouth.Strips.Where(x => x.Id == MouthPlanner.LeftPocketId).Sum(x => x.Width));
        var b = new ArmPlan { Arm = 0, LinkId = 1, AtEnd = false, Heading = Math.PI, Normal = normalB, Wants = wantsB, Wanted = mouthB, Mouth = mouthB, Length = 60 };
        a.Length = 60;
        a.MergedWith = b;
        (a.FarArm, b.FarArm, a.TransitionRoom, b.TransitionRoom) = (b, a, 60, 60);
        TransitionPlanner.PlanAll([new JunctionPlan { Node = 1, Centre = default, Lights = false, Arms = { a } },
            new JunctionPlan { Node = 2, Centre = default, Lights = false, Arms = { b } }]);
        var t = a.Transition!;
        Assert.Same(t, b.Transition);
        Assert.Same(b, t.Far);
        Assert.Equal(60, t.Length, 6);
        int hatch = StripIndex(t, TransitionPlanner.LeadInHatchId);
        double width = a.Mouth.Strips.Where(x => x.Id == MouthPlanner.LeftPocketId || x.Id == MouthPlanner.LeftBikeId).Sum(x => x.Width);
        // the pocket opens out of the median over the merged entry, the median then runs on to the far mouth (2+1 kept)
        Assert.Equal(0, t.WidthAt(hatch, 30), 6);
        Assert.True(t.WidthAt(hatch, 50) >= width - 1e-6 && t.WidthAt(hatch, 60) > 0);
        // at the far mouth, the far arm's own section seen from this end
        Assert.Equal(mouthB.CarriagewayWidth, t.At(60).CarriagewayWidth, 3);
    }

    [Fact]
    public void An_ordinary_segment_has_one_section_all_along_and_a_transition_its_strips_at_every_point()
    {
        var seg = new RoadSegment
        {
            Class = RoadClass.Road, Surface = RoadSurface.Paved, Width = 9, Points = [0, 0, 0, 20, 0, 0, 40, 0, 0],
            Attributes = Street() with { Flags = RoadAttrFlags.Urban },
        };
        var plain = SegmentSections.Of(seg);
        Assert.False(plain.Varies);
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal((-4.5f, 4.5f), (plain.RibbonLeft[i], plain.RibbonRight[i])));
        Assert.Same(plain.RightProfile[0], plain.RightProfile[2]);
        Assert.Equal(2.0f, plain.LeftProfile[1]!.Width, 3);
        // a pocket of 3 m opening at the right over the segment, the sidewalk beside it narrowed to 1.5 m at its first point
        var t = new RoadTransition
        {
            Segment = seg, Stations = [0, 40], Centre = [-0.75f, -0.75f], Anchor = 2,
            Strips =
            [
                new RoadTransitionStrip { Kind = StripKind.Sidewalk, Id = 1, Widths = [2, 2] },
                new RoadTransitionStrip { Kind = StripKind.CarLane, Id = 2, Travel = -1, Widths = [3.75f, 3.75f] },
                new RoadTransitionStrip { Kind = StripKind.CarLane, Id = 3, Travel = 1, Widths = [3.75f, 3.75f] },
                new RoadTransitionStrip { Kind = StripKind.CarLane, Id = 120, Travel = 1, Widths = [3, 0] },
                new RoadTransitionStrip { Kind = StripKind.BikeLane, Id = 4, Travel = 1, Widths = [1.5f, 1.5f] },
                new RoadTransitionStrip { Kind = StripKind.Sidewalk, Id = 5, Widths = [1.5f, 2] },
            ],
        };
        var s = SegmentSections.Of(seg, t);
        Assert.True(s.Varies);
        Assert.Equal(-4.5f, s.RibbonLeft[0], 3);
        Assert.Equal(7.5f, s.RibbonRight[0], 3);
        Assert.Equal(6.0f, s.RibbonRight[1], 3);
        Assert.Equal(4.5f, s.RibbonRight[2], 3);
        // one shape all along: the narrowed sidewalk is the same profile, its width per point
        Assert.Equal(s.RightProfile[0]!.Count, s.RightProfile[2]!.Count);
        Assert.Equal(1.5f, s.RightProfile[0]!.Width, 3);
        Assert.Equal(2.0f, s.RightProfile[2]!.Width, 3);
        Assert.Equal(7.5f + 1.5f, s.OuterRight(0), 3);
    }

    // builds the whole signal test region: 556 s of a 14 min unit run (#1407), so only the full tier runs it
    [Fact, Trait("Speed", "Slow")]
    public void The_written_test_region_holds_its_cores_and_transitions_and_decodes_them_alike()
    {
        string dir = Path.Combine(Path.GetTempPath(), "unitsport-818w-" + Guid.NewGuid().ToString("N"));
        var log = new List<string>();
        try
        {
            Assert.Equal(0, SignalTestRegion.Run(dir, null, log.Add, newJunctions: true, writeJunctions: true));
            var written = log.FirstOrDefault(l => l.Contains("junction planner written (#818 phase 3)", StringComparison.Ordinal));
            Assert.NotNull(written);
            Assert.Contains("pieces lost by a later cut 0", written);
            Assert.Contains(log, l => l.Contains("paint encoding", StringComparison.Ordinal) && l.Contains(" 0 decoded differently", StringComparison.Ordinal));
            int transitions = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "roads_*.road"))
            {
                using var fs = File.OpenRead(file);
                var tile = RoadCodec.Decode(fs);
                foreach (var t in tile.Transitions)
                {
                    transitions++;
                    Assert.True(t.Segment.Attributes.Has(RoadAttrFlags.InTransition));
                    Assert.Equal(SegmentSections.Stations(t.Segment)[^1], t.Stations[^1], 1);
                }
            }
            Assert.True(transitions >= 20, $"{transitions} transitions written");
            // every kerb a zebra or bike crossing meets is still sloped
            Assert.Equal(0, UnitSport.Tools.RoadGen.Diagnostics.KerbCheck.Run(dir, null, false, _ => { }));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (Directory.Exists(dir + "_temp")) Directory.Delete(dir + "_temp", recursive: true);
        }
    }

    [Fact]
    public void A_merged_core_outline_drops_its_spikes_and_cuts_the_loop_where_two_members_kerbs_cross()
    {
        // a 20 x 10 m core whose east side runs on 2 m past a point and back (an inner street's side over a kerb), and whose
        // north side crosses itself in a small twist (two members' kerbs overlapping)
        var ring = new List<UnitSport.Tools.RoadGen.Geometry.Vec2>
        {
            new(0, 0), new(20, 0), new(20, 6), new(20, 8), new(20, 5), new(20, 10), new(12, 10), new(9, 11), new(10, 9.5), new(7, 10), new(0, 10),
        };
        MergedCore.Clean(ring);
        Assert.True(CorePlanner.Simple(ring));
        Assert.InRange(CorePlanner.SignedArea(ring), 199, 202);
        Assert.DoesNotContain(new UnitSport.Tools.RoadGen.Geometry.Vec2(20, 8), ring);
    }
}
