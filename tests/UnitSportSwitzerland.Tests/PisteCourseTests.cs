using System;
using System.Linq;
using UnitSport.World.Snow;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Ski race courses down the slopes (#909): down the nearest run, on down the run continuing from
/// its bottom (never one climbing away, never back), cut at the race length, null far from any run.
/// </summary>
public class PisteCourseTests
{
    // the ground falls 1 m for every metre south
    private static float Alt(double e, double n) => (float)(1000 + n);

    private static PisteCourse.Run Line(ulong id, float width, params double[] pts) => new(id, pts, width);

    // A: north to south from N 1000 to 500. B: from just beside A's bottom on down to N 0.
    // C: from A's bottom back north-east, climbing: a lift line drawn as a slope, say. D: far away.
    private static readonly PisteCourse.Run[] Runs =
    [
        Line(1, 30, 0, 1000, 0, 750, 0, 500),
        Line(2, 25, 10, 495, 20, 250, 10, 0),
        Line(3, 40, 0, 500, 200, 900),
        Line(4, 30, 5000, 1000, 5000, 0),
    ];

    [Fact]
    public void A_course_runs_down_one_slope_and_on_down_the_next()
    {
        var c = PisteCourse.Chain(Runs, 3, 990, Alt, 5000)!;
        Assert.NotNull(c);
        Assert.InRange(c[0].N, 980, 1000);
        Assert.InRange(c[^1].N, -1, 5);                        // the bottom of B
        Assert.True(c.All(p => p.E < 30), "never up C, never off to D");
        // always downhill, every 2 m along the line, A's width then B's
        for (int i = 1; i < c.Count; i++)
        {
            Assert.True(c[i].N <= c[i - 1].N + 1e-6);
            double d = Math.Sqrt(Math.Pow(c[i].E - c[i - 1].E, 2) + Math.Pow(c[i].N - c[i - 1].N, 2));
            if (i < c.Count - 1) Assert.InRange(d, 1.0, PisteCourse.Step + 0.01);   // a corner's chord is shorter; the end is where it is
        }
        Assert.Equal(30f, c[5].Width);
        Assert.Equal(25f, c[^5].Width);
    }

    [Fact]
    public void A_course_is_cut_at_the_race_length()
    {
        var c = PisteCourse.Chain(Runs, 0, 1000, Alt, 600)!;
        double len = 0;
        for (int i = 1; i < c.Count; i++) len += Math.Sqrt(Math.Pow(c[i].E - c[i - 1].E, 2) + Math.Pow(c[i].N - c[i - 1].N, 2));
        Assert.InRange(len, 598, 604);
    }

    [Fact]
    public void A_run_drawn_uphill_is_still_skied_down()
    {
        // B's points listed bottom first
        var flipped = new[] { Line(9, 30, 0, 0, 0, 500, 0, 1000) };
        var c = PisteCourse.Chain(flipped, 0, 1000, Alt, 5000)!;
        Assert.True(c[^1].N < 5 && c[0].N > 995);
    }

    [Fact]
    public void Above_the_start_is_the_same_run_uphill_where_npcs_come_down_from()
    {
        var above = PisteCourse.Above(Runs, 0, 750, Alt, 200);   // a course starts on a vertex of its run
        Assert.Equal(750, above[0].N, 3);
        Assert.True(above[^1].N > above[0].N + 150, "it climbs");
        Assert.True(above.All(p => p.E < 1));
    }

    [Fact]
    public void No_run_within_reach_is_no_course()
    {
        Assert.Null(PisteCourse.Chain(Runs, 2500, 500, Alt, 1000));
    }
}
