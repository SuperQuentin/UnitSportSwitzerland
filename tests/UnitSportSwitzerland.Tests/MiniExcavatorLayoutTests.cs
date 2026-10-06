using Godot;
using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The mini excavator (#614) is the excavator over a smaller <see cref="ExcavatorSpec"/>: its reach
/// and depth are a 2.7 t machine's, its blade lifts and cuts, and the five things it keeps (slew,
/// three joints, blade) fit the 32 bits of a parked vehicle's flags and the four floats of a pose.
/// </summary>
public class MiniExcavatorLayoutTests
{
    private static readonly ExcavatorSpec Mini = MiniExcavatorLayout.Spec;

    [Fact]
    public void It_reaches_and_digs_like_a_mini()
    {
        var far = Mini.Edge(0f, Mini.StickMax, 0f);
        Assert.InRange(far.X, 4.0f, 5.2f);
        var deep = Mini.Edge(Mini.BoomMin, -1.0f, -1.0f);
        Assert.InRange(-deep.Y, 2.2f, 3.2f);
        var rest = Mini.Edge(Mini.RestBoom, Mini.RestStick, Mini.RestBucket);
        Assert.InRange(rest.Y, 0f, 1.5f);
        Assert.True(rest.X > MiniExcavatorLayout.HouseFront, $"the parked bucket is {rest.X:F1} m out");
    }

    [Fact]
    public void The_blade_rests_on_the_ground_lifts_a_foot_and_cuts_in()
    {
        Assert.Equal(0f, MiniExcavatorLayout.BladeEdge(0f), 3);
        Assert.InRange(MiniExcavatorLayout.BladeEdge(Mini.BladeMax), 0.25f, 0.45f);
        Assert.InRange(MiniExcavatorLayout.BladeEdge(Mini.BladeMin), -0.12f, -0.02f);
        // the blade stands ahead of the tracks
        Assert.True(MiniExcavatorLayout.BladePivot.Z + MiniExcavatorLayout.BladeReach.X > Mini.TrackHalfLength);
    }

    [Theory]
    [InlineData(0f, 0.35f, -2.2f, -1.4f, 0f)]
    [InlineData(1.6f, 1.15f, -0.4f, 0.7f, 0.6f)]
    [InlineData(-2.9f, -0.85f, -2.6f, -2.4f, -0.15f)]
    [InlineData(3.1f, 0.1f, -1.0f, -0.2f, 0.31f)]
    public void Parked_flags_keep_the_arm_and_the_blade(float slew, float boom, float stick, float bucket, float blade)
    {
        var (s, b, k, u, d) = Mini.Unpack(Mini.Pack(slew, boom, stick, bucket, blade));
        // seven bits of slew, eight of boom and stick, six of bucket, three of blade
        Assert.True(MathF.Abs(Mathf.AngleDifference(slew, s)) < 0.03f, $"slew {slew} came back {s}");
        Assert.True(MathF.Abs(boom - b) < 0.012f, $"boom {boom} came back {b}");
        Assert.True(MathF.Abs(stick - k) < 0.012f, $"stick {stick} came back {k}");
        Assert.True(MathF.Abs(bucket - u) < 0.03f, $"bucket {bucket} came back {u}");
        Assert.True(MathF.Abs(blade - d) < 0.06f, $"blade {blade} came back {d}");
    }

    [Fact]
    public void A_fresh_mini_parks_in_its_rest_pose_blade_down()
    {
        Assert.Equal((0f, Mini.RestBoom, Mini.RestStick, Mini.RestBucket, 0f), Mini.Unpack(0));
        Assert.NotEqual(0, Mini.Pack(0f, Mini.BoomMin, Mini.StickMin, Mini.BucketMin, Mini.BladeMin));
    }

    [Theory]
    [InlineData(-2.4f, -0.15f)]
    [InlineData(0.7f, 0.6f)]
    [InlineData(-1.0f, 0.2f)]
    [InlineData(0f, 0f)]
    public void The_pose_carries_the_blade_in_the_buckets_float(float bucket, float blade)
    {
        var (u, d) = Mini.FromPoseW(Mini.PoseW(bucket, blade));
        Assert.Equal(bucket, u, 3);
        // 32 steps over the blade's travel
        Assert.True(MathF.Abs(blade - d) < 0.013f, $"blade {blade} came back {d}");
    }

    [Fact]
    public void The_big_ones_pose_and_flags_are_unchanged()
    {
        var big = ExcavatorLayout.Spec;
        Assert.Equal(-1.3f, big.PoseW(-1.3f, 0.5f));
        Assert.Equal((-1.3f, 0f), big.FromPoseW(-1.3f));
        // #611's packing, bit for bit: the rest pose's word
        static int Q(float v, float lo, float hi) => Mathf.Clamp(Mathf.RoundToInt((v - lo) / (hi - lo) * 254f), 0, 254) + 1;
        int old = Q(0f, 0f, Mathf.Tau) | Q(0.25f, big.BoomMin, big.BoomMax) << 8 | Q(-2.2f, big.StickMin, big.StickMax) << 16 | Q(-1.3f, big.BucketMin, big.BucketMax) << 24;
        Assert.Equal(old, ExcavatorLayout.Pack(0f, 0.25f, -2.2f, -1.3f));
    }
}
