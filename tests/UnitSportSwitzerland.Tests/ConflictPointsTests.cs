using UnitSport.Terrain.Format;
using UnitSport.World;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Where a car on a lit junction's path waits for a gap (#1046): a crossroads without pockets (every approach one shared lane),
/// 20 m across, right-hand traffic; x east, z north. The car comes up from the south and turns left (west).
/// </summary>
public class ConflictPointsTests
{
    private const int N = 0, E = 1, S = 2, W = 3;
    private static readonly double[] Headings = [Math.PI / 2, 0, -Math.PI / 2, Math.PI];

    private static readonly SignalPlan Plan = SignalPlan.Build([Arm(N), Arm(E), Arm(S), Arm(W)], seed: 7);
    private static readonly SignalPlan.Conflict[,] Conflicts = Plan.Conflicts();

    private static SignalArm Arm(int i) => new(Headings[i], true, true, false, false, true, false);

    private static int Group(SignalGroupKind kind, int arm) => Plan.Groups.FindIndex(g => g.Kind == kind && g.Arm == arm);

    /// <summary>A line through (x, z) points, at height 0, as the tiles write them.</summary>
    private static float[] Line(params (float X, float Z)[] p) => p.SelectMany(q => new[] { q.X, 0f, q.Z }).ToArray();

    // northbound from the south arm: left to the west, through, right to the east
    private static readonly ConflictWay NbLeft = new(Line((1.75f, -8f), (1.75f, -2f), (-2f, 1.75f), (-8f, 1.75f)), Group(SignalGroupKind.Car, S), ConflictMover.Car, SignalMoves.Left, 1.75f);
    private static readonly ConflictWay NbThrough = new(Line((1.75f, -8f), (1.75f, 8f)), Group(SignalGroupKind.Car, S), ConflictMover.Car, SignalMoves.Through, 1.75f);
    // southbound from the north arm: through, and left to the east
    private static readonly ConflictWay SbThrough = new(Line((-1.75f, 8f), (-1.75f, -8f)), Group(SignalGroupKind.Car, N), ConflictMover.Car, SignalMoves.Through, 1.75f);
    private static readonly ConflictWay SbLeft = new(Line((-1.75f, 8f), (-1.75f, 2f), (2f, -1.75f), (8f, -1.75f)), Group(SignalGroupKind.Car, N), ConflictMover.Car, SignalMoves.Left, 1.75f);
    // the west arm's crossing, kerb to kerb, 3 m deep
    private static readonly ConflictWay WalkWest = new(Line((-10f, -6f), (-10f, 6f)), Group(SignalGroupKind.Pedestrian, W), ConflictMover.Walker, SignalMoves.None, 1.5f);
    private static readonly ConflictWay WalkWestBack = WalkWest with { Points = Line((-10f, 6f), (-10f, -6f)) };

    private static readonly ConflictWay[] Ways = [NbLeft, NbThrough, SbThrough, SbLeft, WalkWest, WalkWestBack];

    [Fact]
    public void LeftTurn_WaitsForTheOncomingThrough_ThenTheCrossingItTurnsInto()
    {
        var points = ConflictPoints.For(Plan, Conflicts, Ways, 0);
        Assert.Equal(2, points.Count);
        // the oncoming lane: the car's body (1 m) meets the 1.75 m half lane once its line is 2.75 m from x = -1.75
        Assert.Equal(2, points[0].Other);
        Assert.InRange(points[0].At, 6.5f, 7.5f);
        Assert.True(points[0].Out > points[0].At + 3f);
        // the oncoming car comes into the left-turner's lane a few metres before the junction's middle
        Assert.InRange(points[0].Enter, 4f, 10f);
        // the west crossing, once (its line is written both ways), near the end of the turn
        Assert.Equal(ConflictMover.Walker, Ways[points[1].Other].Mover);
        Assert.InRange(points[1].At, 14f, 17.5f);
    }

    [Fact]
    public void TwoLefts_NeverYieldToEachOther()
    {
        Assert.False(ConflictPoints.Yields(Plan, Conflicts, NbLeft, SbLeft));
        Assert.False(ConflictPoints.Yields(Plan, Conflicts, SbLeft, NbLeft));
    }

    [Fact]
    public void Through_YieldsToNobody_AndTheOncomingLeftYieldsToIt()
    {
        Assert.Empty(ConflictPoints.For(Plan, Conflicts, Ways, 1));
        Assert.True(ConflictPoints.Yields(Plan, Conflicts, SbLeft, NbThrough));
    }

    [Fact]
    public void LinesApart_NeverMeet()
    {
        Assert.Null(ConflictPoints.Meet(NbThrough, ConflictPoints.CarBody, SbThrough with { Points = Line((-6f, 8f), (-6f, -8f)) }, ConflictPoints.CarBody));
    }
}
