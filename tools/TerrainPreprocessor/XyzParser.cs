using System.Buffers.Text;
using System.IO.Compression;
using System.Runtime.InteropServices;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Streams one swissALTI3D 0.5 m tile (.xyz.zip, bare .xyz, or a legacy pass-1 <c>.raw</c>) into a
/// 2000x2000 grid of globally quantized uint16 heights (row 0 = north). Points are placed by their
/// X/Y coordinates rather than by line order, so tiles with missing points (national border)
/// degrade gracefully.
///
/// <para>
/// Numbers are read by a hand-rolled fixed-point scanner rather than <c>Utf8Parser</c>: every
/// swissALTI3D value is <c>digits.digits</c>, and <c>mantissa / 10^decimals</c> with an exact
/// mantissa and an exact power of ten is one correctly rounded IEEE division — the very double a
/// correctly rounded parser returns. So the output is bit-identical to the old parser at a fraction
/// of the cost. Anything that does not fit the fast shape (exponent, >15 digits) falls back to
/// <c>Utf8Parser</c> for that token.
/// </para>
/// </summary>
public static class XyzParser
{
    public const int CellsPerSide = 2000;
    public const ushort MissingCell = ushort.MaxValue;
    public const long RawFileBytes = (long)CellsPerSide * CellsPerSide * 2;

    private static readonly double[] Pow10 =
    {
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
    };

    /// <summary>Parses <paramref name="data"/> (the file's bytes) into <paramref name="grid"/>.</summary>
    public static void Parse(byte[] data, string sourcePath, TileId tile, ushort[] grid, byte[] textBuffer)
    {
        if (sourcePath.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
        {
            if (data.Length != RawFileBytes)
                throw new InvalidDataException($"{sourcePath}: expected {RawFileBytes} bytes, got {data.Length}");
            data.AsSpan().CopyTo(MemoryMarshal.AsBytes(grid.AsSpan()));
            return; // legacy pass-1 cache, already gap-filled
        }

        Array.Fill(grid, MissingCell);
        long filled;
        using (var ms = new MemoryStream(data, writable: false))
        {
            if (sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".xyz", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"No .xyz entry in {sourcePath}");
                using var stream = entry.Open();
                filled = ParseText(stream, sourcePath, tile, grid, textBuffer);
            }
            else
            {
                filled = ParseText(ms, sourcePath, tile, grid, textBuffer);
            }
        }

        long missing = grid.LongLength - filled;
        if (missing > 0)
        {
            Console.WriteLine($"  [warn] {tile}: {missing} missing cells, filling from row neighbors");
            FillMissing(grid);
        }
    }

    private static long ParseText(Stream stream, string sourceName, TileId tile, ushort[] grid, byte[] buffer)
    {
        double baseE = tile.MinE;
        double topN = tile.MaxN;
        long filled = 0;
        int len = 0;

        while (true)
        {
            int read = stream.Read(buffer, len, buffer.Length - len);
            bool eof = read == 0;
            len += read;

            // parse every complete line; at EOF the tail is complete too
            int end = eof ? len : buffer.AsSpan(0, len).LastIndexOf((byte)'\n') + 1;
            if (end == 0 && !eof)
            {
                if (len == buffer.Length)
                    throw new InvalidDataException($"Line longer than {buffer.Length} bytes in {sourceName}");
                continue;
            }

            filled += ParseLines(buffer.AsSpan(0, end), grid, baseE, topN, sourceName);
            if (eof) break;

            len -= end;
            if (len > 0) Buffer.BlockCopy(buffer, end, buffer, 0, len);
        }
        return filled;
    }

    private static long ParseLines(ReadOnlySpan<byte> s, ushort[] grid, double baseE, double topN, string sourceName)
    {
        long filled = 0;
        int i = 0;
        while (i < s.Length)
        {
            byte b = s[i];
            if (b == '\n' || b == '\r' || b == ' ' || b == '\t') { i++; continue; }
            if (b == 'X' || b == 'x') // "X Y Z" header
            {
                int nl = s[i..].IndexOf((byte)'\n');
                i = nl < 0 ? s.Length : i + nl + 1;
                continue;
            }

            if (!ReadNumber(s, ref i, out double x) ||
                !ReadNumber(s, ref i, out double y) ||
                !ReadNumber(s, ref i, out double z))
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
        return filled;
    }

    private static bool ReadNumber(ReadOnlySpan<byte> s, ref int i, out double value)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        int start = i;

        bool neg = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }

        long mantissa = 0;
        int digits = 0, decimals = 0;
        while (i < s.Length && (uint)(s[i] - '0') <= 9) { mantissa = mantissa * 10 + (s[i] - '0'); digits++; i++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && (uint)(s[i] - '0') <= 9)
            {
                mantissa = mantissa * 10 + (s[i] - '0');
                digits++; decimals++; i++;
            }
        }

        bool terminated = i >= s.Length || s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n';
        if (digits > 0 && digits <= 15 && terminated)
        {
            value = mantissa / Pow10[decimals];
            if (neg) value = -value;
            return true;
        }

        // unusual shape (exponent, huge mantissa): the general parser, for this token only
        int end = start;
        while (end < s.Length && s[end] != ' ' && s[end] != '\t' && s[end] != '\r' && s[end] != '\n') end++;
        i = end;
        return Utf8Parser.TryParse(s[start..end], out value, out int consumed) && consumed == end - start;
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
