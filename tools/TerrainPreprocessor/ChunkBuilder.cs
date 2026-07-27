using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Pass 2: builds the 501x501 corner-aligned vertex grid of a tile from the 0.5 m
/// cell-center lattice. Each vertex is the rounded average of the (up to) 4 surrounding
/// cells; vertices on tile edges pull cells from the neighbor tile's grid, so a shared
/// vertex is computed from the same cells on both sides -> bit-identical seams.
/// Cells falling in tiles that don't exist (dataset boundary) are simply skipped — the
/// remaining cells are the same set for every tile sharing the vertex, keeping seams
/// exact even along the dataset rim.
/// </summary>
public sealed class ChunkBuilder
{
    private readonly TempGridStore _store;

    public ChunkBuilder(TempGridStore store) => _store = store;

    public ChunkGrid Build(TileId id)
    {
        if (_store.Load(id) == null)
            throw new FileNotFoundException($"No temp grid for {id}");
        int n = ChunkFormat.GridSize;
        var heights = new ushort[n * n];
        ushort qMin = ushort.MaxValue, qMax = 0;

        for (int r = 0; r < n; r++)
        {
            // vertex row r sits between cell rows 4r-1 and 4r (row 0 = north in both grids)
            for (int c = 0; c < n; c++)
            {
                int sum = 0, count = 0;
                AddCell(id, 4 * r - 1, 4 * c - 1, ref sum, ref count);
                AddCell(id, 4 * r - 1, 4 * c, ref sum, ref count);
                AddCell(id, 4 * r, 4 * c - 1, ref sum, ref count);
                AddCell(id, 4 * r, 4 * c, ref sum, ref count);
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
