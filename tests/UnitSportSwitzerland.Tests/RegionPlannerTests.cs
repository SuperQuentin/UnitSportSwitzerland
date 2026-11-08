using System.Collections.Generic;
using System.Linq;
using UnitSport.Core;
using UnitSport.Net.Regions;
using Xunit;
using static UnitSport.Net.Regions.RegionPlanner;

namespace UnitSport.Tests;

/// <summary>Which server region each body belongs to (#1456, src/Net/Regions/RegionPlanner.cs, linked in).</summary>
public class RegionPlannerTests
{
    private const double HomeE = 2600000, HomeN = 1200000;
    private static readonly GlobalPos Home = GlobalPos.FromLv95(HomeE, HomeN, 0);

    private static GlobalPos At(double km, double kmN = 0) => GlobalPos.FromLv95(HomeE + km * 1000, HomeN + kmN * 1000, 500);

    private static Plan Decide(List<Body> bodies, List<Region>? regions, ref int next) =>
        RegionPlanner.Decide(bodies, regions ?? new List<Region>(), Home, ref next);

    [Fact]
    public void Bodies_near_home_stay_in_the_main_region()
    {
        int next = 1;
        var plan = Decide(new() { new(1, At(0), None), new(2, At(15), None), new(3, At(-3, 12), Main) }, null, ref next);
        Assert.All(plan.RegionOf.Values, r => Assert.Equal(Main, r));
        Assert.Empty(plan.Create);
        Assert.Empty(plan.Drop);
    }

    [Fact]
    public void Two_bots_300_km_apart_get_two_frames_and_the_far_one_is_anchored_on_it()
    {
        int next = 1;
        var plan = Decide(new() { new(1, At(0), None), new(2, At(300), None) }, null, ref next);
        Assert.Equal(Main, plan.RegionOf[1]);
        Assert.Equal(1, plan.RegionOf[2]);
        var created = Assert.Single(plan.Create);
        Assert.Equal(GlobalPos.FromLv95(HomeE + 300000, HomeN, 0), created.Anchor);
        Assert.Equal(2, next);
    }

    [Fact]
    public void Earth_anchors_are_the_members_mean_on_the_ellipsoid()
    {
        // #1513: an ENU region's anchor is the members' ECEF mean put on the ellipsoid, no LV95 km in it
        int next = 1;
        var bodies = new List<Body> { new(1, At(300.2, 0.3), None), new(2, At(301.4, -0.5), None) };
        var plan = RegionPlanner.Decide(bodies, new List<Region>(), Home, ref next, Anchoring.Earth);
        var anchor = Assert.Single(plan.Create).Anchor;
        Assert.InRange(anchor.Geodetic.H, -1e-6, 1e-6);
        // over the members' midpoint (which is 500 m up): the level distance to it is under a millimetre
        var mid = GlobalPos.Midpoint(bodies[0].At, bodies[1].At);
        Assert.True(anchor.HorizontalDistanceTo(mid) < 0.001, $"{anchor.HorizontalDistanceTo(mid)} m");
        // the flat anchoring keeps today's whole km
        next = 1;
        var flat = RegionPlanner.Decide(bodies, new List<Region>(), Home, ref next);
        Assert.Equal(GlobalPos.FromLv95(HomeE + 301000, HomeN, 0), Assert.Single(flat.Create).Anchor);
    }

    [Fact]
    public void Earth_anchors_hold_far_from_Switzerland()
    {
        // Brest and the antipode's sliver: the Earth mean needs no LV95
        foreach (var (lat, lon) in new[] { (48.39, -4.49), (-47.0, -172.6) })
        {
            var p = GlobalPos.FromEcef(Terrain.Format.Ellipsoid.Wgs84.ToEcef(new Terrain.Format.Geodetic(lat, lon, 120)));
            var anchor = Centre(new[] { new Body(1, p, None), new Body(2, p.Moved(800, 300), None) }, new() { 0, 1 }, Anchoring.Earth);
            Assert.InRange(anchor.Geodetic.H, -1e-6, 1e-6);
            Assert.True(anchor.HorizontalDistanceTo(p.Moved(400, 150)) < 0.01);
        }
    }

    [Fact]
    public void Bodies_within_the_link_distance_share_a_region_far_from_home()
    {
        int next = 1;
        var plan = Decide(new() { new(1, At(300), None), new(2, At(301.5), None), new(3, At(302.9), None) }, null, ref next);
        Assert.Single(plan.Create);
        Assert.Equal(1, plan.RegionOf.Values.Distinct().Single());
    }

    [Fact]
    public void A_chain_out_of_home_stays_in_the_main_region()
    {
        int next = 1;
        // 19 km is home; 20.9 and 22.8 are linked to it in 1.9 km steps
        var plan = Decide(new() { new(1, At(19), None), new(2, At(20.9), None), new(3, At(22.8), None) }, null, ref next);
        Assert.All(plan.RegionOf.Values, r => Assert.Equal(Main, r));
    }

    [Fact]
    public void Members_part_only_past_the_split_distance()
    {
        var regions = new List<Region> { new(5, GlobalPos.FromLv95(HomeE + 300000, HomeN, 0)) };
        int next = 6;
        var together = Decide(new() { new(1, At(300), 5), new(2, At(305), 5) }, regions, ref next);
        Assert.Equal(5, together.RegionOf[1]);
        Assert.Equal(5, together.RegionOf[2]);
        Assert.Empty(together.Create);

        var parted = Decide(new() { new(1, At(300), 5), new(2, At(306.5), 5) }, regions, ref next);
        Assert.Single(parted.Create);
        Assert.NotEqual(parted.RegionOf[1], parted.RegionOf[2]);
        Assert.Contains(5, new[] { parted.RegionOf[1], parted.RegionOf[2] });
    }

    [Fact]
    public void A_split_region_stays_with_its_larger_part()
    {
        var regions = new List<Region> { new(5, GlobalPos.FromLv95(HomeE + 300000, HomeN, 0)) };
        int next = 6;
        var plan = Decide(new() { new(1, At(300), 5), new(2, At(300.5), 5), new(3, At(320), 5) }, regions, ref next);
        Assert.Equal(5, plan.RegionOf[1]);
        Assert.Equal(5, plan.RegionOf[2]);
        Assert.Equal(6, plan.RegionOf[3]);
    }

    [Fact]
    public void Two_regions_that_meet_merge_into_the_one_most_members_are_in()
    {
        var regions = new List<Region> { new(5, GlobalPos.FromLv95(HomeE + 300000, HomeN, 0)), new(7, GlobalPos.FromLv95(HomeE + 304000, HomeN, 0)) };
        int next = 8;
        var plan = Decide(new() { new(1, At(302), 5), new(2, At(302.5), 5), new(3, At(303.5), 7) }, regions, ref next);
        Assert.All(plan.RegionOf.Values, r => Assert.Equal(5, r));
        Assert.Equal(new[] { 7 }, plan.Drop);
        Assert.Empty(plan.Create);
    }

    [Fact]
    public void A_region_whose_bodies_left_home_far_behind_is_replaced_by_one_at_their_centre()
    {
        var regions = new List<Region> { new(5, GlobalPos.FromLv95(HomeE + 300000, HomeN, 0)) };
        int next = 6;
        // a convoy 9 km on, still within the split distance of each other
        var plan = Decide(new() { new(1, At(309), 5), new(2, At(309.4), 5) }, regions, ref next);
        var created = Assert.Single(plan.Create);
        Assert.Equal(GlobalPos.FromLv95(HomeE + 309000, HomeN, 0), created.Anchor);
        Assert.Equal(new[] { 5 }, plan.Drop);
        Assert.All(plan.RegionOf.Values, r => Assert.Equal(6, r));
    }

    [Fact]
    public void A_wide_cluster_centred_on_its_anchor_does_not_re_anchor_every_time()
    {
        var regions = new List<Region> { new(5, GlobalPos.FromLv95(HomeE + 300000, HomeN, 0)) };
        int next = 6;
        var bodies = Enumerable.Range(0, 19).Select(i => new Body(i, At(291 + i * 1.0), 5)).ToList();   // 291..309 km: 9 km out, centred
        var plan = Decide(bodies, regions, ref next);
        Assert.Empty(plan.Create);
        Assert.All(plan.RegionOf.Values, r => Assert.Equal(5, r));
    }

    [Fact]
    public void A_region_nobody_is_in_any_more_is_dropped_but_never_the_main_one()
    {
        var regions = new List<Region> { new(Main, Home), new(5, GlobalPos.FromLv95(HomeE + 300000, HomeN, 0)) };
        int next = 6;
        var plan = Decide(new() { new(1, At(1), Main) }, regions, ref next);
        Assert.Equal(new[] { 5 }, plan.Drop);
        var none = Decide(new(), regions, ref next);
        Assert.Equal(new[] { 5 }, none.Drop);
    }

    [Fact]
    public void Leaving_home_hands_a_body_to_a_region_of_its_own_only_past_the_release_distance()
    {
        int next = 1;
        var stay = Decide(new() { new(1, At(24), Main) }, null, ref next);
        Assert.Equal(Main, stay.RegionOf[1]);
        var gone = Decide(new() { new(1, At(27), Main) }, null, ref next);
        Assert.Equal(1, gone.RegionOf[1]);
        var newcomer = Decide(new() { new(2, At(24), None) }, null, ref next);
        Assert.NotEqual(Main, newcomer.RegionOf[2]);
    }
}
