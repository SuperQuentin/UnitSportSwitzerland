using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Critical power, W′ balance (Skiba's differential model) and heart rate (#1124).</summary>
public class RiderPhysiologyTests
{
    private const float Dt = 1f / 60f;

    private static float Ride(RiderPhysiology body, float watts, float seconds)
    {
        for (float t = 0; t < seconds; t += Dt) body.Step(body.Available(watts), Dt);
        return body.WFraction;
    }

    [Fact]
    public void A_sprint_lasts_as_long_as_the_reserve()
    {
        // 520 W on a 240 W threshold: 280 W over out of 20 kJ is 71 s
        var body = new RiderPhysiology(240f, 20000f);
        Assert.Equal(520f, body.Available(520f));
        Assert.InRange(Ride(body, 520f, 60f), 0.12f, 0.20f);
        Ride(body, 520f, 30f);
        // spent: held to threshold
        Assert.InRange(body.Available(520f), 240f, 250f);
    }

    [Fact]
    public void Below_threshold_the_reserve_never_drains()
    {
        var body = new RiderPhysiology(240f);
        Assert.Equal(1f, Ride(body, 230f, 600f), 3);
    }

    [Fact]
    public void Easing_off_refills_it_the_faster_the_further_below_threshold()
    {
        // Skiba: from empty at P < CP the reserve refills with time constant W′/(CP − P)
        var easy = new RiderPhysiology(240f, 20000f);
        Ride(easy, 600f, 120f);
        Assert.Equal(0f, easy.WFraction, 3);
        float tau = 20000f / (240f - 100f);
        Assert.InRange(Ride(easy, 100f, tau), 0.60f, 0.66f);
        var harder = new RiderPhysiology(240f, 20000f);
        Ride(harder, 600f, 120f);
        Assert.True(Ride(harder, 200f, tau) < 0.40f);
    }

    [Fact]
    public void Heart_rate_follows_the_effort()
    {
        var body = new RiderPhysiology(240f, 20000f, restingHr: 60f, maxHr: 185f);
        // at threshold: ~90% of the way from rest to max
        Assert.InRange(body.TargetHeartRate(240f), 168f, 176f);
        // 180 W of a 240 W threshold: a steady endurance ride, ~150
        Assert.InRange(body.TargetHeartRate(180f), 144f, 154f);
        float start = body.HeartRate;
        Ride(body, 240f, 25f);
        float rose = (body.HeartRate - start) / (body.TargetHeartRate(240f) - start);
        Assert.InRange(rose, 0.58f, 0.68f);    // one rise time constant
        body.Step(0f, Dt, measuredHeartRate: 131f);
        Assert.Equal(131f, body.HeartRate);
    }

    [Fact]
    public void A_new_threshold_keeps_the_share_of_the_reserve()
    {
        var body = new RiderPhysiology(240f, 20000f);
        Ride(body, 520f, 35f);
        float share = body.WFraction;
        body.Set(300f, 20000f);
        Assert.Equal(share, body.WFraction, 3);
    }
}
