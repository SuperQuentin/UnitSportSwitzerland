using Godot;
using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="WheelLoaderLayout"/> (#612): the frame steering's kinematics, the arm's reach up to a
/// lorry's side, and the parked flags.
/// </summary>
public class WheelLoaderLayoutTests
{
    [Fact]
    public void Full_lock_turns_on_a_loaders_circle_and_the_right_way()
    {
        float r = WheelLoaderLayout.TurnRadius(WheelLoaderLayout.MaxArticulation);
        // a 17 t articulated loader turns its rear axle on about 4.5 to 6 m
        Assert.InRange(r, 4f, 6f);
        Assert.True(WheelLoaderLayout.YawRate(2f, 0.5f) > 0f, "front swung left, moving forward: it turns left");
        Assert.True(WheelLoaderLayout.YawRate(-2f, 0.5f) < 0f, "and backing, the other way");
        Assert.Equal(0f, WheelLoaderLayout.YawRate(3f, 0f));
        Assert.True(float.IsPositiveInfinity(WheelLoaderLayout.TurnRadius(0f)));
    }

    [Fact]
    public void Full_lift_puts_the_pin_over_a_lorrys_side()
    {
        Assert.InRange(WheelLoaderLayout.PinHeight(WheelLoaderLayout.LiftMax), 3.8f, 4.8f);
        Assert.True(WheelLoaderLayout.PinHeight(WheelLoaderLayout.LiftMin) < 1.0f, "lowered, the bucket is at the ground");
    }

    [Theory]
    [InlineData(-0.45f, 0.55f, 0f)]
    [InlineData(0.95f, -0.95f, 0.7f)]
    [InlineData(-0.62f, 0.75f, -0.7f)]
    [InlineData(0.2f, 0f, 0.31f)]
    public void Parked_flags_keep_the_arm_the_bucket_and_the_bend(float lift, float tilt, float articulation)
    {
        var (l, t, a) = WheelLoaderLayout.Unpack(WheelLoaderLayout.Pack(lift, tilt, articulation));
        Assert.True(MathF.Abs(l - lift) < 0.012f, $"lift {lift} came back {l}");
        Assert.True(MathF.Abs(t - tilt) < 0.012f, $"tilt {tilt} came back {t}");
        Assert.True(MathF.Abs(a - articulation) < 0.012f, $"articulation {articulation} came back {a}");
    }

    [Fact]
    public void A_fresh_loader_parks_straight_in_its_carry_pose()
    {
        Assert.Equal((WheelLoaderLayout.RestLift, WheelLoaderLayout.RestTilt, 0f), WheelLoaderLayout.Unpack(0));
    }
}
