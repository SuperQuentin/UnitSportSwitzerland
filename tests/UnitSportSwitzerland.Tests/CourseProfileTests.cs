using UnitSport.Trainer;
using Xunit;

namespace UnitSport.Tests;

/// <summary>A training course's profile and what the trainer is sent (#901, src/Trainer/CourseProfile.cs, linked in).</summary>
public class CourseProfileTests
{
    /// <summary>1 km flat, 1 km at +5%, 1 km at -5%, a point every 10 m.</summary>
    private static CourseProfile Hill()
    {
        var pts = new List<(double, double)>();
        for (int i = 0; i <= 300; i++)
        {
            double s = i * 10;
            double h = s <= 1000 ? 500 : s <= 2000 ? 500 + (s - 1000) * 0.05 : 550 - (s - 2000) * 0.05;
            pts.Add((s, h));
        }
        return new CourseProfile(pts);
    }

    [Fact]
    public void Length_and_ascent()
    {
        var p = Hill();
        Assert.Equal(3000, p.Length, 6);
        Assert.Equal(50, p.Ascent, 6);
    }

    [Fact]
    public void Grade_is_read_over_a_window()
    {
        var p = Hill();
        Assert.Equal(0, p.GradeAt(500), 6);
        Assert.Equal(0.05, p.GradeAt(1500), 6);
        Assert.Equal(-0.05, p.GradeAt(2500), 6);
        // straddling the foot of the climb: half of the window on it
        Assert.Equal(0.025, p.GradeAt(1000), 6);
    }

    [Fact]
    public void A_saw_of_gps_heights_reads_as_its_trend()
    {
        // a steady 4% with +-0.4 m of noise on every 5 m point: point to point it swings from -12% to +20%
        var pts = new List<(double, double)>();
        for (int i = 0; i <= 200; i++) pts.Add((i * 5.0, i * 5.0 * 0.04 + (i % 2 == 0 ? 0.4 : -0.4)));
        var p = new CourseProfile(pts);
        Assert.InRange(p.GradeAt(500), 0.035, 0.045);
    }

    [Fact]
    public void Ascent_left_counts_only_what_is_ahead()
    {
        var p = Hill();
        Assert.Equal(50, p.AscentLeft(0), 6);
        Assert.Equal(25, p.AscentLeft(1500), 6);
        Assert.Equal(0, p.AscentLeft(2000), 6);
        Assert.Equal(0, p.AscentLeft(3000), 6);
    }

    [Fact]
    public void A_course_offset_from_zero_reads_from_its_start()
    {
        var p = new CourseProfile(new[] { (100.0, 10.0), (200.0, 20.0) });
        Assert.Equal(100, p.Length, 6);
        Assert.Equal(15, p.HeightAt(50), 6);
    }

    [Fact]
    public void The_feed_scales_by_difficulty_and_clamps()
    {
        var f = new GradeFeed { Difficulty = 0.5 };
        Assert.Equal(0.04, f.Next(0.08, 0));
        f.Reset();
        f.Difficulty = 1;
        Assert.Equal(GradeFeed.MaxGrade, f.Next(0.4, 0));
    }

    [Fact]
    public void The_feed_sends_at_most_once_a_second_and_only_on_a_change()
    {
        var f = new GradeFeed { Difficulty = 1 };
        Assert.Equal(0.05, f.Next(0.05, 10.0));
        Assert.Null(f.Next(0.08, 10.5));          // too soon
        Assert.Null(f.Next(0.051, 11.2));         // too small a change
        Assert.Equal(0.08, f.Next(0.08, 11.3));
        Assert.Equal(0.08, f.Sent);
    }
}
