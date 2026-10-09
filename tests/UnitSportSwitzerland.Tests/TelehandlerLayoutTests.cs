using Godot;
using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The telehandler (#614): three steering modes whose circles and travel are a bicycle's with both
/// axles steered, a 13 m class boom, and its state packed into a parked vehicle's flags and the
/// four floats of a pose without losing the mode or the tilt.
/// </summary>
public class TelehandlerLayoutTests
{
    [Fact]
    public void Four_wheel_steering_halves_the_circle_and_crab_does_not_turn()
    {
        float front = TelehandlerLayout.TurnRadius(SteerMode.Front);
        float four = TelehandlerLayout.TurnRadius(SteerMode.FourWheel);
        Assert.InRange(front, 3.5f, 5.5f);
        Assert.Equal(front / 2f, four, 3);
        Assert.True(float.IsPositiveInfinity(TelehandlerLayout.TurnRadius(SteerMode.Crab)));
    }

    [Fact]
    public void Each_mode_travels_the_way_its_wheels_point()
    {
        const float lock_ = TelehandlerLayout.MaxSteer;
        var (yawF, slipF) = TelehandlerLayout.Motion(2f, lock_, SteerMode.Front);
        var (yaw4, slip4) = TelehandlerLayout.Motion(2f, lock_, SteerMode.FourWheel);
        var (yawC, slipC) = TelehandlerLayout.Motion(2f, lock_, SteerMode.Crab);
        // left lock turns left, the middle drifting a little left of its nose
        Assert.True(yawF > 0f && slipF > 0f && slipF < lock_);
        // four-wheel: twice the turn, and the middle goes straight along its nose
        Assert.Equal(2f * yawF, yaw4, 4);
        Assert.Equal(0f, slip4, 5);
        // crab: no turn at all, travelling at the wheels' angle
        Assert.Equal(0f, yawC, 5);
        Assert.Equal(lock_, slipC, 4);
    }

    [Fact]
    public void The_boom_reaches_a_thirteen_metre_class_machines_heights()
    {
        var top = TelehandlerLayout.Carriage(TelehandlerLayout.LiftMax, TelehandlerLayout.ExtendMax);
        Assert.InRange(top.Y, 9f, 11f);
        var far = TelehandlerLayout.Carriage(0f, TelehandlerLayout.ExtendMax);
        Assert.InRange(far.X, 6.5f, 8.5f);
        // boom down and in: the carriage low, in front of the front wheels, the forks on the ground
        var rest = TelehandlerLayout.Carriage(TelehandlerLayout.RestLift, 0f);
        Assert.True(rest.X > TelehandlerLayout.FrontAxle + TelehandlerLayout.WheelRadius, $"the carriage is {rest.X:F2} m out");
        Assert.InRange(rest.Y - TelehandlerLayout.ForkDrop, -0.05f, 0.15f);
    }

    [Theory]
    [InlineData(-0.22f, 0f, 0.05f, SteerMode.Front, 0)]
    [InlineData(1.15f, 4.3f, 0.35f, SteerMode.Crab, 512)]
    [InlineData(0.4f, 2.17f, -0.6f, SteerMode.FourWheel, 1)]
    [InlineData(0.9f, 0.5f, -0.1f, SteerMode.Crab, 300)]
    public void Parked_flags_keep_the_boom_the_mode_and_the_pallet(float lift, float extend, float tilt, SteerMode mode, int carrying)
    {
        var (l, e, t, m, c) = TelehandlerLayout.Unpack(TelehandlerLayout.Pack(lift, extend, tilt, mode, carrying));
        // seven bits of lift and extension since the pallet took ten (#615), six of tilt
        Assert.True(MathF.Abs(l - lift) < 0.006f, $"lift {lift} came back {l}");
        Assert.True(MathF.Abs(e - extend) < 0.018f, $"extend {extend} came back {e}");
        Assert.True(MathF.Abs(t - tilt) < 0.01f, $"tilt {tilt} came back {t}");
        Assert.Equal(mode, m);
        Assert.Equal(carrying, c);
    }

    [Fact]
    public void A_fresh_one_parks_boom_down_in_front_steering()
    {
        Assert.Equal((TelehandlerLayout.RestLift, TelehandlerLayout.RestExtend, TelehandlerLayout.RestTilt, SteerMode.Front, 0),
            TelehandlerLayout.Unpack(0));
        Assert.NotEqual(0, TelehandlerLayout.Pack(TelehandlerLayout.LiftMin, 0f, TelehandlerLayout.TiltMin, SteerMode.Front));
    }

    [Theory]
    [InlineData(-0.6f, SteerMode.Front)]
    [InlineData(0.6f, SteerMode.Crab)]
    [InlineData(0.13f, SteerMode.FourWheel)]
    [InlineData(0f, SteerMode.Crab)]
    public void The_pose_carries_the_wheels_and_the_mode(float steer, SteerMode mode)
    {
        var (s, m) = TelehandlerLayout.FromPoseSteer(TelehandlerLayout.PoseSteer(steer, mode));
        Assert.Equal(steer, s, 4);
        Assert.Equal(mode, m);
    }

    [Theory]
    [InlineData(0f, -0.6f)]
    [InlineData(4.3f, 0.35f)]
    [InlineData(2.2f, 0.05f)]
    [InlineData(0f, 0.35f)]
    public void The_pose_carries_the_extension_and_the_tilt(float extend, float tilt)
    {
        var (e, t) = TelehandlerLayout.FromPoseExtend(TelehandlerLayout.PoseExtend(extend, tilt));
        Assert.Equal(extend, e, 3);
        // 64 steps over the tilt's travel: 0.008 rad at worst
        Assert.True(MathF.Abs(t - tilt) < 0.008f, $"tilt {tilt} came back {t}");
    }
}
