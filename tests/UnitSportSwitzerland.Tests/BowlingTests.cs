using UnitSport.Interiors;
using Xunit;
using static UnitSport.Interiors.BowlingBall;

namespace UnitSport.Tests;

/// <summary>Bowling (#1323): the ball's run down the lane (src/Interiors/Bowling/BowlingBall.cs) and the score.</summary>
public class BowlingTests
{
    private static readonly BowlingSizes.Run Lane = BowlingSizes.Run.Full;

    [Fact]
    public void AStraightBallWithNoSideSpinRunsStraightIntoTheHeadPin()
    {
        var run = Roll(Lane, new Release(0, 0, 8f, 30f, 0));
        Assert.False(run.Gutter);
        Assert.InRange(run.AtDeck.Pos.X, -0.005f, 0.005f);
        // 18 m at about 8 m/s, a little slowed by the skid
        Assert.InRange(run.Time, 2.0f, 2.6f);
    }

    [Fact]
    public void SideRotationHooksOnTheDryBackEndAndTheBallEndsRolling()
    {
        // a right-hander: out to the right (−x as they face the pins), the hook brings it back left into the pocket
        var run = Roll(Lane, new Release(-0.25f, -0.015f, 8f, 30f, 0.5f));
        Assert.False(run.Gutter);
        float atOil = run.At(1.5f).Pos.X;
        Assert.InRange(atOil, -0.36f, -0.2f);
        Assert.True(run.AtDeck.Pos.X > atOil + 0.15f, $"no hook: {atOil} -> {run.AtDeck.Pos.X}");
        // into the pins at a real entry angle, 2-8 degrees
        float entry = MathF.Atan2(run.AtDeck.Vel.X, run.AtDeck.Vel.Z) * 180 / MathF.PI;
        Assert.InRange(entry, 2f, 8f);
        // rolling at the end: the contact point no longer slips
        var s = run.AtDeck;
        float slipX = s.Vel.X + BowlingSizes.BallRadius * s.Spin.Z, slipZ = s.Vel.Z - BowlingSizes.BallRadius * s.Spin.X;
        Assert.True(MathF.Sqrt(slipX * slipX + slipZ * slipZ) < 0.05f);
    }

    [Fact]
    public void TheHookMirrorsForALeftHander()
    {
        var right = Roll(Lane, new Release(-0.25f, -0.015f, 8f, 30f, 0.5f));
        var left = Roll(Lane, new Release(0.25f, 0.015f, 8f, 30f, -0.5f));
        Assert.Equal(right.AtDeck.Pos.X, -left.AtDeck.Pos.X, 3);
    }

    [Fact]
    public void ABallAimedOffTheLaneGoesIntoTheGutterAndRunsToThePit()
    {
        var run = Roll(Lane, new Release(0.4f, 0.03f, 7f, 20f, 0));
        Assert.True(run.Gutter);
        Assert.True(run.GutterAt < run.Time);
        Assert.True(run.AtDeck.Pos.Z > Lane.LaneEnd);
    }

    [Fact]
    public void TheSameReleaseGivesTheSameRunAfterTheWire()
    {
        var release = new Release(0.21f, -0.0083f, 7.6f, 27.5f, 0.45f);
        var sent = Release.Unpack(release.Pack());
        var a = Roll(Lane, sent);
        var b = Roll(Lane, Release.Unpack(sent.Pack()));
        Assert.Equal(a.AtDeck, b.AtDeck);
        Assert.Equal(a.Pos.Count, b.Pos.Count);
    }

    [Fact]
    public void MiniLanesAreShorter()
    {
        var mini = new BowlingSizes.Run(9.4f - 0.868f * 9.4f / 19.2f, 9.4f, 9.4f / 19.2f);
        Assert.True(Roll(mini, new Release(0, 0, 6f, 20f, 0)).Time < Roll(Lane, new Release(0, 0, 6f, 20f, 0)).Time / 1.7f);
    }

    [Theory]
    [InlineData(new[] { 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10 }, 300)]
    [InlineData(new[] { 9, 0, 9, 0, 9, 0, 9, 0, 9, 0, 9, 0, 9, 0, 9, 0, 9, 0, 9, 0 }, 90)]
    [InlineData(new[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }, 150)]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0)]
    [InlineData(new[] { 10, 7, 3, 9, 0, 10, 0, 8, 8, 2, 0, 6, 10, 10, 10, 8, 1 }, 167)]
    public void AWholeGameScoresAsOnARealSheet(int[] rolls, int total)
    {
        Assert.True(BowlingScore.Over(rolls));
        Assert.Equal(total, BowlingScore.Total(rolls));
        Assert.Equal(total, BowlingScore.Frames(rolls)[9].Total);
    }

    [Fact]
    public void AStrikeWaitsForItsTwoBonusBalls()
    {
        var f = BowlingScore.Frames(new[] { 10, 3 });
        Assert.Null(f[0].Total);
        Assert.Equal(new[] { "X" }, f[0].Marks);
        f = BowlingScore.Frames(new[] { 10, 3, 4 });
        Assert.Equal(17, f[0].Total);
        Assert.Equal(24, f[1].Total);
    }

    [Fact]
    public void TheMarksShowSparesMissesAndTheTenthsThirdBall()
    {
        var rolls = new[] { 0, 10, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9, 1, 10 };
        var f = BowlingScore.Frames(rolls);
        Assert.Equal(new[] { "-", "/" }, f[0].Marks);
        Assert.Equal(new[] { "4", "-" }, f[1].Marks);
        Assert.Equal(new[] { "9", "/", "X" }, f[9].Marks);
        Assert.True(BowlingScore.Over(rolls));
    }

    [Fact]
    public void ThePinsetterResetsOnlyForAFreshRack()
    {
        Assert.True(BowlingScore.FreshRack(Array.Empty<int>()));
        Assert.False(BowlingScore.FreshRack(new[] { 7 }));
        Assert.Equal(3, BowlingScore.Standing(new[] { 7 }));
        Assert.True(BowlingScore.FreshRack(new[] { 10 }));
        // the tenth: a strike gives a full rack for the second ball, a spare for the third
        var nine = Enumerable.Repeat(10, 9).ToArray();
        Assert.True(BowlingScore.FreshRack(nine.Append(10).ToArray()));
        Assert.False(BowlingScore.FreshRack(nine.Append(10).Append(4).ToArray()));
        Assert.True(BowlingScore.FreshRack(nine.Append(6).Append(4).ToArray()));
        Assert.Equal((9, 2), BowlingScore.Next(nine.Append(6).Append(4).ToArray()));
        Assert.Equal((10, 0), BowlingScore.Next(nine.Append(6).Append(3).ToArray()));
    }
}
