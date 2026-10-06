using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// The building sites of one tile (#606): the Godot half of <see cref="ConstructionSites.Plan"/>,
/// which may read <c>src/Interiors</c> (the plan boxes, the street a front door would face) and the
/// road file, where the planner itself must stay in tier 0. A pure function of the tile's bytes,
/// like <c>SiteYards</c>: the server and every client work out the same sites.
/// </summary>
public static class SitePlans
{
    /// <summary>
    /// A plan point is taken when it is this close to a road's edge or another building: the
    /// hoarding's feet, a container's corner.
    /// </summary>
    private const float Clearance = 0.3f;

    /// <summary>Whether the tile has any building site, without planning them.</summary>
    public static bool HasSite(BuildingTile tile)
    {
        foreach (var b in tile.Buildings)
            if (b.Kind == BuildingKind.UnderConstruction) return true;
        return false;
    }

    /// <summary>Every building site of the tile, in building order, each keyed by its building.</summary>
    public static List<ConstructionSite> For(BuildingTile tile, RoadTile? roads)
    {
        var sites = new List<ConstructionSite>();
        if (!HasSite(tile)) return sites;
        var map = BuildingTypes.For(tile);
        for (int i = 0; i < tile.Buildings.Count; i++)
        {
            var b = tile.Buildings[i];
            if (b.Kind != BuildingKind.UnderConstruction || map.Boxes[i] is not { } box) continue;
            var key = new BuildingKey(tile.Id.E, tile.Id.N, i).ToString();
            var street = BuildingFootprint.StreetNear(roads, box.Center);
            var obstacles = new SiteObstacles(map.Boxes, i, roads, Clearance);
            var site = ConstructionSites.Plan(key, b, box, street, obstacles.Blocked);
            if (site != null) sites.Add(site);
        }
        return sites;
    }
}
