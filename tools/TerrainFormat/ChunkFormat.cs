namespace UnitSport.Terrain.Format;

/// <summary>
/// Constants of the .terr chunk format (see plan: 32-byte header + gridSize^2 uint16 heights).
/// Heights are globally quantized so shared edge vertices of adjacent tiles are bit-identical.
/// </summary>
public static class ChunkFormat
{
    /// <summary>"USTC" read as little-endian uint32.</summary>
    public const uint Magic = 0x43545355;

    public const ushort Version = 1;

    /// <summary>Payload is deflate-compressed (reserved for the CDN era, unused for now).</summary>
    public const ushort FlagDeflate = 1;

    /// <summary>Vertices per tile edge; corner-aligned, so spacing = 1000 / (GridSize - 1) = 2 m.</summary>
    public const int GridSize = 501;

    public const double TileSizeM = 1000.0;

    public const double SpacingM = TileSizeM / (GridSize - 1);

    /// <summary>Max representable altitude; Switzerland tops out at 4634 m.</summary>
    public const double MaxHeightM = 4700.0;

    /// <summary>Meters per quantization step (~7.2 cm).</summary>
    public const double HeightScale = MaxHeightM / 65535.0;

    public const int HeaderSize = 32;

    public static string ChunkFileName(TileId id) => $"chunk_{id.E}_{id.N}.terr";

    public static double Dequantize(ushort q) => q * HeightScale;

    public static ushort Quantize(double meters) =>
        (ushort)Math.Clamp((long)Math.Round(meters / HeightScale), 0, 65535);
}
