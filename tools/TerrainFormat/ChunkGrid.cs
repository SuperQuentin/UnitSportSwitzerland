namespace UnitSport.Terrain.Format;

/// <summary>
/// Decoded terrain chunk: an immutable grid of quantized heights.
/// Row 0 is the NORTH edge, rows go south; column 0 is the WEST edge, columns go east.
/// Vertex (col, row) sits at LV95 E = tileE*1000 + 2*col, N = (tileN+1)*1000 - 2*row.
///
/// <para>
/// A grid may be held at full resolution (<see cref="Stride"/> 1, 501x501) or decimated — the
/// horizon rings mesh one vertex in twenty, so carrying the other 399 costs 490 KB per tile to
/// throw away. Column and row indices are always expressed at <b>full resolution</b> whatever the
/// grid holds, so every caller reads the same coordinates it always did; only the values in
/// between stop being available.
/// </para>
/// </summary>
public sealed class ChunkGrid
{
    public TileId Id { get; }
    public ushort[] Heights { get; }
    public float MinHeight { get; }
    public float MaxHeight { get; }

    /// <summary>Full-resolution vertices per stored vertex. 1 is the complete 501x501 grid.</summary>
    public int Stride { get; }

    /// <summary>Stored vertices per side.</summary>
    public int Size { get; }

    /// <summary>Metres between stored vertices.</summary>
    public double Spacing => ChunkFormat.SpacingM * Stride;

    public ChunkGrid(TileId id, ushort[] heights, float minHeight, float maxHeight, int stride = 1)
    {
        if (stride < 1 || (ChunkFormat.GridSize - 1) % stride != 0)
            throw new ArgumentException($"Stride {stride} must divide {ChunkFormat.GridSize - 1}");

        int size = (ChunkFormat.GridSize - 1) / stride + 1;
        if (heights.Length != size * size)
            throw new ArgumentException(
                $"Expected {size}^2 heights at stride {stride}, got {heights.Length}");

        Id = id;
        Heights = heights;
        MinHeight = minHeight;
        MaxHeight = maxHeight;
        Stride = stride;
        Size = size;
    }

    /// <summary>
    /// Throws unless this grid holds every vertex.
    ///
    /// <para>
    /// For the things that are built <i>onto</i> the terrain rather than from it — collision,
    /// road and watercourse draping, tunnel cut walls. A 20 m lattice would not fail there, it
    /// would quietly float the road above the ground or sink it below, which is far harder to
    /// notice than an exception.
    /// </para>
    /// </summary>
    public void RequireFull(string what)
    {
        if (Stride != 1)
            throw new InvalidOperationException(
                $"{what} needs the full-resolution grid for {Id}, got stride {Stride}");
    }

    /// <summary>
    /// Quantized height at a full-resolution vertex. Coordinates between stored vertices round
    /// down to the one at or before them, which is exactly what a decimated render asks for.
    /// </summary>
    public ushort HeightAt(int col, int row) => Heights[row / Stride * Size + col / Stride];

    public double HeightMetersAt(int col, int row) => ChunkFormat.Dequantize(HeightAt(col, row));

    /// <summary>
    /// Bilinear height sample at an LV95 position. Coordinates outside the tile are clamped
    /// to its edge, so callers should pick the owning tile first via TileId.FromLv95.
    /// </summary>
    public double SampleHeight(double lv95E, double lv95N)
    {
        int last = Size - 1;
        double u = Math.Clamp((lv95E - Id.MinE) / Spacing, 0, last);
        double v = Math.Clamp((Id.MaxN - lv95N) / Spacing, 0, last);

        int c0 = Math.Min((int)u, last - 1);
        int r0 = Math.Min((int)v, last - 1);
        double fu = u - c0;
        double fv = v - r0;

        double h00 = ChunkFormat.Dequantize(Heights[r0 * Size + c0]);
        double h10 = ChunkFormat.Dequantize(Heights[r0 * Size + c0 + 1]);
        double h01 = ChunkFormat.Dequantize(Heights[(r0 + 1) * Size + c0]);
        double h11 = ChunkFormat.Dequantize(Heights[(r0 + 1) * Size + c0 + 1]);

        double north = h00 + (h10 - h00) * fu;
        double south = h01 + (h11 - h01) * fu;
        return north + (south - north) * fv;
    }

    /// <summary>
    /// Height sampled the way the RENDERED MESH actually surfaces it, rather than
    /// <see cref="SampleHeight"/>'s smooth bilinear blend of all four corners.
    ///
    /// <para>
    /// <c>TerrainMeshBuilder.BuildSurface</c> splits every quad into two FLAT-SHADED triangles
    /// along the v10-v01 diagonal, not a smooth surface — so on any quad where the four corners
    /// are not coplanar, which is most of them in real terrain, bilinear disagrees with what is
    /// actually drawn. At 2 m spacing (full resolution) that disagreement is a few centimetres
    /// and nobody notices. At 20-40 m spacing, which is what most of a streamed world renders at
    /// beyond ring 4 (see the coarse-tile companions), it is metres — enough that a GPX ribbon or
    /// avatar placed by <see cref="SampleHeight"/> sat visibly under or floating above the ground
    /// the player could actually see, because it was asking a different, smoother surface than
    /// the one on screen. This is the query every runtime caller wants; SampleHeight is kept
    /// as-is for the preprocessor, whose baked geometry was generated against it and would need
    /// a full re-run to change.
    /// </para>
    /// </summary>
    public double SampleMeshHeight(double lv95E, double lv95N)
    {
        int last = Size - 1;
        double u = Math.Clamp((lv95E - Id.MinE) / Spacing, 0, last);
        double v = Math.Clamp((Id.MaxN - lv95N) / Spacing, 0, last);

        int c0 = Math.Min((int)u, last - 1);
        int r0 = Math.Min((int)v, last - 1);
        double fu = u - c0;
        double fv = v - r0;

        double h00 = ChunkFormat.Dequantize(Heights[r0 * Size + c0]);
        double h10 = ChunkFormat.Dequantize(Heights[r0 * Size + c0 + 1]);
        double h01 = ChunkFormat.Dequantize(Heights[(r0 + 1) * Size + c0]);
        double h11 = ChunkFormat.Dequantize(Heights[(r0 + 1) * Size + c0 + 1]);

        // Same diagonal as BuildSurface: (v00,v10,v01) and (v10,v11,v01). fu+fv<=1 is the first
        // triangle (near the NW corner), the rest is the second - each a flat plane through its
        // three corners, not a bilinear blend of all four.
        if (fu + fv <= 1.0)
            return h00 + (h10 - h00) * fu + (h01 - h00) * fv;

        return h10 * (1.0 - fv) + h11 * (fu + fv - 1.0) + h01 * (1.0 - fu);
    }

    /// <summary>
    /// Keeps every <paramref name="stride"/>-th vertex and drops the rest.
    ///
    /// <para>
    /// Decimation, deliberately, not averaging. <c>TerrainMeshBuilder.BuildSurface</c> already
    /// renders the coarse rings by reading <c>HeightMetersAt(c * stride, r * stride)</c>, so the
    /// kept vertices are <b>precisely the ones the mesh uses today</b> and the result is
    /// bit-identical. Averaging would produce a smoother and slightly different mountain, which
    /// is a change to the picture disguised as an optimisation. Both tile edges are kept —
    /// 500 divides by 10 and by 20 — so seams stay exact.
    /// </para>
    /// </summary>
    public ChunkGrid Decimate(int stride)
    {
        if (stride == Stride) return this;
        if (stride % Stride != 0)
            throw new ArgumentException($"Cannot decimate stride {Stride} to {stride}");

        int step = stride / Stride;
        int size = (ChunkFormat.GridSize - 1) / stride + 1;
        var heights = new ushort[size * size];

        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                heights[r * size + c] = Heights[r * step * Size + c * step];

        return new ChunkGrid(Id, heights, MinHeight, MaxHeight, stride);
    }
}
