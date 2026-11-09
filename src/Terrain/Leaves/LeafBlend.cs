using System.Collections.Concurrent;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// Generated ground bent to meet the real leaves (#1510, G2 C1 PR 3): the flat world's fill blend
/// (<c>ProceduralWorld.Blend</c>, <c>terrain/generated-fill</c>) on the cube lattice, convex all the way:
///
///     M(p) = (1 - W(p)) G(p) + W(p) R(p)
///     h(p) = (1 - F(p)) M(p) + F(p) C(p)
///
/// G is the generator's height above sea level; R the real ground's low-pass carried outward, inverse-distance weighted
/// over every real <see cref="RealLeaf.Block"/> within <see cref="Band"/>, each read at its nearest point q, coarser the
/// further it is carried (<see cref="RealLeaf.Low"/>); W = 1 - smoothstep(0, Band, a soft minimum of the distances)
/// hands the ground from one to the other. Near the seam C is the real surface continued to first order from q (its
/// height there and its slope across the edge), and F = 1 - smoothstep(0, <see cref="DetailBand"/>, a soft minimum of
/// the distances) hands M over to it: at a block F = 1 and h is the real ground, with zero slope in F, so the seam is
/// neither a step nor a crease. (The flat world adds a detail term D to M instead; handing over convexly lets R be
/// smoothed, <see cref="SoftenR"/>, so the border's cells are not carried outward as fingers.) A mix of two heights
/// lies between them: never the additive correction that dug a trench under the Rhône
/// (<c>terrain/blend-convex-not-additive</c>).
///
/// <para>
/// The real ground is the blocks of whole real cells, not whole leaves: at the region's border a leaf is real only in
/// part. A vertex in a block keeps its real height (<see cref="RealLeaf.Kept"/>); every other vertex of a part-real leaf
/// is the fill's, as is every vertex of a leaf the region does not hold. A fill vertex on a block's edge (a leaf edge
/// shared with a real leaf) copies that leaf's height, so the seam is exact.
/// </para>
///
/// <para>
/// Seamless between leaves with no coordination: every quantity is a function of a vertex's place on its face's
/// global lattice (level + bits), its pieces summed in the same order from every leaf; W and W R are taken every
/// <see cref="ControlUnits"/> vertices (on the global lattice) and bilinear between, as the flat world's 10 m lattice.
/// Real leaves on another cube face are not seen (the D: region is on face 0, Switzerland's face).
/// </para>
///
/// <para>
/// What stands on the ground moves with it: trees, buildings and roads by the blend's change at their place, and still
/// water is dropped where the blend tilts its bed past 1.5 % (the flat world's <c>MaxWaterTilt</c>).
/// </para>
/// Thread-safe. Godot-free (BlendCheck compiles it).
/// </summary>
public sealed class LeafBlend
{
    /// <summary>Width of the band across which generated ground bends to meet real ground (the flat world's).</summary>
    public const double Band = 3000;

    /// <summary>Width of the hand-over from the band's mix to the real surface continued, metres (the flat world's D reaches 40).</summary>
    public const double DetailBand = 60;

    /// <summary>Width of the soft minimum the hand-over takes of the distances to the blocks, metres: rounds the staircase's corners.</summary>
    public const double SoftDetailM = 12;

    /// <summary>How far the continued slope reaches (the flat world's coarse cell, 10 m).</summary>
    public const double SlopeStepM = 10;

    /// <summary>Width of the soft minimum W takes of the distances, metres.</summary>
    public const double SoftMinM = 150;

    /// <summary>Added to d² in the detail term's inverse-distance weights, m²: small, so the nearest block rules at the seam.</summary>
    public const double Soften = 0.01;

    /// <summary>
    /// Added to d² in R's inverse-distance weights, m² (a cell's side squared): R reads every block within a cell or so
    /// alike, so the border's staircase of cells is not carried outward as fingers. The seam itself is the detail
    /// term's (<see cref="Continued"/>), which takes the ground whole at a block.
    /// </summary>
    public const double SoftenR = 40 * 40;

    /// <summary>Spacing of the W / W R lattice, lattice vertices (~19 m at level 14).</summary>
    public const int ControlUnits = 16;

    /// <summary>Steepest a blend may tilt a still water bed and keep it water (a grade).</summary>
    public const double MaxWaterTilt = 0.015;

    /// <summary>Real leaves' heights kept for the detail term (1 MB each).</summary>
    public const int DetailKept = 48;

    private readonly Func<CubeKey, bool> _isReal;
    private readonly Func<CubeKey, CubeTile?> _heights;
    private readonly LeafFrames _frames;
    private readonly ConcurrentDictionary<CubeKey, Lazy<RealLeaf?>> _summaries = new();
    private readonly ConcurrentDictionary<CubeKey, Lazy<float[]?>> _detail = new();
    private readonly ConcurrentQueue<CubeKey> _detailOrder = new();

    /// <param name="isReal">Whether the region holds a leaf.</param>
    /// <param name="heights">A real leaf's heights (ellipsoidal, NaN where none).</param>
    public LeafBlend(Func<CubeKey, bool> isReal, Func<CubeKey, CubeTile?> heights, LeafFrames? frames = null)
    {
        _isReal = isReal;
        _heights = heights;
        _frames = frames ?? LeafFrames.Swiss;
    }

    /// <summary>The blend's view of a real leaf, made on the first ask and kept; null for a leaf with no real cell.</summary>
    public RealLeaf? Summary(CubeKey key) =>
        !_isReal(key) ? null : _summaries.GetOrAdd(key, k => new Lazy<RealLeaf?>(() =>
            _heights(k) is { } h ? RealLeaf.Of(h, _frames.TransformOf(k)) : null)).Value;

    /// <summary>A real leaf's heights for the detail term, the last <see cref="DetailKept"/> held.</summary>
    private float[]? Detail(CubeKey key)
    {
        bool added = false;
        var lazy = _detail.GetOrAdd(key, k => { added = true; return new Lazy<float[]?>(() => _heights(k)?.Heights); });
        if (added)
        {
            _detailOrder.Enqueue(key);
            while (_detail.Count > DetailKept && _detailOrder.TryDequeue(out var old)) _detail.TryRemove(old, out _);
        }
        return lazy.Value;
    }

    // ---- the pieces --------------------------------------------------------------------------------------------

    /// <summary>A real block on the face's global lattice (bounds inclusive).</summary>
    private readonly record struct Piece(RealLeaf Leaf, long I0, long J0, long I1, long J1)
    {
        /// <summary>The nearest point of the block to a global lattice point, and the distance to it in metres.</summary>
        public double Nearest(long gi, long gj, out long qi, out long qj)
        {
            qi = Math.Clamp(gi, I0, I1);
            qj = Math.Clamp(gj, J0, J1);
            double di = (gi - qi) * Leaf.ScaleI, dj = (gj - qj) * Leaf.ScaleJ;
            return Math.Sqrt(di * di + dj * dj);
        }

        /// <summary>From a box of the global lattice to the block, metres (0 when they touch).</summary>
        public double Gap(long i0, long j0, long i1, long j1)
        {
            double di = Math.Max(0, Math.Max(I0 - i1, i0 - I1)) * Leaf.ScaleI;
            double dj = Math.Max(0, Math.Max(J0 - j1, j0 - J1)) * Leaf.ScaleJ;
            return Math.Sqrt(di * di + dj * dj);
        }
    }

    /// <summary>
    /// The real blocks within <paramref name="reach"/> metres of a leaf, in one order whatever the leaf: real leaves
    /// south to north, west to east, each one's blocks in its quadtree order.
    /// </summary>
    private List<Piece> PiecesNear(CubeKey leaf, double reach)
    {
        var pieces = new List<Piece>();
        int bits = CubeKey.LatticeBits(leaf.Level), cells = 1 << bits;
        long i0 = (long)leaf.X << bits, j0 = (long)leaf.Y << bits, i1 = i0 + cells, j1 = j0 + cells;
        // a level-14 leaf is ~610 m: the window holds every leaf within the band of any point of this one
        int k = (int)Math.Ceiling(reach / 550) + 1, side = 1 << leaf.Level;
        for (int y = Math.Max(0, leaf.Y - k); y <= Math.Min(side - 1, leaf.Y + k); y++)
            for (int x = Math.Max(0, leaf.X - k); x <= Math.Min(side - 1, leaf.X + k); x++)
            {
                var key = new CubeKey(leaf.Face, leaf.Level, x, y);
                if (Summary(key) is not { } real) continue;
                foreach (var b in real.Blocks)
                {
                    var p = new Piece(real, real.Gi0 + b.I0, real.Gj0 + b.J0, real.Gi0 + b.I0 + b.Size, real.Gj0 + b.J0 + b.Size);
                    if (p.Gap(i0, j0, i1, j1) <= reach) pieces.Add(p);
                }
            }
        return pieces;
    }

    /// <summary>Whether any real ground lies within the band of a leaf: else the fill is the generator's alone there.</summary>
    public bool Reaches(CubeKey leaf) => PiecesNear(leaf, Band).Count > 0;

    // ---- W, W R and D ------------------------------------------------------------------------------------------

    /// <summary>W and W R at a global lattice point: <c>ProceduralWorld.Blend.Exact</c>'s sums over the blocks.</summary>
    private static (double W, double WR) Exact(List<Piece> pieces, long gi, long gj)
    {
        double sw = 0, swr = 0, soft = 0;
        foreach (var p in pieces)
        {
            double d = p.Nearest(gi, gj, out long qi, out long qj);
            if (d >= Band) continue;
            double fade = 1 - SmoothStep(0, Band, d);
            // faded with the block's own distance, so a block leaving the band leaves the sum continuously
            soft += Math.Exp(-d / SoftMinM) * fade;
            double w = fade / (d * d + SoftenR);
            sw += w;
            swr += w * p.Leaf.Low(qi - p.Leaf.Gi0, qj - p.Leaf.Gj0, d);
        }
        if (sw == 0) return (0, 0);
        // a soft minimum of the distances, not the minimum: the nearest block changes on the medial axis of a notch
        // or a hole, and W creased along it
        double reach = soft <= 0 ? Band : Math.Clamp(-SoftMinM * Math.Log(soft), 0, Band);
        double weight = 1 - SmoothStep(0, Band, reach);
        return (weight, weight * (swr / sw));
    }

    /// <summary>A block near enough for the detail term, with its leaf's heights and transform at hand.</summary>
    private sealed record Near(Piece Piece, float[] H, LeafTransform T);

    /// <summary>
    /// The real surface near a global lattice point: every block within <see cref="DetailBand"/> continued to first
    /// order from its nearest point q (its height there plus its slope across the edge, taken from inside, over the
    /// first <see cref="SlopeStepM"/>), inverse-distance weighted (<paramref name="c"/>, above sea level), and how much
    /// of the ground it takes (<paramref name="f"/>: 1 at a block, 0 from <see cref="DetailBand"/> out, flat at both
    /// ends so the seam is C1). True with the block's own height (ellipsoidal) when the point lies on one.
    /// </summary>
    private static bool Continued(Near[] near, long gi, long gj, out double f, out double c, out float onReal)
    {
        double sw = 0, swc = 0, soft = 0;
        f = 0;
        onReal = float.NaN;
        foreach (var x in near)
        {
            var p = x.Piece;
            double dist = p.Nearest(gi, gj, out long qi, out long qj);
            if (dist >= DetailBand) continue;
            var leaf = p.Leaf;
            var h = x.H;
            int n = leaf.Cells + 1, li = (int)(qi - leaf.Gi0), lj = (int)(qj - leaf.Gj0);
            if (dist == 0)
            {
                onReal = h[lj * n + li];
                c = f = 0;
                return true;
            }
            var t = x.T;
            double Orth(int i, int j) => h[j * n + i] - t.UndulationAt(i, j);
            double r0 = Orth(li, lj);
            long di = gi - qi, dj = gj - qj;
            double value = r0;
            // the slope from inside the block (whole cells: a neighbour inside is always real)
            if (di != 0)
            {
                double s = di < 0 ? Orth(li + 1, lj) - r0 : r0 - Orth(li - 1, lj);
                value += di * s * (1 - SmoothStep(0, SlopeStepM, Math.Abs(di) * leaf.ScaleI));
            }
            if (dj != 0)
            {
                double s = dj < 0 ? Orth(li, lj + 1) - r0 : r0 - Orth(li, lj - 1);
                value += dj * s * (1 - SmoothStep(0, SlopeStepM, Math.Abs(dj) * leaf.ScaleJ));
            }
            double fd = 1 - SmoothStep(0, DetailBand, dist);
            soft += Math.Exp(-dist / SoftDetailM) * fd;
            double w = fd / (dist * dist + Soften);
            sw += w;
            swc += w * value;
        }
        c = sw == 0 ? 0 : swc / sw;
        // a soft minimum of the distances: the hand-over's edge round the border's staircase of cells, not a scallop
        // round each corner; at most the nearest distance, so a block's own edge is still taken whole
        double reach = soft <= 0 ? DetailBand : Math.Clamp(-SoftDetailM * Math.Log(soft), 0, DetailBand);
        f = 1 - SmoothStep(0, DetailBand, reach);
        return false;
    }

    // ---- a leaf's heights --------------------------------------------------------------------------------------

    /// <summary>A leaf's blended heights, and what the blend changed.</summary>
    public sealed record Result(float[] Heights, bool[]? Kept, double[] Delta);

    /// <summary>
    /// The leaf's heights: the generated ones (ellipsoidal) blended into the real ground near it, real ones kept where
    /// the leaf's own real cells have them. Null when no real ground is within the band and the leaf is not real.
    /// </summary>
    public Result? Heights(CubeKey leaf, float[] generated, float[]? ownReal = null)
    {
        var all = PiecesNear(leaf, Band);
        if (all.Count == 0) return null;
        var own = ownReal != null ? Summary(leaf) : null;
        var t = _frames.TransformOf(leaf);
        int cells = t.Cells, n = cells + 1;
        long gi0 = (long)leaf.X << t.Bits, gj0 = (long)leaf.Y << t.Bits;
        var near = all
            .Where(p => p.Gap(gi0, gj0, gi0 + cells, gj0 + cells) < DetailBand)
            .Select(p => Detail(p.Leaf.Key) is { } h ? new Near(p, h, _frames.TransformOf(p.Leaf.Key)) : null)
            .OfType<Near>().ToArray();

        // W and W R on the control lattice
        int cs = cells / ControlUnits + 1;
        var w = new double[cs * cs];
        var wr = new double[cs * cs];
        for (int cj = 0; cj < cs; cj++)
            for (int ci = 0; ci < cs; ci++)
                (w[cj * cs + ci], wr[cj * cs + ci]) = Exact(all, gi0 + ci * ControlUnits, gj0 + cj * ControlUnits);

        var heights = new float[n * n];
        var delta = new double[n * n];
        var kept = own != null ? new bool[n * n] : null;
        Parallel.For(0, n, j =>
        {
            // the blocks within the detail band of this row
            long gj = gj0 + j;
            var nearRow = near.Where(x => Math.Max(0, Math.Max(x.Piece.J0 - gj, gj - x.Piece.J1)) * x.Piece.Leaf.ScaleJ < DetailBand).ToArray();
            int cj = Math.Min(j / ControlUnits, cs - 2);
            double v = (j - cj * ControlUnits) / (double)ControlUnits;
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                float g = generated[k];
                if (own != null && own.Kept(i, j) && !float.IsNaN(ownReal![k]))
                {
                    heights[k] = ownReal[k];
                    kept![k] = true;
                    delta[k] = float.IsNaN(g) ? 0 : ownReal[k] - g;
                    continue;
                }
                if (float.IsNaN(g))
                {
                    heights[k] = g;
                    continue;
                }
                if (Continued(nearRow, gi0 + i, gj0 + j, out double f, out double cont, out float onReal))
                {
                    heights[k] = onReal;
                    delta[k] = onReal - g;
                    continue;
                }
                int ci = Math.Min(i / ControlUnits, cs - 2);
                double u = (i - ci * ControlUnits) / (double)ControlUnits;
                int c = cj * cs + ci;
                double ww = Bilerp(w[c], w[c + 1], w[c + cs], w[c + cs + 1], u, v);
                double wwr = Bilerp(wr[c], wr[c + 1], wr[c + cs], wr[c + cs + 1], u, v);
                double und = t.UndulationAt(i, j);
                double gs = g - und;
                // far from the seam the band's mix; within the detail band, handed convexly to the real surface continued
                double h = (1 - f) * ((1 - ww) * gs + wwr) + f * cont;
                heights[k] = (float)(h + und);
                delta[k] = heights[k] - g;
            }
        });
        return new Result(heights, kept, delta);
    }

    // ---- a whole leaf ------------------------------------------------------------------------------------------

    /// <summary>
    /// A leaf of the fill blended into the real ground round it: <paramref name="generated"/> alone for a leaf the region
    /// does not hold, or merged into <paramref name="real"/> where the region holds part of it (its real cells kept with
    /// everything on them, the rest the fill's). The generated leaf unchanged when no real ground is within the band.
    /// </summary>
    public CubeLeafContent Apply(CubeLeafContent generated, CubeLeafContent? real = null)
    {
        var key = generated.Key;
        if (generated.Heights is not { } gh) return real ?? generated;
        var r = Heights(key, gh.Heights, real?.Heights?.Heights);
        if (r is null) return real ?? generated;
        var t = _frames.TransformOf(key);
        int n = t.Cells + 1;
        var lift = new Lift(t, r.Delta, n);
        bool InFill(float x, float y, float z)
        {
            if (r.Kept is null) return true;
            var (i, j, _) = t.LocalToLattice(x, y, z);
            int a = Math.Clamp((int)Math.Round(i), 0, n - 1), b = Math.Clamp((int)Math.Round(j), 0, n - 1);
            return !r.Kept[b * n + a];
        }

        var content = new CubeLeafContent
        {
            Key = key,
            Heights = new CubeTile { Key = key, Bits = gh.Bits, Heights = r.Heights, Quantum = gh.Quantum },
            Cover = Merge(generated.Cover, real?.Cover, r.Kept),
            Water = Water(generated.Water, real?.Water, r, t),
            Holes = real?.Holes,
            Fields = real?.Fields,
            Pistes = real?.Pistes,
            Photo = real?.Photo,
            Trees = new(),
            Buildings = new BuildingTile { Buildings = new() },
        };
        // what the region put on the cells the fill took (its roads, buildings and trees run on past its heights) is
        // seated on the fill's ground there: left at the region's heights it hung over the fill, its bridge faces
        // colliding 27 m up north of Riddes
        var seat = r.Kept is null ? null : new Seat(t, r.Heights, r.Kept, n);
        if (real?.Trees is { } rt)
            foreach (var tree in rt)
                content.Trees.Add(seat is null ? tree : tree with { Y = seat.Ground(tree.X, tree.Y, tree.Z) ?? tree.Y });
        if (generated.Trees is { } gt)
            foreach (var tree in gt)
                if (InFill(tree.X, tree.Y, tree.Z)) content.Trees.Add(tree with { Y = tree.Y + (float)lift.At(tree.X, tree.Y, tree.Z) });
        if (real?.Buildings is { } rb)
            foreach (var b in rb.Buildings)
            {
                var (mx, mz) = SourceTileToLeaf.Middle(b);
                content.Buildings.Buildings.Add(seat?.Ground(mx, b.MinY, mz) is float y ? Lifted(b, y - b.MinY) : b);
            }
        if (generated.Buildings is { } gb)
            foreach (var b in gb.Buildings)
            {
                var (mx, mz) = SourceTileToLeaf.Middle(b);
                float y = (b.MinY + b.MaxY) / 2;
                if (!InFill(mx, y, mz)) continue;
                content.Buildings.Buildings.Add(Lifted(b, (float)lift.At(mx, b.MinY, mz)));
            }
        // a part-real leaf keeps its real roads; the fill's roads are those of the leaves the region does not hold
        if (real?.Roads is { } realRoads)
        {
            if (seat is null) content.Roads = realRoads;
            else
            {
                // a copy: the store keeps its own
                var copy = LeafRoads.Decode(LeafRoads.Encode(realRoads));
                LeafRecords.Map(copy, seat);
                content.Roads = copy;
            }
        }
        else if (generated.Roads is { } roads)
        {
            LeafRecords.Map(roads, lift);
            content.Roads = roads;
        }
        return content;
    }

    private static Building Lifted(Building b, float dy)
    {
        var tri = (float[])b.Triangles.Clone();
        for (int k = 1; k < tri.Length; k += 3) tri[k] += dy;
        return new Building { Kind = b.Kind, Egid = b.Egid, YearBuilt = b.YearBuilt, Floors = b.Floors, MinY = b.MinY + dy, MaxY = b.MaxY + dy, Triangles = tri };
    }

    private static byte[]? Merge(byte[]? generated, byte[]? real, bool[]? kept)
    {
        if (kept is null || real is null) return generated ?? real;
        if (generated is null) return real;
        var cells = (byte[])generated.Clone();
        for (int k = 0; k < cells.Length && k < kept.Length; k++)
            if (kept[k]) cells[k] = real[k];
        return cells;
    }

    /// <summary>
    /// Still water: the real samples on the real cells, the fill's elsewhere moved with its bed, and dropped where the
    /// blend tilts the bed past <see cref="MaxWaterTilt"/>.
    /// </summary>
    private static CubeWater? Water(CubeWater? generated, CubeWater? real, Result r, LeafTransform t)
    {
        if (generated is null) return r.Kept is null ? null : real;
        int bits = generated.Bits, side = generated.Side, n = t.Cells + 1;
        var w = CubeWater.Dry(bits);
        bool any = false;
        var (ax, ay, az) = t.LatticeToLocal(0, t.Cells / 2, 0);
        var (bx, by, bz) = t.LatticeToLocal(t.Cells, t.Cells / 2, 0);
        double stepM = 2 * CubeWater.Stride * Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az)) / t.Cells;
        for (int sr = 0; sr < side; sr++)
            for (int sc = 0; sc < side; sc++)
            {
                int i = sc * CubeWater.Stride, j = sr * CubeWater.Stride, k = j * n + i, s = sr * side + sc;
                if (r.Kept is { } kept && kept[k])
                {
                    if (real is null) continue;
                    w.Level[s] = real.Level[s];
                    w.Fetch[s] = real.Fetch[s];
                }
                else
                {
                    float level = generated.Level[s];
                    if (float.IsNaN(level)) continue;
                    double D(int a, int b) => r.Delta[Math.Clamp(b, 0, n - 1) * n + Math.Clamp(a, 0, n - 1)];
                    double gx = (D(i + CubeWater.Stride, j) - D(i - CubeWater.Stride, j)) / stepM;
                    double gy = (D(i, j + CubeWater.Stride) - D(i, j - CubeWater.Stride)) / stepM;
                    if (Math.Sqrt(gx * gx + gy * gy) > MaxWaterTilt) continue;
                    w.Level[s] = (float)(level + r.Delta[k]);
                    w.Fetch[s] = generated.Fetch[s];
                }
                any |= !float.IsNaN(w.Level[s]);
            }
        return any ? w : null;
    }

    /// <summary>
    /// Seats a part-real leaf's own records on the fill's ground where they stand on a cell the fill took (each point
    /// draped, a building by its base); those on its real cells stay. Headings stay.
    /// </summary>
    private sealed class Seat(LeafTransform t, float[] heights, bool[] kept, int n) : LeafRecords.IMap
    {
        /// <summary>The fill's ground (local y) under a point on a cell the fill took; null on a real cell.</summary>
        public float? Ground(float x, float y, float z)
        {
            var (i, j, _) = t.LocalToLattice(x, y, z);
            double fi = System.Math.Clamp(i, 0, n - 1), fj = System.Math.Clamp(j, 0, n - 1);
            int a = (int)System.Math.Round(fi), b = (int)System.Math.Round(fj);
            if (kept[b * n + a]) return null;
            int a0 = System.Math.Min((int)fi, n - 2), b0 = System.Math.Min((int)fj, n - 2);
            int k = b0 * n + a0;
            double h = Bilerp(heights[k], heights[k + 1], heights[k + n], heights[k + n + 1], fi - a0, fj - b0);
            if (double.IsNaN(h)) return null;
            return (float)t.LatticeToLocal(fi, fj, h).Y;
        }

        public (float X, float Y, float Z) Point(float x, float y, float z) => (x, Ground(x, y, z) ?? y, z);
        public float Godot(float heading, float x, float y, float z) => heading;
        public float Math(float angle, float x, float y, float z) => angle;
    }

    /// <summary>Moves a record up by the blend's change at its place; headings stay.</summary>
    private sealed class Lift(LeafTransform t, double[] delta, int n) : LeafRecords.IMap
    {
        public double At(float x, float y, float z)
        {
            var (i, j, _) = t.LocalToLattice(x, y, z);
            double fi = System.Math.Clamp(i, 0, n - 1), fj = System.Math.Clamp(j, 0, n - 1);
            int a = System.Math.Min((int)fi, n - 2), b = System.Math.Min((int)fj, n - 2);
            int k = b * n + a;
            return Bilerp(delta[k], delta[k + 1], delta[k + n], delta[k + n + 1], fi - a, fj - b);
        }

        public (float X, float Y, float Z) Point(float x, float y, float z) => (x, y + (float)At(x, y, z), z);
        public float Godot(float heading, float x, float y, float z) => heading;
        public float Math(float angle, float x, float y, float z) => angle;
    }

    internal static double Bilerp(double a, double b, double c, double d, double u, double v)
    {
        double south = a + (b - a) * u, north = c + (d - c) * u;
        return south + (north - south) * v;
    }

    internal static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
