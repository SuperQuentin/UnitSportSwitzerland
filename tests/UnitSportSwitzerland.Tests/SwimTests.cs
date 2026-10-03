using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The swimmer's air reserve (src/Player/AirReserve.cs, #301), linked in.</summary>
public class SwimTests
{
    private static (float Damage, int Bites, int Gasps) Run(ref AirReserve air, float seconds, bool under, bool sprint = false, float dt = 1f / 60f)
    {
        float damage = 0f;
        int bites = 0, gasps = 0;
        for (float t = 0; t < seconds - 1e-4f; t += dt)
        {
            var (d, g) = air.Step(dt, under, sprint);
            damage += d;
            if (d > 0f) bites++;
            if (g) gasps++;
        }
        return (damage, bites, gasps);
    }

    [Fact]
    public void A_full_breath_lasts_45_seconds_under_and_drains_faster_on_a_sprint_stroke()
    {
        var air = AirReserve.Full;
        Run(ref air, 30f, under: true);
        Assert.Equal(15f, air.Seconds, 1);
        var sprint = AirReserve.Full;
        Run(ref sprint, 10f, under: true, sprint: true);
        Assert.Equal(AirReserve.Max - 10f * AirReserve.SprintDrain, sprint.Seconds, 1);
    }

    [Fact]
    public void Nothing_hurts_while_there_is_air_and_it_refills_with_the_head_out()
    {
        var air = AirReserve.Full;
        var (damage, _, _) = Run(ref air, 44f, under: true);
        Assert.Equal(0f, damage);
        Run(ref air, 2f, under: false);
        Assert.Equal(Math.Min(AirReserve.Max, 1f + 2f * AirReserve.Refill), air.Seconds, 1);
        Run(ref air, 10f, under: false);
        Assert.Equal(AirReserve.Max, air.Seconds);
    }

    [Fact]
    public void Empty_it_bites_at_once_then_once_a_second()
    {
        var air = new AirReserve { Seconds = 0f };
        var (damage, bites, _) = Run(ref air, 5f, under: true);
        Assert.Equal(5, bites);
        Assert.Equal(5f * AirReserve.DrownDamage, damage);
        // a long frame (a hitch) still takes one bite, not a burst
        var hitch = new AirReserve { Seconds = 0f };
        var (d, _) = hitch.Step(3f, true, false);
        Assert.Equal(AirReserve.DrownDamage, d);
        // with the head out it stops at once
        var (after, _, _) = Run(ref air, 3f, under: false);
        Assert.Equal(0f, after);
    }

    [Fact]
    public void Coming_up_after_a_long_time_under_is_a_gasp_and_a_short_dip_is_not()
    {
        var air = AirReserve.Full;
        Run(ref air, 5f, under: true);
        var (_, _, shortDip) = Run(ref air, 1f, under: false);
        Assert.Equal(0, shortDip);
        Run(ref air, 35f, under: true);
        var (_, _, longDip) = Run(ref air, 1f, under: false);
        Assert.Equal(1, longDip);
    }

    // ---- wading (src/Player/Wading.cs, #380) ----------------------------------------------------

    [Fact]
    public void Ankle_deep_water_does_not_slow_the_walk()
    {
        Assert.Equal(1f, Wading.SpeedFactor(0f, false));
        Assert.Equal(1f, Wading.SpeedFactor(Wading.Feel, true));
        Assert.Equal(1f, Wading.SpeedFactor(float.NaN, false), 3);
    }

    [Fact]
    public void Knee_deep_slows_a_little_and_waist_deep_a_lot_a_run_more_than_a_walk()
    {
        float kneeWalk = Wading.SpeedFactor(Wading.Knee, false), kneeRun = Wading.SpeedFactor(Wading.Knee, true);
        float waistWalk = Wading.SpeedFactor(Wading.Waist, false), waistRun = Wading.SpeedFactor(Wading.Waist, true);
        Assert.InRange(kneeWalk, 0.78f, 0.9f);
        Assert.InRange(kneeRun, 0.7f, 0.85f);
        Assert.InRange(waistWalk, 0.35f, 0.5f);
        Assert.InRange(waistRun, 0.15f, 0.3f);
        Assert.True(kneeRun < kneeWalk && waistRun < waistWalk);
        // never faster deeper
        for (float d = 0f; d < 1.3f; d += 0.01f)
            Assert.True(Wading.SpeedFactor(d + 0.01f, true) <= Wading.SpeedFactor(d, true) + 1e-6f);
    }

    [Fact]
    public void Spray_comes_from_moving_legs_in_the_water_most_by_the_knees()
    {
        Assert.Equal(0f, Wading.Spray(0.5f, 0f));
        Assert.Equal(0f, Wading.Spray(0f, 3f));
        Assert.True(Wading.Spray(0.5f, 2f) > Wading.Spray(0.15f, 2f));
        Assert.True(Wading.Spray(0.5f, 2f) > Wading.Spray(1.2f, 2f));
        Assert.True(Wading.Spray(0.5f, 3f) > Wading.Spray(0.5f, 1f));
        Assert.Equal(0f, Wading.StrideVolume(0f));
        Assert.True(Wading.StrideVolume(0.6f) > Wading.StrideVolume(0.1f));
    }
}
