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
        new(1, 2),   // 2 m
        new(2, 4),   // 4 m
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

    /// <summary>
    /// The share of a tile's trees drawn at a ring distance (<see cref="ChunkNode.SetTreeDensity"/>).
    /// Full on the tile underfoot and its neighbours; past them every tree is a billboard of a
    /// few pixels, and a forest 49 tiles wide drew all of up to 60k per tile.
    /// </summary>
    public float TreeDensity(int dist) => SparseFarTrees
        ? (dist == 0 ? 1f : dist == 1 ? 0.5f : 0.15f)   // Very low, a standalone headset's GPU (#1112)
        : dist <= 1 ? 1f : dist == 2 ? 0.5f : 0.25f;

    /// <summary>Very low (#1112, #1216): the neighbours' trees halved and the farther ones at 15 %.</summary>
    public bool SparseFarTrees { get; init; }

    /// <summary>
    /// A building cell's own mesh is drawn to this far from the eye, its boxes beyond
    /// (<see cref="BuildingMeshBuilder.BuildProxies"/>): a 3D distance per 125 m cell (#1112; every
    /// preset since #1216, each at its own distance).
    /// </summary>
    public float BuildingBoxFromM { get; init; } = 1000f;

    /// <summary>
    /// The tiles round an anchor whose buildings are drawn in 125 m cells, each its own mesh or its boxes
    /// (<see cref="BuildingBoxFromM"/>): those whose near edge, (ring - 1) km off, is nearer than the boxes start.
    /// Beyond, a tile is one mesh of boxes (#1216).
    /// </summary>
    public int CellRings => 1 + (int)((BuildingBoxFromM - 1f) / 1000f);

    /// <summary>The road markings are drawn to this far, per 125 m cell; 0 everywhere they are loaded (#1112, #1216).</summary>
    public float RoadPaintToM { get; init; } = 1000f;

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

    /// <summary>
    /// The ring a tile <paramref name="ring"/> rings out is worth from <paramref name="aglM"/> metres
    /// above the ground (#1112): the ring of its 3D distance, from its near edge ((ring - 0.5) km) and the
    /// height, never nearer than the ring itself. Below 150 m it is the ring: a body on the ground, a car.
    /// </summary>
    public static int WithAltitude(int ring, float aglM)
    {
        if (aglM <= 150f) return ring;
        double h = Math.Max(0.0, ring - 0.5), v = aglM / 1000.0;
        double d = Math.Sqrt(h * h + v * v);
        int byDistance = d < 0.5 ? 0 : (int)Math.Ceiling(d - 0.5);
        return Math.Max(ring, byDistance);
    }

    public static int Distance(TileId a, TileId b) =>
        Math.Max(Math.Abs(a.E - b.E), Math.Abs(a.N - b.N));

    /// <summary>
    /// A policy from the player's settings: the preset fixes the inner rings and how far roads
    /// and buildings reach, and the render distance sets where the last, stride-50 ring ends.
    /// The stride-50 ring always starts one past the preset's last fine ring, so a radius below
    /// that just truncates the table. <paramref name="finestStride"/> is the visual style's
    /// (<see cref="Styles.StyleKit.FinestStride"/>): no ring is drawn finer than it.
    /// </summary>
    public static LodPolicy Create(DetailPreset detail, int maxRings, int finestStride = 1)
    {
        (Ring[] inner, int roads, int buildings) = detail switch
        {
            // a standalone headset (#1112): 4 m quads underfoot (125k triangles, Low's were 500k), 10 m on the neighbours
            DetailPreset.VeryLow => (new Ring[] { new(0, 4), new(1, 10), new(3, 20) }, 1, 1),
            DetailPreset.Low => (new Ring[] { new(0, 2), new(1, 4), new(2, 10), new(5, 20) }, 2, 1),
            DetailPreset.High => (new Ring[] { new(1, 1), new(3, 2), new(5, 4), new(8, 10), new(12, 20) }, 6, 5),
            // Rings by screen-space error: a ring-d tile is at least (d-1) km away, where an
            // s-metre quad spans at most s/(d-1) mrad; one pixel of the 640-wide vertex snap is
            // ~1.9 mrad. Rings 2-3 were 1-2 mrad (sub-pixel triangles, 11M of them); now every
            // ring past the first is 3-5.5 mrad, as the stride-20 and stride-50 rings already were.
            _ => (new Ring[] { new(0, 1), new(1, 2), new(2, 4), new(6, 10), new(9, 20) }, 4, 3),
        };

        var rings = new List<Ring>();
        foreach (var r in inner)
            if (r.MaxDist < maxRings) rings.Add(r with { Stride = Math.Max(r.Stride, finestStride) });
        rings.Add(new Ring(maxRings, FarStride));

        // far buildings as their boxes and the road markings cut off, nearer the lower the preset (#1216)
        (float boxFrom, float paintTo) = detail switch
        {
            DetailPreset.VeryLow => (300f, 250f),
            DetailPreset.Low => (600f, 500f),
            DetailPreset.High => (2000f, 0f),
            _ => (1000f, 1000f),
        };
        return new LodPolicy
        {
            SparseFarTrees = detail == DetailPreset.VeryLow,
            BuildingBoxFromM = boxFrom,
            RoadPaintToM = paintTo,
            Rings = rings.ToArray(),
            RoadMaxDist = Math.Min(roads, maxRings),
            // "--buildingrings N": how far buildings reach, to measure what they cost (#1112)
            BuildingMaxDist = Math.Min(Core.CmdArgs.Int("--buildingrings") ?? buildings, maxRings),
        };
    }

    /// <summary>
    /// The player's table. Low data (#63) takes the Low table within <see cref="GameSettings.LowDataRings"/>:
    /// full tiles (~0.5 MB each, more with buildings) only in the nearest rings, the 5 KB coarse
    /// companions out to the cap, and the horizon past it. In VR the preset is the headset's own
    /// (<see cref="GameSettings.VrDetail"/>, #1036).
    /// </summary>
    public static LodPolicy FromSettings(GameSettings s) => s.LowDataActive
        ? Create(s.EffectiveDetail, Math.Min(s.RenderDistanceRings, GameSettings.LowDataRings), Styles.StyleKit.FinestStride)
        : Create(s.EffectiveDetail, s.RenderDistanceRings, Styles.StyleKit.FinestStride);
}
