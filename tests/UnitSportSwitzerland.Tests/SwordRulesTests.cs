using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The sword's combo, charge, plunge, dodge and stamina (#1371, src/Combat/SwordRules.cs, SwordGuard.cs, linked in).</summary>
public class SwordRulesTests
{
    private const double Dt = 1.0 / 60;

    /// <summary>Runs the machine for <paramref name="seconds"/> with the buttons as given; returns the clock.</summary>
    private static double Run(SwordMachine m, double now, double seconds, SwordInput input, List<SwordMove>? began = null, List<SwordMove>? active = null)
    {
        for (double end = now + seconds; now < end; now += Dt)
        {
            m.Step(now, input);
            if (m.Began) began?.Add(m.Move);
            if (m.ActiveBegan) active?.Add(m.Move);
        }
        return now;
    }

    private static SwordInput Press(bool attack = false, bool dodge = false) => new() { Attack = attack, Dodge = dodge };

    [Fact]
    public void Frame_data_is_sane_for_every_move()
    {
        foreach (SwordMove move in Enum.GetValues<SwordMove>())
        {
            var d = SwordRules.Def(move);
            Assert.Equal(move, d.Move);
            if (move is SwordMove.None or SwordMove.Charge) continue;
            Assert.True(d.Cancel <= d.Total, $"{move} frees its hands inside its own length");
            Assert.True(d.Recovery > 0f, $"{move} has a recovery");
        }
    }

    [Fact]
    public void A_tapped_attack_swings_and_ends_by_itself()
    {
        var m = new SwordMachine();
        var began = new List<SwordMove>();
        var active = new List<SwordMove>();
        double t = Run(m, 0, 0.05, Press(), began, active);
        t = Run(m, t, Dt, Press(attack: true), began, active);
        t = Run(m, t, 1.0, Press(), began, active);
        Assert.Equal(new[] { SwordMove.Slash1 }, began);
        Assert.Equal(new[] { SwordMove.Slash1 }, active);
        Assert.Equal(SwordMove.None, m.Move);
        Assert.Equal(SwordPhase.Idle, m.Phase(t));
    }

    [Fact]
    public void The_blade_is_live_only_between_startup_and_recovery()
    {
        var m = new SwordMachine();
        m.Step(1.0, Press(attack: true));
        var d = SwordRules.Def(SwordMove.Slash1);
        m.Step(1.0 + d.Startup * 0.5, Press());
        Assert.Equal(SwordPhase.Startup, m.Phase(1.0 + d.Startup * 0.5));
        Assert.False(m.Live(1.0 + d.Startup * 0.5));
        m.Step(1.0 + d.Startup + 0.01, Press());
        Assert.True(m.Live(1.0 + d.Startup + 0.01));
        m.Step(1.0 + d.Startup + d.Active + 0.01, Press());
        Assert.Equal(SwordPhase.Recovery, m.Phase(1.0 + d.Startup + d.Active + 0.01));
        Assert.False(m.Live(1.0 + d.Startup + d.Active + 0.01));
    }

    [Fact]
    public void Presses_in_the_cancel_window_chain_the_whole_combo_and_it_wraps()
    {
        var m = new SwordMachine();
        var began = new List<SwordMove>();
        double t = 0;
        for (int i = 0; i < 7; i++)
        {
            t = Run(m, t, Dt, Press(attack: true), began);
            // let go, then wait just past this swing's cancel window
            double wait = SwordRules.Def(m.Move).Cancel + 0.02;
            t = Run(m, t, wait, Press(), began);
        }
        Assert.Equal(new[] { SwordMove.Slash1, SwordMove.Slash2, SwordMove.Slash3, SwordMove.Slash4, SwordMove.Slash5, SwordMove.Slash1, SwordMove.Slash2 }, began);
    }

    [Fact]
    public void A_press_before_the_window_opens_is_buffered_and_used_when_it_does()
    {
        var m = new SwordMachine();
        var began = new List<SwordMove>();
        double t = Run(m, 0, Dt, Press(attack: true), began);
        t = Run(m, t, 0.34, Press(), began);
        t = Run(m, t, Dt, Press(attack: true), began);   // too early for the 0.60 s window, inside the 0.32 s buffer of it
        Assert.Equal(new[] { SwordMove.Slash1 }, began);
        t = Run(m, t, 0.4, Press(), began);
        Assert.Equal(new[] { SwordMove.Slash1, SwordMove.Slash2 }, began);
    }

    [Fact]
    public void A_late_press_starts_the_combo_over()
    {
        var m = new SwordMachine();
        var began = new List<SwordMove>();
        double t = Run(m, 0, Dt, Press(attack: true), began);
        t = Run(m, t, 1.5, Press(), began);
        t = Run(m, t, Dt, Press(attack: true), began);
        Assert.Equal(new[] { SwordMove.Slash1, SwordMove.Slash1 }, began);
    }

    [Fact]
    public void A_press_just_after_a_swing_ends_continues_the_combo()
    {
        var m = new SwordMachine();
        var began = new List<SwordMove>();
        double t = Run(m, 0, Dt, Press(attack: true), began);
        t = Run(m, t, SwordRules.Def(SwordMove.Slash1).Total + 0.15, Press(), began);
        t = Run(m, t, Dt, Press(attack: true), began);
        Assert.Equal(new[] { SwordMove.Slash1, SwordMove.Slash2 }, began);
    }

    [Fact]
    public void Holding_the_attack_after_a_swing_charges_and_letting_go_lands_the_charged_blow()
    {
        var m = new SwordMachine();
        var began = new List<SwordMove>();
        var active = new List<SwordMove>();
        double t = Run(m, 0, 1.0, Press(attack: true), began, active);   // pressed and held
        Assert.True(m.Charging);
        Assert.Equal(new[] { SwordMove.Slash1 }, began);
        float before = m.Stamina.Points;
        t = Run(m, t, SwordRules.ChargeMin + 0.05, Press(attack: true), began, active);
        t = Run(m, t, Dt, Press(), began, active);   // let go
        Assert.Equal(SwordMove.Charged, m.Move);
        Assert.False(m.Charging);
        Assert.Equal(before - SwordRules.ChargedCost, m.Stamina.Points, 1);
        t = Run(m, t, 1.0, Press(), began, active);
        Assert.Contains(SwordMove.Charged, active);
    }

    [Fact]
    public void A_short_hold_lets_go_without_a_blow_or_a_cost()
    {
        var m = new SwordMachine();
        double t = Run(m, 0, 0.9, Press(attack: true));
        Assert.True(m.Charging);
        t = Run(m, t, 0.1, Press(attack: true));
        t = Run(m, t, Dt, Press());
        Assert.False(m.Charging);
        Assert.Equal(SwordMove.None, m.Move);
        Assert.Equal(SwordStamina.Max, m.Stamina.Points, 1);
    }

    [Fact]
    public void A_charged_blow_needs_its_stamina_and_the_swing_does_not_charge_without_it()
    {
        var m = new SwordMachine();
        m.Stamina.TrySpend(SwordStamina.Max - 10f);   // 10 left, and the pause before it refills running
        Run(m, 0, 1.0, Press(attack: true));
        Assert.False(m.Charging);
    }

    [Fact]
    public void The_dodge_costs_stamina_has_i_frames_and_cannot_be_chained_without_it()
    {
        var m = new SwordMachine();
        double t = Run(m, 0, Dt, Press(dodge: true));
        Assert.Equal(SwordMove.Dodge, m.Move);
        Assert.Equal(SwordStamina.Max - SwordRules.DodgeCost, m.Stamina.Points, 1);
        double start = m.MoveAt;
        Assert.False(m.Invulnerable(start + 0.01));
        t = Run(m, t, 0.1, Press());
        Assert.True(m.Invulnerable(t));
        t = Run(m, t, 0.4, Press());
        Assert.False(m.Invulnerable(t));
        Assert.Equal(SwordMove.None, m.Move);

        // the meter runs dry: no dodge, and it says so
        m.Stamina.Points = 5f;
        t = Run(m, t + 1, Dt, Press(dodge: true));
        Assert.Equal(SwordMove.None, m.Move);
        Assert.True(m.Refused);
    }

    [Fact]
    public void A_dodge_cancels_a_swings_recovery_but_not_its_startup()
    {
        var m = new SwordMachine();
        double t = Run(m, 0, Dt, Press(attack: true));
        t = Run(m, t, 0.05, Press());
        t = Run(m, t, Dt, Press(dodge: true));   // inside the wind-up: buffered, then too old when the window opens
        t = Run(m, t, 0.5, Press());
        Assert.Equal(SwordMove.Slash1, m.Move);
        t = Run(m, t, 0.2, Press());
        Assert.Equal(SwordMove.Slash1, m.Move);
        // a press late in the follow-through is taken the moment the window opens
        var n = new SwordMachine();
        double u = Run(n, 0, Dt, Press(attack: true));
        u = Run(n, u, 0.52, Press());
        u = Run(n, u, Dt, Press(dodge: true));
        u = Run(n, u, 0.2, Press());
        Assert.Equal(SwordMove.Dodge, n.Move);
    }

    [Fact]
    public void Stamina_comes_back_after_its_pause()
    {
        var m = new SwordMachine();
        double t = Run(m, 0, Dt, Press(dodge: true));
        t = Run(m, t, 0.4, Press());
        float low = m.Stamina.Points;
        t = Run(m, t, 0.2, Press());
        Assert.Equal(low, m.Stamina.Points, 0);   // still inside the delay
        t = Run(m, t, 2.0, Press());
        Assert.True(m.Stamina.Points > low + 15f);
        t = Run(m, t, 5.0, Press());
        Assert.Equal(SwordStamina.Max, m.Stamina.Points, 1);
    }

    [Fact]
    public void An_attack_in_a_fall_is_a_plunge_whose_blow_lands_with_the_ground()
    {
        var m = new SwordMachine();
        var active = new List<SwordMove>();
        double t = Run(m, 0, Dt, new SwordInput { Attack = true, Airborne = true, Falling = true }, null, active);
        Assert.Equal(SwordMove.Plunge, m.Move);
        t = Run(m, t, 0.5, new SwordInput { Airborne = true, Falling = true }, null, active);
        Assert.Empty(active);   // still diving
        t = Run(m, t, Dt, new SwordInput { Grounded = true }, null, active);
        Assert.Equal(new[] { SwordMove.Plunge }, active);
        t = Run(m, t, 1.0, new SwordInput { Grounded = true }, null, active);
        Assert.Equal(SwordMove.None, m.Move);
    }

    [Fact]
    public void A_plunge_that_finds_no_ground_gives_up()
    {
        var m = new SwordMachine();
        double t = Run(m, 0, Dt, new SwordInput { Attack = true, Airborne = true, Falling = true });
        Run(m, t, SwordRules.PlungeMax + 0.5, new SwordInput { Airborne = true, Falling = true });
        Assert.Equal(SwordMove.None, m.Move);
    }

    [Fact]
    public void A_rising_attack_is_an_ordinary_swing()
    {
        var m = new SwordMachine();
        Run(m, 0, Dt, new SwordInput { Attack = true, Airborne = true, Falling = false });
        Assert.Equal(SwordMove.Slash1, m.Move);
    }

    [Fact]
    public void Hit_stop_holds_the_swings_clock()
    {
        var a = new SwordMachine();
        var b = new SwordMachine();
        Run(a, 0, Dt, Press(attack: true));
        Run(b, 0, Dt, Press(attack: true));
        a.Freeze(Dt, 0.2);
        Run(a, Dt, 0.95, Press());
        Run(b, Dt, 0.95, Press());
        Assert.Equal(SwordMove.Slash1, a.Move);   // delayed by the freeze
        Assert.Equal(SwordMove.None, b.Move);
    }

    [Fact]
    public void The_cone_takes_the_body_in_front_and_not_behind_or_beyond_reach()
    {
        // facing +X from the origin
        Assert.True(SwordRules.InCone(0, 0, 1, 0, 2f, 0f, 2.3f, 55f, out _));
        Assert.True(SwordRules.InCone(0, 0, 1, 0, 2f, 1.0f, 2.3f, 55f, out _));
        Assert.False(SwordRules.InCone(0, 0, 1, 0, -1.5f, 0f, 2.3f, 55f, out _));
        Assert.False(SwordRules.InCone(0, 0, 1, 0, 4f, 0f, 2.3f, 55f, out _));
        Assert.True(SwordRules.InCone(0, 0, 1, 0, -1.5f, 0f, 2.8f, 180f, out _));   // a plunge hits all round
        Assert.False(SwordRules.InCone(0, 0, 1, 0, 0f, 3f, 2.3f, 55f, out _));
    }

    [Fact]
    public void Soft_lock_picks_the_target_nearest_the_aim_inside_the_cone()
    {
        var c = new[]
        {
            new SwordRules.Candidate(4f, 3f, 1),    // off to the side
            new SwordRules.Candidate(3f, 0.2f, 2),  // dead ahead
            new SwordRules.Candidate(-3f, 0f, 3),   // behind
            new SwordRules.Candidate(30f, 0f, 4),   // too far
        };
        int i = SwordRules.Best(c, 0, 0, 1, 0, SwordRules.SoftRange, SwordRules.SoftConeDeg);
        Assert.Equal(2, c[i].Id);
        Assert.Equal(-1, SwordRules.Best(new[] { new SwordRules.Candidate(-3f, 0f, 3) }, 0, 0, 1, 0, SwordRules.SoftRange, SwordRules.SoftConeDeg));
    }

    [Fact]
    public void The_arc_sweeps_and_comes_home()
    {
        foreach (var m in new[] { SwordMove.Slash1, SwordMove.Slash2, SwordMove.Slash3, SwordMove.Slash4, SwordMove.Slash5, SwordMove.Charged })
        {
            var start = SwordRules.Arc(m, 0f);
            var end = SwordRules.Arc(m, 1f);
            Assert.True(MathF.Abs(start.Yaw) + MathF.Abs(start.Pitch) < 1e-3f, $"{m} starts at rest");
            Assert.True(MathF.Abs(end.Yaw) + MathF.Abs(end.Pitch) < 0.05f, $"{m} ends at rest");
        }
        // a slash's sweep passes through the middle: the yaw changes sign across the active frames
        var d = SwordRules.Def(SwordMove.Slash1);
        float t0 = d.Startup / d.Total, t1 = (d.Startup + d.Active) / d.Total;
        Assert.True(SwordRules.Arc(SwordMove.Slash1, t0).Yaw * SwordRules.Arc(SwordMove.Slash1, t1).Yaw < 0f);
    }

    [Fact]
    public void Knock_is_a_little_weaker_at_the_far_end_of_the_reach()
    {
        Assert.True(SwordRules.KnockAt(SwordMove.Slash5, 0.5f) > SwordRules.KnockAt(SwordMove.Slash5, 3f));
        Assert.True(SwordRules.KnockAt(SwordMove.Charged, 1f) > SwordRules.KnockAt(SwordMove.Slash1, 1f));
    }

    // ---- the server's guard ----------------------------------------------------------------------------------

    [Fact]
    public void An_honest_combo_passes_the_guard_and_each_target_is_hit_once_per_swing()
    {
        var g = new SwordGuard();
        double now = 100;
        for (int i = 1; i <= 5; i++)
        {
            var move = SwordRules.Slash(i);
            Assert.Equal(SwordVerdict.Ok, g.TryAct(7, move, now));
            var d = SwordRules.Def(move);
            Assert.True(g.TryHit(7, move, 42, now + d.Startup + 0.01));
            Assert.False(g.TryHit(7, move, 42, now + d.Startup + 0.02), "the same target twice in one swing");
            now += d.Cancel + 0.01;
        }
    }

    [Fact]
    public void The_guard_refuses_a_swing_rate_the_frame_data_forbids()
    {
        var g = new SwordGuard();
        int taken = 0;
        for (int i = 0; i < 100; i++)
            if (g.TryAct(7, SwordMove.Slash1, 50 + i * 0.01) == SwordVerdict.Ok) taken++;
        // a second of 100 swings a second: at most one every cancel window (0.60 s), about two
        Assert.InRange(taken, 1, 3);
    }

    [Fact]
    public void The_guard_refuses_a_hit_before_the_blade_could_be_live_or_long_after()
    {
        var g = new SwordGuard();
        Assert.Equal(SwordVerdict.Ok, g.TryAct(7, SwordMove.Slash5, 10));
        Assert.False(g.TryHit(7, SwordMove.Slash5, 1, 10.0), "the instant it starts");
        Assert.False(g.TryHit(7, SwordMove.Slash5, 1, 16), "seconds after it ended");
        Assert.True(g.TryHit(7, SwordMove.Slash5, 1, 10.25));
    }

    [Fact]
    public void The_guard_refuses_a_hit_of_a_move_that_was_never_announced()
    {
        var g = new SwordGuard();
        Assert.False(g.TryHit(7, SwordMove.Slash1, 1, 10), "no act at all");
        g.TryAct(7, SwordMove.Slash1, 10);
        Assert.False(g.TryHit(7, SwordMove.Charged, 1, 10.2), "another move's hit");
        Assert.False(g.TryHit(7, SwordMove.Dodge, 1, 10.2));
    }

    [Fact]
    public void The_guard_counts_stamina_for_dodges_and_a_charged_blow()
    {
        var g = new SwordGuard();
        double now = 10;
        int dodges = 0;
        for (int i = 0; i < 12; i++, now += 0.4)
            if (g.TryAct(7, SwordMove.Dodge, now) == SwordVerdict.Ok) dodges++;
        // 100 stamina and 12 of leeway: five dodges and a bit of regen, never twelve
        Assert.InRange(dodges, 5, 8);
        // the charged blow only after a charge was announced
        var h = new SwordGuard();
        Assert.Equal(SwordVerdict.TooSoon, h.TryAct(8, SwordMove.Charged, 5));
        h.TryAct(8, SwordMove.Charge, 5);
        Assert.Equal(SwordVerdict.Ok, h.TryAct(8, SwordMove.Charged, 6));
    }

    [Fact]
    public void A_dodge_is_untouchable_only_inside_its_window()
    {
        var g = new SwordGuard();
        Assert.False(g.Invulnerable(7, 20));
        g.TryAct(7, SwordMove.Dodge, 20);
        Assert.True(g.Invulnerable(7, 20.1));
        Assert.False(g.Invulnerable(7, 20.8));
        g.Forget(7);
        Assert.False(g.Invulnerable(7, 20.1));
    }

    [Fact]
    public void A_made_up_move_is_refused()
    {
        var g = new SwordGuard();
        Assert.Equal(SwordVerdict.BadMove, g.TryAct(7, (SwordMove)99, 1));
        Assert.Equal(SwordVerdict.Ok, g.TryAct(7, SwordMove.None, 1));
    }
}
