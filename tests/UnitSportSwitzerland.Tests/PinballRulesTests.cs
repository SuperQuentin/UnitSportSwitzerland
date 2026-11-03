using UnitSport.Interiors.Pinball;
using Xunit;
using static UnitSport.Interiors.Pinball.PinballSolver;

namespace UnitSportSwitzerland.Tests;

/// <summary>Pinball's rules (#1325, src/Interiors/Pinball/PinballRules.cs) fed events by hand on the Clockwork table.</summary>
public class PinballRulesTests
{
    private static readonly PinballTable T = PinballLayouts.Get(0);
    private int _step;

    private PinballRules Started()
    {
        var r = new PinballRules(T, 5);
        r.Start(0);
        _step = 1000;
        Feed(r, EventKind.Launch, 0);
        return r;
    }

    private void Feed(PinballRules r, EventKind kind, int id, double after = 0.2)
    {
        _step += (int)(after * 1000);
        r.Apply(new Event(_step, kind, id));
    }

    private void Hit(PinballRules r, string sw, double after = 0.2) => Feed(r, EventKind.Switch, T.SwitchIndex(sw), after);

    private void Shot(PinballRules r, string shot, double after = 0.2) => Feed(r, EventKind.Switch, T.ShotNamed(shot)!.Switches[0], after);

    [Fact]
    public void Start_serves_a_ball_and_switches_score()
    {
        var r = new PinballRules(T, 5);
        r.Start(0);
        Assert.Equal(PinballRules.Phase.Playing, r.State);
        Assert.Contains(r.Commands, c => c.Command == PinballRules.Command.ServeBall);
        Assert.Equal(1, r.BallsInPlay);
        _step = 1000;
        Hit(r, "Gear 1");
        Hit(r, "Left sling");
        Assert.Equal(1_110, r.Players[0].Score);
    }

    [Fact]
    public void A_drain_inside_the_ball_save_is_saved_and_after_it_ends_the_ball()
    {
        var r = Started();
        Feed(r, EventKind.Drain, 0, 2.0);
        Assert.Equal("BALL SAVED", r.Message);
        Assert.Contains(r.Commands, c => c.Command == PinballRules.Command.LaunchBall);
        Assert.Equal(1, r.BallsInPlay);
        Assert.Equal(1, r.Players[0].Ball);
        Feed(r, EventKind.Drain, 1, 10.0);
        Assert.Equal(2, r.Players[0].Ball);
    }

    [Fact]
    public void Three_balls_then_game_over_and_a_match_draw()
    {
        var r = Started();
        for (int ball = 0; ball < 3; ball++)
        {
            Feed(r, EventKind.Launch, 0, 1);
            Feed(r, EventKind.Drain, 0, 20);
        }
        Assert.Equal(PinballRules.Phase.GameOver, r.State);
        Assert.InRange(r.MatchDigits, 0, 90);
        Assert.Equal(0, r.MatchDigits % 10);
        Assert.Contains(r.Commands, c => c.Command == PinballRules.Command.FlippersOff);
    }

    [Fact]
    public void The_lit_scoop_starts_a_mission_and_its_goals_complete_it()
    {
        var r = Started();
        Assert.True(r.Players[0].MissionLit);
        Shot(r, "Watch case");
        Assert.Equal(0, r.Mission);
        Assert.Contains(r.Commands, c => c.Command == PinballRules.Command.ReleaseScoop);
        long before = r.Players[0].Score;
        for (int i = 0; i < 25; i++) Shot(r, "Mainspring", 0.1);
        Assert.Equal(-1, r.Mission);
        Assert.True(r.Players[0].Done[0]);
        Assert.True(r.Players[0].Score - before >= T.Missions[0].Award);
    }

    [Fact]
    public void A_mission_runs_out_of_time()
    {
        var r = Started();
        Shot(r, "Watch case");
        Assert.Equal(0, r.Mission);
        r.Advance(r.Now + T.Missions[0].Seconds + 1);
        Assert.Equal(-1, r.Mission);
        Assert.False(r.Players[0].Done[0]);
    }

    [Fact]
    public void A_cleared_drop_bank_lights_the_scoop_again_and_resets()
    {
        var r = Started();
        Shot(r, "Watch case");
        r.Advance(r.Now + 60);
        Assert.False(r.Players[0].MissionLit);
        r.Commands.Clear();
        foreach (int sw in T.ShotNamed("Hours")!.Switches) Feed(r, EventKind.Switch, sw);
        Assert.True(r.Players[0].MissionLit);
        Assert.Contains(r.Commands, c => c.Command == PinballRules.Command.ResetBank);
    }

    [Fact]
    public void Two_locks_start_a_three_ball_multiball_with_the_jackpot()
    {
        var r = Started();
        for (int lockNo = 0; lockNo < 2; lockNo++)
        {
            foreach (int sw in T.LockTargets) Feed(r, EventKind.Switch, sw);
            Assert.True(r.Players[0].LockLit);
            Shot(r, T.LockShot, 5);
        }
        Assert.True(r.Multiball);
        Assert.Equal(3, r.BallsInPlay);
        Assert.Equal(2, r.Commands.Count(c => c.Command == PinballRules.Command.LaunchBall));
        long before = r.Players[0].Score;
        Shot(r, T.JackpotShot, 5);
        Assert.True(r.Players[0].Score - before >= 50_000);
        // the save runs out; two drains end the multiball, the last ball plays on
        Feed(r, EventKind.Drain, 1, 15);
        Feed(r, EventKind.Drain, 2, 1);
        Assert.False(r.Multiball);
        Assert.Equal(1, r.BallsInPlay);
        Assert.Equal(1, r.Players[0].Ball);
    }

    [Fact]
    public void The_top_lanes_raise_the_bonus_multiplier_and_the_flippers_move_them()
    {
        var r = Started();
        r.SkillLane = -1;
        Hit(r, "Top lane 1");
        r.Flipped(1, r.Now);
        Assert.True(r.Players[0].Lanes[1]);
        Hit(r, "Top lane 1");
        Hit(r, "Top lane 3");
        Assert.Equal(2, r.Players[0].BonusX);
        Assert.DoesNotContain(true, r.Players[0].Lanes);
    }

    [Fact]
    public void The_skill_shot_lane_scores_after_the_plunge()
    {
        var r = new PinballRules(T, 5);
        r.Start(0);
        _step = 500;
        Feed(r, EventKind.Launch, 0);
        int lane = r.SkillLane;
        Assert.InRange(lane, 0, 2);
        long before = r.Players[0].Score;
        Feed(r, EventKind.Switch, T.TopLanes[lane], 1.5);
        Assert.Equal(26_000, r.Players[0].Score - before);
    }

    [Fact]
    public void Three_warnings_tilt_and_the_tilted_ball_scores_nothing()
    {
        var r = Started();
        Feed(r, EventKind.TiltWarn, 0, 9);
        Feed(r, EventKind.TiltWarn, 0);
        Assert.False(r.Tilted);
        Feed(r, EventKind.TiltWarn, 0);
        Assert.True(r.Tilted);
        Assert.Contains(r.Commands, c => c.Command == PinballRules.Command.FlippersOff);
        long before = r.Players[0].Score;
        Hit(r, "Gear 1");
        Feed(r, EventKind.Drain, 0, 1);
        Assert.Equal(before, r.Players[0].Score);
        Assert.Equal(2, r.Players[0].Ball);
        Assert.False(r.Tilted);
    }

    [Fact]
    public void Two_missions_light_the_extra_ball_and_the_scoop_gives_it()
    {
        var r = Started();
        var p = r.Players[0];
        p.Done[0] = p.Done[1] = true;
        Shot(r, "Watch case");
        Assert.Equal(2, r.Mission);
        foreach (var (shot, count) in T.Missions[2].Goals)
            for (int i = 0; i < count; i++) Shot(r, shot);
        Assert.True(p.ExtraLit);
        Shot(r, "Watch case", 5);
        Assert.Equal(1, p.ExtraBalls);
        Feed(r, EventKind.Drain, 0, 20);
        Assert.Equal(1, p.Ball);
        Assert.Equal(0, p.ExtraBalls);
    }

    [Fact]
    public void Every_mission_done_starts_the_wizard_mode_from_the_scoop()
    {
        var r = Started();
        Array.Fill(r.Players[0].Done, true);
        r.Players[0].MissionLit = true;
        Shot(r, "Watch case");
        Assert.True(r.Wizard);
        Assert.True(r.Multiball);
        Assert.Equal(3, r.BallsInPlay);
        long before = r.Players[0].Score;
        Shot(r, "Left orbit", 5);
        Assert.True(r.Players[0].Score - before >= 100_000);
    }

    [Fact]
    public void Major_shots_in_quick_succession_are_combos()
    {
        var r = Started();
        Shot(r, "Left orbit", 1);
        long before = r.Players[0].Score;
        Shot(r, "Right orbit", 1.5);
        Assert.Equal(1, r.Combo);
        Assert.Equal(7_500 + 20_000, r.Players[0].Score - before);
    }

    [Fact]
    public void A_second_player_joins_during_the_first_ball_and_turns_alternate()
    {
        var r = Started();
        Assert.True(r.Start(r.Now));
        Assert.Equal(2, r.Players.Count);
        Feed(r, EventKind.Drain, 0, 20);
        Assert.Equal(1, r.Up);
        Assert.False(r.Start(r.Now + 1), "no one joins after the first ball");
    }

    [Fact]
    public void Lamps_follow_the_state()
    {
        var r = Started();
        var scoopLamp = T.Lamps.First(l => l.Kind == PinballTable.LampKind.Scoop);
        Assert.Equal(PinballRules.Lit.Blink, r.LampState(scoopLamp));
        Shot(r, "Watch case");
        var goal = T.Lamps.First(l => l.Kind == PinballTable.LampKind.Shot && l.Shot == "Mainspring" || l.Kind == PinballTable.LampKind.Mission && l.Index == 0);
        Assert.Equal(PinballRules.Lit.Blink, r.LampState(goal));
        Assert.Equal(PinballRules.Lit.Off, r.LampState(scoopLamp));
    }
}
