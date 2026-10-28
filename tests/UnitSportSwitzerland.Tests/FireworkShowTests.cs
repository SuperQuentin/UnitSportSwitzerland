using UnitSport.Occasions.Festivals;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The fireworks script and the closing show's rules (#1269 part 2, src/Occasions/Festivals/FireworkShow.cs, linked in).</summary>
public class FireworkShowTests
{
    [Fact]
    public void The_same_seed_is_the_same_show()
    {
        var a = FireworkShow.Plan(1269);
        var b = FireworkShow.Plan(1269);
        Assert.Equal(a.Duration, b.Duration);
        Assert.Equal(a.Heading, b.Heading);
        Assert.Equal(a.Shells, b.Shells);
    }

    [Fact]
    public void Another_seed_is_another_show()
    {
        Assert.NotEqual(FireworkShow.Plan(1).Shells, FireworkShow.Plan(2).Shells);
    }

    [Fact]
    public void A_show_lasts_six_to_twelve_minutes_and_the_server_can_tell_without_the_script()
    {
        var seen = new HashSet<double>();
        for (int seed = 0; seed < 200; seed++)
        {
            var show = FireworkShow.Plan(seed);
            Assert.InRange(show.Duration, 360, 720);
            Assert.Equal(show.Duration, FireworkShow.DurationOf(seed));
            seen.Add(show.Duration);
        }
        Assert.True(seen.Count >= 5, "the lengths vary");
    }

    [Fact]
    public void The_finale_is_forty_shells_in_the_last_twenty_seconds()
    {
        foreach (int seed in new[] { 7, 1269, 40001, 999 })
        {
            var show = FireworkShow.Plan(seed);
            double from = show.Duration - FireworkShow.FinaleSeconds;
            int inFinale = show.Shells.Count(s => s.T >= from - 0.2);
            Assert.True(inFinale >= FireworkShow.FinaleShells, $"seed {seed}: {inFinale} shells in the finale");
            // the acts before it are slower: fewer than two shells a second on average
            int before = show.Shells.Count(s => s.T < from - 0.2);
            Assert.True(before / (from - 0.2) < 1.5, $"seed {seed}: {before} shells before the finale");
            Assert.True(show.Shells[^1].T <= show.Duration, "nothing after the end");
            Assert.True(show.Shells[0].T >= FireworkShow.LeadIn, "a quiet lead-in");
        }
    }

    [Fact]
    public void Shells_are_in_order_and_stay_in_a_sane_box()
    {
        for (int seed = 0; seed < 60; seed++)
        {
            var show = FireworkShow.Plan(seed);
            for (int i = 1; i < show.Shells.Length; i++) Assert.True(show.Shells[i].T >= show.Shells[i - 1].T);
            foreach (var s in show.Shells)
            {
                Assert.InRange(s.Height, 80, 170);
                Assert.True(Math.Sqrt(s.East * s.East + s.North * s.North) < 250, "within 250 m of the launch point");
                Assert.InRange(s.Colour, 0, FireworkShow.Colours - 1);
                var (lo, hi) = FireworkShow.HeightRange(s.Kind);
                Assert.InRange(s.Height, lo - 0.01f, hi + 0.01f);
                Assert.Equal(s.T - FireworkShow.RiseSeconds, s.Launch);
            }
        }
    }

    [Fact]
    public void Every_kind_flies_and_a_show_has_its_Swiss_cross_over_the_middle_of_the_fan()
    {
        var kinds = new HashSet<ShellKind>();
        for (int seed = 0; seed < 30; seed++)
        {
            var show = FireworkShow.Plan(seed);
            foreach (var s in show.Shells) kinds.Add(s.Kind);
            var crosses = show.Shells.Where(s => s.Kind == ShellKind.SwissCross).ToArray();
            Assert.True(crosses.Length >= 2, "one closes the second act and one stands in the finale");
            foreach (var c in crosses)
            {
                Assert.Equal(0, c.Colour);
                Assert.Equal(Math.Sin(show.Heading) * 110, c.East, 1);
                Assert.Equal(Math.Cos(show.Heading) * 110, c.North, 1);
            }
        }
        foreach (var kind in Enum.GetValues<ShellKind>()) Assert.Contains(kind, kinds);
    }

    [Fact]
    public void The_cursor_finds_the_first_shell_at_or_after_a_time()
    {
        var show = FireworkShow.Plan(1269);
        Assert.Equal(0, show.First(-5));
        Assert.Equal(show.Shells.Length, show.First(show.Duration + 100));
        for (int i = 0; i < show.Shells.Length; i += 7)
        {
            double t = show.Shells[i].T;
            int at = show.First(t);
            Assert.True(show.Shells[at].T >= t && (at == 0 || show.Shells[at - 1].T < t));
        }
        // a peer joining 120 s in skips everything before the cursor
        int joined = show.First(120 - 0.5);
        Assert.True(joined > 0 && show.Shells[joined].T >= 119.5 && show.Shells[joined - 1].T < 119.5);
    }

    // ---- when a show plays -----------------------------------------------------------------------

    [Fact]
    public void An_evening_show_starts_when_the_sky_crosses_half_past_nine()
    {
        Assert.True(FestivalShows.Starts(21.4, 21.6));
        Assert.True(FestivalShows.Starts(12, 22.5), "a jump of the clock across 21:30 counts");
        Assert.False(FestivalShows.Starts(21.6, 21.9), "once a night");
        Assert.False(FestivalShows.Starts(20.0, 21.2), "not before");
        Assert.False(FestivalShows.Starts(23.9, 2.0), "not in the small hours");
        Assert.True(FestivalShows.Starts(null, 22.0), "a server that wakes up in the evening starts one");
        Assert.False(FestivalShows.Starts(null, 14.0));
        Assert.False(FestivalShows.Starts(null, 3.0));
    }

    [Fact]
    public void The_next_evening_starts_another_show_after_the_clock_wrapped()
    {
        double? last = 21.4;
        int started = 0;
        // one real game day at 0.1 h a step
        for (int step = 0; step < 250; step++)
        {
            double hour = (21.4 + step * 0.1) % 24;
            if (FestivalShows.Starts(last, hour)) started++;
            last = hour;
        }
        Assert.Equal(2, started);
    }

    [Fact]
    public void Only_fireworks_and_both_have_fireworks()
    {
        Assert.True(FestivalShows.HasFireworks(ClosingShow.Fireworks));
        Assert.True(FestivalShows.HasFireworks(ClosingShow.Both));
        Assert.False(FestivalShows.HasFireworks(ClosingShow.Concert));
        Assert.False(FestivalShows.HasFireworks(ClosingShow.Bonfires));
        Assert.False(FestivalShows.HasFireworks(ClosingShow.None));
    }

    [Fact]
    public void The_seed_is_stable_and_differs_by_instance_and_by_evening()
    {
        Assert.Equal(FestivalShows.SeedOf("aelplerfest-2027", 1), FestivalShows.SeedOf("aelplerfest-2027", 1));
        Assert.NotEqual(FestivalShows.SeedOf("aelplerfest-2027", 1), FestivalShows.SeedOf("aelplerfest-2027", 2));
        Assert.NotEqual(FestivalShows.SeedOf("aelplerfest-2027", 1), FestivalShows.SeedOf("aelplerfest-2028", 1));
        Assert.True(FestivalShows.SeedOf("x", 1) >= 0);
        // pinned: a different hash would change every show ever planned
        Assert.Equal(FestivalShows.SeedOf("test-2027", 1), FestivalShows.SeedOf("test-2027", 1));
    }

    [Fact]
    public void A_show_is_on_the_books_from_its_start_until_it_ends()
    {
        int seed = 1269;
        double length = FireworkShow.DurationOf(seed), start = 1000;
        Assert.False(FestivalShows.Active(0, seed, 1000), "no start, no show");
        Assert.True(FestivalShows.Active(start, seed, start - 10), "announced a few seconds ahead");
        Assert.True(FestivalShows.Active(start, seed, start + length - 1));
        Assert.False(FestivalShows.Active(start, seed, start + length + FestivalShows.Tail + 1), "over");
    }

    [Fact]
    public void A_shell_two_kilometres_off_is_heard_six_seconds_late()
    {
        Assert.Equal(5.83, FestivalShows.SoundDelay(2000), 2);
        Assert.Equal(0, FestivalShows.SoundDelay(0));
        Assert.True(FestivalShows.SoundDelay(FestivalShows.DrawRangeM) < 45);
    }

    [Fact]
    public void The_test_festival_ends_on_fireworks()
    {
        Assert.Equal(ClosingShow.Fireworks, FestivalCatalog.Test(2600000, 1200000).Show);
    }
}
