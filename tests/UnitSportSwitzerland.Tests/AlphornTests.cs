using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Horn;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The alphorn and yodel game (#1269, src/Occasions/Festivals/Horn, linked in): the chart, the intervals, the grade, the harmony, the echo.</summary>
public class AlphornTests
{
    [Fact]
    public void The_same_seed_is_the_same_song()
    {
        var a = AlphornChart.Make(77);
        var b = AlphornChart.Make(77);
        Assert.Equal(a.Melody, b.Melody);
        Assert.Equal(a.Bpm, b.Bpm);
        foreach (var part in Enum.GetValues<HornPart>()) Assert.Equal(a.Of(part), b.Of(part));
        Assert.NotEqual(AlphornChart.Make(78).Melody, a.Melody);
    }

    [Fact]
    public void A_song_has_sixteen_to_thirty_two_notes_at_sixty_to_seventy_six_beats_a_minute()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var c = AlphornChart.Make(seed);
            Assert.InRange(c.Melody.Length, 16, 32);
            Assert.InRange(c.Bpm, 60, 76);
            Assert.Equal(AlphornChart.Home, c.Melody[^1].Partial);
            Assert.True(c.Melody[0].Start >= AlphornChart.LeadBeats);
            for (int i = 1; i < c.Melody.Length; i++) Assert.True(c.Melody[i].Start >= c.Melody[i - 1].End, $"seed {seed} note {i} overlaps");
        }
    }

    [Fact]
    public void Every_voice_stays_on_the_horns_natural_tones_and_in_the_song()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var c = AlphornChart.Make(seed);
            foreach (var part in Enum.GetValues<HornPart>())
            {
                Assert.NotEmpty(c.Of(part));
                foreach (var n in c.Of(part))
                {
                    Assert.True(AlphornRules.IsPartial(n.Partial), $"{part} {n.Partial}");
                    Assert.True(n.Start >= AlphornChart.LeadBeats && n.End <= c.Beats);
                }
            }
            for (int i = 0; i < c.Melody.Length; i++) Assert.True(c.Of(HornPart.Third)[i].Partial < c.Melody[i].Partial);
            Assert.All(c.Of(HornPart.Drone), n => Assert.InRange(n.Partial, 2, 4));
        }
    }

    [Theory]
    [InlineData(8, 8, 3)]    // unison
    [InlineData(4, 8, 3)]    // octave
    [InlineData(6, 4, 2)]    // fifth 3:2
    [InlineData(8, 6, 2)]    // fourth 4:3
    [InlineData(10, 8, 2)]   // major third 5:4
    [InlineData(12, 10, 1)]  // minor third 6:5
    [InlineData(11, 4, 1)]   // the Alphorn-Fa against the drone: 11:8
    [InlineData(9, 8, -1)]   // a second
    [InlineData(5, 3, 1)]
    public void Intervals_are_scored_by_their_folded_ratio(int a, int b, int score)
    {
        Assert.Equal(score, AlphornRules.Interval(a, b));
        Assert.Equal(score, AlphornRules.Interval(b, a));
    }

    private static HornPlay[] Perfect(AlphornChart c, HornPart part) =>
        c.Of(part).Select(n => new HornPlay(n.Partial, n.Start * c.SecondsPerBeat, n.End * c.SecondsPerBeat)).ToArray();

    [Fact]
    public void A_perfect_song_scores_everything_and_nobody_runs_out_of_breath()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var c = AlphornChart.Make(seed);
            foreach (var part in Enum.GetValues<HornPart>())
            {
                var s = AlphornRules.Grade(c, part, Perfect(c, part));
                Assert.True(s.Points == s.Max && s.Winded == 0, $"seed {seed} {part}: {s.Points}/{s.Max}, winded {s.Winded}");
            }
        }
    }

    [Fact]
    public void The_wrong_partial_or_a_late_press_scores_nothing()
    {
        var c = AlphornChart.Make(5);
        var wrong = Perfect(c, HornPart.Melody).Select(p => p with { Partial = p.Partial == 12 ? 11 : p.Partial + 1 }).ToArray();
        Assert.Equal(0, AlphornRules.Grade(c, HornPart.Melody, wrong).Points);
        var late = Perfect(c, HornPart.Melody).Select(p => p with { On = p.On + 0.4, Off = p.Off + 0.4 }).ToArray();
        Assert.Equal(0, AlphornRules.Grade(c, HornPart.Melody, late).Points);
        Assert.Equal(0, AlphornRules.Grade(c, HornPart.Melody, []).Points);
    }

    [Fact]
    public void A_good_press_is_worth_less_than_a_perfect_one()
    {
        var c = AlphornChart.Make(5);
        var n = c.Melody[0];
        double at = n.Start * c.SecondsPerBeat;
        int perfect = AlphornRules.Grade(c, HornPart.Melody, [new HornPlay(n.Partial, at, n.End * c.SecondsPerBeat)]).Points;
        int good = AlphornRules.Grade(c, HornPart.Melody, [new HornPlay(n.Partial, at + 0.1, n.End * c.SecondsPerBeat + 0.1)]).Points;
        Assert.Equal(AlphornRules.NoteMax, perfect);
        Assert.InRange(good, 1, perfect - 1);
    }

    [Fact]
    public void Holding_longer_than_the_breath_cuts_the_note()
    {
        var c = AlphornChart.Make(5);
        var n = c.Melody[0];
        double on = n.Start * c.SecondsPerBeat;
        var s = AlphornRules.Grade(c, HornPart.Melody, [new HornPlay(n.Partial, on, on + 20 * c.SecondsPerBeat)]);
        Assert.Equal(1, s.Winded);
        Assert.Equal(on + AlphornRules.BreathBeats * c.SecondsPerBeat, s.Sounded[0].Off, 6);
    }

    [Fact]
    public void A_blow_counts_for_one_note_only()
    {
        var c = AlphornChart.Make(5);
        var n = c.Melody[0];
        double on = n.Start * c.SecondsPerBeat;
        var once = AlphornRules.Grade(c, HornPart.Melody, [new HornPlay(n.Partial, on, on + 0.3)]);
        var twice = AlphornRules.Grade(c, HornPart.Melody, [new HornPlay(n.Partial, on, on + 0.3), new HornPlay(n.Partial, on + 0.01, on + 0.3)]);
        Assert.Equal(once.Points, twice.Points);
    }

    [Fact]
    public void Voices_that_fit_together_earn_harmony_and_a_lone_voice_none()
    {
        var c = AlphornChart.Make(11);
        var melody = Perfect(c, HornPart.Melody);
        Assert.Equal(1, AlphornRules.Harmony(c, [melody]));
        double duet = AlphornRules.Harmony(c, [melody, Perfect(c, HornPart.Third)]);
        double trio = AlphornRules.Harmony(c, [melody, Perfect(c, HornPart.Third), Perfect(c, HornPart.Drone)]);
        Assert.InRange(duet, 1.3, AlphornRules.MaxHarmony);
        Assert.InRange(trio, 1.2, AlphornRules.MaxHarmony);
    }

    [Fact]
    public void A_clash_earns_nothing_and_the_same_partial_earns_the_most()
    {
        var c = AlphornChart.Make(11);
        var melody = Perfect(c, HornPart.Melody);
        // a voice a second above every note: a clash all through
        var clash = melody.Select(p => p with { Partial = p.Partial + 1 }).ToArray();
        Assert.Equal(1, AlphornRules.Harmony(c, [melody, clash]), 3);
        Assert.Equal(AlphornRules.MaxHarmony, AlphornRules.Harmony(c, [melody, melody]), 3);
    }

    [Fact]
    public void A_bot_plays_a_graded_part_better_the_more_skilled_it_is()
    {
        var c = AlphornChart.Make(21);
        int Score(double skill) => AlphornRules.Grade(c, HornPart.Melody, AlphornRules.BotPlays(c, HornPart.Melody, skill, new Random(3))).Points;
        Assert.True(Score(1) > Score(0.2));
        Assert.True(AlphornRules.Grade(c, HornPart.Melody, AlphornRules.BotPlays(c, HornPart.Melody, 1, new Random(3))).Accuracy > 0.8);
    }

    [Fact]
    public void Pay_follows_accuracy_and_harmony()
    {
        var c = AlphornChart.Make(5);
        var full = AlphornRules.Grade(c, HornPart.Melody, Perfect(c, HornPart.Melody));
        var none = AlphornRules.Grade(c, HornPart.Melody, []);
        Assert.Equal(AlphornRules.BatzenPerSong, AlphornRules.Batzen(full, 1));
        Assert.Equal(32, AlphornRules.Batzen(full, AlphornRules.MaxHarmony));
        Assert.Equal(0, AlphornRules.Batzen(none, 1));
        Assert.Equal((int)Math.Round(full.Points * 1.5), AlphornRules.BoardPoints(full, 1.5));
    }

    // ---- the echo ------------------------------------------------------------------------------

    private static double? ValleyEast(double e, double n) => e < 1000 ? 0 : 300;

    [Fact]
    public void A_valley_a_kilometre_across_answers_in_about_six_seconds()
    {
        var echo = EchoFinder.Find(ValleyEast, 0, 0, Math.PI / 2, 0, 0);
        Assert.NotNull(echo);
        Assert.InRange(echo!.Value.DistanceM, 975, 1050);
        Assert.InRange(echo.Value.DelaySeconds, 5.6, 6.2);
        Assert.InRange(echo.Value.Gain, 0.01, 0.1);
    }

    [Fact]
    public void Nothing_to_answer_on_a_plateau_or_with_the_bell_turned_away()
    {
        Assert.Null(EchoFinder.Find((_, _) => 0.0, 0, 0, 0, 0, 0));
        Assert.Null(EchoFinder.Find(ValleyEast, 0, 0, -Math.PI / 2, 0, 0));   // facing west: the wall is behind
        Assert.Null(EchoFinder.Find((e, _) => e < 5000 ? 0 : 300, 0, 0, Math.PI / 2, 0, 0));   // beyond 4 km
        Assert.Null(EchoFinder.Find((_, _) => null, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void The_echo_gets_quieter_by_six_decibels_a_doubling_of_the_path()
    {
        var near = EchoFinder.Find((e, _) => e < 500 ? 0 : 300, 0, 0, Math.PI / 2, 0, 0)!.Value;
        var far = EchoFinder.Find((e, _) => e < 1000 ? 0 : 300, 0, 0, Math.PI / 2, 0, 0)!.Value;
        Assert.Equal(2, near.Gain / far.Gain, 1);
        Assert.True(far.LowPassHz < near.LowPassHz);
    }

    // ---- the wire and the festival ---------------------------------------------------------------

    [Fact]
    public void Blows_survive_the_wire_and_a_bad_string_is_refused_whole()
    {
        var c = AlphornChart.Make(9);
        var plays = Perfect(c, HornPart.Melody);
        var back = HornWire.Decode(HornWire.Encode(plays))!;
        Assert.Equal(plays.Length, back.Length);
        Assert.All(plays.Zip(back), z => { Assert.Equal(z.First.Partial, z.Second.Partial); Assert.Equal(z.First.On, z.Second.On, 3); Assert.Equal(z.First.Off, z.Second.Off, 3); });
        Assert.Empty(HornWire.Decode("")!);
        Assert.Null(HornWire.Decode("8,100"));          // a field short
        Assert.Null(HornWire.Decode("13,100,200"));     // not a natural tone
        Assert.Null(HornWire.Decode("8,200,100"));      // ends before it starts
        Assert.Null(HornWire.Decode("8,-5,100"));
        Assert.Null(HornWire.Decode("8,1,2;x"));
        Assert.Null(HornWire.Decode(string.Join(";", Enumerable.Repeat("8,1,2", HornWire.MaxPlays + 1))));
        Assert.Null(HornWire.Decode(new string('1', HornWire.MaxChars + 1)));
    }

    [Fact]
    public void The_song_of_a_slot_is_the_same_for_everyone_and_changes_with_the_slot_and_the_year()
    {
        Assert.Equal(HornWire.SeedOf("aelplerfest-2027", 5), HornWire.SeedOf("aelplerfest-2027", 5));
        Assert.NotEqual(HornWire.SeedOf("aelplerfest-2027", 5), HornWire.SeedOf("aelplerfest-2027", 6));
        Assert.NotEqual(HornWire.SeedOf("aelplerfest-2027", 5), HornWire.SeedOf("aelplerfest-2028", 5));
        Assert.All(Enumerable.Range(0, 50), s => Assert.True(HornWire.SeedOf("x", s) >= 0));
    }

    [Fact]
    public void The_alplerfest_is_a_sound_festival_with_the_alphorn_open_from_the_first_day()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.AelplerfestId)!;
        Assert.NotNull(def);
        Assert.Empty(def.Problems());
        Assert.True(def.IsOpen(AlphornRules.GameId, 1));
        Assert.Equal(11, def.Length);
        Assert.Equal(AlphornRules.DayCap, def.Game(AlphornRules.GameId)!.DayCap);
        // a perfect song at full harmony pays 32: the day's cap of 80 bites on the third
        int best = AlphornRules.Batzen(new HornScore(100, 100, 1, 1, 1, 0, []), AlphornRules.MaxHarmony);
        Assert.True(best * 2 < AlphornRules.DayCap && best * 3 > AlphornRules.DayCap);
    }
}
