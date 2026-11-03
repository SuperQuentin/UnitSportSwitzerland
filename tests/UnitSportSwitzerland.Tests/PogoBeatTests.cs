using UnitSport.Concert;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The concert pogo's rhythm judge (src/Concert/PogoBeat.cs, linked in, #1150).</summary>
public class PogoBeatTests
{
    private const double Spb = 0.5;   // 120 BPM

    [Fact]
    public void Grades_by_time_off_the_beat()
    {
        var j = new PogoBeat();
        Assert.Equal(PogoGrade.Perfect, j.Press(10.0 + 0.05 / Spb, Spb));
        Assert.Equal(PogoGrade.Good, j.Press(11.0 - 0.12 / Spb, Spb));
        Assert.Equal(PogoGrade.Miss, j.Press(12.5, Spb));
        Assert.Equal(0, j.Combo);
        Assert.Equal(2, j.Best);
    }

    [Fact]
    public void Two_presses_on_one_beat_is_a_miss()
    {
        var j = new PogoBeat();
        Assert.NotEqual(PogoGrade.Miss, j.Press(4.0, Spb));
        Assert.Equal(PogoGrade.Miss, j.Press(4.05, Spb));
    }

    [Fact]
    public void A_hit_starts_the_pogo_and_two_beats_let_go_end_it()
    {
        var j = new PogoBeat();
        j.Tick(7.5, Spb);
        Assert.False(j.Pogoing);
        j.Press(8.0, Spb);
        Assert.True(j.Pogoing);
        for (double b = 8.0; b < 9.25; b += 0.05) Assert.False(j.Tick(b, Spb));   // beat 9 still open
        Assert.True(j.Pogoing);
        bool stopped = false;
        for (double b = 9.25; b < 11.0; b += 0.05) stopped |= j.Tick(b, Spb);
        Assert.True(stopped);
        Assert.False(j.Pogoing);
        Assert.Equal(0, j.Combo);
    }

    [Fact]
    public void Hitting_every_beat_keeps_the_combo()
    {
        var j = new PogoBeat();
        for (int n = 20; n < 36; n++)
        {
            for (double b = n - 0.5; b < n; b += 0.05) j.Tick(b, Spb);
            Assert.Equal(PogoGrade.Perfect, j.Press(n + 0.02, Spb));
            for (double b = n; b < n + 0.5; b += 0.05) j.Tick(b, Spb);
        }
        Assert.Equal(16, j.Combo);
        Assert.True(j.Pogoing);
    }

    [Fact]
    public void A_fast_song_narrows_the_window_to_a_share_of_the_beat()
    {
        double spb = 60.0 / 200.0;
        Assert.Equal(spb * PogoBeat.MaxBeatShare, PogoBeat.Window(spb), 6);
        Assert.Equal(PogoGrade.Miss, new PogoBeat().Press(5.45, spb));
    }

    [Fact]
    public void A_seek_does_not_count_the_skipped_beats_as_misses()
    {
        var j = new PogoBeat();
        j.Press(3.0, Spb);
        j.Tick(3.1, Spb);
        Assert.False(j.Tick(200.0, Spb));
        Assert.True(j.Pogoing);
    }
}
