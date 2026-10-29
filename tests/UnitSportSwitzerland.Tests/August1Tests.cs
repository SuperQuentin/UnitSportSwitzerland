using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.August1;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The 1 August (#1355): the festival's dates and site, the Höhenfeuer's hash and hours, the lakes and their launch points, the lampion walk's ring and pay, the volley. Pure.</summary>
public class August1Tests
{
    private static ResolvedSite Site() => new("the test field", 2600000, 1200000, 120, false, null, "1 August at the test field");

    [Fact]
    public void The_festival_is_in_the_catalogue_and_sound()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.August1Id);
        Assert.NotNull(def);
        Assert.Empty(def!.Problems());
        Assert.Equal("august1", def.Id);
        Assert.Equal(2, def.Length);
        Assert.Equal(August1Rules.Lakes.Length, def.Sites.Length);
        foreach (string g in new[] { August1Rules.LampionGame, August1Rules.ShowGame }) Assert.True(def.IsOpen(g, 1));
        Assert.Equal(180, def.TokenCap);
        Assert.Equal(ClosingShow.Bonfires, def.Show);
    }

    [Theory]
    [InlineData(2026, 7, 31, 8, 1)]
    [InlineData(2027, 7, 31, 8, 1)]
    [InlineData(2028, 7, 31, 8, 1)]
    public void It_runs_from_31_july_to_1_august(int year, int month, int day, int endMonth, int endDay)
    {
        var def = FestivalCatalog.Get(FestivalCatalog.August1Id)!;
        var (from, to) = def.Schedule[0];
        var span = FestivalCalendar.SpanOf(from, to, year);
        Assert.NotNull(span);
        Assert.Equal(new DateOnly(year, month, day), span!.Value.Start);
        Assert.Equal(new DateOnly(year, endMonth, endDay), span.Value.End);
        Assert.Equal(1, span.Value.Day(span.Value.Start));
        Assert.Equal(2, span.Value.Day(span.Value.End));
    }

    [Fact]
    public void The_hoehenfeuer_light_one_summit_in_four_under_2500_m()
    {
        int lit = 0;
        const int Total = 20000;
        for (ulong h = 1; h <= Total; h++)
            if (August1Rules.Lit(h * 0x9E3779B1UL, 1800f)) lit++;
        Assert.InRange(lit / (double)Total, 0.23, 0.27);
        // never at or above the ceiling, whatever the hash
        for (ulong h = 1; h <= 2000; h++) Assert.False(August1Rules.Lit(h, 2500f));
        for (ulong h = 1; h <= 2000; h++) Assert.False(August1Rules.Lit(h, 3400f));
        // a summit is lit or not for good: the same on every peer and every call
        for (ulong h = 1; h <= 500; h++) Assert.Equal(August1Rules.Lit(h, 900f), August1Rules.Lit(h, 900f));
        // a check's salt moves the choice
        Assert.Contains(Enumerable.Range(1, 200), h => August1Rules.Lit((ulong)h, 900f, 0) != August1Rules.Lit((ulong)h, 900f, 1));
    }

    [Fact]
    public void The_fires_burn_from_21_to_5_and_the_walk_needs_the_dark()
    {
        Assert.True(August1Rules.Burning(21));
        Assert.True(August1Rules.Burning(23.99));
        Assert.True(August1Rules.Burning(0));
        Assert.True(August1Rules.Burning(4.99));
        Assert.False(August1Rules.Burning(5));
        Assert.False(August1Rules.Burning(12));
        Assert.False(August1Rules.Burning(20.99));
        Assert.True(August1Rules.IsNight(20.5));
        Assert.True(August1Rules.IsNight(3));
        Assert.False(August1Rules.IsNight(20.4));
        Assert.False(August1Rules.IsNight(5.5));
    }

    [Fact]
    public void The_lake_show_starts_when_the_sky_crosses_22_and_not_after_midnight()
    {
        Assert.True(August1Rules.ShowStarts(21.9, 22.05));
        Assert.True(August1Rules.ShowStarts(null, 22.6));
        Assert.False(August1Rules.ShowStarts(22.1, 22.5));
        Assert.False(August1Rules.ShowStarts(null, 21.5));
        Assert.False(August1Rules.ShowStarts(23.9, 0.1));
        Assert.False(August1Rules.ShowStarts(null, 2));
    }

    [Fact]
    public void Seven_lakes_sit_in_switzerland_with_their_ground_inland_and_their_barge_on_the_water()
    {
        Assert.Equal(7, August1Rules.Lakes.Length);
        foreach (var lake in August1Rules.Lakes)
        {
            var (qe, qn) = lake.Quay;
            Assert.InRange(qe, 2480000, 2840000);
            Assert.InRange(qn, 1070000, 1300000);
            var (se, sn) = lake.Site;
            var (be, bn) = lake.Barge;
            Assert.Equal(August1Rules.Inland, August1Rules.Dist(se, sn, (qe, qn)), 3);
            Assert.Equal(August1Rules.BargeOut, August1Rules.Dist(be, bn, (qe, qn)), 3);
            // the ground, the quay and the barge are in a line along the bearing
            Assert.Equal(August1Rules.Inland + August1Rules.BargeOut, August1Rules.Dist(se, sn, (be, bn)), 3);
        }
        // Zürich is north-east of Genève
        var (ze, zn) = August1Rules.Lakes[4].Quay;
        var (ge, gn) = August1Rules.Lakes[0].Quay;
        Assert.InRange(ze - ge, 150000, 190000);
        Assert.InRange(zn - gn, 110000, 150000);
    }

    [Fact]
    public void The_show_leaves_from_the_nearest_lakes_barge_in_a_lake_town_and_north_of_the_site_elsewhere()
    {
        var lucerne = August1Rules.Lakes[5];
        var (le, ln) = lucerne.Site;
        var atLake = new ResolvedSite(lucerne.Name, le, ln, 120, false, null, "x");
        var (be, bn) = lucerne.Barge;
        var near = August1Rules.LaunchFor(atLake, le, ln);
        Assert.Equal(be, near.E, 3);
        Assert.Equal(bn, near.N, 3);
        Assert.Equal(lucerne.Bearing, near.Bearing, 6);
        // a viewer standing in Lugano sees Lugano's show, whichever lake town the festival site is
        var (ue, un) = August1Rules.Lakes[6].Barge;
        Assert.Equal(August1Rules.Lakes[6].Name, August1Rules.LaunchFor(atLake, ue + 500, un).Name);
        // a relocated festival (a town with no lake in the table) or a check's fixture: 200 m north of the middle, the fan to the north
        var elsewhere = Site();
        var l = August1Rules.LaunchFor(elsewhere, 0, 0);
        Assert.Equal(elsewhere.E, l.E);
        Assert.Equal(elsewhere.N + 200, l.N);
        Assert.Equal(0, l.Bearing);
    }

    [Fact]
    public void The_walk_is_ten_stops_on_a_ring_and_the_post()
    {
        var site = Site();
        var path = August1Rules.Path(site);
        Assert.Equal(August1Rules.Waypoints + 1, path.Length);
        for (int k = 0; k < August1Rules.Waypoints; k++)
            Assert.Equal(August1Rules.RingRadius, August1Rules.Dist(path[k].E, path[k].N, (site.E, site.N)), 6);
        Assert.Equal(August1Rules.PostAt(site), path[August1Rules.Waypoints]);
        Assert.Equal(August1Rules.RingRadius, August1Rules.Dist(path[^1].E, path[^1].N, (site.E, site.N)), 6);
        // one body cannot pass two stops at once, and the first stop is out of the post's reach
        for (int k = 1; k < path.Length; k++)
            Assert.True(August1Rules.Dist(path[k].E, path[k].N, path[k - 1]) > 2 * August1Rules.WaypointReach);
        Assert.False(August1Rules.NearPost(site, path[0].E, path[0].N));
        // the stops are further apart than the party may spread: the leader at one is out of reach of someone left at the last
        Assert.True(August1Rules.Dist(path[0].E, path[0].N, path[August1Rules.Waypoints]) > August1Rules.Together);
        // the stands are inside the ring and apart
        Assert.True(August1Rules.Dist(August1Rules.LampionStandAt(site).E, August1Rules.LampionStandAt(site).N, (site.E, site.N)) < August1Rules.RingRadius - 20);
        Assert.True(August1Rules.Dist(August1Rules.LampionStandAt(site).E, August1Rules.LampionStandAt(site).N, August1Rules.FireworkStandAt(site)) > 2 * August1Rules.StandReach);
    }

    [Fact]
    public void The_walk_has_a_stroll_par_a_limit_and_pays_by_friends()
    {
        var site = Site();
        double len = August1Rules.LoopLength(site);
        Assert.InRange(len, 540, 600);
        Assert.Equal(len / 1.2, August1Rules.ParSeconds(site), 6);
        Assert.True(August1Rules.MaxSeconds(site) >= 3 * August1Rules.ParSeconds(site));
        Assert.False(August1Rules.Plausible(site, 30));
        Assert.True(August1Rules.Plausible(site, August1Rules.ParSeconds(site)));
        Assert.True(August1Rules.Plausible(site, 1, 1e9));
        Assert.Equal(25, August1Rules.WalkPay(1));
        Assert.Equal(35, August1Rules.WalkPay(2));
        Assert.Equal(45, August1Rules.WalkPay(3));
        Assert.Equal(45, August1Rules.WalkPay(8));
        Assert.True(August1Rules.WalkPay(3) <= August1Rules.WalkDayCap);
    }

    [Fact]
    public void A_volley_is_forty_seconds_of_low_shells_from_a_seed()
    {
        var a = FireworkShow.Volley(1234);
        var b = FireworkShow.Volley(1234);
        Assert.Equal(a.Shells, b.Shells);
        Assert.Equal(FireworkShow.VolleySeconds, a.Duration);
        Assert.Equal(15, a.Shells.Length);
        Assert.All(a.Shells, s => Assert.InRange(s.Height, 55f, 95f));
        Assert.All(a.Shells, s => Assert.InRange(s.T, 2, FireworkShow.VolleySeconds));
        Assert.Equal(ShellKind.SwissCross, a.Shells[^1].Kind);
        Assert.NotEqual(a.Shells, FireworkShow.Volley(1235).Shells);
        for (int i = 1; i < a.Shells.Length; i++) Assert.True(a.Shells[i].T >= a.Shells[i - 1].T);
        // a volley stays near the lighter: no shell further than 80 m out
        Assert.All(a.Shells, s => Assert.True(Math.Sqrt(s.East * s.East + s.North * s.North) < 80));
    }

    [Fact]
    public void Own_volleys_have_their_own_seeds_and_the_stands_have_a_price()
    {
        var seeds = Enumerable.Range(1, 50).Select(n => August1Rules.OwnSeed("august1-2027", n)).ToHashSet();
        Assert.Equal(50, seeds.Count);
        Assert.NotEqual(August1Rules.OwnSeed("august1-2027", 1), FestivalShows.SeedOf("august1-2027", 1));
        Assert.Equal(20, August1Rules.BoxPrice);
        Assert.Equal(3, August1Rules.BoxesPerDay);
        Assert.True(August1Rules.BoxPrice * August1Rules.BoxesPerDay <= FestivalCatalog.Get(FestivalCatalog.August1Id)!.TokenCap);
    }

    [Fact]
    public void The_items_are_in_the_1_august_block()
    {
        Assert.Equal(520, (int)ItemId.Lampion);
        Assert.Equal(521, (int)ItemId.FireworkBox);
        Assert.InRange((int)ItemId.Lampion, 520, 524);
        Assert.InRange((int)ItemId.FireworkBox, 520, 524);
    }
}
