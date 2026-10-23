using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Addict's 52/36 × 11-28 and its Di2 synchro shifting (#1124).</summary>
public class BikeDrivetrainTests
{
    private static BikeDrivetrain Addict() => new(BicycleCatalog.AddictRc15);

    [Fact]
    public void Development_spans_a_compact_road_bike()
    {
        var d = Addict();
        d.Set(0, 0);
        Assert.InRange(d.Development, 10.0f, 10.2f);    // 52×11
        d.Set(1, 10);
        Assert.InRange(d.Development, 2.70f, 2.80f);    // 36×28
        Assert.InRange(d.Cadence(14.8f / 3.6f), 88f, 92f);   // 36×28 at 14.8 km/h is 90 rpm
    }

    [Fact]
    public void Synchro_shift_climbs_the_whole_range_and_back_every_step_one_way()
    {
        var d = Addict();
        d.Set(1, 10);
        float last = d.Ratio;
        int steps = 0;
        while (d.ShiftHarder())
        {
            Assert.True(d.Ratio > last, $"harder went from {last} to {d.Ratio} at {d}");
            last = d.Ratio;
            steps++;
        }
        Assert.Equal("52×11", d.ToString());
        Assert.InRange(steps, 10, 16);   // fewer than the 22 combinations: synchro skips the cross-chained ones
        while (d.ShiftEasier())
        {
            Assert.True(d.Ratio < last, $"easier went from {last} to {d.Ratio} at {d}");
            last = d.Ratio;
        }
        Assert.Equal("36×28", d.ToString());
    }

    [Theory]
    [InlineData(25f)]
    [InlineData(32f)]
    [InlineData(45f)]
    public void Auto_shifting_settles_in_the_cadence_window(float kmh)
    {
        var d = Addict();
        for (int i = 0; i < 600; i++) d.AutoShift(kmh / 3.6f, pedalling: true, 1f / 60f);
        Assert.InRange(d.Cadence(kmh / 3.6f), BikeDrivetrain.ShiftDownBelow, BikeDrivetrain.ShiftUpAbove);
    }

    [Fact]
    public void A_steep_climb_ends_in_the_lowest_gear_grinding()
    {
        var d = Addict();
        for (int i = 0; i < 600; i++) d.AutoShift(8f / 3.6f, pedalling: true, 1f / 60f);
        Assert.Equal("36×28", d.ToString());
        Assert.InRange(d.Cadence(8f / 3.6f), 45f, 52f);
    }

    [Fact]
    public void Freewheeling_never_shifts()
    {
        var d = Addict();
        string before = d.ToString();
        for (int i = 0; i < 600; i++) d.AutoShift(15f, pedalling: false, 1f / 60f);
        Assert.Equal(before, d.ToString());
    }

    [Fact]
    public void Power_falls_away_from_90_rpm()
    {
        Assert.Equal(1f, BikeDrivetrain.PowerAtCadence(90f), 3);
        Assert.Equal(0.75f, BikeDrivetrain.PowerAtCadence(130f), 3);
        Assert.Equal(BikeDrivetrain.PowerAtCadence(60f), BikeDrivetrain.PowerAtCadence(30f), 3);
    }
}
