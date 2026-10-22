using UnitSport.Terrain.Format;
using UnitSport.World;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Trams (#1057): which way trams run on TLM's single tracks (the partner on the left), the tram groups of a signal plan (green with
/// their approach, never with what they cross, a turning tram's own phase), and the cars at a tram path giving way to it.
/// </summary>
public class TramTests
{
    private static (double E, double N)[] Line(double e0, double n0, double e1, double n1) => [(e0, n0), (e1, n1)];

    [Fact]
    public void TwoTracks_EachRunsWithItsPartnerOnTheLeft()
    {
        // both drawn west to east, 3.2 m apart: the southern one eastbound, the northern one westbound (trams keep right)
        var ways = TramTracks.Directions([Line(0, -1.6, 200, -1.6), Line(0, 1.6, 200, 1.6)]);
        Assert.Equal([1, -1], ways);
        // the same, the northern one drawn the other way: it runs along its drawing
        Assert.Equal([1, 1], TramTracks.Directions([Line(0, -1.6, 200, -1.6), Line(200, 1.6, 0, 1.6)]));
    }

    [Fact]
    public void ATrackAlone_OrMappedWithTwoTracks_CarriesBothWays()
    {
        Assert.Equal([0], TramTracks.Directions([Line(0, 0, 200, 0)]));
        // a road 12 m away is no partner; a double-track line carries both ways itself
        Assert.Equal([0, 0], TramTracks.Directions([Line(0, 0, 200, 0), Line(0, 12, 200, 12)]));
        Assert.Equal([0, 0], TramTracks.Directions([Line(0, -1.6, 200, -1.6), Line(0, 1.6, 200, 1.6)], [true, true]));
    }

    /// <summary>A crossroads (east, north, west, south) with trams straight through east-west, both ways.</summary>
    private static SignalPlan Crossroads(SignalMoves eastTrams, SignalMoves westTrams) => SignalPlan.Build(
    [
        new SignalArm(0, true, true, LeftPocket: true, Pedestrians: true, Rank: 2, TramMoves: eastTrams),
        new SignalArm(Math.PI / 2, true, true, Pedestrians: true),
        new SignalArm(Math.PI, true, true, LeftPocket: true, Pedestrians: true, Rank: 2, TramMoves: westTrams),
        new SignalArm(-Math.PI / 2, true, true, Pedestrians: true),
    ], 1057);

    [Fact]
    public void TramGroups_GoWithTheirApproach_AndThePlanHolds()
    {
        var plan = Crossroads(SignalMoves.Through, SignalMoves.Through);
        Assert.Empty(plan.Validate());
        var trams = Enumerable.Range(0, plan.Groups.Count).Where(g => plan.Groups[g].Kind == SignalGroupKind.Tram).ToList();
        Assert.Equal(2, trams.Count);
        var conflicts = plan.Conflicts();
        foreach (int t in trams)
        {
            // green at some point, and green only while its arm's through cars are (it goes straight on beside them)
            int car = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == plan.Groups[t].Arm);
            Assert.Contains(plan.Groups[t].Intervals, iv => iv.Aspect == SignalAspect.Green);
            for (double s = 0.05; s < plan.Cycle; s += 0.1)
                if (plan.State(t, s - plan.Offset) == SignalAspect.Green)
                    Assert.NotEqual(SignalAspect.Red, plan.State(car, s - plan.Offset));
            // the cross road's cars and the walkers across its arm never with it
            Assert.All(Enumerable.Range(0, plan.Groups.Count).Where(g => plan.Groups[g].Kind == SignalGroupKind.Car && plan.Groups[g].Arm % 2 == 1),
                g => Assert.Equal(SignalPlan.Conflict.Hard, conflicts[t, g]));
        }
    }

    [Fact]
    public void ALeftTurningTram_CrossesTheOncomingCars_SoItIsNeverGreenWithThem()
    {
        var plan = Crossroads(SignalMoves.Left, SignalMoves.None);
        Assert.Empty(plan.Validate());
        int tram = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram);
        int oncoming = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == 2);
        Assert.Equal(SignalPlan.Conflict.Hard, plan.Conflicts()[tram, oncoming]);
        Assert.Contains(plan.Groups[tram].Intervals, iv => iv.Aspect == SignalAspect.Green);
    }

    [Fact]
    public void ACarTurningLeftAcrossTheOncomingTram_GivesWayToIt()
    {
        var plan = Crossroads(SignalMoves.None, SignalMoves.Through);
        var conflicts = plan.Conflicts();
        int tram = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram);
        // the east approach's main lane (its lefts in a pocket here: build one without)
        var shared = SignalPlan.Build(
        [
            new SignalArm(0, true, true, Rank: 2),
            new SignalArm(Math.PI / 2, true, true),
            new SignalArm(Math.PI, true, true, Rank: 2, TramMoves: SignalMoves.Through),
            new SignalArm(-Math.PI / 2, true, true),
        ], 7);
        int car = shared.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == 0);
        int t2 = shared.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram);
        Assert.Equal(SignalPlan.Conflict.Permissive, shared.Conflicts()[car, t2]);
        // at a point: the car's left path across the tram's straight way (tile-local x, y, z)
        float[] left = [10, 0, 3, 0, 0, 3, -2, 0, 0, -3, 0, -15];
        float[] tramWay = [-15, 0, -1.6f, 15, 0, -1.6f];
        var ways = new List<ConflictWay>
        {
            new(left, car, ConflictMover.Car, SignalMoves.Left, 1.5f),
            new(tramWay, t2, ConflictMover.Tram, SignalMoves.Through, 1.2f),
        };
        var points = ConflictPoints.For(shared, shared.Conflicts(), ways, 0);
        Assert.Single(points);
        Assert.Equal(1, points[0].Other);
        Assert.True(tram >= 0 && conflicts[tram, tram] == SignalPlan.Conflict.None);
    }

    [Fact]
    public void TramsTurningEveryWay_EachGetAGreen()
    {
        // Geneva 2500039,1119621 had a tram group no phase could carry (#1057): every tram group is green somewhere, the plan valid
        foreach (var moves in new[] { SignalMoves.Left, SignalMoves.Right, SignalMoves.Left | SignalMoves.Through })
        {
            var plan = SignalPlan.Build(
            [
                new SignalArm(0, true, true, Pedestrians: true, TramMoves: moves),
                new SignalArm(Math.PI / 2, true, true, Pedestrians: true, TramMoves: moves),
                new SignalArm(Math.PI, true, true, Pedestrians: true, TramMoves: moves),
                new SignalArm(-Math.PI / 2, true, true, Pedestrians: true, TramMoves: moves),
            ], 3);
            Assert.Empty(plan.Validate());
            Assert.All(plan.Groups.Where(g => g.Kind == SignalGroupKind.Tram), g => Assert.Contains(g.Intervals, iv => iv.Aspect == SignalAspect.Green));
        }
    }

    /// <summary>How long before <paramref name="s"/> time <paramref name="t"/> is, round the cycle.</summary>
    private static float Ahead(SignalPlan plan, float s, float t) => ((s - t) % plan.Cycle + plan.Cycle) % plan.Cycle;

    /// <summary>When each green of group <paramref name="g"/> starts, round the cycle (a green over the seam once).</summary>
    private static List<float> Starts(SignalPlan plan, int g)
    {
        var v = plan.Groups[g].Intervals;
        return v.Where((iv, k) => iv.Aspect == SignalAspect.Green && !(k == 0 && iv.From < 1e-3f && v[^1].Aspect == SignalAspect.Green))
            .Select(iv => iv.From).ToList();
    }

    [Fact]
    public void ATram_LeadsItsApproachsCars_ByASecond()
    {
        // the user: "a short leading signal (1 sec)"
        var plan = Crossroads(SignalMoves.Through, SignalMoves.None);
        Assert.Empty(plan.Validate());
        int tram = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram);
        int car = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == 0);
        var trams = Starts(plan, tram);
        foreach (float s in Starts(plan, car).Where(s => trams.Any(t => Ahead(plan, s, t) < 5)))
            Assert.Contains(trams, t => Math.Abs(Ahead(plan, s, t) - SignalPlan.TramLead) < 0.05f);
    }

    [Fact]
    public void ACarTurningAcrossTheTram_StartsTheRidersLeadAfterIt()
    {
        // the user: "if there is conflicting movements, add a longer leading signal, like for bikes": the oncoming left turns
        var plan = SignalPlan.Build(
        [
            new SignalArm(0, true, true, Rank: 2),
            new SignalArm(Math.PI / 2, true, true),
            new SignalArm(Math.PI, true, true, Rank: 2, TramMoves: SignalMoves.Through),
            new SignalArm(-Math.PI / 2, true, true),
        ], 7);
        Assert.Empty(plan.Validate());
        int tram = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram);
        int oncoming = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == 0);
        var trams = Starts(plan, tram);
        var cars = Starts(plan, oncoming);
        Assert.NotEmpty(cars);
        Assert.All(cars, s => Assert.Contains(trams, t => Ahead(plan, s, t) >= SignalPlan.BikeLead - 0.05f && Ahead(plan, s, t) < 6f));
    }

    [Fact]
    public void TramsGoingTwoWays_HaveAGroupPerDirection()
    {
        // the user: one head per direction, its arrow on its plate
        var plan = Crossroads(SignalMoves.Left | SignalMoves.Through, SignalMoves.None);
        Assert.Empty(plan.Validate());
        var trams = plan.Groups.Where(g => g.Kind == SignalGroupKind.Tram).ToList();
        Assert.Equal([SignalMoves.Left, SignalMoves.Through], trams.Select(g => g.Moves));
        Assert.All(trams, g => Assert.Contains(g.Intervals, iv => iv.Aspect == SignalAspect.Green));
    }

    [Fact]
    public void OneTrackBranching_TimesEachDirectionOnItsOwn()
    {
        // the user (#1105): transit gets a signal per direction on a single lane, never one combined group. One track from the
        // east branching straight on and left: the straight tram goes with its cars while the left one still waits for the
        // oncoming cars, so there is a time with through on go and left on stop
        var plan = Crossroads(SignalMoves.Left | SignalMoves.Through, SignalMoves.None);
        int through = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram && g.Moves == SignalMoves.Through);
        int left = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram && g.Moves == SignalMoves.Left);
        Assert.True(through >= 0 && left >= 0 && through != left);
        Assert.Equal(plan.Groups[through].Arm, plan.Groups[left].Arm);
        Assert.Contains(plan.Groups[left].Intervals, iv => iv.Aspect == SignalAspect.Green);
        bool apart = false;
        for (double s = 0.05; s < plan.Cycle; s += 0.1)
            apart |= plan.State(through, s - plan.Offset) == SignalAspect.Green && plan.State(left, s - plan.Offset) == SignalAspect.Red;
        Assert.True(apart, "through on go while left is on stop");
        // and a tram path picks its own direction's group, not the first one of the track
        Assert.Equal(through, UnitSport.Tools.RoadGen.Rewrite.TileRewriter.TramGroup(plan, 0, SignalMoves.Through));
        Assert.Equal(left, UnitSport.Tools.RoadGen.Rewrite.TileRewriter.TramGroup(plan, 0, SignalMoves.Left));
    }

    [Theory]
    [InlineData(50, 5f)]
    [InlineData(60, 6f)]
    [InlineData(80, 7f)]
    public void ATramsAmber_IsLongerThanACars_ByTheApproachsSpeed(int kmh, float amber)
    {
        // the user (#1105): "5s at 50, 6s at 60 and 7s above"; both heads, the cars' own amber unchanged (3/4/5 s)
        foreach (bool romandy in new[] { false, true })
        {
            var plan = SignalPlan.Build(
            [
                new SignalArm(0, true, true, Pedestrians: true, Rank: 2, SpeedKmh: kmh, TramMoves: SignalMoves.Through),
                new SignalArm(Math.PI / 2, true, true, Pedestrians: true, SpeedKmh: kmh),
                new SignalArm(Math.PI, true, true, Pedestrians: true, Rank: 2, SpeedKmh: kmh),
                new SignalArm(-Math.PI / 2, true, true, Pedestrians: true, SpeedKmh: kmh),
            ], 1105, tramTHead: romandy);
            Assert.Empty(plan.Validate());
            int tram = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram);
            int car = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == 0);
            float Amber(int g) => plan.Groups[g].Intervals.Where(iv => iv.Aspect == SignalAspect.Amber).Sum(iv => iv.To - iv.From);
            Assert.Equal(amber, Amber(tram), 0.05f);
            Assert.Equal(SignalPlan.Yellow(kmh), Amber(car), 0.05f);
            Assert.Equal(amber, plan.PassingTime(tram));
            Assert.True(plan.Cycle <= SignalPlan.MaxCycle);
        }
    }

    [Fact]
    public void RomandysTramRedAmber_IsTheLongOne()
    {
        // the user: the flashing bar across before go lasts "way longer than for cars" (#1105: the head's flag reached the plan only
        // after its timing, so it stayed a car's 2 s)
        foreach (bool romandy in new[] { false, true })
        {
            var plan = SignalPlan.Build(
            [
                new SignalArm(0, true, true, Pedestrians: true, Rank: 2, TramMoves: SignalMoves.Through),
                new SignalArm(Math.PI / 2, true, true, Pedestrians: true),
                new SignalArm(Math.PI, true, true, Pedestrians: true, Rank: 2),
                new SignalArm(-Math.PI / 2, true, true, Pedestrians: true),
            ], 1105, tramTHead: romandy);
            Assert.Equal(romandy, plan.TramTHead);
            var tram = plan.Groups.First(g => g.Kind == SignalGroupKind.Tram);
            float redAmber = tram.Intervals.Where(iv => iv.Aspect == SignalAspect.RedAmber).Sum(iv => iv.To - iv.From);
            Assert.Equal(romandy ? SignalPlan.TramRedAmberT : 2f, redAmber, 0.05f);
        }
    }

    [Theory]
    [InlineData("GE", 2_500_000, true)]
    [InlineData("VD", 2_530_000, true)]
    [InlineData("VS", 2_590_000, true)]
    [InlineData("VS", 2_640_000, false)]
    [InlineData("ZH", 2_683_000, false)]
    [InlineData(null, 2_500_000, false)]
    public void RomandyHasTheTHead(string? canton, double e, bool t) =>
        Assert.Equal(t, UnitSport.Tools.RoadGen.Rewrite.TileRewriter.RomandyTramHead(canton, e));
}
