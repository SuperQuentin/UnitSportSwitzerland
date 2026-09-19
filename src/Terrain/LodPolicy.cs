using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Ring-based LOD selection in tile units (Chebyshev distance from the anchor's tile).
/// Strides must divide GridSize-1 (1000) and, past the road ring, be multiples of
/// <see cref="ChunkFormat.CoarseStride"/> so the 5 KB companion tile can serve them:
/// valid values 1, 2, 4, 10, 20, 50.
/// </summary>
public sealed class LodPolicy
{
    public readonly record struct Ring(int MaxDist, int Stride);

    /// <summary>
    /// The outermost stride. 21x21 vertices a tile — 441, against 2,601 at stride 20 — which is
    /// what makes a 40-ring radius (6,561 tiles) cost about what 9 rings used to.
    /// </summary>
    public const int FarStride = 50;

    /// <summary>Outermost ring distance defines the load radius.</summary>
    public Ring[] Rings { get; init; } =
    {
        new(0, 1),   // 1 m quads — full source resolution on the tile you are standing on
        new(2, 2),   // 2 m
        new(3, 4),   // 4 m
        new(6, 10),  // 10 m
        new(9, 20),  // 20 m
        new(15, FarStride),  // 50 m
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

    /// <summary>
    /// A policy from the player's settings: the preset fixes the inner rings and how far roads
    /// and buildings reach, and the render distance sets where the last, stride-50 ring ends.
    /// The stride-50 ring always starts one past the preset's last fine ring, so a radius below
    /// that just truncates the table.
    /// </summary>
    public static LodPolicy Create(DetailPreset detail, int maxRings)
    {
        (Ring[] inner, int roads, int buildings) = detail switch
        {
            DetailPreset.Low => (new Ring[] { new(0, 2), new(1, 4), new(2, 10), new(5, 20) }, 2, 1),
            DetailPreset.High => (new Ring[] { new(1, 1), new(3, 2), new(5, 4), new(8, 10), new(12, 20) }, 6, 5),
            _ => (new Ring[] { new(0, 1), new(2, 2), new(3, 4), new(6, 10), new(9, 20) }, 4, 3),
        };

        var rings = new List<Ring>();
        foreach (var r in inner)
            if (r.MaxDist < maxRings) rings.Add(r);
        rings.Add(new Ring(maxRings, FarStride));

        return new LodPolicy
        {
            Rings = rings.ToArray(),
            RoadMaxDist = Math.Min(roads, maxRings),
            BuildingMaxDist = Math.Min(buildings, maxRings),
        };
    }

    public static LodPolicy FromSettings(GameSettings s) => Create(s.Detail, s.RenderDistanceRings);
}
