using UnitSport.Terrain.Format;
using UnitSport.World.Snow;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The ski slopes' snow (#887): a deterministic daily weather, the 06:00 reset, wear through the
/// day, slush low and late on a warm day, ice after a thaw and a cold night, powder after a storm.
/// </summary>
public class SnowModelTests
{
    private static readonly PisteRecord Red = new(1, PisteType.Downhill, PisteDifficulty.Intermediate, PisteGrooming.Classic,
        false, 30, "Mont-Fort", "12", [], 1800, 2400, 0.5f, 22);
    private static readonly PisteRecord Freeride = Red with { Difficulty = PisteDifficulty.Freeride, Grooming = PisteGrooming.Backcountry, SteepDeg = 32 };

    private static DayWeather Mild(long day = 10) => new(day, 0, 0, 0, 0, -6, 2, 0.1f);
    private static DayWeather Cold(long day = 10) => new(day, 0, 0, 0, 0, -16, -9, 0.1f);

    [Fact]
    public void The_weather_is_the_same_for_every_peer_and_varies_by_day()
    {
        var a = SnowWeather.ForDay(42, 7);
        Assert.Equal(a, SnowWeather.ForDay(42, 7));
        Assert.NotEqual(a, SnowWeather.ForDay(43, 7));
        Assert.NotEqual(a, SnowWeather.ForDay(42, 8));
        // over a season: some storms, some snowy days, never absurd temperatures
        int storms = 0, snowy = 0;
        for (long d = 0; d < 400; d++)
        {
            var w = SnowWeather.ForDay(d, 1);
            if (w.NewSnowCm > 0) storms++;
            if (w.DaySnowCm > 0) snowy++;
            Assert.InRange(w.TMax2000, -14f, 7f);
            Assert.True(w.TMin2000 < w.TMax2000);
            Assert.InRange(w.NewSnowCm, 0f, 41f);
        }
        Assert.InRange(storms, 50, 130);
        Assert.InRange(snowy, 25, 90);
    }

    [Fact]
    public void An_override_sets_todays_weather_and_lapses_tomorrow_unless_sticky()
    {
        var ov = new WeatherOverride(NewSnowCm: 30, TMax2000: -5, Day: 5);
        Assert.Equal(30f, SnowWeather.ForDay(5, 1, ov).NewSnowCm);
        Assert.Equal(-5f, SnowWeather.ForDay(5, 1, ov).TMax2000);
        Assert.Equal(SnowWeather.ForDay(6, 1), SnowWeather.ForDay(6, 1, ov));
        Assert.Equal(30f, SnowWeather.ForDay(6, 1, ov with { Sticky = true }).NewSnowCm);
        Assert.NotEqual(SnowWeather.ForDay(5, 1), SnowWeather.ForDay(5, 1, new WeatherOverride(Reroll: 1, Day: 5)));
    }

    [Fact]
    public void A_ski_day_runs_from_six_to_six()
    {
        Assert.Equal((0L, 6.0), SnowClock.Split(6 * 3600));
        var (day, hour) = SnowClock.Split(86400 + 5 * 3600); // 05:00 on day 1 is still day 0's night
        Assert.Equal(0L, day);
        Assert.Equal(29.0, hour, 6);
        Assert.Equal(1L, SnowClock.Split(86400 + 6 * 3600).Day);
    }

    [Fact]
    public void Temperature_falls_with_altitude_and_peaks_in_the_afternoon()
    {
        var w = Mild();
        Assert.Equal(-6f, w.TempAt(6, 2000), 3);
        Assert.Equal(2f, w.TempAt(14, 2000), 3);
        Assert.Equal(2f - 6.5f, w.TempAt(14, 3000), 3);
        Assert.True(w.TempAt(22, 2000) < w.TempAt(14, 2000));
    }

    [Fact]
    public void Groomers_leave_corduroy_at_six_and_the_crowd_wears_it_off()
    {
        var w = Cold();
        var morning = SnowModel.Evaluate(w, w, 6.5, 2000, 0.5f, Red, 0);
        Assert.Equal(SnowCondition.Corduroy, morning.Condition);
        Assert.True(morning.Fresh > 0.95f);
        Assert.Equal(0f, morning.Wear);
        var evening = SnowModel.Evaluate(w, w, 16.5, 2000, 0.5f, Red, 0);
        Assert.True(evening.Fresh < 0.4f, $"fresh {evening.Fresh}");
        Assert.NotEqual(SnowCondition.Corduroy, evening.Condition);
        // the next morning is back to corduroy: the wear is per ski day
        var next = SnowModel.Evaluate(Cold(11), w, 6.5, 2000, 0.5f, Red, 0);
        Assert.Equal(SnowCondition.Corduroy, next.Condition);
    }

    [Fact]
    public void Real_skiers_add_to_the_crowds_wear()
    {
        var w = Cold();
        var quiet = SnowModel.Evaluate(w, w, 9, 2000, 0.5f, Red, 0);
        var busy = SnowModel.Evaluate(w, w, 9, 2000, 0.5f, Red, 1.2f);
        Assert.True(busy.Fresh < quiet.Fresh * 0.2f);
        Assert.True(busy.Wear > quiet.Wear + 1);
    }

    [Fact]
    public void A_warm_afternoon_turns_low_sunny_slopes_to_slush_and_leaves_high_ones_dry()
    {
        var w = new DayWeather(10, 0, 0, 0, 0, -2, 6, 0);
        var low = SnowModel.Evaluate(w, w, 15, 1500, 0.9f, Red, 0);
        var high = SnowModel.Evaluate(w, w, 15, 3000, 0.9f, Red, 0);
        var lowMorning = SnowModel.Evaluate(w, w, 8, 1500, 0.9f, Red, 0);
        Assert.Equal(SnowCondition.Slush, low.Condition);
        Assert.NotEqual(SnowCondition.Slush, high.Condition);
        Assert.True(high.Wet < low.Wet - 0.5f, $"high wet {high.Wet}");
        Assert.True(lowMorning.Wet < low.Wet);
        Assert.True(low.FrictionScale > 1.5f);
        // a north face at the same altitude melts less
        Assert.True(SnowModel.Evaluate(w, w, 15, 2400, 0.1f, Red, 0).Wet < SnowModel.Evaluate(w, w, 15, 2400, 0.9f, Red, 0).Wet - 0.2f);
    }

    [Fact]
    public void A_thaw_then_a_cold_night_makes_an_icy_morning_that_the_sun_softens()
    {
        var thaw = new DayWeather(9, 0, 0, 0, 0, -1, 7, 0);
        var today = new DayWeather(10, 0, 0, 0, 0, -12, 3, 0);
        var morning = SnowModel.Evaluate(today, thaw, 7, 1800, 0.5f, Freeride, 0);
        Assert.Equal(SnowCondition.Icy, morning.Condition);
        Assert.True(morning.GripScale < 0.7f);
        var noon = SnowModel.Evaluate(today, thaw, 14, 1800, 0.8f, Freeride, 0);
        Assert.True(noon.Ice < morning.Ice * 0.5f, $"noon ice {noon.Ice}");
        // the groomers till most of the crust on a groomed run
        Assert.True(SnowModel.Evaluate(today, thaw, 7, 1800, 0.5f, Red, 0).Ice < morning.Ice * 0.6f);
    }

    [Fact]
    public void A_storm_leaves_powder_off_piste_that_is_chopped_up_through_the_day()
    {
        var storm = new DayWeather(10, 30, 0, 0, 0, -12, -6, 0.4f);
        var calm = Cold(9);
        var first = SnowModel.Evaluate(storm, calm, 8, 2200, 0.5f, Freeride, 0);
        Assert.Equal(SnowCondition.Powder, first.Condition);
        Assert.True(first.FrictionScale > 1.5f);
        var late = SnowModel.Evaluate(storm, calm, 16, 2200, 0.5f, Freeride, 1.5f);
        Assert.True(late.Loose < first.Loose * 0.3f);
        Assert.Equal(SnowCondition.Moguls, late.Condition); // a steep ungroomed face skied all day
        // the groomed run only keeps what fell after the groomers: a thin layer on corduroy
        var red = SnowModel.Evaluate(storm, calm, 8, 2200, 0.5f, Red, 0);
        Assert.True(red.LooseCm is > 3 and < 7);
    }

    [Fact]
    public void A_mogul_run_has_moguls_from_the_first_lift()
    {
        var w = Cold();
        var bumps = SnowModel.Evaluate(w, w, 9, 2000, 0.5f, Red with { Grooming = PisteGrooming.Mogul }, 0);
        Assert.Equal(SnowCondition.Moguls, bumps.Condition);
    }
}
