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
    // real tile within Band, the bilinear of its 100 m knots (Rs) at the tile's nearest point,
    // weighted by inverse distance. W = 1 - smoothstep(0, Band, distance to the nearest real tile)
    // hands the ground from one to the other. D, within DetailBand only, adds the real residual
    // (R - Rs at the nearest point), so the real ground's roughness carries across the seam and
    // dies out 150 m in. At a real edge W = 1, and h = Rs + (R - Rs) = R.
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

    /// <summary>Distance over which the real ground's fine detail carries into generated ground.</summary>
    public const double DetailBand = 150;

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
        }
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

    /// <summary>Bilinear through a tile's 11x11 knots. Exactly the knot on a knot, edges included.</summary>
    private static double KnotAt(double[] knots, TileId k, double e, double n)
    {
        const int side = HorizonFormat.SamplesPerSide, last = side - 1;
        double u = (e - k.MinE) / HorizonFormat.SpacingM;
        double v = (k.MaxN - n) / HorizonFormat.SpacingM;
        int c0 = Math.Min((int)u, last), r0 = Math.Min((int)v, last);
        // on the far edge the cell is the last knot itself, not the last cell at fraction 1,
        // whose a + (b - a) * 1 is not always b
        int c1 = Math.Min(c0 + 1, last), r1 = Math.Min(r0 + 1, last);
        return Bilerp(knots[r0 * side + c0], knots[r0 * side + c1], knots[r1 * side + c0], knots[r1 * side + c1],
            u - c0, v - r0);
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
        /// Per detail tile, its det at every whole metre of this tile's four edges (NaN until
        /// asked). From inside the tile the nearest point of a neighbour always lies on the tile's
        /// own edge, so a million vertices share these few thousand values.
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
                _detMemo[i] = new double[4 * EdgeSlots];
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
        /// W, how much of the ground here is real, and W R: the real low-pass carried outward,
        /// inverse-distance weighted over every real tile within Band, times W.
        /// </summary>
        private (double W, double WR) Exact(double e, double n)
        {
            double sw = 0, swr = 0, minD = double.MaxValue;
            foreach (var x in _near)
            {
                var k = x.Real.Id;
                double d = Nearest(k, e, n, out double qe, out double qn);
                if (d >= Band) continue;
                if (d < minD) minD = d;
                double w = (1 - SmoothStep(0, Band, d)) / (d * d + Soften);
                sw += w;
                swr += w * KnotAt(x.Real.Knots, k, qe, qn);
            }
            if (sw == 0) return (0, 0);
            double weight = 1 - SmoothStep(0, Band, minD);
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
                sw += w;
                swd += w * fd * Det(i, qe, qn);
            }
            return sw == 0 ? 0 : swd / sw;
        }

        /// <summary>
        /// The real residual at a detail tile's point — the ground's roughness the 100 m knots do not
        /// hold, which D carries across the seam. Zero at a knot.
        /// </summary>
        private double Det(int detail, double qe, double qn)
        {
            int slot = EdgeSlot(qe, qn);
            var memo = _detMemo[detail];
            if (slot >= 0 && !double.IsNaN(memo[slot])) return memo[slot];

            var x = _details[detail];
            var k = x.Real.Id;
            double det = RealAt(x.Real.Grid!, qe, qn) - KnotAt(x.Real.Knots, k, qe, qn);
            // a race only stores the same number twice
            if (slot >= 0) memo[slot] = det;
            return det;
        }

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
