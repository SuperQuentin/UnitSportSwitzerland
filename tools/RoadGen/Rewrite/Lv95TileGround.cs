namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;

/// <summary>
/// The network stage's LV95 ground (#1507, G2 B3 4a): the loaded chunk grids of a block and its halo, each
/// point read from the one tile <see cref="TileId.FromLv95"/> names, none where that tile is not loaded. This
/// is the stage's rule from before #1507, kept to the bit: <see cref="Lv95Ground"/> also tries the tiles west
/// and south of a point on a tile edge, which would give a height where the stage had none (at the halo's
/// outer edges) and so other bytes.
/// </summary>
public sealed class Lv95TileGround(Dictionary<TileId, ChunkGrid> grids) : IGround
{
    public double? Sample(double e, double n) =>
        grids.TryGetValue(TileId.FromLv95(e, n), out var grid) ? grid.SampleHeight(e, n) : null;

    /// <summary>The ground over <paramref name="tiles"/>, or null when none of them has a grid.</summary>
    public static Lv95TileGround? Over(Func<TileId, ChunkGrid?> grid, IEnumerable<TileId> tiles)
    {
        var grids = new Dictionary<TileId, ChunkGrid>();
        foreach (var id in tiles)
            if (grid(id) is { } g) grids[id] = g;
        return grids.Count == 0 ? null : new Lv95TileGround(grids);
    }
}
