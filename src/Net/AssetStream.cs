using System.IO.Compression;
using UnitSport.Terrain.Format;

namespace UnitSport.Net;

/// <summary>Which generated file an asset request refers to.</summary>
public enum AssetKind
{
    /// <summary>The whole terrain manifest, sent once on join.</summary>
    Manifest = 0,

    /// <summary>.terr height grid, ~490 KB — by far the largest per-tile payload.</summary>
    Chunk = 1,

    /// <summary>.road polylines.</summary>
    Roads = 2,

    /// <summary>.cover ground-cover raster, already deflate-compressed inside the format.</summary>
    Cover = 3,

    /// <summary>.trees instances.</summary>
    Trees = 4,

    /// <summary>.bldg building solids — can be 2 MB on a dense town tile.</summary>
    Buildings = 5,

    /// <summary>.holes carved terrain quads; present on very few tiles.</summary>
    Holes = 6,

    /// <summary>
    /// places.json, the searchable town index behind the Tab teleport.
    /// <para>
    /// Not tile-scoped, and easy to forget: it is the only asset the *UI* reads rather than
    /// the streamer, so a client without it connects fine, streams terrain fine, and simply
    /// shows an empty city list.
    /// </para>
    /// </summary>
    Places = 7,
}

/// <summary>
/// Shared constants and helpers for streaming generated terrain files over the ENet link.
///
/// <para>
/// The unit of transfer is the <b>raw file</b>, byte for byte. Re-encoding on the server
/// would cost CPU, risk drifting from what the preprocessor wrote, and defeat the client's
/// on-disk cache — which stores what it receives under the ordinary filename so the ordinary
/// decoders read it back with no special case.
/// </para>
/// </summary>
public static class AssetStream
{
    /// <summary>
    /// Payload bytes per fragment.
    ///
    /// ENet fragments reliable packets itself, but handing it a 2 MB packet stalls the
    /// channel until the whole thing is acknowledged. Slicing in the application keeps each
    /// send small enough to interleave, and lets the server meter bandwidth per peer.
    /// </summary>
    public const int FragmentBytes = 24 * 1024;

    /// <summary>
    /// Transfer channel for bulk data.
    ///
    /// Player transforms ride the default channel. ENet guarantees ordering per channel, so
    /// putting a multi-megabyte building tile on the same one would head-of-line block every
    /// position update behind it and make everyone else visibly stutter.
    /// </summary>
    public const int Channel = 2;

    /// <summary>Filename a kind maps to inside the chunk directory.</summary>
    public static string FileNameFor(AssetKind kind, TileId id) => kind switch
    {
        AssetKind.Manifest => "manifest.json",
        AssetKind.Places => PlaceIndex.FileName,
        AssetKind.Chunk => ChunkFormat.ChunkFileName(id),
        AssetKind.Roads => RoadFormat.FileName(id),
        AssetKind.Cover => CoverFormat.FileName(id),
        AssetKind.Trees => TreeFormat.FileName(id),
        AssetKind.Buildings => BuildingFormat.FileName(id),
        AssetKind.Holes => HoleFormat.FileName(id),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// True for kinds whose format already compresses its own payload, so deflating again
    /// only burns CPU to add a few bytes.
    /// </summary>
    public static bool IsAlreadyCompressed(AssetKind kind) => kind == AssetKind.Cover;

    /// <summary>Deflates a payload, returning null when the result is not smaller.</summary>
    public static byte[]? TryCompress(byte[] payload)
    {
        using var output = new MemoryStream(payload.Length);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(payload, 0, payload.Length);

        return output.Length < payload.Length ? output.ToArray() : null;
    }

    /// <summary>Inflates a payload produced by <see cref="TryCompress"/>.</summary>
    public static byte[] Decompress(byte[] compressed, int expectedLength)
    {
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var output = new MemoryStream(expectedLength);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// CRC-32 of a payload, used to reject a transfer that arrived truncated or interleaved
    /// wrongly before it is written into the cache. A corrupt cached chunk would otherwise be
    /// believed forever.
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
        }
        return ~crc;
    }
}
