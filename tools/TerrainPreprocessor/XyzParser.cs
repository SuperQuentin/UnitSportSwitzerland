using System.Buffers.Text;
using System.IO.Compression;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Pass 1: streams one swissALTI3D .xyz.zip into a 2000x2000 grid of globally quantized
/// uint16 heights (row 0 = north). Points are placed by their X/Y coordinates rather than
/// by line order, so tiles with missing points (national border, future) degrade gracefully.
/// </summary>
public static class XyzParser
{
    public const int CellsPerSide = 2000;
    public const ushort MissingCell = ushort.MaxValue;

    public static ushort[] Parse(string zipPath, TileId tile)
    {
        var grid = new ushort[CellsPerSide * CellsPerSide];
        Array.Fill(grid, MissingCell);

        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".xyz", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"No .xyz entry in {zipPath}");

        double baseE = tile.MinE;
        double topN = tile.MaxN;
        long filled = 0;

        using var stream = entry.Open();
        var buffer = new byte[1 << 20];
        int len = 0;

        while (true)
        {
            int read = stream.Read(buffer, len, buffer.Length - len);
            bool eof = read == 0;
            len += read;

            int pos = 0;
            while (true)
            {
                int nl = Array.IndexOf(buffer, (byte)'\n', pos, len - pos);
                if (nl < 0)
                {
                    if (!eof || pos >= len) break;
                    nl = len; // final line without trailing newline
                }

                int lineEnd = nl;
                if (lineEnd > pos && buffer[lineEnd - 1] == (byte)'\r') lineEnd--;
                if (lineEnd > pos && buffer[pos] != (byte)'X') // skip "X Y Z" header
                {
                    ParseLine(buffer.AsSpan(pos, lineEnd - pos), grid, baseE, topN, zipPath, ref filled);
                }

                pos = nl + 1;
                if (pos > len) break;
            }

            if (eof) break;
            // keep the partial line at the end of the buffer
            len -= pos;
            if (len > 0) Array.Copy(buffer, pos, buffer, 0, len);
            if (len == buffer.Length)
                throw new InvalidDataException($"Line longer than {buffer.Length} bytes in {zipPath}");
        }

        long missing = grid.LongLength - filled;
        if (missing > 0)
        {
            Console.WriteLine($"  [warn] {tile}: {missing} missing cells, filling from row neighbors");
            FillMissing(grid);
        }
        return grid;
    }

    private static void ParseLine(ReadOnlySpan<byte> line, ushort[] grid, double baseE, double topN,
        string sourceName, ref long filled)
    {
        if (line.IsEmpty) return;
        if (!TryReadDouble(ref line, out double x) ||
            !TryReadDouble(ref line, out double y) ||
            !TryReadDouble(ref line, out double z))
            throw new InvalidDataException($"Unparsable line in {sourceName}");

        // Cell centers sit at base + 0.25 + 0.5*i; recover the index by rounding.
        int col = (int)Math.Round((x - baseE) * 2.0 - 0.5);
        int row = (int)Math.Round((topN - y) * 2.0 - 0.5);
        if ((uint)col >= CellsPerSide || (uint)row >= CellsPerSide)
            throw new InvalidDataException($"Point ({x}, {y}) outside tile in {sourceName}");

        int idx = row * CellsPerSide + col;
        if (grid[idx] == MissingCell) filled++;
        grid[idx] = ChunkFormat.Quantize(z);
    }

    private static bool TryReadDouble(ref ReadOnlySpan<byte> line, out double value)
    {
        while (!line.IsEmpty && line[0] == (byte)' ') line = line[1..];
        if (!Utf8Parser.TryParse(line, out value, out int consumed)) return false;
        line = line[consumed..];
        return true;
    }

    /// <summary>Fills missing cells from the nearest valid cell in the same row, else same column.</summary>
    private static void FillMissing(ushort[] grid)
    {
        for (int r = 0; r < CellsPerSide; r++)
        {
            int rowStart = r * CellsPerSide;
            // left-to-right then right-to-left carry
            ushort carry = MissingCell;
            for (int c = 0; c < CellsPerSide; c++)
            {
                if (grid[rowStart + c] != MissingCell) carry = grid[rowStart + c];
                else if (carry != MissingCell) grid[rowStart + c] = carry;
            }
            carry = MissingCell;
            for (int c = CellsPerSide - 1; c >= 0; c--)
            {
                if (grid[rowStart + c] != MissingCell) carry = grid[rowStart + c];
                else if (carry != MissingCell) grid[rowStart + c] = carry;
            }
        }
        // any rows that were entirely missing: copy from vertical neighbors
        for (int r = 0; r < CellsPerSide; r++)
        {
            if (grid[r * CellsPerSide] != MissingCell) continue;
            for (int rr = 1; rr < CellsPerSide; rr++)
            {
                int src = (r + rr < CellsPerSide ? r + rr : r - rr);
                if ((uint)src < CellsPerSide && grid[src * CellsPerSide] != MissingCell)
                {
                    Array.Copy(grid, src * CellsPerSide, grid, r * CellsPerSide, CellsPerSide);
                    break;
                }
            }
        }
    }
}
