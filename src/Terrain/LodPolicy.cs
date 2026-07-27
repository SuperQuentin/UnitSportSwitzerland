using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Ring-based LOD selection in tile units (Chebyshev distance from the anchor's tile).
/// Strides must divide GridSize-1 (500): valid values 1, 2, 4, 10, 20.
/// </summary>
public sealed class LodPolicy
{
    public readonly record struct Ring(int MaxDist, int Stride);

    /// <summary>Outermost ring distance defines the load radius.</summary>
    public Ring[] Rings { get; init; } =
    {
        new(0, 1),   // 2 m quads — full source resolution on the tile you are standing on
        new(2, 2),   // 4 m
        new(3, 4),   // 8 m
        new(6, 10),  // 20 m
        new(9, 20),  // 40 m
    };

    /// <summary>Chunks within this distance also get collision shapes.</summary>
    public int CollisionMaxDist { get; init; } = 1;

    /// <summary>
    /// Chunks within this distance get road meshes. Roads are draped onto the full-detail
    /// heightfield, so beyond the fine LOD rings they would visibly sink into the coarser
    /// terrain — cheaper and better-looking to stop drawing them.
    /// </summary>
    public int RoadMaxDist { get; init; } = 4;

    /// <summary>
    /// Chunks within this distance get building meshes. Buildings are full LoD2 shells
    /// (~115 triangles each), so this stays tighter than roads.
    /// </summary>
    public int BuildingMaxDist { get; init; } = 3;

    /// <summary>Extra rings a chunk may drift out before being unloaded (hysteresis).</summary>
    public int UnloadSlack { get; init; } = 1;

    public int MaxDist => Rings[^1].MaxDist;

    /// <summary>Returns the stride for a chunk at the given distance, or -1 if out of range.</summary>
    public int StrideFor(int dist)
    {
        foreach (var ring in Rings)
            if (dist <= ring.MaxDist)
                return ring.Stride;
        return -1;
    }

    public static int Distance(TileId a, TileId b) =>
        Math.Max(Math.Abs(a.E - b.E), Math.Abs(a.N - b.N));
}
