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
    /// <summary>The ground's name for the tile under a generated site's LV95 place.</summary>
    public static TileId TileOf(FootPlayer me, double e, double n)
    {
        if (me.Terrain is not { } ground || me.Origin is not { } origin) return TileId.FromLv95(e, n);
        var world = origin.ToWorld(GlobalPos.FromLv95(e, n, me.Global.Alt));
        return ground.ContentName(ground.TileAt(world));
    }

    /// <summary>A point of a tile's plans (its E/N names and its height in the tile) in the world.</summary>
    public static Vector3 World(FootPlayer me, TileId tile, double e, double n, double h) =>
        me.Origin!.ToWorld(GlobalPos.OfTileEn(tile, e, n, h));

    /// <summary>
    /// The foot of the nearest crane, round the player's tile (<paramref name="rings"/> of the ground's tiles), with one of
    /// its site's materials pallets under its jib (#1704): planned from the tiles' files, drawn or not. The generator plans
    /// other sites on an earth world's leaves than on the flat world's tiles, so a check cannot name one.
    /// </summary>
    public static async System.Threading.Tasks.Task<GlobalPos?> CraneWithPalletNear(FootPlayer me, int rings = 4, Action<string>? log = null)
    {
        if (me.Terrain is not { } ground || ground.Content is not { } source) return null;
        var here = ground.ContentName(ground.TileAt(me.GlobalPosition));
        var at = me.Global;
        GlobalPos? best = null;
        double nearest = double.MaxValue;
        int tiles = 0, withSites = 0, sitesSeen = 0, cranes = 0, palletsSeen = 0;
        float closest = float.MaxValue;
        // ring by ring, nearest first: the first ring that has one ends the search (each leaf asked is made, ~2 s)
        for (int ring = 0; ring <= rings && best == null; ring++)
        for (int dx = -ring; dx <= ring; dx++)
            for (int dz = ring; dz >= -ring; dz--)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                var id = here.Neighbour(dx, dz);
                tiles++;
                if (await source.LoadBuildingsAsync(id) is not { } tile || !SitePlans.HasSite(tile)) continue;
                withSites++;
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
                        foreach (var p in pallets) closest = Math.Min(closest, p.DistanceTo(crane.Base));
                        if (!pallets.Any(p => p.DistanceTo(crane.Base) is var d && d > CraneMotion.MinTrolley + 1f && d < crane.JibLength - 2f)) continue;
                        var (e, n) = id.Global(crane.Base.X, crane.Base.Y);
                        var foot = GlobalPos.OfTileEn(id, e, n, (grid?.SampleMeshHeight(e, n) ?? 0) + 2);
                        double far = foot.HorizontalDistanceTo(at);
                        if (far < nearest) (nearest, best) = (far, foot);
                    }
                }
            }
        log?.Invoke($"{tiles} tiles, {withSites} with sites, {sitesSeen} sites, {cranes} cranes, {palletsSeen} pallets, the closest pallet {closest:F1} m from a crane's foot");
        return best;
    }

    /// <summary>A point of a tile's plans (tile-local x, y, z) in the world.</summary>
    public static Vector3 WorldLocal(FootPlayer me, TileId tile, Vector3 local) =>
        me.Origin!.ToWorld(GlobalPos.OfTile(tile, local.X, local.Y, local.Z));
}
