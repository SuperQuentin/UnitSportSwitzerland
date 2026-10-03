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
    public void Bikes_LeadTheirApproach_AndNeverMeetTheRightArrow()
    {
        var plan = SignalPlan.Build([Arm(N, true, true, bike: true), Arm(E, true, true), Arm(S, true, true, bike: true), Arm(W, true, true)]);
        AssertValid(plan);
        int bike = Group(plan, SignalGroupKind.Bike, S), car = Group(plan, SignalGroupKind.Car, S);
        int right = Group(plan, SignalGroupKind.RightArrow, S);
        Assert.Empty(WhenGreen(plan, bike, right));
        Assert.True(WhenGreen(plan, bike).Count > WhenGreen(plan, bike, car).Count, "the bike green starts before the car's");
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
        var plan = SignalPlan.Build([Arm(N, true, true, bike: true), Arm(E), Arm(S, true), Arm(W, right: true)], seed: 99, pedestrianAmber: false);
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
        Assert.Equal(plan.Arms.Count, p.Arms.Count);
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
}
