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
    public const ushort LegacyVersion = 2;

    /// <summary>
    /// Version 3 (#1399): the same file, heights stored over -50..4900 m instead of 0..4700 m, so
    /// Mont Blanc (4806 m) is not clipped and the Camargue or the Wateringues are not clamped to
    /// 0 m. Only the quantization differs; see <see cref="HeightEncoding"/>.
    /// </summary>
    public const ushort WideVersion = 3;

    private static HeightEncoding _current = HeightEncoding.Legacy;
    private static bool _locked;

    /// <summary>
    /// How this process turns quantized heights into metres: the encoding of the region it works
    /// on, version 2 until <see cref="Use"/> or the first decoded tile says otherwise. A region is
    /// built in one encoding (its manifest's <c>formatVersion</c>), so one process-wide choice is
    /// enough and the ~60 call sites of <see cref="Dequantize"/> need no tile in hand.
    /// </summary>
    public static HeightEncoding Current => _current;

    /// <summary>The version the process writes and expects.</summary>
    public static ushort Version => _current.Version;

    /// <summary>Selects the encoding explicitly (manifest load, preprocessor flag). Overrides an earlier choice.</summary>
    public static void Use(int version)
    {
        _current = HeightEncoding.ForVersion(version);
        _locked = true;
    }

    /// <summary>
    /// A decoded tile names its encoding: take it when none was chosen yet, refuse a tile of the
    /// other encoding afterwards (heights would be silently wrong by metres).
    /// </summary>
    public static void Adopt(HeightEncoding found)
    {
        lock (typeof(ChunkFormat))
        {
            if (!_locked) { _current = found; _locked = true; }
            else if (_current.Version != found.Version)
                throw new InvalidDataException(
                    $"Tile is height format v{found.Version} but this process decodes v{_current.Version}");
        }
    }

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

    /// <summary>Max representable altitude of the current encoding (4700 m, or 4900 m in v3).</summary>
    public static double MaxHeightM => _current.MaxM;

    /// <summary>Lowest representable altitude of the current encoding (0 m, or -50 m in v3).</summary>
    public static double MinHeightM => _current.MinM;

    /// <summary>Meters per quantization step (~7.2 cm in v2, ~7.6 cm in v3).</summary>
    public static double HeightScale => _current.Scale;

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

    public static double Dequantize(ushort q) => _current.Dequantize(q);

    public static ushort Quantize(double meters) => _current.Quantize(meters);
}

/// <summary>
/// How a uint16 maps to metres: <c>MinM + q * Scale</c> over <c>MinM..MaxM</c>. Version 2 is 0..4700 m
/// (Switzerland tops out at 4634 m), version 3 is -50..4900 m for France.
/// </summary>
public readonly struct HeightEncoding
{
    public ushort Version { get; }
    public double MinM { get; }
    public double MaxM { get; }
    public double Scale { get; }

    private HeightEncoding(ushort version, double minM, double maxM)
    {
        Version = version; MinM = minM; MaxM = maxM; Scale = (maxM - minM) / 65535.0;
    }

    public static readonly HeightEncoding Legacy = new(ChunkFormat.LegacyVersion, 0.0, 4700.0);
    public static readonly HeightEncoding Wide = new(ChunkFormat.WideVersion, -50.0, 4900.0);

    public static HeightEncoding ForVersion(int version) => version switch
    {
        ChunkFormat.LegacyVersion => Legacy,
        ChunkFormat.WideVersion => Wide,
        _ => throw new InvalidDataException($"Unsupported chunk version {version}"),
    };

    public double Dequantize(ushort q) => q * Scale + MinM;

    public ushort Quantize(double meters) =>
        (ushort)Math.Clamp((long)Math.Round((meters - MinM) / Scale), 0, 65535);
}
