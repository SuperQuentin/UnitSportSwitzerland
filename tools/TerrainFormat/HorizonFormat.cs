using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UnitSport.Terrain.Format;

/// <summary>
/// The far horizon: every tile of the region decimated to a 100 m lattice and packed into ONE
/// region-wide file, <c>horizon.bin</c>, read once at boot.
///
/// <para>
/// This is what makes the "open map" possible for almost nothing. The LOD rings end where the
/// per-tile files stop being worth reading — even the 5 KB coarse companion is 5 KB per tile,
/// and a 60 km horizon is 11,000 tiles. At 100 m spacing a tile is 11x11 samples, 242 bytes,
/// and the whole region is a single small file: 6,699 tiles -> 1.6 MB, all of Switzerland ->
/// ~10 MB. The Alps' ridges are kilometre-scale features, so a 100 m lattice keeps every peak's
/// shape while a 1 km one (the manifest's per-tile min/max) turns them into stair-steps.
/// </para>
///
/// <para>
/// Sparse — one record per tile that exists — rather than a dense raster over the region's
/// bounding box, because the built region is not a rectangle and the dense form of a country
/// at 100 m is 15 MB of mostly nothing.
/// </para>
///
/// <para>
/// Since #298 the heights under a lake are its bed, so version 2 carries the still water level
/// at the same samples after the heights, and the horizon draws the higher of the two
/// (<see cref="HorizonIndex.Surface"/>) or every lake would be a pit from afar. The heights stay
/// the ground: the generated fill blends on them as knots.
/// </para>
/// </summary>
public static class HorizonFormat
{
    /// <summary>"USTH" read as little-endian uint32.</summary>
    public const uint Magic = 0x48545355;

    /// <summary>
    /// 1: heights only. 2 (#298): the same heights, then a water section (<see cref="Encode"/>).
    /// A file is written as version 1 when no tile has water, so a region without any reads
    /// exactly as before; the decoder takes both.
    /// </summary>
    public const ushort Version = 2;

    /// <summary>Metres between samples. Must be a multiple of <see cref="ChunkFormat.CoarseStride"/>
    /// so the coarse companion tile can serve the pass without touching the full grid.</summary>
    public const int SpacingM = 100;

    /// <summary>Full-resolution vertices per horizon sample.</summary>
    public const int Stride = (int)(SpacingM / ChunkFormat.SpacingM);

    /// <summary>Samples per tile edge, corner-aligned like the tiles themselves (11).</summary>
    public const int SamplesPerSide = (ChunkFormat.GridSize - 1) / Stride + 1;

    public const int SamplesPerTile = SamplesPerSide * SamplesPerSide;

    public const string FileName = "horizon.bin";

    public const int HeaderSize = 16;

    public const int RecordSize = 8 + SamplesPerTile * 2;

    /// <summary>The 11x11 samples of one tile, row 0 = north edge, col 0 = west edge.</summary>
    public static ushort[] Extract(ChunkGrid grid)
    {
        var samples = new ushort[SamplesPerTile];
        for (int r = 0; r < SamplesPerSide; r++)
            for (int c = 0; c < SamplesPerSide; c++)
                samples[r * SamplesPerSide + c] = grid.HeightAt(c * Stride, r * Stride);
        return samples;
    }

    /// <summary>
    /// The still water level at the same 11x11 samples (#298), quantized like the heights, 0 where
    /// dry; null when no sample of the tile is wet.
    /// </summary>
    public static ushort[]? ExtractWater(WaterGrid water)
    {
        var levels = new ushort[SamplesPerTile];
        bool any = false;
        for (int r = 0; r < SamplesPerSide; r++)
            for (int c = 0; c < SamplesPerSide; c++)
            {
                ushort q = water.Levels[r * Stride * ChunkFormat.GridSize + c * Stride];
                levels[r * SamplesPerSide + c] = q;
                any |= q != 0;
            }
        return any ? levels : null;
    }

    /// <summary>
    /// Version 2 appends, after the height records, an i32 count and one record per tile with
    /// water: i32 E, i32 N, 121 u16 levels (0 = dry). Written as version 1 (no section) when
    /// <paramref name="water"/> is null or empty.
    /// </summary>
    public static void Encode(IReadOnlyDictionary<TileId, ushort[]> tiles, Stream output,
        IReadOnlyDictionary<TileId, ushort[]>? water = null)
    {
        bool withWater = water is { Count: > 0 };
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], withWater ? Version : (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], SpacingM);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], SamplesPerSide);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0); // reserved
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], tiles.Count);
        output.Write(header);

        // sorted so the file is deterministic and a diff between two builds means something
        WriteRecords(tiles, output);
        if (!withWater) return;

        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, water!.Count);
        output.Write(count);
        WriteRecords(water, output);
    }

    private static void WriteRecords(IReadOnlyDictionary<TileId, ushort[]> tiles, Stream output)
    {
        Span<byte> rec = stackalloc byte[RecordSize];
        foreach (var (id, samples) in tiles.OrderBy(kv => kv.Key.N).ThenBy(kv => kv.Key.E))
        {
            if (samples.Length != SamplesPerTile)
                throw new ArgumentException($"{id}: expected {SamplesPerTile} samples, got {samples.Length}");
            BinaryPrimitives.WriteInt32LittleEndian(rec[0..], id.E);
            BinaryPrimitives.WriteInt32LittleEndian(rec[4..], id.N);
            for (int i = 0; i < SamplesPerTile; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(rec[(8 + 2 * i)..], samples[i]);
            output.Write(rec);
        }
    }

    public static HorizonIndex Decode(Stream input)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        input.ReadExactly(header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != Magic) throw new InvalidDataException($"Bad horizon magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version is not (1 or Version)) throw new InvalidDataException($"Unsupported horizon version {version}");
        int spacing = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        int side = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (spacing != SpacingM || side != SamplesPerSide)
            throw new InvalidDataException($"Unsupported horizon lattice {spacing} m / {side} samples");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);

        var tiles = ReadRecords(input, count);
        if (version == 1) return new HorizonIndex(tiles);

        Span<byte> countBytes = stackalloc byte[4];
        input.ReadExactly(countBytes);
        return new HorizonIndex(tiles, ReadRecords(input, BinaryPrimitives.ReadInt32LittleEndian(countBytes)));
    }

    private static Dictionary<TileId, ushort[]> ReadRecords(Stream input, int count)
    {
        var tiles = new Dictionary<TileId, ushort[]>(count);
        Span<byte> rec = stackalloc byte[RecordSize];
        for (int t = 0; t < count; t++)
        {
            input.ReadExactly(rec);
            var id = new TileId(
                BinaryPrimitives.ReadInt32LittleEndian(rec[0..]),
                BinaryPrimitives.ReadInt32LittleEndian(rec[4..]));
            var samples = new ushort[SamplesPerTile];
            for (int i = 0; i < SamplesPerTile; i++)
                samples[i] = BinaryPrimitives.ReadUInt16LittleEndian(rec[(8 + 2 * i)..]);
            tiles[id] = samples;
        }
        return tiles;
    }
}

/// <summary>
/// Decoded <c>horizon.bin</c>: the 11x11 lattice of every tile, by tile, and the still water level
/// at those samples where there is water (#298). Heights are the ground (a lake's bed): the
/// generated fill blends on them. What the horizon draws is <see cref="Surface"/>.
/// </summary>
public sealed class HorizonIndex
{
    private readonly Dictionary<TileId, ushort[]> _tiles;
    private readonly Dictionary<TileId, ushort[]> _water;

    public HorizonIndex(Dictionary<TileId, ushort[]> tiles, Dictionary<TileId, ushort[]>? water = null)
    {
        _tiles = tiles;
        _water = water ?? new Dictionary<TileId, ushort[]>();
    }

    public int Count => _tiles.Count;

    public IEnumerable<TileId> Tiles => _tiles.Keys;

    /// <summary>Tiles with a wet sample, and their 11x11 levels (0 = dry).</summary>
    public IReadOnlyDictionary<TileId, ushort[]> Water => _water;

    public bool Contains(TileId id) => _tiles.ContainsKey(id);

    public bool TryGet(TileId id, out ushort[] samples) => _tiles.TryGetValue(id, out samples!);

    public bool TryGetWater(TileId id, out ushort[] levels) => _water.TryGetValue(id, out levels!);

    /// <summary>Height in metres at a sample of a tile, row 0 = north.</summary>
    public double HeightMetersAt(TileId id, int col, int row) =>
        ChunkFormat.Dequantize(_tiles[id][row * HorizonFormat.SamplesPerSide + col]);

    /// <summary>
    /// What the horizon draws at a sample: the water level where it stands above the ground, else
    /// the ground; <paramref name="wet"/> says which.
    /// </summary>
    public static ushort Surface(ushort height, ushort[]? levels, int index, out bool wet)
    {
        ushort level = levels?[index] ?? 0;
        wet = level != 0 && level >= height;
        return wet ? level : height;
    }

    /// <summary>The drawn surface in metres at a sample of a tile (row 0 = north): ground or water, the higher.</summary>
    public double SurfaceMetersAt(TileId id, int col, int row)
    {
        int i = row * HorizonFormat.SamplesPerSide + col;
        _water.TryGetValue(id, out var levels);
        return ChunkFormat.Dequantize(Surface(_tiles[id][i], levels, i, out _));
    }
}
