using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Traffic-light heads (#350): which lenses a pole carries, from the plan of its arm. Arms: 0 north,
/// 1 east, 2 south, 3 west; poles stand on the south arm (the northbound approach).
/// </summary>
public class SignalBuilderTests
{
    private static readonly double[] Headings = [Math.PI / 2, 0, -Math.PI / 2, Math.PI];

    private static SignalBuilder.Lamps Lamps(bool left, bool right, bool pedAmber, SignalPoleFlags flags, bool bike = false)
    {
        var arms = Enumerable.Range(0, 4).Select(i => new SignalArm(Headings[i], true, true, left, right, Pedestrians: true, BikeSignal: bike)).ToList();
        var plan = SignalPlan.Build(arms, pedestrianAmber: pedAmber);
        var tile = new RoadTile
        {
            Id = new TileId(2500, 1117),
            Segments = [],
            Signals = [new RoadSignal { X = 500, Y = 400, Z = 500, Stops = new float[12], Plan = plan,
                Poles = [new SignalPole(505, 400.1f, 512, (float)Math.PI, (float)(Math.PI / 2), 2, flags)] }],
        };
        return SignalBuilder.BuildLamps(tile)!;
    }

    private static int Count(SignalBuilder.Lamps lamps, SignalBuilder.Shape shape) => lamps.Lenses.Count(l => l.Shape == shape);

    [Fact]
    public void MainPole_LeftArrow_MainHead_RightArrow_PedestrianHead()
    {
        var lamps = Lamps(left: true, right: true, pedAmber: true, SignalPoleFlags.Main | SignalPoleFlags.Pedestrian);
        Assert.Equal(3, Count(lamps, SignalBuilder.Shape.LeftArrow));
        Assert.Equal(3, Count(lamps, SignalBuilder.Shape.RightArrow));
        Assert.Equal(3, Count(lamps, SignalBuilder.Shape.Square));   // a 3-lens pedestrian head (Vaud)
        Assert.True(Count(lamps, SignalBuilder.Shape.Circle) >= 3);
        // every lens lights from a group of the south approach, or its crossing
        var plan = lamps.Plans[0];
        Assert.All(lamps.Lenses, l => Assert.Equal(2, plan.Groups[l.Group].Arm));
        // car heads face south (toward the drivers), the pedestrian head west
        var car = lamps.Lenses.First(l => l.Shape == SignalBuilder.Shape.LeftArrow).Transform.Basis.Z;
        var ped = lamps.Lenses.First(l => l.Shape == SignalBuilder.Shape.Square).Transform.Basis.Z;
        Assert.True(car.Z > 0.99f, $"car heads face south: {car}");
        Assert.True(ped.X < -0.99f, $"pedestrian head faces west: {ped}");
        // the left arrow stands left of the main head as the driver sees it (west, at a smaller X)
        float arrowX = lamps.Lenses.First(l => l.Shape == SignalBuilder.Shape.LeftArrow).Transform.Origin.X;
        float rightX = lamps.Lenses.First(l => l.Shape == SignalBuilder.Shape.RightArrow).Transform.Origin.X;
        Assert.True(arrowX < 505 && rightX > 505, $"left arrow at {arrowX}, right arrow at {rightX}");
    }

    [Fact]
    public void MainHead_ABallOnlyWhereItGivesEveryMove_ElseItsArrow()
    {
        // left and right pockets with their own arrows: the main head is straight on alone (#759)
        var pockets = Lamps(left: true, right: true, pedAmber: true, SignalPoleFlags.Main);
        var main = pockets.Lenses.First(l => l.Shape == SignalBuilder.Shape.Circle && l.Role != SignalBuilder.Role.Flash);
        Assert.Equal(SignalMoves.Through, SignalBuilder.ArrowMoves(pockets.Plans[0], main.Group));
        // no pockets: one head for every move, a ball
        var single = Lamps(left: false, right: false, pedAmber: true, SignalPoleFlags.Main);
        var ball = single.Lenses.First(l => l.Shape == SignalBuilder.Shape.Circle && l.Role != SignalBuilder.Role.Flash);
        Assert.Equal(SignalMoves.None, SignalBuilder.ArrowMoves(single.Plans[0], ball.Group));
        // the pockets' own arrow heads are not car groups: no second arrow drawn over theirs
        var arrow = pockets.Lenses.First(l => l.Shape == SignalBuilder.Shape.LeftArrow);
        Assert.Equal(SignalMoves.None, SignalBuilder.ArrowMoves(pockets.Plans[0], arrow.Group));
    }

    [Fact]
    public void SecondPole_NeverTheRightArrow()
    {
        var lamps = Lamps(left: true, right: true, pedAmber: true, SignalPoleFlags.Second);
        Assert.Equal(3, Count(lamps, SignalBuilder.Shape.LeftArrow));
        Assert.Equal(0, Count(lamps, SignalBuilder.Shape.RightArrow));
        Assert.Equal(0, Count(lamps, SignalBuilder.Shape.Square));
    }

    [Fact]
    public void GenevaPedestrianHead_HasTwoLenses_AndPermissiveTurnsAFlasher()
    {
        var lamps = Lamps(left: false, right: false, pedAmber: false, SignalPoleFlags.Main | SignalPoleFlags.Pedestrian);
        Assert.Equal(2, Count(lamps, SignalBuilder.Shape.Square));
        Assert.Contains(lamps.Lenses, l => l.Role == SignalBuilder.Role.Flash);
        Assert.DoesNotContain(lamps.Lenses, l => l.Shape == SignalBuilder.Shape.Square && l.Role == SignalBuilder.Role.Amber);
    }

    [Fact]
    public void BikeHead_LowOnTheMainPole_NotOnTheSecond()
    {
        var main = Lamps(left: true, right: true, pedAmber: true, SignalPoleFlags.Main | SignalPoleFlags.Pedestrian, bike: true);
        var bikeLenses = main.Lenses.Where(l => l.Shape == SignalBuilder.Shape.Bike).ToList();
        Assert.Equal(3, bikeLenses.Count);
        Assert.All(bikeLenses, l => Assert.Equal(SignalGroupKind.Bike, main.Plans[0].Groups[l.Group].Kind));
        // below every car lens
        float carLow = main.Lenses.Where(l => l.Shape is SignalBuilder.Shape.Circle or SignalBuilder.Shape.LeftArrow).Min(l => l.Transform.Origin.Y);
        Assert.True(bikeLenses.Max(l => l.Transform.Origin.Y) < carLow);
        var second = Lamps(left: true, right: true, pedAmber: true, SignalPoleFlags.Second, bike: true);
        Assert.DoesNotContain(second.Lenses, l => l.Shape == SignalBuilder.Shape.Bike);
    }

    [Fact]
    public void PermissiveLeft_OnABall_FlashesTheHeadsOwnYellow()
    {
        // #1109: no pockets, the ball's left gives way to the oncoming cars: its yellow lens flashes with that, no lamp beside it
        foreach (var flags in new[] { SignalPoleFlags.Main, SignalPoleFlags.Second })
        {
            var lamps = Lamps(left: false, right: false, pedAmber: true, flags);
            var amber = Assert.Single(lamps.Lenses, l => l.Shape == SignalBuilder.Shape.Circle && l.Role == SignalBuilder.Role.Amber);
            Assert.NotNull(amber.Blink);
            Assert.Equal(amber.Group, amber.Blink!.Qualifies);
            Assert.DoesNotContain(lamps.Lenses, l => l.Role == SignalBuilder.Role.Flash && l.Blink is not null);
        }
        // a left pocket's arrow is protected: nothing flashes for it
        var pockets = Lamps(left: true, right: true, pedAmber: true, SignalPoleFlags.Main);
        Assert.DoesNotContain(pockets.Lenses, l => l.Blink is not null);
    }

    [Fact]
    public void PermissiveLeft_OnAnArrow_AFlasherOnItsLeft()
    {
        // #1109: a right pocket going with its approach leaves the main head straight on and left, an arrow: its yellow stays, a
        // flasher stands on its left, lit by the left's flashing; on the main pole only
        var lamps = Lamps(left: false, right: true, pedAmber: true, SignalPoleFlags.Main);
        var plan = lamps.Plans[0];
        var main = lamps.Lenses.First(l => l.Shape == SignalBuilder.Shape.Circle && l.Role == SignalBuilder.Role.Green
            && (plan.Groups[l.Group].Moves & SignalMoves.Left) != 0);
        Assert.Equal(SignalMoves.Left | SignalMoves.Through, SignalBuilder.ArrowMoves(plan, main.Group));
        Assert.DoesNotContain(lamps.Lenses, l => l.Role == SignalBuilder.Role.Amber && l.Blink is not null);
        var left = Assert.Single(lamps.Lenses, l => l.Role == SignalBuilder.Role.Flash && l.Blink is not null);
        Assert.Equal(main.Group, left.Group);
        // on its left as the driver sees it (west, a smaller X), level with its green
        Assert.True(left.Transform.Origin.X < main.Transform.Origin.X - 0.5f, $"left flasher at {left.Transform.Origin.X}, head at {main.Transform.Origin.X}");
        Assert.Equal(main.Transform.Origin.Y, left.Transform.Origin.Y, 3);
        var second = Lamps(left: false, right: true, pedAmber: true, SignalPoleFlags.Second);
        Assert.DoesNotContain(second.Lenses, l => l.Role == SignalBuilder.Role.Flash);
    }
}
