using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.TestRegion;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The test region's traffic (#1134): the capacity formula on a known plan (J1's, as the region builds it), the endpoints the build
/// writes (one per arm and mode, none between lights closer than <see cref="SignalTestRegion.ClusterReach"/>, shares to the arms
/// beyond such a pair), and the file's round trip.
/// </summary>
public class RegionTrafficTests(SignalTestRegionFixture region) : IClassFixture<SignalTestRegionFixture>
{
    private List<SignalTestRegion.Lights> Lights() => SignalTestRegion.ReadLights(region.Dir, SignalTestRegion.Junctions);

    /// <summary>A movement's effective green worked out by hand from its groups' intervals: green and amber, less the start-up loss per green.</summary>
    private static float EffectiveGreen(SignalPlan plan, IEnumerable<int> groups)
    {
        float sum = 0f;
        foreach (int g in groups)
        {
            var iv = plan.Groups[g].Intervals;
            sum += iv.Where(x => x.Aspect is SignalAspect.Green or SignalAspect.Amber).Sum(x => x.To - x.From);
            // green runs: a green interval whose predecessor (round the cycle) is not green
            for (int i = 0; i < iv.Count; i++)
                if (iv[i].Aspect == SignalAspect.Green && iv[(i + iv.Count - 1) % iv.Count].Aspect != SignalAspect.Green) sum -= SignalPlan.StartLoss;
        }
        return sum;
    }

    [Fact]
    public void J1s_through_rate_is_green_share_times_lane_times_saturation_times_degree()
    {
        var j1 = Lights().Single(l => l.Name == "J1-ideal-lanes");
        var plan = j1.Plan;
        var flows = SignalCapacity.Cars(plan, k => j1.Approaches.TryGetValue(k, out var a) ? a.Lanes : null);
        // every arm drives every way: 4 arms x 3 moves
        Assert.Equal(12, flows.Count);
        foreach (var f in flows.Where(f => f.Turn == SignalMoves.Through))
        {
            // J1: one through lane, its own group, never giving way
            var groups = plan.Movements().Where(m => m.From == f.From && m.To == f.To
                && plan.Groups[m.Group].Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow).Select(m => m.Group).ToList();
            Assert.Single(groups);
            float want = SignalCapacity.Degree * SignalCapacity.CarSaturation * EffectiveGreen(plan, groups) / plan.Cycle / 60f;
            Assert.InRange(f.PerMinute, want - 0.15f, want + 0.15f);
            Assert.InRange(f.PerMinute, 3f, 15f);
        }
        // a turn's saturation is less than straight on's: a lane each, J1's protected arrows
        foreach (var f in flows.Where(f => f.Turn != SignalMoves.Through))
            Assert.True(f.PerMinute > 0.5f, $"{f.From}->{f.To} {f.Turn} {f.PerMinute}");
    }

    [Fact]
    public void A_shared_lane_splits_by_moves_and_a_path_counts_as_a_bike_lane()
    {
        ApproachLane Lane(SignalMoves m, ApproachLaneKind k = ApproachLaneKind.Car) => new(0, 0, 0, m, k);
        var all = SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
        var lanes = new[] { Lane(SignalMoves.Left), Lane(SignalMoves.Through | SignalMoves.Right) };
        Assert.Equal(1f, SignalCapacity.LanesFor(lanes, ApproachLaneKind.Car, SignalMoves.Left, all), 3);
        Assert.Equal(2f / 3f, SignalCapacity.LanesFor(lanes, ApproachLaneKind.Car, SignalMoves.Through, all), 3);
        Assert.Equal(1f / 3f, SignalCapacity.LanesFor(lanes, ApproachLaneKind.Car, SignalMoves.Right, all), 3);
        Assert.Equal(0f, SignalCapacity.LanesFor(lanes, ApproachLaneKind.Bike, SignalMoves.Right, all), 3);
    }

    [Fact]
    public void Every_lit_junction_gets_one_endpoint_per_arm_and_mode_and_none_between_close_lights()
    {
        var endpoints = TrafficEndpointsFile.Read(Path.Combine(region.Dir, TrafficEndpointsFile.FileName));
        Assert.NotEmpty(endpoints);
        var lights = Lights();
        // every lit junction generates cars (but a cluster's arms into its partner)
        foreach (var l in lights)
            Assert.True(endpoints.Any(x => x.Junction == l.Name && x.Mode == EndpointMode.Car && x.Generates), $"{l.Name} generates no cars");
        // one per junction, arm and mode
        foreach (var g in endpoints.Where(x => x.Mode is EndpointMode.Car or EndpointMode.Bike).GroupBy(x => (x.Junction, x.Arm, x.Mode)))
            Assert.Single(g);
        // no two car endpoints on one spot: a stretch between two lights has each junction's endpoint, apart
        var cars = endpoints.Where(x => x.Mode == EndpointMode.Car).ToList();
        for (int i = 0; i < cars.Count; i++)
            for (int k = i + 1; k < cars.Count; k++)
                Assert.True(Math.Sqrt(Math.Pow(cars[i].E - cars[k].E, 2) + Math.Pow(cars[i].N - cars[k].N, 2)) > 5, $"{cars[i].Junction} {cars[i].Arm} on {cars[k].Junction} {cars[k].Arm}");
        // J5a and J5b, 100 m apart: no endpoint on the stretch between them, and J5a's west generator sends cars on through J5b
        var j5a = region.Junction("J5a");
        var j5b = region.Junction("J5b");
        Assert.DoesNotContain(endpoints, x => x.N == j5a.N && x.E > j5a.E && x.E < j5b.E);
        var west = cars.Single(x => x.Junction == j5a.Name && x.Arm == "W");
        var j5bEast = cars.Single(x => x.Junction == j5b.Name && x.Arm == "E");
        Assert.Contains(west.Shares, s => s.To == j5bEast.Id);
        // the rate is the sum of the shares (the movements' demand)
        foreach (var x in cars.Where(x => x.Generates))
            Assert.InRange(x.Shares.Sum(s => s.Weight), x.PerMinute - 0.01f, x.PerMinute + 0.01f);
        // walkers in pairs over each crossing, trams at the tram lines' ends
        Assert.Contains(endpoints, x => x.Mode == EndpointMode.Walker && x.Junction == region.Junction("J7").Name);
        Assert.True(endpoints.Count(x => x.Mode == EndpointMode.Tram && x.Generates) >= 3);
    }

    [Fact]
    public void J5a_feeds_the_strip_to_J5b_no_more_than_J5b_lets_out_of_it()
    {
        // #1134: everything J5a's generators send on through the 100 m strip is capped at what J5b's west approach discharges
        var endpoints = TrafficEndpointsFile.Read(Path.Combine(region.Dir, TrafficEndpointsFile.FileName));
        var a = region.Junction("J5a");
        var b = region.Junction("J5b");
        var toB = endpoints.Where(x => x.Mode == EndpointMode.Car && x.Junction == b.Name).Select(x => x.Id).ToHashSet();
        float fed = endpoints.Where(x => x.Mode == EndpointMode.Car && x.Junction == a.Name)
            .Sum(x => x.Shares.Where(s => toB.Contains(s.To)).Sum(s => s.Weight));
        var lb = SignalTestRegion.ReadLights(region.Dir, SignalTestRegion.Junctions).Single(l => l.Name == b.Name);
        var flows = SignalCapacity.Cars(lb.Plan, k => lb.Approaches.TryGetValue(k, out var x) ? x.Lanes : null);
        int west = Enumerable.Range(0, lb.Plan.Arms.Count).MinBy(k => Math.Cos(lb.Plan.Arms[k].Heading));
        float discharge = flows.Where(f => f.From == west).Sum(f => f.PerMinute);
        Assert.True(fed > 0f);
        Assert.True(fed <= discharge + 0.05f, $"fed {fed:F2}/min into the strip, it passes {discharge:F2}/min");
        Assert.Equal(1f, SignalCapacity.StretchShare(5f, 10f));
        Assert.Equal(0.5f, SignalCapacity.StretchShare(20f, 10f), 3);
    }

    [Fact]
    public void A_route_through_J5a_and_J5b_is_fed_no_more_than_either_junction_passes_it()
    {
        // #1134, the user: "consider both junctions' capacity for paths that cross both": every route from a J5a arm through the
        // strip to a J5b arm (through and turning) at most its movement's demand at J5a and at J5b
        var endpoints = TrafficEndpointsFile.Read(Path.Combine(region.Dir, TrafficEndpointsFile.FileName)).Where(x => x.Mode == EndpointMode.Car).ToList();
        var lights = SignalTestRegion.ReadLights(region.Dir, SignalTestRegion.Junctions);
        var la = lights.Single(l => l.Name.StartsWith("J5a-")); var lb = lights.Single(l => l.Name.StartsWith("J5b-"));
        var fa = SignalCapacity.Cars(la.Plan, k => la.Approaches.TryGetValue(k, out var x) ? x.Lanes : null);
        var fb = SignalCapacity.Cars(lb.Plan, k => lb.Approaches.TryGetValue(k, out var x) ? x.Lanes : null);
        int ArmOf(SignalTestRegion.Lights l, string compass) => Enumerable.Range(0, l.Plan.Arms.Count).Single(k => Compass(l.Plan.Arms[k].Heading) == compass);
        int aEast = ArmOf(la, "E"), bWest = ArmOf(lb, "W");
        int routes = 0;
        foreach (var g in endpoints.Where(x => x.Junction == la.Name && x.Generates))
            foreach (var (to, w) in g.Shares)
            {
                var d = endpoints.Single(x => x.Id == to);
                if (d.Junction != lb.Name) continue;
                float first = fa.Where(f => f.From == ArmOf(la, g.Arm) && f.To == aEast).Sum(f => f.PerMinute);
                float second = fb.Where(f => f.From == bWest && f.To == ArmOf(lb, d.Arm)).Sum(f => f.PerMinute);
                Assert.True(w <= Math.Min(first, second) + 0.01f, $"J5a {g.Arm} -> J5b {d.Arm}: {w:F2}/min over min({first:F2}, {second:F2})");
                routes++;
            }
        Assert.True(routes >= 6, $"{routes} routes through both");
    }

    private static string Compass(double heading)
    {
        double deg = (heading * 180 / Math.PI % 360 + 360) % 360;
        return deg < 45 || deg >= 315 ? "E" : deg < 135 ? "N" : deg < 225 ? "W" : "S";
    }

    [Fact]
    public void The_endpoints_file_reads_back_what_was_written()
    {
        string path = Path.Combine(Path.GetTempPath(), "unitsport-1134-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            var written = new[]
            {
                new RegionEndpoint(1, EndpointMode.Car, 2910250.5, 1322500, 0.5f, true, true, 12.25f, [(2, 1.5f), (3, 2f)], "J1-ideal-lanes", "W"),
                new RegionEndpoint(2, EndpointMode.Walker, 2910260, 1322510.25, -1.5708f, false, true, 0f, [], "J1-ideal-lanes", "W left"),
            };
            TrafficEndpointsFile.Write(path, written);
            var read = TrafficEndpointsFile.Read(path);
            Assert.Equal(2, read.Count);
            Assert.Equal(written[0] with { Shares = [] }, read[0] with { Shares = [] });
            Assert.Equal(written[0].Shares, read[0].Shares);
            Assert.Equal("W left", read[1].Arm);
            Assert.Equal(EndpointMode.Walker, read[1].Mode);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>
/// Don't block the box (#1134, <see cref="BoxRule"/>): a car enters only with room on its exit past the cars on it or committed to
/// it, unless its approach or its exit arm is flagged <c>AllowBlock</c>; the flags survive the tile format.
/// </summary>
public class BoxRuleTests(SignalTestRegionFixture region) : IClassFixture<SignalTestRegionFixture>
{
    [Fact]
    public void The_strip_between_J5a_and_J5b_holds_as_many_cars_as_fit_and_no_more()
    {
        // eastbound out of J5a: the strip from J5a's core (its east arm's stop line, where the westbound stand) to J5b's west stop line
        var a = region.Junction("J5a");
        var b = region.Junction("J5b");
        var tile = region.Tile(a.E, a.N);
        var id = TileId.FromLv95(a.E, a.N);
        (double E, double N) At(RoadApproach x) => (id.MinE + x.X, id.MaxN - x.Z);
        var stops = tile.Approaches.Where(x => x.Signal >= 0).Select(At).Where(p => Math.Abs(p.N - a.N) < 8).ToList();
        double from = stops.Where(p => p.E > a.E && p.E < (a.E + b.E) / 2).Min(p => p.E);
        double to = stops.Where(p => p.E < b.E && p.E > (a.E + b.E) / 2).Max(p => p.E);
        float storage = (float)(to - from);
        Assert.InRange(storage, 20f, 100f);
        int fit = (int)(storage / BoxRule.CarRoom);
        Assert.True(BoxRule.Fits(storage, fit - 1));
        Assert.False(BoxRule.Fits(storage, fit));
        // a flag on the approach or on the exit lets the next one in anyway
        Assert.True(BoxRule.Fits(storage, fit, approachAllows: true));
        Assert.True(BoxRule.Fits(storage, fit, exitAllows: true));
        // a long free exit is free up to the reach
        Assert.True(BoxRule.Fits(1000f, (int)(BoxRule.Reach / BoxRule.CarRoom) - 1));
        Assert.False(BoxRule.Fits(1000f, (int)(BoxRule.Reach / BoxRule.CarRoom)));
    }

    [Fact]
    public void The_exit_is_walked_leg_by_leg_until_one_more_car_fits()
    {
        // the last leg before the next line full, room on an earlier one: it goes
        Assert.True(BoxRule.Fits([(12f, 0), (30f, 4)], openBeyond: false));
        // room only on the leg after a full one: the room is added up in order, it goes
        Assert.True(BoxRule.Fits([(15f, 2), (20f, 0)], openBeyond: false));
        // every leg full up to the line: it waits
        Assert.False(BoxRule.Fits([(15f, 2), (30f, 4), (8f, 1)], openBeyond: false));
        // full as far as its way is planned, the road going on: free beyond
        Assert.True(BoxRule.Fits([(15f, 2)], openBeyond: true));
    }

    [Fact]
    public void AllowBlock_on_an_approach_and_on_an_arm_survives_the_tile_format()
    {
        var src = region.Tile(region.Junction("J1").E, region.Junction("J1").N);
        var signal = src.Signals.Single();
        var plan = signal.Plan;
        plan.Arms[1] = plan.Arms[1] with { AllowBlock = true };
        var approaches = src.Approaches.Select((x, i) => i == 0 ? new RoadApproach
        {
            X = x.X, Y = x.Y, Z = x.Z, Heading = x.Heading, Signal = x.Signal, SignalArm = x.SignalArm, Banned = SignalMoves.Left,
            LaneCentre = x.LaneCentre, Lanes = x.Lanes, AllowBlock = true,
        } : x).ToList();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            RoadSignal.Write(w, src.Signals);
            RoadApproach.Write(w, approaches);
        }
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var signals = RoadSignal.Read(r)!;
        var read = RoadApproach.Read(r)!;
        Assert.True(signals[0].Plan.Arms[1].AllowBlock);
        Assert.Equal(plan.Arms[1].Rank, signals[0].Plan.Arms[1].Rank);
        Assert.False(signals[0].Plan.Arms[0].AllowBlock);
        Assert.True(read[0].AllowBlock);
        Assert.Equal(SignalMoves.Left, read[0].Banned);
        Assert.False(read[1].AllowBlock);
    }
}


/// <summary>Lane choice and lane changes (#1134, <see cref="LaneChoice"/>).</summary>
public class LaneChoiceTests
{
    [Fact]
    public void Two_through_lanes_fill_evenly()
    {
        var queued = new[] { 0, 0 };
        for (int car = 0; car < 20; car++)
        {
            int k = LaneChoice.Pick([(3f, queued[0]), (6f, queued[1])], SignalMoves.None, (uint)car * 2654435761u);
            queued[k]++;
        }
        Assert.Equal(10, queued[0]);
        Assert.Equal(10, queued[1]);
    }

    [Fact]
    public void A_car_turning_left_next_prefers_the_left_lane_unless_it_is_much_longer()
    {
        Assert.Equal(0, LaneChoice.Pick([(3f, 2), (6f, 2)], SignalMoves.Left, 0));
        Assert.Equal(1, LaneChoice.Pick([(3f, 2), (6f, 2)], SignalMoves.Right, 0));
        // the left lane two cars longer: the right one after all
        Assert.Equal(1, LaneChoice.Pick([(3f, 4), (6f, 2)], SignalMoves.Left, 0));
    }

    [Fact]
    public void Lanes_change_one_at_a_time_and_spaced()
    {
        // three lanes over, far from the line: one lane now
        float aim = LaneChoice.Aim(0f, -9.6f, float.NaN, 1000f, float.MaxValue, out bool starts);
        Assert.True(starts);
        Assert.Equal(-LaneChoice.LaneWidth, aim, 3);
        // that change under way: carried on, no new one
        aim = LaneChoice.Aim(-1f, -9.6f, -LaneChoice.LaneWidth, 10f, float.MaxValue, out starts);
        Assert.False(starts);
        Assert.Equal(-LaneChoice.LaneWidth, aim, 3);
        // done, but only 10 m since: it keeps its lane
        aim = LaneChoice.Aim(-LaneChoice.LaneWidth, -9.6f, -LaneChoice.LaneWidth, 10f, float.MaxValue, out starts);
        Assert.False(starts);
        Assert.Equal(-LaneChoice.LaneWidth, aim, 3);
        // the line near: it goes on at once, still one lane
        aim = LaneChoice.Aim(-LaneChoice.LaneWidth, -9.6f, -LaneChoice.LaneWidth, 10f, 40f, out starts);
        Assert.True(starts);
        Assert.Equal(-2 * LaneChoice.LaneWidth, aim, 3);
        // a taper's small drift: followed at once
        Assert.Equal(0.6f, LaneChoice.Aim(0f, 0.6f, float.NaN, 0f, 100f, out starts), 3);
        Assert.False(starts);
    }
}
