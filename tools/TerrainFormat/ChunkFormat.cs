namespace UnitSport.Terrain.Format;

/// <summary>
/// Constants of the .terr chunk format (see plan: 32-byte header + gridSize^2 uint16 heights).
/// Heights are globally quantized so shared edge vertices of adjacent tiles are bit-identical.
/// </summary>
public static class ChunkFormat
{
    /// <summary>"USTC" read as little-endian uint32.</summary>
    public const uint Magic = 0x43545355;

    /// <summary>
    /// Bumped 1 -> 2 for the GridSize 501 -> 1001 change (2 m -> 1 m spacing). Old files decode
    /// fine by shape (nothing reads Version to branch layout, since the header carries GridSize
    /// implicitly via file length), but this is the marker that the *source* was rebuilt at the
    /// new resolution — a region built before this bump has 2 m tiles and must be reprocessed
    /// with `--verify` before its coarse companions and roads are regenerated.
    /// </summary>
    public const ushort Version = 2;

    /// <summary>Payload is deflate-compressed (reserved for the CDN era, unused for now).</summary>
    public const ushort FlagDeflate = 1;

    /// <summary>
    /// Vertices per tile edge; corner-aligned, so spacing = 1000 / (GridSize - 1) = 1 m.
    ///
    /// <para>
    /// 1001, not 2001 (which would match swissALTI3D's native 0.5 m exactly): 1 m spacing is a
    /// deliberate midpoint. It still recovers real detail the old 2 m grid discarded (see
    /// ChunkBuilder's averaging footprint), but at 4x the vertex count per tile rather than 0.5 m
    /// full-source-resolution's 16x — a full-region rebuild at 16x storage and preprocessing time
    /// was not worth it for a level of detail a flat-shaded, dithered, vertex-snapped renderer
    /// cannot show. GridSize-1 (1000) still divides every existing LOD/coarse stride (1, 2, 4, 10,
    /// 20) cleanly, so the ring table and coarse-tile format need no redesign.
    /// </para>
    /// </summary>
    public const int GridSize = 1001;

    public const double TileSizeM = 1000.0;

    public const double SpacingM = TileSizeM / (GridSize - 1);

    /// <summary>Max representable altitude; Switzerland tops out at 4634 m.</summary>
    public const double MaxHeightM = 4700.0;

    /// <summary>Meters per quantization step (~7.2 cm).</summary>
    public const double HeightScale = MaxHeightM / 65535.0;

    public const int HeaderSize = 32;

    public static string ChunkFileName(TileId id) => $"chunk_{id.E}_{id.N}.terr";

    /// <summary>
    /// Stride of the coarse companion tile. Ten because it is the finest stride the LOD rings
    /// ever ask for beyond the road ring (10 at d 4..6, 20 at d 7..9), and 20 is a multiple of
    /// it — so one small file serves every ring that is allowed to use one. 51x51 = 5.2 KB
    /// against the full tile's 490 KB.
    /// </summary>
    public const int CoarseStride = 10;

    /// <summary>
    /// The same tile decimated, for rings that render one vertex in ten or twenty. Absent for
    /// regions built before the format existed, in which case the full tile is read instead.
    /// </summary>
    public static string CoarseFileName(TileId id) => $"chunk_{id.E}_{id.N}.terrc";

    public static double Dequantize(ushort q) => q * HeightScale;

    public static ushort Quantize(double meters) =>
        (ushort)Math.Clamp((long)Math.Round(meters / HeightScale), 0, 65535);
}
