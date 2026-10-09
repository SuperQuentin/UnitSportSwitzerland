using Godot;
using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The mini dumper (#614): its tracks counter-rotate to turn on the spot, the skip tips forward
/// about its front hinge (its back rising, its floor pouring out ahead of the tracks), and the
/// parked flags keep the skip's state, zero meaning down.
/// </summary>
public class MiniDumperLayoutTests
{
    [Fact]
    public void Standing_the_tracks_run_against_each_other()
    {
        var (left, right) = MiniDumperLayout.Tracks(0f, MiniDumperLayout.TurnRate);
        Assert.Equal(-left, right, 5);
        Assert.True(right > 0f);
        var (l2, r2) = MiniDumperLayout.Tracks(MiniDumperLayout.TopSpeed, 0f);
        Assert.Equal(l2, r2);
    }

    /// <summary>A point of the skip (authored) once tipped: turned about the hinge's X axis by the tip angle, as the rig turns it in node space.</summary>
    private static Vector3 Tipped(Vector3 authored)
    {
        // node space is the authored frame turned half round Y: a rotation about X keeps its sign there
        var node = new Vector3(-authored.X, authored.Y, -authored.Z);
        var hinge = new Vector3(-MiniDumperLayout.Hinge.X, MiniDumperLayout.Hinge.Y, -MiniDumperLayout.Hinge.Z);
        var turned = hinge + new Basis(Vector3.Right, MiniDumperLayout.TipAngle) * (node - hinge);
        return new Vector3(-turned.X, turned.Y, -turned.Z);
    }

    [Fact]
    public void Tipped_the_skip_raises_its_back_and_pours_ahead_of_the_tracks()
    {
        var backTop = new Vector3(0, MiniDumperLayout.SkipTop, MiniDumperLayout.SkipBack);
        var tipped = Tipped(backTop);
        Assert.True(tipped.Y - backTop.Y > 0.6f, $"the back rises {tipped.Y - backTop.Y:F2} m");
        // the back wall comes over the hinge: the load falls ahead of the tracks, not on them
        Assert.True(tipped.Z > MiniDumperLayout.TrackHalfLength, $"the back ends {tipped.Z:F2} m ahead");
        // the floor slopes down toward the front, steeper than a load lies (about 35°)
        var floorBack = Tipped(new Vector3(0, MiniDumperLayout.SkipFloor, MiniDumperLayout.SkipBack));
        float slope = Mathf.Atan2(floorBack.Y - MiniDumperLayout.Hinge.Y, floorBack.Z - MiniDumperLayout.Hinge.Z);
        Assert.True(Mathf.Abs(slope) > Mathf.DegToRad(50f), $"the floor at {Mathf.RadToDeg(slope):F0}°");
        // and nothing of it goes into the ground
        Assert.True(Tipped(new Vector3(0, MiniDumperLayout.SkipTop, MiniDumperLayout.SkipFront)).Y > 0.05f);
    }

    [Fact]
    public void The_skip_sits_within_the_machine_s_width()
    {
        Assert.True(MiniDumperLayout.SkipHalf <= MiniDumperLayout.HalfWidth + 0.02f);
        Assert.True(MiniDumperLayout.HalfGauge + MiniDumperLayout.ShoeWidth * 0.5f <= MiniDumperLayout.HalfWidth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parked_flags_keep_the_skip(bool tipped)
    {
        int flags = MiniDumperLayout.Pack(tipped);
        Assert.NotEqual(0, flags);
        Assert.Equal(tipped, MiniDumperLayout.Unpack(flags));
    }

    [Fact]
    public void A_fresh_dumper_parks_with_its_skip_down() => Assert.False(MiniDumperLayout.Unpack(0));
}
