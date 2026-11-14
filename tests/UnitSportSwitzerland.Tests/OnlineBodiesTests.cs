using System.Collections.Generic;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Net.Regions;
using UnitSport.Terrain.Format;
using Xunit;
using static UnitSport.Net.Regions.RegionPlanner;

namespace UnitSport.Tests;

/// <summary>
/// Online between bodies (#1852, C3b): the body on the wire (<c>GlobalPos.WireBody</c>/<c>FromWire</c>), the server's regions per
/// body (<c>RegionPlanner</c>) and the line of sight round another body's sphere (<c>BodySight</c>), all linked in.
/// </summary>
public class OnlineBodiesTests
{
    private static readonly GlobalPos Home = GlobalPos.FromLv95(2600000, 1200000, 0);

    private static GlobalPos Moon(double latDeg, double lonDeg, double h = 0) =>
        GlobalPos.On(BodyId.Moon, CelestialBody.Moon.Shape.ToEcef(new Geodetic(latDeg, lonDeg, h)));

    [Fact]
    public void A_place_on_the_Moon_keeps_its_body_on_the_wire_and_an_Earth_one_is_body_0()
    {
        var moon = Moon(26.13, 3.63, -1500);
        var (x, y, z) = moon.ToWire();
        var back = GlobalPos.FromWire(x, y, z, (BodyId)moon.WireBody);
        Assert.Equal(BodyId.Moon, back.Body);
        Assert.Equal(moon.Ecef.X, back.Ecef.X);
        Assert.Equal(moon.Ecef.Z, back.Ecef.Z);
        Assert.Equal(0, Home.WireBody);
        var (hx, hy, hz) = Home.ToWire();
        Assert.Equal(BodyId.Earth, GlobalPos.FromWire(hx, hy, hz).Body);
    }

    [Fact]
    public void Bodies_on_the_Moon_never_join_home_nor_an_Earth_region_and_get_a_frame_on_the_Moon()
    {
        int next = 1;
        // a Moon body whose body-fixed doubles lie 1.7 Mm from the centre: as Earth doubles it would be inside the Earth
        var bodies = new List<Body> { new(1, Home, None), new(2, Moon(26.13, 3.63), None), new(3, Moon(26.13, 3.632), None) };
        var plan = RegionPlanner.Decide(bodies, new List<Region>(), Home, ref next, Anchoring.Earth);
        Assert.Equal(Main, plan.RegionOf[1]);
        Assert.Equal(plan.RegionOf[2], plan.RegionOf[3]);
        Assert.NotEqual(Main, plan.RegionOf[2]);
        var made = Assert.Single(plan.Create);
        Assert.Equal(BodyId.Moon, made.Anchor.Body);
        Assert.InRange(made.Anchor.Geodetic.H, -1e-3, 1e-3);
        Assert.InRange(made.Anchor.Geodetic.LatDeg, 26.12, 26.14);
    }

    [Fact]
    public void A_Moon_region_is_never_claimed_by_bodies_on_the_Earth()
    {
        int next = 5;
        var moonRegion = new Region(4, Moon(0, 0));
        // an Earth body far from home that the planner last put in region 4 (a stale id): it gets a frame of its own
        var far = GlobalPos.FromLv95(2900000, 1200000, 500);
        var plan = RegionPlanner.Decide(new List<Body> { new(1, far, 4) }, new List<Region> { moonRegion }, Home, ref next, Anchoring.Earth);
        Assert.NotEqual(4, plan.RegionOf[1]);
        Assert.Equal(BodyId.Earth, Assert.Single(plan.Create).Anchor.Body);
        Assert.Contains(4, plan.Drop);
    }

    [Fact]
    public void The_Moons_sphere_hides_the_far_side_and_not_a_neighbour()
    {
        // two walkers 50 m apart see each other; one on the far side is hidden
        Assert.True(BodySight.Clear(Moon(0, 0, 2), Moon(0, 0.0017, 2)));
        Assert.False(BodySight.Clear(Moon(0, 0, 2), Moon(0, 180, 2)));
        // a shuttle 100 km up over a walker 5° away (150 km round the curve) is in sight
        Assert.True(BodySight.Clear(Moon(0, 0, 2), Moon(0, 5, 100_000)));
        // two players down a basin 6 km deep, 20 km apart, see each other across its floor
        Assert.True(BodySight.Clear(Moon(-50, 170, -6000), Moon(-50, 170.6, -6000)));
        // never across two bodies
        Assert.False(BodySight.Clear(Home, Moon(0, 0)));
    }
}
