using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// A generated stand-in world for a copy of the game with no terrain data at all: an alpine
/// valley with a river, a road and a railway along its floor, villages strung along the road,
/// farms, forest up to a tree line, rock and snow above it, vineyards on the sunny side.
///
/// <para>
/// It is only a fallback. A fresh clone has no <c>terrain_chunks/</c> (the generated data is
/// 5.3 GB and not in the repository), and until the preprocessor is run or a server is joined
/// the world used to be an empty void — nothing to stand on, so nothing in the game could be
/// tried. <see cref="FallbackChunkSource"/> serves these tiles through the ordinary
/// <see cref="IChunkSource"/> seam in the ordinary formats, so every system that reads terrain,
/// roads, cover, trees or buildings works on them unchanged; <see cref="ChunkManager.RetireFallback"/>
/// throws all of it away the moment real tiles become available.
/// </para>
///
/// <para>
/// Everything is a pure function of LV95 position, so it is thread-safe, needs no state per tile,
/// and a vertex on a tile edge gets the same height from both tiles that share it — the seams are
/// bit-identical after quantisation for the same reason the real ones are. Distances along the
/// valley are measured from <see cref="CenterE"/>/<see cref="CenterN"/>, which is placed on the
/// main road in the middle of a village, so the default spawn lands somewhere worth looking at.
/// </para>
/// </summary>
public sealed partial class ProceduralWorld
{
    /// <summary>Tiles either side of the centre that exist, i.e. an 81 x 81 km square.</summary>
    public const int RadiusTiles = 40;

    /// <summary>Horizon reach beyond the playable square: the default 60 km horizon from its edge.</summary>
    public const int HorizonRadiusTiles = RadiusTiles + 60;

    public double CenterE { get; }
    public double CenterN { get; }
    private readonly TileId _center;

    public ProceduralWorld(double centerE, double centerN)
    {
        CenterE = centerE;
        CenterN = centerN;
        _center = TileId.FromLv95(centerE, centerN);
        _axis0 = RawAxis(0);
    }

    public bool Contains(TileId id) =>
        Math.Abs(id.E - _center.E) <= RadiusTiles && Math.Abs(id.N - _center.N) <= RadiusTiles;

    public IEnumerable<TileId> Tiles
    {
        get
        {
            for (int n = -RadiusTiles; n <= RadiusTiles; n++)
                for (int e = -RadiusTiles; e <= RadiusTiles; e++)
                    yield return new TileId(_center.E + e, _center.N + n);
        }
    }

    // ---- the valley ------------------------------------------------------------------------
    //
    // One valley running roughly east-west through the centre. Along it everything is a
    // function of x (metres east of the centre) alone, so a grid evaluates it once per column.

    private const double RiverOffset = -200;     // river, south of the road
    private const double RailOffset = -330;      // railway, across the river from the road
    private const double FloorOffset = -150;     // middle of the flat floor
    private const double RiverHalf = 11;         // flat bed, water drawn inside it
    private const double RiverBank = 20;         // bank top
    private const double WallSpan = 4200;        // floor edge to the top of the valley wall

    private readonly double _axis0;

    private static double RawAxis(double x) => 1400 * Math.Sin(x / 4300 + 0.4) + 380 * Math.Sin(x / 1650 + 1.9);

    /// <summary>The main road's northing offset at x; 0 at the centre.</summary>
    private double Axis(double x) => RawAxis(x) - _axis0;

    /// <summary>dAxis/dx, for the road's direction.</summary>
    private static double AxisSlope(double x) =>
        1400 / 4300.0 * Math.Cos(x / 4300 + 0.4) + 380 / 1650.0 * Math.Cos(x / 1650 + 1.9);

    private readonly record struct Column(double Axis, double Floor, double HalfWidth);

    private Column ColumnAt(double x) => new(
        Axis(x),
        // falls gently toward the west, monotonic so the river always runs one way
        520 + 0.0035 * x + 25 * Math.Sin(x / 9000),
        520 + 180 * Math.Sin(x / 2900 + 0.7));

    /// <summary>Terrain height in metres at an LV95 position.</summary>
    public double Height(double e, double n) => Height(null, e, n);

    private double Height(Lattice? lattice, double e, double n)
    {
        double x = e - CenterE;
        var (massif, detail) = SampleNoise(lattice, e, n);
        return HeightAt(x, n - CenterN, ColumnAt(x), massif, detail);
    }

    private static double HeightAt(double x, double y, in Column c, double massif, double detail)
    {
        // Distance beyond the edge of the flat floor, then an S-shaped rise over a few km: a
        // gentle foot where the fans spill onto the floor, walls of 35-40 degrees in the middle,
        // easing off into the high massif.
        double d = Math.Abs(y - (c.Axis + FloorOffset)) - c.HalfWidth;
        double rise = d <= 0 ? 0 : d >= WallSpan ? 1 : SmoothStep(0, WallSpan, d);
        double h = c.Floor + massif * rise + detail * (1.0 + 40 * rise);

        // the river: a flat bed a couple of metres below the floor, so the water surface built
        // from it (WaterMeshBuilder) is level across and sits inside its banks
        double dr = Math.Abs(y - (c.Axis + RiverOffset));
        if (dr < RiverBank)
        {
            double bed = c.Floor - 2.2;
            h = bed + (h - bed) * SmoothStep(RiverHalf, RiverBank, dr);
        }
        return h;
    }

    // ---- the noise lattice -----------------------------------------------------------------
    //
    // The mountains and the ground's roughness are noise, and noise is the expensive part: ten
    // octaves per vertex made a full-resolution tile on a slope take 320 ms. The finest octave
    // is 60 m across, so sampling it on a 5 m lattice and interpolating loses nothing a 1 m mesh
    // can show, and costs 41 thousand evaluations per tile instead of a million.
    //
    // The lattice is anchored to LV95 multiples of 5 m, so its values are a function of position
    // alone: two tiles interpolate the same numbers at a shared edge, and every stride-10 and
    // horizon sample lands exactly on a lattice point, where interpolation returns the corner
    // unchanged - which is what keeps the coarse tile equal to the decimated full one.

    private const int LatticeM = 5;
    private const int LatticeBorder = 2;   // cells beyond the tile, for slopes at its edges
    private const int LatticeSide = (int)(ChunkFormat.TileSizeM / LatticeM) + 1 + 2 * LatticeBorder;

    private sealed class Lattice
    {
        public long I0, J0;   // lattice index of the first sample, east and north
        public readonly double[] Massif = new double[LatticeSide * LatticeSide];
        public readonly double[] Detail = new double[LatticeSide * LatticeSide];
    }

    private double MassifNoise(long i, long j)
    {
        double x = i * (double)LatticeM - CenterE, y = j * (double)LatticeM - CenterN;
        return 1900 + 700 * Noise.Fbm(x / 6500, y / 6500, 3, 11) + 1500 * Noise.Ridged(x / 2700, y / 2700, 4, 23);
    }

    private double DetailNoise(long i, long j)
    {
        double x = i * (double)LatticeM - CenterE, y = j * (double)LatticeM - CenterN;
        return Noise.Fbm(x / 240, y / 240, 3, 37);
    }

    private static (long Index, double Frac) Cell(double v)
    {
        double cell = Math.Floor(v / LatticeM);
        return ((long)cell, (v - cell * LatticeM) / LatticeM);
    }

    private static double Bilerp(double a, double b, double c, double d, double u, double v)
    {
        double south = a + (b - a) * u;
        double north = c + (d - c) * u;
        return south + (north - south) * v;
    }

    /// <summary>
    /// Massif and roughness at a point: from a tile's lattice when it covers the point, else
    /// from the four corners evaluated on the spot. Both paths do the same arithmetic on the
    /// same corner values, so they agree to the bit.
    /// </summary>
    private (double Massif, double Detail) SampleNoise(Lattice? lattice, double e, double n)
    {
        var (i, u) = Cell(e);
        var (j, v) = Cell(n);
        if (lattice != null)
        {
            long li = i - lattice.I0, lj = j - lattice.J0;
            if (li >= 0 && lj >= 0 && li + 1 < LatticeSide && lj + 1 < LatticeSide)
            {
                int k = (int)(lj * LatticeSide + li);
                var m = lattice.Massif;
                var d = lattice.Detail;
                return (Bilerp(m[k], m[k + 1], m[k + LatticeSide], m[k + LatticeSide + 1], u, v),
                    Bilerp(d[k], d[k + 1], d[k + LatticeSide], d[k + LatticeSide + 1], u, v));
            }
        }
        return (Bilerp(MassifNoise(i, j), MassifNoise(i + 1, j), MassifNoise(i, j + 1), MassifNoise(i + 1, j + 1), u, v),
            Bilerp(DetailNoise(i, j), DetailNoise(i + 1, j), DetailNoise(i, j + 1), DetailNoise(i + 1, j + 1), u, v));
    }

    private readonly Dictionary<TileId, Lattice> _lattices = new();
    private readonly Queue<TileId> _latticeOrder = new();

    /// <summary>A tile's lattice, kept for a few tiles: its grid, cover and trees all read it.</summary>
    private Lattice LatticeFor(TileId id)
    {
        lock (_lattices)
            if (_lattices.TryGetValue(id, out var cached)) return cached;

        var lattice = new Lattice
        {
            I0 = (long)(id.MinE / LatticeM) - LatticeBorder,
            J0 = (long)(id.MinN / LatticeM) - LatticeBorder,
        };
        for (int lj = 0; lj < LatticeSide; lj++)
            for (int li = 0; li < LatticeSide; li++)
            {
                lattice.Massif[lj * LatticeSide + li] = MassifNoise(lattice.I0 + li, lattice.J0 + lj);
                lattice.Detail[lj * LatticeSide + li] = DetailNoise(lattice.I0 + li, lattice.J0 + lj);
            }

        lock (_lattices)
        {
            if (_lattices.TryAdd(id, lattice))
            {
                _latticeOrder.Enqueue(id);
                while (_latticeOrder.Count > 16) _lattices.Remove(_latticeOrder.Dequeue());
            }
        }
        return lattice;
    }

    // ---- terrain grids ---------------------------------------------------------------------

    /// <summary>
    /// The height grid of a tile at a stride. Stride 10 is the coarse companion; evaluated at the
    /// same points, it is exactly the decimation of the full grid, as the real one is.
    /// </summary>
    public ChunkGrid BuildGrid(TileId id, int stride, Blend? blend = null)
    {
        CheckBlend(id, blend);
        int size = (ChunkFormat.GridSize - 1) / stride + 1;
        var heights = new ushort[size * size];
        double step = ChunkFormat.SpacingM * stride;
        // coarse samples sit on lattice points: no reason to fill the whole lattice for them
        bool onLattice = step % LatticeM == 0;
        var lattice = onLattice ? null : LatticeFor(id);
        if (blend != null && !onLattice) blend.PrepareLattice();

        var columns = new Column[size];
        for (int c = 0; c < size; c++) columns[c] = ColumnAt(id.MinE + c * step - CenterE);
        // at stride 1 the blend is added a row at a time: the same numbers as Correction per point
        var correction = blend != null && stride == 1 ? new double[size] : null;

        // row-major, so the writes and the lattice reads both walk memory in order
        for (int r = 0; r < size; r++)
        {
            double n = id.MaxN - r * step;
            if (correction != null) blend!.CorrectionRow(n, correction);
            for (int c = 0; c < size; c++)
            {
                double e = id.MinE + c * step;
                var (massif, detail) = onLattice
                    ? OnLattice(e, n)
                    : SampleNoise(lattice, e, n);
                double h = HeightAt(e - CenterE, n - CenterN, columns[c], massif, detail);
                if (correction != null) h += correction[c];
                else if (blend != null) h += blend.Correction(e, n);
                heights[r * size + c] = ChunkFormat.Quantize(h);
            }
        }

        // a vertex on a real tile's edge takes the real tile's height, to the bit
        if (blend != null)
            for (int r = 0; r < size; r++)
            {
                bool edgeRow = r == 0 || r == size - 1;
                for (int c = 0; c < size; c += edgeRow ? 1 : size - 1)
                    if (blend.TryRealVertex(id.MinE + c * step, id.MaxN - r * step, out ushort real))
                        heights[r * size + c] = real;
            }

        ushort min = ushort.MaxValue, max = 0;
        foreach (ushort q in heights)
        {
            if (q < min) min = q;
            if (q > max) max = q;
        }
        return new ChunkGrid(id, heights,
            (float)ChunkFormat.Dequantize(min), (float)ChunkFormat.Dequantize(max), stride);
    }

    private static void CheckBlend(TileId id, Blend? blend)
    {
        if (blend != null && blend.Tile != id)
            throw new ArgumentException($"Blend for {blend.Tile} used to build {id}");
    }

    /// <summary>The noise at a lattice point, which is exactly what interpolating there returns.</summary>
    private (double Massif, double Detail) OnLattice(double e, double n)
    {
        long i = (long)Math.Floor(e / LatticeM), j = (long)Math.Floor(n / LatticeM);
        return (MassifNoise(i, j), DetailNoise(i, j));
    }

    /// <summary>The 100 m far-horizon lattice, well past the playable square.</summary>
    public HorizonIndex BuildHorizon()
    {
        var ids = new List<TileId>();
        for (int dn = -HorizonRadiusTiles; dn <= HorizonRadiusTiles; dn++)
            for (int de = -HorizonRadiusTiles; de <= HorizonRadiusTiles; de++)
                ids.Add(new TileId(_center.E + de, _center.N + dn));

        var samples = new ushort[ids.Count][];
        Parallel.For(0, ids.Count, t => samples[t] = HorizonSamples(ids[t], null));

        var tiles = new Dictionary<TileId, ushort[]>(ids.Count);
        for (int t = 0; t < ids.Count; t++) tiles[ids[t]] = samples[t];
        return new HorizonIndex(tiles);
    }

    /// <summary>
    /// One tile's 11x11 horizon samples. A blend needs only its real knots here: at a 100 m point
    /// D is exactly zero, and a sample on a real tile's edge copies that tile's knot, so these are
    /// the grid's own heights at those points.
    /// </summary>
    public ushort[] HorizonSamples(TileId id, Blend? blend)
    {
        CheckBlend(id, blend);
        const int side = HorizonFormat.SamplesPerSide;
        var tile = new ushort[HorizonFormat.SamplesPerTile];
        for (int c = 0; c < side; c++)
        {
            double e = id.MinE + c * HorizonFormat.SpacingM;
            var column = ColumnAt(e - CenterE);
            for (int r = 0; r < side; r++)
            {
                double n = id.MaxN - r * HorizonFormat.SpacingM;
                var (massif, detail) = OnLattice(e, n);
                double h = HeightAt(e - CenterE, n - CenterN, column, massif, detail);
                if (blend != null)
                {
                    bool edge = c == 0 || r == 0 || c == side - 1 || r == side - 1;
                    if (edge && blend.TryRealKnot(e, n, out ushort real))
                    {
                        tile[r * side + c] = real;
                        continue;
                    }
                    h += blend.Correction(e, n);
                }
                tile[r * side + c] = ChunkFormat.Quantize(h);
            }
        }
        return tile;
    }

    public TerrainManifest BuildManifest() => new()
    {
        SuggestedOriginLv95 = new Lv95Point { E = CenterE, N = CenterN },
        Tiles = Tiles.Select(t => new ManifestTile { E = t.E, N = t.N }).ToList(),
    };

    // ---- ground cover ----------------------------------------------------------------------

    /// <summary>
    /// Spacing, in grid cells, of the fields the cover is classified from. Every tile in the rings
    /// asks for cover, most of them far off and drawn one vertex in ten or fifty, so it must not
    /// need the tile's whole 5 m noise lattice: at 10 m every sample is a lattice point, evaluated
    /// on its own, a quarter of the work.
    /// </summary>
    private const int CoverStep = 2 * LatticeM;

    // keyed by the blend's version too: the same tile blends differently once the real set changes
    private readonly Dictionary<(TileId, long), byte[]> _covers = new();
    private readonly Queue<(TileId, long)> _coverOrder = new();

    /// <summary>
    /// The cover raster, kept for a few tiles: the loader asks for cover and then for trees, and
    /// trees are scattered from it.
    /// </summary>
    public byte[] BuildCover(TileId id, Blend? blend = null)
    {
        CheckBlend(id, blend);
        var key = (id, blend?.Version ?? long.MinValue);
        lock (_covers)
            if (_covers.TryGetValue(key, out var cached)) return cached;

        var cells = ClassifyTile(id, blend);

        lock (_covers)
        {
            if (_covers.TryAdd(key, cells))
            {
                _coverOrder.Enqueue(key);
                while (_coverOrder.Count > 16) _covers.Remove(_coverOrder.Dequeue());
            }
        }
        return cells;
    }

    /// <summary>
    /// Classifies a 1001^2 raster from altitude and slope sampled every 10 m.
    ///
    /// <para>
    /// The fields are evaluated ten thousand times rather than a million. A 10 m square whose
    /// four corners agree is filled whole; one on a boundary is classified cell by cell from
    /// bilinear fields, so class edges come out as smooth lines rather than 10 m stairs. The
    /// river is tested against its exact distance everywhere, because its water has to sit on
    /// the flat bed <see cref="HeightAt"/> carved, not near it.
    /// </para>
    /// </summary>
    private byte[] ClassifyTile(TileId id, Blend? blend)
    {
        const int size = CoverFormat.Size;
        const int lat = (size - 1) / CoverStep + 1;   // 101
        const int ext = lat + 2;                        // plus a border, for slopes at the edges

        // heights at the samples, border included; the border is exactly the blend's S lattice
        blend?.PrepareLattice();
        var h = new double[ext * ext];
        var corr = blend == null ? null : new double[ext * ext];
        var axis = new double[ext];
        var floor = new double[ext];
        for (int i = 0; i < ext; i++)
        {
            double e = id.MinE + (i - 1) * CoverStep;
            var column = ColumnAt(e - CenterE);
            axis[i] = column.Axis;
            floor[i] = column.Floor;
            for (int j = 0; j < ext; j++)
            {
                double n = id.MaxN - (j - 1) * CoverStep;
                var (massif, detail) = OnLattice(e, n);
                h[j * ext + i] = HeightAt(e - CenterE, n - CenterN, column, massif, detail);
                if (blend != null)
                {
                    double c = blend.Correction(e, n);
                    h[j * ext + i] += c;
                    corr![j * ext + i] = c;
                }
            }
        }

        // fields per lattice point
        var alt = new float[lat * lat];
        var slope = new float[lat * lat];
        var forest = new float[lat * lat];
        var crop = new float[lat * lat];
        var above = new float[lat * lat];
        var tilt = new float[lat * lat];
        var classes = new byte[lat * lat];
        for (int j = 0; j < lat; j++)
            for (int i = 0; i < lat; i++)
            {
                int k = j * lat + i;
                int e = (j + 1) * ext + i + 1;
                double gx = (h[e + 1] - h[e - 1]) / (2 * CoverStep);
                double gy = (h[e + ext] - h[e - ext]) / (2 * CoverStep);
                double x = id.MinE + i * CoverStep - CenterE;
                double y = id.MaxN - j * CoverStep - CenterN;
                alt[k] = (float)h[e];
                slope[k] = (float)(Math.Atan(Math.Sqrt(gx * gx + gy * gy)) * 180 / Math.PI);
                forest[k] = (float)Noise.Fbm(x / 650, y / 650, 3, 51);
                crop[k] = (float)Noise.Fbm(x / 420, y / 420, 2, 67);
                above[k] = (float)(h[e] - floor[i + 1]);
                // how steeply the blend leans the ground here, to keep water off tilted river beds
                if (corr != null)
                {
                    double cx = (corr[e + 1] - corr[e - 1]) / (2 * CoverStep);
                    double cy = (corr[e + ext] - corr[e - ext]) / (2 * CoverStep);
                    tilt[k] = (float)Math.Sqrt(cx * cx + cy * cy);
                }
                classes[k] = (byte)Classify(alt[k], slope[k], forest[k], crop[k], above[k],
                    y - axis[i + 1], Math.Abs(y - (axis[i + 1] + RiverOffset)), Blend.WaterAllowed(tilt[k]));
            }

        var cells = new byte[size * size];
        for (int j = 0; j < lat - 1; j++)
            for (int i = 0; i < lat - 1; i++)
            {
                int k00 = j * lat + i, k10 = k00 + 1, k01 = k00 + lat, k11 = k01 + 1;
                byte c = classes[k00];
                double yTop = id.MaxN - j * CoverStep - CenterN;
                double riverAt = axis[i + 1] + RiverOffset;
                bool nearRiver = Math.Abs(yTop - riverAt) < RiverBank + 2 * CoverStep + 30;
                bool uniform = !nearRiver && classes[k10] == c && classes[k01] == c && classes[k11] == c;

                int r0 = j * CoverStep, c0 = i * CoverStep;
                // the last square also owns the tile's last row/column
                int rows = j == lat - 2 ? CoverStep + 1 : CoverStep;
                int cols = i == lat - 2 ? CoverStep + 1 : CoverStep;
                for (int dr = 0; dr < rows; dr++)
                {
                    int row = r0 + dr;
                    float v = dr / (float)CoverStep;
                    for (int dc = 0; dc < cols; dc++)
                    {
                        int col = c0 + dc;
                        if (uniform) { cells[row * size + col] = c; continue; }

                        float u = dc / (float)CoverStep;
                        float Lerp(float[] f) =>
                            (f[k00] * (1 - u) + f[k10] * u) * (1 - v) + (f[k01] * (1 - u) + f[k11] * u) * v;

                        double x = id.MinE + col - CenterE;
                        double y = id.MaxN - row - CenterN;
                        double ax = axis[i + 1] + (axis[i + 2] - axis[i + 1]) * u;
                        cells[row * size + col] = (byte)Classify(Lerp(alt), Lerp(slope), Lerp(forest),
                            Lerp(crop), Lerp(above), y - ax, Math.Abs(y - (ax + RiverOffset)),
                            corr == null || Blend.WaterAllowed(Lerp(tilt)));
                    }
                }
            }
        return cells;
    }

    /// <param name="fromRoad">Signed offset from the main road; positive is the north side.</param>
    /// <param name="waterOk">False where a blend has tilted the river bed: no water on a slope.</param>
    private static CoverClass Classify(float alt, float slope, float forest, float crop, float above,
        double fromRoad, double fromRiver, bool waterOk)
    {
        if (fromRiver < RiverHalf - 0.5 && waterOk) return CoverClass.Water;

        if (alt > 2950 && slope < 30) return CoverClass.Glacier;
        if (alt > 2750 && slope < 22 && forest > 0.1f) return CoverClass.Snowfield;
        if (slope > 42 || (slope > 34 && alt > 2150)) return CoverClass.Rock;
        if (alt > 2350 && slope > 24) return CoverClass.Scree;
        if (alt > 2550) return CoverClass.LooseScree;

        // the tree line wanders by a couple of hundred metres, as real ones do
        float treeLine = 1900 + 120 * crop;
        if (above > 15 + 60 * (crop + 0.5f) && slope > 4 && alt < treeLine + 120)
        {
            if (alt > treeLine) return forest > 0.2f ? CoverClass.Shrub : CoverClass.Open;
            if (forest > -0.12f)
                return alt > treeLine - 160 ? CoverClass.OpenForest : CoverClass.Forest;
        }

        // Valais-style vineyards: the lower slope on the north side, facing the sun
        if (fromRoad > 250 && above > 15 && slope is > 7 and < 32 && alt < 1050 && crop > -0.05f)
            return CoverClass.Vineyard;

        // orchards on the floor around the villages
        if (Math.Abs(fromRoad) is > 40 and < 360 && fromRiver > 45 && above < 15 && slope < 8
            && crop > 0.32f)
            return CoverClass.Orchard;

        return CoverClass.Open;
    }

    // ---- trees -----------------------------------------------------------------------------

    private const byte KindConifer = 0, KindShrub = 1, KindFruit = 2, KindSolitary = 3;

    /// <summary>
    /// Scattered forest, orchard rows on a world-anchored grid, and solitary trees across the
    /// open floor — the same three kinds the preprocessor emits. Kept off roads and out of
    /// buildings by a mask stamped from both.
    /// </summary>
    public List<TreeInstance> BuildTrees(TileId id, Blend? blend = null)
    {
        const int size = CoverFormat.Size;
        var cells = BuildCover(id, blend);
        blend?.PrepareLattice();
        var site = new Site(LatticeFor(id), blend);
        var blocked = BuildTreeMask(id, site);
        var trees = new List<TreeInstance>();

        for (int row = 0; row < size; row++)
            for (int col = 0; col < size; col++)
            {
                int cell = row * size + col;
                var cls = (CoverClass)cells[cell];
                if (blocked[cell]) continue;

                // each 1 m cell is 0.0001 ha; densities are per hectare
                // the east column and south row belong to the next tile, which emits them
                if (col == size - 1 || row == size - 1) continue;

                float perCell;
                if (CoverFormat.IsWooded(cls)) perCell = CoverFormat.TreeDensity(cls) * 0.0001f;
                else if (cls == CoverClass.Open) perCell = 12f * 0.0001f;   // hedgerow and field trees
                else continue;

                int ge = id.E * 1000 + col, gn = id.N * 1000 - row;
                if (Noise.Hash01(ge, gn, 71) > perCell) continue;

                double e = id.MinE + col + (Noise.Hash01(ge, gn, 73) - 0.5);
                double n = id.MaxN - row + (Noise.Hash01(ge, gn, 79) - 0.5);
                // field trees stand in clumps and lines along field edges, not evenly sprinkled
                if (cls == CoverClass.Open
                    && Math.Abs(Noise.Fbm((e - CenterE) / 90, (n - CenterN) / 90, 2, 97)) > 0.06)
                    continue;
                double h = Ground(site, e, n);
                if (cls == CoverClass.Open && h > 1700) continue;

                float r = (float)Noise.Hash01(ge, gn, 83);
                (float height, byte kind) = cls switch
                {
                    CoverClass.Forest => (14f + r * 10f, KindConifer),
                    CoverClass.OpenForest => (10f + r * 8f, KindConifer),
                    CoverClass.Woodland => (7f + r * 6f, KindConifer),
                    CoverClass.Shrub => (2.5f + r * 2f, KindShrub),
                    _ => (9f + r * 7f, KindSolitary),
                };
                trees.Add(new TreeInstance((float)(e - id.MinE), (float)h, (float)(id.MaxN - n), height, kind));
            }

        // orchards: 6 m rows anchored to world coordinates, so they run on across tile seams
        const double spacing = 6;
        for (double e = Math.Ceiling(id.MinE / spacing) * spacing; e < id.MinE + ChunkFormat.TileSizeM; e += spacing)
            for (double n = Math.Ceiling(id.MinN / spacing) * spacing; n < id.MaxN; n += spacing)
            {
                int col = (int)Math.Round(e - id.MinE), row = (int)Math.Round(id.MaxN - n);
                if ((uint)col >= size || (uint)row >= size) continue;
                int cell = row * size + col;
                if ((CoverClass)cells[cell] != CoverClass.Orchard || blocked[cell]) continue;
                float r = (float)Noise.Hash01((int)e, (int)n, 89);
                trees.Add(new TreeInstance((float)(e - id.MinE), (float)Ground(site, e, n), (float)(id.MaxN - n),
                    3.4f + r * 1.4f, KindFruit));
            }

        return trees;
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static double SmoothStep(double a, double b, double v)
    {
        double t = Math.Clamp((v - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Deterministic 2D gradient noise, no Godot types: it runs on the tile workers.</summary>
    private static class Noise
    {
        private static uint Hash(int x, int y, uint seed)
        {
            uint h = seed * 0x9E3779B9u ^ (uint)x * 0x85EBCA6Bu ^ (uint)y * 0xC2B2AE35u;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            return h ^ (h >> 16);
        }

        public static double Hash01(int x, int y, uint seed) => (Hash(x, y, seed) & 0xFFFFFF) / 16777216.0;

        // eight gradients, looked up rather than switched on: the hash is random, so a switch
        // mispredicts on most calls and that was most of the cost
        private static readonly double[] GradX = [1, 1, -1, -1, 1.4142, -1.4142, 0, 0];
        private static readonly double[] GradY = [1, -1, 1, -1, 0, 0, 1.4142, -1.4142];

        private static double Grad(int ix, int iy, double fx, double fy, uint seed)
        {
            uint g = Hash(ix, iy, seed) & 7;
            return GradX[g] * fx + GradY[g] * fy;
        }

        /// <summary>Perlin-style gradient noise, roughly in [-1, 1].</summary>
        public static double Perlin(double x, double y, uint seed)
        {
            int ix = (int)x, iy = (int)y;
            if (x < ix) ix--;
            if (y < iy) iy--;
            double fx = x - ix, fy = y - iy;
            double u = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
            double v = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
            double n00 = Grad(ix, iy, fx, fy, seed);
            double n10 = Grad(ix + 1, iy, fx - 1, fy, seed);
            double n01 = Grad(ix, iy + 1, fx, fy - 1, seed);
            double n11 = Grad(ix + 1, iy + 1, fx - 1, fy - 1, seed);
            double a = n00 + (n10 - n00) * u;
            double b = n01 + (n11 - n01) * u;
            return (a + (b - a) * v) * 0.8;
        }

        public static double Fbm(double x, double y, int octaves, uint seed)
        {
            double sum = 0, amp = 1, norm = 0;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Perlin(x, y, seed + (uint)o);
                norm += amp;
                amp *= 0.5;
                // rotate a little each octave so the lattices do not line up
                (x, y) = (x * 1.6 + y * 1.2 + 3.1, -x * 1.2 + y * 1.6 + 7.7);
            }
            return sum / norm;
        }

        /// <summary>Ridged multifractal in [0, 1]: sharp crests, broad hollows.</summary>
        public static double Ridged(double x, double y, int octaves, uint seed)
        {
            double sum = 0, amp = 1, norm = 0, weight = 1;
            for (int o = 0; o < octaves; o++)
            {
                double r = 1 - Math.Abs(Perlin(x, y, seed + (uint)o));
                r *= r * weight;
                weight = Math.Clamp(r * 1.5, 0, 1);
                sum += amp * r;
                norm += amp;
                amp *= 0.5;
                (x, y) = (x * 1.6 + y * 1.2 + 5.3, -x * 1.2 + y * 1.6 + 1.9);
            }
            return sum / norm;
        }
    }
}
