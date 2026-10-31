using UnitSport.Concert;
using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.MontreuxJazz;
using UnitSport.Terrain.Stadiums;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The jazz night, the set's clock, the rules and the festival def (#1394).</summary>
public class MontreuxJazzRulesTests
{
    private static readonly StadiumSpec Venue = StadiumCatalog.MontreuxJazzOpenAir;
    private static ConcertEvent Night(int n) => ConcertPlanner.Jazz(Venue, 2026, n);
    private static ResolvedSite Site => new("Quai de Montreux", Venue.E, Venue.N, 120, false, null, "");

    [Theory]
    [InlineData(2026, 7, 2, 0)]
    [InlineData(2026, 7, 3, 1)]
    [InlineData(2026, 7, 10, 8)]
    [InlineData(2026, 7, 18, 16)]
    [InlineData(2026, 7, 19, 0)]
    [InlineData(2027, 7, 2, 1)]
    [InlineData(2027, 7, 17, 16)]
    [InlineData(2026, 12, 25, 0)]
    public void The_nights_follow_the_real_dates(int y, int m, int d, int night) =>
        Assert.Equal(night, MontreuxJazzRules.NightOf(new DateOnly(y, m, d)));

    [Fact]
    public void The_festival_def_is_sound_and_under_its_cap()
    {
        var def = FestivalCatalog.MontreuxJazz();
        Assert.Empty(def.Problems());
        Assert.Equal(400, def.TokenCap);
        Assert.Equal(16, def.Length);
        Assert.False(def.Relocates);
        Assert.Equal(ClosingShow.Concert, def.Show);
        Assert.True(def.MilestoneTotal <= def.TokenCap / 4);
        Assert.True(def.IsOpen(MontreuxJazzRules.ListenGame, 1) && def.IsOpen(MontreuxJazzRules.EncoreGame, 1));
        Assert.NotNull(FestivalCatalog.Get(MontreuxJazzRules.FestivalId));
    }

    [Fact]
    public void A_region_without_Montreux_does_not_hold_it_and_no_town_stands_in()
    {
        var def = FestivalCatalog.MontreuxJazz();
        var towns = new[] { new TownRef("Bulle", 2570000, 1160000, 900) };
        Assert.Null(FestivalSites.Resolve(def, (_, _) => false, towns));
        Assert.Null(FestivalSites.Resolve(def, (e, n) => e == 2570000, towns));
        Assert.NotNull(FestivalSites.Resolve(def, (_, _) => true, towns));
        // another festival still moves to the town
        Assert.True(FestivalSites.Resolve(FestivalCatalog.ChristmasMarket(), (e, n) => e == 2570000, towns)!.Relocated);
    }

    [Fact]
    public void A_jazz_night_is_deterministic_small_and_calm()
    {
        for (int night = 1; night <= 16; night++)
        {
            var e = Night(night);
            Assert.Equal(e.ToString(), Night(night).ToString());
            Assert.Equal(e.Seed, Night(night).Seed);
            Assert.Equal(Genre.Jazz, e.Genre);
            Assert.Equal(StageLayout.EndNorth, e.Layout);
            Assert.Equal(ScreenSet.None, e.Screens);
            Assert.InRange(e.Bpm, 80f, 150f);
            Assert.True(e.Width <= 12.01f && e.Depth == MontreuxJazzRules.DeckDepth && e.RoofHeight == 4.5f);
            Assert.Equal(6 + 6 + 4, e.Spots + e.Washes + e.FloorBeams);
            Assert.Equal(0, e.Beams + e.Blinders + e.Strobes + e.PixelBars + e.Flames + e.Co2Jets + e.Gerbs + e.Airbursts);
            Assert.Equal(MontreuxJazzRules.SetSongs(night), e.SetSongs);
            Assert.Equal(night == 16, e.FinaleNight);
            Assert.Equal(night == 16 ? 2 : 0, e.ConfettiCannons);
        }
        Assert.NotEqual(Night(1).Seed, Night(2).Seed);
        Assert.NotEqual(ConcertPlanner.Jazz(Venue, 2026, 1).Seed, ConcertPlanner.Jazz(Venue, 2027, 1).Seed);
        Assert.Equal(2, Night(1).SetSongs);
        Assert.Equal(4, Night(8).SetSongs);
        Assert.Equal(6, Night(16).SetSongs);
    }

    [Fact]
    public void Jazz_is_never_drawn_by_the_random_planner()
    {
        foreach (var venue in StadiumCatalog.All)
            for (int seed = 0; seed < 300; seed++)
                Assert.NotEqual(Genre.Jazz, ConcertPlanner.Plan(venue, seed).Genre);
    }

    [Fact]
    public void Tonight_is_the_jazz_night_inside_the_season_and_an_ordinary_one_outside()
    {
        Assert.Equal(Genre.Jazz, ConcertPlanner.Tonight(Venue, new DateTime(2026, 7, 10)).Genre);
        Assert.Equal(4, ConcertPlanner.Tonight(Venue, new DateTime(2026, 7, 10)).SetSongs);
        Assert.True(ConcertPlanner.Open(Venue, new DateTime(2026, 7, 18)));
        Assert.False(ConcertPlanner.Open(Venue, new DateTime(2026, 7, 19)));
        Assert.False(ConcertPlanner.Open(Venue, new DateTime(2026, 10, 30)));
        // the other venues are open every night, and their nights did not move
        Assert.All(StadiumCatalog.All.Where(s => s != Venue), s => Assert.True(ConcertPlanner.Open(s, new DateTime(2026, 7, 10))));
        Assert.Equal(ConcertPlanner.Plan(StadiumCatalog.Geneva, ConcertPlanner.Seed(StadiumCatalog.Geneva.Name, new DateTime(2026, 7, 10))).ToString(),
            ConcertPlanner.Tonight(StadiumCatalog.Geneva, new DateTime(2026, 7, 10)).ToString());
    }

    [Fact]
    public void The_set_runs_songs_then_the_call_then_the_bis_then_a_rest()
    {
        const int songs = 2;
        Assert.Equal(2 * 25 + 3 + 8 + 2, JazzSet.SetBars(songs));
        Assert.Equal(JazzPart.Song, JazzSet.At(0, songs).Part);
        Assert.Equal(ShowSection.Calm, JazzSet.At(0, songs).Section);
        Assert.Equal(ShowSection.Groove, JazzSet.At(4 * 4, songs).Section);
        Assert.True(JazzSet.At((6 * 4 - 1) * 4, songs).LastSection);
        // the dark bar between two songs, then the second song's calm
        Assert.Equal(JazzPart.Between, JazzSet.At(24 * 4, songs).Part);
        Assert.Equal(ShowSection.Blackout, JazzSet.At(24 * 4, songs).Section);
        var second = JazzSet.At(25 * 4, songs);
        Assert.Equal((JazzPart.Song, 1, ShowSection.Calm), (second.Part, second.Song, second.Section));
        // the call right after the last song's end
        double call = JazzSet.CallStart(0, songs);
        Assert.Equal(50 * 4, call);
        Assert.Equal(JazzPart.Call, JazzSet.At(call, songs).Part);
        Assert.Equal(JazzPart.Call, JazzSet.At(call + 3 * 4 - 0.01, songs).Part);
        var bis = JazzSet.At(call + 3 * 4, songs);
        Assert.Equal((JazzPart.Bis, ShowSection.Chorus), (bis.Part, bis.Section));
        Assert.Equal(ShowSection.Peak, JazzSet.At(call + 3 * 4 + 4 * 4, songs).Section);
        Assert.Equal(JazzPart.Rest, JazzSet.At(call + 11 * 4, songs).Part);
        // and the next set begins where this one's last bar ends
        var next = JazzSet.At(JazzSet.SetBars(songs) * 4.0, songs);
        Assert.Equal((1L, JazzPart.Song), (next.Set, next.Part));
        Assert.Equal(24 * 4, JazzSet.SongEnd(0, 0, songs));
        Assert.Equal(JazzSet.SetBars(songs) * 4 + 24 * 4, JazzSet.SongEnd(1, 0, songs));
    }

    [Fact]
    public void The_clock_is_the_desks_beat_and_the_call_fits_the_encore_window()
    {
        var e = Night(3);
        // the same beat the light desk counts: show seconds x bpm / 60
        double t = 12345.678;
        Assert.Equal(JazzSet.At(t * e.Bpm / 60.0, e.SetSongs), MontreuxJazzRules.Moment(e, t));
        var later = MontreuxJazzRules.Moment(e, t + 86400.0 * 3);
        Assert.Equal((MontreuxJazzRules.Moment(e, t).Set, MontreuxJazzRules.Moment(e, t).Part), (later.Set, later.Part));
        Assert.Equal(24 * 4 * 60.0 / e.Bpm, MontreuxJazzRules.SongSeconds(e), 6);
        // the call is longer than its ring: two lead beats, eight beats, two beats of tail
        Assert.Equal(JazzSet.CallBars * 4, JazzSet.CallLeadBeats + JazzSet.CallBeats + 2);
    }

    [Fact]
    public void A_song_counts_at_eighty_percent_of_it()
    {
        Assert.True(MontreuxJazzRules.Counts(80, 100));
        Assert.True(MontreuxJazzRules.Counts(100, 100));
        Assert.False(MontreuxJazzRules.Counts(79.9, 100));
        Assert.False(MontreuxJazzRules.Counts(0, 100));
    }

    [Fact]
    public void The_pay_is_six_a_song_ten_for_the_bis_and_half_as_much_again_on_the_Jazz_Night()
    {
        Assert.Equal(6, MontreuxJazzRules.SongPay(1));
        Assert.Equal(10, MontreuxJazzRules.EncorePay(1));
        Assert.Equal(9, MontreuxJazzRules.SongPay(MontreuxJazzRules.JazzNight));
        Assert.Equal(15, MontreuxJazzRules.EncorePay(MontreuxJazzRules.JazzNight));
        Assert.Equal(6, MontreuxJazzRules.SongPay(MontreuxJazzRules.ClosingNight));
    }

    [Fact]
    public void The_zone_is_ahead_of_the_stage_and_the_rail_marks_a_listener()
    {
        var site = Site;
        var front = MontreuxJazzRules.StageFront(site);
        // the deck's front edge, on the venue's own axis (azimuth 270: the stage in the west by the lake, the audience east)
        Assert.Equal(Venue.E + (-Venue.FrontHalfV + 1.5 + 7), front.E, 3);
        Assert.Equal(Venue.N, front.N, 3);
        var rail = MontreuxJazzRules.Rail(site);
        Assert.True(MontreuxJazzRules.AtRail(site, rail.E, rail.N));
        Assert.False(MontreuxJazzRules.AtRail(site, rail.E + 10, rail.N));
        Assert.True(MontreuxJazzRules.InZone(site, front.E + 10, front.N));        // ten metres into the audience
        Assert.True(MontreuxJazzRules.InZone(site, front.E + 30, front.N + 8));
        Assert.False(MontreuxJazzRules.InZone(site, front.E + 40, front.N));        // 36 m and more: not counted by accident
        Assert.False(MontreuxJazzRules.InZone(site, front.E - 10, front.N));        // on the deck, behind the front
        Assert.False(MontreuxJazzRules.InZone(site, front.E + 10, front.N + 40));
        // the round trip of the frame
        var (u, v) = MontreuxJazzRules.ToFrame(site.E, site.N, 270f, front.E + 10, front.N);
        Assert.Equal(0, u, 3);
        Assert.Equal(MontreuxJazzRules.FrontV + 10, v, 3);
    }

    private static MontreuxJazzRules.Meter Ring(params (long Who, int[] Beats)[] claps)
    {
        var m = new MontreuxJazzRules.Meter();
        foreach (var (who, beats) in claps)
            foreach (int b in beats) m.Clap(who, JazzSet.CallLeadBeats + b, 0.5);
        return m;
    }

    [Fact]
    public void Alone_a_player_cannot_fill_the_meter_on_eight_beats()
    {
        var all = Ring((1, [0, 1, 2, 3, 4, 5, 6, 7]));
        Assert.Equal(8, all.Total);
        Assert.False(all.Reached);
        // a second pass on the same beats is a miss (twice on one beat)
        var again = Ring((1, [0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3]));
        Assert.Equal(8, again.Total);
    }

    [Fact]
    public void Two_friends_clapping_six_each_fill_the_meter_and_both_count()
    {
        var m = Ring((1, [0, 1, 2, 3, 4, 5]), (2, [2, 3, 4, 5, 6, 7]));
        Assert.Equal(12, m.Total);
        Assert.True(m.Reached);
        Assert.Equal(6, m.Own(1));
        Assert.Equal(6, m.Own(2));
        Assert.Equal("12 of 12", MontreuxJazzRules.MeterText(m.Total));
    }

    [Fact]
    public void A_strangers_claps_add_to_the_meter()
    {
        var m = Ring((1, [0, 1, 2, 3, 4, 5, 6, 7]), (99, [0, 1, 2, 3]));
        Assert.True(m.Reached);
        Assert.Equal(4, m.Own(99));
        Assert.Equal([1L, 99L], m.Clappers.OrderBy(x => x));
    }

    [Fact]
    public void Claps_off_the_beat_or_outside_the_ring_do_not_count()
    {
        var m = new MontreuxJazzRules.Meter();
        // half a beat off: a miss
        Assert.Equal(PogoGrade.Miss, m.Clap(1, JazzSet.CallLeadBeats + 0.5, 0.5));
        // before the ring and past it: nothing
        Assert.Equal(PogoGrade.None, m.Clap(1, 0.0, 0.5));
        Assert.Equal(PogoGrade.None, m.Clap(1, JazzSet.CallLeadBeats + JazzSet.CallBeats + 1, 0.5));
        Assert.Equal(0, m.Total);
        Assert.Equal(PogoGrade.Perfect, m.Clap(1, JazzSet.CallLeadBeats + 3.02, 0.5));
        Assert.Equal(1, m.Total);
        // twice on one beat is a miss
        Assert.Equal(PogoGrade.Miss, m.Clap(1, JazzSet.CallLeadBeats + 3.0, 0.5));
        Assert.Equal(1, m.Total);
    }
}
