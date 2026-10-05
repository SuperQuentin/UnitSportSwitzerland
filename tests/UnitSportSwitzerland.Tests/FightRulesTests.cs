using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Fist fights (#495, src/Combat/FightRules.cs, linked in): guards, inputs, rounds.</summary>
public class FightRulesTests
{
    private const long A = 1, B = 2;
    private static readonly byte Stand = (byte)FightStance.Stand;

    [Theory]
    [InlineData(FightMove.Jab, FightStance.Crouch, StrikeOutcome.Whiff)]
    [InlineData(FightMove.Jab, FightStance.GuardHigh, StrikeOutcome.Blocked)]
    [InlineData(FightMove.Kick, FightStance.GuardLow, StrikeOutcome.Blocked)]
    [InlineData(FightMove.LowJab, FightStance.GuardHigh, StrikeOutcome.Hit)]
    [InlineData(FightMove.Sweep, FightStance.GuardLow, StrikeOutcome.Blocked)]
    [InlineData(FightMove.Sweep, FightStance.Air, StrikeOutcome.Whiff)]
    [InlineData(FightMove.JumpKick, FightStance.GuardLow, StrikeOutcome.Hit)]
    [InlineData(FightMove.JumpKick, FightStance.GuardHigh, StrikeOutcome.Blocked)]
    [InlineData(FightMove.Uppercut, FightStance.Down, StrikeOutcome.Whiff)]
    [InlineData(FightMove.Kick, FightStance.Dazed, StrikeOutcome.Whiff)]
    [InlineData(FightMove.Finisher, FightStance.Dazed, StrikeOutcome.Hit)]
    [InlineData(FightMove.Finisher, FightStance.Stand, StrikeOutcome.Whiff)]
    public void Guards_stop_what_they_should(FightMove move, FightStance victim, StrikeOutcome expected) =>
        Assert.Equal(expected, FightRules.Resolve(move, (byte)victim));

    [Fact]
    public void A_victim_mid_sweep_ducks_a_high()
    {
        Assert.Equal(StrikeOutcome.Whiff, FightRules.Resolve(FightMove.Jab, FightRules.PoseOf(FightMove.Sweep)));
        Assert.Equal(StrikeOutcome.Hit, FightRules.Resolve(FightMove.Kick, FightRules.PoseOf(FightMove.Sweep)));
    }

    [Fact]
    public void Pose_round_trips_every_move()
    {
        foreach (FightMove m in Enum.GetValues<FightMove>())
            if (FightRules.IsMove(m)) Assert.Equal(m, FightRules.MoveOf(FightRules.PoseOf(m)));
        Assert.Equal(FightMove.None, FightRules.MoveOf((byte)FightStance.GuardHigh));
    }

    [Fact]
    public void Reach_has_room_for_the_network_but_not_across_the_arena()
    {
        Assert.True(FightRules.InReach(FightMove.Jab, 2.4f));
        Assert.False(FightRules.InReach(FightMove.Jab, 4f));
        Assert.False(FightRules.InReach(FightMove.None, 0.5f));
    }

    [Fact]
    public void Down_forward_punch_is_the_uppercut()
    {
        var input = new FightInput();
        input.Direction(FightKey.Down, 1.0);
        input.Direction(FightKey.Forward, 1.15);
        Assert.Equal(FightMove.Uppercut, input.Press(FightKey.Punch, 1.25, false, false, false));
        // the sequence is spent
        Assert.Equal(FightMove.Jab, input.Press(FightKey.Punch, 2.0, false, false, false));
    }

    [Fact]
    public void A_slow_sequence_is_two_moves_not_a_special()
    {
        var input = new FightInput();
        input.Direction(FightKey.Down, 1.0);
        input.Direction(FightKey.Forward, 2.0);
        Assert.Equal(FightMove.Jab, input.Press(FightKey.Punch, 2.1, false, false, false));
    }

    [Fact]
    public void Jab_jab_kick_is_the_string_and_one_jab_kick_is_not()
    {
        var input = new FightInput();
        Assert.Equal(FightMove.Jab, input.Press(FightKey.Punch, 0, false, false, false));
        Assert.Equal(FightMove.Jab, input.Press(FightKey.Punch, 0.3, false, false, false));
        Assert.Equal(FightMove.StringKick, input.Press(FightKey.Kick, 0.5, false, false, false));

        input.Reset();
        Assert.Equal(FightMove.Jab, input.Press(FightKey.Punch, 0, false, false, false));
        Assert.Equal(FightMove.Kick, input.Press(FightKey.Kick, 0.2, false, false, false));
    }

    [Fact]
    public void Crouch_and_air_change_the_move()
    {
        var input = new FightInput();
        Assert.Equal(FightMove.Sweep, input.Press(FightKey.Kick, 0, true, false, false));
        Assert.Equal(FightMove.LowJab, input.Press(FightKey.Punch, 1, true, false, false));
        Assert.Equal(FightMove.JumpKick, input.Press(FightKey.Kick, 2, false, true, false));
        Assert.Equal(FightMove.None, input.Press(FightKey.Punch, 3, false, true, false));
    }

    [Fact]
    public void Down_down_kick_finishes_only_when_allowed()
    {
        var input = new FightInput();
        input.Direction(FightKey.Down, 1.0);
        input.Direction(FightKey.Down, 1.2);
        Assert.Equal(FightMove.Kick, input.Press(FightKey.Kick, 1.3, false, false, false));
        input.Direction(FightKey.Down, 2.0);
        input.Direction(FightKey.Down, 2.2);
        Assert.Equal(FightMove.Finisher, input.Press(FightKey.Kick, 2.3, false, false, true));
    }

    private static FightMatch Live()
    {
        var m = new FightMatch(A, B, 0);
        Assert.False(m.Tick(1));
        Assert.True(m.Tick(FightMatch.IntroSeconds));
        Assert.Equal(FightPhase.Live, m.Phase);
        return m;
    }

    [Fact]
    public void No_strike_lands_before_FIGHT()
    {
        var m = new FightMatch(A, B, 0);
        Assert.Equal(StrikeOutcome.Refused, m.Strike(A, FightMove.Jab, Stand, 0.5));
        Assert.Equal(FightRules.MaxHp, m.HpB);
    }

    [Fact]
    public void Strikes_faster_than_the_move_are_refused()
    {
        var m = Live();
        double t = 10;
        Assert.Equal(StrikeOutcome.Hit, m.Strike(A, FightMove.Kick, Stand, t));
        Assert.Equal(StrikeOutcome.Refused, m.Strike(A, FightMove.Kick, Stand, t + 0.1));
        Assert.Equal(StrikeOutcome.Hit, m.Strike(A, FightMove.Kick, Stand, t + FightRules.Def(FightMove.Kick).Total));
        Assert.Equal(FightRules.MaxHp - 18, m.HpB);
    }

    [Fact]
    public void A_cheat_spamming_jabs_lands_only_a_real_rate()
    {
        var m = Live();
        int landed = 0;
        for (int i = 0; i < 100; i++)
            if (m.Strike(A, FightMove.Jab, Stand, 10 + i * 0.01) == StrikeOutcome.Hit) landed++;
        Assert.InRange(landed, 1, 1 + (int)(1.0 / (FightRules.Def(FightMove.Jab).Total - FightMatch.RateLeeway)));
    }

    [Fact]
    public void The_string_kick_may_cut_into_the_jab()
    {
        var m = Live();
        Assert.Equal(StrikeOutcome.Hit, m.Strike(A, FightMove.Jab, Stand, 10));
        Assert.Equal(StrikeOutcome.Hit, m.Strike(A, FightMove.StringKick, Stand, 10.15));
    }

    private static void KoB(FightMatch m, ref double t)
    {
        var kick = FightRules.Def(FightMove.Uppercut);
        while (m.HpB > 0 && m.Phase == FightPhase.Live)
        {
            m.Strike(A, FightMove.Uppercut, Stand, t);
            t += kick.Total;
        }
    }

    [Fact]
    public void Two_KOs_win_the_match_and_offer_the_finish()
    {
        var m = Live();
        double t = 10;
        KoB(m, ref t);
        Assert.Equal(FightPhase.RoundOver, m.Phase);
        Assert.Equal(FightEnd.Perfect, m.LastEnd);
        Assert.Equal(1, m.WinsA);

        t += FightMatch.RoundOverSeconds;
        Assert.True(m.Tick(t));
        Assert.Equal(2, m.Round);
        Assert.Equal(FightRules.MaxHp, m.HpB);
        t += FightMatch.IntroSeconds;
        Assert.True(m.Tick(t));

        m.Strike(B, FightMove.Jab, Stand, t);
        t += 1;
        KoB(m, ref t);
        Assert.Equal(FightPhase.FinishHim, m.Phase);
        Assert.Equal(FightEnd.Ko, m.LastEnd);
        Assert.Equal(A, m.Winner);

        // the loser cannot finish, the winner can
        Assert.Equal(StrikeOutcome.Refused, m.Strike(B, FightMove.Finisher, (byte)FightStance.Dazed, t));
        Assert.Equal(StrikeOutcome.Hit, m.Strike(A, FightMove.Finisher, (byte)FightStance.Dazed, t + 1));
        Assert.Equal(FightPhase.Done, m.Phase);
        Assert.Equal(FightEnd.Fatality, m.LastEnd);
        Assert.False(m.Expired(t + 1.5));
        Assert.True(m.Expired(t + 1 + FightMatch.DoneSeconds));
    }

    [Fact]
    public void Time_out_goes_to_the_healthier()
    {
        var m = Live();
        m.Strike(B, FightMove.Jab, Stand, 5);
        Assert.True(m.Tick(FightMatch.IntroSeconds + FightRules.RoundSeconds));
        Assert.Equal(FightEnd.Time, m.LastEnd);
        Assert.Equal(B, m.Winner);
        Assert.Equal(1, m.WinsB);
    }

    [Fact]
    public void Two_draws_end_the_match_drawn()
    {
        var m = Live();
        double t = FightMatch.IntroSeconds + FightRules.RoundSeconds;
        Assert.True(m.Tick(t));
        Assert.Equal(FightEnd.Draw, m.LastEnd);
        t += FightMatch.RoundOverSeconds;
        m.Tick(t);
        t += FightMatch.IntroSeconds;
        m.Tick(t);
        t += FightRules.RoundSeconds;
        m.Tick(t);
        Assert.Equal(FightPhase.Done, m.Phase);
        Assert.Equal(0, m.Winner);
        Assert.Equal(FightEnd.Draw, m.LastEnd);
    }

    [Fact]
    public void A_forfeit_gives_the_match_to_the_other()
    {
        var m = Live();
        m.Forfeit(B, 3);
        Assert.Equal(FightPhase.Done, m.Phase);
        Assert.Equal(A, m.Winner);
        Assert.Equal(FightEnd.Forfeit, m.LastEnd);
    }
}
