using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

public sealed partial class ProceduralWorld
{
    // ---- blending into real terrain ------------------------------------------------------------
    //
    // A generated tile next to real ones bends its ground to meet them:
    //
    //     h(p) = (1 - W(p)) G(p) + W(p) R(p) + D(p)
    //
    // G is the generator's own height. R is the real ground's low-pass carried outward: over every
    // real tile within Band, its knots (Rs) at the tile's nearest point, weighted by inverse
    // distance. W = 1 - smoothstep(0, Band, distance to the nearest real tile) hands the ground from
    // one to the other. D, within DetailBand only, continues the real surface to first order — its
    // residual (R - Rs) plus its slope across the edge — so the seam is C1, neither ridged nor
    // creased, and fades out 40 m in. At a real edge W = 1, and h = Rs + (R - Rs) = R.
    //
    // Carried out d metres, a real tile's knots are read from a pyramid (RealTile.Levels) at a
    // spacing of about d/2 — 100 m knots at the seam, their area averages on 200 m, 500 m and 1 km
    // lattices further out, the tile's mean at 3 km. The nearest point is constant along every line
    // across the edge, so whatever detail R holds is extruded straight out: at a single spacing,
    // real gullies and ridges between 100 m and 1 km became 3 km streaks. Read coarser the further
    // out, a feature carried d metres is never narrower than about d/2, which reads as shape.
    //
    // A convex mix, not an additive correction. The first version added (Rs - Gs) to G — keeping
    // the generator's relief and moving it to meet the real low-pass — and it dug trenches: where
    // a steep generated flank met a real valley floor, the flank's fall away from the seam was kept
    // at full size, and measured on real tiles at Riddes it put the ground 150 m BELOW the Rhone
    // 200 m from the border. A mix of two heights always lies between them.
    //
    // Inverse distance, not "extrude the nearest edge": the nearest edge jumps on the medial axis
    // (a one-tile hole in a real region, a notch), which would be a cliff; IDW is continuous
    // everywhere. Going through the 100 m knots stops real fine detail being smeared 3 km outward
    // as streaks.
    //
    // What keeps it seamless without any coordination between tiles:
    //  - W and W R at a point are pure functions of the point and the real tiles within Band of
    //    it, summed in (N, E) order. Two generated tiles sharing an edge see the same real tiles
    //    there, so they compute the same bits. Both are sampled on a world-anchored 10 m lattice
    //    and interpolated, so a stride-10 grid and the 100 m horizon land on lattice points and
    //    read the stored values unchanged: coarse = decimated full, as with the noise lattice. G
    //    is exact per point on every path, and they all combine the three the same way (Mix).
    //  - D is exact per point, and at a 100 m point the nearest point on every real tile is one of
    //    its knots, where R - Rs = 0 exactly. So the horizon needs no real grid.
    //  - A generated vertex on a real tile's edge copies that tile's quantised height, which makes
    //    the seam bit-identical where the mix only gets within millimetres.

    /// <summary>Width of the band across which generated ground bends to meet real ground.</summary>
    public const double Band = 3000;

    /// <summary>
    /// Distance over which the real ground's fine detail carries into generated ground. Short, so an
    /// extruded ripple has no length to show as a comb. It MUST stay under the horizon's 100 m:
    /// real edges lie on the 100 m lattice, so a 100 m point is then either on a real edge (where D is
    /// the residual at a knot, exactly 0) or out of reach — which is why the horizon needs no real grid.
    /// </summary>
    public const double DetailBand = 40;

    /// <summary>
    /// Metres between the two real heights the continued slope is taken from where a single metre
    /// is not available or not shared: a coarse vertex step.
    /// </summary>
    private const double SlopeStep = ChunkFormat.CoarseStride * ChunkFormat.SpacingM;

    /// <summary>Width of the soft minimum W takes of the distances to real tiles, in metres.</summary>
    private const double SoftMinM = 150;

    /// <summary>Real tiles a generated tile's blend needs: within Band of any point it samples.</summary>
    private const double NearReach = Band + 100;

    /// <summary>Tiles either side that can be within <see cref="NearReach"/>.</summary>
    private const int NearWindow = 4;

    /// <summary>
    /// Added to d² in the inverse-distance weights, in m². Small, so the weights interpolate at a
    /// real edge: with 1 m², a second real tile 5 m away still took 4% of the weight at the seam.
    /// </summary>
    private const double Soften = 0.01;

    /// <summary>Spacing of the W / W R lattice; a divisor of the coarse stride and of the horizon's 100 m.</summary>
    private const int SStep = 10;
    private const int SBorder = 1;
    private const int SSide = (int)(ChunkFormat.TileSizeM / SStep) + 1 + 2 * SBorder;

    /// <summary>
    /// Steepest a blend may tilt a river bed and keep it water (a grade). The test is on the
    /// correction's gradient, not its size: it is non-zero across the whole band, so "moved by more
    /// than half a metre" took every river within 3 km of real ground, flat beds included. At 1.5%
    /// a 22 m bed leans 0.3 m bank to bank, and a river's own grade stays plausible.
    /// </summary>
    private const double MaxWaterTilt = 0.015;

    /// <summary>A real tile as a blend sees it: its 100 m knots, and a grid for the eight neighbours.</summary>
    public sealed class RealTile
    {
        public TileId Id { get; }

        /// <summary>Full or coarse; null when only the knots are known (the far ones).</summary>
        public ChunkGrid? Grid { get; }

        internal readonly ushort[] KnotsQ;
        internal readonly double[] Knots;

        /// <summary>
        /// The knots and their area averages, one lattice per <see cref="LevelSpacing"/>: level 0 is
        /// the knots themselves, the last the tile's mean. Area-averaged with tent weights, never
        /// decimated — decimating aliases, which is the streak again at a coarser pitch.
        /// </summary>
        internal readonly double[][] Levels;

        public RealTile(TileId id, ushort[] knots, ChunkGrid? grid)
        {
            if (knots.Length != HorizonFormat.SamplesPerTile)
                throw new ArgumentException($"{id}: expected {HorizonFormat.SamplesPerTile} knots, got {knots.Length}");
            if (grid != null && grid.Id != id)
                throw new ArgumentException($"Grid {grid.Id} handed in for real tile {id}");
            Id = id;
            Grid = grid;
            KnotsQ = knots;
            Knots = new double[knots.Length];
            for (int i = 0; i < knots.Length; i++) Knots[i] = ChunkFormat.Dequantize(knots[i]);
            Levels = new double[LevelSpacing.Length][];
            Levels[0] = Knots;
            for (int l = 1; l < LevelSpacing.Length; l++) Levels[l] = Average(Knots, LevelSide(l), LevelSpacing[l]);
        }

        private static double[] Average(double[] knots, int side, double spacing)
        {
            const int ks = HorizonFormat.SamplesPerSide;
            var level = new double[side * side];
            for (int r = 0; r < side; r++)
                for (int c = 0; c < side; c++)
                {
                    // a single node (the mean) weighs the knots as the area each stands for
                    double cx = side == 1 ? -1 : c * spacing, cy = side == 1 ? -1 : r * spacing;
                    double sum = 0, wsum = 0;
                    for (int kr = 0; kr < ks; kr++)
                        for (int kc = 0; kc < ks; kc++)
                        {
                            double wx = Tent(kc, cx, spacing, ks), wy = Tent(kr, cy, spacing, ks);
                            double w = wx * wy;
                            if (w <= 0) continue;
                            sum += w * knots[kr * ks + kc];
                            wsum += w;
                        }
                    level[r * side + c] = sum / wsum;
                }
            return level;
        }

        /// <summary>Weight of knot <paramref name="k"/> for a node at <paramref name="centre"/> m (-1: the trapezoid mean).</summary>
        private static double Tent(int k, double centre, double spacing, int knots)
        {
            if (centre < 0) return k == 0 || k == knots - 1 ? 0.5 : 1;
            return Math.Max(0, 1 - Math.Abs(k * HorizonFormat.SpacingM - centre) / spacing);
        }
    }

    /// <summary>Spacing of each level of <see cref="RealTile.Levels"/>, in metres; the last is the mean.</summary>
    private static readonly double[] LevelSpacing = [100, 200, 500, 1000, 1500];

    private static int LevelSide(int level) =>
        level == LevelSpacing.Length - 1 ? 1 : (int)(ChunkFormat.TileSizeM / LevelSpacing[level]) + 1;

    /// <summary>For BlendCheck's streak measure only: read every distance at level 0, as the first version did.</summary>
    internal static bool SingleLevelForChecks;

    /// <summary>
    /// A real tile's low-pass at its point q, as carried <paramref name="d"/> metres: the level whose
    /// spacing is about d/2, blended with the next by a smoothstep in log-distance so no level switch
    /// is a step. Exactly the knots within 200 m, so the seam is untouched.
    /// </summary>
    private static double Low(RealTile r, double qe, double qn, double d)
    {
        double sigma = Math.Max(LevelSpacing[0], d / 2);
        if (sigma <= LevelSpacing[0] || SingleLevelForChecks) return KnotAt(r.Knots, r.Id, qe, qn);
        int last = LevelSpacing.Length - 1;
        if (sigma >= LevelSpacing[last]) return r.Levels[last][0];
        int l = 0;
        while (sigma >= LevelSpacing[l + 1]) l++;
        double f = SmoothStep(0, 1, Math.Log(sigma / LevelSpacing[l]) / Math.Log(LevelSpacing[l + 1] / LevelSpacing[l]));
        double a = LevelAt(r, l, qe, qn), b = LevelAt(r, l + 1, qe, qn);
        return a + (b - a) * f;
    }

    private static double LevelAt(RealTile r, int level, double e, double n)
    {
        int side = LevelSide(level);
        return side == 1 ? r.Levels[level][0] : LatticeAt(r.Levels[level], side, LevelSpacing[level], r.Id, e, n);
    }

    /// <summary>Tiles whose real data a blend for <paramref name="tile"/> would use.</summary>
    public static IEnumerable<TileId> BlendWindow(TileId tile)
    {
        for (int dn = -NearWindow; dn <= NearWindow; dn++)
            for (int de = -NearWindow; de <= NearWindow; de++)
            {
                var k = new TileId(tile.E + de, tile.N + dn);
                if (k != tile && SquareGap(tile, k) <= NearReach) yield return k;
            }
    }

    /// <summary>Whether a blend wants <paramref name="k"/>'s grid, and at which resolution.</summary>
    /// <returns>0 for knots only, 1 for the full grid, <see cref="ChunkFormat.CoarseStride"/> for the coarse one.</returns>
    public static int BlendGridStride(TileId tile, TileId k, bool full)
    {
        int de = Math.Abs(k.E - tile.E), dn = Math.Abs(k.N - tile.N);
        if (de > 1 || dn > 1 || (de == 0 && dn == 0)) return 0;
        // a diagonal neighbour only ever contributes its corner, which the coarse grid holds
        return full && de + dn == 1 ? 1 : ChunkFormat.CoarseStride;
    }

    private static double SquareGap(TileId a, TileId b)
    {
        double ge = Math.Max(0, Math.Abs(a.E - b.E) - 1) * ChunkFormat.TileSizeM;
        double gn = Math.Max(0, Math.Abs(a.N - b.N) - 1) * ChunkFormat.TileSizeM;
        return Math.Sqrt(ge * ge + gn * gn);
    }

    /// <summary>
    /// The blend for a generated tile, or null when no real tile is near enough to matter —
    /// in which case the tile is exactly what it would be with no real terrain anywhere.
    /// </summary>
    /// <param name="version">Changes whenever the real set does; keys the cover cache.</param>
    public Blend? CreateBlend(TileId tile, IEnumerable<RealTile> reals, long version)
    {
        var near = reals
            .Where(r => r.Id != tile && SquareGap(tile, r.Id) <= NearReach)
            .OrderBy(r => r.Id.N).ThenBy(r => r.Id.E)
            .Select(r => new Blend.Entry(r))
            .ToArray();
        return near.Length == 0 ? null : new Blend(this, tile, version, near);
    }

    /// <summary>Through a tile's 11x11 knots (<see cref="LatticeAt"/>). Exactly the knot on a knot, edges included.</summary>
    private static double KnotAt(double[] knots, TileId k, double e, double n) =>
        LatticeAt(knots, HorizonFormat.SamplesPerSide, HorizonFormat.SpacingM, k, e, n);

    /// <summary>
    /// Catmull-Rom through a lattice anchored at a tile's north-west corner, ends clamped. Not
    /// bilinear: a bilinear lattice has a slope break at every node, and carried straight out
    /// across the band every break became a crease — 100 m strips in shaded relief. Catmull-Rom is
    /// C1 and still returns the node itself on a node, so a 100 m point reads the knot exactly.
    /// </summary>
    private static double LatticeAt(double[] values, int side, double spacing, TileId k, double e, double n)
    {
        int last = side - 1;
        double u = (e - k.MinE) / spacing;
        double v = (k.MaxN - n) / spacing;
        int c = Math.Min((int)u, last), r = Math.Min((int)v, last);
        double fu = u - c, fv = v - r;
        double Row(int rr)
        {
            rr = Math.Clamp(rr, 0, last);
            double V(int cc) => values[rr * side + Math.Clamp(cc, 0, last)];
            return CatmullRom(V(c - 1), V(c), V(c + 1), V(c + 2), fu);
        }
        return CatmullRom(Row(r - 1), Row(r), Row(r + 1), Row(r + 2), fv);
    }

    /// <summary>The Catmull-Rom segment from b to c; exactly b at t = 0.</summary>
    private static double CatmullRom(double a, double b, double c, double d, double t)
    {
        if (t == 0) return b;
        return b + 0.5 * t * (c - a + t * (2 * a - 5 * b + 4 * c - d + t * (3 * (b - c) + d - a)));
    }

    /// <summary>Nearest point of a tile's square, and the distance to it (0 inside).</summary>
    private static double Nearest(TileId k, double e, double n, out double qe, out double qn)
    {
        qe = Math.Clamp(e, k.MinE, k.MinE + ChunkFormat.TileSizeM);
        qn = Math.Clamp(n, k.MinN, k.MaxN);
        double de = e - qe, dn = n - qn;
        return Math.Sqrt(de * de + dn * dn);
    }

    /// <summary>
    /// The real mesh height at a point: the stored vertex when the point is one, so a 10 m point
    /// reads the same bits from the full grid and the coarse one; the mesh's own triangles otherwise.
    /// </summary>
    private static double RealAt(ChunkGrid grid, double e, double n)
    {
        double u = (e - grid.Id.MinE) / grid.Spacing, v = (grid.Id.MaxN - n) / grid.Spacing;
        if (u == Math.Floor(u) && v == Math.Floor(v) && u >= 0 && v >= 0 && u < grid.Size && v < grid.Size)
            return ChunkFormat.Dequantize(grid.Heights[(int)v * grid.Size + (int)u]);
        return grid.SampleMeshHeight(e, n);
    }

    public sealed class Blend
    {
        internal sealed record Entry(RealTile Real);

        private readonly ProceduralWorld _world;
        private readonly Entry[] _near;
        private readonly Entry[] _details;
        /// <summary>The W lattice and the W R lattice, interleaved: one reference, so a race cannot tear them.</summary>
        private double[]? _s;
        private readonly long _i0, _j0;   // lattice index of the lattice's first point

        /// <summary>
        /// Per detail tile, at every whole metre of this tile's four edges, the five numbers D reads
        /// there (<see cref="Detail"/>; NaN until asked). From inside the tile the nearest point of a
        /// neighbour always lies on the tile's own edge, so a million vertices share these.
        /// </summary>
        private readonly double[][] _detMemo;
        private const int EdgeSlots = ChunkFormat.GridSize;

        // the tile's square, unpacked once: the per-vertex paths read them a million times
        private readonly double _minE, _maxE, _minN, _maxN;

        public TileId Tile { get; }
        public long Version { get; }

        /// <summary>Real tiles that can reach this one, in (N, E) order.</summary>
        public IEnumerable<TileId> Near => _near.Select(x => x.Real.Id);

        internal Blend(ProceduralWorld world, TileId tile, long version, Entry[] near)
        {
            _world = world;
            Tile = tile;
            Version = version;
            _near = near;
            _i0 = (long)(tile.MinE / SStep) - SBorder;
            _j0 = (long)(tile.MinN / SStep) - SBorder;
            _minE = tile.MinE;
            _maxE = tile.MinE + ChunkFormat.TileSizeM;
            _minN = tile.MinN;
            _maxN = tile.MaxN;
            _details = near
                .Where(x => x.Real.Grid != null && BlendGridStride(tile, x.Real.Id, true) != 0)
                .ToArray();
            _detMemo = new double[_details.Length][];
            for (int i = 0; i < _details.Length; i++)
            {
                _detMemo[i] = new double[DetailValues * 4 * EdgeSlots];
                Array.Fill(_detMemo[i], double.NaN);
            }
        }

        /// <summary>
        /// What to add to the generated height <paramref name="g"/> at an LV95 point: the ground
        /// there is <c>g + Correction(e, n, g)</c>, on every path.
        /// </summary>
        public double Correction(double e, double n, double g)
        {
            var (w, wr) = S(e, n);
            return Mix(g, w, wr, D(e, n));
        }

        /// <summary>How much of the ground at a point is real (W); for BlendCheck's measures.</summary>
        internal double RealWeight(double e, double n) => S(e, n).W;

        /// <summary>
        /// The one place the parts combine: (1 - W) G + W R + D, as a correction to G. The row
        /// path and the point path both come through here, so they agree to the bit.
        /// </summary>
        private static double Mix(double g, double w, double wr, double d) => wr - w * g + d;

        // ---- W and W R ------------------------------------------------------------------------

        /// <summary>
        /// Fills the tile's lattice, for the builds that ask at most points of the tile. A stride-10
        /// grid or the horizon reads a few thousand lattice points and computes them on the spot;
        /// either way the value is the same function of the point.
        /// </summary>
        public void PrepareLattice()
        {
            if (_s != null) return;
            var s = new double[2 * SSide * SSide];
            for (int lj = 0; lj < SSide; lj++)
                for (int li = 0; li < SSide; li++)
                {
                    var (w, wr) = Exact((_i0 + li) * (double)SStep, (_j0 + lj) * (double)SStep);
                    int k = 2 * (lj * SSide + li);
                    s[k] = w;
                    s[k + 1] = wr;
                }
            // a race only computes the same numbers twice
            _s = s;
        }

        private (double W, double WR) S(double e, double n)
        {
            double ci = Math.Floor(e / SStep), cj = Math.Floor(n / SStep);
            double u = (e - ci * SStep) / SStep, v = (n - cj * SStep) / SStep;
            long i = (long)ci, j = (long)cj;
            var s = _s;
            long li = i - _i0, lj = j - _j0;
            if (s != null && li >= 0 && lj >= 0 && li + 1 < SSide && lj + 1 < SSide)
            {
                int k = (int)(lj * SSide + li);
                return Interpolate(s, k, u, v);
            }
            if (u == 0 && v == 0) return At(i, j);
            var (a, b, c, d) = (At(i, j), At(i + 1, j), At(i, j + 1), At(i + 1, j + 1));
            return (Bilerp(a.W, b.W, c.W, d.W, u, v), Bilerp(a.WR, b.WR, c.WR, d.WR, u, v));
        }

        /// <summary>Both lattices at a cell: the same Bilerp the corner-by-corner path does.</summary>
        private static (double W, double WR) Interpolate(double[] s, int k, double u, double v)
        {
            int a = 2 * k, b = 2 * (k + 1), c = 2 * (k + SSide), d = 2 * (k + SSide + 1);
            return (Bilerp(s[a], s[b], s[c], s[d], u, v), Bilerp(s[a + 1], s[b + 1], s[c + 1], s[d + 1], u, v));
        }

        private (double W, double WR) At(long i, long j)
        {
            var s = _s;
            long li = i - _i0, lj = j - _j0;
            if (s != null && li >= 0 && lj >= 0 && li < SSide && lj < SSide)
            {
                int k = (int)(2 * (lj * SSide + li));
                return (s[k], s[k + 1]);
            }
            return Exact(i * (double)SStep, j * (double)SStep);
        }

        /// <summary>
        /// W, how much of the ground here is real, and W R: the real low-pass carried outward (read
        /// coarser the further it is carried), inverse-distance weighted over every real tile
        /// within Band, times W.
        /// </summary>
        private (double W, double WR) Exact(double e, double n)
        {
            double sw = 0, swr = 0, soft = 0;
            foreach (var x in _near)
            {
                var k = x.Real.Id;
                double d = Nearest(k, e, n, out double qe, out double qn);
                if (d >= Band) continue;
                double fade = 1 - SmoothStep(0, Band, d);
                // faded with the tile's own distance, so a tile leaving the band leaves the sum
                // continuously: far out every term is tiny, and dropping one of two halved it
                soft += Math.Exp(-d / SoftMinM) * fade;
                double w = fade / (d * d + Soften);
                sw += w;
                swr += w * Low(x.Real, qe, qn, d);
            }
            if (sw == 0) return (0, 0);
            // a soft minimum of the distances, not the minimum: the nearest tile changes on the
            // medial axis of a hole or a notch, and W creased along it (an X across a one-tile hole)
            double reach = soft <= 0 ? Band : Math.Clamp(-SoftMinM * Math.Log(soft), 0, Band);
            double weight = 1 - SmoothStep(0, Band, reach);
            return (weight, weight * (swr / sw));
        }

        // ---- D -------------------------------------------------------------------------------

        private double D(double e, double n)
        {
            if (_details.Length == 0) return 0;
            // every detail tile is a neighbour, so none is nearer than this tile's own boundary
            double inset = Math.Min(Math.Min(e - _minE, _maxE - e), Math.Min(n - _minN, _maxN - n));
            if (inset >= DetailBand) return 0;

            double sw = 0, swd = 0;
            for (int i = 0; i < _details.Length; i++)
            {
                var x = _details[i];
                var k = x.Real.Id;
                double d = Nearest(k, e, n, out double qe, out double qn);
                if (d >= DetailBand) continue;
                double fd = 1 - SmoothStep(0, DetailBand, d);
                double w = fd / (d * d + Soften);
                // the real surface continued to first order: its residual at q, plus its slope
                // across the edge (taken from inside the real tile) times the distance out — the
                // slope only across the first coarse cell (SlopeFade), where it makes the seam C1
                double de = e - qe, dn = n - qn;
                double value = Detail(i, qe, qn, DetResidual)
                    + (de > 0 ? de * SlopeFade(de) * Detail(i, qe, qn, SlopeFromWest)
                        : de < 0 ? de * SlopeFade(-de) * Detail(i, qe, qn, SlopeFromEast) : 0)
                    + (dn > 0 ? dn * SlopeFade(dn) * Detail(i, qe, qn, SlopeFromSouth)
                        : dn < 0 ? dn * SlopeFade(-dn) * Detail(i, qe, qn, SlopeFromNorth) : 0);
                sw += w;
                swd += w * fd * value;
            }
            return sw == 0 ? 0 : swd / sw;
        }

        /// <summary>
        /// How much of the continued slope is kept at a distance out: 1 with zero derivative at the
        /// seam (so the seam is C1), gone with zero derivative one coarse cell out. Gone exactly at
        /// every 10 m point, which is what keeps a coarse tile the decimation of the full one: the
        /// slope is the one part of D a 1 m grid and a 10 m grid would read differently. Carried
        /// the whole band instead, a steep 1 m slope was extrapolated metres (3.8 m measured).
        /// </summary>
        private static double SlopeFade(double distance) => 1 - SmoothStep(0, SlopeStep, distance);

        // what Detail returns, and its slot in the memo
        private const int DetResidual = 0, SlopeFromWest = 1, SlopeFromEast = 2, SlopeFromSouth = 3, SlopeFromNorth = 4;
        private const int DetailValues = 5;

        /// <summary>
        /// One of the numbers D continues a detail tile's surface with, at its point q: the real
        /// residual (R - Rs, zero at a knot — the ground's roughness the knots do not hold), or the
        /// real surface's slope along an axis, one-sided from inside the real tile: over 1 m on a
        /// full grid, so the seam is C1 where it is drawn; over <see cref="SlopeStep"/> on a coarse one
        /// and at a real corner (the only q two tiles share, one of which may hold that neighbour
        /// coarse). Only ever read between the seam and the first 10 m point (<see cref="SlopeFade"/>),
        /// so the two never meet at a vertex both grids have. The slope is of R itself, not of the residual: beyond the edge the low-pass is flat along the
        /// normal, so its slope has to be carried here or the seam creases.
        /// </summary>
        private double Detail(int detail, double qe, double qn, int what)
        {
            int slot = EdgeSlot(qe, qn);
            var memo = _detMemo[detail];
            int m = slot < 0 ? -1 : slot * DetailValues + what;
            if (m >= 0 && !double.IsNaN(memo[m])) return memo[m];

            var real = _details[detail].Real;
            var grid = real.Grid!;
            double r0 = RealAt(grid, qe, qn);
            // 1 m where the full grid has it, so the seam is C1 at the resolution drawn; 10 m at a
            // real tile's corner, which is the only q two tiles can share (and which a diagonal
            // neighbour, held coarse, must agree on), and on a coarse grid
            double step = grid.Stride == 1 && !IsCorner(real.Id, qe, qn) ? grid.Spacing : SlopeStep;
            double value = what switch
            {
                DetResidual => r0 - KnotAt(real.Knots, real.Id, qe, qn),
                SlopeFromWest => (r0 - RealAt(grid, qe - step, qn)) / step,
                SlopeFromEast => (RealAt(grid, qe + step, qn) - r0) / step,
                SlopeFromSouth => (r0 - RealAt(grid, qe, qn - step)) / step,
                _ => (RealAt(grid, qe, qn + step) - r0) / step,
            };
            // a race only stores the same number twice
            if (m >= 0) memo[m] = value;
            return value;
        }

        private static bool IsCorner(TileId k, double e, double n) =>
            (e == k.MinE || e == k.MinE + ChunkFormat.TileSizeM) && (n == k.MinN || n == k.MaxN);

        /// <summary>Index of a whole-metre point on the tile's boundary, or -1.</summary>
        private int EdgeSlot(double e, double n)
        {
            int edge;
            double along;
            if (e == _minE) { edge = 0; along = _maxN - n; }
            else if (e == _maxE) { edge = 1; along = _maxN - n; }
            else if (n == _maxN) { edge = 2; along = e - _minE; }
            else if (n == _minN) { edge = 3; along = e - _minE; }
            else return -1;
            if (along < 0 || along > ChunkFormat.TileSizeM || along != Math.Floor(along)) return -1;
            return edge * EdgeSlots + (int)along;
        }

        // ---- a whole grid row ----------------------------------------------------------------

        /// <summary>
        /// <see cref="Correction"/> along one row of a stride-1 grid, to the bit, without redoing
        /// per vertex what only changes per row or per column: <paramref name="g"/> holds the
        /// generated heights of the row, and receives the blended ones. Needs <see cref="PrepareLattice"/>.
        /// </summary>
        internal void BlendRow(double n, double[] g)
        {
            var s = _s ?? throw new InvalidOperationException("BlendRow before PrepareLattice");
            if (g.Length != ChunkFormat.GridSize) throw new ArgumentException("BlendRow is for stride-1 rows");

            // the same u, v and cells S computes, so the same Bilerp on the same numbers
            var cells = _rowCells ??= ColumnCells();
            var colK = cells.K;
            var colU = cells.U;
            double cj = Math.Floor(n / SStep);
            double v = (n - cj * SStep) / SStep;
            int rowK = (int)(((long)cj - _j0) * SSide);
            bool nearRowEdge = _details.Length > 0 && Math.Min(n - _minN, _maxN - n) < DetailBand;

            for (int c = 0; c < g.Length; c++)
            {
                var (w, wr) = Interpolate(s, rowK + colK[c], colU[c], v);
                bool nearEdge = nearRowEdge || (_details.Length > 0 && (c < DetailBand || c > ChunkFormat.TileSizeM - DetailBand));
                g[c] += Mix(g[c], w, wr, nearEdge ? D(_minE + c, n) : 0);
            }
        }

        /// <summary>Each stride-1 column's S lattice cell and fraction; a reference, so a race cannot tear it.</summary>
        private sealed record Cells(int[] K, double[] U);

        private Cells? _rowCells;

        private Cells ColumnCells()
        {
            var k = new int[ChunkFormat.GridSize];
            var u = new double[ChunkFormat.GridSize];
            for (int c = 0; c < k.Length; c++)
            {
                double e = _minE + c;
                double ci = Math.Floor(e / SStep);
                u[c] = (e - ci * SStep) / SStep;
                k[c] = (int)((long)ci - _i0);
            }
            return new Cells(k, u);
        }

        // ---- the seam ------------------------------------------------------------------------

        /// <summary>The quantised height of a real grid vertex at this point, if a neighbour holds one.</summary>
        public bool TryRealVertex(double e, double n, out ushort height)
        {
            foreach (var x in _details)
            {
                var g = x.Real.Grid!;
                var k = g.Id;
                if (e < k.MinE || e > k.MinE + ChunkFormat.TileSizeM || n < k.MinN || n > k.MaxN) continue;
                double u = (e - k.MinE) / g.Spacing, v = (k.MaxN - n) / g.Spacing;
                if (u != Math.Floor(u) || v != Math.Floor(v)) continue;
                height = g.Heights[(int)v * g.Size + (int)u];
                return true;
            }
            height = 0;
            return false;
        }

        /// <summary>The quantised knot of a real tile at this point, if one has a knot there.</summary>
        public bool TryRealKnot(double e, double n, out ushort height)
        {
            foreach (var x in _near)
            {
                var k = x.Real.Id;
                if (e < k.MinE || e > k.MinE + ChunkFormat.TileSizeM || n < k.MinN || n > k.MaxN) continue;
                double u = (e - k.MinE) / HorizonFormat.SpacingM, v = (k.MaxN - n) / HorizonFormat.SpacingM;
                if (u != Math.Floor(u) || v != Math.Floor(v)) continue;
                height = x.Real.KnotsQ[(int)v * HorizonFormat.SamplesPerSide + (int)u];
                return true;
            }
            height = 0;
            return false;
        }

        /// <summary>
        /// Whether a river may be drawn at a point: not on a bed the blend has tilted. Central
        /// differences over the cover raster's 10 m spacing, as the raster computes it.
        /// </summary>
        internal bool WaterAllowed(double e, double n)
        {
            const double d = CoverStep;
            double C(double pe, double pn) => Correction(pe, pn, _world.Height(pe, pn));
            double gx = (C(e + d, n) - C(e - d, n)) / (2 * d);
            double gy = (C(e, n + d) - C(e, n - d)) / (2 * d);
            return WaterAllowed(Math.Sqrt(gx * gx + gy * gy));
        }

        internal static bool WaterAllowed(double tilt) => tilt <= MaxWaterTilt;
    }

    // ---- the site: everything a height query needs ---------------------------------------------

    /// <summary>
    /// What a height query on a tile reads: the tile's noise lattice (or none, to evaluate on the
    /// spot, which gives the same bits) and its blend (or none). Every query about ground, from the
    /// grid to a tree, a road vertex or a farm's footing, goes through <see cref="Ground"/>.
    /// </summary>
    private readonly record struct Site(Lattice? Noise, Blend? Blend);

    private double Ground(in Site site, double e, double n)
    {
        double h = Height(site.Noise, e, n);
        return site.Blend is { } b ? h + b.Correction(e, n, h) : h;
    }
}
