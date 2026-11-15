using System;
using System.Linq;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The place search's places on other bodies (#2039).</summary>
public class BodyPlacesTests
{
    [Fact]
    public void The_moon_lists_its_colonies_sites_and_outposts()
    {
        var moon = BodyPlaces.For(BodyId.Moon);
        Assert.Equal(MoonColonies.All.Length + MoonSites.All.Length + MoonOutposts.All.Length, moon.Count);
        foreach (var c in MoonColonies.All) Assert.Contains(moon, p => p.Name == c.Name && p.Kind.Contains("colony"));
        Assert.Contains(moon, p => p.Kind == "Apollo landing site");
    }

    [Fact]
    public void A_colony_lands_outside_its_modules()
    {
        foreach (var c in MoonColonies.All)
        {
            var place = BodyPlaces.For(BodyId.Moon).First(p => p.Name == c.Name);
            var plan = ColonyPlan.For(c);
            // the frame is x east, z south: the landing's z is minus its north
            double x = place.LandEast, z = -place.LandNorth;
            foreach (var m in plan.Modules)
            {
                double reach = Math.Max(m.Length, m.Width) / 2;
                double d = Math.Sqrt((x - m.X) * (x - m.X) + (z - m.Z) * (z - m.Z));
                Assert.True(d > reach * 0.75, $"{c.Name}: the landing is {d:F0} m from module {m.Id} ({m.Kind}, reach {reach:F0} m)");
            }
        }
    }

    [Fact]
    public void Every_body_with_places_has_them_on_the_globe()
    {
        foreach (var b in BodyPlaces.Bodies)
        {
            var list = BodyPlaces.For(b);
            Assert.NotEmpty(list);
            foreach (var p in list)
            {
                Assert.Equal(b, p.Body);
                Assert.InRange(p.LatDeg, -90, 90);
                Assert.InRange(p.LonDeg, -180, 360);
                Assert.False(string.IsNullOrWhiteSpace(p.Name));
            }
            Assert.Equal(list.Count, list.Select(p => p.Name).Distinct().Count());
        }
        Assert.Empty(BodyPlaces.For(BodyId.Earth));
        Assert.Empty(BodyPlaces.For(BodyId.Jupiter));
    }

    [Fact]
    public void Search_matches_every_word_any_case()
    {
        var hits = BodyPlaces.Search(BodyId.Mars, "olympus MONS");
        Assert.Single(hits);
        Assert.Equal("Olympus Mons", hits[0].Name);
        Assert.Equal(BodyPlaces.For(BodyId.Mars).Count, BodyPlaces.Search(BodyId.Mars, "  ").Count);
        Assert.Contains(BodyPlaces.Search(BodyId.Mars, "rover"), p => p.Name.Contains("Curiosity"));
        Assert.Empty(BodyPlaces.Search(BodyId.Mars, "zermatt"));
    }
}
