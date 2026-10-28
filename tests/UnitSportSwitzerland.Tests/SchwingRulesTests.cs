using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Schwingen (#1269, src/Combat/SchwingRules.cs, linked in): throws and their answers, balance, Bodenarbeit, the result.</summary>
public class SchwingRulesTests
{
    private const long A = 1, B = 2;

    private static SchwingMatch Live(out double now, bool schlussgang = false)
    {
        var m = new SchwingMatch(A, B, 0, schlussgang);
        now = SchwingRules.IntroSeconds + 0.1;
        m.Tick(now);
        Assert.Equal(SchwingPhase.Live, m.Phase);
        return m;
    }

    private static void Run(SchwingMatch m, ref double now, double seconds)
    {
        double end = now + seconds;
        while (now < end) { now += 0.1; m.Tick(now); }
    }

    [Fact]
    public void Every_throw_has_a_distinct_brace_and_counter_and_the_table_is_complete()
    {
        Assert.Equal(6, SchwingRules.Throws.Count);
        foreach (var d in SchwingRules.Throws)
        {
            Assert.NotEqual(SchwingLean.None, d.Brace);
            Assert.NotEqual(SchwingLean.None, d.Counter);
            Assert.NotEqual(d.Brace, d.Counter);
            Assert.True(d.Weight > 0 && d.Name.Length > 0);
            Assert.Equal(d.Throw, SchwingRules.Def(d.Throw).Throw);
        }
        Assert.Equal(SchwingThrow.None, SchwingRules.Def(SchwingThrow.None).Throw);
    }

    [Theory]
    [InlineData(SchwingThrow.Kurz, SchwingLean.Out, true, SchwingAnswer.Braced)]
    [InlineData(SchwingThrow.Kurz, SchwingLean.Out, false, SchwingAnswer.Landed)]
    [InlineData(SchwingThrow.Kurz, SchwingLean.Left, false, SchwingAnswer.Countered)]
    [InlineData(SchwingThrow.Hueften, SchwingLean.Right, false, SchwingAnswer.Countered)]
    [InlineData(SchwingThrow.Hueften, SchwingLean.None, true, SchwingAnswer.Landed)]
    [InlineData(SchwingThrow.Brienzer, SchwingLean.In, true, SchwingAnswer.Braced)]
    public void Telegraph_answers(SchwingThrow t, SchwingLean lean, bool brace, SchwingAnswer expected) =>
        Assert.Equal(expected, SchwingRules.Answer(t, lean, brace));

    [Theory]
    [InlineData(false, SchwingLean.None, SchwingThrow.Kurz)]
    [InlineData(false, SchwingLean.Out, SchwingThrow.Brienzer)]
    [InlineData(false, SchwingLean.Left, SchwingThrow.Hueften)]
    [InlineData(false, SchwingLean.Right, SchwingThrow.Uebersprung)]
    [InlineData(true, SchwingLean.None, SchwingThrow.Gammen)]
    [InlineData(true, SchwingLean.Out, SchwingThrow.Wyberhaken)]
    public void The_buttons_and_the_stick_choose_the_throw(bool kick, SchwingLean stick, SchwingThrow expected) =>
        Assert.Equal(expected, SchwingRules.Choose(kick, stick));

    [Fact]
    public void The_stick_makes_a_lean_with_a_dead_zone()
    {
        Assert.Equal(SchwingLean.None, SchwingRules.LeanOf(0.3f, -0.2f));
        Assert.Equal(SchwingLean.In, SchwingRules.LeanOf(0.9f, 0.2f));
        Assert.Equal(SchwingLean.Out, SchwingRules.LeanOf(-0.9f, 0.2f));
        Assert.Equal(SchwingLean.Left, SchwingRules.LeanOf(0.2f, 0.9f));
        Assert.Equal(SchwingLean.Right, SchwingRules.LeanOf(0.2f, -0.9f));
    }

    [Fact]
    public void Nothing_happens_before_the_bout_is_live()
    {
        var m = new SchwingMatch(A, B, 0);
        Assert.False(m.StartThrow(A, SchwingThrow.Kurz, 0.5));
        Assert.False(m.SetLean(A, SchwingLean.In, false));
    }

    [Fact]
    public void An_unanswered_throw_costs_the_defender_balance_after_the_telegraph()
    {
        var m = Live(out double now);
        Assert.True(m.StartThrow(A, SchwingThrow.Kurz, now));
        Assert.Equal(SchwingRules.MaxGrip - SchwingRules.GripCost, m.GripA);
        Run(m, ref now, SchwingRules.Telegraph - 0.15);
        Assert.Equal(SchwingThrow.Kurz, m.Pending);   // still winding up
        Assert.Equal(SchwingRules.MaxBalance, m.BalanceB);
        Run(m, ref now, 0.3);
        Assert.Equal(SchwingThrow.None, m.Pending);
        Assert.True(m.BalanceB < SchwingRules.MaxBalance);
        Assert.Equal(SchwingAnswer.Landed, m.LastAnswer);
        Assert.Equal(SchwingPhase.Live, m.Phase);
    }

    [Fact]
    public void A_braced_throw_costs_the_thrower_instead()
    {
        var m = Live(out double now);
        m.StartThrow(A, SchwingThrow.Kurz, now);
        m.SetLean(B, SchwingLean.Out, true);
        Run(m, ref now, 0.5);
        Assert.Equal(SchwingAnswer.Braced, m.LastAnswer);
        Assert.True(m.BalanceA < SchwingRules.MaxBalance);
        Assert.True(m.BalanceB >= SchwingRules.MaxBalance - 0.01f);
    }

    [Fact]
    public void A_counter_costs_the_thrower_more_than_a_brace()
    {
        var m = Live(out double now);
        m.StartThrow(A, SchwingThrow.Kurz, now);
        m.SetLean(B, SchwingLean.Left, false);
        Run(m, ref now, 0.45);
        Assert.Equal(SchwingAnswer.Countered, m.LastAnswer);
        Assert.True(m.BalanceA <= SchwingRules.MaxBalance - 15f);
    }

    [Fact]
    public void A_throw_needs_grip_and_a_free_hand_and_has_a_cooldown()
    {
        var m = Live(out double now);
        Assert.True(m.StartThrow(A, SchwingThrow.Kurz, now));
        Assert.False(m.StartThrow(A, SchwingThrow.Hueften, now + 0.01), "one throw at a time");
        Assert.False(m.StartThrow(B, SchwingThrow.Kurz, now + 0.01), "the rival cannot throw while one is under way");
        Run(m, ref now, 0.5);
        Assert.False(m.StartThrow(A, SchwingThrow.Hueften, now), "cooling down");
        Run(m, ref now, 0.9);
        Assert.True(m.StartThrow(A, SchwingThrow.Hueften, now));
        var empty = Live(out double t2);
        empty.GripA = SchwingRules.GripNeeded - 1;
        Assert.False(empty.StartThrow(A, SchwingThrow.Kurz, t2), "no grip left");
    }

    [Fact]
    public void Leaning_against_each_other_wears_both_grips_and_one_alone_pushes()
    {
        var m = Live(out double now);
        m.SetLean(A, SchwingLean.In, false);
        m.SetLean(B, SchwingLean.Out, false);
        Run(m, ref now, 2);
        Assert.True(m.GripA < 95f && m.GripB < 95f);
        var p = Live(out double n2);
        p.SetLean(A, SchwingLean.In, false);
        Run(p, ref n2, 3);
        Assert.True(p.BalanceB < SchwingRules.MaxBalance);
        Assert.Equal(SchwingRules.MaxBalance, p.BalanceA);
    }

    [Fact]
    public void Balance_comes_back_when_nobody_pushes()
    {
        var m = Live(out double now);
        m.BalanceB = 50f;
        Run(m, ref now, 3);
        Assert.True(m.BalanceB > 60f);
    }

    private static SchwingMatch ThrownDown(out double now)
    {
        var m = Live(out now);
        m.BalanceB = 30f;   // one unanswered throw takes it under the floor
        m.StartThrow(A, SchwingThrow.Brienzer, now);
        Run(m, ref now, 0.5);
        return m;
    }

    [Fact]
    public void A_landed_throw_under_the_balance_floor_goes_to_the_ground()
    {
        var m = ThrownDown(out _);
        Assert.Equal(SchwingPhase.Ground, m.Phase);
        Assert.Equal(A, m.Top);
        Assert.Equal(SchwingAnswer.Down, m.LastAnswer);
        Assert.Contains(m.Events, e => e.Kind == SchwingEventKind.GroundBegan);
    }

    [Fact]
    public void Bodenarbeit_pinned_with_the_grip_is_a_win_for_the_thrower()
    {
        var m = ThrownDown(out double now);
        for (int i = 0; i < 40 && m.Phase == SchwingPhase.Ground; i++)
        {
            now += 0.1;
            m.PressTug(A, now);
            m.Tick(now);
        }
        Assert.Equal(SchwingPhase.Done, m.Phase);
        Assert.Equal(SchwingEnd.Won, m.End);
        Assert.Equal(A, m.Winner);
    }

    [Fact]
    public void Pinned_without_a_hand_on_the_trousers_is_only_gestellt()
    {
        var m = ThrownDown(out double now);
        m.GripA = 0;
        for (int i = 0; i < 40 && m.Phase == SchwingPhase.Ground; i++)
        {
            now += 0.1;
            m.PressTug(A, now);
            m.Tick(now);
        }
        Assert.Equal(SchwingEnd.Gestellt, m.End);
        Assert.Equal(0, m.Winner);
    }

    [Fact]
    public void The_one_underneath_can_twist_out()
    {
        var m = ThrownDown(out double now);
        for (int i = 0; i < 40 && m.Phase == SchwingPhase.Ground; i++)
        {
            now += 0.1;
            m.PressTug(B, now);
            m.Tick(now);
        }
        Assert.Equal(SchwingPhase.Live, m.Phase);
        Assert.Equal(60f, m.BalanceB);
        Assert.Contains(m.Events, e => e.Kind == SchwingEventKind.Escaped && e.Who == B);
    }

    [Fact]
    public void The_tug_is_rate_limited_and_the_thrower_waits_for_its_hold()
    {
        var m = ThrownDown(out double now);
        Assert.False(m.PressTug(A, now + 0.01), "the first beat takes the hold");
        Assert.True(m.PressTug(B, now + 0.01));
        Assert.False(m.PressTug(B, now + 0.02), "too soon after its last press");
        Assert.True(m.PressTug(A, now + 0.5));
    }

    [Fact]
    public void Nobody_pressing_on_the_ground_goes_to_whoever_leads_after_six_seconds()
    {
        var m = ThrownDown(out double now);
        m.PressTug(A, now + 0.5);   // 57: the thrower leads
        Run(m, ref now, SchwingRules.GroundSeconds + 0.5);
        Assert.Equal(SchwingEnd.Won, m.End);
        Assert.Equal(A, m.Winner);
    }

    [Fact]
    public void A_time_out_is_gestellt_and_the_clock_is_longer_in_the_Schlussgang()
    {
        var m = Live(out double now);
        Run(m, ref now, SchwingRules.GangSeconds + 1);
        Assert.Equal(SchwingPhase.Done, m.Phase);
        Assert.Equal(SchwingEnd.Gestellt, m.End);
        Assert.Equal(0, m.Winner);
        var s = Live(out double n2, schlussgang: true);
        Run(s, ref n2, SchwingRules.GangSeconds + 1);
        Assert.Equal(SchwingPhase.Live, s.Phase);
        Assert.Equal(SchwingRules.SchlussgangSeconds, s.Length);
    }

    [Fact]
    public void A_forfeit_gives_the_bout_to_the_other_and_the_bout_expires()
    {
        var m = Live(out double now);
        m.Forfeit(A, now);
        Assert.Equal(B, m.Winner);
        Assert.Equal(SchwingEnd.Forfeit, m.End);
        Assert.False(m.Expired(now));
        Assert.True(m.Expired(now + SchwingRules.DoneSeconds + 0.1));
        m.Forfeit(B, now);   // no second ending
        Assert.Equal(B, m.Winner);
    }

    [Fact]
    public void Acts_are_budgeted_per_second()
    {
        var m = Live(out double now);
        int ok = 0;
        for (int i = 0; i < 100; i++) if (m.Allow(A, now + i * 0.001)) ok++;
        Assert.Equal(SchwingRules.ActsPerSecond, ok);
        Assert.True(m.Allow(A, now + 1.2));
        Assert.True(m.Allow(B, now), "the other wrestler has its own budget");
    }

    [Fact]
    public void Scores_and_rewards_follow_the_sport()
    {
        Assert.Equal(10f, SchwingRules.Score(true, false));
        Assert.Equal(9f, SchwingRules.Score(false, true));
        Assert.Equal(8.5f, SchwingRules.Score(false, false));
        var win = SchwingRules.Reward(true, false, false);
        var draw = SchwingRules.Reward(false, true, false);
        var loss = SchwingRules.Reward(false, false, false);
        Assert.True(win.Batzen > draw.Batzen && draw.Batzen > loss.Batzen && loss.Batzen > 0);
        Assert.True(SchwingRules.Reward(true, false, true).Batzen > win.Batzen);
    }

    [Fact]
    public void Poses_follow_the_state()
    {
        var m = Live(out double now);
        Assert.Equal((int)SchwingStance.Grip, m.PoseOf(A, now));
        m.SetLean(A, SchwingLean.In, false);
        Assert.Equal((int)SchwingStance.LeanIn, m.PoseOf(A, now));
        m.SetLean(B, SchwingLean.None, true);
        Assert.Equal((int)SchwingStance.Brace, m.PoseOf(B, now));
        m.StartThrow(A, SchwingThrow.Gammen, now);
        Assert.Equal(SchwingRules.PoseOf(SchwingThrow.Gammen), m.PoseOf(A, now));
        Assert.Equal(SchwingThrow.Gammen, SchwingRules.ThrowOf(m.PoseOf(A, now)));
        Assert.Equal(0, m.PoseOf(99, now));
        var g = ThrownDown(out double t2);
        Assert.Equal((int)SchwingStance.GroundTop, g.PoseOf(A, t2));
        Assert.True(SchwingRules.Lying(g.PoseOf(B, t2)));
    }
}
