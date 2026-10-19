using UnitSport.Trainer;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The hidden FTP test (#902, src/Trainer/FtpDetector.cs, linked in).</summary>
public class FtpDetectorTests
{
    /// <summary>A ride from (minutes, watts) blocks, one reading a second.</summary>
    private static List<float> Ride(params (int Minutes, float Watts)[] blocks)
    {
        var s = new List<float>();
        foreach (var (m, w) in blocks) s.AddRange(Enumerable.Repeat(w, m * 60));
        return s;
    }

    [Fact]
    public void Under_thirty_minutes_is_no_test()
    {
        Assert.Null(FtpDetector.Estimate(Ride((29, 300f))));
    }

    [Fact]
    public void Thirty_minutes_reads_95_percent_of_the_best_twenty()
    {
        var e = FtpDetector.Estimate(Ride((5, 120f), (20, 300f), (5, 120f)))!.Value;
        Assert.Equal(FtpMethod.Best20, e.Method);
        Assert.Equal(300, e.Best20);
        Assert.Equal(285, e.Watts);
        Assert.Null(e.Best60);
    }

    [Fact]
    public void An_hour_held_steady_is_the_ftp_itself()
    {
        var e = FtpDetector.Estimate(Ride((60, 250f)))!.Value;
        Assert.Equal(FtpMethod.Best60, e.Method);
        Assert.Equal(250, e.Watts);
    }

    [Fact]
    public void An_hour_with_a_hard_twenty_keeps_the_higher_reading()
    {
        // 20 min at 320 W in an otherwise easy hour: 304 W from the 20, ~213 W from the 60
        var e = FtpDetector.Estimate(Ride((20, 160f), (20, 320f), (25, 160f)))!.Value;
        Assert.Equal(FtpMethod.Best20, e.Method);
        Assert.Equal(304, e.Watts);
        Assert.NotNull(e.Best60);
    }

    [Fact]
    public void It_only_ever_raises()
    {
        var ride = Ride((30, 200f));    // reads 190 W
        Assert.Null(FtpDetector.Detect(ride, 250));
        Assert.Null(FtpDetector.Detect(ride, 190));
        Assert.Equal(190, FtpDetector.Detect(ride, 189)!.Value.Watts);
        Assert.Equal(190, FtpDetector.Detect(ride, null)!.Value.Watts);
    }

    [Fact]
    public void A_dead_sensor_is_no_ftp()
    {
        Assert.Null(FtpDetector.Estimate(Ride((40, 20f))));
    }

    [Fact]
    public void Best_average_slides_over_the_whole_ride()
    {
        var s = new List<float> { 100, 200, 300, 100 };
        Assert.Equal(250, FtpDetector.BestAverage(s, 2));
        Assert.Equal(175, FtpDetector.BestAverage(s, 4));
        Assert.Null(FtpDetector.BestAverage(s, 5));
    }

    [Fact]
    public void Steps_at_any_rate_fill_whole_seconds()
    {
        var log = new PowerLog();
        for (int i = 0; i < 60; i++) log.Add(200f, 1.0 / 60);
        Assert.Single(log.Seconds);
        Assert.Equal(200f, log.Seconds[0], 0.01f);

        log.Clear();
        log.Add(100f, 0.5);
        log.Add(300f, 0.5);
        Assert.Equal(200f, Assert.Single(log.Seconds), 0.01f);

        // a 2.5 s hitch fills two seconds and leaves half of a third
        log.Clear();
        log.Add(300f, 2.5);
        Assert.Equal(2, log.Seconds.Count);
        log.Add(100f, 0.5);
        Assert.Equal(200f, log.Seconds[2], 0.01f);
    }

    [Fact]
    public void Spikes_are_clamped_and_nonsense_is_zero()
    {
        var log = new PowerLog();
        log.Add(40000f, 1);
        log.Add(float.NaN, 1);
        log.Add(-50f, 1);
        Assert.Equal(new[] { PowerLog.MaxWatts, 0f, 0f }, log.Seconds);
    }

    [Fact]
    public void The_profile_keeps_a_short_history()
    {
        var p = new RiderProfile();
        var e = new FtpEstimate(240, FtpMethod.Best20, 253, null, 1800);
        Assert.Null(p.Apply(e, new DateTime(2026, 10, 19)));
        Assert.Equal(240, p.Ftp);
        for (int i = 0; i < 30; i++) p.Apply(e with { Watts = 241 + i }, new DateTime(2026, 10, 20));
        Assert.Equal(RiderProfile.HistoryLength, p.FtpHistory.Count);
        Assert.Equal(270, p.FtpHistory[^1].Watts);
        Assert.Equal("Best20", p.FtpHistory[^1].Source);
    }

    [Fact]
    public void The_message_says_what_and_from_what()
    {
        var e = new FtpEstimate(245, FtpMethod.Best20, 258, null, 1800);
        Assert.Equal("Hidden FTP test: 245 W, 95% of your best 20 min (258 W). Was 232 W.", RiderProfile.Message(e, 232));
        Assert.Equal("Hidden FTP test: 250 W, your best 60 min. Your first FTP.",
            RiderProfile.Message(new FtpEstimate(250, FtpMethod.Best60, 260, 250, 3600), null));
    }
}
