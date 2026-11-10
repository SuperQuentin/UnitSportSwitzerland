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

    /// <summary>A point of a tile's plans (tile-local x, y, z) in the world.</summary>
    public static Vector3 WorldLocal(FootPlayer me, TileId tile, Vector3 local) =>
        me.Origin!.ToWorld(GlobalPos.OfTile(tile, local.X, local.Y, local.Z));
}
