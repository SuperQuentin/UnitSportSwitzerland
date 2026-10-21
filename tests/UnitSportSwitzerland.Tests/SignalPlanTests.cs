using UnitSport.Terrain.Format;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Fixed-time signal plans (#349): the phase schemes agreed in #346 and the Swiss transitions.
/// Arms: 0 north, 1 east, 2 south, 3 west (outward headings). "NB" is traffic coming up from the
/// south arm, so its approach is arm 2.
/// </summary>
public class SignalPlanTests(ITestOutputHelper output)
{
    private const int N = 0, E = 1, S = 2, W = 3;
    private static readonly double[] Headings = [Math.PI / 2, 0, -Math.PI / 2, Math.PI];

    private static SignalArm Arm(int i, bool left = false, bool right = false, bool peds = true, bool bike = false,
        bool @in = true, bool @out = true) =>
        new(Headings[i], @in, @out, left, right, peds, bike);

    private static int Group(SignalPlan plan, SignalGroupKind kind, int arm) =>
        plan.Groups.FindIndex(g => g.Kind == kind && g.Arm == arm);

    /// <summary>Times (cycle position, 0.1 s steps) at which every listed group is green.</summary>
    private static List<double> WhenGreen(SignalPlan plan, params int[] groups)
    {
        var list = new List<double>();
        for (double t = 0.05; t < plan.Cycle; t += 0.1)
            if (groups.All(g => plan.State(g, t - plan.Offset) == SignalAspect.Green)) list.Add(t);
        return list;
    }

    /// <summary>Separate greens in one cycle (a green across the cycle's seam counts once).</summary>
    private static int GreenRuns(SignalPlan plan, int g)
    {
        var list = plan.Groups[g].Intervals;
        int runs = list.Count(iv => iv.Aspect == SignalAspect.Green);
        if (list.Count > 1 && list[0].Aspect == SignalAspect.Green && list[^1].Aspect == SignalAspect.Green) runs--;
        return runs;
    }

    private void AssertValid(SignalPlan plan)
    {
        output.WriteLine(plan.Describe());
        var errors = plan.Validate();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void FullCrossroads_RunsTheSixPhases()
    {
        var plan = SignalPlan.Build([Arm(N, true, true), Arm(E, true, true), Arm(S, true, true), Arm(W, true, true)], seed: 7);
        AssertValid(plan);
        int nbCar = Group(plan, SignalGroupKind.Car, S), nbLeft = Group(plan, SignalGroupKind.LeftArrow, S);
        int sbCar = Group(plan, SignalGroupKind.Car, N), sbLeft = Group(plan, SignalGroupKind.LeftArrow, N);
        int ebRight = Group(plan, SignalGroupKind.RightArrow, W), wbRight = Group(plan, SignalGroupKind.RightArrow, E);
        int nbRight = Group(plan, SignalGroupKind.RightArrow, S), sbRight = Group(plan, SignalGroupKind.RightArrow, N);
        int wbCar = Group(plan, SignalGroupKind.Car, E), wbLeft = Group(plan, SignalGroupKind.LeftArrow, E);
        int pedN = Group(plan, SignalGroupKind.Pedestrian, N), pedE = Group(plan, SignalGroupKind.Pedestrian, E);
        int pedS = Group(plan, SignalGroupKind.Pedestrian, S), pedW = Group(plan, SignalGroupKind.Pedestrian, W);

        // 1. NB through + left (leading), EB right overlap, east pedestrians
        var p1 = WhenGreen(plan, nbCar, nbLeft);
        Assert.NotEmpty(p1);
        foreach (double t in p1)
        {
            Assert.Equal(SignalAspect.Green, plan.State(ebRight, t - plan.Offset));
            Assert.Equal(SignalAspect.Green, plan.State(pedE, t - plan.Offset));
            Assert.Equal(SignalAspect.Red, plan.State(sbCar, t - plan.Offset));
            Assert.Equal(SignalAspect.Red, plan.State(pedW, t - plan.Offset));
        }
        // 2. both through, east and west pedestrians
        var p2 = WhenGreen(plan, nbCar, sbCar);
        Assert.NotEmpty(p2);
        foreach (double t in p2)
        {
            Assert.Equal(SignalAspect.Green, plan.State(pedE, t - plan.Offset));
            Assert.Equal(SignalAspect.Green, plan.State(pedW, t - plan.Offset));
            Assert.NotEqual(SignalAspect.Green, plan.State(nbLeft, t - plan.Offset));
            Assert.NotEqual(SignalAspect.Green, plan.State(sbLeft, t - plan.Offset));
        }
        // 3. SB through + left (lagging), WB right overlap, west pedestrians
        var p3 = WhenGreen(plan, sbCar, sbLeft);
        Assert.NotEmpty(p3);
        foreach (double t in p3)
        {
            Assert.Equal(SignalAspect.Green, plan.State(wbRight, t - plan.Offset));
            Assert.Equal(SignalAspect.Green, plan.State(pedW, t - plan.Offset));
            Assert.Equal(SignalAspect.Red, plan.State(pedE, t - plan.Offset));
        }
        // 4. rotated: the east-west road leads with one of its approaches, with an overlap from the north-south road
        var p4 = WhenGreen(plan, wbCar, wbLeft);
        Assert.NotEmpty(p4);
        Assert.Contains(p4, t => plan.State(nbRight, t - plan.Offset) == SignalAspect.Green || plan.State(sbRight, t - plan.Offset) == SignalAspect.Green);
        Assert.NotEmpty(WhenGreen(plan, pedN));
        Assert.NotEmpty(WhenGreen(plan, pedS));
        // the lead's through carries on into the both-ways phase: one green run per cycle, no yellow between
        Assert.Equal(1, GreenRuns(plan, nbCar));
        Assert.Equal(1, GreenRuns(plan, sbCar));
        Assert.InRange(plan.Cycle, 60, SignalPlan.MaxCycle);
    }

    [Fact]
    public void NoPockets_BothDirectionsTogether_LeftsPermissive()
    {
        var plan = SignalPlan.Build([Arm(N), Arm(E), Arm(S), Arm(W)]);
        AssertValid(plan);
        Assert.DoesNotContain(plan.Groups, g => g.Kind is SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow);
        int nb = Group(plan, SignalGroupKind.Car, S), sb = Group(plan, SignalGroupKind.Car, N);
        int eb = Group(plan, SignalGroupKind.Car, W), wb = Group(plan, SignalGroupKind.Car, E);
        Assert.Equal(WhenGreen(plan, nb), WhenGreen(plan, sb));
        Assert.Empty(WhenGreen(plan, nb, eb));
        Assert.NotEmpty(WhenGreen(plan, eb, wb));
        // turns across the pedestrian green get the flashing yellow lamp
        int flasher = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Flasher && g.Qualifies == nb);
        Assert.True(flasher >= 0);
        Assert.Contains(WhenGreen(plan, nb), t => plan.State(flasher, t - plan.Offset) == SignalAspect.FlashingAmber);
        Assert.Equal(60, plan.Cycle, 1);
    }

    [Fact]
    public void PocketOnOneSide_OnlyThatSideIsProtected()
    {
        var plan = SignalPlan.Build([Arm(N), Arm(E), Arm(S, left: true), Arm(W)]);
        AssertValid(plan);
        int nbLeft = Group(plan, SignalGroupKind.LeftArrow, S), sb = Group(plan, SignalGroupKind.Car, N);
        Assert.True(nbLeft >= 0);
        Assert.Equal(-1, Group(plan, SignalGroupKind.LeftArrow, N));
        Assert.Empty(WhenGreen(plan, nbLeft, sb));
        // the southbound left turn goes, yielding, with the through phase
        Assert.Equal(SignalMoves.Left | SignalMoves.Through | SignalMoves.Right, plan.Groups[sb].Moves);
    }

    [Fact]
    public void RightPocketWithoutOverlap_GoesWithItsApproachAndTheFlasher()
    {
        // no left pocket anywhere: no leading phase for the right arrows to overlap
        var plan = SignalPlan.Build([Arm(N), Arm(E), Arm(S, right: true), Arm(W)]);
        AssertValid(plan);
        Assert.DoesNotContain(plan.Groups, g => g.Kind == SignalGroupKind.RightArrow);
        int lane = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == S && g.Moves == SignalMoves.Right);
        int nb = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == S && g.Moves != SignalMoves.Right);
        Assert.True(lane >= 0 && nb >= 0);
        Assert.Equal(WhenGreen(plan, nb), WhenGreen(plan, lane));
        Assert.Contains(plan.Groups, g => g.Kind == SignalGroupKind.Flasher && g.Qualifies == lane);
    }

    [Fact]
    public void TJunction_ThreePhases()
    {
        // main road east-west, side road south; westbound (from the east arm) turns left into it
        var plan = SignalPlan.Build([Arm(E, left: true), Arm(S, right: true), Arm(W)]);
        AssertValid(plan);
        int wb = Group(plan, SignalGroupKind.Car, 0), wbLeft = Group(plan, SignalGroupKind.LeftArrow, 0);
        int eb = Group(plan, SignalGroupKind.Car, 2), side = Group(plan, SignalGroupKind.Car, 1);
        int sideRight = Group(plan, SignalGroupKind.RightArrow, 1);
        Assert.True(wbLeft >= 0 && side >= 0 && sideRight >= 0);
        Assert.NotEmpty(WhenGreen(plan, wb, eb));
        Assert.NotEmpty(WhenGreen(plan, wb, wbLeft));
        Assert.Empty(WhenGreen(plan, eb, wbLeft));
        Assert.Empty(WhenGreen(plan, side, wb));
        // the side road's right turn joins the eastbound road: it overlaps the westbound left-turn
        // phase, never the eastbound through it would merge into
        Assert.Contains(WhenGreen(plan, wb, wbLeft), t => plan.State(sideRight, t - plan.Offset) == SignalAspect.Green);
        Assert.Empty(WhenGreen(plan, sideRight, eb));
    }

    [Fact]
    public void OneWayArm_AndFiveArms_StillValid()
    {
        AssertValid(SignalPlan.Build([Arm(N, @in: false), Arm(E, left: true), Arm(S, left: true), Arm(W, left: true)]));
        AssertValid(SignalPlan.Build([Arm(N, @out: false), Arm(E), Arm(S), Arm(W)]));
        var five = new List<SignalArm>(Enumerable.Range(0, 5).Select(i => new SignalArm(i * 2 * Math.PI / 5, true, true, Pedestrians: true)));
        AssertValid(SignalPlan.Build(five));
    }

    [Fact]
    public void TwoLensPedestrianHeads_GoStraightToRed()
    {
        var plan = SignalPlan.Build([Arm(N), Arm(E), Arm(S), Arm(W)], pedestrianAmber: false);
        AssertValid(plan);
        int ped = Group(plan, SignalGroupKind.Pedestrian, E);
        Assert.DoesNotContain(plan.Groups[ped].Intervals, iv => iv.Aspect == SignalAspect.Amber);
        var withAmber = SignalPlan.Build([Arm(N), Arm(E), Arm(S), Arm(W)]);
        Assert.Contains(withAmber.Groups[Group(withAmber, SignalGroupKind.Pedestrian, E)].Intervals, iv => iv.Aspect == SignalAspect.Amber);
    }

    [Fact]
    public void Bikes_LeadTheirApproach_OnlyWhereACarTurnsAcrossThem_AndNeverMeetTheRightArrow()
    {
        // a right arrow of its own holds the turning cars: the riders are protected, no lead (the user, #979)
        var plan = SignalPlan.Build([Arm(N, true, true, bike: true), Arm(E, true, true), Arm(S, true, true, bike: true), Arm(W, true, true)]);
        AssertValid(plan);
        int bike = Group(plan, SignalGroupKind.Bike, S), car = Group(plan, SignalGroupKind.Car, S);
        int right = Group(plan, SignalGroupKind.RightArrow, S);
        Assert.Empty(WhenGreen(plan, bike, right));
        Assert.Equal(WhenGreen(plan, bike).Count, WhenGreen(plan, bike, car).Count);
        // the cars turning right from the riders' own green: the riders lead them
        var turning = SignalPlan.Build([Arm(N), Arm(E), Arm(S, bike: true), Arm(W)]);
        AssertValid(turning);
        int bike2 = Group(turning, SignalGroupKind.Bike, S), car2 = Group(turning, SignalGroupKind.Car, S);
        Assert.True(WhenGreen(turning, bike2).Count > WhenGreen(turning, bike2, car2).Count, "the bike green starts before the car's");
    }

    [Fact]
    public void KerbsideBikeLane_IsHeldFromTheRightTurn_OnlyByARealPhase()
    {
        // left pockets everywhere: the right arrows have their own phases, the bike green runs with them red
        var held = SignalPlan.Build([Arm(N, true, true), Arm(E, true, true), Arm(S, true, true, bike: true), Arm(W, true, true)]);
        AssertValid(held);
        Assert.True(held.ThroughWithRightHeld(S));
        // no left pocket: the right pocket goes green with its approach; the bike's 3 s lead is no phase (#351: layout (b))
        var with = SignalPlan.Build([Arm(N), Arm(E), Arm(S, right: true, bike: true), Arm(W)]);
        AssertValid(with);
        Assert.False(with.ThroughWithRightHeld(S));
    }

    [Fact]
    public void State_IsAFunctionOfTheClock()
    {
        var a = SignalPlan.Build([Arm(N, true), Arm(E), Arm(S, true), Arm(W)], seed: 12345);
        var b = SignalPlan.Build([Arm(N, true), Arm(E), Arm(S, true), Arm(W)], seed: 12345);
        Assert.Equal(a.Offset, b.Offset);
        for (double t = 1000; t < 1000 + 2 * a.Cycle; t += 0.37)
            for (int g = 0; g < a.Groups.Count; g++)
            {
                Assert.Equal(a.State(g, t), b.State(g, t));
                Assert.Equal(a.State(g, t), a.State(g, t + a.Cycle));
                double until = a.UntilChange(g, t);
                if (!double.IsInfinity(until) && until > 0.02)
                    Assert.Equal(a.State(g, t), a.State(g, t + until - 0.01));
            }
    }
    [Fact]
    public void RoadTile_RoundTripsTheSignalSection()
    {
        var plan = SignalPlan.Build([Arm(N, true, true, bike: true) with { BikeRightOnRed = true }, Arm(E), Arm(S, true), Arm(W, right: true)], seed: 99, pedestrianAmber: false);
        plan.ArrowPlates = true;   // bit 1 of the pedestrian byte, beside bit 0 (#759)
        var tile = new RoadTile
        {
            Id = new TileId(2500, 1117),
            Segments = [new RoadSegment { Class = RoadClass.Road, Width = 6, Points = [0, 400, 0, 10, 400, 0] }],
            Signals = [new RoadSignal { X = 5, Y = 400.1f, Z = 6, Stops = Enumerable.Range(0, 12).Select(i => (float)i).ToArray(), Plan = plan }],
        };
        using var ms = new MemoryStream();
        RoadCodec.Encode(tile, ms);
        ms.Position = 0;
        var back = RoadCodec.Decode(ms);
        var p = Assert.Single(back.Signals).Plan;
        Assert.Equal(plan.Cycle, p.Cycle);
        Assert.Equal(plan.Offset, p.Offset);
        Assert.False(p.PedestrianAmber);
        Assert.True(p.ArrowPlates);
        Assert.Equal(plan.Arms.Count, p.Arms.Count);
        Assert.Equal([true, false, false, false], p.Arms.Select(a => a.BikeRightOnRed));   // bit 6 of the arm byte (#711)
        Assert.Equal(plan.Groups.Count, p.Groups.Count);
        Assert.Equal(11f, back.Signals[0].Stops[11]);
        for (double t = 0; t < 2 * plan.Cycle; t += 0.25)
            for (int g = 0; g < plan.Groups.Count; g++)
                Assert.Equal(plan.State(g, t), p.State(g, t));
        Assert.Empty(p.Validate());
    }
    [Theory]
    [InlineData(187, -135, 45)]
    [InlineData(176, -37, 145)]
    public void SkewedT_FromGeneva_StillValid(double a, double b, double c)
    {
        static SignalArm Deg(double d) => new(d * Math.PI / 180, true, true, Pedestrians: true);
        AssertValid(SignalPlan.Build([Deg(a), Deg(b), Deg(c)]));
    }
    [Fact]
    public void ArmWithNoTraffic_FromGeneva_StillValid()
    {
        static SignalArm Deg(double d, bool i, bool o) => new(d * Math.PI / 180, i, o, Pedestrians: true);
        AssertValid(SignalPlan.Build([Deg(198, false, false), Deg(-84, true, true), Deg(5, false, true), Deg(95, true, false)]));
        // the same with a right pocket on the approach that can only turn right
        AssertValid(SignalPlan.Build([Deg(198, false, false), Deg(-84, true, true) with { RightPocket = true }, Deg(5, false, true), Deg(95, true, false)]));
    }

    /// <summary>A crossroads with every pocket, the east arm's walkers crossing its 10 m road in two stages (#1030), 3.5 m each way to the island.</summary>
    private static SignalPlan TwoStageCrossroads(uint seed = 7) => SignalPlan.Build([Arm(N, true, true),
        Arm(E, true, true) with { CrossingM = 10, HalfInM = 3.5f, HalfOutM = 3.5f }, Arm(S, true, true), Arm(W, true, true)], seed);

    private static int Half(SignalPlan plan, int arm, CrossingHalf half) =>
        plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Pedestrian && g.Arm == arm && g.Half == half);

    [Fact]
    public void TwoStageCrossing_EachHalfIsStoppedOnlyByItsOwnLanes_AndClearsOnlyItself()
    {
        var plan = TwoStageCrossroads();
        AssertValid(plan);
        int arriving = Half(plan, E, CrossingHalf.Arriving), departing = Half(plan, E, CrossingHalf.Departing);
        Assert.True(arriving >= 0 && departing >= 0);
        Assert.Equal(-1, Half(plan, E, CrossingHalf.Whole));
        var c = plan.Conflicts();
        // westbound traffic comes from the east arm (its approach), eastbound goes into it
        // (the right arrow no phase carries here is a full green with its approach)
        var westbound = Enumerable.Range(0, plan.Groups.Count).Where(g => plan.Groups[g].Arm == E
            && plan.Groups[g].Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow).ToList();
        Assert.Equal(3, westbound.Count);
        int ebCar = Group(plan, SignalGroupKind.Car, W);
        foreach (int g in westbound)
        {
            Assert.Equal(SignalPlan.Conflict.Hard, c[arriving, g]);
            Assert.Equal(SignalPlan.Conflict.None, c[departing, g]);
        }
        Assert.Equal(SignalPlan.Conflict.None, c[arriving, ebCar]);
        Assert.Equal(SignalPlan.Conflict.Hard, c[departing, ebCar]);
        // both halves green together, long enough to walk kerb to kerb at once (10 m at 1.2 m/s)
        Assert.True(WhenGreen(plan, arriving, departing).Count * 0.1 >= 5, "the halves never green together");
        // each half's clearance its own 3.5 m (2/3 at 1.2 m/s: the 2 s floor), not the whole road's 5.6 s
        foreach (int g in (int[])[arriving, departing])
            Assert.All(plan.Groups[g].Intervals.Where(iv => iv.Aspect == SignalAspect.Amber), iv => Assert.Equal(2f, iv.To - iv.From, 0.05f));
    }

    [Theory]
    [InlineData(7u)]
    [InlineData(99u)]
    public void TwoStageCrossing_TheIslandWaitIsAtMostAThirdOfTheCycle(uint seed)
    {
        var plan = TwoStageCrossroads(seed);
        AssertValid(plan);
        var waits = plan.IslandWaits();
        foreach (var w in waits) output.WriteLine($"group {w.Group} green {w.Green:F1}, on the island {w.Arrive:F1}, waits {w.Wait:F1} s for {w.Other}");
        Assert.NotEmpty(waits);
        Assert.All(waits, w => Assert.True(w.Wait <= plan.Cycle * SignalPlan.IslandWaitShare + 0.1f, $"waits {w.Wait:F1} s of a {plan.Cycle:F1} s cycle"));
    }

    /// <summary>
    /// Two Geneva junctions (LV95 2499863,1118279 and 2499652,1118687, #1030) whose arm 3 crosses in two stages: their first
    /// walkers missed the other half's green by tenths of a second, and the cycle grew to 177 s chasing the wait. Now that
    /// green holds on for them: the waits within a third, the cycle (about) the one-stage plan's.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TwoStageCrossing_FromGeneva_KeepsTheWaitWithoutLengtheningTheCycle(int which)
    {
        static SignalArm Deg(double d, bool i, bool o, float crossing, bool peds = true, bool left = false, byte rank = 18, bool bike = false) =>
            new(d * Math.PI / 180, i, o, LeftPocket: left, Pedestrians: peds, BikeSignal: bike, CrossingM: crossing, Rank: rank);
        List<SignalArm> arms = which == 0
            ? [Deg(230, false, true, 10), Deg(-11, true, true, 13.2f, left: true), Deg(46, true, true, 10, rank: 2), Deg(168, true, true, 16.5f)]
            : [Deg(-121, false, true, 8, peds: false), Deg(326, true, true, 13.5f, left: true, bike: true), Deg(59, true, true, 5.8f, rank: 2),
                Deg(130, true, true, 12.1f, left: true, bike: true)];
        var one = SignalPlan.Build(arms, 7);
        arms[3] = arms[3] with { HalfInM = arms[3].CrossingM * 0.45f, HalfOutM = arms[3].CrossingM * 0.4f };
        var two = SignalPlan.Build(arms, 7);
        AssertValid(two);
        output.WriteLine($"one stage {one.Cycle:F1} s, two stages {two.Cycle:F1} s");
        foreach (var w in two.IslandWaits()) output.WriteLine($"group {w.Group} green {w.Green:F1}, on the island {w.Arrive:F1}, waits {w.Wait:F1} s");
        Assert.All(two.IslandWaits(), w => Assert.True(w.Wait <= two.Cycle * SignalPlan.IslandWaitShare + 0.1f, $"waits {w.Wait:F1} s of {two.Cycle:F1}"));
        // an island makes the cycle 3 s longer at most (the user)
        Assert.InRange(two.Cycle, 0, one.Cycle + SignalPlan.IslandGrowMost + 0.05f);
        // and timed from conflict times too (Geneva: a stretch left the arriving half green all cycle, the plan invalid)
        var conflicts = two.Conflicts();
        int n = two.Groups.Count;
        var times = new float[n, n];
        for (int g = 0; g < n; g++)
            for (int h = 0; h < n; h++)
                times[g, h] = conflicts[g, h] == SignalPlan.Conflict.Hard ? (two.Groups[g].Kind == SignalGroupKind.Pedestrian ? 6f : 3f) : float.NaN;
        bool timed = two.Retime(times);
        output.WriteLine($"timed {timed}\n{two.Describe()}");
        Assert.True(timed, "timed from conflict times");
        Assert.All(two.IslandWaits(), w => Assert.True(w.Wait <= two.Cycle * SignalPlan.IslandWaitShare + 0.1f, $"timed: waits {w.Wait:F1} s of {two.Cycle:F1}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RidersInABikeBox_NeverEndBeforeTheCarsBehindThem(bool box)
    {
        // the riders turning left wait in a bike box in front of the pocket's cars: an early end of their green would have the
        // cars run into them from behind; an early start is fine (the user). Their slow clearing (8 s against the cars' 3 s)
        // ends a group of their own early only without a box.
        var arms = new List<SignalArm> { Arm(N, true, true), Arm(E, true, true), Arm(S, true, true), Arm(W, true, true) };
        arms[W] = arms[W] with { RiderLeft = true, RiderBox = box };
        var plan = SignalPlan.Build(arms, 7);
        int riders = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Bike && g.Arm == W && g.Moves == SignalMoves.Left);
        int cars = Group(plan, SignalGroupKind.LeftArrow, W);
        Assert.True(riders >= 0 && cars >= 0);
        var conflicts = plan.Conflicts();
        int n = plan.Groups.Count;
        var times = new float[n, n];
        for (int g = 0; g < n; g++)
            for (int h = 0; h < n; h++)
                times[g, h] = conflicts[g, h] == SignalPlan.Conflict.Hard ? (g == riders ? 8f : 3f) : float.NaN;
        Assert.True(plan.Retime(times), "retimed");
        AssertValid(plan);
        float End(int g) => plan.Groups[g].Intervals.Where(v => v.Aspect == SignalAspect.Green).Select(v => v.To).First();
        float Start(int g) => plan.Groups[g].Intervals.Where(v => v.Aspect == SignalAspect.Green).Select(v => v.From).First();
        output.WriteLine($"riders green {Start(riders):F1}-{End(riders):F1}, cars {Start(cars):F1}-{End(cars):F1}");
        if (box) Assert.True(End(riders) >= End(cars) - 0.05f, "the box's riders end before the cars behind them");
        else Assert.True(End(riders) < End(cars) - 0.5f, "riders on their own lane end early to clear");
        Assert.True(Start(riders) <= Start(cars) + 0.05f, "the riders never start after their cars");
    }

    /// <summary>
    /// Geneva's 10-arm node at LV95 2500974,1117838 (#1030): split phasing ran its 9 approaches one by one, 5 s greens, to 157 s
    /// once timed from conflict times. 120 s is the legal maximum (the user): approaches that never conflict share a phase.
    /// </summary>
    [Fact]
    public void ManyArms_StayWithinTheLegalMaximum()
    {
        static SignalArm Deg(double d, bool i, float crossing, byte rank = 1, bool peds = false, float kmh = 60) =>
            new(d * Math.PI / 180, i, true, Pedestrians: peds, CrossingM: crossing, Rank: rank, SpeedKmh: kmh);
        var plan = SignalPlan.Build([Deg(211, true, 4.5f), Deg(214, true, 12, 34, true, 50), Deg(-54, true, 3), Deg(34, false, 13.5f, 34),
            Deg(124, true, 3), Deg(34, true, 4.5f), Deg(33, true, 6, 2), Deg(125, true, 3), Deg(-56, true, 3), Deg(-147, true, 7.5f, 2, true, 50)], 7);
        AssertValid(plan);
        Assert.InRange(plan.Cycle, 0, SignalPlan.MaxCycle);
        var conflicts = plan.Conflicts();
        int n = plan.Groups.Count;
        var times = new float[n, n];
        for (int g = 0; g < n; g++)
            for (int h = 0; h < n; h++)
                times[g, h] = conflicts[g, h] == SignalPlan.Conflict.Hard ? (plan.Groups[g].Kind == SignalGroupKind.Pedestrian ? 9f : 5f) : float.NaN;
        bool timed = plan.Retime(times);
        output.WriteLine($"timed {timed}\n{plan.Describe()}");
        AssertValid(plan);
        Assert.InRange(plan.Cycle, 0, SignalPlan.MaxCycle);
    }

    [Fact]
    public void TwoStageCrossing_RoundTripsItsHalves()
    {
        var plan = TwoStageCrossroads();
        var tile = new RoadTile
        {
            Id = new TileId(2500, 1117),
            Segments = [new RoadSegment { Class = RoadClass.Road, Width = 6, Points = [0, 400, 0, 10, 400, 0] }],
            Signals = [new RoadSignal { X = 5, Y = 400.1f, Z = 6, Stops = new float[12], Plan = plan }],
        };
        using var ms = new MemoryStream();
        RoadCodec.Encode(tile, ms);
        ms.Position = 0;
        var p = Assert.Single(RoadCodec.Decode(ms).Signals).Plan;
        Assert.Equal(plan.Groups.Select(g => (g.Kind, g.Moves, g.Half)), p.Groups.Select(g => (g.Kind, g.Moves, g.Half)));
        Assert.Empty(p.Validate());
    }
}
