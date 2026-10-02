using System.IO.Compression;

namespace UnitSport.Terrain.Format;

/// <summary>
/// The still water surface of one tile (#298), on the same 1001x1001 vertex lattice as the
/// height grid and the cover raster, so a vertex, its cover and its water level share an index.
///
/// <para>
/// Since #298 the <c>.terr</c> heights under water are the <b>bed</b> (swissBATHY3D where surveyed,
/// a synthetic shelf and slope elsewhere), so the surface swissALTI3D used to give for free lives
/// here instead: <see cref="Levels"/> holds the quantized level (<see cref="ChunkFormat.Quantize"/>,
/// the same 7.2 cm steps as the heights, so a level is bit-identical to the flat swissALTI3D
/// surface it came from) at every wet vertex and 0 at a dry one. A river keeps its downstream
/// gradient, a lake is flat. <see cref="Fetch"/> is a rough wind fetch per wet vertex, for waves:
/// the smaller of the water body's size and the local width, in <see cref="FetchUnitM"/> steps.
/// </para>
///
/// <para>
/// Only tiles with water get a file. Layout: the 20-byte <see cref="TileHeader"/> (magic "USWL",
/// flags 1 = deflate, count = cell side 1001), then a deflate stream of the 1001² u16 levels
/// (little-endian) followed by the 1001² fetch bytes. Shared edge vertices of two tiles hold the
/// same values, like the heights.
/// </para>
/// </summary>
public static class WaterFormat
{
    /// <summary>"USWL" little-endian.</summary>
    public const uint Magic = 0x4C575355;

    public const ushort Version = 1;
    public const int HeaderSize = TileHeader.Size;
    public const int Size = ChunkFormat.GridSize;

    /// <summary>Metres per fetch step: 255 = 5.1 km or more, open lake.</summary>
    public const double FetchUnitM = 20.0;

    public static string FileName(TileId id) => $"water_{id.E}_{id.N}.water";

    public static byte QuantizeFetch(double metres) =>
        (byte)Math.Clamp((int)Math.Round(metres / FetchUnitM), 1, 255);

    public static void Encode(WaterLayer layer, Stream output)
    {
        // flags 1 = deflate; the count word holds the cell side, as in .cover
        new TileHeader(Magic, Version, 1, layer.Id, Size).Write(output);

        using var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
        var bytes = new byte[layer.Levels.Length * 2];
        for (int i = 0; i < layer.Levels.Length; i++)
        {
            bytes[2 * i] = (byte)layer.Levels[i];
            bytes[2 * i + 1] = (byte)(layer.Levels[i] >> 8);
        }
        deflate.Write(bytes);
        deflate.Write(layer.Fetch);
    }

    public static WaterLayer Decode(Stream input)
    {
        var header = TileHeader.Read(input, Magic, "water");
        header.CheckVersion(Version, "water");
        if (header.Count != Size)
            throw new InvalidDataException($"Unsupported water size {header.Count}");

        int n = Size * Size;
        var bytes = new byte[n * 2];
        var fetch = new byte[n];
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        deflate.ReadExactly(bytes);
        deflate.ReadExactly(fetch);

        var levels = new ushort[n];
        for (int i = 0; i < n; i++) levels[i] = (ushort)(bytes[2 * i] | bytes[2 * i + 1] << 8);
        return new WaterLayer(header.Id, levels, fetch);
    }
}

/// <summary>
/// One tile's decoded water layer (<see cref="WaterFormat"/>). Row 0 is the north edge, column 0
/// the west edge, exactly as in <see cref="ChunkGrid"/>.
/// </summary>
public sealed class WaterLayer
{
    public const int Size = WaterFormat.Size;

    public TileId Id { get; }

    /// <summary>Quantized still level per vertex (<see cref="ChunkFormat.Quantize"/>); 0 = dry.</summary>
    public ushort[] Levels { get; }

    /// <summary>Fetch per vertex in <see cref="WaterFormat.FetchUnitM"/> steps; 0 = dry.</summary>
    public byte[] Fetch { get; }

    public WaterLayer(TileId id, ushort[] levels, byte[] fetch)
    {
        if (levels.Length != Size * Size || fetch.Length != Size * Size)
            throw new ArgumentException($"Expected {Size}^2 water cells, got {levels.Length} / {fetch.Length}");
        Id = id;
        Levels = levels;
        Fetch = fetch;
    }

    public static WaterLayer Dry(TileId id) => new(id, new ushort[Size * Size], new byte[Size * Size]);

    public bool IsWet(int col, int row) => Levels[row * Size + col] != 0;

    public double LevelMetersAt(int col, int row) => ChunkFormat.Dequantize(Levels[row * Size + col]);

    public int WetCount
    {
        get
        {
            int n = 0;
            foreach (ushort l in Levels) if (l != 0) n++;
            return n;
        }
    }

    /// <summary>
    /// The still level at an LV95 position: the bilinear weights of the cell's wet corners only,
    /// renormalised, so a point on the shore takes the level of the water beside it. False when no
    /// corner of the cell is wet. Positions outside the tile clamp to its edge.
    /// </summary>
    public bool TrySampleLevel(double lv95E, double lv95N, out double level)
    {
        const int last = Size - 1;
        double u = Math.Clamp((lv95E - Id.MinE) / ChunkFormat.SpacingM, 0, last);
        double v = Math.Clamp((Id.MaxN - lv95N) / ChunkFormat.SpacingM, 0, last);
        int c0 = Math.Min((int)u, last - 1), r0 = Math.Min((int)v, last - 1);
        double fu = u - c0, fv = v - r0;

        double sum = 0, weight = 0;
        Add(Levels[r0 * Size + c0], (1 - fu) * (1 - fv));
        Add(Levels[r0 * Size + c0 + 1], fu * (1 - fv));
        Add(Levels[(r0 + 1) * Size + c0], (1 - fu) * fv);
        Add(Levels[(r0 + 1) * Size + c0 + 1], fu * fv);

        if (weight <= 0)
        {
            // a corner exactly on the cell edge has weight 0 but is still the nearest water
            level = 0;
            ushort any = Math.Max(Math.Max(Levels[r0 * Size + c0], Levels[r0 * Size + c0 + 1]),
                Math.Max(Levels[(r0 + 1) * Size + c0], Levels[(r0 + 1) * Size + c0 + 1]));
            if (any == 0) return false;
            level = ChunkFormat.Dequantize(any);
            return true;
        }
        level = sum / weight;
        return true;

        void Add(ushort q, double w)
        {
            if (q == 0 || w <= 0) return;
            sum += ChunkFormat.Dequantize(q) * w;
            weight += w;
        }
    }
}
