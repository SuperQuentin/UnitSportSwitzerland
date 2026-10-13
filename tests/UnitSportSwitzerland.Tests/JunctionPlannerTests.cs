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

    public JunctionPlannerFixture() => Code = SignalTestRegion.Run(Dir, null, Log.Add, newJunctions: true);

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
        var wants = new ArmWants(Lights: false, all, SignalMoves.None, [SignalMoves.Left], null, RightPocket: true, BikeBetween: false, LeftTurnBikeLane: false);
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
        var mouth = MouthPlanner.Wanted(normal, new ArmWants(true, all, SignalMoves.None, [SignalMoves.Left], null, false, false, LeftTurnBikeLane: true, ThroughBike: true));
        // the straight-on exit carries no bike lane on: none is added (the left-turn one stays, beside nothing)
        var none = MouthPlanner.Wanted(normal, new ArmWants(true, all, SignalMoves.None, [SignalMoves.Left], null, false, false, LeftTurnBikeLane: true));
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
    public void A_section_shrinks_in_the_users_order_until_it_fits()
    {
        var side = new RoadSide(SidewalkDm: 30, Bike: BikeKind.Track, BikeDm: 20, KerbCm: 12, VergeDm: 10);
        var normal = Section.Normal(new RoadAttributes(LanesForward: 1, LanesBackward: 1, WidthCm: 700, Left: side, Right: side), 7f, true);
        var all = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
        var wanted = MouthPlanner.Wanted(normal, new ArmWants(false, all, SignalMoves.None, [SignalMoves.Left], null, true, false, false));
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
        var bareWanted = MouthPlanner.Wanted(bare, new ArmWants(false, all, SignalMoves.None, [SignalMoves.Left], null, false, false, false));
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
}
