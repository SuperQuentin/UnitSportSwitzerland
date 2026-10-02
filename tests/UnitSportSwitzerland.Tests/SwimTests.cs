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
}
