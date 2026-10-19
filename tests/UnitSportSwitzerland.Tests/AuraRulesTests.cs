using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Aura battles (#907, src/Combat/AuraRules.cs + AuraMatch.cs, linked in): timing, score, turns.</summary>
public class AuraRulesTests
{
    private const long A = 1, B = 2;
    private static readonly AuraMove[] Opening = { AuraMove.StareDown, AuraMove.SlowWalk, AuraMove.Siuuu };
    private static AuraGrade[] All(AuraGrade g, int n) => Enumerable.Repeat(g, n).ToArray();

    [Fact]
    public void Catalog_ids_are_pinned_and_round_trip()
    {
        Assert.Equal(1, (int)AuraMove.StandTall);
        Assert.Equal(9, (int)AuraMove.Siuuu);
        Assert.Equal(13, (int)AuraMove.CutTheRope);
        Assert.Equal(20, (int)AuraMove.Bow);
        Assert.Equal(20, AuraRules.Moves.Length);
        foreach (var m in AuraRules.Moves)
        {
            var def = AuraRules.Def(m);
            Assert.Equal(m, def.Move);
            Assert.True(def.Beats is 2 or 4, $"{m} beats");
            Assert.InRange(def.Difficulty, 1, 3);
            Assert.False(string.IsNullOrEmpty(def.Name));
            foreach (var c in def.Counters) Assert.True(AuraRules.IsMove(c), $"{m} counters {c}");
            Assert.Equal(m, AuraRules.MoveOf(AuraRules.PoseOf(m)));
        }
        Assert.Equal(AuraMove.None, AuraRules.MoveOf((int)AuraStance.Waiting));
    }

    [Fact]
    public void A_plan_holds_three_to_six_known_moves_within_sixteen_beats()
    {
        Assert.True(AuraRules.ValidatePlan(Opening));
        Assert.False(AuraRules.ValidatePlan(new[] { AuraMove.StareDown, AuraMove.Siuuu }));
        Assert.False(AuraRules.ValidatePlan(All2(AuraMove.StandTall, 7)));
        Assert.True(AuraRules.ValidatePlan(new[] { AuraMove.StareDown, AuraMove.SlowWalk, AuraMove.Griddy, AuraMove.Siuuu }));
        Assert.False(AuraRules.ValidatePlan(new[] { AuraMove.StareDown, AuraMove.SlowWalk, AuraMove.Griddy, AuraMove.Siuuu, AuraMove.Yawn }));
        Assert.False(AuraRules.ValidatePlan(new[] { AuraMove.StareDown, AuraMove.SlowWalk, (AuraMove)99 }));
    }

    private static AuraMove[] All2(AuraMove m, int n) => Enumerable.Repeat(m, n).ToArray();

    [Fact]
    public void The_schedule_says_which_move_is_on()
    {
        // stare down 4 beats, slow walk 4, Siuuu 4: 2 s each at 120 BPM, from t0 = 10
        Assert.Equal(-1, AuraRules.MoveAt(Opening, 10, 9.9));
        Assert.Equal(0, AuraRules.MoveAt(Opening, 10, 10));
        Assert.Equal(1, AuraRules.MoveAt(Opening, 10, 12.0));
        Assert.Equal(2, AuraRules.MoveAt(Opening, 10, 15.9));
        Assert.Equal(2, AuraRules.MoveAt(Opening, 10, 30));
        Assert.Equal(12, AuraRules.CutAt(Opening, 0, 10), 6);
        Assert.Equal(12, AuraRules.StartAt(Opening, 1, 10), 6);
        Assert.Equal(8, AuraRules.NextBar(5.5, 2), 6);
    }

    [Fact]
    public void A_short_plan_is_padded_and_a_bad_one_refused()
    {
        Assert.Equal(All2(AuraMove.StandTall, 3), AuraRules.Accept(null));
        Assert.Equal(new[] { AuraMove.Siuuu, AuraMove.StandTall, AuraMove.StandTall }, AuraRules.Accept(new[] { AuraMove.Siuuu }));
        Assert.Null(AuraRules.Accept(All2(AuraMove.StandTall, 7)));
        Assert.Null(AuraRules.Accept(new[] { (AuraMove)200 }));
    }

    [Theory]
    [InlineData(0.0, 1, AuraGrade.Perfect)]
    [InlineData(0.05, 1, AuraGrade.Perfect)]
    [InlineData(0.051, 1, AuraGrade.Good)]
    [InlineData(-0.10, 1, AuraGrade.Good)]
    [InlineData(0.16, 2, AuraGrade.Ok)]
    [InlineData(-0.161, 2, AuraGrade.Miss)]
    [InlineData(0.045, 3, AuraGrade.Good)]   // a hard move's Perfect is ±0.0425
    [InlineData(0.14, 3, AuraGrade.Miss)]    // and its Ok ±0.136
    public void A_press_is_graded_by_its_distance_to_the_downbeat(double offset, int difficulty, AuraGrade expected) =>
        Assert.Equal(expected, AuraRules.Grade(offset, difficulty));

    [Fact]
    public void Worked_example_the_opening_combo()
    {
        var s = AuraRules.Score(Opening, All(AuraGrade.Good, 3));
        Assert.Equal(25.6f, s.Base, 3);
        Assert.Equal(6, s.Variety);   // stare, walk, meme
        Assert.Equal(2, s.Chain);     // stare into the walk
        Assert.Equal(2, s.Opener);
        Assert.Equal(5, s.Finisher);  // Siuuu held
        Assert.Equal(0, s.TryHard);
        Assert.Equal(41, s.Total);
    }

    [Fact]
    public void Worked_example_answering_it()
    {
        var plan = new[] { AuraMove.Yawn, AuraMove.Sunglasses, AuraMove.PointToSky };
        var s = AuraRules.Score(plan, All(AuraGrade.Perfect, 3), new AuraContext { RivalLast = Opening });
        Assert.Equal(26, s.Base, 3);
        Assert.Equal(3, s.Variety);
        Assert.Equal(2, s.Chain);
        Assert.Equal(5, s.Finisher);
        Assert.Equal(18, s.Answer);   // the yawn flips the Siuuu (10), the sunglasses meet the stare (8)
        Assert.Equal(0, s.Streak);
        Assert.Equal(54, s.Total);
    }

    [Fact]
    public void Repeating_a_move_pays_less_each_time()
    {
        var s = AuraRules.Score(All2(AuraMove.StandTall, 3), All(AuraGrade.Good, 3));
        Assert.Equal(6, s.Base, 3);   // 4 + 2 + 0
        Assert.Equal(-4, s.Repeat);
        Assert.Equal(7, s.Total);
    }

    [Fact]
    public void Stale_and_bitten_moves_are_halved()
    {
        Assert.Equal(37, AuraRules.Score(Opening, All(AuraGrade.Good, 3), new AuraContext { OwnPrevious = new[] { AuraMove.StareDown } }).Total);
        Assert.Equal(23.68f, AuraRules.Score(Opening, All(AuraGrade.Good, 3), new AuraContext { OwnEarlier = new[] { AuraMove.StareDown } }).Base, 3);
        Assert.Equal(35, AuraRules.Score(Opening, All(AuraGrade.Good, 3), new AuraContext { RivalLast = new[] { AuraMove.Siuuu } }).Total);
    }

    [Fact]
    public void Trying_too_hard_costs()
    {
        var plan = new[] { AuraMove.Lasso, AuraMove.Siuuu, AuraMove.Mewing, AuraMove.SixSeven, AuraMove.PointToSky };
        var s = AuraRules.Score(plan, All(AuraGrade.Good, 5));
        Assert.Equal(-11, s.TryHard); // difficulty 12 (−6), a third meme (−5)
    }

    [Fact]
    public void A_missed_press_breaks_the_chain_and_a_missed_finish_fails_the_pose()
    {
        var mid = AuraRules.Score(Opening, new[] { AuraGrade.Good, AuraGrade.Miss, AuraGrade.Good });
        Assert.Equal(-2, mid.Stumble);
        Assert.Equal(0, mid.Chain);
        Assert.Equal(3, mid.Variety);
        Assert.Equal(26, mid.Total);

        var end = AuraRules.Score(Opening, new[] { AuraGrade.Good, AuraGrade.Good, AuraGrade.Miss });
        Assert.Equal(-6, end.Finisher);
        Assert.Equal(13, end.Total);
    }

    [Fact]
    public void A_perfect_streak_adds_past_the_third()
    {
        var plan = new[] { AuraMove.StareDown, AuraMove.SlowWalk, AuraMove.ArmsCrossed, AuraMove.Mewing, AuraMove.PointToSky, AuraMove.Yawn };
        Assert.True(AuraRules.ValidatePlan(plan));
        Assert.Equal(3, AuraRules.Score(plan, All(AuraGrade.Perfect, 6)).Streak);
    }

    [Fact]
    public void The_crowd_never_beats_skill()
    {
        Assert.Equal(2, AuraRules.Score(Opening, All(AuraGrade.Good, 3), new AuraContext { Crowd = 1 }).Crowd);
        Assert.Equal(8, AuraRules.Score(Opening, All(AuraGrade.Good, 3), new AuraContext { Crowd = 10 }).Crowd);
        // a weak turn: a quarter of 7
        Assert.Equal(1, AuraRules.Score(All2(AuraMove.StandTall, 3), All(AuraGrade.Good, 3), new AuraContext { Crowd = 5 }).Crowd);
    }

    [Fact]
    public void Score_parts_survive_the_wire()
    {
        var s = AuraRules.Score(Opening, All(AuraGrade.Good, 3), new AuraContext { Crowd = 2 });
        Assert.Equal(s.Total, AuraScore.FromParts(s.ToParts()).Total);
    }

    [Fact]
    public void A_suggested_plan_is_valid_and_answers_the_rival()
    {
        var plain = AuraRules.SuggestPlan();
        Assert.True(AuraRules.ValidatePlan(plain));
        Assert.True(AuraRules.Estimate(plain).Total >= AuraRules.Estimate(Opening).Total);

        var ctx = new AuraContext { RivalLast = Opening };
        var answer = AuraRules.SuggestPlan(ctx);
        Assert.True(AuraRules.ValidatePlan(answer));
        Assert.True(AuraRules.Estimate(answer, ctx).Answer > 0);
    }

    // --- the match ---

    /// <summary>Plans <paramref name="plan"/> at <paramref name="now"/>, presses each Next at its downbeat + <paramref name="late"/>, runs the verdict out.</summary>
    private static double Play(AuraMatch m, double now, long who, AuraMove[] plan, double late = 0)
    {
        Assert.Equal(AuraPhase.Plan, m.Phase);
        Assert.Equal(who, m.Performer);
        Assert.True(m.SubmitPlan(who, m.Turn, plan, now));
        now = m.T0;
        m.Tick(now);
        Assert.Equal(AuraPhase.Perform, m.Phase);
        for (int i = 0; i < plan.Length; i++)
        {
            now = AuraRules.CutAt(plan, i, m.T0) + late;
            m.Tick(now);
            Assert.NotNull(m.Next(who, m.Turn, i, now));
        }
        now += 0.01;
        m.Tick(now);
        Assert.Equal(AuraPhase.Verdict, m.Phase);
        now = m.PhaseEnds;
        m.Tick(now);
        return now;
    }

    [Fact]
    public void A_turn_plans_counts_down_to_the_bar_and_grades_each_press()
    {
        var m = new AuraMatch(A, B, 0);
        m.Tick(AuraMatch.IntroSeconds);
        Assert.Equal(AuraPhase.Plan, m.Phase);
        Assert.Equal(A, m.Performer);
        Assert.False(m.SubmitPlan(B, 0, Opening, 4));         // not B's turn
        Assert.False(m.SubmitPlan(A, 0, All2(AuraMove.StandTall, 9), 4));
        Assert.True(m.SubmitPlan(A, 0, Opening, 4));
        Assert.Equal(6, m.T0, 6);                              // the first bar 2 s on
        Assert.Equal(AuraPhase.Countdown, m.Phase);

        m.Tick(6);
        Assert.Equal(AuraPhase.Perform, m.Phase);
        Assert.Null(m.Next(A, 0, 0, 7.0));                     // a beat early: a double tap, ignored
        Assert.Null(m.Next(A, 0, 1, 8.0));                     // out of order
        Assert.Equal(AuraGrade.Perfect, m.Next(A, 0, 0, 8.0)); // stare down ends on beat 4
        Assert.Equal(AuraGrade.Miss, m.Next(A, 0, 1, 9.7));    // rushed: 0.3 s early
        var frozen = new List<int>();
        m.Tick(12.3, frozen);                                   // the Siuuu never cut: frozen
        Assert.Equal(new[] { 2 }, frozen);
        Assert.Equal(AuraPhase.Verdict, m.Phase);
        Assert.Equal(new[] { AuraGrade.Perfect, AuraGrade.Miss, AuraGrade.Miss }, m.LastTurn!.Grades);
        Assert.Equal(m.LastTurn.Score.Total, m.TotalA);

        m.Tick(m.PhaseEnds);
        Assert.Equal(AuraPhase.Plan, m.Phase);
        Assert.Equal(1, m.Turn);
        Assert.Equal(B, m.Performer);
    }

    [Fact]
    public void Nothing_planned_stands_tall_three_times()
    {
        var m = new AuraMatch(A, B, 0);
        m.Tick(AuraMatch.IntroSeconds);
        m.Tick(m.PhaseEnds + AuraMatch.PlanGrace + 0.01);
        Assert.Equal(AuraPhase.Countdown, m.Phase);
        Assert.Equal(All2(AuraMove.StandTall, 3), m.Plan);
    }

    [Fact]
    public void Round_two_opens_with_B_and_round_three_with_the_leader()
    {
        var weak = All2(AuraMove.StandTall, 3);
        var m = new AuraMatch(A, B, 0);
        double now = AuraMatch.IntroSeconds;
        m.Tick(now);
        now = Play(m, now, A, Opening);
        now = Play(m, now, B, weak);
        now = Play(m, now, B, weak);
        now = Play(m, now, A, new[] { AuraMove.Yawn, AuraMove.Sunglasses, AuraMove.PointToSky });
        Assert.True(m.TotalA > m.TotalB);
        Assert.Equal(A, m.Performer);   // A leads, so A opens round 3 and B answers
        now = Play(m, now, A, new[] { AuraMove.Lasso, AuraMove.Mewing, AuraMove.ArmsCrossed });
        now = Play(m, now, B, new[] { AuraMove.CutTheRope, AuraMove.Griddy, AuraMove.StandTall });
        Assert.Equal(AuraPhase.Done, m.Phase);
        Assert.Equal(A, m.Winner);
        Assert.Equal(6, m.History.Count);
        Assert.Equal(m.History.Where(t => t.Performer == A).Max(t => t.Score.Total), m.BestOf(A)!.Score.Total);
        Assert.False(m.Expired(now));
        Assert.True(m.Expired(now + AuraMatch.DoneSeconds));
    }

    [Fact]
    public void Answers_and_stale_moves_read_the_history()
    {
        var m = new AuraMatch(A, B, 0);
        double now = AuraMatch.IntroSeconds;
        m.Tick(now);
        now = Play(m, now, A, Opening);
        now = Play(m, now, B, new[] { AuraMove.Yawn, AuraMove.Sunglasses, AuraMove.PointToSky });
        Assert.Equal(18, m.LastTurn!.Score.Answer);
        var ctx = m.ContextFor(B);  // round 2 opens with B
        Assert.Equal(new[] { AuraMove.Yawn, AuraMove.Sunglasses, AuraMove.PointToSky }, ctx.OwnPrevious);
        Assert.Equal(Opening, ctx.RivalLast);
    }

    [Fact]
    public void Leaving_forfeits()
    {
        var m = new AuraMatch(A, B, 0);
        m.Tick(AuraMatch.IntroSeconds);
        m.Forfeit(A, 5);
        Assert.Equal(AuraPhase.Done, m.Phase);
        Assert.Equal(B, m.Winner);
        Assert.True(m.Forfeited);
        Assert.False(m.SubmitPlan(A, 0, Opening, 5));
    }

    // --- #914 ---

    [Theory]
    [InlineData(70f, 140f)]
    [InlineData(174f, 87f)]
    [InlineData(128f, 128f)]
    [InlineData(0f, 120f)]
    [InlineData(float.NaN, 120f)]
    public void A_radio_tempo_is_folded_into_range(float bpm, float expected) =>
        Assert.Equal(expected, AuraRules.FoldBpm(bpm), 3);

    [Fact]
    public void Bars_count_from_the_music_downbeat()
    {
        // 150 BPM: 0.4 s a beat, 1.6 s a bar, a downbeat at 0.3
        Assert.Equal(13.1, AuraRules.NextBar(10, 2, 0.4, 0.3), 6);
        var plan = new[] { AuraMove.StareDown, AuraMove.Floss };
        Assert.Equal(13.1 + 1.6, AuraRules.CutAt(plan, 0, 13.1, 0.4), 6);
        Assert.Equal(1, AuraRules.MoveAt(plan, 13.1, 14.8, 0.4));
    }

    [Fact]
    public void The_new_moves_answer_and_chain()
    {
        var lasso = new[] { AuraMove.Lasso, AuraMove.LeadRival, AuraMove.ArmsCrossed };
        Assert.Equal(8, AuraRules.Score(lasso, All(AuraGrade.Good, 3)).Chain);   // the pair +5, mime into the pose +3
        Assert.True(AuraRules.Counters(AuraMove.CutTheRope, AuraMove.LeadRival));
        Assert.True(AuraRules.Counters(AuraMove.Moonwalk, AuraMove.SlowWalk));
        Assert.True(AuraRules.Counters(AuraMove.Shush, AuraMove.SixSeven));
    }

    [Fact]
    public void A_bot_plans_worse_the_less_skilled()
    {
        Assert.Equal(AuraRules.SuggestPlan(), AuraRules.SuggestPlan(null, 1f, 7));
        float sum = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            var clumsy = AuraRules.SuggestPlan(null, 0f, seed);
            Assert.True(AuraRules.ValidatePlan(clumsy), $"seed {seed}: {string.Join(",", clumsy)}");
            sum += AuraRules.Estimate(clumsy).Raw;
        }
        // greedy is not optimal, so one lucky draw may beat it; on average a clumsy bot plans worse
        Assert.True(sum / 20 < AuraRules.Estimate(AuraRules.SuggestPlan()).Raw);
    }

    /// <summary>A's turn 1 up to the perform phase, 0.5 s beats from 0.</summary>
    private static AuraMatch Performing()
    {
        var m = new AuraMatch(A, B, 0);
        m.Tick(AuraMatch.IntroSeconds);
        Assert.True(m.SubmitPlan(A, 0, Opening, 4));
        m.Tick(m.T0);
        return m;
    }

    [Fact]
    public void A_perfect_cut_tempts_the_waiter_who_keeps_a_straight_face()
    {
        var m = Performing();
        Assert.Equal(AuraGrade.Perfect, m.Next(A, 0, 0, 8.0));
        Assert.True(m.TemptOpened);
        Assert.Equal(8.0, m.TemptAt, 6);
        Assert.Null(m.Compose(A, 0, 8.3, 8.3));   // the performer has nothing to hold in
        Assert.Equal(true, m.Compose(B, 0, 8.4, 8.5));
        Assert.True(double.IsNaN(m.TemptAt));
        m.Tick(9.9);
        Assert.False(m.Laughed);
    }

    [Fact]
    public void An_unanswered_tempt_is_a_laugh()
    {
        var m = Performing();
        m.Next(A, 0, 0, 8.0);
        m.Tick(8.0 + AuraRules.TemptWindow + AuraMatch.TemptSlack + 0.01);
        Assert.True(m.Laughed);
        Assert.Equal(1, m.LaughsB);
        Assert.Equal(AuraRules.LaughPenalty, m.TotalB);
        // the performer's verdict holds the bonus
        m.Next(A, 0, 1, 10.0);
        m.Next(A, 0, 2, 12.0);
        m.Tick(12.01);
        Assert.Equal(AuraPhase.Verdict, m.Phase);
        Assert.Equal(AuraRules.LaughBonus, m.LastTurn!.Score.Composure);
    }

    [Fact]
    public void Pressing_with_nothing_to_hold_in_breaks_character()
    {
        var m = Performing();
        Assert.Equal(false, m.Compose(B, 0, 7.0, 7.0));
        Assert.True(m.Laughed);
        Assert.Null(m.Compose(B, 0, 7.5, 7.5));   // once a turn
        m.Next(A, 0, 0, 8.0);
        Assert.False(m.TemptOpened);              // nothing left to tempt
    }

    [Fact]
    public void The_match_keeps_the_music_beat()
    {
        var m = new AuraMatch(A, B, 0) { Beat = 0.4, Anchor = 0.3 };
        m.Tick(AuraMatch.IntroSeconds);
        Assert.True(m.SubmitPlan(A, 0, Opening, 10));
        Assert.Equal(13.1, m.T0, 6);
        m.Tick(m.T0);
        Assert.Equal(AuraGrade.Perfect, m.Next(A, 0, 0, 13.1 + 1.6));
    }
}
