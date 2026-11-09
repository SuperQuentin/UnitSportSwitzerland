using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// Where the earth world's leaves come from (#1510, G2 C1): the region's built ones (<see cref="LeafStore"/>), or those
/// and what the game produces round them (<see cref="EarthLeaves"/>: the generated world, the fill, a fixture course).
/// Thread-safe.
/// </summary>
public interface ILeafSource
{
    /// <summary>Whether the leaf has content here, real or produced (a produced one may not be built yet).</summary>
    bool Has(CubeKey key);

    /// <summary>Whether the region holds the leaf (all of it or part of it) as built data.</summary>
    bool IsReal(CubeKey key);

    /// <summary>The leaf, read or produced on the first ask (may take a second: call it on a worker); null where none.</summary>
    CubeLeafContent? Get(CubeKey key);

    /// <summary>The leaf only if it is at hand: never reads or produces (for a per-frame query).</summary>
    CubeLeafContent? Cached(CubeKey key);
}
