using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// The region's leaves and the generated fill round them as the game builds them, the same on a client and on the
/// server (#1682): the region's built leaves (<see cref="TerrainPaths.FindLeafDir"/>), the generator anchored where every
/// copy anchors it, the fill on the globe's pyramid (#1678, <see cref="World.Globe.DataDir"/>) and its domain round
/// <paramref name="startE"/>/<paramref name="startN"/> (it grows round every focus, <see cref="EarthWorld"/>). A leaf's
/// content depends on the generator, the pyramid and the real leaves alone, never on the start: a client and the server
/// make the same fill leaf.
/// </summary>
public static class GameLeaves
{
    /// <param name="generated">The generator; null, the region's leaves alone.</param>
    /// <param name="fillOn">The fill's setting, asked whenever a leaf is.</param>
    /// <param name="roads">Whether the fill's leaves carry roads (<c>--no-fill-roads</c>: without RoadGen's stage).</param>
    public static EarthLeaves Region(ProceduralWorld? generated, double startE, double startN, Func<bool>? fillOn = null, bool? roads = null)
    {
        var real = LeafStore.Open(TerrainPaths.FindLeafDir());
        EarthLeaves leaves;
        if (generated != null)
        {
            var (lat, lon) = Format.Lv95Source.Instance.ToLatLon(startE, startN);
            // the fill on the globe's pyramid where the game has the full one (#1678); the shipped levels are coarser
            // than the generator's own relief, which it keeps then
            var pyramid = PyramidBase.Open(World.Globe.DataDir);
            GD.Print($"[earth] the fill on {(pyramid != null ? $"the pyramid (to level {pyramid.FinestLevel}, {pyramid.Lakes.Length} lake(s))" : "the generator's own relief")}");
            leaves = EarthLeaves.WithFill(real, generated, lat, lon, fillOn, roads ?? !CmdArgs.Has("--no-fill-roads"), pyramid);
        }
        else leaves = EarthLeaves.Region(real);
        leaves.Log = s => GD.Print(s);
        return leaves;
    }
}
