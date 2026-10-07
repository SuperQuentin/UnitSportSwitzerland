using System.Linq;
using UnitSport.Trailer;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The trailer's bar clock and its cut check (src/Trailer/Song.cs, linked in, #706).</summary>
public class TrailerSongTests
{
    [Fact]
    public void Bars_run_from_the_first_downbeat_at_123_bpm()
    {
        Assert.Equal(0, Song.Bar(1));
        Assert.Equal(Song.FirstBeat + 240.0 / 123.0, Song.Bar(2), 9);
        Assert.Equal(Song.FirstBeat + 40 * 240.0 / 123.0, Song.Bar(41), 9);
        // the drop of "Voxel Revolution", measured at 78.23 s
        Assert.InRange(Song.Bar(41), 78.2, 78.3);
    }

    [Fact]
    public void A_cut_on_the_bars_to_the_end_of_the_song_is_fine()
    {
        var cut = new[] { (1, 1, 3), (2, 4, 2), (3, 6, 60) };
        Assert.Empty(Song.CutProblems(cut));
    }

    [Theory]
    [InlineData(5, "starts at bar 5")]   // a gap: bar 4 is not filmed
    [InlineData(3, "starts at bar 3")]   // an overlap
    public void A_gap_or_an_overlap_is_a_problem(int secondFrom, string expected)
    {
        var cut = new[] { (1, 1, 3), (2, secondFrom, 63) };
        Assert.Contains(Song.CutProblems(cut), p => p.Contains(expected));
    }

    [Fact]
    public void Shots_out_of_order_too_short_or_ending_early_are_problems()
    {
        var problems = Song.CutProblems(new[] { (2, 1, 3), (1, 4, 0), (3, 4, 10) }).ToList();
        Assert.Contains(problems, p => p.Contains("comes after"));
        Assert.Contains(problems, p => p.Contains("lasts 0 bars"));
        Assert.Contains(problems, p => p.Contains("the last shot ends"));
    }
}
