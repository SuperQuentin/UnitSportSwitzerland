using UnitSport.Occasions.Festivals;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The stone put's rules and ground (#1269 part 3, src/Occasions/Festivals/SteinRules.cs, linked in).</summary>
public class SteinRulesTests
{
    private const int Seed = 4242;

    /// <summary>The stamps of a put: the lift held <paramref name="held"/> s, then each press <paramref name="off"/> s off its beat.</summary>
    private static SteinThrow Put(double held, double off = 0, double pitch = SteinRules.BestAngle)
    {
        double down = 100, up = down + held;
        var s = new double[SteinRules.Stamp.Count];
        s[SteinRules.Stamp.Down] = down;
        s[SteinRules.Stamp.Up] = up;
        for (int n = 1; n <= 4; n++) s[SteinRules.Stamp.Step1 + n - 1] = SteinRules.BeatAt(up, n) + off;
        return new SteinThrow(s, pitch);
    }

    private static SteinThrow Perfect(int cls, double pitch = SteinRules.BestAngle) => Put(SteinRules.NextPeak(Seed, cls, 0.5), 0, pitch);

    [Fact]
    public void The_stone_weighs_83_and_a_half_kilos_and_a_perfect_put_reaches_its_par()
    {
        Assert.Equal(83.5, SteinRules.Classes[0].Kg);
        for (int cls = 0; cls < SteinRules.Classes.Length; cls++)
        {
            var r = SteinRules.Resolve(Seed, cls, Perfect(cls));
            Assert.Null(r.Foul);
            Assert.Equal(SteinRules.Classes[cls].Par, r.Metres, 2);
            Assert.Equal(SteinRules.Classes[cls].MaxBatzen, r.Batzen);
            Assert.All(r.Steps, g => Assert.Equal(StepGrade.Perfect, g));
        }
        Assert.InRange(SteinRules.Classes[0].Par, 3.5, 4.5);   // the real Unspunnen throws
    }

    [Fact]
    public void A_lighter_stone_goes_further_and_pays_less()
    {
        for (int i = 1; i < SteinRules.Classes.Length; i++)
        {
            Assert.True(SteinRules.Classes[i].Par > SteinRules.Classes[i - 1].Par);
            Assert.True(SteinRules.Classes[i].MaxBatzen < SteinRules.Classes[i - 1].MaxBatzen);
        }
        Assert.True(SteinRules.MeterPeriod(0) > SteinRules.MeterPeriod(2), "a heavier stone swings the meter slower");
    }

    [Fact]
    public void The_meter_is_a_triangle_between_nothing_and_the_peak_and_never_the_same_twice()
    {
        for (double t = 0; t < 12; t += 0.037)
            Assert.InRange(SteinRules.Meter(Seed, 0, t), 0, 1);
        double peak = SteinRules.NextPeak(Seed, 0, 0.5);
        Assert.True(peak >= 0.5);
        Assert.Equal(1.0, SteinRules.Meter(Seed, 0, peak), 6);
        Assert.Equal(0.0, SteinRules.Meter(Seed, 0, peak + SteinRules.MeterPeriod(0) / 2), 6);
        Assert.NotEqual(SteinRules.Meter(1, 0, 1.0), SteinRules.Meter(2, 0, 1.0));
        Assert.Equal(SteinRules.Meter(7, 0, 1.0), SteinRules.Meter(7, 0, 1.0));
        Assert.True(SteinRules.LiftQuality(1.0) == 1 && SteinRules.LiftQuality(0.95) > 0.999 && SteinRules.LiftQuality(0.3) == 0);
    }

    [Fact]
    public void The_drum_is_ninety_beats_a_minute_and_the_windows_are_the_pogos()
    {
        Assert.Equal(2.0 / 3.0, SteinRules.SecondsPerBeat, 9);
        Assert.Equal(StepGrade.Perfect, SteinRules.Grade(10.03, 10.0));
        Assert.Equal(StepGrade.Good, SteinRules.Grade(10.12, 10.0));
        Assert.Equal(StepGrade.Good, SteinRules.Grade(9.88, 10.0));
        Assert.Equal(StepGrade.Miss, SteinRules.Grade(10.2, 10.0));
        Assert.True(SteinRules.Factor(StepGrade.Perfect) > SteinRules.Factor(StepGrade.Good) && SteinRules.Factor(StepGrade.Good) > SteinRules.Factor(StepGrade.Miss));
    }

    [Fact]
    public void Missing_the_beat_loses_distance_step_by_step()
    {
        double peak = SteinRules.NextPeak(Seed, 0, 0.5);
        double perfect = SteinRules.Resolve(Seed, 0, Put(peak)).Metres;
        double good = SteinRules.Resolve(Seed, 0, Put(peak, 0.1)).Metres;
        double missed = SteinRules.Resolve(Seed, 0, Put(peak, 0.3)).Metres;
        Assert.True(perfect > good && good > missed, $"{perfect} > {good} > {missed}");
        Assert.True(missed > 0.5, "a clumsy put still goes somewhere");
    }

    [Fact]
    public void A_stone_let_go_off_the_peak_goes_shorter_and_at_the_trough_it_slips()
    {
        double peak = SteinRules.NextPeak(Seed, 0, 0.5);
        double near = SteinRules.Resolve(Seed, 0, Put(peak - 0.15)).Metres;
        Assert.True(near < SteinRules.Resolve(Seed, 0, Perfect(0)).Metres);
        var slip = SteinRules.Resolve(Seed, 0, Put(peak + SteinRules.MeterPeriod(0) / 2));
        Assert.NotNull(slip.Foul);
        Assert.Equal(0, slip.Batzen);
        Assert.Equal(0, slip.Points);
        Assert.InRange(slip.Metres, 0, 0.5);
    }

    [Fact]
    public void The_best_angle_is_the_plans_38_to_42_and_a_flat_or_a_high_push_is_worse()
    {
        double best = SteinRules.Resolve(Seed, 0, Perfect(0, 40)).Metres;
        foreach (double angle in new[] { 38.0, 42.0 })
            Assert.InRange(SteinRules.Resolve(Seed, 0, Perfect(0, angle)).Metres, best * 0.98, best * 1.02);
        Assert.True(SteinRules.Resolve(Seed, 0, Perfect(0, 30)).Metres < best, "a push out of the band is never the best");
        Assert.True(SteinRules.Resolve(Seed, 0, Perfect(0, 46)).Metres < best);
        Assert.True(SteinRules.Resolve(Seed, 0, Perfect(0, 10)).Metres < best * 0.8, "a flat push");
        Assert.True(SteinRules.Resolve(Seed, 0, Perfect(0, 70)).Metres < best * 0.6, "a push to the sky");
        // nonsense angles are clamped, not trusted
        Assert.Equal(SteinRules.Resolve(Seed, 0, Perfect(0, 75)).Metres, SteinRules.Resolve(Seed, 0, Perfect(0, 500)).Metres);
        Assert.True(double.IsFinite(SteinRules.Resolve(Seed, 0, Perfect(0, double.NaN)).Metres));
    }

    [Fact]
    public void The_flight_is_ballistic_from_the_release_height_and_lands_where_the_range_says()
    {
        double v = SteinRules.FullSpeed(0), a = SteinRules.BestAngle;
        double t = SteinRules.FlightTime(v, a);
        var (along, up) = SteinRules.Position(v, a, t);
        Assert.Equal(SteinRules.Range(v, a), along, 6);
        Assert.Equal(0, up, 6);
        Assert.Equal(SteinRules.ReleaseHeight, SteinRules.Position(v, a, 0).Up, 9);
        Assert.True(SteinRules.Position(v, a, t / 2).Up > SteinRules.ReleaseHeight);
    }

    [Fact]
    public void Implausible_stamps_are_refused_and_a_sloppy_but_honest_put_is_not()
    {
        double begun = 190;
        double[] honest = [begun + 1, begun + 3, begun + 3.7, begun + 4.3, begun + 5.1, begun + 5.7];
        Assert.Null(SteinRules.Implausible(honest, begun, begun + 5.8));
        Assert.NotNull(SteinRules.Implausible(null, begun, begun + 6));
        Assert.NotNull(SteinRules.Implausible([1, 2, 3], begun, begun + 6));
        Assert.NotNull(SteinRules.Implausible([begun + 1, begun + 3, double.NaN, begun + 4, begun + 5, begun + 6], begun, begun + 6));
        Assert.NotNull(SteinRules.Implausible(honest, begun + 30, begun + 36));   // pressed before the attempt began
        Assert.NotNull(SteinRules.Implausible(honest, begun, begun + 1));          // the future
        Assert.NotNull(SteinRules.Implausible(honest, begun, begun + 60));         // stale
        double[] backwards = [begun + 1, begun + 3, begun + 5, begun + 4, begun + 5.1, begun + 5.7];
        Assert.NotNull(SteinRules.Implausible(backwards, begun, begun + 5.8));
        double[] machineGun = [begun + 1, begun + 3, begun + 3.7, begun + 3.72, begun + 3.74, begun + 3.76];
        Assert.NotNull(SteinRules.Implausible(machineGun, begun, begun + 3.8));
        double[] longHold = [begun + 1, begun + 90, begun + 91, begun + 92, begun + 93, begun + 94];
        Assert.NotNull(SteinRules.Implausible(longHold, begun, begun + 94.1));
    }

    [Fact]
    public void A_skilled_bot_puts_further_than_a_clumsy_one_and_is_never_implausible()
    {
        var rng = new Random(7);
        double skilled = 0, clumsy = 0;
        for (int i = 0; i < 100; i++)
        {
            int seed = i * 31 + 5;
            var s = SteinRules.BotAttempt(0.95, rng, seed, 0, 50);
            var c = SteinRules.BotAttempt(0.15, rng, seed, 0, 50);
            Assert.Null(SteinRules.Implausible(s.Stamps, 50, s.Stamps[^1]));
            Assert.Null(SteinRules.Implausible(c.Stamps, 50, c.Stamps[^1]));
            skilled += SteinRules.Resolve(seed, 0, s).Metres;
            clumsy += SteinRules.Resolve(seed, 0, c).Metres;
        }
        Assert.InRange(skilled / 100, SteinRules.Classes[0].Par * 0.7, SteinRules.Classes[0].Par * 1.02);
        Assert.True(clumsy / 100 < skilled / 100 - 0.4, $"{clumsy / 100} < {skilled / 100}");
    }

    [Fact]
    public void A_day_of_puts_pays_well_inside_the_games_daily_cap_and_the_festivals_economy()
    {
        var def = FestivalCatalog.Aelplerfest();
        Assert.Empty(def.Problems());
        Assert.True(def.IsOpen(SteinRules.GameId, 1));
        int perDay = SteinRules.Classes[0].MaxBatzen * SteinRules.AttemptsPerDay;
        Assert.True(perDay <= def.Game(SteinRules.GameId)!.DayCap, "three perfect puts fit the day's cap");
        Assert.True(perDay * def.Length >= def.TokenCap, "the stone alone could reach the cap, so nobody is stuck below it");
    }

    [Fact]
    public void The_ground_is_a_pure_function_of_the_site_clear_of_the_mortar_rack()
    {
        var site = new ResolvedSite("Höhematte", 2632427, 1170452, 120, false, null, "x");
        var a = SteinGround.Of(site);
        Assert.Equal(a, SteinGround.Of(site));
        Assert.Equal(SteinGround.OffsetM, new SteinGround(site.E, site.N, 0, 0).DistanceTo(a.E, a.N), 6);
        Assert.Equal(1.0, a.DirE * a.DirE + a.DirN * a.DirN, 6);
        var (e, n) = a.Along(10);
        Assert.Equal(10.0, a.DistanceTo(e, n), 6);
        Assert.True(a.DistanceTo(site.E - 14, site.N) > 10);   // the rack stands 14 m west of the middle
        var other = SteinGround.Of(site with { E = site.E + 1 });
        Assert.NotEqual(a.Yaw, other.Yaw);
    }
}
