using Godot;
using UnitSport.Interiors;
using UnitSport.Items;
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
        for (int i = 0; i < tile.Buildings.Count; i++)
            if (Of(tile, roads, i) is { } site) sites.Add(site);
        return sites;
    }

    /// <summary>The building site of one building of the tile, as <see cref="For"/> plans it; null if it is none.</summary>
    public static ConstructionSite? Of(BuildingTile tile, RoadTile? roads, int index)
    {
        if (index < 0 || index >= tile.Buildings.Count) return null;
        var b = tile.Buildings[index];
        var map = BuildingTypes.For(tile);
        if (b.Kind != BuildingKind.UnderConstruction || map.Boxes[index] is not { } box) return null;
        var key = new BuildingKey(tile.Id.E, tile.Id.N, index).ToString();
        var street = BuildingFootprint.StreetNear(roads, box.Center);
        var obstacles = new SiteObstacles(map.Boxes, index, roads, Clearance);
        // a pit (#619) keeps off a road's sidewalks too, the yard off its carriageway
        return ConstructionSites.Plan(key, b, box, street, obstacles.Blocked, obstacles.Undiggable);
    }

    /// <summary>
    /// A site's pallets of bricks and cement (#615), as its dressing lays them out
    /// (<see cref="SitePalletSpot"/>), in LV95 on the drawn ground: the pallets a machine with tines
    /// lifts, drawn by <c>PalletService</c> beside the yards' stacks. Their runners run along the
    /// materials zone, square to the plan box.
    /// </summary>
    public static IEnumerable<YardPallet> PalletsOf(BuildingTile tile, ConstructionSite site, ChunkGrid? grid)
    {
        foreach (var p in SiteShellBuilder.Dressing(tile, site, grid).Pallets)
        {
            var at = SiteShellBuilder.ToTile(site, p.Centre);
            // the runners' way in the tile (x east, z south, as the world's), and a pallet's yaw:
            // its runners are its +X, turned about +Y
            var runners = p.AlongX ? site.Box.AxisU : site.Box.AxisV;
            float yaw = Mathf.Atan2(-runners.Y, runners.X);
            yield return new YardPallet(site.Key, p.Slot, tile.Id.MinE + at.X, tile.Id.MaxN - at.Z, at.Y, yaw, p.Load, Site: true);
        }
    }

    /// <summary>A tile's yard containers (#627), every site's, for a client's index round the player.</summary>
    public static YardStock[] YardOfTile(IChunkSource source, TileId id) => YardOf(source, id, null);

    /// <summary>
    /// The server's answer to "what is yard container <paramref name="building"/>, index i" (#627):
    /// the site's containers worked out from the tile's own files, as every peer lists them, so
    /// nothing the asker sends decides where one is or what it is. Empty if the building is no site.
    /// </summary>
    public static YardStock[] YardAt(IChunkSource source, string building) =>
        BuildingKey.TryParse(building, out var key) ? YardOf(source, key.Tile, building) : [];

    /// <summary>
    /// The yard containers (<see cref="SiteLoot"/>) of a tile's sites, or of one site only
    /// (<paramref name="only"/>), on the ground the dressing stands on. Off the main thread.
    /// </summary>
    private static YardStock[] YardOf(IChunkSource source, TileId id, string? only)
    {
        var tile = source.LoadBuildingsAsync(id).GetAwaiter().GetResult();
        if (tile is not { Buildings.Count: > 0 } || !HasSite(tile)) return [];
        var roads = source.LoadRoadsAsync(id).GetAwaiter().GetResult();
        var grid = source.LoadChunkAsync(id).GetAwaiter().GetResult();
        var all = new List<YardStock>();
        foreach (var site in For(tile, roads))
            if (only == null || site.Key == only)
                all.AddRange(SiteLoot.Of(site, SiteShellBuilder.Dressing(tile, site, grid), tile.Id.MinE, tile.Id.MaxN));
        return all.ToArray();
    }

    /// <summary>
    /// The server's answer to "which pallet is <c>&lt;building&gt;:c&lt;slot&gt;</c>": worked out from the
    /// tile's own files as every peer draws it, so nothing the asker sends decides where it is or
    /// what is on it (as <c>SiteYards.PalletAt</c> answers for a yard's stack). Null if there is none.
    /// </summary>
    public static YardPallet? PalletAt(IChunkSource source, string building, int slot)
    {
        if (!BuildingKey.TryParse(building, out var key)) return null;
        var tile = source.LoadBuildingsAsync(key.Tile).GetAwaiter().GetResult();
        if (tile is not { Buildings.Count: > 0 } || !HasSite(tile)) return null;
        var roads = source.LoadRoadsAsync(key.Tile).GetAwaiter().GetResult();
        var grid = source.LoadChunkAsync(key.Tile).GetAwaiter().GetResult();
        foreach (var site in For(tile, roads))
        {
            if (site.Key != building) continue;
            foreach (var p in PalletsOf(tile, site, grid))
                if (p.Slot == slot) return p;
        }
        return null;
    }

    /// <summary>
    /// The server's answer to "where does crane <c>&lt;building&gt;|&lt;k&gt;</c> stand" (#618): from the
    /// tile's own files, as every peer builds it. Null if there is no such crane.
    /// </summary>
    public static SiteShellBuilder.CraneStand? CraneAt(IChunkSource source, string key, out TileId tileId)
    {
        tileId = default;
        if (!SiteShellBuilder.TryParseCraneKey(key, out var building, out int k) || !BuildingKey.TryParse(building, out var b)) return null;
        tileId = b.Tile;
        var tile = source.LoadBuildingsAsync(b.Tile).GetAwaiter().GetResult();
        if (tile is not { Buildings.Count: > 0 } || !HasSite(tile)) return null;
        var roads = source.LoadRoadsAsync(b.Tile).GetAwaiter().GetResult();
        var grid = source.LoadChunkAsync(b.Tile).GetAwaiter().GetResult();
        foreach (var site in For(tile, roads))
            if (site.Key == building)
                return k >= 0 && k < site.Cranes.Count ? SiteShellBuilder.Stand(tile, site, grid, k) : null;
        return null;
    }
}
