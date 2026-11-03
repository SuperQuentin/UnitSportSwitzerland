using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Chalanda;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Chalandamarz (#1356): the festival's date and sites, the march beat and its grading, the loop and its stations, the walking rule, the pay. Pure.</summary>
public class ChalandaTests
{
    private static ResolvedSite Site() => new("the test field", 2600000, 1200000, 100, false, null, "Chalandamarz at the test field");

    [Fact]
    public void The_festival_is_in_the_catalogue_and_sound()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.ChalandaId);
        Assert.NotNull(def);
        Assert.Empty(def!.Problems());
        Assert.Equal("chalandamarz", def.Id);
        Assert.Equal(1, def.Length);
        Assert.True(def.IsOpen(ChalandaRules.UmzugGame, 1));
        Assert.Equal(100, def.TokenCap);
        Assert.Equal(["Scuol", "Zuoz", "Guarda"], def.Sites.Select(s => s.Name));
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    [InlineData(2028)]
    public void It_is_the_first_of_march_alone(int year)
    {
        var def = FestivalCatalog.Get(FestivalCatalog.ChalandaId)!;
        var (from, to) = def.Schedule[0];
        var span = FestivalCalendar.SpanOf(from, to, year);
        Assert.NotNull(span);
        Assert.Equal(new DateOnly(year, 3, 1), span!.Value.Start);
        Assert.Equal(new DateOnly(year, 3, 1), span.Value.End);
        Assert.Equal(1, span.Value.Day(span.Value.Start));
    }

    [Fact]
    public void The_sites_are_in_the_engadine()
    {
        foreach (var s in FestivalCatalog.Get(FestivalCatalog.ChalandaId)!.Sites)
        {
            Assert.InRange(s.E, 2780000, 2830000);
            Assert.InRange(s.N, 1155000, 1195000);
        }
    }

    [Fact]
    public void A_ring_on_the_beat_is_perfect_a_near_one_good_and_half_a_beat_off_a_miss()
    {
        long b = 1000;
        double t = ChalandaRules.BeatTime(b);
        Assert.Equal(RingGrade.Perfect, ChalandaRules.Grade(t));
        Assert.Equal(RingGrade.Perfect, ChalandaRules.Grade(t + 0.05));
        Assert.Equal(RingGrade.Perfect, ChalandaRules.Grade(t - 0.05));
        Assert.Equal(RingGrade.Good, ChalandaRules.Grade(t + 0.1));
        Assert.Equal(RingGrade.Good, ChalandaRules.Grade(t - 0.12));
        Assert.Equal(RingGrade.Miss, ChalandaRules.Grade(t + 0.2));
        Assert.Equal(RingGrade.Miss, ChalandaRules.Grade(t + ChalandaRules.SecondsPerBeat / 2));
        Assert.Equal(b, ChalandaRules.BeatOf(t + 0.1));
        Assert.Equal(b + 1, ChalandaRules.BeatOf(t + 0.3));
    }

    [Fact]
    public void The_beat_is_the_same_moment_for_everybody_and_the_next_beat_is_ahead()
    {
        for (double now = 100.0; now < 101.0; now += 0.07)
        {
            long next = ChalandaRules.NextBeat(now);
            Assert.True(ChalandaRules.BeatTime(next) > now);
            Assert.True(ChalandaRules.BeatTime(next) - now <= ChalandaRules.SecondsPerBeat + 1e-9);
        }
        Assert.Equal(120.0, 60.0 / ChalandaRules.SecondsPerBeat);
    }

    [Fact]
    public void A_stamp_from_the_future_or_long_ago_or_not_a_number_is_not_believed()
    {
        Assert.True(ChalandaRules.BelievableStamp(99.9, 100));
        Assert.True(ChalandaRules.BelievableStamp(100.2, 100));
        Assert.False(ChalandaRules.BelievableStamp(100.5, 100));
        Assert.False(ChalandaRules.BelievableStamp(97.0, 100));
        Assert.False(ChalandaRules.BelievableStamp(double.NaN, 100));
        Assert.False(ChalandaRules.BelievableStamp(double.PositiveInfinity, 100));
    }

    [Fact]
    public void The_loop_is_seven_stations_and_the_stand()
    {
        var site = Site();
        var path = ChalandaRules.Path(site);
        Assert.Equal(ChalandaRules.Stations + 1, path.Length);
        for (int k = 0; k < ChalandaRules.Stations; k++)
            Assert.Equal(ChalandaRules.LoopRadius, ChalandaRules.Dist(path[k].E, path[k].N, (site.E, site.N)), 6);
        Assert.Equal(ChalandaRules.StandAt(site), path[ChalandaRules.Stations]);
        // a station is far enough from the next that one body cannot pass two at once, and the first is out of the stand's reach
        for (int k = 1; k < path.Length; k++)
            Assert.True(ChalandaRules.Dist(path[k].E, path[k].N, path[k - 1]) > 2 * ChalandaRules.StationReach);
        Assert.False(ChalandaRules.NearStand(site, path[0].E, path[0].N));
        Assert.True(ChalandaRules.NearStand(site, path[ChalandaRules.Stations].E + 1, path[ChalandaRules.Stations].N));
        foreach (var (e, n) in path) Assert.True(ChalandaRules.OnLoop(site, e, n));
    }

    [Fact]
    public void The_march_has_a_sensible_length_par_and_limit()
    {
        var site = Site();
        double len = ChalandaRules.Length(site), par = ChalandaRules.ParSeconds(site);
        Assert.InRange(len, 250, 300);
        Assert.InRange(par, 150, 250);
        Assert.True(ChalandaRules.MaxSeconds(site) >= 300);
        Assert.False(ChalandaRules.Plausible(site, 3));
        Assert.True(ChalandaRules.Plausible(site, len / ChalandaRules.SprintSpeed + 0.1));
        Assert.True(ChalandaRules.Plausible(site, 3, 1e9));
    }

    [Fact]
    public void The_walk_has_a_corridor_and_a_far_edge()
    {
        var site = Site();
        Assert.True(ChalandaRules.OnLoop(site, site.E + ChalandaRules.LoopRadius, site.N));
        Assert.True(ChalandaRules.OnLoop(site, site.E, site.N + ChalandaRules.LoopRadius + ChalandaRules.LoopWidth - 1));
        Assert.False(ChalandaRules.OnLoop(site, site.E, site.N));
        Assert.False(ChalandaRules.OnLoop(site, site.E, site.N + ChalandaRules.LoopRadius + ChalandaRules.LoopWidth + 1));
        Assert.False(ChalandaRules.Left(site, site.E, site.N + ChalandaRules.LoopRadius + 10));
        Assert.True(ChalandaRules.Left(site, site.E, site.N + ChalandaRules.LoopRadius + ChalandaRules.LeaveMargin + 1));
    }

    [Fact]
    public void Points_and_pay_follow_the_rings_and_stay_inside_the_day_cap()
    {
        Assert.Equal(0, ChalandaRules.RingPoints(RingGrade.Miss));
        Assert.Equal(ChalandaRules.GoodPoints, ChalandaRules.RingPoints(RingGrade.Good));
        Assert.Equal(ChalandaRules.PerfectPoints, ChalandaRules.RingPoints(RingGrade.Perfect));
        Assert.True(ChalandaRules.RingPoints(RingGrade.Perfect) > ChalandaRules.RingPoints(RingGrade.Good));
        Assert.Equal(ChalandaRules.FinishBatzen, ChalandaRules.Batzen(0));
        Assert.Equal(ChalandaRules.FinishBatzen + 5, ChalandaRules.Batzen(50));
        Assert.Equal(ChalandaRules.UmzugDayCap, ChalandaRules.Batzen(100000));
        Assert.Equal(ChalandaRules.FinishBatzen, ChalandaRules.Batzen(-40));
        // a perfect, fully in-step march (a ring a beat all the way) reaches the cap
        int beats = (int)(ChalandaRules.ParSeconds(Site()) / ChalandaRules.SecondsPerBeat);
        int perfect = beats * (ChalandaRules.PerfectPoints + ChalandaRules.SyncBonus);
        Assert.Equal(ChalandaRules.UmzugDayCap, ChalandaRules.Batzen(perfect));
        var def = FestivalCatalog.Get(FestivalCatalog.ChalandaId)!;
        Assert.True(def.Milestones[^1].Points < perfect);
    }

    [Fact]
    public void The_new_item_is_in_the_festivals_block()
    {
        Assert.InRange((int)ItemId.ChalandaTreichel, 515, 519);
    }
}
