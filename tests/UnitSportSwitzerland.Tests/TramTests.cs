using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Rewrite;
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

    // ---- tram lanes on their own bed (#1161) ---------------------------------------------------

    /// <summary>
    /// The west arm's mouth of an east-west road (its carriageway 3.3 m wide, 7 m south of the junction's centre: a divided road's
    /// eastbound one), and a track crossing the core's outline at <paramref name="n"/> m north of the centre, running east (in).
    /// </summary>
    private static double? BesideTheWestArm(double n, bool entering = true, bool lanes = true, double headingDeg = 0)
    {
        var left = new Vec2(-12, -7 + 1.65);
        var right = new Vec2(-12, -7 - 1.65);
        var dir = new Vec2(Math.Cos(headingDeg * Math.PI / 180), Math.Sin(headingDeg * Math.PI / 180));
        return TileRewriter.Alongside(left, right, new Vec2(-1, 0), new Vec2(-10, n), entering ? dir : dir * -1, entering, lanes);
    }

    [Fact]
    public void ATrackOnTheCentralReservation_BelongsToTheCarriagewayBesideIt()
    {
        // the eastbound track of a double track between carriageways 14 m apart: 3.75 m past the carriageway's left kerb
        Assert.Equal(3.75, BesideTheWestArm(-1.6)!.Value, 3);
        // leaving by it, its way out: only where the arm's cars leave that way too
        Assert.NotNull(BesideTheWestArm(-1.6, entering: false));
        Assert.Null(BesideTheWestArm(-1.6, lanes: false));
    }

    [Fact]
    public void ATrackOnABedBesideTheRoad_BelongsToTheArm()
    {
        // 2 m past the right kerb, a bed of its own beside the road
        Assert.Equal(2, BesideTheWestArm(-7 - 1.65 - 2)!.Value, 3);
    }

    [Fact]
    public void ATrackTooFarAway_OrAcrossTheArm_DoesNotBelongToIt()
    {
        // past the carriageway's width and the bed's margin
        Assert.Null(BesideTheWestArm(-7 + 1.65 + 3.3 + TileRewriter.TramBedMargin + 0.5));
        // crossing the arm's way (a tram across the street, not along it)
        Assert.Null(BesideTheWestArm(-1.6, headingDeg: 90));
        Assert.Null(BesideTheWestArm(-1.6, headingDeg: 40));
        Assert.NotNull(BesideTheWestArm(-1.6, headingDeg: 25));
    }

    /// <summary>A crossroads whose east-west approaches have their trams straight on, left of their cars (a central reservation).</summary>
    private static SignalPlan Reserved(bool leftPockets) => SignalPlan.Build(
    [
        new SignalArm(0, true, true, LeftPocket: leftPockets, Rank: 2, TramMoves: SignalMoves.Through, TramSide: -1),
        new SignalArm(Math.PI / 2, true, true),
        new SignalArm(Math.PI, true, true, LeftPocket: leftPockets, Rank: 2, TramMoves: SignalMoves.Through, TramSide: -1),
        new SignalArm(-Math.PI / 2, true, true),
    ], 1161);

    [Fact]
    public void ACarTurningLeftAcrossItsOwnApproachsReservedTram_GivesWayToIt()
    {
        // a shared lane's left turn crosses the tram on the reservation beside it: the car gives way (the tram never), they share
        // the green and the tram leads them; the tram's side is the group's
        var plan = Reserved(leftPockets: false);
        Assert.Empty(plan.Validate());
        var conflicts = plan.Conflicts();
        for (int arm = 0; arm <= 2; arm += 2)
        {
            int tram = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram && g.Arm == arm);
            int car = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == arm);
            Assert.Equal(-1, plan.Groups[tram].TramSide);
            Assert.Equal(SignalPlan.Conflict.Permissive, conflicts[tram, car]);
            bool together = false;
            for (double s = 0.05; s < plan.Cycle; s += 0.1)
                together |= plan.State(tram, s) == SignalAspect.Green && plan.State(car, s) == SignalAspect.Green;
            Assert.True(together, $"arm {arm}: the tram is green with its cars");
        }
    }

    [Fact]
    public void ALeftArrowAcrossItsOwnApproachsReservedTram_NeverSharesItsGreen()
    {
        var plan = Reserved(leftPockets: true);
        Assert.Empty(plan.Validate());
        var conflicts = plan.Conflicts();
        int tram = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram && g.Arm == 0);
        int arrow = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.LeftArrow && g.Arm == 0);
        Assert.Equal(SignalPlan.Conflict.Hard, conflicts[tram, arrow]);
        for (double s = 0.05; s < plan.Cycle; s += 0.1)
            Assert.False(plan.State(tram, s) == SignalAspect.Green && plan.State(arrow, s) == SignalAspect.Green, $"at {s:F1} s");
        // a tram among its cars (in the street, side 0) is as before: no conflict with its own approach
        var street = SignalPlan.Build(
        [
            new SignalArm(0, true, true, LeftPocket: true, Rank: 2, TramMoves: SignalMoves.Through),
            new SignalArm(Math.PI / 2, true, true),
            new SignalArm(Math.PI, true, true, LeftPocket: true, Rank: 2),
            new SignalArm(-Math.PI / 2, true, true),
        ], 1161);
        int t2 = street.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram);
        int a2 = street.Groups.FindIndex(g => g.Kind == SignalGroupKind.LeftArrow && g.Arm == 0);
        Assert.Equal(SignalPlan.Conflict.None, street.Conflicts()[t2, a2]);
    }

    [Fact]
    public void ATramHead_StandsRightOfItsStopLine_OnTheReservationWhereThereIsRoom()
    {
        // #1161, the user: "at least on the right of the stop line, the closer the better (if there is an island)". A carriageway
        // from -1.65 to 3.15 m: a track 4.8 m left of the axis (a reservation between it and the cars) gets a pole 1.7 m right of it
        Assert.Equal(-4.8 + TileRewriter.TramPoleAside, TileRewriter.TramPoleSpot(-1.65, 3.15, -4.8)!.Value, 3);
        // a bed beside the road, right of the cars: its pole beyond it, off the road
        Assert.Equal(6 + TileRewriter.TramPoleAside, TileRewriter.TramPoleSpot(-1.65, 3.15, 6)!.Value, 3);
        // no room: a reservation too narrow (the spot within half a metre of the kerb), a track in the street: the kerb pole then
        Assert.Null(TileRewriter.TramPoleSpot(-1.65, 3.15, -3.5));
        Assert.Null(TileRewriter.TramPoleSpot(-5, 5, -1.6));
    }


    /// <summary>
    /// A crossroads with left pockets (40 m each: their turn phases' fixed greens clear a full queue) and walkers on every arm, timed
    /// from conflict times of <paramref name="seconds"/> between every pair that never shares a green (#1161).
    /// </summary>
    private static SignalPlan TimedWithConflicts(float seconds)
    {
        var plan = SignalPlan.Build(
        [
            new SignalArm(0, true, true, LeftPocket: true, Pedestrians: true, LeftStorageM: 40, StorageM: 200),
            new SignalArm(Math.PI / 2, true, true, LeftPocket: true, Pedestrians: true, LeftStorageM: 40, StorageM: 200),
            new SignalArm(Math.PI, true, true, LeftPocket: true, Pedestrians: true, LeftStorageM: 40, StorageM: 200),
            new SignalArm(-Math.PI / 2, true, true, LeftPocket: true, Pedestrians: true, LeftStorageM: 40, StorageM: 200),
        ], 1161);
        int n = plan.Groups.Count;
        var conflicts = plan.Conflicts();
        var times = new float[n, n];
        for (int g = 0; g < n; g++)
            for (int h = 0; h < n; h++)
                times[g, h] = conflicts[g, h] == SignalPlan.Conflict.Hard ? seconds : float.NaN;
        Assert.True(plan.Retime(times));
        Assert.Empty(plan.Validate());
        Assert.True(plan.KeepsTimes(times));
        return plan;
    }

    [Fact]
    public void APlanOverTheMaximum_IsSqueezedTo120s_ItsConflictTimesKept()
    {
        // #1161, the user: "squeeze to 120s": its turn phases' queue-clearing greens shrink toward the minimum, the conflict times stay
        var plan = TimedWithConflicts(12);
        Assert.True(plan.Squeezed);
        Assert.True(plan.ConflictTimed);
        Assert.False(plan.Overflows);
        Assert.InRange(plan.Cycle, 100, SignalPlan.MaxCycle);
        // (with short conflict times the greens keep what they want)
        Assert.False(TimedWithConflicts(4).Squeezed);
    }

    [Fact]
    public void APlanStillOverTheMaximumWithEveryGreenAtItsMinimum_IsKeptAndReported()
    {
        // "...and then only allow overflow but report until 1120 lands": the plan as it is, conflict-timed, over 120 s
        var plan = TimedWithConflicts(24);
        Assert.True(plan.ConflictTimed);
        Assert.True(plan.Overflows);
        Assert.True(plan.Cycle > SignalPlan.MaxCycle);
        Assert.Equal(6, plan.PhaseCount);
    }

    [Fact]
    public void AMiddleStageOverTheTramReservation_IsStoppedOnlyByTheTramsOverIt()
    {
        // #1176: a divided road (its west carriageways arms 0 in and 3 out, east 1 out and 4 in) with its tram on the reservation,
        // across a road (2, 5); the west crossing's middle stage over the tracks
        var plan = SignalPlan.Build(
        [
            new SignalArm(Math.PI, true, false, Pedestrians: true, TramMoves: SignalMoves.Through, TramSide: -1, MiddleM: 9),
            new SignalArm(0.05, false, true, Pedestrians: true),
            new SignalArm(-Math.PI / 2, true, true, Pedestrians: true),
            new SignalArm(Math.PI - 0.05, false, true, Pedestrians: true),
            new SignalArm(0, true, false, Pedestrians: true, TramMoves: SignalMoves.Through, TramSide: -1),
            new SignalArm(Math.PI / 2, true, true, Pedestrians: true),
        ], 1176);
        Assert.Empty(plan.Validate());
        int middle = plan.Groups.FindIndex(g => g.Half == CrossingHalf.Middle);
        Assert.Equal(0, plan.Groups[middle].Arm);
        Assert.Equal(3, plan.Twin(0));
        Assert.Equal(9f, plan.CrossingOf(middle));
        var conflicts = plan.Conflicts();
        // the eastbound trams entering by its arm and the westbound ones leaving by its twin
        foreach (int tram in Enumerable.Range(0, plan.Groups.Count).Where(g => plan.Groups[g].Kind == SignalGroupKind.Tram))
            Assert.Equal(SignalPlan.Conflict.Hard, conflicts[middle, tram]);
        Assert.All(Enumerable.Range(0, plan.Groups.Count).Where(g => plan.Groups[g].Kind != SignalGroupKind.Tram),
            g => Assert.NotEqual(SignalPlan.Conflict.Hard, conflicts[middle, g]));
        // green while the trams are red, never with them
        Assert.Contains(plan.Groups[middle].Intervals, iv => iv.Aspect == SignalAspect.Green);
        for (double t = 0.05; t < plan.Cycle; t += 0.1)
            if (plan.State(middle, t) == SignalAspect.Green)
                Assert.All(Enumerable.Range(0, plan.Groups.Count).Where(g => plan.Groups[g].Kind == SignalGroupKind.Tram),
                    g => Assert.NotEqual(SignalAspect.Green, plan.State(g, t)));
    }

    [Fact]
    public void AZebraWithoutLights_HasNoStripesOverTheTramTracks()
    {
        // #1179, the user: "a crossing who never has priority shouldn't get yellow stripes anyways": its walkers give way to the
        // trams there; three bars at x 0, 2 and 4, a track zone around x 2
        static float[] Bar(float x) => [x - 0.25f, 0, 0, x - 0.25f, 0, 3, x + 0.25f, 0, 3, x + 0.25f, 0, 0];
        var zebra = new RoadPaint { Shape = PaintShape.Triangles, Type = PaintType.YellowSolid, Vertices = [.. Bar(0), .. Bar(2), .. Bar(4)],
            Indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11] };
        bool Track(float x, float z) => MathF.Abs(x - 2) < 1;
        var cut = UnitSport.Tools.RoadGen.Network.RailRoadOverlap.ZebraOffTracks(zebra, Track, null);
        Assert.NotNull(cut);
        Assert.Equal(24, cut!.Vertices.Length);
        Assert.Equal(12, cut.Indices.Length);
        // at lights its walkers' green stops the trams: it keeps them
        Assert.Null(UnitSport.Tools.RoadGen.Network.RailRoadOverlap.ZebraOffTracks(zebra, Track, [(2f, 10f)]));
    }
}
