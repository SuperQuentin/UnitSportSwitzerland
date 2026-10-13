using Godot;
using UnitSport.Items;
using UnitSport.Terrain.Construction;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="CraneControl"/> (#618): a crane worked by a player eases its drives, stops at the ends
/// of its jib and over what is under its hook, and takes and sets down a pallet only where its
/// slings reach.
/// </summary>
public class CraneControlTests
{
    private static readonly CraneJob Job = new("2586_1115_51|0", new Vector2(100, 100), JibY: 520f, JibLength: 40f, RestYaw: 1.2f,
        Pick: new Vector3(110, 470, 120), Drops: new[] { new Vector3(80, 482, 90) });

    private static (CranePose Pose, CraneLevers Speed) Run(CranePose pose, CraneLevers levers, float seconds, float floor = 470f)
    {
        var speed = new CraneLevers();
        for (float t = 0; t < seconds; t += 1f / 60)
            pose = CraneControl.Step(Job, pose, levers, ref speed, floor, 1f / 60);
        return (pose, speed);
    }

    [Fact]
    public void Each_drive_ramps_up_to_its_rate()
    {
        var start = new CranePose(0, 10, 500, false);
        var speed = new CraneLevers();
        var p = CraneControl.Step(Job, start, new CraneLevers(1, 1, 1), ref speed, 470f, 0.1f);
        // a tenth of a second in, a tenth of the way to full speed
        Assert.Equal(0.1f, speed.Slew, 3);
        var (after, full) = Run(start, new CraneLevers(1, 1, 1), 3f);
        Assert.Equal(1f, full.Slew, 3);
        // three seconds with one of ramping: two and a half seconds' worth at the full rate
        Assert.InRange(after.Yaw, 2.45f * CraneControl.SlewRate, 2.55f * CraneControl.SlewRate);
        Assert.InRange(after.Trolley, 10 + 2.45f * CraneControl.TrolleyRate, 10 + 2.55f * CraneControl.TrolleyRate);
        Assert.InRange(after.HookY, 500 + 2.45f * CraneControl.HoistRate, 500 + 2.55f * CraneControl.HoistRate);
    }

    [Fact]
    public void Positive_slew_turns_left()
    {
        // the game's yaw grows anticlockwise from above: a jib along −Z turning left swings to −X
        var (p, _) = Run(new CranePose(0, 20, 500, false), new CraneLevers(1, 0, 0), 2f);
        Assert.True(CraneControl.HookAt(Job, p).X < Job.Base.X);
    }

    [Fact]
    public void The_trolley_and_the_hook_stop_at_their_ends()
    {
        var (outer, s1) = Run(new CranePose(0, 30, 500, false), new CraneLevers(0, 1, 1), 30f);
        Assert.Equal(Job.JibLength - 1f, outer.Trolley);
        Assert.Equal(CraneControl.Top(Job), outer.HookY);
        Assert.Equal(0f, s1.Trolley);
        Assert.Equal(0f, s1.Hoist);
        var (inner, _) = Run(outer, new CraneLevers(0, -1, -1), 60f, floor: 480f);
        Assert.Equal(CraneMotion.MinTrolley, inner.Trolley);
        Assert.Equal(480f, inner.HookY, 3);
    }

    [Fact]
    public void The_hook_slows_as_it_comes_down_onto_something()
    {
        var speed = new CraneLevers(0, 0, -1);
        var high = CraneControl.Step(Job, new CranePose(0, 20, 510, false), new CraneLevers(0, 0, -1), ref speed, 480f, 0.1f);
        speed = new CraneLevers(0, 0, -1);
        var near = CraneControl.Step(Job, new CranePose(0, 20, 480.2f, false), new CraneLevers(0, 0, -1), ref speed, 480f, 0.1f);
        Assert.Equal(-CraneControl.HoistRate * 0.1f, high.HookY - 510, 3);
        Assert.Equal(-CraneControl.LandingMin * 0.1f, near.HookY - 480.2f, 3);
    }

    [Fact]
    public void The_hook_takes_a_pallet_only_just_over_it()
    {
        var pallet = new Vector3(120, 470, 100);
        float onTop = pallet.Y + Pallets.Height + CraneControl.HookBottom;
        Assert.True(CraneControl.Hooks(new Vector3(120, onTop, 100), pallet));
        Assert.True(CraneControl.Hooks(new Vector3(120.5f, onTop + 0.5f, 100.5f), pallet));
        Assert.False(CraneControl.Hooks(new Vector3(121, onTop, 100), pallet));
        Assert.False(CraneControl.Hooks(new Vector3(120, onTop + 1f, 100), pallet));
        Assert.False(CraneControl.Hooks(new Vector3(120, onTop - 0.5f, 100), pallet));
    }

    [Fact]
    public void A_hooked_pallet_waits_on_the_ground_until_the_slings_are_taut()
    {
        const float rest = 470f;
        float onTop = rest + Pallets.Height + CraneControl.HookBottom;
        Assert.Equal(rest, CraneControl.HungAt(onTop, rest));
        Assert.Equal(rest, CraneControl.HungAt(rest + CraneControl.SlingHang, rest));
        Assert.Equal(rest + 2f, CraneControl.HungAt(rest + CraneControl.SlingHang + 2f, rest));
        // once lifted it hangs, wherever the hook goes
        Assert.Equal(460f, CraneControl.HungAt(460f + CraneControl.SlingHang, float.NaN));
    }

    [Fact]
    public void A_pallet_is_set_down_only_on_what_is_under_it()
    {
        Assert.True(CraneControl.SetsDown(482.1f, 482f));
        Assert.True(CraneControl.SetsDown(482f, 482f));
        Assert.False(CraneControl.SetsDown(483f, 482f));
        Assert.False(CraneControl.SetsDown(481f, 482f));
    }
}
