using System.Linq;
using UnitSport.Trailer;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The films' bar clocks and the cut check (src/Trailer/Song.cs, linked in, #706, #717).</summary>
public class TrailerSongTests
{
    private static readonly Song V = Song.VoxelRevolution;

    [Fact]
    public void Bars_run_from_the_first_downbeat_at_123_bpm()
    {
        Assert.Equal(0, V.Bar(1));
        Assert.Equal(V.FirstBeat + 240.0 / 123.0, V.Bar(2), 9);
        Assert.Equal(V.FirstBeat + 40 * 240.0 / 123.0, V.Bar(41), 9);
        // the drop of "Voxel Revolution", measured at 78.23 s
        Assert.InRange(V.Bar(41), 78.2, 78.3);
    }

    [Fact]
    public void A_cut_on_the_bars_to_the_end_of_the_song_is_fine()
    {
        var cut = new[] { (1, 1, 3), (2, 4, 2), (3, 6, 60) };
        Assert.Empty(V.CutProblems(cut));
    }

    [Theory]
    [InlineData(5, "starts at bar 5")]   // a gap: bar 4 is not filmed
    [InlineData(3, "starts at bar 3")]   // an overlap
    public void A_gap_or_an_overlap_is_a_problem(int secondFrom, string expected)
    {
        var cut = new[] { (1, 1, 3), (2, secondFrom, 63) };
        Assert.Contains(V.CutProblems(cut), p => p.Contains(expected));
    }

    [Fact]
    public void Shots_out_of_order_too_short_or_ending_early_are_problems()
    {
        var problems = V.CutProblems(new[] { (2, 1, 3), (1, 4, 0), (3, 4, 10) }).ToList();
        Assert.Contains(problems, p => p.Contains("comes after"));
        Assert.Contains(problems, p => p.Contains("lasts 0 bars"));
        Assert.Contains(problems, p => p.Contains("the last shot ends"));
    }

    [Fact]
    public void The_clip_runs_15_bars_at_121_bpm_from_the_first_downbeat()
    {
        var song = Song.IGotAStick;
        Assert.Equal(0.322 + 240.0 / 121.0, song.Bar(2), 9);
        // 14 bars of music and a 15th for the end card, to the film's end
        Assert.Equal(song.End, song.Bar(16), 2);
        Assert.Empty(song.CutProblems(new[] { (1, 1, 2), (2, 3, 13) }));
        Assert.NotEmpty(song.CutProblems(new[] { (1, 1, 2), (2, 3, 12) }));
    }
}
