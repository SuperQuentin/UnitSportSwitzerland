using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Height lookup over a set of loaded chunk grids: the LV95 instance of the stages' ground
/// (<see cref="Lv95Ground"/>, #1507), where the boundary rule and its reasons now live.
/// </summary>
public static class TerrainSampler
{
    public static Func<double, double, double?> For(Dictionary<TileId, ChunkGrid> grids) => new Lv95Ground(grids).Sample;
}
