namespace UnitSport.Terrain.Format;

/// <summary>
/// A leaf's surface as the globe draws it from afar (#1654): its heights, with the still water over them where it has
/// any. Since the water stage (#298, G2 B3 PR 6) a leaf's heights are a lake's bed, 300 m under Lavaux, and the
/// pyramid averaged from them sank every lake. The water is on every second lattice vertex (<see cref="CubeWater.Stride"/>);
/// a vertex between samples takes the mean of its wet neighbours' levels (along the edge for a vertex on one, so two
/// leaves give an edge the same bits). The water stage leaves a lake leaf's outer samples dry while its heights there
/// are still the bed (B4: 76 leaves, 14,000 vertices along their edges, 300 m down): a dry vertex more than
/// <see cref="BedM"/> under water within <see cref="ReachSamples"/> samples is that water's surface too. The surface is
/// never under the ground.
/// </summary>
public static class CubeSurface
{
    /// <summary>How far under nearby water a dry vertex must lie to be taken for its bed, metres.</summary>
    public const float BedM = 20f;

    /// <summary>How far a dry vertex looks for that water, in samples (2 lattice vertices each: ~5 m at level 14).</summary>
    public const int ReachSamples = 2;

    /// <summary>The leaf's heights with its water's surface over them; the same tile when nothing changes.</summary>
    public static CubeTile Of(CubeTile heights, CubeWater? water)
    {
        if (water == null || water.Bits != heights.Bits) return heights;
        int n = (1 << heights.Bits) + 1;
        float[]? h = null;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float ground = heights.Heights[j * n + i];
                float surface = SurfaceAt(water, i, j, ground);
                if (surface == ground || float.IsNaN(surface) && float.IsNaN(ground)) continue;
                h ??= (float[])heights.Heights.Clone();
                h[j * n + i] = surface;
            }
        return h == null ? heights : new CubeTile { Key = heights.Key, Bits = heights.Bits, Quantum = heights.Quantum, Heights = h };
    }

    /// <summary>
    /// The surface at lattice vertex (i, j) over <paramref name="ground"/> (NaN where none): the still water's level
    /// where it is wet and over the ground, the nearby water's over a bed left dry, else the ground.
    /// </summary>
    public static float SurfaceAt(CubeWater water, int i, int j, float ground)
    {
        float level = LevelAt(water.Level, water.Side, i, j);
        if (float.IsNaN(level))
        {
            if (float.IsNaN(ground)) return ground;
            float near = NearestLevel(water.Level, water.Side, i, j);
            return !float.IsNaN(near) && ground < near - BedM ? near : ground;
        }
        return !float.IsNaN(ground) && ground >= level ? ground : level;
    }

    /// <summary>The still level at lattice vertex (i, j): its own sample, or the mean of the wet samples round it; NaN dry.</summary>
    public static float LevelAt(float[] level, int side, int i, int j)
    {
        int c0 = i >> 1, r0 = j >> 1, c1 = c0 + (i & 1), r1 = r0 + (j & 1);
        float sum = 0;
        int wet = 0;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                float v = level[r * side + c];
                if (float.IsNaN(v)) continue;
                sum += v;
                wet++;
            }
        return wet == 0 ? float.NaN : sum / wet;
    }

    /// <summary>The highest wet level within <see cref="ReachSamples"/> samples of vertex (i, j); NaN where none.</summary>
    private static float NearestLevel(float[] level, int side, int i, int j)
    {
        int c = i >> 1, r = j >> 1;
        float best = float.NaN;
        for (int y = Math.Max(0, r - ReachSamples); y <= Math.Min(side - 1, r + ReachSamples + (j & 1)); y++)
            for (int x = Math.Max(0, c - ReachSamples); x <= Math.Min(side - 1, c + ReachSamples + (i & 1)); x++)
            {
                float v = level[y * side + x];
                if (!float.IsNaN(v) && !(v <= best)) best = v;
            }
        return best;
    }

    /// <summary>
    /// Still levels to the decimetre with their sample counts into one level per lake: within a metre of a busier
    /// level, a level is that lake's (Léman's leaves hold 422.2 and 422.3: two snaps left a 0.3 m step, which the globe's
    /// still-water test drew as a green line, #1669).
    /// </summary>
    public static float[] LakeLevels(IEnumerable<KeyValuePair<int, long>> levels, long minSamples)
    {
        var kept = new List<(float Level, long Count)>();
        foreach (var (dm, count) in levels.OrderByDescending(l => l.Value).ThenBy(l => l.Key))
        {
            float level = dm / 10f;
            int at = kept.FindIndex(k => Math.Abs(k.Level - level) <= 1f);
            if (at >= 0) kept[at] = (kept[at].Level, kept[at].Count + count);
            else kept.Add((level, count));
        }
        return kept.Where(k => k.Count >= minSamples).Select(k => k.Level).OrderBy(l => l).ToArray();
    }
}
