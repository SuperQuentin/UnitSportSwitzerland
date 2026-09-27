using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Reduces a tile's 0.5 m cell-center lattice to its corner-aligned vertex grid. Each vertex is
/// the rounded average of every source cell inside its own footprint — <see cref="Ratio"/> x
/// <see cref="Ratio"/> cells, one full non-overlapping block per vertex. Footprints tile the plane,
/// so every cell belongs to exactly one vertex and the reduction is a single pass over the cells.
///
/// <para>
/// Perimeter vertices sit on a lattice line shared with the neighbour tiles, and their footprint
/// straddles it. Rather than reach into the neighbours' cells (which is what forced the old
/// pipeline to keep a 50 GB cache of every parsed tile and build tiles one at a time behind a
/// locked LRU), each tile emits the <em>partial sum</em> of its own cells for its perimeter
/// vertices — an <see cref="EdgeStrips"/>, 16 KB. The final perimeter value is the sum of the
/// partials of every tile touching that lattice point divided by their cell count: the same cells
/// in the same integer arithmetic as before, so seams stay bit-identical, and a tile missing from
/// the dataset simply contributes nothing (as it did when its cells were skipped).
/// </para>
/// </summary>
public static class TileReducer
{
    public const int N = ChunkFormat.GridSize;
    public const int Cells = XyzParser.CellsPerSide;

    /// <summary>Source cells per output vertex, along one axis (2 at today's 1 m spacing).</summary>
    public static readonly int Ratio;

    /// <summary>Cell index (along one axis) -> the vertex whose footprint holds it.</summary>
    private static readonly int[] VertexOf = new int[Cells];

    /// <summary>How many of a tile's own cells (along one axis) land on each vertex.</summary>
    public static readonly int[] OwnCount = new int[N];

    static TileReducer()
    {
        if (Cells % (N - 1) != 0)
            throw new InvalidOperationException($"{Cells} cells do not divide into {N - 1} vertex spans");
        Ratio = Cells / (N - 1);
        // vertex r's footprint is centered on cell ratio*r, spanning half the ratio either side:
        // offsets -half .. ratio-half-1 (ratio 2: the 2x2 block straddling the vertex)
        int half = Ratio / 2;
        for (int i = 0; i < Cells; i++)
        {
            int v = (i + half) / Ratio;
            VertexOf[i] = v;
            OwnCount[v]++;
        }
    }

    /// <summary>
    /// Writes the finished interior vertices into <paramref name="heights"/> (perimeter left for
    /// <see cref="FinishPerimeter"/>) and the perimeter partial sums into <paramref name="edges"/>.
    /// <paramref name="sums"/> is N*N scratch.
    /// </summary>
    public static void Reduce(ushort[] cells, uint[] sums, ushort[] heights, uint[] edges)
    {
        Array.Clear(sums);
        var vertexOf = VertexOf;
        for (int row = 0; row < Cells; row++)
        {
            var src = cells.AsSpan(row * Cells, Cells);
            var dst = sums.AsSpan(vertexOf[row] * N, N);
            for (int col = 0; col < src.Length; col++)
                dst[vertexOf[col]] += src[col];
        }

        for (int r = 1; r < N - 1; r++)
        {
            int rc = OwnCount[r];
            for (int c = 1; c < N - 1; c++)
            {
                uint count = (uint)(rc * OwnCount[c]);
                heights[r * N + c] = (ushort)((sums[r * N + c] + count / 2) / count);
            }
        }

        for (int i = 0; i < N; i++)
        {
            edges[EdgeStrips.North + i] = sums[i];
            edges[EdgeStrips.South + i] = sums[(N - 1) * N + i];
            edges[EdgeStrips.West + i] = sums[i * N];
            edges[EdgeStrips.East + i] = sums[i * N + N - 1];
        }
    }

    /// <summary>
    /// Completes the perimeter of <paramref name="id"/> from the edge partials of every tile around
    /// it. <paramref name="edgesOf"/> returns null for a tile outside the dataset.
    /// </summary>
    public static void FinishPerimeter(TileId id, ushort[] heights, Func<TileId, uint[]?> edgesOf)
    {
        // the 3x3 block around the tile, fetched once
        var hood = new uint[]?[9];
        for (int dN = -1; dN <= 1; dN++)
            for (int dE = -1; dE <= 1; dE++)
                hood[(dN + 1) * 3 + dE + 1] = edgesOf(new TileId(id.E + dE, id.N + dN));

        for (int c = 0; c < N; c++)
        {
            heights[c] = Combine(id, 0, c, hood);
            heights[(N - 1) * N + c] = Combine(id, N - 1, c, hood);
        }
        for (int r = 1; r < N - 1; r++)
        {
            heights[r * N] = Combine(id, r, 0, hood);
            heights[r * N + N - 1] = Combine(id, r, N - 1, hood);
        }
    }

    private static ushort Combine(TileId id, int r, int c, uint[]?[] hood)
    {
        long sum = 0, count = 0;
        for (int dN = -1; dN <= 1; dN++)
            for (int dE = -1; dE <= 1; dE++)
            {
                var edges = hood[(dN + 1) * 3 + dE + 1];
                if (edges == null) continue;
                // the same lattice point in that tile's own indices; row 0 is north, so the
                // tile to the north (N+1) sees our row 0 as its row N-1
                int rr = r + dN * (N - 1), cc = c - dE * (N - 1);
                if ((uint)rr >= N || (uint)cc >= N) continue;
                sum += EdgeStrips.PartialAt(edges, rr, cc);
                count += OwnCount[rr] * OwnCount[cc];
            }
        if (count == 0)
            throw new InvalidDataException($"Vertex ({c},{r}) of {id} has no source cells");
        return (ushort)((sum + count / 2) / count);
    }
}

/// <summary>
/// A tile's perimeter partial sums (four strips of N) and their on-disk form, a <c>.edge</c> file
/// in the cache directory. That file is what makes a build incremental: a tile already built needs
/// only these 16 KB — not its source, not its cells — to let a new neighbour finish their seam.
/// </summary>
public static class EdgeStrips
{
    public const int N = ChunkFormat.GridSize;
    public const int North = 0, South = N, West = 2 * N, East = 3 * N, Length = 4 * N;

    private const uint Magic = 0x31474445; // "EDG1"
    private const int HeaderSize = 32;
    public const long FileBytes = HeaderSize + Length * 4L;

    public static uint PartialAt(uint[] edges, int r, int c)
    {
        if (r == 0) return edges[North + c];
        if (r == N - 1) return edges[South + c];
        if (c == 0) return edges[West + r];
        if (c == N - 1) return edges[East + r];
        throw new ArgumentException($"({c},{r}) is not a perimeter vertex");
    }

    public static string PathFor(string dir, TileId id) => Path.Combine(dir, $"{id.E}_{id.N}.edge");

    /// <summary>Writes the strips, stamped with the source they came from so a changed source re-parses.</summary>
    public static void Write(string path, uint[] edges, long sourceLength, long sourceTicks)
    {
        var bytes = new byte[FileBytes];
        var h = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(h[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], (ushort)N);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], (ushort)TileReducer.Cells);
        BinaryPrimitives.WriteInt64LittleEndian(h[8..], sourceLength);
        BinaryPrimitives.WriteInt64LittleEndian(h[16..], sourceTicks);
        var body = MemoryMarshal.AsBytes(edges.AsSpan());
        if (BitConverter.IsLittleEndian) body.CopyTo(h[HeaderSize..]);
        else for (int i = 0; i < edges.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(h[(HeaderSize + i * 4)..], edges[i]);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    /// <summary>Null when absent, from another grid layout, or (if a stamp is given) from a different source.</summary>
    public static uint[]? TryRead(string path, long? sourceLength = null, long? sourceTicks = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != FileBytes) return null;
        var bytes = File.ReadAllBytes(path);
        var h = bytes.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(h[0..]) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(h[4..]) != N
            || BinaryPrimitives.ReadUInt16LittleEndian(h[6..]) != TileReducer.Cells)
            return null;
        if (sourceLength is { } len && BinaryPrimitives.ReadInt64LittleEndian(h[8..]) != len) return null;
        if (sourceTicks is { } ticks && BinaryPrimitives.ReadInt64LittleEndian(h[16..]) != ticks) return null;

        var edges = new uint[Length];
        for (int i = 0; i < Length; i++)
            edges[i] = BinaryPrimitives.ReadUInt32LittleEndian(h[(HeaderSize + i * 4)..]);
        return edges;
    }
}

public static class AtomicFile
{
    /// <summary>Write-then-rename, so an interrupted run never leaves a truncated file that looks valid.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            write(fs);
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, s => s.Write(bytes));
}
