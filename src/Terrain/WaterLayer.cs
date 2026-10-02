using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// The still water of one loaded tile, in memory (#299): the level of the surface at rest and how
/// much of the sea state reaches each point (<see cref="Scale"/>). Plain C#, no Godot: the tile
/// worker builds it, <see cref="ChunkManager.TryGetWaterLevel"/> reads it on any peer, and the
/// water mesh is laid on its lattice so a vertex and a query agree exactly.
///
/// <para>
/// Lattice: <see cref="WaterTile.Size"/>² samples every <see cref="WaterTile.Stride"/> m, row 0 the
/// north edge, like <see cref="ChunkGrid"/>. <see cref="Level"/> is NaN where there is no water.
/// <see cref="Scale"/> is 0..255 for 0..1 of the wave amplitude: fetch (rivers and ponds stay flat)
/// times depth (waves die on a beach and on a legacy tile's 0.12 m of water).
/// </para>
/// </summary>
public sealed class WaterLayer
{
    public const int Stride = WaterTile.Stride;
    public const int Size = WaterTile.Size;

    /// <summary>
    /// Legacy tiles (no water layer from the source): the surface sits this far above the terrain
    /// at a water cell, as it always has (swissALTI3D models a lake as its surface, there is no bed).
    /// </summary>
    public const float LegacyLift = 0.12f;

    /// <summary>Fetch at which a water body takes about two thirds of the sea state (1 - 1/e).</summary>
    public const float FetchScaleM = 1500f;

    /// <summary>Depth from which waves are full; shallower they shrink to nothing on the shore.</summary>
    public const float FullDepthM = 6f;

    /// <summary>Fetch where no shore is in sight inside the tile (a lake that spans tiles).</summary>
    public const float OpenFetchM = 4000f;

    /// <summary>Still level per sample, metres; NaN: dry.</summary>
    public float[] Level { get; }

    /// <summary>Wave scale per sample, 0..255 for 0..1; 0 where dry.</summary>
    public byte[] Scale { get; }

    /// <summary>Derived from the cover raster (a tile with no source layer): its surface is the terrain + 0.12 m.</summary>
    public bool Legacy { get; init; }

    /// <summary>How many samples have water.</summary>
    public int WetSamples { get; }

    public WaterLayer(float[] level, byte[] scale)
    {
        if (level.Length != Size * Size || scale.Length != Size * Size)
            throw new ArgumentException($"a water layer is {Size}x{Size} samples");
        Level = level;
        Scale = scale;
        int wet = 0;
        foreach (float l in level) if (!float.IsNaN(l)) wet++;
        WetSamples = wet;
    }

    /// <summary>Approximate managed size, for memory accounting.</summary>
    public long Bytes => Level.LongLength * 4 + Scale.LongLength + 64;

    public bool IsWet(int col, int row) => !float.IsNaN(Level[row * Size + col]);

    public float LevelAt(int col, int row) => Level[row * Size + col];

    public float ScaleAt(int col, int row) => Scale[row * Size + col] / 255f;

    /// <summary>
    /// The layer of a tile from its source's data: the bed is the height grid (any stride; the
    /// depth only shapes the wave scale), the fetch the source's or the distance to the shore.
    /// </summary>
    public static WaterLayer? Create(WaterTile tile, ChunkGrid bed)
    {
        if (tile.Level.Length != Size * Size) throw new ArgumentException($"water level must be {Size}x{Size}");
        var level = tile.Level;
        bool any = false;
        foreach (float l in level) if (!float.IsNaN(l)) { any = true; break; }
        if (!any) return null;

        var fetch = tile.FetchM ?? ShoreFetch(level);
        var scale = new byte[Size * Size];
        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
            {
                int i = r * Size + c;
                if (float.IsNaN(level[i])) continue;
                float depth = level[i] - BedAt(bed, c * Stride, r * Stride);
                scale[i] = Quantize(FetchFactor(fetch[i]) * DepthFactor(depth));
            }
        return new WaterLayer(level, scale);
    }

    /// <summary>
    /// The bed under full-resolution vertex (col, row) for the wave scale: bilinear over the 10 m
    /// lattice (<see cref="ChunkFormat.CoarseStride"/>), which the full grid and the coarse companion
    /// hold alike. A server builds its layer on the coarse grid and a client on the full one, and
    /// their waves must still match to the millimetre.
    /// </summary>
    public static float BedAt(ChunkGrid grid, int col, int row)
    {
        const int L = ChunkFormat.CoarseStride, Last = ChunkFormat.GridSize - 1;
        int c0 = Math.Min(col / L * L, Last - L), r0 = Math.Min(row / L * L, Last - L);
        float fu = (col - c0) / (float)L, fv = (row - r0) / (float)L;
        float h00 = (float)grid.HeightMetersAt(c0, r0), h10 = (float)grid.HeightMetersAt(c0 + L, r0);
        float h01 = (float)grid.HeightMetersAt(c0, r0 + L), h11 = (float)grid.HeightMetersAt(c0 + L, r0 + L);
        float north = h00 + (h10 - h00) * fu, south = h01 + (h11 - h01) * fu;
        return north + (south - north) * fv;
    }

    /// <summary>
    /// The legacy layer (#299 before #298): water wherever the cover raster says so, its surface the
    /// terrain + <see cref="LegacyLift"/>, exactly where <see cref="WaterMeshBuilder"/> always drew
    /// it. Needs the full-resolution grid. Null when the tile has no water.
    /// </summary>
    public static WaterLayer? FromCover(ChunkGrid grid, byte[] cover)
    {
        if (grid.Stride != 1 || cover.Length != ChunkFormat.GridSize * ChunkFormat.GridSize) return null;
        float[]? level = null;
        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
            {
                int fc = c * Stride, fr = r * Stride;
                if ((CoverClass)cover[fr * ChunkFormat.GridSize + fc] != CoverClass.Water) continue;
                if (level == null)
                {
                    level = new float[Size * Size];
                    Array.Fill(level, float.NaN);
                }
                level[r * Size + c] = (float)grid.HeightMetersAt(fc, fr) + LegacyLift;
            }
        return level == null ? null : Create(new WaterTile { Level = level }, grid) is { } layer
            ? new WaterLayer(layer.Level, layer.Scale) { Legacy = true } : null;
    }

    /// <summary>
    /// The still level and wave scale at a point given in metres from the tile's north-west corner
    /// (x east, z south). False when the nearest sample is dry. Bilinear over the wet corners of the
    /// sample square, so a river's slope is smooth and a bank does not drag the level down.
    /// </summary>
    public bool TrySample(double localX, double localZ, out float level, out float scale)
    {
        level = 0f;
        scale = 0f;
        double u = localX / Stride, v = localZ / Stride;
        int nc = (int)Math.Round(u), nr = (int)Math.Round(v);
        if ((uint)nc >= Size || (uint)nr >= Size || float.IsNaN(Level[nr * Size + nc])) return false;

        int c0 = Math.Clamp((int)Math.Floor(u), 0, Size - 2), r0 = Math.Clamp((int)Math.Floor(v), 0, Size - 2);
        double fu = Math.Clamp(u - c0, 0, 1), fv = Math.Clamp(v - r0, 0, 1);
        double sumL = 0, sumS = 0, sumW = 0;
        for (int k = 0; k < 4; k++)
        {
            int c = c0 + (k & 1), r = r0 + (k >> 1);
            int i = r * Size + c;
            float l = Level[i];
            if (float.IsNaN(l)) continue;
            double w = ((k & 1) == 0 ? 1 - fu : fu) * ((k >> 1) == 0 ? 1 - fv : fv);
            sumL += w * l;
            sumS += w * Scale[i];
            sumW += w;
        }
        if (sumW <= 1e-9)
        {
            // the nearest sample is wet but carries no weight (exactly on it): take it as it is
            int i = nr * Size + nc;
            level = Level[i];
            scale = Scale[i] / 255f;
            return true;
        }
        level = (float)(sumL / sumW);
        scale = (float)(sumS / sumW / 255.0);
        return true;
    }

    /// <summary>0..1: how much of the sea state a water body this wide takes.</summary>
    public static float FetchFactor(float fetchM) => fetchM <= 0 ? 0f : 1f - MathF.Exp(-fetchM / FetchScaleM);

    /// <summary>0..1: full waves from <see cref="FullDepthM"/>, none at the waterline (smoothstep).</summary>
    public static float DepthFactor(float depthM)
    {
        float t = Math.Clamp(depthM / FullDepthM, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static byte Quantize(float scale) => (byte)Math.Clamp((int)MathF.Round(scale * 255f), 0, 255);

    /// <summary>
    /// A fetch from the shape of the water in this tile alone: twice the distance to the nearest
    /// dry sample (a river's width at its middle), with the tile's edges open water, capped at
    /// <see cref="OpenFetchM"/>. Two-pass chamfer distance, a few milliseconds per tile.
    /// </summary>
    public static float[] ShoreFetch(float[] level)
    {
        const float Inf = 1e9f, Ortho = Stride, Diag = Stride * 1.41421356f;
        var d = new float[Size * Size];
        for (int i = 0; i < d.Length; i++) d[i] = float.IsNaN(level[i]) ? 0f : Inf;

        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
            {
                int i = r * Size + c;
                if (d[i] == 0f) continue;
                float best = d[i];
                if (c > 0) best = Math.Min(best, d[i - 1] + Ortho);
                if (r > 0)
                {
                    best = Math.Min(best, d[i - Size] + Ortho);
                    if (c > 0) best = Math.Min(best, d[i - Size - 1] + Diag);
                    if (c < Size - 1) best = Math.Min(best, d[i - Size + 1] + Diag);
                }
                d[i] = best;
            }
        for (int r = Size - 1; r >= 0; r--)
            for (int c = Size - 1; c >= 0; c--)
            {
                int i = r * Size + c;
                if (d[i] == 0f) continue;
                float best = d[i];
                if (c < Size - 1) best = Math.Min(best, d[i + 1] + Ortho);
                if (r < Size - 1)
                {
                    best = Math.Min(best, d[i + Size] + Ortho);
                    if (c < Size - 1) best = Math.Min(best, d[i + Size + 1] + Diag);
                    if (c > 0) best = Math.Min(best, d[i + Size - 1] + Diag);
                }
                d[i] = best;
            }
        for (int i = 0; i < d.Length; i++) d[i] = Math.Min(d[i] * 2f, OpenFetchM);
        return d;
    }
}
