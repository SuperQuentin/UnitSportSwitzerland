using UnitSport.Terrain.Format;
using UnitSport.World;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The traffic modes' shared base (#1060): which conflict points each kind of mover answers (a car waits at a point only for
/// movers of the point's kind), the names <c>--trafficcheck --mover</c> takes, and the room left for trams in the cars' yields.
/// </summary>
public class TrafficModesTests
{
    [Fact]
    public void EachMode_AnswersItsOwnConflictPoints()
    {
        Assert.Equal(ConflictMover.Walker, MoverKinds.Conflict(MoverKind.Walker));
        Assert.Equal(ConflictMover.Bike, MoverKinds.Conflict(MoverKind.Rider));
        Assert.Equal(ConflictMover.Tram, MoverKinds.Conflict(MoverKind.Tram));
        Assert.Equal(ConflictMover.Car, MoverKinds.Conflict(MoverKind.Car));
        Assert.Null(MoverKinds.Conflict(MoverKind.Train));
        Assert.Equal([MoverKind.Walker, MoverKind.Rider, MoverKind.Tram], MoverKinds.Modes);
    }

    [Theory]
    [InlineData("walker", MoverKind.Walker)]
    [InlineData("Pedestrians", MoverKind.Walker)]
    [InlineData("bike", MoverKind.Rider)]
    [InlineData("riders", MoverKind.Rider)]
    [InlineData("tram", MoverKind.Tram)]
    [InlineData("car", MoverKind.Car)]
    [InlineData("train", MoverKind.Train)]
    public void MoverNames_Parse(string name, MoverKind kind) => Assert.Equal(kind, MoverKinds.Parse(name));

    [Fact]
    public void UnknownOrNoMover_IsNone()
    {
        Assert.Null(MoverKinds.Parse(null));
        Assert.Null(MoverKinds.Parse("boat"));
    }

    // a crossroads, 20 m across (as ConflictPointsTests): a left turn from the south, and a line through from the north
    private static readonly SignalPlan Plan = SignalPlan.Build(
        [new(Math.PI / 2, true, true), new(0, true, true), new(-Math.PI / 2, true, true), new(Math.PI, true, true)], seed: 7);
    private static int CarGroup(int arm) => Plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == arm);
    private static float[] Line(params (float X, float Z)[] p) => p.SelectMany(q => new[] { q.X, 0f, q.Z }).ToArray();
    private static readonly ConflictWay Left = new(Line((1.75f, -8f), (1.75f, -2f), (-2f, 1.75f), (-8f, 1.75f)), CarGroup(2), ConflictMover.Car, SignalMoves.Left, 1.75f);
    private static readonly ConflictWay Oncoming = new(Line((-1.75f, 8f), (-1.75f, -8f)), CarGroup(0), ConflictMover.Car, SignalMoves.Through, 1.75f);

    [Fact]
    public void ATurningCar_GivesWayToATramOnAPermissiveConflict_AndMeetsItsWiderBodySooner()
    {
        var conflicts = Plan.Conflicts();
        var tram = Oncoming with { Mover = ConflictMover.Tram };
        Assert.True(ConflictPoints.Yields(Plan, conflicts, Left, tram));
        var car = ConflictPoints.For(Plan, conflicts, [Left, Oncoming], 0);
        var withTram = ConflictPoints.For(Plan, conflicts, [Left, tram], 0);
        Assert.Single(car);
        Assert.Single(withTram);
        // the tram's body (1.2 m about its line) comes into the turning car's lane before a car's (1 m) would
        Assert.True(withTram[0].Enter <= car[0].Enter);
    }
}
