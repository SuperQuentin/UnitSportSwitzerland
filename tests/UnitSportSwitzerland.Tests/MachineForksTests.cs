using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The fork rule for any machine with tines (#615): the forklift's own rule is the general one at
/// its tines, the telehandler's and the fork loader's tines rest on the ground with the arm down,
/// and what is on the forks rides through their poses and parked flags with nothing lost.
/// </summary>
public class MachineForksTests
{
    [Theory]
    [InlineData(0f, 1.6f, 1f, 0.15f, 0f)]
    [InlineData(0.32f, 1.2f, 0.1f, 0.2f, 1f)]
    [InlineData(0.34f, 1.6f, 1f, 0.15f, 0f)]
    [InlineData(0f, 1.0f, 1f, 0.15f, 0f)]
    [InlineData(0f, 2.3f, 1f, 0.15f, 0f)]
    [InlineData(0f, 1.6f, 0.7f, 0.15f, 0f)]
    [InlineData(0f, 1.6f, 1f, 0.4f, 0f)]
    [InlineData(0f, 1.6f, 1f, 0.15f, 2.5f)]
    public void The_forklifts_rule_is_the_general_one_at_its_tines(float x, float z, float along, float lift, float speed) =>
        Assert.Equal(
            Pallets.OnTines(x, z - ForkliftLayout.MastZ, along, lift, speed, ForkliftLayout.TineLength, ForkliftLayout.ForkHalfSpan),
            Pallets.Forked(x, z, along, lift, speed));

    [Fact]
    public void A_pallet_rides_where_the_forklift_always_put_it() =>
        Assert.Equal(ForkliftLayout.MastZ + Pallets.LoadAhead, ForkliftLayout.LoadCentre.Z, 5);

    [Fact]
    public void Arm_down_the_tines_are_on_the_ground_ahead_of_the_wheels()
    {
        // under the height they go in at, over nothing below the ground
        float tele = TelehandlerLayout.ForkHeight(TelehandlerLayout.RestLift, 0f);
        Assert.InRange(tele, 0f, ForkliftLayout.SetDown);
        float loader = WheelLoaderLayout.ForkHeight(WheelLoaderLayout.LiftMin);
        Assert.InRange(loader, 0f, ForkliftLayout.SetDown);
        // and the tines' heel clear of the wheels, so they reach under a pallet before the tyres meet it
        Assert.True(TelehandlerLayout.Carriage(TelehandlerLayout.RestLift, 0f).X + TelehandlerLayout.ForkFace
            > TelehandlerLayout.FrontAxle + TelehandlerLayout.WheelRadius);
        Assert.True(WheelLoaderLayout.Pin(WheelLoaderLayout.LiftMin).X + WheelLoaderLayout.ForkFace
            > WheelLoaderLayout.FrontAxle + WheelLoaderLayout.WheelRadius);
    }

    [Fact]
    public void Lifting_the_arm_a_little_raises_the_tines_through_the_seat()
    {
        // a few hundredths of a radian take them from the ground through Pallets.Seat: lifting is quick
        Assert.True(TelehandlerLayout.ForkHeight(TelehandlerLayout.RestLift + 0.05f, 0f) > Pallets.Seat);
        Assert.True(WheelLoaderLayout.ForkHeight(WheelLoaderLayout.LiftMin + 0.08f) > Pallets.Seat);
    }

    [Theory]
    [InlineData(-0.62f, 0.05f, 0f, 0)]
    [InlineData(0.95f, 0.3f, 0.7f, 512)]
    [InlineData(0.1f, -0.5f, -0.7f, 129)]
    [InlineData(-0.3f, 0.12f, 0.33f, 1)]
    public void A_parked_fork_loader_keeps_its_arm_and_its_pallet(float lift, float tilt, float articulation, int carrying)
    {
        var (l, t, a, c) = WheelLoaderLayout.UnpackForks(WheelLoaderLayout.PackForks(lift, tilt, articulation, carrying));
        Assert.True(MathF.Abs(l - lift) < 0.004f, $"lift {lift} came back {l}");
        Assert.True(MathF.Abs(t - tilt) < 0.004f, $"tilt {tilt} came back {t}");
        Assert.True(MathF.Abs(a - articulation) < 0.006f, $"articulation {articulation} came back {a}");
        Assert.Equal(carrying, c);
    }

    [Fact]
    public void A_fresh_fork_loader_parks_arm_down_and_empty() =>
        Assert.Equal((WheelLoaderLayout.LiftMin, WheelLoaderLayout.ForkRestTilt, 0f, 0), WheelLoaderLayout.UnpackForks(0));

    [Theory]
    [InlineData(-0.62f, 0)]
    [InlineData(0.95f, 512)]
    [InlineData(0f, 1)]
    [InlineData(-0.22f, 257)]
    [InlineData(1.15f, 300)]
    public void The_pose_carries_the_lift_and_the_pallet(float lift, int carrying)
    {
        var (l, c) = WheelLoaderLayout.FromPoseLift(WheelLoaderLayout.PoseLift(WheelLoaderLayout.ClampLift(lift), carrying));
        Assert.Equal(WheelLoaderLayout.ClampLift(lift), l, 3);
        Assert.Equal(carrying, c);
        var (tl, tc) = TelehandlerLayout.FromPoseLift(TelehandlerLayout.PoseLift(TelehandlerLayout.ClampLift(lift), carrying));
        Assert.Equal(TelehandlerLayout.ClampLift(lift), tl, 3);
        Assert.Equal(carrying, tc);
    }

    [Fact]
    public void A_bucket_holds_rolled_back_and_tips_out_dumped_far_apart()
    {
        // between the two, a pallet just scooped is not dropped, nor one just dropped scooped again
        Assert.True(Pallets.Curled(Pallets.CurlCarry) && !Pallets.Dumped(Pallets.CurlCarry));
        Assert.True(Pallets.Dumped(Pallets.DumpDrop) && !Pallets.Curled(Pallets.DumpDrop));
        Assert.True(Pallets.CurlCarry - Pallets.DumpDrop > 0.5f);
        // the loader's carry pose holds nothing back from a dump, and its fully curled bucket on the
        // ground is short of holding: the arm has to lift as it curls, as a real loader's does
        Assert.False(Pallets.Dumped(WheelLoaderLayout.RestLift + WheelLoaderLayout.RestTilt));
        Assert.False(Pallets.Curled(WheelLoaderLayout.LiftMin + WheelLoaderLayout.TiltMax));
        Assert.True(Pallets.Curled(0f + WheelLoaderLayout.TiltMax));
        Assert.True(Pallets.Dumped(0f + WheelLoaderLayout.TiltMin));
    }

    [Theory]
    [InlineData(0f, 0.6f, 0f, true)]
    [InlineData(0.9f, 0.6f, 0f, true)]
    [InlineData(1.0f, 0.6f, 0f, false)]
    [InlineData(0f, 0.05f, 0f, false)]
    [InlineData(0f, 1.8f, 0f, true)]
    [InlineData(0f, 1.9f, 0f, false)]
    [InlineData(0f, 0.6f, 0.7f, false)]
    [InlineData(0f, 0.6f, -0.5f, true)]
    public void A_pallet_is_in_the_loaders_bucket_within_its_width_out_to_its_lip_on_its_floor(float x, float ahead, float up, bool inIt) =>
        Assert.Equal(inIt, Pallets.InBucket(x, ahead, up, WheelLoaderLayout.BucketHalf, WheelLoaderLayout.BucketReach));

    [Theory]
    [InlineData(-0.45f, 0.55f, 0f, 0)]
    [InlineData(0.95f, -0.95f, 0.7f, 512)]
    [InlineData(0.2f, 0.75f, -0.31f, 129)]
    public void A_parked_bucket_loader_keeps_its_arm_and_its_pallet(float lift, float tilt, float articulation, int carrying)
    {
        var (l, t, a, c) = WheelLoaderLayout.Unpack(WheelLoaderLayout.Pack(lift, tilt, articulation, carrying));
        Assert.True(MathF.Abs(l - lift) < 0.004f, $"lift {lift} came back {l}");
        Assert.True(MathF.Abs(t - tilt) < 0.008f, $"tilt {tilt} came back {t}");
        Assert.True(MathF.Abs(a - articulation) < 0.006f, $"articulation {articulation} came back {a}");
        Assert.Equal(carrying, c);
    }
}
