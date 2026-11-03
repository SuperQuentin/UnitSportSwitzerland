using System;
using UnitSport.Player;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The ski force model (#899): carving keeps the speed and skidding scrubs it, edges hold a
/// traverse, a hockey stop stops, skis barely move off snow and the stride is a press, not a hold.
/// </summary>
public class SkiDynamicsTests
{
    private static readonly SkiDynamics.Tuning Sim = SkiDynamics.Tuning.Sim;
    private const float Dt = 1f / 60f;

    /// <summary>The heading turns left by <paramref name="angle"/>: in the skis' frame the velocity turns right.</summary>
    private static void Turn(ref SkiDynamics.State s, float angle)
    {
        float c = MathF.Cos(angle), sn = MathF.Sin(angle);
        (s.U, s.W) = (s.U * c + s.W * sn, -s.U * sn + s.W * c);
    }

    private static SkiDynamics.Ground Flat(bool snow = true) => new(snow, 0, 0);

    [Fact]
    public void A_straight_run_reaches_a_top_speed_that_tucking_raises()
    {
        // a 25 degree groomed slope, straight down the fall line
        var (along, _) = SkiDynamics.Slope(0, MathF.Cos(0.436f), MathF.Sin(0.436f), 0, 1);
        float Top(bool tuck)
        {
            var s = new SkiDynamics.State();
            for (int i = 0; i < 60 * 120; i++)
                SkiDynamics.Step(ref s, Sim, new SkiDynamics.Ground(true, along, 0), new SkiDynamics.Input(0, 0, tuck, 0), Dt);
            return s.Speed;
        }
        float upright = Top(false), tucked = Top(true);
        Assert.InRange(upright, 15f, 30f);
        Assert.True(tucked > upright * 1.25f, $"tucked {tucked} upright {upright}");
    }

    [Fact]
    public void A_carve_keeps_its_speed_and_a_skid_of_the_same_turn_loses_it()
    {
        float After(float yawRate, float edge)
        {
            var s = SkiDynamics.State.From(15, 0);
            for (int i = 0; i < 90; i++)
            {
                Turn(ref s, yawRate * Dt);
                SkiDynamics.Step(ref s, Sim with { Mu = 0, DragArea = 0 }, Flat(), new SkiDynamics.Input(0, 0, false, edge), Dt);
            }
            return s.Speed;
        }
        float carve = After(0.3f, 0.6f);      // 4.5 m/s² sideways: within the edges' grip
        float skid = After(2.0f, 0.6f);       // 30 m/s²: the skis are thrown across
        Assert.True(carve > 14.5f, $"carve {carve}");
        Assert.True(skid < carve - 3f, $"skid {skid}");
    }

    [Fact]
    public void Set_edges_hold_a_traverse_and_flat_skis_slip_down()
    {
        float Slipped(float edge)
        {
            var s = SkiDynamics.State.From(8, 0);
            for (int i = 0; i < 120; i++)
                SkiDynamics.Step(ref s, Sim, new SkiDynamics.Ground(true, 0, -3f), new SkiDynamics.Input(0, 0, false, edge), Dt);
            return -s.W;
        }
        Assert.True(Slipped(1f) < 0.1f, $"edged {Slipped(1f)}");
        Assert.True(Slipped(0f) > 0.3f, $"flat {Slipped(0f)}");
    }

    [Fact]
    public void A_hockey_stop_stops_from_fifty_kmh_in_a_few_seconds()
    {
        var s = SkiDynamics.State.From(14, 1.5f);   // the skis pivoted across the travel
        float t = 0, dist = 0;
        while (s.Speed > 0.5f && t < 10)
        {
            SkiDynamics.Step(ref s, Sim, Flat(), new SkiDynamics.Input(0, 0, false, 1f), Dt);
            dist += s.Speed * Dt;
            t += Dt;
        }
        Assert.InRange(t, 1f, 3.5f);
        Assert.True(dist < 25f, $"{dist} m");
    }

    [Fact]
    public void Off_snow_the_skis_stay_put_on_a_gentle_slope_and_creep_on_a_steep_one()
    {
        float After(float slopeRad)
        {
            var (along, _) = SkiDynamics.Slope(0, MathF.Cos(slopeRad), MathF.Sin(slopeRad), 0, 1);
            var s = new SkiDynamics.State();
            for (int i = 0; i < 600; i++)
                SkiDynamics.Step(ref s, Sim, new SkiDynamics.Ground(false, along, 0), new SkiDynamics.Input(0, 0, false, 0), Dt);
            return s.Speed;
        }
        Assert.True(After(0.17f) < 0.01f);    // 10 degrees of meadow: nothing
        Assert.True(After(0.35f) > 0.3f);     // 20 degrees: a creep
        // and on snow the same slope runs away
        var (a10, _) = SkiDynamics.Slope(0, MathF.Cos(0.17f), MathF.Sin(0.17f), 0, 1);
        var snow = new SkiDynamics.State();
        for (int i = 0; i < 600; i++)
            SkiDynamics.Step(ref snow, Sim, new SkiDynamics.Ground(true, a10, 0), new SkiDynamics.Input(0, 0, false, 0), Dt);
        Assert.True(snow.Speed > 5f);
    }

    [Fact]
    public void Holding_forward_is_one_stride_and_tapping_it_skates_up_to_a_cap()
    {
        var held = new SkiDynamics.State();
        for (int i = 0; i < 120; i++)
            SkiDynamics.Step(ref held, Sim, Flat(), new SkiDynamics.Input(1, 0, false, 0), Dt);
        Assert.True(held.Speed < 1.2f, $"held {held.Speed}");

        float Tapped(bool snow)
        {
            var s = new SkiDynamics.State();
            for (int i = 0; i < 240; i++)   // a press every 10 frames: 6 a second
                SkiDynamics.Step(ref s, Sim, Flat(snow), new SkiDynamics.Input(i % 10 < 5 ? 1 : 0, 0, false, 0), Dt);
            return s.Speed;
        }
        float onSnow = Tapped(true), offSnow = Tapped(false);
        Assert.InRange(onSnow, 3f, Sim.StrideCap);
        Assert.InRange(offSnow, 0.4f, Sim.OffSnowCap);
        Assert.True(onSnow > offSnow * 2.5f);
    }

    [Fact]
    public void A_landing_is_judged_by_the_speed_into_the_ground()
    {
        Assert.Equal(0, SkiDynamics.Landing(4f));    // 0.8 m
        Assert.Equal(1, SkiDynamics.Landing(6.5f));  // 2.2 m
        Assert.Equal(2, SkiDynamics.Landing(9f));    // 4.1 m
    }

    [Fact]
    public void The_fall_line_splits_along_and_across_the_skis()
    {
        // ground falling to the south (+z): its normal leans south
        float nz = MathF.Sin(0.3f), ny = MathF.Cos(0.3f);
        var south = SkiDynamics.Slope(0, ny, nz, 0, 1);
        Assert.True(south.Along > 2f && MathF.Abs(south.Across) < 1e-4f);
        var north = SkiDynamics.Slope(0, ny, nz, 0, -1);
        Assert.True(north.Along < -2f);
        // facing east (+x), the fall to the south is on the right: across (+ left) is negative
        var east = SkiDynamics.Slope(0, ny, nz, 1, 0);
        Assert.True(MathF.Abs(east.Along) < 1e-4f && east.Across < -2f);
    }
}
