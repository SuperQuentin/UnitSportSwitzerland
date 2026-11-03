using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Fasnacht;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Fasnacht (#1357): both festivals' dates, the stall's masks, the Morgestraich's window, the Guggenmusik's chart, grading, sync and din. Pure.</summary>
public class FasnachtTests
{
    private static ResolvedSite Site() => new("the test field", 2600000, 1200000, 90, false, null, "Fasnacht at the test field");

    [Fact]
    public void Both_festivals_are_in_the_catalogue_and_sound()
    {
        foreach (string id in new[] { FestivalCatalog.FasnachtId, FestivalCatalog.BaslerFasnachtId })
        {
            var def = FestivalCatalog.Get(id);
            Assert.NotNull(def);
            Assert.Empty(def!.Problems());
            foreach (string g in new[] { FasnachtRules.LarvenGame, FasnachtRules.GuggenGame, FasnachtRules.RaeppliGame }) Assert.True(def.IsOpen(g, 1));
        }
        Assert.Equal(6, FestivalCatalog.Get(FestivalCatalog.FasnachtId)!.Length);
        Assert.Equal(3, FestivalCatalog.Get(FestivalCatalog.BaslerFasnachtId)!.Length);
        Assert.Equal(300, FestivalCatalog.Get(FestivalCatalog.BaslerFasnachtId)!.TokenCap);
        Assert.True(FestivalCatalog.Get(FestivalCatalog.BaslerFasnachtId)!.IsOpen(FasnachtRules.MorgestraichGame, 1));
        Assert.Null(FestivalCatalog.Get(FestivalCatalog.FasnachtId)!.Game(FasnachtRules.MorgestraichGame));
    }

    [Theory]
    [InlineData(FasnachtRules.FestivalId, 2026, 2, 12, 2, 17, DayOfWeek.Thursday)]
    [InlineData(FasnachtRules.FestivalId, 2027, 2, 4, 2, 9, DayOfWeek.Thursday)]
    [InlineData(FasnachtRules.FestivalId, 2028, 2, 24, 2, 29, DayOfWeek.Thursday)]
    [InlineData(FasnachtRules.BaselId, 2026, 2, 23, 2, 25, DayOfWeek.Monday)]
    [InlineData(FasnachtRules.BaselId, 2027, 2, 15, 2, 17, DayOfWeek.Monday)]
    [InlineData(FasnachtRules.BaselId, 2028, 3, 6, 3, 8, DayOfWeek.Monday)]
    public void They_open_on_the_real_dates(string id, int year, int month, int day, int endMonth, int endDay, DayOfWeek weekday)
    {
        var (from, to) = FestivalCatalog.Get(id)!.Schedule[0];
        var span = FestivalCalendar.SpanOf(from, to, year);
        Assert.NotNull(span);
        Assert.Equal(new DateOnly(year, month, day), span!.Value.Start);
        Assert.Equal(weekday, span.Value.Start.DayOfWeek);
        Assert.Equal(new DateOnly(year, endMonth, endDay), span.Value.End);
    }

    [Fact]
    public void The_ground_is_a_function_of_the_site()
    {
        var site = Site();
        Assert.Equal((site.E - 9, site.N), FasnachtRules.StallAt(site));
        Assert.True(FasnachtRules.NearStall(site, site.E - 9, site.N + 4));
        Assert.False(FasnachtRules.NearStall(site, site.E - 9, site.N + 8));
        Assert.True(FasnachtRules.NearStall(site, site.E - 9, site.N + 8, FasnachtRules.ServerSlack));
        Assert.True(FasnachtRules.NearStage(site, site.E + 14, site.N + 6));
        Assert.True(FasnachtRules.NearLantern(site, site.E, site.N + 11));
        Assert.False(FasnachtRules.NearLantern(site, site.E, site.N));
    }

    [Fact]
    public void The_stall_hands_out_one_of_four_masks_by_player_and_day()
    {
        var seen = new HashSet<int>();
        for (int k = 0; k < 200; k++)
        {
            int m = FasnachtRules.MaskFor($"player-{k}", 1 + k % 6);
            Assert.InRange(m, 0, FasnachtRules.MaskKinds - 1);
            Assert.Equal(m, FasnachtRules.MaskFor($"player-{k}", 1 + k % 6));
            seen.Add(m);
        }
        Assert.Equal(FasnachtRules.MaskKinds, seen.Count);
        Assert.Equal(FasnachtRules.MaskKinds, (int)ItemId.LarveTeufel - (int)ItemId.LarveWaggis + 1);
    }

    [Fact]
    public void The_items_are_in_the_fasnachts_block()
    {
        foreach (var id in new[] { ItemId.Raeppli, ItemId.LarveWaggis, ItemId.LarveDante, ItemId.LarveHarlekin, ItemId.LarveTeufel })
            Assert.InRange((int)id, 505, 509);
        Assert.Equal(5, new[] { ItemId.Raeppli, ItemId.LarveWaggis, ItemId.LarveDante, ItemId.LarveHarlekin, ItemId.LarveTeufel }.Distinct().Count());
        Assert.InRange(FasnachtRules.FirstHeadwear, 5, 10);
        Assert.InRange(FasnachtRules.LastHeadwear, 5, 10);
    }

    [Theory]
    [InlineData(FasnachtRules.BaselId, 1, 3.99, false)]
    [InlineData(FasnachtRules.BaselId, 1, 4.0, true)]
    [InlineData(FasnachtRules.BaselId, 1, 5.49, true)]
    [InlineData(FasnachtRules.BaselId, 1, 5.5, false)]
    [InlineData(FasnachtRules.BaselId, 2, 4.5, false)]
    [InlineData(FasnachtRules.FestivalId, 1, 4.5, false)]
    public void The_morgestraich_is_baslers_first_day_at_four(string festival, int day, double hour, bool on)
    {
        Assert.Equal(on, FasnachtRules.Morgestraich(festival, day, hour));
        Assert.Equal(on ? 0f : 1f, FasnachtRules.Lamps(festival, day, hour));
    }

    // ---- the Guggenmusik ----------------------------------------------------------------------------------------

    [Fact]
    public void A_song_is_a_function_of_its_seed()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var a = GuggenChart.Make(seed);
            var b = GuggenChart.Make(seed);
            Assert.Equal(a.Bpm, b.Bpm);
            Assert.InRange(a.Bpm, GuggenChart.MinBpm, GuggenChart.MaxBpm);
            foreach (GuggenPart part in Enum.GetValues<GuggenPart>())
            {
                Assert.Equal(a.Of(part), b.Of(part));
                var notes = a.Of(part);
                Assert.True(notes.Length >= GuggenChart.Bars * 2);
                for (int i = 1; i < notes.Length; i++) Assert.True(notes[i].Beat > notes[i - 1].Beat, $"{part} out of order at seed {seed}");
                Assert.All(notes, n => Assert.InRange(n.Beat, GuggenChart.LeadBeats, GuggenChart.LeadBeats + GuggenChart.Bars * GuggenChart.BeatsPerBar));
            }
        }
        Assert.NotEqual(GuggenChart.Make(1).Of(GuggenPart.Trumpet), GuggenChart.Make(2).Of(GuggenPart.Trumpet));
        Assert.Equal((GuggenChart.LeadBeats + 32) * 60.0 / GuggenChart.Make(7).Bpm, GuggenChart.Make(7).Seconds, 6);
    }

    [Fact]
    public void The_three_parts_are_a_drum_a_stab_and_an_oompah()
    {
        var c = GuggenChart.Make(42);
        Assert.Equal(GuggenChart.Bars * 5, c.Of(GuggenPart.Drum).Length);                 // four beats and one off-beat a bar
        Assert.Equal(GuggenChart.Bars * 2, c.Of(GuggenPart.Sousaphone).Length);           // beats 1 and 3
        Assert.InRange(c.Of(GuggenPart.Trumpet).Length, GuggenChart.Bars * 2, GuggenChart.Bars * 4);
        Assert.All(c.Of(GuggenPart.Sousaphone), n => Assert.True(n.Beat % 2 == 0, "the sousaphone plays on the odd beats of the bar"));
        Assert.All(c.Of(GuggenPart.Trumpet), n => Assert.Equal(0.5, n.Beat % 1, 6));
    }

    private static double[] Perfect(GuggenChart c, GuggenPart p) => c.Of(p).Select(c.TimeOf).ToArray();

    [Fact]
    public void A_perfect_part_scores_everything_on_300_seeds()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var c = GuggenChart.Make(seed);
            foreach (GuggenPart part in Enum.GetValues<GuggenPart>())
            {
                var s = GuggenRules.Grade(c, part, Perfect(c, part));
                Assert.Equal(s.Max, s.Points);
                Assert.Equal(s.Notes, s.Hit);
                Assert.Equal(0, s.Extra);
            }
        }
    }

    [Fact]
    public void Late_or_early_scores_less_and_off_the_beat_scores_nothing()
    {
        var c = GuggenChart.Make(5);
        var good = GuggenRules.Grade(c, GuggenPart.Drum, Perfect(c, GuggenPart.Drum).Select(t => t + 0.1).ToArray());   // inside the good window, outside the perfect
        Assert.Equal(good.Notes, good.Hit);
        Assert.Equal(good.Notes * GuggenRules.GoodPoints, good.Points);
        // a sousaphone's notes are two beats apart: a tap 0.3 s late hits none of them
        var late = GuggenRules.Grade(c, GuggenPart.Sousaphone, Perfect(c, GuggenPart.Sousaphone).Select(t => t + 0.3).ToArray());
        Assert.Equal(0, late.Hit);
        Assert.Equal(0, late.Points);
        Assert.Equal(late.Notes, late.Extra);
    }

    [Fact]
    public void Holding_the_key_down_scores_nothing()
    {
        var c = GuggenChart.Make(9);
        var spam = Enumerable.Range(0, 190).Select(i => i * 0.08).ToArray();
        var s = GuggenRules.Grade(c, GuggenPart.Drum, spam);
        Assert.True(s.Extra > 20);
        Assert.True(s.Accuracy < 0.4, $"spam scored {s.Accuracy:P0}");
        Assert.Equal(0, GuggenRules.Grade(c, GuggenPart.Drum, []).Points);
        Assert.Equal(0, GuggenRules.Batzen(GuggenRules.Grade(c, GuggenPart.Drum, spam.Take(30).ToArray()), 1.6));
    }

    [Fact]
    public void Voices_in_step_make_a_din()
    {
        var c = GuggenChart.Make(11);
        var all = Enum.GetValues<GuggenPart>().Select(p => GuggenRules.Grade(c, p, Perfect(c, p))).ToList();
        Assert.Equal(0, GuggenRules.Sync(c, all.Take(1).ToList()));
        Assert.Equal(1.0, GuggenRules.Sync(c, all.Take(2).ToList()), 6);
        Assert.Equal(1.0, GuggenRules.Sync(c, all), 6);
        Assert.Equal(1.0, GuggenRules.Din(1, 1));
        Assert.Equal(1.3, GuggenRules.Din(1, 2), 6);
        Assert.Equal(GuggenRules.MaxDin, GuggenRules.Din(1, 3), 6);
        Assert.Equal(1.0, GuggenRules.Din(0, 3), 6);
        // a drummer who misses every second note breaks the beats the others share
        var drum = Perfect(c, GuggenPart.Drum).Where((_, i) => i % 2 == 0).ToArray();
        var ragged = new List<GuggenScore> { GuggenRules.Grade(c, GuggenPart.Drum, drum), all[1], all[2] };
        double sync = GuggenRules.Sync(c, ragged);
        Assert.InRange(sync, 0.05, 0.9);
        Assert.True(GuggenRules.Din(sync, 3) < GuggenRules.MaxDin);
    }

    [Fact]
    public void Pay_is_accuracy_times_din_capped_and_nothing_under_a_quarter()
    {
        var c = GuggenChart.Make(13);
        var full = GuggenRules.Grade(c, GuggenPart.Drum, Perfect(c, GuggenPart.Drum));
        Assert.Equal(GuggenRules.BatzenPerSong, GuggenRules.Batzen(full, 1.0));
        Assert.Equal(GuggenRules.BatzenMax, GuggenRules.Batzen(full, 1.6));
        Assert.Equal((int)Math.Round(full.Points * 1.3), GuggenRules.BoardPoints(full, 1.3));
        var weak = GuggenRules.Grade(c, GuggenPart.Drum, Perfect(c, GuggenPart.Drum).Take(c.Of(GuggenPart.Drum).Length / 5).ToArray());
        Assert.Equal(0, GuggenRules.Batzen(weak, 1.6));
        Assert.True(GuggenRules.DayCap >= 2 * GuggenRules.BatzenMax);
    }

    [Fact]
    public void A_good_bot_plays_the_whole_part_and_a_poor_one_loses_notes()
    {
        var c = GuggenChart.Make(17);
        var rng = new Random(1);
        foreach (GuggenPart part in Enum.GetValues<GuggenPart>())
        {
            var sharp = GuggenRules.Grade(c, part, GuggenRules.BotTaps(c, part, 1.0, rng));
            Assert.Equal(sharp.Notes, sharp.Hit);
            var poor = GuggenRules.Grade(c, part, GuggenRules.BotTaps(c, part, 0.1, rng));
            Assert.True(poor.Points < sharp.Points);
        }
    }

    [Fact]
    public void Taps_cross_the_wire_strictly()
    {
        var taps = new[] { 0.0, 1.234, 15.4 };
        Assert.Equal(taps, GuggenWire.Decode(GuggenWire.Encode(taps)));
        Assert.Empty(GuggenWire.Decode("")!);
        Assert.Null(GuggenWire.Decode("1,two,3"));
        Assert.Null(GuggenWire.Decode("-5"));
        Assert.Null(GuggenWire.Decode("700000"));
        Assert.Null(GuggenWire.Decode(string.Join(',', Enumerable.Repeat("1", GuggenWire.MaxTaps + 1))));
        Assert.Null(GuggenWire.Decode(new string('1', GuggenWire.MaxChars + 1)));
    }
}
