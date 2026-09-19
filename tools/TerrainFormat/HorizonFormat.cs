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
/// </summary>
public static class HorizonFormat
{
    /// <summary>"USTH" read as little-endian uint32.</summary>
    public const uint Magic = 0x48545355;

    public const ushort Version = 1;

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

    public static void Encode(IReadOnlyDictionary<TileId, ushort[]> tiles, Stream output)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], SpacingM);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], SamplesPerSide);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0); // reserved
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], tiles.Count);
        output.Write(header);

        Span<byte> rec = stackalloc byte[RecordSize];
        // sorted so the file is deterministic and a diff between two builds means something
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
        if (version != Version) throw new InvalidDataException($"Unsupported horizon version {version}");
        int spacing = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        int side = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (spacing != SpacingM || side != SamplesPerSide)
            throw new InvalidDataException($"Unsupported horizon lattice {spacing} m / {side} samples");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);

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
        return new HorizonIndex(tiles);
    }
}

/// <summary>Decoded <c>horizon.bin</c>: the 11x11 lattice of every tile, by tile.</summary>
public sealed class HorizonIndex
{
    private readonly Dictionary<TileId, ushort[]> _tiles;

    public HorizonIndex(Dictionary<TileId, ushort[]> tiles) => _tiles = tiles;

    public int Count => _tiles.Count;

    public IEnumerable<TileId> Tiles => _tiles.Keys;

    public bool Contains(TileId id) => _tiles.ContainsKey(id);

    public bool TryGet(TileId id, out ushort[] samples) => _tiles.TryGetValue(id, out samples!);

    /// <summary>Height in metres at a sample of a tile, row 0 = north.</summary>
    public double HeightMetersAt(TileId id, int col, int row) =>
        ChunkFormat.Dequantize(_tiles[id][row * HorizonFormat.SamplesPerSide + col]);
}
