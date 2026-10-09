using Godot;
using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="ExcavatorLayout"/> (#611): the arm's reach and depth are a 20 t machine's, its parked
/// flags keep the arm whole, and its tracks counter-rotate to turn on the spot.
/// </summary>
public class ExcavatorLayoutTests
{
    [Fact]
    public void It_reaches_and_digs_like_a_twenty_tonne_machine()
    {
        // boom level, stick out, bucket in line: the furthest it reaches
        var far = ExcavatorLayout.Edge(0f, ExcavatorLayout.StickMax, 0f);
        Assert.InRange(far.X, 9.5f, 11.5f);
        // boom down, stick and bucket folded under: the deepest it digs
        var deep = ExcavatorLayout.Edge(ExcavatorLayout.BoomMin, -1.0f, -1.0f);
        Assert.InRange(-deep.Y, 5f, 7f);
        // boom up, stick out: high enough to load a lorry over its side (about 6 m)
        var high = ExcavatorLayout.Edge(ExcavatorLayout.BoomMax, ExcavatorLayout.StickMax, 0f);
        Assert.True(high.Y > 6f, $"{high.Y:F1} m");
    }

    [Fact]
    public void The_rest_pose_has_the_bucket_on_or_near_the_ground_in_front()
    {
        var rest = ExcavatorLayout.Edge(ExcavatorLayout.RestBoom, ExcavatorLayout.RestStick, ExcavatorLayout.RestBucket);
        Assert.InRange(rest.Y, -0.5f, 2.5f);
        Assert.True(rest.X > ExcavatorLayout.HouseFront, $"the parked bucket is {rest.X:F1} m out, under the cab");
    }

    [Theory]
    [InlineData(0f, 0.25f, -2.2f, -1.3f)]
    [InlineData(1.6f, 1.05f, -0.45f, 0.6f)]
    [InlineData(-2.9f, -0.75f, -2.55f, -2.4f)]
    [InlineData(3.1f, 0.1f, -1.0f, -0.2f)]
    public void Parked_flags_keep_the_arm(float slew, float boom, float stick, float bucket)
    {
        var (s, b, k, u) = ExcavatorLayout.Unpack(ExcavatorLayout.Pack(slew, boom, stick, bucket));
        Assert.True(MathF.Abs(Mathf.AngleDifference(slew, s)) < 0.02f, $"slew {slew} came back {s}");
        // eight bits over each joint's travel: under 0.012 rad
        Assert.True(MathF.Abs(boom - b) < 0.012f, $"boom {boom} came back {b}");
        Assert.True(MathF.Abs(stick - k) < 0.012f, $"stick {stick} came back {k}");
        Assert.True(MathF.Abs(bucket - u) < 0.012f, $"bucket {bucket} came back {u}");
        Assert.InRange(s, -MathF.PI, MathF.PI);
    }

    [Fact]
    public void A_fresh_machine_parks_in_its_rest_pose()
    {
        var (s, b, k, u) = ExcavatorLayout.Unpack(0);
        Assert.Equal((0f, ExcavatorLayout.RestBoom, ExcavatorLayout.RestStick, ExcavatorLayout.RestBucket), (s, b, k, u));
        Assert.NotEqual(0, ExcavatorLayout.Pack(0f, ExcavatorLayout.BoomMin, ExcavatorLayout.StickMin, ExcavatorLayout.BucketMin));
    }

    [Fact]
    public void Turning_on_the_spot_runs_one_track_back_and_the_other_forward()
    {
        var (l, r) = ExcavatorLayout.Tracks(0f, ExcavatorLayout.TurnRate);
        Assert.True(l < 0 && r > 0);
        Assert.Equal(-l, r, 3);
        var (l2, r2) = ExcavatorLayout.Tracks(1.2f, 0f);
        Assert.Equal(1.2f, l2, 3);
        Assert.Equal(1.2f, r2, 3);
    }
}
