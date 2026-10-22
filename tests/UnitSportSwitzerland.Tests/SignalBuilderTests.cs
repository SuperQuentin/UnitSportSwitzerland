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
            // (the east approach's left turn has its lamp on this pole, by the south crosswalk it crosses: not this head's)
            // one lamp per turn (#1117): the right turn's for the east crossing (a walker) beside the head on the main pole, the left
            // turn's for the west crossing and the oncoming cars (a mix: its arrow), on the head's left (no pole by the west
            // crosswalk here) or on the left pole beside its head; none of its own for the oncoming cars
            var mine = lamps.Lenses.Where(l => l.Group == amber.Group && l.Role == SignalBuilder.Role.Flash).Select(l => (l.Plate, l.PlateArrow)).ToList();
            if (flags == SignalPoleFlags.Main)
                Assert.Equal([(SignalPlate.Pedestrian, SignalMoves.Right), (SignalPlate.Arrow, SignalMoves.Left)], mine.OrderBy(x => x.PlateArrow == SignalMoves.Left));
            else Assert.Equal([(SignalPlate.Arrow, SignalMoves.Left)], mine);
        }
        // a left pocket's arrow is protected: nothing flashes for it
        var pockets = Lamps(left: true, right: true, pedAmber: true, SignalPoleFlags.Main);
        Assert.DoesNotContain(pockets.Lenses, l => l.Blink is not null);
    }

    [Fact]
    public void PermissiveLeft_OnACombinedArrow_AFlasherOnItsLeft()
    {
        // #1109: a right pocket going with its approach leaves the main head straight on and left, an arrow: its yellow stays, a
        // flasher stands on its left, lit by the left's flashing; on the main pole only
        var lamps = Lamps(left: false, right: true, pedAmber: true, SignalPoleFlags.Main);
        var plan = lamps.Plans[0];
        var main = lamps.Lenses.First(l => l.Shape == SignalBuilder.Shape.Circle && l.Role == SignalBuilder.Role.Green
            && (plan.Groups[l.Group].Moves & SignalMoves.Left) != 0);
        Assert.Equal(SignalMoves.Left | SignalMoves.Through, SignalBuilder.ArrowMoves(plan, main.Group));
        Assert.DoesNotContain(lamps.Lenses, l => l.Group == main.Group && l.Role is SignalBuilder.Role.Amber or SignalBuilder.Role.Green && l.Blink is not null);
        var left = Assert.Single(lamps.Lenses, l => l.Group == main.Group && l.Role == SignalBuilder.Role.Flash && l.Blink is not null
            && l is { Plate: SignalPlate.Arrow, PlateArrow: SignalMoves.Left });
        // on its left as the driver sees it (west, a smaller X), level with its green
        Assert.True(left.Transform.Origin.X < main.Transform.Origin.X - 0.5f, $"left flasher at {left.Transform.Origin.X}, head at {main.Transform.Origin.X}");
        Assert.Equal(main.Transform.Origin.Y, left.Transform.Origin.Y, 3);
        var second = Lamps(left: false, right: true, pedAmber: true, SignalPoleFlags.Second);
        // (an approach's left pole carries its left turn's lamp, #1117: the same one, not a second)
        Assert.Single(second.Lenses, l => l.Group == main.Group && l.Role == SignalBuilder.Role.Flash && l.Plate == SignalPlate.Arrow);
    }

    [Fact]
    public void PermissiveTurn_OnAnUncombinedArrow_FlashesItsYellowForItsGreen()
    {
        // #1109, the user: "for flashing arrows it takes the place of the green arrow". The right lane's head beside the main one
        // shows the right arrow alone; its turn crosses the walkers: its green and yellow carry the flashing, no lamp beside it
        var lamps = Lamps(left: false, right: true, pedAmber: true, SignalPoleFlags.Main);
        var plan = lamps.Plans[0];
        int lane = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == 2 && g.Moves == SignalMoves.Right);
        Assert.Equal(SignalMoves.Right, SignalBuilder.ArrowMoves(plan, lane));
        var green = Assert.Single(lamps.Lenses, l => l.Group == lane && l.Role == SignalBuilder.Role.Green);
        var amber = Assert.Single(lamps.Lenses, l => l.Group == lane && l.Role == SignalBuilder.Role.Amber);
        Assert.NotNull(green.Blink);
        Assert.Same(green.Blink, amber.Blink);
        int flasher = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Flasher && g.Qualifies == lane);
        Assert.DoesNotContain(lamps.Lenses, l => l.Role == SignalBuilder.Role.Flash && l.Group == flasher);
        // flashing only during its green
        for (double t = 0.05; t < plan.Cycle; t += 0.1)
            Assert.True(plan.State(green.Blink!, t - plan.Offset) != SignalAspect.FlashingAmber || plan.State(lane, t - plan.Offset) == SignalAspect.Green);
    }

    [Fact]
    public void PermissiveLeft_OnAnUncombinedOverheadArrow_FlashesItsYellowForItsGreen()
    {
        // an overhead head over the left lane of an approach without a left pocket: the left arrow alone, its left giving way
        var arms = Enumerable.Range(0, 4).Select(i => new SignalArm(Headings[i], true, true, Pedestrians: true)).ToList();
        var plan = SignalPlan.Build(arms);
        var signal = new RoadSignal { X = 500, Y = 400, Z = 500, Stops = new float[12], Plan = plan };
        signal.Overheads.Add(new SignalOverhead
        {
            Kind = OverheadKind.Arm, Arm = 2, X = 505, Y = 400.1f, Z = 512, EndX = 498, EndY = 400.1f, EndZ = 512, Bottom = 405f,
            Heading = (float)Math.PI, Heads = [new OverheadHead(4f, SignalMoves.Left)],
        });
        var tile = new RoadTile { Id = new TileId(2500, 1117), Segments = [], Signals = [signal] };
        var lamps = SignalBuilder.BuildLamps(tile)!;
        var green = Assert.Single(lamps.Lenses, l => l.Role == SignalBuilder.Role.Green);
        Assert.Equal(SignalMoves.Left, green.Arrow);
        Assert.NotNull(green.Blink);
        Assert.Contains(Enumerable.Range(0, (int)(plan.Cycle * 10)).Select(k => k * 0.1),
            t => plan.State(green.Blink!, t) == SignalAspect.FlashingAmber);
    }

    [Fact]
    public void LeftTurnsFlasher_StandsByTheLeftCrosswalk()
    {
        // #1109, the user: "right flasher here, left flasher on a pole next to the left crosswalk". No pockets: the northbound ball
        // turns right across the east crossing and left across the west one. Without a left pole of its own, the left turn's lamp
        // stands on the west arm's far-side pole (#1117, the user: "for a turn-into flasher, it should be on the far side pole":
        // the north-west corner, across the junction, where the turning car arrives), facing the northbound drivers
        var arms = Enumerable.Range(0, 4).Select(i => new SignalArm(Headings[i], true, true, Pedestrians: true)).ToList();
        var plan = SignalPlan.Build(arms);
        SignalBuilder.Lamps Build(params SignalPole[] poles) => SignalBuilder.BuildLamps(new RoadTile
        {
            Id = new TileId(2500, 1117), Segments = [],
            Signals = [new RoadSignal { X = 500, Y = 400, Z = 500, Stops = new float[12], Plan = plan, Poles = [.. poles] }],
        })!;
        var southMain = new SignalPole(505, 400.1f, 512, (float)Math.PI, (float)(Math.PI / 2), 2, SignalPoleFlags.Main);
        var westMain = new SignalPole(494, 400.1f, 506, (float)(Math.PI / 2), 0f, 3, SignalPoleFlags.Main | SignalPoleFlags.Pedestrian);
        var westSecond = new SignalPole(494, 400.1f, 494, (float)(Math.PI / 2), (float)Math.PI, 3, SignalPoleFlags.Second | SignalPoleFlags.Pedestrian);
        int nb = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == 2);
        int f = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Flasher && g.Qualifies == nb);
        // (the left turn also gives way to the oncoming cars: a mix, its arrow, #1117)
        Assert.Equal((SignalPlate.Arrow, SignalMoves.Left), plan.PlateOf(f, turns: SignalMoves.Left));
        Assert.Equal(SignalPlate.Pedestrian, plan.PlateOf(f, turns: SignalMoves.Right).Plate);
        var lamps = Build(southMain, westMain, westSecond);
        var mine = lamps.Lenses.Where(l => l.Role == SignalBuilder.Role.Flash && l.Group == nb).ToList();
        Assert.Equal(2, mine.Count);
        var left = Assert.Single(mine, l => l.Transform.Origin.X < 497);
        var right = Assert.Single(mine, l => l.Transform.Origin.X > 505);
        // the left one faces the northbound drivers (south, +Z), beside the west arm's far pole on its side away from the west exit
        // (east of it, the user: "on the side of the pole, pointing away from the exit"), at a car head's green height (the pole has
        // a walkers' head: 3.35 m + half a lens pitch), clear of that pole's own lenses; each flashing for its own turn's crossing
        Assert.True(left.Transform.Basis.Z.Z > 0.99f, $"faces {left.Transform.Basis.Z}");
        Assert.True(Math.Abs(left.Transform.Origin.Z - 494) < 0.3f && left.Transform.Origin.X > 494.3f && left.Transform.Origin.X < 494.8f, $"at {left.Transform.Origin}");
        Assert.Equal(400.1f + 3.35f + 0.15f, left.Transform.Origin.Y, 2);
        var poleLenses = lamps.Lenses.Where(l => l.Group != nb && Math.Abs(l.Transform.Origin.Z - 494) < 1f && Math.Abs(l.Transform.Origin.X - 494) < 1f).ToList();
        Assert.NotEmpty(poleLenses);
        Assert.All(poleLenses, l => Assert.True((l.Transform.Origin - left.Transform.Origin).Length() > 0.3f, $"lens at {l.Transform.Origin}"));
        Assert.NotNull(left.Blink);
        Assert.NotNull(right.Blink);
        Assert.NotSame(left.Blink, right.Blink);
        // with a left pole of its own, the lamp stands there, left of its car head
        var southSecond = new SignalPole(495, 400.1f, 512, (float)Math.PI, -(float)(Math.PI / 2), 2, SignalPoleFlags.Second);
        var withSecond = Build(southMain, westMain, westSecond, southSecond);
        var onSecond = Assert.Single(withSecond.Lenses, l => l.Role == SignalBuilder.Role.Flash && l.Group == nb && l.Transform.Origin.X < 497);
        var secondHead = withSecond.Lenses.Where(l => l.Group == nb && l.Role == SignalBuilder.Role.Green && l.Transform.Origin.X < 500).Single();
        Assert.True(onSecond.Transform.Origin.X < secondHead.Transform.Origin.X - 0.5f && Math.Abs(onSecond.Transform.Origin.Z - secondHead.Transform.Origin.Z) < 0.01f);
        // the arm turned into has no far-side pole: its main pole at the near corner
        var near = Build(southMain, westMain);
        var onNear = Assert.Single(near.Lenses, l => l.Role == SignalBuilder.Role.Flash && l.Group == nb && l.Transform.Origin.X < 497);
        Assert.True(Math.Abs(onNear.Transform.Origin.Z - 506) < 0.6f, $"at {onNear.Transform.Origin}");
        // no pole by the left crosswalk: the left turn's lamp on the head's left, the right turn's on its right (one lamp per turn)
        var alone = Build(southMain);
        var both = alone.Lenses.Where(l => l.Role == SignalBuilder.Role.Flash && l.Group == nb).OrderBy(l => l.Transform.Origin.X).ToList();
        Assert.Equal(2, both.Count);
        Assert.Equal((SignalPlate.Arrow, SignalMoves.Left), (both[0].Plate, both[0].PlateArrow));
        Assert.Equal(SignalPlate.Pedestrian, both[1].Plate);
        Assert.True(both[0].Transform.Origin.X < 505 && both[1].Transform.Origin.X > 505);
    }
}
