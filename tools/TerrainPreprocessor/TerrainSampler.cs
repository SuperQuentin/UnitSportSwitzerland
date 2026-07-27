using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Height lookup over a set of loaded chunk grids.
///
/// The naive version — resolve the tile with <see cref="TileId.FromLv95"/>, sample it, give
/// up if it is absent — fails on any position that lands exactly on a tile boundary, because
/// <c>FromLv95</c> can only name one of the two (or four) tiles that share that lattice line.
/// Clipped polylines have a vertex on the boundary by construction, and feature passes run
/// in batches, so the tile it names is regularly the one that is not loaded. The caller then
/// sees a null in the middle of an otherwise draped line.
///
/// Every tile sharing the position is tried instead. Heights along a shared edge are
/// bit-identical (global quantization), so which one answers does not matter.
/// </summary>
public static class TerrainSampler
{
    public static Func<double, double, double?> For(Dictionary<TileId, ChunkGrid> grids)
        => (e, n) =>
        {
            int te = (int)Math.Floor(e / ChunkFormat.TileSizeM);
            int tn = (int)Math.Floor(n / ChunkFormat.TileSizeM);

            if (grids.TryGetValue(new TileId(te, tn), out var g)) return g.SampleHeight(e, n);

            // on a boundary the position also belongs to the tile west and/or south of it
            bool onEast = e - te * ChunkFormat.TileSizeM < 1e-6;
            bool onNorth = n - tn * ChunkFormat.TileSizeM < 1e-6;

            if (onEast && grids.TryGetValue(new TileId(te - 1, tn), out g)) return g.SampleHeight(e, n);
            if (onNorth && grids.TryGetValue(new TileId(te, tn - 1), out g)) return g.SampleHeight(e, n);
            if (onEast && onNorth && grids.TryGetValue(new TileId(te - 1, tn - 1), out g))
                return g.SampleHeight(e, n);

            return null;
        };
}
