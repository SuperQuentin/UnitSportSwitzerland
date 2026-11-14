using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// #1926: a site's materials keep a way in for a machine (#619) that is clear of the roads too, and <c>fixture:site</c> has a
/// topped-out site whose pallets a machine can come at under flat (<c>tools/sitepalletnetcheck.sh</c>). Planned as
/// <c>SitePlans.Of</c> plans them, from the fixture's own tiles.
/// </summary>
[Collection(UnitSport.Tests.FixtureWorld.Collection)]
public class SiteFixtureWayTests
{
    private static async Task<List<(ConstructionSite Site, SiteObstacles Obstacles)>> Sites()
    {
        var source = FixtureChunkSource.Create("site", 2583000, 1113000)!;
        var sites = new List<(ConstructionSite, SiteObstacles)>();
        foreach (var id in source.Tiles)
        {
            if (await source.LoadBuildingsAsync(id) is not { } tile) continue;
            var roads = await source.LoadRoadsAsync(id);
            var map = BuildingTypes.For(tile);
            for (int i = 0; i < tile.Buildings.Count; i++)
            {
                var b = tile.Buildings[i];
                if (b.Kind != BuildingKind.UnderConstruction || map.Boxes[i] is not { } box) continue;
                // the street point, as BuildingFootprint.StreetNear finds it on this one straight road
                Vector2? street = null;
                float best = 60f;
                foreach (var s in roads!.Segments)
                    for (int k = 0; k + 1 < s.PointCount; k++)
                    {
                        var a = new Vector2(s.Points[k * 3], s.Points[k * 3 + 2]);
                        var ac = new Vector2(s.Points[k * 3 + 3], s.Points[k * 3 + 5]) - a;
                        var f = a + ac * Mathf.Clamp((box.Center - a).Dot(ac) / ac.LengthSquared(), 0f, 1f);
                        if (f.DistanceTo(box.Center) < best) (best, street) = (f.DistanceTo(box.Center), f);
                    }
                var obstacles = new SiteObstacles(map.Boxes, i, roads, 0.3f);
                var key = new BuildingKey(tile.Id.E, tile.Id.N, i).ToString();
                if (ConstructionSites.Plan(key, b, box, street, obstacles.Blocked, obstacles.Undiggable) is { } site)
                    sites.Add((site, obstacles));
            }
        }
        return sites;
    }

    [Fact]
    public async Task The_fixture_has_a_topped_out_site_whose_materials_a_machine_can_reach()
    {
        var sites = await Sites();
        Assert.Contains(sites, x => x.Site.Phase == SitePhase.ToppedOut && ConstructionSites.MaterialsWayIn(x.Site, x.Obstacles.Blocked));
    }

    [Fact]
    public async Task A_way_kept_across_the_street_is_no_way_in()
    {
        // the shell site 24 m from the street: its yard reaches over the road, and the strip its materials face lies on it
        var (shell, obstacles) = (await Sites()).Single(x => x.Site.Phase == SitePhase.Shell);
        Assert.True(ConstructionSites.MaterialsWayIn(shell), "the geometric test alone sees a way");
        Assert.False(ConstructionSites.MaterialsWayIn(shell, obstacles.Blocked), "but it is the street");
    }
}
