using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Pass 2: builds the corner-aligned vertex grid of a tile from the 0.5 m cell-center
/// lattice. Each vertex is the rounded average of every source cell inside its own
/// footprint — <see cref="Ratio"/> x <see cref="Ratio"/> cells, one full non-overlapping
/// block per vertex, not a fixed 2x2 subset of it. Vertices on tile edges pull cells from
/// the neighbor tile's grid, so a shared vertex is computed from the same cells on both
/// sides -> bit-identical seams. Cells falling in tiles that don't exist (dataset
/// boundary) are simply skipped — the remaining cells are the same set for every tile
/// sharing the vertex, keeping seams exact even along the dataset rim.
///
/// <para>
/// The footprint used to be hardcoded to the 4 cells nearest each vertex regardless of
/// <see cref="Ratio"/> — correct only by coincidence when Ratio was exactly 2 (today, at
/// 1 m spacing over 0.5 m cells), and wrong at the old 2 m spacing (Ratio 4), where it
/// silently discarded 12 of the 16 cells the vertex's true footprint actually covered.
/// Averaging the full <c>Ratio x Ratio</c> block is the general fix and degrades cleanly
/// to the same 2x2 case that already worked.
/// </para>
/// </summary>
public sealed class ChunkBuilder
{
    private readonly TempGridStore _store;

    public ChunkBuilder(TempGridStore store) => _store = store;

    /// <summary>Source cells per output vertex, along one axis (2 at today's 1 m spacing).</summary>
    private static int Ratio => XyzParser.CellsPerSide / (ChunkFormat.GridSize - 1);

    public ChunkGrid Build(TileId id)
    {
        if (_store.Load(id) == null)
            throw new FileNotFoundException($"No temp grid for {id}");
        int n = ChunkFormat.GridSize;
        int ratio = Ratio;
        // vertex r's footprint is centered on cell row ratio*r, spanning half the ratio either
        // side - e.g. ratio 2: offsets -1,0 (the 2x2 block straddling the vertex); ratio 4 (the
        // old 2 m spacing): offsets -2,-1,0,1, the full 4x4 block, not just its inner 2x2 quarter.
        int half = ratio / 2;
        var heights = new ushort[n * n];
        ushort qMin = ushort.MaxValue, qMax = 0;

        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                int sum = 0, count = 0;
                for (int dr = -half; dr < ratio - half; dr++)
                    for (int dc = -half; dc < ratio - half; dc++)
                        AddCell(id, ratio * r + dr, ratio * c + dc, ref sum, ref count);
                if (count == 0)
                    throw new InvalidDataException($"Vertex ({c},{r}) of {id} has no source cells");
                ushort q = (ushort)((sum + count / 2) / count);
                heights[r * n + c] = q;
                if (q < qMin) qMin = q;
                if (q > qMax) qMax = q;
            }
        }

        return new ChunkGrid(id, heights,
            (float)ChunkFormat.Dequantize(qMin), (float)ChunkFormat.Dequantize(qMax));
    }

    /// <summary>
    /// Accumulates cell (row, col), where indices may be -1 or 2000 and then spill into
    /// the neighbor tile. Cells in tiles without data are skipped.
    /// </summary>
    private void AddCell(TileId id, int row, int col, ref int sum, ref int count)
    {
        int cells = XyzParser.CellsPerSide;
        int tileE = id.E, tileN = id.N;

        if (col < 0) { tileE--; col += cells; }
        else if (col >= cells) { tileE++; col -= cells; }
        if (row < 0) { tileN++; row += cells; } // row 0 = north, so row -1 is in the tile to the north
        else if (row >= cells) { tileN--; row -= cells; }

        var grid = _store.Load(new TileId(tileE, tileN));
        if (grid == null) return;
        sum += grid[row * cells + col];
        count++;
    }
}
