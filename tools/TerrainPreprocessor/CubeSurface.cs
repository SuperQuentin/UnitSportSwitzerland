using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A leaf's surface as the globe's coarser levels draw it (#1654): its heights, with the still water over them where it
/// has any. Since the water stage (#298, G2 B3 PR 6) a leaf's heights are a lake's bed, 300 m under Lavaux, and the
/// pyramid averaged from them would sink every lake. The water is on every second lattice vertex
/// (<see cref="CubeWater.Stride"/>); a vertex between samples takes the mean of its wet neighbours' levels (along the
/// edge for a vertex on one, so two leaves give an edge the same bits). The surface is never under the ground.
/// </summary>
public static class CubeSurface
{
    /// <summary>The leaf's heights with its water's surface over them; the same tile when it has no water.</summary>
    public static CubeTile Of(CubeTile heights, CubeWater? water)
    {
        if (water == null || water.Bits != heights.Bits) return heights;
        int n = (1 << heights.Bits) + 1, side = water.Side;
        float[]? h = null;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float level = LevelAt(water.Level, side, i, j);
                if (float.IsNaN(level)) continue;
                float ground = heights.Heights[j * n + i];
                if (!float.IsNaN(ground) && ground >= level) continue;
                h ??= (float[])heights.Heights.Clone();
                h[j * n + i] = level;
            }
        return h == null ? heights : new CubeTile { Key = heights.Key, Bits = heights.Bits, Quantum = heights.Quantum, Heights = h };
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
}
