using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The aura battler's body (#914, src/Combat/AuraBody.cs, linked in): its pose, and the VR poses to strike.</summary>
public class AuraBodyTests
{
    private static readonly AuraMove[] Plan = { AuraMove.StareDown, AuraMove.SlowWalk, AuraMove.Siuuu };
    private static readonly AuraGrade[] Clean = { AuraGrade.Good, AuraGrade.Good, AuraGrade.Good };

    private static int Pose(AuraPhase phase, bool performing, double now, AuraGrade[]? grades = null, bool laughing = false, int won = 0) =>
        AuraBody.PoseFor(phase, performing, won, Plan, 10, 0.5, grades ?? Clean, now, laughing, out _);

    [Fact]
    public void The_pose_follows_the_battle()
    {
        Assert.Equal((int)AuraStance.Planning, Pose(AuraPhase.Plan, true, 5));
        Assert.Equal((int)AuraStance.Waiting, Pose(AuraPhase.Plan, false, 5));
        Assert.Equal((int)AuraStance.Waiting, Pose(AuraPhase.Perform, false, 11));
        Assert.Equal(AuraRules.PoseOf(AuraMove.StareDown), Pose(AuraPhase.Perform, true, 11));
        Assert.Equal(AuraRules.PoseOf(AuraMove.Siuuu), Pose(AuraPhase.Perform, true, 14.2));
        Assert.Equal((int)AuraStance.Laugh, Pose(AuraPhase.Perform, false, 11, laughing: true));
        Assert.Equal((int)AuraStance.Victory, Pose(AuraPhase.Done, false, 30, won: 1));
        Assert.Equal((int)AuraStance.Defeat, Pose(AuraPhase.Done, true, 30, won: -1));
    }

    [Fact]
    public void A_missed_cut_staggers_the_next_beat()
    {
        var missed = new[] { AuraGrade.Miss, AuraGrade.Good, AuraGrade.Good };
        Assert.Equal((int)AuraStance.Stumble, Pose(AuraPhase.Perform, true, 12.2, missed));
        Assert.Equal(AuraRules.PoseOf(AuraMove.SlowWalk), Pose(AuraPhase.Perform, true, 12.6, missed));
    }

    [Fact]
    public void The_slow_walk_steps_in_and_back()
    {
        AuraBody.PoseFor(AuraPhase.Perform, true, 0, Plan, 10, 0.5, Clean, 13.0, false, out float mid);
        AuraBody.PoseFor(AuraPhase.Perform, true, 0, Plan, 10, 0.5, Clean, 12.0, false, out float start);
        Assert.Equal(1f, mid, 3);
        Assert.Equal(0f, start, 3);
    }

    [Theory]
    // right hand (x, y, z), left hand (x, y, z), relative to the eyes: x right, y up, -z ahead
    [InlineData(0.25f, -0.62f, 0.05f, -0.25f, -0.62f, 0.05f, AuraGesture.HandsLow)]
    [InlineData(0.20f, 0.30f, -0.10f, -0.25f, -0.62f, 0.05f, AuraGesture.OneUp)]
    [InlineData(0.20f, 0.30f, -0.10f, -0.20f, 0.30f, -0.10f, AuraGesture.BothUp)]
    [InlineData(-0.10f, -0.40f, -0.20f, 0.10f, -0.40f, -0.20f, AuraGesture.Crossed)]
    [InlineData(0.05f, -0.10f, -0.15f, -0.25f, -0.62f, 0.05f, AuraGesture.Face)]
    [InlineData(0.20f, -0.20f, -0.50f, -0.20f, -0.20f, -0.50f, AuraGesture.BothForward)]
    [InlineData(0.60f, -0.20f, 0.00f, -0.60f, -0.20f, 0.00f, AuraGesture.Out)]
    public void Hands_make_the_gesture(float rx, float ry, float rz, float lx, float ly, float lz, AuraGesture expected) =>
        Assert.True(AuraBody.Classify(rx, ry, rz, lx, ly, lz).HasFlag(expected), AuraBody.Classify(rx, ry, rz, lx, ly, lz).ToString());

    [Fact]
    public void Each_move_has_a_pose_to_strike()
    {
        foreach (var m in AuraRules.Moves) Assert.NotEqual(AuraGesture.None, AuraBody.GestureOf(m));
        var face = AuraBody.Classify(0.05f, -0.10f, -0.15f, -0.25f, -0.62f, 0.05f);
        Assert.True(AuraBody.Strikes(AuraMove.Sunglasses, face));
        Assert.False(AuraBody.Strikes(AuraMove.Yawn, face));
        var rest = AuraBody.Classify(0.25f, -0.62f, 0.05f, -0.25f, -0.62f, 0.05f);
        Assert.False(AuraBody.Strikes(AuraMove.ArmsCrossed, rest));
        Assert.True(AuraBody.Strikes(AuraMove.None, rest));   // dropping the last pose: hands down
    }
}
