using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Fahnen;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Fahnenschwingen's rules (#1269 part 5): the gesture reader, the routine plan, the windows, the score, the sync bonus, the wire, the bot.</summary>
public class FahnenRulesTests
{
    // ---- the gesture reader ----------------------------------------------------------------------

    [Theory]
    [InlineData(Stroke.Up)]
    [InlineData(Stroke.Down)]
    [InlineData(Stroke.Left)]
    [InlineData(Stroke.Right)]
    [InlineData(Stroke.CircleCw)]
    [InlineData(Stroke.CircleCcw)]
    [InlineData(Stroke.Eight)]
    public void A_clean_path_reads_back_as_its_stroke(Stroke s) => Assert.Equal(s, GestureRing.Recognise(GestureRing.Ideal(s)));

    [Fact]
    public void Sectors_run_clockwise_from_up()
    {
        Assert.Equal(0, GestureRing.SectorOf(0, -1));
        Assert.Equal(2, GestureRing.SectorOf(1, 0));
        Assert.Equal(4, GestureRing.SectorOf(0, 1));
        Assert.Equal(6, GestureRing.SectorOf(-1, 0));
        Assert.Equal(1, GestureRing.SectorOf(1, -1));
    }

    [Fact]
    public void A_wobble_does_not_turn_a_loop_into_something_else()
    {
        byte[] ring = [0, 1, 2, 1, 2, 3, 4, 5, 6, 7, 0, 1];
        Assert.Equal(Stroke.CircleCw, GestureRing.Recognise(ring));
    }

    [Fact]
    public void Too_short_scribbles_and_garbage_are_nothing()
    {
        Assert.Equal(Stroke.None, GestureRing.Recognise([0, 0]));
        Assert.Equal(Stroke.None, GestureRing.Recognise([0, 9, 0, 0, 0]));
        Assert.Equal(Stroke.None, GestureRing.Recognise([0, 4, 0, 4, 0, 4, 0, 4]));
        Assert.Equal(Stroke.None, GestureRing.Recognise(new byte[GestureRing.MaxSamples + 1]));
    }

    // ---- the routine -----------------------------------------------------------------------------

    [Fact]
    public void A_routine_has_eight_to_twelve_figures_starts_with_a_raise_and_throws_once_or_twice()
    {
        for (int seed = 1; seed < 400; seed++)
        {
            var r = FahnenRules.Plan(seed);
            Assert.InRange(r.Figures.Length, 8, 12);
            Assert.Equal(FahnenFigure.Ufzug, r.Figures[0].Kind);
            Assert.Equal(FahnenRules.CountInBeats, r.Figures[0].StartBeat);
            int throws = r.Figures.Count(f => f.Kind == FahnenFigure.Hochwurf);
            Assert.InRange(throws, 1, 2);
            for (int i = 1; i < r.Figures.Length; i++)
            {
                Assert.Equal(r.Figures[i - 1].StartBeat + r.Figures[i - 1].Beats, r.Figures[i].StartBeat);
                Assert.NotEqual(r.Figures[i - 1].Kind, r.Figures[i].Kind);
            }
            Assert.True(FahnenRules.Seconds(r.TotalBeats) < 60);
        }
    }

    [Fact]
    public void The_same_seed_is_the_same_routine_and_seeds_differ()
    {
        Assert.Equal(FahnenRules.Plan(77).Figures, FahnenRules.Plan(77).Figures);
        Assert.True(Enumerable.Range(1, 30).Select(s => string.Join(",", FahnenRules.Plan(s).Figures.Select(f => f.Kind))).Distinct().Count() > 10);
    }

    [Fact]
    public void Players_who_ask_together_share_a_slot_and_a_seed()
    {
        double now = 1234.5;
        long slot = FahnenRules.SlotOf(now);
        Assert.True(FahnenRules.SlotStart(slot) >= now + FahnenRules.LeadSeconds);
        Assert.True(FahnenRules.SlotStart(slot) < now + FahnenRules.LeadSeconds + FahnenRules.SlotBeats * FahnenRules.SecondsPerBeat + 1e-6);
        Assert.Equal(slot, FahnenRules.SlotOf(now + 0.2));
        Assert.Equal(FahnenRules.SeedOf("aelplerfest-2027", slot), FahnenRules.SeedOf("aelplerfest-2027", slot));
        Assert.NotEqual(FahnenRules.SeedOf("aelplerfest-2027", slot), FahnenRules.SeedOf("aelplerfest-2027", slot + 1));
    }

    // ---- the windows and the score ---------------------------------------------------------------

    private static FigureInput[] Swing(FahnenRoutine r, int jitterMs = 0, int held = 2)
    {
        var list = new List<FigureInput>();
        foreach (var f in r.Figures)
        {
            if (f.Kind == FahnenFigure.Hochwurf)
                list.Add(new(Ms(FahnenRules.Seconds(f.StartBeat)), Ms(FahnenRules.Seconds(f.StartBeat + held)) + jitterMs, Ms(FahnenRules.Seconds(f.StartBeat + held + held + 1)) + jitterMs, []));
            else
                list.Add(new(Ms(FahnenRules.Seconds(f.StartBeat + f.Beats)) + jitterMs, 0, 0, GestureRing.Ideal(FahnenRules.StrokeFor(f.Kind))));
        }
        return [.. list];
    }

    private static int Ms(double s) => (int)Math.Round(s * 1000);

    [Fact]
    public void A_perfect_routine_scores_a_thousand_and_a_good_one_less()
    {
        var r = FahnenRules.Plan(5);
        var perfect = FahnenRules.Grade(r, Swing(r, 0, 3));
        Assert.Equal(FahnenRules.MaxScore, perfect.Score);
        Assert.All(perfect.Grades, g => Assert.Equal(FahnenGrade.Perfect, g));
        var good = FahnenRules.Grade(r, Swing(r, 200, 3));
        Assert.All(good.Grades, g => Assert.Equal(FahnenGrade.Good, g));
        Assert.InRange(good.Score, 600, 750);
        var late = FahnenRules.Grade(r, Swing(r, 350, 3));
        Assert.All(late.Grades, g => Assert.Equal(FahnenGrade.Off, g));
        Assert.True(late.Score < good.Score);
        Assert.Equal(FahnenRules.MaxScore / FahnenRules.BatzenDivisor, FahnenRules.BatzenFor(perfect.Score));
    }

    [Fact]
    public void A_higher_throw_is_worth_more()
    {
        var r = FahnenRules.Plan(5);
        Assert.True(FahnenRules.Grade(r, Swing(r, 0, 3)).Score > FahnenRules.Grade(r, Swing(r, 0, 1)).Score);
        Assert.Equal(1.0, FahnenRules.FlightFactor(2));
        Assert.Equal(1.8, FahnenRules.FlightFactor(4), 6);
    }

    [Fact]
    public void A_wrong_gesture_is_a_miss_but_the_routine_goes_on()
    {
        var r = FahnenRules.Plan(5);
        var inputs = Swing(r);
        int g = Array.FindIndex(r.Figures, 1, f => f.Kind != FahnenFigure.Hochwurf && f.Kind != FahnenFigure.Ufzug);
        inputs[g] = inputs[g] with { Path = GestureRing.Ideal(Stroke.Up) };
        var o = FahnenRules.Grade(r, inputs);
        Assert.Equal(FahnenGrade.Miss, o.Grades[g]);
        Assert.False(o.Dropped);
        Assert.Equal(FahnenGrade.Perfect, o.Grades[^1]);
    }

    [Fact]
    public void A_dropped_flag_ends_the_routine()
    {
        var r = FahnenRules.Plan(5);
        var inputs = Swing(r, 0, 2);
        int t = Array.FindIndex(r.Figures, f => f.Kind == FahnenFigure.Hochwurf);
        inputs[t] = inputs[t] with { C = inputs[t].C + 900 };   // the catch comes nearly two beats late
        var o = FahnenRules.Grade(r, inputs);
        Assert.True(o.Dropped);
        Assert.Equal(FahnenGrade.Miss, o.Grades[t]);
        Assert.All(o.Grades.Skip(t), g => Assert.Equal(FahnenGrade.Miss, g));
        Assert.True(o.Score < FahnenRules.MaxScore * t / r.Figures.Length + 1);
    }

    [Fact]
    public void A_release_off_the_three_beats_or_a_late_start_is_a_miss()
    {
        var slot = new FigureSlot(FahnenFigure.Hochwurf, 20, 8);
        double s = FahnenRules.Seconds(20);
        Assert.Equal(FahnenGrade.Miss, FahnenRules.GradeThrow(slot, new(Ms(s), Ms(s + 0.1), Ms(s + 2), [])).Grade);   // released before beat 1
        Assert.Equal(FahnenGrade.Miss, FahnenRules.GradeThrow(slot, new(Ms(s) + 2000, Ms(s + 1.25), Ms(s + 3.1), [])).Grade);   // held from far too late
        Assert.Equal(FahnenGrade.Miss, FahnenRules.GradeThrow(slot, new(Ms(s), Ms(s + 4 * FahnenRules.SecondsPerBeat), Ms(s + 8), [])).Grade);   // held four beats
        Assert.Equal(4, FahnenRules.GradeThrow(slot, new(Ms(s), Ms(FahnenRules.Seconds(23)), Ms(FahnenRules.Seconds(27)), [])).Flight);
    }

    // ---- swinging in step ------------------------------------------------------------------------

    [Fact]
    public void Two_players_who_land_the_same_figures_swing_in_step_and_earn_the_bonus()
    {
        var r = FahnenRules.Plan(9);
        var a = FahnenRules.Grade(r, Swing(r)).Grades;
        var b = FahnenRules.Grade(r, Swing(r, 200)).Grades;
        var bad = Enumerable.Repeat(FahnenGrade.Miss, a.Length).ToArray();
        var synced = FahnenRules.SyncedOf([a, b, bad]);
        Assert.Equal([true, true, false], synced);
        Assert.Equal(1300, FahnenRules.WithSync(1000, true));
        Assert.Equal(700, FahnenRules.WithSync(700, false));
        Assert.Equal([false], FahnenRules.SyncedOf([a]));
    }

    // ---- the wire --------------------------------------------------------------------------------

    [Fact]
    public void A_submission_survives_the_wire_and_garbage_is_refused()
    {
        var r = FahnenRules.Plan(5);
        var inputs = Swing(r);
        var (stamps, path) = FahnenRules.ToWire(inputs);
        var back = FahnenRules.FromWire(stamps, path)!;
        Assert.Equal(inputs.Length, back.Length);
        for (int i = 0; i < inputs.Length; i++)
        {
            Assert.Equal((inputs[i].A, inputs[i].B, inputs[i].C), (back[i].A, back[i].B, back[i].C));
            Assert.Equal(inputs[i].Path, back[i].Path);
        }
        Assert.Null(FahnenRules.FromWire([1, 2, 3], []));
        Assert.Null(FahnenRules.FromWire([1, 2, 3, 5], [0, 0]));
        Assert.Null(FahnenRules.FromWire([1, 2, 3, -1], []));
        Assert.Null(FahnenRules.FromWire([1, 2, 3, 0], [7]));
        Assert.Null(FahnenRules.FromWire(new int[(FahnenRules.MaxFigures + 1) * FahnenRules.IntsPerFigure], []));
    }

    // ---- the bot ---------------------------------------------------------------------------------

    [Fact]
    public void A_skilled_bot_scores_well_and_a_clumsy_one_worse()
    {
        long skilled = 0, clumsy = 0;
        for (int seed = 1; seed <= 60; seed++)
        {
            var r = FahnenRules.Plan(seed);
            skilled += FahnenRules.Grade(r, FahnenRules.BotRoutine(r, 1.0, seed)).Score;
            clumsy += FahnenRules.Grade(r, FahnenRules.BotRoutine(r, 0.1, seed)).Score;
        }
        Assert.True(skilled / 60 > 800, $"skilled {skilled / 60}");
        Assert.True(clumsy < skilled * 0.8, $"clumsy {clumsy / 60} vs skilled {skilled / 60}");
    }

    // ---- the festival ----------------------------------------------------------------------------

    [Fact]
    public void The_alplerfest_is_sound_and_the_flags_open_on_day_three()
    {
        var def = FestivalCatalog.Aelplerfest();
        Assert.Empty(def.Problems());
        Assert.Equal(11, def.Length);
        Assert.False(def.IsOpen(FestivalCatalog.FahneGame, 2));
        Assert.True(def.IsOpen(FestivalCatalog.FahneGame, 3));
        Assert.NotNull(FestivalCatalog.Get(FestivalCatalog.AelplerfestId));
    }

    [Fact]
    public void A_perfect_routine_pays_about_forty_Batzen_and_the_day_cap_is_a_few_of_them()
    {
        var def = FestivalCatalog.Aelplerfest();
        int perfect = FahnenRules.BatzenFor(FahnenRules.MaxScore);
        int syncedPerfect = FahnenRules.BatzenFor(FahnenRules.WithSync(FahnenRules.MaxScore, true));
        Assert.InRange(perfect, 30, 50);
        Assert.True(syncedPerfect > perfect);
        Assert.True(def.Game(FestivalCatalog.FahneGame)!.DayCap / perfect <= 4);
    }
}
