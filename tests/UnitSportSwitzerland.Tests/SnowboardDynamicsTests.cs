using System;
using UnitSport.Player;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The snowboard on the ski model (#922): a shorter push than a skate, the toe edge holding more
/// than the heel edge, and riding switch when the board travels tail first.
/// </summary>
public class SnowboardDynamicsTests
{
    private const float Dt = 1f / 60f;

    private static float Pushed(SkiDynamics.Tuning t, bool snow)
    {
        var s = new SkiDynamics.State();
        for (int i = 0; i < 60 * 10; i++)
            SkiDynamics.Step(ref s, t, new SkiDynamics.Ground(snow, 0, 0), new SkiDynamics.Input(i % 12 < 6 ? 1 : 0, 0, false, 0), Dt);
        return s.Speed;
    }

    [Fact]
    public void A_one_foot_push_is_slower_than_a_skate_and_slower_still_off_snow()
    {
        float skis = Pushed(SkiDynamics.Tuning.Sim, true), board = Pushed(SkiDynamics.Tuning.Board, true);
        float hobble = Pushed(SkiDynamics.Tuning.Board, false);
        Assert.True(board < skis * 0.7f, $"board {board} skis {skis}");
        Assert.InRange(board, 1.0f, 2.6f);
        Assert.True(hobble < board, $"off snow {hobble} on snow {board}");
    }

    [Fact]
    public void The_toe_edge_holds_more_than_the_heel_edge()
    {
        Assert.True(SkiDynamics.BoardEdgeGrip(true) > SkiDynamics.BoardEdgeGrip(false));
        // regular, facing the right of the travel: leaning right (bank −) is the toe edge
        Assert.True(SkiDynamics.ToeSide(-0.5f, facingRight: true));
        Assert.False(SkiDynamics.ToeSide(0.5f, facingRight: true));
        Assert.True(SkiDynamics.ToeSide(0.5f, facingRight: false));
    }

    [Fact]
    public void Travelling_tail_first_rides_switch_and_keeps_the_velocity()
    {
        float yaw = 0.3f, slip = 2.6f;
        float before = yaw + slip;
        Assert.True(SkiDynamics.Switch(ref yaw, ref slip, 8f));
        Assert.InRange(MathF.Abs(slip), 0f, MathF.PI / 2f);
        // the travel's direction in the world is unchanged
        Assert.Equal(MathF.Cos(before), MathF.Cos(yaw + slip), 4);
        Assert.Equal(MathF.Sin(before), MathF.Sin(yaw + slip), 4);
    }

    [Fact]
    public void A_sideways_skid_or_a_crawl_is_not_switch()
    {
        float yaw = 0f, slip = 1.6f;
        Assert.False(SkiDynamics.Switch(ref yaw, ref slip, 8f));    // a quarter turn: a skid stop
        slip = 3f;
        Assert.False(SkiDynamics.Switch(ref yaw, ref slip, 0.5f));  // too slow to say
    }

    [Fact]
    public void A_hop_turn_swings_square_across_in_a_fifth_of_a_second_and_no_further()
    {
        float slip = 0f, t = 0f;
        while (MathF.Abs(slip) < SkiDynamics.StopAngle - 1e-4f && t < 1f)
        {
            slip = SkiDynamics.SwingAcross(slip, 1f, SkiDynamics.HopTurnRate, Dt);
            t += Dt;
        }
        Assert.InRange(t, 0.15f, 0.25f);
        // turning left (+1) puts the travel to the right of the nose: slip −
        Assert.Equal(-SkiDynamics.StopAngle, slip, 4);
        // held on, it stays square across
        for (int i = 0; i < 60; i++) slip = SkiDynamics.SwingAcross(slip, 1f, SkiDynamics.HopTurnRate, Dt);
        Assert.Equal(-SkiDynamics.StopAngle, slip, 4);
        // swung back the other way, it comes back
        Assert.True(SkiDynamics.SwingAcross(slip, -1f, SkiDynamics.HopTurnRate, Dt) > slip);
        // a skid already past the quarter turn is not pushed on
        Assert.Equal(-2f, SkiDynamics.SwingAcross(-2f, 1f, SkiDynamics.HopTurnRate, Dt), 4);
    }

    [Fact]
    public void Landed_square_across_the_edges_stop_fast_but_not_at_once()
    {
        // 15 m/s, landed at the stop angle on full edge, on the flat
        var s = SkiDynamics.State.From(15f, SkiDynamics.StopAngle);
        float t = 0f;
        while (s.Speed > 1f && t < 10f)
        {
            SkiDynamics.Step(ref s, SkiDynamics.Tuning.Sim, new SkiDynamics.Ground(true, 0, 0), new SkiDynamics.Input(0, 0, false, 1f), Dt);
            t += Dt;
        }
        Assert.InRange(t, 1.2f, 4f);
    }

    [Fact]
    public void A_board_runs_slower_than_tucked_skis()
    {
        var (along, _) = SkiDynamics.Slope(0, MathF.Cos(0.35f), MathF.Sin(0.35f), 0, 1);
        float Top(SkiDynamics.Tuning t, bool tuck)
        {
            var s = new SkiDynamics.State();
            for (int i = 0; i < 60 * 120; i++)
                SkiDynamics.Step(ref s, t, new SkiDynamics.Ground(true, along, 0), new SkiDynamics.Input(0, 0, tuck, 0), Dt);
            return s.Speed;
        }
        Assert.True(Top(SkiDynamics.Tuning.Board, true) < Top(SkiDynamics.Tuning.Sim, true));
    }
}
