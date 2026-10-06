using Godot;
using UnitSport.Terrain.Construction;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="CraneMotion.Pose"/> (#610) moves a site's crane with nothing sent: every peer must
/// get the same pose from the same clocks, the crane must keep working hours, really lift from
/// the pick to a drop, never jump between two frames, and drift only slowly when it rests.
/// </summary>
public class CraneMotionTests
{
    private static readonly CraneJob Job = new("2586_1115_51|0", new Vector2(100, 100), JibY: 520f, JibLength: 40f, RestYaw: 1.2f,
        Pick: new Vector3(110, 470, 120), Drops: new[] { new Vector3(80, 482, 90), new Vector3(70, 482, 110), new Vector3(95, 482, 75) });

    [Theory]
    [InlineData(6.99, 0, false)]
    [InlineData(7.0, 0, true)]
    [InlineData(11.99, 2, true)]
    [InlineData(12.5, 2, false)]
    [InlineData(13.0, 4, true)]
    [InlineData(16.99, 5, true)]
    [InlineData(17.0, 5, false)]
    [InlineData(10.0, 6, false)]   // Sunday
    [InlineData(10.0, 13, false)]  // the next Sunday
    [InlineData(10.0, -1, false)]  // a day before the clock's first is a Sunday too
    public void It_keeps_working_hours(double hour, long day, bool working) =>
        Assert.Equal(working, CraneMotion.Working(hour, day));

    [Fact]
    public void Every_peer_sees_the_same_pose()
    {
        for (double t = 0; t < 600; t += 7.3)
            Assert.Equal(CraneMotion.Pose(Job, t, 9.5, 1), CraneMotion.Pose(Job, t, 9.5, 1));
    }

    [Fact]
    public void A_cycle_lifts_from_the_pick_to_a_drop_and_carries_its_load()
    {
        bool down = false, carried = false, setDown = false;
        for (double t = 1000; t < 1000 + CraneMotion.Cycle; t += 0.25)
        {
            var p = CraneMotion.Pose(Job, t, 10, 1);
            Assert.InRange(p.Trolley, CraneMotion.MinTrolley, Job.JibLength - 1f);
            Assert.InRange(p.HookY, Job.Pick.Y, Job.JibY);
            if (MathF.Abs(p.HookY - (Job.Pick.Y + CraneMotion.LoadHeight)) < 0.05f) down = true;
            if (p.Loaded && p.HookY > Job.JibY - 1.5f) carried = true;
            if (Job.Drops.Any(d => MathF.Abs(p.HookY - (d.Y + CraneMotion.LoadHeight)) < 0.05f) && p.Loaded)
            {
                // over a drop when it sets down: the jib points at it and the trolley stands over it
                var target = Job.Drops.First(d => MathF.Abs(p.HookY - (d.Y + CraneMotion.LoadHeight)) < 0.05f);
                var (yaw, reach) = CraneMotion.Aim(Job, target);
                Assert.True(MathF.Abs(Mathf.AngleDifference(yaw, p.Yaw)) < 0.01f);
                Assert.Equal(reach, p.Trolley, 2);
                setDown = true;
            }
        }
        Assert.True(down, "the hook never came down at the pick");
        Assert.True(carried, "nothing was carried at the top");
        Assert.True(setDown, "nothing was set down on a drop");
    }

    [Fact]
    public void It_never_jumps_between_two_frames()
    {
        var last = CraneMotion.Pose(Job, 0, 10, 1);
        for (double t = 0.05; t < 4 * CraneMotion.Cycle; t += 0.05)
        {
            var p = CraneMotion.Pose(Job, t, 10, 1);
            Assert.True(MathF.Abs(Mathf.AngleDifference(last.Yaw, p.Yaw)) < 0.05f, $"slewed {Mathf.AngleDifference(last.Yaw, p.Yaw):F3} rad in a frame at {t:F2}");
            Assert.True(MathF.Abs(p.Trolley - last.Trolley) < 0.3f, $"trolley jumped at {t:F2}");
            Assert.True(MathF.Abs(p.HookY - last.HookY) < 0.5f, $"hook jumped at {t:F2}");
            last = p;
        }
    }

    [Fact]
    public void Out_of_hours_it_weathervanes_slowly_with_its_trolley_in()
    {
        var last = CraneMotion.Pose(Job, 0, 22, 1);
        float min = float.MaxValue, max = float.MinValue;
        for (double t = 1; t < 3600; t += 1)
        {
            var p = CraneMotion.Pose(Job, t, 22, 1);
            Assert.False(p.Loaded);
            Assert.Equal(CraneMotion.MinTrolley, p.Trolley);
            Assert.True(MathF.Abs(Mathf.AngleDifference(last.Yaw, p.Yaw)) < 0.02f, "a resting crane swung");
            float d = Mathf.AngleDifference(Job.RestYaw, p.Yaw);
            min = Math.Min(min, d);
            max = Math.Max(max, d);
            last = p;
        }
        // but it does move: an hour of wind swings it through more than half a radian
        Assert.True(max - min > 0.5f, $"drifted only {max - min:F2} rad in an hour");
    }

    [Fact]
    public void The_jib_is_inside_its_collision_box()
    {
        foreach (var kind in new[] { CraneKind.Tower, CraneKind.SelfErecting })
        {
            var spot = new CraneSpot(kind, Vector2.Zero, 30f, 45f, 0f);
            var (center, size) = CranePlans.JibBounds(spot);
            var lo = center - size / 2 - Vector3.One * 0.5f;
            var hi = center + size / 2 + Vector3.One * 0.5f;
            foreach (var b in CranePlans.Jib(spot).Where(b => b.Part == ShellPart.CraneYellow && b.Max.Z < -1.5f))
                Assert.True(b.Min.Z >= lo.Z && b.Max.Z <= hi.Z && b.Max.Y <= hi.Y, $"{kind}: a jib piece outside its box");
            Assert.True(CranePlans.MastHeight(spot) > spot.HookHeight);
        }
    }
}
