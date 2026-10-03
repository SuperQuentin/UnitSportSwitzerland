using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// A generated stand-in for terrain the game has no data for, shaped on the real country: the
/// macro relief is a 500 m heightmap of Switzerland and its borders (<see cref="Relief"/>), the
/// rivers follow its real drainage, valley floors are flattened along them, and roads, railways
/// and villages run up the valleys (<see cref="Network"/>). Rougher ground gets more generated
/// detail, lakes are flat water, forest grows to a wandering tree line, rock, scree and glacier
/// above it, vineyards on sunny slopes.
///
/// <para>
/// A fresh clone has no <c>terrain_chunks/</c> (the generated data is 5.3 GB and not in the
/// repository), and a partial region — a MapSetup zone, a server streaming part of the country —
/// ends somewhere. Both used to end in void. <see cref="FallbackChunkSource"/> serves these tiles
/// wherever no real tile exists, through the ordinary <see cref="IChunkSource"/> seam in the
/// ordinary formats, so every system that reads terrain, roads, cover, trees or buildings works on
/// them unchanged; beside real tiles the ground bends to meet them (<see cref="Blend"/>).
/// </para>
///
/// <para>
/// Everything is a pure function of LV95 position, so it is thread-safe, needs no state per tile,
/// and a vertex on a tile edge gets the same height from both tiles that share it — the seams are
/// bit-identical after quantisation for the same reason the real ones are. The world does not
/// depend on the anchor (<see cref="CenterE"/>/<see cref="CenterN"/>, the default spawn on every
/// peer), which only bounds the villages offered as towns.
/// </para>
/// </summary>
public sealed partial class ProceduralWorld
{
    /// <summary>
    /// How far generation reaches past the spawn and past real terrain, in tiles
    /// (<see cref="FallbackChunkSource.FillRadiusTiles"/>).
    /// </summary>
    public const int RadiusTiles = 40;

    /// <summary>Horizon reach beyond the fill: the default 60 km horizon from its edge.</summary>
    public const int HorizonRadiusTiles = RadiusTiles + 60;

    public double CenterE { get; }
    public double CenterN { get; }

    // noise is evaluated relative to a fixed point, so the numbers stay small and the world is
    // the same whatever the anchor
    private const double NoiseE = 2600000, NoiseN = 1200000;

    public ProceduralWorld(double centerE, double centerN)
    {
        CenterE = centerE;
        CenterN = centerN;
        // deriving the rivers from the heightmap takes a second or two: start now, on a worker,
        // so the first tile does not wait for all of it
        ThreadPool.QueueUserWorkItem(_ => _ = Network.Instance);
    }

    // ---- the fields ----------------------------------------------------------------------------
    //
    // A height is made of two fields sampled on world-anchored lattices and interpolated:
    //
    //  - the coarse field, every 25 m: the macro height plus the relief-scaled detail, the valleys
    //    pressed into it (as h = P + Q f), the lakes, and what the cover needs from the valleys.
    //    All of it varies over hundreds of metres, and the valleys' distance tests are the
    //    expensive part, so they are done 1 700 times a tile instead of 41 000.
    //  - the fine field, every 5 m: the ground's roughness f (its finest octave is 60 m across)
    //    and the river channel — how far the nearest one is, its level, width and depth.
    //
    // At every 5 m point the two make a height: h = P + Q f, then the lake shore, then the channel
    // carved in. Between them the height is bilinear, so a full grid costs one interpolation a
    // vertex; a channel's flat bottom stays flat, being the same height at every point across it.
    //
    // Both lattices are anchored to LV95 multiples of their spacing, so their values are a function
    // of position alone: two tiles interpolate the same numbers at a shared edge, and a point on a
    // lattice point reads the corner unchanged — every stride-10 sample is on the 5 m lattice and
    // every horizon sample on both, which keeps the coarse tile equal to the decimated full one.

    private const int FineM = 5;
    private const int CoarseM = 25;
    private const int FineBorder = 2;     // cells beyond the tile, for slopes at its edges
    private const int CoarseBorder = 4;   // 100 m: farms and trees just past the edge still hit it
    private const int FineSide = (int)(ChunkFormat.TileSizeM / FineM) + 1 + 2 * FineBorder;
    private const int CoarseSide = (int)(ChunkFormat.TileSizeM / CoarseM) + 1 + 2 * CoarseBorder;

    /// <summary>Distance recorded where no channel is near.</summary>
    private const double NoChannel = 1e4;

    /// <summary>The coarse field at a point.</summary>
    private struct Coarse
    {
        public double P, Q;       // the height before lakes and channels is P + Q f
        public double Lake;       // how far into a lake, 0..1; water above 0.5
        public double Level;      // that lake's level, where Lake > 0
        public double MaxDepth;   // the deepest that lake's bed gets (#298), where Lake > 0
        public double Fetch;      // that lake's size (√area), for the waves
        public double Valley;     // how much of the strongest valley floor is here, 0..1
        public double Floor;      // that valley's floor level, where Valley > 0
    }

    /// <summary>The fine field at a point.</summary>
    private struct Fine
    {
        public double F;          // roughness noise, roughly -1..1
        public double Dr;         // distance to the nearest channel, or NoChannel
        public double Level;      // its bed there (the water level of its flat bottom)
        public double Half;       // half-width of its flat bottom
        public double Bank;       // distance to the top of its bank
        public double Depth;      // how far the bottom sits below the bank
        public double Wet;        // 1 where it carries water
        public double Keep;       // 0 under a road or railway: a culvert, no channel
    }

    private sealed class Lattice
    {
        public long CI0, CJ0;                                   // coarse index of the first sample
        public readonly Coarse[] C = new Coarse[CoarseSide * CoarseSide];
        public long FI0, FJ0;
        public double[]? H;                                     // heights every 5 m, for full grids
    }

    // ---- the coarse field ------------------------------------------------------------------------

    // Small per-thread memos: a point query reads four corners, its neighbour mostly the same four.
    // The values are a pure function of (i, j), so a hit is the same bits as a recomputation.
    private const int MemoSize = 1024;
    [ThreadStatic] private static (long I, long J, Coarse C)[]? _coarseMemo;
    [ThreadStatic] private static (long I, long J, Fine F)[]? _fineMemo;

    private static int MemoSlot(long i, long j) => (int)((uint)(i * 73856093 ^ j * 19349663) % MemoSize);

    private static Coarse CoarseAt(long i, long j)
    {
        var memo = _coarseMemo ??= InitMemo<Coarse>();
        ref var slot = ref memo[MemoSlot(i, j)];
        if (slot.I == i && slot.J == j) return slot.C;
        var c = ComputeCoarse(i, j);
        slot = (i, j, c);
        return c;
    }

    private static Fine FineAt(long i, long j)
    {
        var memo = _fineMemo ??= InitMemo<Fine>();
        ref var slot = ref memo[MemoSlot(i, j)];
        if (slot.I == i && slot.J == j) return slot.F;
        var f = ComputeFine(i, j);
        slot = (i, j, f);
        return f;
    }

    private static (long, long, T)[] InitMemo<T>()
    {
        var memo = new (long, long, T)[MemoSize];
        Array.Fill(memo, (long.MinValue, long.MinValue, default!));
        return memo;
    }

    private static Coarse ComputeCoarse(long i, long j)
    {
        double e = i * (double)CoarseM, n = j * (double)CoarseM;
        var relief = Relief.Instance;
        var net = Network.Instance;
        double x = e - NoiseE, y = n - NoiseN;

        // the real country, and generated detail in proportion to how rough it is there: gentle
        // on the Plateau, ridges and gullies in the Alps
        double macro = relief.Macro(e, n);
        double rough = relief.RoughAt(e, n);
        double ridges = Math.Clamp(0.12 * rough, 2, 110) * (Noise.Ridged(x / 1600, y / 1600, 4, 23) - 0.3);
        double swell = Math.Clamp(0.05 * rough, 2, 30) * Noise.Fbm(x / 700, y / 700, 2, 29);
        double t = macro + ridges + swell;
        double q = Math.Clamp(0.02 * rough, 1, 12);

        // The valleys, each pressing its floor into the ground by its weight V: h <- (1 - V) h + V F,
        // smaller rivers first so a main valley's floor wins where they meet. Composed, that is
        // h = A t + B. V is the strongest of the segments' (1 at the floor, 0 past the reach), F
        // the bed at every segment in reach weighted by inverse distance to the 8th power: next
        // to nearest-point, but continuous where the nearest point jumps on the inside of a bend.
        double a = 1, b = 0, strongest = 0, strongestFloor = 0;
        int river = -1;
        double v = 0, sw = 0, swb = 0;
        void Flush()
        {
            if (v <= 0) return;
            double floor = swb / sw;
            a *= 1 - v;
            b = b * (1 - v) + floor * v;
            if (v >= strongest)
            {
                strongest = v;
                strongestFloor = floor;
            }
        }
        foreach (int s in net.Valleys.At(e, n))
        {
            if (net.RiverOf[s] != river)
            {
                Flush();
                river = net.RiverOf[s];
                v = sw = swb = 0;
            }
            var (d, u) = SegmentDistance(e, n, net.PE[s], net.PN[s], net.PE[s + 1], net.PN[s + 1]);
            double reach = net.Reach[s] + (net.Reach[s + 1] - net.Reach[s]) * u;
            if (d >= reach) continue;
            double floorHalf = net.Floor[s] + (net.Floor[s + 1] - net.Floor[s]) * u;
            v = Math.Max(v, 1 - SmoothStep(floorHalf, reach, d));
            double w = 1 / (d * d + 1);
            w *= w;
            w *= w;
            sw += w;
            swb += w * (net.Bed[s] + (net.Bed[s + 1] - net.Bed[s]) * u);
        }
        Flush();

        var c = new Coarse
        {
            P = a * t + b,
            // the floor keeps a trace of roughness, a few decimetres
            Q = a * q + (1 - a) * 0.8,
            Valley = strongest,
            Floor = strongestFloor,
        };

        // lakes: a ragged shore, but no lake where the heightmap has none
        double lake = LakeField(e, n, out double level, out int lakeId);
        if (lake > 0)
        {
            c.Lake = lake;
            c.Level = level;
            c.MaxDepth = relief.LakeMaxDepth[lakeId];
            c.Fetch = Math.Sqrt(relief.LakeAreaM2[lakeId]);
        }
        return c;
    }

    /// <summary>How far into a lake a point is, 0..1 (water above 0.5): the relief's lake weight with a ragged shore.</summary>
    private static double LakeField(double e, double n, out double level, out int lakeId)
    {
        double lake = Relief.Instance.LakeWeight(e, n, out level, out lakeId);
        if (lake <= 0) return 0;
        double x = e - NoiseE, y = n - NoiseN;
        return Math.Clamp(lake + 0.6 * lake * (1 - lake) * Noise.Fbm(x / 400, y / 400, 3, 41), 0, 1);
    }

    /// <summary>
    /// A tile's still water at its 11x11 horizon samples (#298): the lake level where a sample is in
    /// a lake, 0 elsewhere; null when none is. The horizon heights are the beds, so it draws the
    /// higher of the two. Lakes only (a river is narrower than the 100 m lattice), and from the lake
    /// field alone, a few microseconds a tile, so the generated horizon cache does not need it.
    /// </summary>
    public static ushort[]? HorizonWater(TileId id)
    {
        const int side = HorizonFormat.SamplesPerSide;
        ushort[]? levels = null;
        for (int r = 0; r < side; r++)
            for (int c = 0; c < side; c++)
            {
                double e = id.MinE + c * HorizonFormat.SpacingM, n = id.MaxN - r * HorizonFormat.SpacingM;
                if (LakeField(e, n, out double level, out _) < 0.5) continue;
                (levels ??= new ushort[HorizonFormat.SamplesPerTile])[r * side + c] = ChunkFormat.Quantize(level);
            }
        return levels;
    }

    /// <summary>
    /// Between two coarse samples; exactly the first at t = 0, so interpolating on a lattice point
    /// returns the corner unchanged (the lake level and the valley floor are picked, not mixed).
    /// </summary>
    private static Coarse Lerp(in Coarse a, in Coarse b, double t)
    {
        if (t == 0) return a;
        return new Coarse
        {
            P = a.P + (b.P - a.P) * t,
            Q = a.Q + (b.Q - a.Q) * t,
            Lake = a.Lake + (b.Lake - a.Lake) * t,
            Level = Math.Max(a.Level, b.Level),
            MaxDepth = Math.Max(a.MaxDepth, b.MaxDepth),
            Fetch = Math.Max(a.Fetch, b.Fetch),
            Valley = a.Valley + (b.Valley - a.Valley) * t,
            Floor = a.Valley >= b.Valley ? a.Floor : b.Floor,
        };
    }

    private static Coarse Bilerp(in Coarse a, in Coarse b, in Coarse c, in Coarse d, double u, double v)
    {
        var south = Lerp(a, b, u);
        var north = Lerp(c, d, u);
        return Lerp(south, north, v);
    }

    // ---- the fine field --------------------------------------------------------------------------

    private static Fine ComputeFine(long i, long j)
    {
        double e = i * (double)FineM, n = j * (double)FineM;
        var net = Network.Instance;
        var f = new Fine
        {
            F = Noise.Fbm((e - NoiseE) / 240, (n - NoiseN) / 240, 3, 37),
            Dr = NoChannel,
            Keep = 1,
        };

        // the nearest channel among those whose bank reaches this bucket, however far: a lattice
        // cell with one corner in reach then has real values at all four
        double best = double.MaxValue;
        foreach (int s in net.Channels.At(e, n))
        {
            var (d, u) = SegmentDistance(e, n, net.PE[s], net.PN[s], net.PE[s + 1], net.PN[s + 1]);
            if (d >= best) continue;
            best = d;
            f.Dr = d;
            f.Level = net.Bed[s] + (net.Bed[s + 1] - net.Bed[s]) * u;
            f.Half = net.Half[s] + (net.Half[s + 1] - net.Half[s]) * u;
            f.Bank = net.Bank[s] + (net.Bank[s + 1] - net.Bank[s]) * u;
            f.Depth = net.Depth[s] + (net.Depth[s + 1] - net.Depth[s]) * u;
            f.Wet = net.Water[s] && net.Water[s + 1] ? 1 : 0;
        }
        // where a road or railway crosses, the river goes under it: the ground stays whole
        if (f.Dr < f.Bank + 2 * FineM)
        {
            var (dl, cls) = NearestLine(e, n);
            if (cls is { } k)
            {
                double half = RoadFormat.DefaultWidth(k) / 2;
                f.Keep = SmoothStep(half + 2, half + 8, dl);
            }
        }
        return f;
    }

    // ---- sampling ----------------------------------------------------------------------------------

    private static double Bilerp(double a, double b, double c, double d, double u, double v)
    {
        double south = a + (b - a) * u;
        double north = c + (d - c) * u;
        return south + (north - south) * v;
    }

    private static (long Index, double Frac) Cell(double v, int spacing)
    {
        double cell = Math.Floor(v / spacing);
        return ((long)cell, (v - cell * spacing) / spacing);
    }

    /// <summary>
    /// The coarse field at a point: from a tile's lattice when it covers the point, else from the
    /// four corners evaluated on the spot. Both do the same arithmetic on the same corner values,
    /// so they agree to the bit.
    /// </summary>
    private static Coarse SampleCoarse(Lattice? lattice, double e, double n)
    {
        var (i, u) = Cell(e, CoarseM);
        var (j, v) = Cell(n, CoarseM);
        if (lattice != null)
        {
            long li = i - lattice.CI0, lj = j - lattice.CJ0;
            if (li >= 0 && lj >= 0 && li + 1 < CoarseSide && lj + 1 < CoarseSide)
            {
                int k = (int)(lj * CoarseSide + li);
                var c = lattice.C;
                return Bilerp(c[k], c[k + 1], c[k + CoarseSide], c[k + CoarseSide + 1], u, v);
            }
        }
        return Bilerp(CoarseAt(i, j), CoarseAt(i + 1, j), CoarseAt(i, j + 1), CoarseAt(i + 1, j + 1), u, v);
    }

    /// <summary>The fine field at the 5 m lattice point nearest a point: for siting, not for heights.</summary>
    private static Fine FineNear(double e, double n) =>
        FineAt((long)Math.Round(e / FineM), (long)Math.Round(n / FineM));

    /// <summary>The height from the two fields: the valleys' ground, the lake shore, the channel.</summary>
    private static double Combine(in Coarse c, in Fine f)
    {
        double h = c.P + c.Q * f.F;

        if (c.Lake > 0)
        {
            // low ground near a lake is lifted clear of it, then the shore comes down to the level
            h += Math.Max(0, c.Level + 0.5 - h) * SmoothStep(0.05, 0.3, c.Lake);
            if (c.Lake >= 0.5) return c.Level - LakeDepth(c);
            h = c.Level + (h - c.Level) * SmoothStep(0.5, 0.3, c.Lake);
        }

        if (f.Dr < f.Bank)
        {
            // a flat bottom below the floor, so the water drawn on it is level from bank to bank
            // (only ever down: a channel whose bed is above the ground here is not dug at all)
            double carve = (1 - SmoothStep(f.Half, f.Bank, f.Dr)) * f.Keep * (1 - SmoothStep(0.3, 0.5, c.Lake));
            double bed = f.Level - f.Depth;
            if (h > bed)
            {
                h -= (h - bed) * carve;
                // #298: the flat bottom is the water's level; the river's own channel lies below it
                if (f.Dr < f.Half && f.Wet > 0) h -= RiverDepth(f) * carve;
            }
        }
        return h;
    }

    /// <summary>
    /// How far into a lake the weight is, in metres from the shore: the weight runs from 0.5 on the
    /// shore to 1 over half a 500 m relief cell.
    /// </summary>
    private const double LakeShoreScaleM = 500;

    /// <summary>The lake's depth below its level (#298): <see cref="WaterBed"/>'s shelf and drop-off, from the weight.</summary>
    private static double LakeDepth(in Coarse c) =>
        WaterBed.Depth((c.Lake - 0.5) * LakeShoreScaleM, double.PositiveInfinity, c.MaxDepth);

    /// <summary>A river's depth below its flat bottom (#298): <see cref="WaterBed"/>'s channel across the flat bottom's width.</summary>
    private static double RiverDepth(in Fine f) =>
        WaterBed.Depth(f.Half - f.Dr, f.Half, WaterBed.MaxMaxDepthM);

    /// <summary>Terrain height in metres at an LV95 position.</summary>
    public double Height(double e, double n) => Height(null, e, n);

    /// <summary>The height at a 5 m lattice point, its coarse field from a tile's lattice or on the spot.</summary>
    private static double FineHeight(Lattice? lattice, long i, long j) =>
        Combine(SampleCoarse(lattice, i * (double)FineM, j * (double)FineM), FineAt(i, j));

    /// <summary>
    /// The height at a point: bilinear between the 5 m lattice heights, from a tile's lattice when
    /// it has them, else computed on the spot — the same arithmetic on the same corners.
    /// </summary>
    private static double Height(Lattice? lattice, double e, double n)
    {
        var (i, u) = Cell(e, FineM);
        var (j, v) = Cell(n, FineM);
        if (lattice?.H is { } h)
        {
            long li = i - lattice.FI0, lj = j - lattice.FJ0;
            if (li >= 0 && lj >= 0 && li + 1 < FineSide && lj + 1 < FineSide)
            {
                int k = (int)(lj * FineSide + li);
                return Bilerp(h[k], h[k + 1], h[k + FineSide], h[k + FineSide + 1], u, v);
            }
        }
        return Bilerp(FineHeight(lattice, i, j), FineHeight(lattice, i + 1, j),
            FineHeight(lattice, i, j + 1), FineHeight(lattice, i + 1, j + 1), u, v);
    }

    /// <summary>The height exactly at a point on both lattices (every 25 m), as interpolating there returns it.</summary>
    private static double HeightOnLattice(double e, double n) =>
        Combine(CoarseAt((long)Math.Floor(e / CoarseM), (long)Math.Floor(n / CoarseM)),
            FineAt((long)Math.Floor(e / FineM), (long)Math.Floor(n / FineM)));

    // ---- the lattice cache -------------------------------------------------------------------

    private readonly Dictionary<TileId, Lattice> _lattices = new();
    private readonly Queue<TileId> _latticeOrder = new();

    /// <summary>
    /// A tile's lattices, kept for a few tiles: its grid, cover, trees, roads and buildings all
    /// read them. The coarse one is cheap and always there; the fine one only when asked for.
    /// </summary>
    private Lattice LatticeFor(TileId id, bool fine)
    {
        Lattice? lattice;
        lock (_lattices)
            _lattices.TryGetValue(id, out lattice);

        if (lattice == null)
        {
            lattice = new Lattice
            {
                CI0 = (long)(id.MinE / CoarseM) - CoarseBorder,
                CJ0 = (long)(id.MinN / CoarseM) - CoarseBorder,
                FI0 = (long)(id.MinE / FineM) - FineBorder,
                FJ0 = (long)(id.MinN / FineM) - FineBorder,
            };
            for (int lj = 0; lj < CoarseSide; lj++)
                for (int li = 0; li < CoarseSide; li++)
                    lattice.C[lj * CoarseSide + li] = CoarseAt(lattice.CI0 + li, lattice.CJ0 + lj);
            lock (_lattices)
            {
                if (_lattices.TryGetValue(id, out var raced)) lattice = raced;
                else
                {
                    _lattices[id] = lattice;
                    _latticeOrder.Enqueue(id);
                    while (_latticeOrder.Count > 16) _lattices.Remove(_latticeOrder.Dequeue());
                }
            }
        }

        if (fine && lattice.H == null)
        {
            var h = new double[FineSide * FineSide];
            for (int lj = 0; lj < FineSide; lj++)
                for (int li = 0; li < FineSide; li++)
                    h[lj * FineSide + li] = FineHeight(lattice, lattice.FI0 + li, lattice.FJ0 + lj);
            lattice.H = h;   // a reference write: a racing reader sees null or all of it
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
        // coarse samples sit on fine lattice points: no reason to fill the whole fine lattice
        bool onFine = step % FineM == 0;
        var lattice = LatticeFor(id, fine: !onFine);
        if (blend != null && !onFine) blend.PrepareLattice();

        // at stride 1 the blend is applied a row at a time: the same numbers as Correction per point
        var row = new double[size];
        bool byRow = blend != null && stride == 1;

        // row-major, so the writes and the lattice reads both walk memory in order
        for (int r = 0; r < size; r++)
        {
            double n = id.MaxN - r * step;
            for (int c = 0; c < size; c++)
            {
                double e = id.MinE + c * step;
                double h = onFine
                    ? FineHeight(lattice, (long)Math.Floor(e / FineM), (long)Math.Floor(n / FineM))
                    : Height(lattice, e, n);
                if (blend != null && !byRow) h += blend.Correction(e, n, h);
                row[c] = h;
            }
            if (byRow) blend!.BlendRow(n, row);
            for (int c = 0; c < size; c++) heights[r * size + c] = ChunkFormat.Quantize(row[c]);
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
            for (int r = 0; r < side; r++)
            {
                double n = id.MaxN - r * HorizonFormat.SpacingM;
                double h = HeightOnLattice(e, n);
                if (blend != null)
                {
                    bool edge = c == 0 || r == 0 || c == side - 1 || r == side - 1;
                    if (edge && blend.TryRealKnot(e, n, out ushort real))
                    {
                        tile[r * side + c] = real;
                        continue;
                    }
                    h += blend.Correction(e, n, h);
                }
                tile[r * side + c] = ChunkFormat.Quantize(h);
            }
        }
        return tile;
    }

    // ---- ground cover ----------------------------------------------------------------------

    /// <summary>
    /// Spacing, in grid cells, of the fields the cover is classified from. Every tile in the rings
    /// asks for cover, most of them far off and drawn one vertex in ten or fifty, so it must not
    /// need the tile's whole fine lattice: at 10 m every sample is a fine lattice point, evaluated
    /// on its own, a quarter of the work.
    /// </summary>
    private const int CoverStep = 2 * FineM;

    // keyed by the blend's version too: the same tile blends differently once the real set changes
    // (the full and the coarse blend classify alike: the raster reads 10 m points only)
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
    /// The tile's still water (#298), on the runtime's 2 m lattice: wherever the cover says Water,
    /// a lake's level, else the river's (its flat bottom, the channel lies below it), else the
    /// ground (a scrap of water the fields do not explain). Fetch: the lake's size, the river's
    /// width. A sample whose ground stands above that level is dry: the cover is classified from
    /// 10 m fields and the shore from 5 m ones, so they disagree by a metre or two on a shore.
    /// Null when the tile is dry.
    /// </summary>
    public WaterTile? BuildWater(TileId id, Blend? blend = null)
    {
        var cells = BuildCover(id, blend);
        int n = WaterTile.Size;
        float[]? level = null, fetch = null;
        int wet = 0;
        blend?.PrepareLattice();
        var lattice = LatticeFor(id, fine: true);
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                int col = c * WaterTile.Stride, row = r * WaterTile.Stride;
                if ((CoverClass)cells[row * CoverFormat.Size + col] != CoverClass.Water) continue;
                if (level == null)
                {
                    level = new float[n * n];
                    fetch = new float[n * n];
                    Array.Fill(level, float.NaN);
                }
                double e = id.MinE + col, nn = id.MaxN - row;
                var coarse = SampleCoarse(lattice, e, nn);
                var fine = FineNear(e, nn);
                double l, f;
                if (coarse.Lake > 0.3) (l, f) = (coarse.Level, coarse.Fetch);
                else if (fine.Dr < fine.Bank) (l, f) = (fine.Level - fine.Depth, 2 * fine.Half);
                else (l, f) = (Height(lattice, e, nn), 10);
                double ground = Height(lattice, e, nn);
                if (blend != null) ground += blend.Correction(e, nn, ground);
                if (ground > l) continue;
                wet++;
                level[r * n + c] = (float)l;
                fetch![r * n + c] = (float)f;
            }
        return wet == 0 ? null : new WaterTile { Level = level!, FetchM = fetch };
    }

    /// <summary>Everything a cover class is decided from, at one point.</summary>
    private struct CoverInputs
    {
        public float Alt, Slope, Forest, Crop, Above, South, Vines, Road, River, Half, Wet, Lake, Bed;
        public bool WaterOk;
    }

    /// <summary>
    /// Classifies a 1001^2 raster from fields sampled every 10 m.
    ///
    /// <para>
    /// The fields are evaluated ten thousand times rather than a million. A 10 m square whose
    /// four corners agree is filled whole; one on a boundary is classified cell by cell from
    /// bilinear fields, so class edges come out as smooth lines rather than 10 m stairs. Squares
    /// near a channel are always done cell by cell, because its water has to sit on the flat bed
    /// <see cref="Combine"/> carved, not near it.
    /// </para>
    /// </summary>
    private byte[] ClassifyTile(TileId id, Blend? blend)
    {
        const int size = CoverFormat.Size;
        const int lat = (size - 1) / CoverStep + 1;   // 101
        const int ext = lat + 2;                        // plus a border, for slopes at the edges

        // heights at the samples, border included; the border is exactly the blend's lattice
        blend?.PrepareLattice();
        var lattice = LatticeFor(id, fine: false);
        var h = new double[ext * ext];
        var corr = blend == null ? null : new double[ext * ext];
        var coarse = new Coarse[ext * ext];
        var fine = new Fine[ext * ext];
        for (int j = 0; j < ext; j++)
        {
            double n = id.MaxN - (j - 1) * CoverStep;
            for (int i = 0; i < ext; i++)
            {
                double e = id.MinE + (i - 1) * CoverStep;
                int k = j * ext + i;
                coarse[k] = SampleCoarse(lattice, e, n);
                fine[k] = FineAt((long)Math.Floor(e / FineM), (long)Math.Floor(n / FineM));
                h[k] = Combine(coarse[k], fine[k]);
                if (blend != null)
                {
                    double c = blend.Correction(e, n, h[k]);
                    h[k] += c;
                    corr![k] = c;
                }
            }
        }

        // fields per sample
        var inputs = new CoverInputs[lat * lat];
        var classes = new byte[lat * lat];
        for (int j = 0; j < lat; j++)
            for (int i = 0; i < lat; i++)
            {
                int k = j * lat + i;
                int x = (j + 1) * ext + i + 1;
                double gx = (h[x + 1] - h[x - 1]) / (2 * CoverStep);
                double gy = (h[x - ext] - h[x + ext]) / (2 * CoverStep);   // north is up the rows
                double tilt = 0;
                // how steeply the blend leans the ground here, to keep water off tilted beds
                if (corr != null)
                {
                    double cx = (corr[x + 1] - corr[x - 1]) / (2 * CoverStep);
                    double cy = (corr[x + ext] - corr[x - ext]) / (2 * CoverStep);
                    tilt = Math.Sqrt(cx * cx + cy * cy);
                }
                inputs[k] = Inputs(id.MinE + i * CoverStep, id.MaxN - j * CoverStep, h[x], gx, gy,
                    coarse[x], fine[x], Blend.WaterAllowed(tilt));
                classes[k] = (byte)Classify(inputs[k]);
            }

        var cells = new byte[size * size];
        for (int j = 0; j < lat - 1; j++)
            for (int i = 0; i < lat - 1; i++)
            {
                int k00 = j * lat + i, k10 = k00 + 1, k01 = k00 + lat, k11 = k01 + 1;
                byte c = classes[k00];
                bool nearWater = Math.Min(Math.Min(inputs[k00].River, inputs[k10].River),
                    Math.Min(inputs[k01].River, inputs[k11].River)) < 60;
                bool uniform = !nearWater && classes[k10] == c && classes[k01] == c && classes[k11] == c;

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
                        var at = Mix(inputs[k00], inputs[k10], inputs[k01], inputs[k11], u, v);
                        cells[row * size + col] = (byte)Classify(at);
                    }
                }
            }
        return cells;
    }

    /// <summary>Cover inputs inside a 10 m square, bilinear from its corners.</summary>
    private static CoverInputs Mix(in CoverInputs a, in CoverInputs b, in CoverInputs c, in CoverInputs d,
        float u, float v)
    {
        float wa = (1 - u) * (1 - v), wb = u * (1 - v), wc = (1 - u) * v, wd = u * v;
        return new CoverInputs
        {
            Alt = a.Alt * wa + b.Alt * wb + c.Alt * wc + d.Alt * wd,
            Slope = a.Slope * wa + b.Slope * wb + c.Slope * wc + d.Slope * wd,
            Forest = a.Forest * wa + b.Forest * wb + c.Forest * wc + d.Forest * wd,
            Crop = a.Crop * wa + b.Crop * wb + c.Crop * wc + d.Crop * wd,
            Above = a.Above * wa + b.Above * wb + c.Above * wc + d.Above * wd,
            South = a.South * wa + b.South * wb + c.South * wc + d.South * wd,
            Vines = a.Vines * wa + b.Vines * wb + c.Vines * wc + d.Vines * wd,
            Road = a.Road * wa + b.Road * wb + c.Road * wc + d.Road * wd,
            River = a.River * wa + b.River * wb + c.River * wc + d.River * wd,
            Half = a.Half * wa + b.Half * wb + c.Half * wc + d.Half * wd,
            Wet = a.Wet * wa + b.Wet * wb + c.Wet * wc + d.Wet * wd,
            Lake = a.Lake * wa + b.Lake * wb + c.Lake * wc + d.Lake * wd,
            Bed = a.Bed * wa + b.Bed * wb + c.Bed * wc + d.Bed * wd,
            WaterOk = (a.WaterOk ? wa : 0) + (b.WaterOk ? wb : 0) + (c.WaterOk ? wc : 0) + (d.WaterOk ? wd : 0) > 0.5f,
        };
    }

    /// <summary>The cover inputs at a point, from its height, gradient and fields.</summary>
    private static CoverInputs Inputs(double e, double n, double h, double gx, double gy,
        in Coarse c, in Fine f, bool waterOk)
    {
        double x = e - NoiseE, y = n - NoiseN;
        double grade = Math.Sqrt(gx * gx + gy * gy);
        var inputs = new CoverInputs
        {
            Alt = (float)h,
            Slope = (float)(Math.Atan(grade) * 180 / Math.PI),
            Forest = (float)Noise.Fbm(x / 650, y / 650, 3, 51),
            Crop = (float)Noise.Fbm(x / 420, y / 420, 2, 67),
            // off a valley floor, or no valley at all: well above it
            Above = (float)(c.Valley > 0.02 ? h - c.Floor : 1000),
            // 1 on a slope facing due south, -1 facing north
            South = (float)(grade > 1e-6 ? gy / grade : 0),
            Vines = (float)Noise.Fbm(x / 9000, y / 9000, 2, 227),
            River = (float)f.Dr,
            Half = (float)f.Half,
            Wet = (float)(f.Wet * f.Keep),
            Lake = (float)c.Lake,
            Bed = (float)(f.Level - f.Depth),
            WaterOk = waterOk,
        };
        // orchards want to know how far the road is; only worth asking on a candidate
        inputs.Road = inputs.Above < 15 && inputs.Slope < 8 && inputs.Crop > 0.32f
            ? (float)NearestLine(e, n).Distance : (float)Network.LineReach;
        return inputs;
    }

    private static CoverClass Classify(in CoverInputs p)
    {
        // a river only where the ground really is its flat bottom: not where a road crosses it, nor
        // where its bed would be above the ground and it was never dug
        if (p.WaterOk && (p.Lake > 0.5f || (p.River < p.Half - 0.5f && p.Wet > 0.5f && p.Alt < p.Bed + 0.5f)))
            return CoverClass.Water;

        if (p.Alt > 2950 && p.Slope < 30) return CoverClass.Glacier;
        if (p.Alt > 2750 && p.Slope < 22 && p.Forest > 0.1f) return CoverClass.Snowfield;
        if (p.Slope > 42 || (p.Slope > 34 && p.Alt > 2150)) return CoverClass.Rock;
        if (p.Alt > 2350 && p.Slope > 24) return CoverClass.Scree;
        if (p.Alt > 2550) return CoverClass.LooseScree;

        // vineyards: low slopes facing the sun, in the regions that grow them
        if (p.South > 0.45f && p.Vines > 0.12f && p.Above > 15 && p.Slope is > 7 and < 32
            && p.Alt < 1000 && p.Crop > -0.05f)
            return CoverClass.Vineyard;

        // the tree line wanders by a couple of hundred metres, as real ones do
        float treeLine = 1900 + 120 * p.Crop;
        if (p.Above > 15 + 60 * (p.Crop + 0.5f) && p.Slope > 4 && p.Alt < treeLine + 120)
        {
            if (p.Alt > treeLine) return p.Forest > 0.2f ? CoverClass.Shrub : CoverClass.Open;
            if (p.Forest > -0.12f)
                return p.Alt > treeLine - 160 ? CoverClass.OpenForest : CoverClass.Forest;
        }

        // orchards on the valley floor, near the road
        if (p.Road is > 20 and < 360 && p.River > 45 && p.Above < 15 && p.Slope < 8 && p.Crop > 0.32f)
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
        var site = new Site(LatticeFor(id, fine: true), blend);
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
                    && Math.Abs(Noise.Fbm((e - NoiseE) / 90, (n - NoiseN) / 90, 2, 97)) > 0.06)
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
