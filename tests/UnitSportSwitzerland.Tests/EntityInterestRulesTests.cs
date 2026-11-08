using System.Collections.Generic;
using UnitSport.Net;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Who is sent which vehicle or item (src/Net/EntityInterestRules.cs, #689).</summary>
public class EntityInterestRulesTests
{
    [Fact]
    public void A_car_is_sent_from_about_two_kilometres_an_airliner_much_further()
    {
        float car = EntityInterestRules.VehicleRange(4.4f, 60000f, 75f);
        Assert.InRange(car, 1800f, 2600f);
        float a320 = EntityInterestRules.VehicleRange(37f, 60000f, 75f);
        Assert.True(a320 > 6000f);
        // never further than the viewer draws
        Assert.Equal(1000f, EntityInterestRules.VehicleRange(37f, 1000f, 75f));
        // a zoomed-in view sees further
        Assert.True(EntityInterestRules.VehicleRange(4.4f, 60000f, 30f) > car);
        // a tiny thing is still sent close by
        Assert.Equal(EntityInterestRules.AlwaysWithin, EntityInterestRules.VehicleRange(0.1f, 60000f, 75f));
    }

    [Fact]
    public void Fade_is_over_before_the_entity_leaves()
    {
        // drawn to FadeEnd, fully gone by FadeEnd + FadeMargin, despawned beyond LeaveFactor
        Assert.True(EntityInterestRules.FadeEnd + EntityInterestRules.FadeMargin < 1f);
        Assert.True(EntityInterestRules.LeaveFactor > 1f);
        // a dropped item is drawn to 90 m: it is always there before it could be seen
        Assert.True(EntityInterestRules.ItemRange > 90f);
    }

    [Fact]
    public void Hysteresis_keeps_an_entity_at_the_edge()
    {
        Assert.True(EntityInterestRules.Keep(1100, 1000, was: true));
        Assert.False(EntityInterestRules.Keep(1100, 1000, was: false));
        Assert.False(EntityInterestRules.Keep(1300, 1000, was: true));
        Assert.True(EntityInterestRules.Keep(100, 10, was: false));
    }

    [Fact]
    public void Look_ahead_follows_travel_but_not_a_teleport()
    {
        var (e, n) = EntityInterestRules.Ahead(1000, 0, 900, 0, 1);
        Assert.Equal(1200, e, 3);
        Assert.Equal(0, n, 3);
        Assert.Equal((5000.0, 0.0), EntityInterestRules.Ahead(5000, 0, 0, 0, 1));
        Assert.Equal((1.0, 2.0), EntityInterestRules.Ahead(1, 2, 0, 0, 0));
    }

    [Fact]
    public void Newcomers_come_nearest_first_within_the_budget_and_kept_ones_stay()
    {
        var candidates = new List<EntityInterestRules.Candidate>
        {
            new(0, 900, 1000, false),
            new(1, 100, 1000, false),
            new(2, 500, 1000, false),
            new(3, 1100, 1000, true),    // already there, inside the leave range
            new(4, 5000, 1000, true),    // already there, now too far
        };
        var keep = new List<int>();
        EntityInterestRules.Select(candidates, keep, new List<EntityInterestRules.Candidate>(), budget: 2);
        Assert.Equal(new[] { 3, 1, 2 }, keep);
    }

    [Fact]
    public void Cube_cell_rings_hold_everything_within_reach()
    {
        // #1513: entities are bucketed by level-13 cube cell; the rings round a viewer's cell (across face edges) must
        // hold every place within reach, at Bern, astride a face edge and near a cube corner
        var places = new List<UnitSport.Core.GlobalPos>
        {
            UnitSport.Core.GlobalPos.FromLv95(2600000, 1200000, 500),
            OnFace(0, 0.99995, 0.3), OnFace(0, -0.3, -0.99995), OnFace(0, 0.97, 0.97), OnFace(3, -0.97, 0.97),
        };
        foreach (double reach in new[] { 150.0, 2400, 7000 })
        {
            int rings = EntityInterestRules.CellRings(reach);
            foreach (var viewer in places)
            {
                var centre = CubeKey.FromEcef(viewer.Ecef, EntityInterestRules.CellLevel);
                var found = new HashSet<CubeKey>();
                for (int dx = -rings; dx <= rings; dx++)
                    for (int dy = -rings; dy <= rings; dy++)
                        if (centre.Neighbour(dx, dy) is { } k) found.Add(k);
                for (int b = 0; b < 72; b++)
                    foreach (double f in new[] { 0.5, 0.999 })
                    {
                        double a = b * 5 * System.Math.PI / 180, d = reach * f;
                        var at = viewer.Moved(d * System.Math.Sin(a), d * System.Math.Cos(a));
                        Assert.Contains(CubeKey.FromEcef(at.Ecef, EntityInterestRules.CellLevel), found);
                    }
            }
        }
    }

    private static UnitSport.Core.GlobalPos OnFace(int face, double u, double v) =>
        UnitSport.Core.GlobalPos.FromEcef(CubeSphere.ToEcef(face, u, v, 400));
}
