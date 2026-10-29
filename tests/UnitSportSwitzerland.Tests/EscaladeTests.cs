using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Escalade;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Escalade (#1351): the festival's dates and site, the Marmite's pair, the Course's gates, time and pay, the soup. Pure.</summary>
public class EscaladeTests
{
    private static ResolvedSite Site() => new("the test field", 2600000, 1200000, 100, false, null, "Escalade at the test field");

    [Fact]
    public void The_festival_is_in_the_catalogue_and_sound()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.EscaladeId);
        Assert.NotNull(def);
        Assert.Empty(def!.Problems());
        Assert.Equal("escalade", def.Id);
        Assert.Equal(2, def.Length);
        foreach (string g in new[] { EscaladeRules.MarmiteGame, EscaladeRules.CourseGame, EscaladeRules.SoupeGame }) Assert.True(def.IsOpen(g, 1));
        Assert.Equal(300, def.TokenCap);
    }

    [Theory]
    [InlineData(2026, 12, 12, 12, 13)]
    [InlineData(2027, 12, 11, 12, 12)]
    [InlineData(2028, 12, 9, 12, 10)]
    public void It_opens_on_the_second_saturday_of_december_for_two_days(int year, int month, int day, int endMonth, int endDay)
    {
        var def = FestivalCatalog.Get(FestivalCatalog.EscaladeId)!;
        var (from, to) = def.Schedule[0];
        var span = FestivalCalendar.SpanOf(from, to, year);
        Assert.NotNull(span);
        Assert.Equal(new DateOnly(year, month, day), span!.Value.Start);
        Assert.Equal(DayOfWeek.Saturday, span.Value.Start.DayOfWeek);
        Assert.Equal(new DateOnly(year, endMonth, endDay), span.Value.End);
        Assert.Equal(1, span.Value.Day(span.Value.Start));
    }

    [Fact]
    public void The_course_is_six_gates_and_the_line()
    {
        var site = Site();
        var path = EscaladeRules.Path(site);
        Assert.Equal(EscaladeRules.Gates + 1, path.Length);
        for (int k = 0; k < EscaladeRules.Gates; k++)
            Assert.Equal(EscaladeRules.RingRadius, Math.Sqrt(Math.Pow(path[k].E - site.E, 2) + Math.Pow(path[k].N - site.N, 2)), 6);
        Assert.Equal(EscaladeRules.StartAt(site), path[EscaladeRules.Gates]);
        // gates are far enough apart that one body cannot pass two at once
        for (int k = 1; k < path.Length; k++)
            Assert.True(EscaladeRules.Dist(path[k].E, path[k].N, path[k - 1]) > 2 * EscaladeRules.GateReach);
        // and the first gate is not within the line's reach
        Assert.False(EscaladeRules.NearStart(site, path[0].E, path[0].N));
    }

    [Fact]
    public void The_course_has_a_sensible_length_par_and_limit()
    {
        var site = Site();
        double len = EscaladeRules.CourseLength(site), par = EscaladeRules.ParSeconds(site);
        Assert.InRange(len, 300, 500);
        Assert.InRange(par, 50, 100);
        Assert.True(EscaladeRules.MaxSeconds(site) >= 180);
        // a teleport is not a run; a sprint is
        Assert.False(EscaladeRules.Plausible(site, 3));
        Assert.True(EscaladeRules.Plausible(site, len / EscaladeRules.SprintSpeed + 0.1));
        Assert.True(EscaladeRules.Plausible(site, 3, 1e9));
    }

    [Fact]
    public void The_course_pays_for_speed()
    {
        const double Par = 70;
        Assert.Equal(60, EscaladeRules.CoursePay(40, Par));          // quicker than 80 % of the par
        Assert.Equal(60, EscaladeRules.CoursePay(Par * 0.8, Par));
        Assert.Equal(20, EscaladeRules.CoursePay(Par * 1.5, Par));   // a slow run still pays the base
        Assert.Equal(20, EscaladeRules.CoursePay(Par * 3, Par));
        int onPar = EscaladeRules.CoursePay(Par, Par);
        Assert.InRange(onPar, 21, 59);
        Assert.True(EscaladeRules.CoursePay(Par * 1.1, Par) < onPar);
        // the best run fits the game's day cap twice over
        Assert.True(2 * (EscaladeRules.CourseBatzen + EscaladeRules.SpeedBatzen) <= EscaladeRules.CourseDayCap);
    }

    [Fact]
    public void The_pair_is_the_youngest_and_the_oldest_account()
    {
        var a = new EscaladeRules.Member("a", 100, "Anna");
        var b = new EscaladeRules.Member("b", 500, "Ben");
        var c = new EscaladeRules.Member("c", 300, "Cleo");
        var (young, old) = EscaladeRules.Pair([c, a, b])!.Value;
        Assert.Equal("b", young.Key);
        Assert.Equal("a", old.Key);
        Assert.Null(EscaladeRules.Pair([]));
    }

    [Fact]
    public void A_lone_player_is_both_and_two_players_are_never_the_same_one()
    {
        var solo = new EscaladeRules.Member("a", 100, "Anna");
        var (y, o) = EscaladeRules.Pair([solo])!.Value;
        Assert.Equal(solo, y);
        Assert.Equal(solo, o);
        // equal creation times: the key breaks the tie, in a way every caller agrees on
        var x = new EscaladeRules.Member("x", 100, "Xavier");
        var z = new EscaladeRules.Member("z", 100, "Zoe");
        var (y1, o1) = EscaladeRules.Pair([x, z])!.Value;
        var (y2, o2) = EscaladeRules.Pair([z, x])!.Value;
        Assert.NotEqual(y1.Key, o1.Key);
        Assert.Equal(y1, y2);
        Assert.Equal(o1, o2);
    }

    [Fact]
    public void The_ground_is_set_out_apart()
    {
        var site = Site();
        var m = EscaladeRules.MarmiteAt(site);
        Assert.False(EscaladeRules.NearPot(site, m.E, m.N));
        Assert.False(EscaladeRules.NearStart(site, m.E, m.N));
        Assert.True(EscaladeRules.NearMarmite(site, m.E, m.N + 2));
        var p = EscaladeRules.PotAt(site);
        Assert.False(EscaladeRules.NearMarmite(site, p.E, p.N));
        Assert.True(EscaladeRules.NearPot(site, p.E, p.N));
        // the pot and the line are each within the crew's reach of the Marmite's square
        Assert.True(EscaladeRules.Dist(p.E, p.N, m) < EscaladeRules.CrewReach);
    }

    [Fact]
    public void Pay_stays_inside_the_festival()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.EscaladeId)!;
        // what the festival can pay one player: both days of every game, at best
        Assert.Equal(EscaladeRules.MarmiteDayCap, EscaladeRules.SmashBatzen + EscaladeRules.SmashPairBatzen);
        Assert.Equal(EscaladeRules.SoupeBatzen, EscaladeRules.SoupeDayCap);
        Assert.True(def.MilestoneTotal <= def.TokenCap * FestivalDef.MilestoneShare);
        Assert.Equal("Ainsi périssent les ennemis de la République!", EscaladeRules.Saying);
    }

    [Fact]
    public void The_items_are_in_the_festivals_block()
    {
        Assert.Equal(503, (int)ItemId.MarmiteChocolate);
        Assert.Equal(504, (int)ItemId.SoupeEscalade);
        Assert.InRange((int)ItemId.MarmiteChocolate, 500, 539);
        Assert.InRange((int)ItemId.SoupeEscalade, 500, 539);
    }
}
