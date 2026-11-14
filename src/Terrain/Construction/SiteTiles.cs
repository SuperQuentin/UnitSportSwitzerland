using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// For the building-site probes (#1704): where a generated site's plans are filed, on both worlds. The generator names a
/// site by its LV95 place; the ground files what stands there under its own tile names (<see cref="IWorldGround.ContentName"/>:
/// the LV95 tile, or an earth leaf's stand-in), and the site's plans (yards, cranes, offices) are in that tile's frame
/// (<see cref="GlobalPos.OfTile"/>).
/// </summary>
internal static class SiteTiles
{
    /// <summary>The ground's name for the tile under a generated site's place.</summary>
    public static TileId TileOf(FootPlayer me, GlobalPos site)
    {
        if (me.Terrain is not { } ground || me.Origin is not { } origin) return site.Tile;
        return ground.ContentName(ground.TileAt(origin.ToWorld(site)));
    }

    /// <summary>A point of a tile's plans (its E/N names and its height in the tile) in the world.</summary>
    public static Vector3 World(FootPlayer me, TileId tile, double e, double n, double h) =>
        me.Origin!.ToWorld(GlobalPos.OfTileEn(tile, e, n, h));

    /// <summary>
    /// The foot of the nearest crane with one of its site's materials pallets under its jib (#1704, #1731): the generated
    /// world's sites (<see cref="ProceduralWorld.SitesNear"/>) nearest first, out to <paramref name="radius"/> m, each
    /// planned from the ground's own tile for it (an earth leaf's stand-in, its leaf made on first ask). A site's dressing
    /// rolls from its building's identity, which differs between the flat world and the leaves, so a check cannot name one.
    /// </summary>
    public static async System.Threading.Tasks.Task<GlobalPos?> CraneWithPalletNear(FootPlayer me, double radius = 12000, Action<string>? log = null)
    {
        if (me.Terrain is not { } ground || ground.Content is not { } source) return null;
        var at = me.Global;
        var (e0, n0, _) = SwissGeodesy.FromEcef(at.Ecef);
        var world = new ProceduralWorld(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
        var places = new List<GlobalPos>();
        foreach (var (se, sn, _, _) in world.SitesNear(e0, n0, radius)) places.Add(GlobalPos.FromLv95(se, sn, at.Alt));
        places.Sort((a, b) => a.HorizontalDistanceSquaredTo(at).CompareTo(b.HorizontalDistanceSquaredTo(at)));
        var seen = new HashSet<TileId>();
        int sitesSeen = 0, cranes = 0, palletsSeen = 0;
        foreach (var place in places)
        {
            var id = TileOf(me, place);
            if (!seen.Add(id)) continue;
            if (await source.LoadBuildingsAsync(id) is not { } tile || !SitePlans.HasSite(tile)) continue;
            var roads = await source.LoadRoadsAsync(id);
            var grid = await source.LoadChunkAsync(id);
            foreach (var site in SitePlans.For(tile, roads))
            {
                var pallets = SitePlans.PalletsOf(tile, site, grid).Select(p => id.Local(p.E, p.N))
                    .Select(l => new Vector2((float)l.X, (float)l.Z)).ToList();
                sitesSeen++;
                palletsSeen += pallets.Count;
                foreach (var crane in site.Cranes)
                {
                    cranes++;
                    if (!pallets.Any(p => p.DistanceTo(crane.Base) is var d && d > CraneMotion.MinTrolley + 1f && d < crane.JibLength - 2f)) continue;
                    var (e, n) = id.Global(crane.Base.X, crane.Base.Y);
                    var foot = GlobalPos.OfTileEn(id, e, n, (grid?.SampleMeshHeight(e, n) ?? 0) + 2);
                    log?.Invoke($"{places.Count} generated sites within {radius / 1000:F0} km, {seen.Count} tiles read, {sitesSeen} sites, {cranes} cranes, {palletsSeen} pallets: {site.Key}'s crane {foot.HorizontalDistanceTo(at):F0} m away");
                    return foot;
                }
            }
        }
        log?.Invoke($"{places.Count} generated sites within {radius / 1000:F0} km, {seen.Count} tiles read, {sitesSeen} sites, {cranes} cranes, {palletsSeen} pallets: no crane with a pallet under its jib");
        return null;
    }

    /// <summary>A point of a tile's plans (tile-local x, y, z) in the world.</summary>
    public static Vector3 WorldLocal(FootPlayer me, TileId tile, Vector3 local) =>
        me.Origin!.ToWorld(GlobalPos.OfTile(tile, local.X, local.Y, local.Z));
}
