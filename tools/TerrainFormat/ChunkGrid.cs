namespace UnitSport.Terrain.Format;

/// <summary>
/// Decoded terrain chunk: an immutable 501x501 grid of quantized heights.
/// Row 0 is the NORTH edge, rows go south; column 0 is the WEST edge, columns go east.
/// Vertex (col, row) sits at LV95 E = tileE*1000 + 2*col, N = (tileN+1)*1000 - 2*row.
/// </summary>
public sealed class ChunkGrid
{
    public TileId Id { get; }
    public ushort[] Heights { get; }
    public float MinHeight { get; }
    public float MaxHeight { get; }

    public ChunkGrid(TileId id, ushort[] heights, float minHeight, float maxHeight)
    {
        if (heights.Length != ChunkFormat.GridSize * ChunkFormat.GridSize)
            throw new ArgumentException($"Expected {ChunkFormat.GridSize}^2 heights, got {heights.Length}");
        Id = id;
        Heights = heights;
        MinHeight = minHeight;
        MaxHeight = maxHeight;
    }

    public ushort HeightAt(int col, int row) => Heights[row * ChunkFormat.GridSize + col];

    public double HeightMetersAt(int col, int row) => ChunkFormat.Dequantize(HeightAt(col, row));

    /// <summary>
    /// Bilinear height sample at an LV95 position. Coordinates outside the tile are clamped
    /// to its edge, so callers should pick the owning tile first via TileId.FromLv95.
    /// </summary>
    public double SampleHeight(double lv95E, double lv95N)
    {
        int last = ChunkFormat.GridSize - 1;
        double u = Math.Clamp((lv95E - Id.MinE) / ChunkFormat.SpacingM, 0, last);
        double v = Math.Clamp((Id.MaxN - lv95N) / ChunkFormat.SpacingM, 0, last);

        int c0 = Math.Min((int)u, last - 1);
        int r0 = Math.Min((int)v, last - 1);
        double fu = u - c0;
        double fv = v - r0;

        double h00 = HeightMetersAt(c0, r0);
        double h10 = HeightMetersAt(c0 + 1, r0);
        double h01 = HeightMetersAt(c0, r0 + 1);
        double h11 = HeightMetersAt(c0 + 1, r0 + 1);

        double north = h00 + (h10 - h00) * fu;
        double south = h01 + (h11 - h01) * fu;
        return north + (south - north) * fv;
    }
}
