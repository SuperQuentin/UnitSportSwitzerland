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

    /// <summary>Seconds a group is green per cycle.</summary>
    private static float GreenFor(SignalPlan plan, int g) =>
        plan.Groups[g].Intervals.Where(iv => iv.Aspect == SignalAspect.Green).Sum(iv => iv.To - iv.From);

    [Fact]
    public void TurnPhase_ClearsItsPocketsQueue()
    {
        // #982: a turn phase's green clears its pocket's full queue, the start-up loss then a car per 6 m every 2 s
        SignalArm Pocket(int i, float storage) => Arm(i, left: true) with { LeftStorageM = storage };
        var shortPocket = SignalPlan.Build([Pocket(N, 18), Arm(E), Pocket(S, 18), Arm(W)]);
        var longPocket = SignalPlan.Build([Pocket(N, 36), Arm(E), Pocket(S, 36), Arm(W)]);
        AssertValid(shortPocket);
        AssertValid(longPocket);
        Assert.Equal(8f, GreenFor(shortPocket, Group(shortPocket, SignalGroupKind.LeftArrow, S)), 1);   // 2 + 3 cars x 2
        Assert.Equal(14f, GreenFor(longPocket, Group(longPocket, SignalGroupKind.LeftArrow, S)), 1);    // 2 + 6 cars x 2
        // what a pocket needs past the turn phase's budget lengthens the cycle; the through phases keep their green
        Assert.Equal(2 * (14f - 8f), longPocket.Cycle - shortPocket.Cycle, 1);
        Assert.Equal(GreenFor(shortPocket, Group(shortPocket, SignalGroupKind.Car, E)), GreenFor(longPocket, Group(longPocket, SignalGroupKind.Car, E)), 1);
        // a turn lane the whole street long clears no more than the longest pocket holds
        var lane = SignalPlan.Build([Pocket(N, 400), Arm(E), Pocket(S, 400), Arm(W)]);
        Assert.Equal(SignalPlan.ClearingGreen(SignalPlan.MaxTurnStorage), GreenFor(lane, Group(lane, SignalGroupKind.LeftArrow, S)), 1);
        // unknown storage: the budget alone
        var unknown = SignalPlan.Build([Arm(N, left: true), Arm(E), Arm(S, left: true), Arm(W)]);
        Assert.Equal(SignalPlan.TurnPhaseGreen, GreenFor(unknown, Group(unknown, SignalGroupKind.LeftArrow, S)), 1);
    }

    [Fact]
    public void ShortStreets_CapTheirGreens_AndShortenTheCycle()
    {
        // #982: a phase gets no more green than clearing what its lanes store; the time it saves shortens the cycle (the user)
        SignalArm Street(int i, float storage) => Arm(i) with { StorageM = storage };
        var open = SignalPlan.Build([Arm(N), Arm(E), Arm(S), Arm(W)]);
        var capped = SignalPlan.Build([Street(N, 300), Street(E, 24), Street(S, 300), Street(W, 24)]);
        AssertValid(capped);
        Assert.Equal(60, open.Cycle, 1);
        int ew = Group(capped, SignalGroupKind.Car, E), ns = Group(capped, SignalGroupKind.Car, N);
        Assert.Equal(10f, GreenFor(capped, ew), 1);   // 2 + 4 cars x 2
        Assert.Equal(GreenFor(open, Group(open, SignalGroupKind.Car, N)), GreenFor(capped, ns), 1);
        Assert.Equal(open.Cycle - (GreenFor(open, Group(open, SignalGroupKind.Car, E)) - 10f), capped.Cycle, 1);
        // every street a car long: the minimum cycle, shared out
        var tiny = SignalPlan.Build([Street(N, 6), Street(E, 6), Street(S, 6), Street(W, 6)]);
        AssertValid(tiny);
        Assert.Equal(SignalPlan.MinCycle, tiny.Cycle, 1);
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
        // no left pocket anywhere: no leading phase for the right arrows to overlap; protected with its approach, the east arm's
        // walkers it crosses would be green in no phase (they cross the east-west traffic in the other): the one case it stays
        // permissive (#1119, without adding a phase)
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
    [Fact]
    public void RoadTile_RoundTripsTheOverheadsAndThePoleColour()
    {
        // #1037: the canton's pole colour in bits 3-5 of the plan's flag byte, the overhead structures in OVHD after SGNL
        var plan = SignalPlan.Build([Arm(N, true, true), Arm(E), Arm(S, true), Arm(W)], seed: 7, pedestrianAmber: true);
        plan.ArrowPlates = true;
        plan.PoleColour = SignalPoleColour.TrafficGrey;
        var signal = new RoadSignal { X = 5, Y = 400, Z = 6, Stops = new float[12], Plan = plan };
        signal.Overheads.Add(new SignalOverhead
        {
            Kind = OverheadKind.Portal, Arm = 2, X = 1, Y = 400.1f, Z = 2, EndX = 13, EndY = 400.2f, EndZ = 2.5f, Bottom = 405, Heading = 1.5f,
            Heads = [new OverheadHead(3.4f, SignalMoves.Through | SignalMoves.Right), new OverheadHead(9.4f, SignalMoves.Left)],
        });
        var tile = new RoadTile
        {
            Id = new TileId(2500, 1117),
            Segments = [new RoadSegment { Class = RoadClass.Road, Width = 6, Points = [0, 400, 0, 10, 400, 0] }],
            Signals = [signal],
        };
        using var ms = new MemoryStream();
        RoadCodec.Encode(tile, ms);
        ms.Position = 0;
        var back = Assert.Single(RoadCodec.Decode(ms).Signals);
        Assert.Equal((SignalPoleColour.TrafficGrey, true, true), (back.Plan.PoleColour, back.Plan.ArrowPlates, back.Plan.PedestrianAmber));
        var o = Assert.Single(back.Overheads);
        Assert.Equal((OverheadKind.Portal, (byte)2, 1f, 400.1f, 2f, 13f, 400.2f, 2.5f, 405f, 1.5f),
            (o.Kind, o.Arm, o.X, o.Y, o.Z, o.EndX, o.EndY, o.EndZ, o.Bottom, o.Heading));
        Assert.Equal(signal.Overheads[0].Heads, o.Heads);
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

    [Fact]
    public void LoneBikeLaneIntoABox_OneRidersGroupWithEveryMove()
    {
        // #1184 (the user, J8 N): a T's stem with one bike lane serving its riders' left and right into a bike box: one bike group
        // with both moves (no right-only one), going with the stem's cars, never ending before them (the riders wait in front)
        var arms = new List<SignalArm> { Arm(N) with { RiderAll = true, RiderBox = true, RiderRight = true }, Arm(E), Arm(W) };
        var plan = SignalPlan.Build(arms, 7);
        AssertValid(plan);
        var riders = plan.Groups.Select((g, k) => (g, k)).Where(p => p.g.Kind == SignalGroupKind.Bike && p.g.Arm == 0).ToList();
        Assert.Single(riders);
        Assert.Equal(SignalMoves.Left | SignalMoves.Right, riders[0].g.Moves);
        Assert.True(SignalPlan.BoxRiders(riders[0].g));
        int box = riders[0].k, cars = Group(plan, SignalGroupKind.Car, 0);
        Assert.NotEmpty(WhenGreen(plan, box, cars));
        var conflicts = plan.Conflicts();
        int n = plan.Groups.Count;
        var times = new float[n, n];
        for (int g = 0; g < n; g++)
            for (int h = 0; h < n; h++)
                times[g, h] = conflicts[g, h] == SignalPlan.Conflict.Hard ? (g == box ? 8f : 3f) : float.NaN;
        Assert.True(plan.Retime(times), "retimed");
        AssertValid(plan);
        float End(int g) => plan.Groups[g].Intervals.Where(v => v.Aspect == SignalAspect.Green).Select(v => v.To).First();
        Assert.True(End(box) >= End(cars) - 0.05f, "the box's riders end before the cars behind them");

        // on a crossroads the box's riders turning left give way to the oncoming traffic as the cars behind them do: they share
        // the oncoming cars' green; turning they give way to the walkers too
        var cross = new List<SignalArm> { Arm(N) with { RiderAll = true, RiderBox = true }, Arm(E), Arm(S), Arm(W) };
        var four = SignalPlan.Build(cross, 7);
        AssertValid(four);
        int all = four.Groups.FindIndex(g => g.Kind == SignalGroupKind.Bike && g.Arm == N);
        Assert.Equal(SignalMoves.Left | SignalMoves.Through | SignalMoves.Right, four.Groups[all].Moves);
        Assert.NotEmpty(WhenGreen(four, all, Group(four, SignalGroupKind.Car, S)));
        Assert.Equal(SignalPlan.Conflict.Permissive, four.Conflicts()[all, Group(four, SignalGroupKind.Pedestrian, E)]);
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

    // ---- flasher plates and permissive lefts (#1109) ----------------------------------------------

    private static int FlasherOf(SignalPlan plan, int group) =>
        plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Flasher && g.Qualifies == group);

    [Fact]
    public void FlasherPlate_WalkersOnly_APedestrian()
    {
        // no bikes: the northbound right turn crosses only the east crossing green with it: a walker. Its left turn crosses the west
        // one and gives way to the oncoming cars: a mix, the turn's arrow (#1117, the user: "bike if only bike, pedestrian if only
        // pedestrian, arrow if mix of conflicts")
        var plan = SignalPlan.Build([Arm(N), Arm(E), Arm(S), Arm(W)]);
        int nb = Group(plan, SignalGroupKind.Car, S);
        Assert.Equal((SignalPlate.Pedestrian, SignalMoves.Right), plan.PlateOf(FlasherOf(plan, nb), turns: SignalMoves.Right));
        Assert.Equal((SignalPlate.Arrow, SignalMoves.Left), plan.PlateOf(FlasherOf(plan, nb), turns: SignalMoves.Left));
        Assert.Equal((SignalPlate.Arrow, SignalMoves.Left), plan.TurnPlate(nb, SignalMoves.Left));
        // not a flasher, or a bike group's (its own yellow, no lamp): no plate
        Assert.Equal(SignalPlate.None, plan.PlateOf(nb).Plate);
    }

    [Fact]
    public void FlasherPlate_WalkersAndRiders_TheArrowOfTheTurnsCrossingBoth()
    {
        // a kerbside bike signal on every approach: the right turn crosses the riders beside it and the right arm's walkers, the
        // left turn the oncoming riders and the left arm's walkers: both ways on the plate (one turn alone: the test region's J7)
        var plan = SignalPlan.Build([Arm(N, bike: true), Arm(E, bike: true), Arm(S, bike: true), Arm(W, bike: true)]);
        AssertValid(plan);
        int nb = Group(plan, SignalGroupKind.Car, S);
        var (plate, arrow) = plan.PlateOf(FlasherOf(plan, nb));
        Assert.Equal(SignalPlate.Arrow, plate);
        Assert.Equal(SignalMoves.Left | SignalMoves.Right, arrow);
        // each turn's own lamp (#1109, the user: "right flasher here, left flasher on a pole next to the left crosswalk"): its own
        // arrow, flashing only for its own crossings
        int f = FlasherOf(plan, nb);
        Assert.Equal((SignalPlate.Arrow, SignalMoves.Right), plan.PlateOf(f, turns: SignalMoves.Right));
        Assert.Equal((SignalPlate.Arrow, SignalMoves.Left), plan.PlateOf(f, turns: SignalMoves.Left));
        var left = plan.SideFlash(f, SignalMoves.Left)!;
        var right = plan.SideFlash(f, SignalMoves.Right)!;
        for (double t = 0.05; t < plan.Cycle; t += 0.1)
        {
            bool any = plan.State(f, t) == SignalAspect.FlashingAmber;
            Assert.Equal(any, plan.State(left, t) == SignalAspect.FlashingAmber || plan.State(right, t) == SignalAspect.FlashingAmber);
        }
    }

    [Fact]
    public void FlasherPlate_RidersOnly_ABike()
    {
        // walkers on no arm: the right turn crosses only the riders' way, a bike; the left turn crosses the oncoming riders and gives
        // way to the oncoming cars: a mix, its arrow (#1117)
        var plan = SignalPlan.Build([Arm(N, peds: false, bike: true), Arm(E, peds: false, bike: true), Arm(S, peds: false, bike: true), Arm(W, peds: false, bike: true)]);
        int nb = Group(plan, SignalGroupKind.Car, S);
        Assert.Equal(SignalPlate.Bike, plan.PlateOf(FlasherOf(plan, nb), turns: SignalMoves.Right).Plate);
        Assert.Equal((SignalPlate.Arrow, SignalMoves.Left), plan.PlateOf(FlasherOf(plan, nb), turns: SignalMoves.Left));
    }

    [Fact]
    public void PermissiveLeft_FromASharedLane_FlashesWhileTheOncomingCarsAreGreen()
    {
        var plan = SignalPlan.Build([Arm(N), Arm(E), Arm(S), Arm(W)]);
        int nb = Group(plan, SignalGroupKind.Car, S), sb = Group(plan, SignalGroupKind.Car, N);
        Assert.Contains(sb, plan.LeftGivesWayTo(nb));
        var flash = plan.LeftFlash(nb);
        Assert.NotNull(flash);
        // flashing only during its green, and whenever its green and the oncoming one's are on together
        for (double t = 0.05; t < plan.Cycle; t += 0.1)
        {
            bool on = plan.State(flash!, t - plan.Offset) == SignalAspect.FlashingAmber;
            bool both = plan.State(nb, t - plan.Offset) == SignalAspect.Green && plan.State(sb, t - plan.Offset) == SignalAspect.Green;
            Assert.True(!on || plan.State(nb, t - plan.Offset) == SignalAspect.Green, $"flashing outside its green at {t:F1}");
            Assert.True(!both || on, $"not flashing at {t:F1} with the oncoming cars green");
        }
        // derived from the plan as stored: the same after a round trip through the tile
        var tile = new RoadTile
        {
            Id = new TileId(2500, 1117),
            Segments = [new RoadSegment { Class = RoadClass.Road, Width = 6, Points = [0, 400, 0, 10, 400, 0] }],
            Signals = [new RoadSignal { X = 5, Y = 400.1f, Z = 6, Stops = new float[12], Plan = plan }],
        };
        using var ms = new MemoryStream();
        RoadCodec.Encode(tile, ms);
        ms.Position = 0;
        var back = Assert.Single(RoadCodec.Decode(ms).Signals).Plan;
        Assert.Equal(flash!.Intervals, back.LeftFlash(nb)!.Intervals);
        Assert.Equal(plan.PlateOf(FlasherOf(plan, nb)), back.PlateOf(FlasherOf(back, nb)));
    }

    [Fact]
    public void ProtectedLeft_NeverFlashes()
    {
        // the left pockets' arrows are protected, and the main lanes beside them do not turn left
        var plan = SignalPlan.Build([Arm(N, true, true), Arm(E, true, true), Arm(S, true, true), Arm(W, true, true)], seed: 7);
        for (int g = 0; g < plan.Groups.Count; g++)
        {
            Assert.Empty(plan.LeftGivesWayTo(g));
            Assert.Null(plan.LeftFlash(g));
        }
    }

    [Fact]
    public void TeeStem_WithALeftAndARightLane_EachTurnItsOwnGroup_Protected()
    {
        // #1119, the user: every turn-only lane its own light. The stem (south) has a left and a right pocket and no through: a left
        // arrow and a right arrow, no ball for both; both protected in the side phase
        var plan = SignalPlan.Build([Arm(E, left: true), Arm(S, left: true, right: true), Arm(W, right: true)]);
        AssertValid(plan);
        Assert.Equal(-1, Group(plan, SignalGroupKind.Car, 1));
        int l = Group(plan, SignalGroupKind.LeftArrow, 1), r = Group(plan, SignalGroupKind.RightArrow, 1);
        Assert.True(l >= 0 && r >= 0);
        var c = plan.Conflicts();
        foreach (int g in (int[])[l, r])
        {
            Assert.NotEmpty(WhenGreen(plan, g));
            for (int h = 0; h < plan.Groups.Count; h++)
                if (c[g, h] != SignalPlan.Conflict.None) Assert.Empty(WhenGreen(plan, g, h));
        }
    }

    [Fact]
    public void RightPocket_ProtectedWithItsApproach_WhereItsWalkersKeepAnotherGreen()
    {
        // #1119, the user: "even right turns with their own lanes are usually protected". Left pockets on the north-south road
        // give it a lead phase in which the east arm's walkers (across the northbound right turn's exit) are green: the right
        // arrow no phase carried before goes with its approach, the walkers red meanwhile, green in the lead
        var plan = SignalPlan.Build([Arm(N, left: true), Arm(E), Arm(S, left: true, right: true), Arm(W)]);
        AssertValid(plan);
        int r = plan.Groups.FindIndex(g => g.Arm == S && (g.Kind == SignalGroupKind.RightArrow || g is { Kind: SignalGroupKind.Car, Moves: SignalMoves.Right }));
        Assert.Equal(SignalGroupKind.RightArrow, plan.Groups[r].Kind);
        var c = plan.Conflicts();
        Assert.NotEmpty(WhenGreen(plan, r));
        for (int h = 0; h < plan.Groups.Count; h++)
            if (c[r, h] == SignalPlan.Conflict.Hard) Assert.Empty(WhenGreen(plan, r, h));
        Assert.NotEmpty(WhenGreen(plan, Group(plan, SignalGroupKind.Pedestrian, E)));
    }

    [Fact]
    public void FromGeneva_AStemWithTwoLanesWhoseRightTurnCannotBeProtected_StillGoesGreen()
    {
        // LV95 2499873,1117528 (#1119): a one-way in and a stem with a left and a right lane; the right arrow, held off the walkers
        // it crosses in no phase, gives way with the stem's left arrow (its plan was written invalid: the right turn never green)
        double D(double deg) => deg * Math.PI / 180;
        var plan = SignalPlan.Build([
            new SignalArm(D(-154), false, true, Pedestrians: true, CrossingM: 10, Rank: 18),
            new SignalArm(D(-64), true, false, Pedestrians: true, CrossingM: 8, Rank: 18),
            new SignalArm(D(27), false, true, Pedestrians: true, CrossingM: 8, Rank: 2),
            new SignalArm(D(116), true, true, LeftPocket: true, RightPocket: true, Pedestrians: true, CrossingM: 9.5f, Rank: 2)]);
        AssertValid(plan);
        Assert.Contains(plan.Groups, g => g is { Kind: SignalGroupKind.LeftArrow, Arm: 3 });
    }
    [Fact]
    public void RightTurnBikeLane_HasItsOwnGroup_ProtectedWithTheRightArrow()
    {
        // #1165, the user: "right turn only bike lanes should get their own light". Left pockets everywhere: the right arrows are
        // protected (#1119); the riders' own right-turn lane at the kerb (layout b) goes with its approach's right arrow, protected
        // too, and the through riders' group goes straight on only
        var plan = SignalPlan.Build([Arm(N, true, true), Arm(E, true, true),
            Arm(S, true, true, bike: true) with { RiderRight = true, BikeBetween = true }, Arm(W, true, true)]);
        AssertValid(plan);
        var bikes = plan.Groups.Where(g => g.Kind == SignalGroupKind.Bike && g.Arm == S).ToList();
        Assert.Equal(2, bikes.Count);
        Assert.Contains(bikes, g => g.Moves == SignalMoves.Through);
        int riders = plan.Groups.FindIndex(g => g is { Kind: SignalGroupKind.Bike, Moves: SignalMoves.Right } && g.Arm == S);
        int right = Group(plan, SignalGroupKind.RightArrow, S);
        Assert.True(riders >= 0 && right >= 0);
        var c = plan.Conflicts();
        Assert.Equal(SignalPlan.Conflict.None, c[riders, right]);
        Assert.NotEmpty(WhenGreen(plan, riders));
        Assert.NotEmpty(WhenGreen(plan, riders, right));
        // protected: never green with the walkers across its exit, no flasher
        Assert.Empty(WhenGreen(plan, riders, Group(plan, SignalGroupKind.Pedestrian, E)));
        Assert.DoesNotContain(plan.Groups, g => g.Kind == SignalGroupKind.Flasher && g.Qualifies == riders);
        // the paths' group (RoadGen's PathGroup, the riders' BikeGroupFor): the right-only group for a right turn
        Assert.Equal(riders, plan.Groups.FindIndex(g => g.Arm == S && g.Kind == SignalGroupKind.Bike && (g.Moves & SignalMoves.Right) != 0));
    }

    [Fact]
    public void RightTurnBikeLane_WithAPermissiveRightTurn_GivesWayToTheExitsWalkers()
    {
        // no left pockets: the right pocket's arrow finds no protected phase and goes with its approach giving way (#1109); the
        // riders' right turn with it, their own yellow flashing
        var plan = SignalPlan.Build([Arm(N), Arm(E), Arm(S, right: true) with { RiderRight = true, BikeBetween = true }, Arm(W)]);
        AssertValid(plan);
        int riders = plan.Groups.FindIndex(g => g is { Kind: SignalGroupKind.Bike, Moves: SignalMoves.Right } && g.Arm == S);
        Assert.True(riders >= 0);
        Assert.DoesNotContain(plan.Groups, g => g.Kind == SignalGroupKind.Bike && g.Arm == S && g.Moves != SignalMoves.Right);
        Assert.NotEmpty(WhenGreen(plan, riders, Group(plan, SignalGroupKind.Pedestrian, E)));
        Assert.Contains(plan.Groups, g => g.Kind == SignalGroupKind.Flasher && g.Qualifies == riders);
    }

    [Fact]
    public void SharedBikeLane_KeepsTheCombinedGroup()
    {
        var plan = SignalPlan.Build([Arm(N, true, true), Arm(E, true, true), Arm(S, true, true, bike: true), Arm(W, true, true)]);
        AssertValid(plan);
        var bike = Assert.Single(plan.Groups, g => g.Kind == SignalGroupKind.Bike && g.Arm == S);
        Assert.Equal(SignalMoves.Through | SignalMoves.Right, bike.Moves);
    }
}
