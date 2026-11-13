using System;
using System.Diagnostics;
using UnitSport.Terrain.Format;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The per-leaf transforms of the cube-native build (#1507, G2 B3; tools/TerrainFormat/LeafTransform.cs): a
/// work frame's plane point and height above sea level against a leaf's anchor frame and lattice, within
/// 0.1 mm of the rigorous chain (<see cref="WorkFrame.ToEcef"/>), the shared edges the same bits from both
/// leaves, headings through the Jacobian.
/// </summary>
public class LeafTransformTests
{
    private readonly ITestOutputHelper _out;

    public LeafTransformTests(ITestOutputHelper output) => _out = output;

    private const int Level = 14;

    /// <summary>A leaf seen from a frame: in its own frame, and from the next frame over (a margin leaf), across a face edge too.</summary>
    private static readonly (string Name, CubeKey Leaf, CubeKey Frame)[] Cases = Build();

    private static (string, CubeKey, CubeKey)[] Build()
    {
        var geneva = CubeKey.FromLatLon(46.2044, 6.1432, Level);
        var sion = CubeKey.FromLatLon(46.2331, 7.3606, Level);
        // the last leaf of face 0 along u, and the frame across the edge on the next face
        var edge = new CubeKey(0, Level, (1 << Level) - 1, 5000);
        var across = edge.Neighbour(1, 0)!.Value;
        var corner = new CubeKey(0, Level, (1 << Level) - 1, (1 << Level) - 1);
        var brest = CubeKey.FromLatLon(48.39, -4.49, Level);
        return new[]
        {
            ("Geneva", geneva, geneva.AncestorAt(WorkFrame.Level)),
            ("Sion", sion, sion.AncestorAt(WorkFrame.Level)),
            ("Geneva from the frame east", geneva, geneva.AncestorAt(WorkFrame.Level).Neighbour(1, 0)!.Value),
            ("face edge", edge, edge.AncestorAt(WorkFrame.Level)),
            ("across a face edge", across, edge.AncestorAt(WorkFrame.Level)),
            ("cube corner", corner, corner.AncestorAt(WorkFrame.Level)),
            ("Brest", brest, brest.AncestorAt(WorkFrame.Level)),
        };
    }

    private static WorkFrame FrameOf(CubeKey cell) => new(cell, SwissSeaLevel.Instance);

    /// <summary>The chain: lattice point → its normal → plane point and ECEF at a height above sea level.</summary>
    private static ((double X, double Y) Plane, EcefPos Ecef) Chain(WorkFrame frame, LeafTransform t, double i, double j, double hSea)
    {
        var n = t.NormalAt(i, j);
        double lat = Math.Atan2(n.Z, Math.Sqrt(n.X * n.X + n.Y * n.Y)) * 180 / Math.PI, lon = Math.Atan2(n.Y, n.X) * 180 / Math.PI;
        var plane = frame.Plane.ToPlane(lat, lon);
        return (plane, frame.ToEcef(plane.X, plane.Y, hSea));
    }

    private static double Dist(double ax, double ay, double az, double bx, double by, double bz) =>
        Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));

    [Fact]
    public void Plane_and_local_are_within_a_tenth_of_a_millimetre_of_the_chain_over_a_leaf()
    {
        var rng = new Random(1507);
        foreach (var (name, leaf, cell) in Cases)
        {
            var frame = FrameOf(cell);
            var t = new LeafTransform(frame, leaf);
            double worstLocal = 0, worstBack = 0, worstPlane = 0, worstLattice = 0;
            for (int k = 0; k < 400; k++)
            {
                // the leaf, its edges and its corners included
                double i = k < 8 ? (k & 1) * t.Cells : rng.NextDouble() * t.Cells, j = k < 8 ? ((k >> 1) & 1) * t.Cells : rng.NextDouble() * t.Cells;
                if (k >= 8 && k < 40) i = (k & 1) * t.Cells;
                double hSea = -500 + rng.NextDouble() * 5000;
                var (plane, ecef) = Chain(frame, t, i, j, hSea);
                var (ex, ey, ez) = t.Anchor.ToLocal(ecef);

                var (lx, ly, lz) = t.PlaneToLocal(plane.X, plane.Y, hSea);
                worstLocal = Math.Max(worstLocal, Dist(lx, ly, lz, ex, ey, ez));
                var (bx, by, bh) = t.LocalToPlane(ex, ey, ez);
                worstBack = Math.Max(worstBack, Dist(bx, by, bh, plane.X, plane.Y, hSea));
                var (px, py) = t.LatticeToPlane(i, j);
                worstPlane = Math.Max(worstPlane, Dist(px, py, 0, plane.X, plane.Y, 0));
                var (fi, fj) = t.PlaneToLattice(plane.X, plane.Y);
                var (qx, qy) = t.LatticeToPlane(fi, fj);
                worstLattice = Math.Max(worstLattice, Dist(qx, qy, 0, plane.X, plane.Y, 0));
                Assert.True(Math.Abs(fi - i) < 1e-4 && Math.Abs(fj - j) < 1e-4, $"{name}: plane to lattice ({fi:F6}, {fj:F6}) for ({i:F6}, {j:F6})");
            }
            _out.WriteLine($"{name}: plane->local {worstLocal * 1000:F4} mm, local->plane {worstBack * 1000:F4} mm, lattice->plane {worstPlane * 1000:F4} mm, plane->lattice->plane {worstLattice * 1000:F4} mm");
            Assert.True(worstLocal < 1e-4, $"{name}: plane to local {worstLocal * 1000:F3} mm off the chain");
            Assert.True(worstBack < 1e-4, $"{name}: local to plane {worstBack * 1000:F3} mm off the chain");
            Assert.True(worstPlane < 1e-4, $"{name}: lattice to plane {worstPlane * 1000:F3} mm off the chain");
            Assert.True(worstLattice < 1e-4, $"{name}: plane to lattice and back {worstLattice * 1000:F3} mm off");
        }
    }

    [Fact]
    public void The_undulation_holds_its_kinks_across_a_geoid_grid_line()
    {
        // LV95 E 2,560,000 is a CHGeo2004 grid line (every 20 km from 2,460,000): a leaf across it near Aigle
        var g = SwissGeodesy.ToWgs84(2_560_000, 1_130_000, 400);
        var leaf = CubeKey.FromLatLon(g.LatDeg, g.LonDeg, Level);
        var frame = FrameOf(leaf.AncestorAt(WorkFrame.Level));
        var t = new LeafTransform(frame, leaf);
        double worst = 0;
        for (int k = 0; k <= 2000; k++)
        {
            double i = t.Cells * (k / 2000.0), j = t.Cells * 0.37;
            var n = t.NormalAt(i, j);
            double lat = Math.Atan2(n.Z, Math.Sqrt(n.X * n.X + n.Y * n.Y)) * 180 / Math.PI, lon = Math.Atan2(n.Y, n.X) * 180 / Math.PI;
            worst = Math.Max(worst, Math.Abs(t.UndulationAt(i, j) - SwissSeaLevel.Instance.Undulation(lat, lon)));
        }
        _out.WriteLine($"undulation across a grid line: worst {worst * 1000:F4} mm");
        Assert.True(worst < 1e-5, $"the undulation is {worst * 1000:F3} mm off across a grid line");
    }

    [Fact]
    public void The_swiss_sea_level_split_sums_to_its_undulation_to_the_bit()
    {
        var rng = new Random(7);
        for (int k = 0; k < 2000; k++)
        {
            double lat = 45.5 + rng.NextDouble() * 2.5, lon = 5.5 + rng.NextDouble() * 5;
            var (smooth, a, b) = SwissSeaLevel.Instance.Split(lat, lon);
            Assert.Equal(SwissSeaLevel.Instance.Undulation(lat, lon), smooth + SwissSeaLevel.Instance.Grid(a, b));
        }
    }

    [Fact]
    public void A_shared_edge_maps_to_the_same_bits_from_both_leaves()
    {
        var geneva = CubeKey.FromLatLon(46.2044, 6.1432, Level);
        var edge = new CubeKey(0, Level, (1 << Level) - 1, 5000);
        foreach (var (a, side) in new[] { (geneva, 0), (geneva, 1), (edge, 0), (edge, 1), (new CubeKey(0, Level, (1 << Level) - 1, (1 << Level) - 1), 1) })
        {
            var b = a.EdgeNeighbour(side);
            var frame = FrameOf(a.AncestorAt(WorkFrame.Level));
            var ta = new LeafTransform(frame, a);
            var tb = new LeafTransform(frame, b);
            int cells = ta.Cells, lv = Level + ta.Bits;
            int compared = 0;
            for (int s = 0; s <= cells; s++)
            {
                int i = side == 0 ? cells : s, j = side == 0 ? s : cells;
                long gi = ((long)a.X << ta.Bits) + i, gj = ((long)a.Y << ta.Bits) + j;
                var at = b.Face == a.Face ? (gi, gj) : CubeSphere.LatticeOn(a.Face, lv, gi, gj, b.Face)!.Value;
                int bi = (int)(at.Item1 - ((long)b.X << tb.Bits)), bj = (int)(at.Item2 - ((long)b.Y << tb.Bits));
                Assert.Equal(ta.LatticeToPlane(i, j), tb.LatticeToPlane(bi, bj));
                Assert.Equal(ta.UndulationAt(i, j), tb.UndulationAt(bi, bj));
                double hEll = 1234.5 + s;
                Assert.Equal(ta.LatticeToEcef(i, j, hEll), tb.LatticeToEcef(bi, bj, hEll));
                compared++;
            }
            // fractional points along a same-face edge are the same bits too
            if (b.Face == a.Face)
                for (int s = 0; s < 50; s++)
                {
                    double f = s * 10.37;
                    var (i, j) = side == 0 ? ((double)cells, f) : (f, (double)cells);
                    var (bi, bj) = side == 0 ? (0.0, f) : (f, 0.0);
                    Assert.Equal(ta.LatticeToPlane(i, j), tb.LatticeToPlane(bi, bj));
                    Assert.Equal(ta.LatticeToEcef(i, j, 800), tb.LatticeToEcef(bi, bj, 800));
                }
            _out.WriteLine($"{a} | {b}: {compared} edge points the same bits");
        }
    }

    [Fact]
    public void Headings_turn_through_the_jacobian_as_the_chain_turns_them()
    {
        var rng = new Random(3);
        foreach (var (name, leaf, cell) in Cases)
        {
            var frame = FrameOf(cell);
            var t = new LeafTransform(frame, leaf);
            double worst = 0, worstBack = 0, turn = 0;
            for (int k = 0; k < 100; k++)
            {
                double i = rng.NextDouble() * t.Cells, j = rng.NextDouble() * t.Cells, heading = rng.NextDouble() * 360, hSea = rng.NextDouble() * 3000;
                var (x, y) = t.LatticeToPlane(i, j);
                // the chain: a metre either way along the heading, both ends through the rigorous chain
                double r = heading * Math.PI / 180, sx = Math.Sin(r), sy = Math.Cos(r);
                var (ax, _, az) = t.Anchor.ToLocal(frame.ToEcef(x - sx, y - sy, hSea));
                var (bx, _, bz) = t.Anchor.ToLocal(frame.ToEcef(x + sx, y + sy, hSea));
                double chain = Math.Atan2(bx - ax, -(bz - az)) * 180 / Math.PI;
                double fast = t.HeadingToLocal(x, y, heading, hSea);
                double d = Math.Abs(((fast - chain) % 360 + 540) % 360 - 180);
                worst = Math.Max(worst, d);
                double back = t.HeadingToPlane(x, y, fast, hSea);
                worstBack = Math.Max(worstBack, Math.Abs(((back - heading) % 360 + 540) % 360 - 180));
                turn = Math.Max(turn, Math.Abs(((fast - heading) % 360 + 540) % 360 - 180));
            }
            _out.WriteLine($"{name}: heading off the chain {worst * 3600:F4}″, back {worstBack * 3600:F6}″, the turn plane → leaf up to {turn:F4}°");
            Assert.True(worst < 1e-4, $"{name}: heading {worst * 3600:F3}″ off the chain");
            Assert.True(worstBack < 1e-7, $"{name}: heading round trip {worstBack * 3600:F5}″ off");
        }
    }

    [Fact]
    public void The_cost_of_the_rigorous_chain_against_the_transform()
    {
        var leaf = CubeKey.FromLatLon(46.2044, 6.1432, Level);
        var frame = FrameOf(leaf.AncestorAt(WorkFrame.Level));
        var sw = Stopwatch.StartNew();
        LeafTransform t = null!;
        const int leaves = 20;
        for (int k = 0; k < leaves; k++) t = new LeafTransform(frame, leaf);
        double perLeaf = sw.Elapsed.TotalMilliseconds / leaves;

        const int points = 200_000;
        var rng = new Random(1);
        var xs = new (double X, double Y)[points];
        for (int k = 0; k < points; k++) xs[k] = t.LatticeToPlane(1 + rng.NextDouble() * 510, 1 + rng.NextDouble() * 510);
        double sink = 0;
        sw.Restart();
        for (int k = 0; k < points / 10; k++)
        {
            var e = frame.ToEcef(xs[k].X, xs[k].Y, 500);
            sink += t.Anchor.ToLocal(e).X;
        }
        double chain = sw.Elapsed.TotalMilliseconds * 1e3 / (points / 10);
        sw.Restart();
        for (int k = 0; k < points; k++) sink += t.PlaneToLocal(xs[k].X, xs[k].Y, 500).X;
        double fast = sw.Elapsed.TotalMilliseconds * 1e3 / points;
        sw.Restart();
        for (int k = 0; k < points; k++) sink += t.LatticeToPlane(1 + (k % 510), 1 + (k / 510 % 510)).X;
        double lattice = sw.Elapsed.TotalMilliseconds * 1e3 / points;
        sw.Restart();
        for (int k = 0; k < points / 10; k++) sink += SwissSeaLevel.Instance.Undulation(46.2 + k * 1e-7, 6.1);
        double undulation = sw.Elapsed.TotalMilliseconds * 1e3 / (points / 10);
        _out.WriteLine($"transform per leaf {perLeaf:F2} ms; per point: rigorous chain plane->local {chain:F3} µs (undulation alone {undulation:F3} µs), "
            + $"transform plane->local {fast:F3} µs, lattice->plane {lattice:F3} µs; a leaf's 513² vertices rigorous {chain * 263169 / 1000:F0} ms, "
            + $"by the transform {lattice * 263169 / 1000:F1} ms ({sink:E1})");
        Assert.True(fast < chain, "the transform is slower than the chain");
    }

    /// <summary>
    /// The earth world's recent-leaf rule (#1751, <c>EarthWorld.Locate</c>): a point whose affine lattice guess
    /// (<see cref="LeafTransform.LocalToLatticeGuess"/>) is at least 4 cells inside a leaf, at most 10 km over its anchor,
    /// is in the leaf the cube key names (<see cref="CubeKey.FromEcef"/>), so skipping the key never changes the answer;
    /// and the guess is within a cell of the Newton inverse on the ground.
    /// </summary>
    [Fact]
    public void A_point_well_inside_by_the_lattice_guess_is_in_the_leaf_the_key_names()
    {
        const double margin = 4;
        int accepted = 0;
        double worstGuess = 0;
        foreach (var (name, leaf, cell) in Cases)
        {
            var t = new LeafTransform(FrameOf(cell), leaf);
            double hi = t.Cells - margin;
            // rings 0 to 10 cells in from each edge, and the middle, at heights from below sea level to 10 km
            foreach (double h in new[] { -400.0, 0, 2000, 4800, 9900 })
                for (double d = -2; d <= 10; d += 0.25)
                    for (int s = 0; s <= 64; s++)
                    {
                        double along = s * t.Cells / 64.0;
                        foreach (var (i, j) in new[] { (d, along), (t.Cells - d, along), (along, d), (along, t.Cells - d), (t.Cells / 2.0, along) })
                        {
                            var p = t.LatticeToEcef(i, j, h);
                            var (x, y, z) = t.Anchor.ToLocal(p);
                            var (gi, gj) = t.LocalToLatticeGuess(x, z);
                            if (h is >= 0 and <= 4800)
                            {
                                var (ni, nj, _) = t.LocalToLattice(x, y, z);
                                worstGuess = Math.Max(worstGuess, Math.Max(Math.Abs(gi - ni), Math.Abs(gj - nj)));
                            }
                            if (!(gi >= margin && gi <= hi && gj >= margin && gj <= hi) || Math.Abs(y) > 10_000) continue;
                            accepted++;
                            Assert.True(CubeKey.FromEcef(p, leaf.Level) == leaf,
                                $"{name}: guess ({gi:F3}, {gj:F3}) at {h} m is {CubeKey.FromEcef(p, leaf.Level)}, not {leaf}");
                        }
                    }
        }
        _out.WriteLine($"{accepted} points accepted, the guess within {worstGuess:F3} cells of the inverse on the ground");
        Assert.True(accepted > 10_000);
        Assert.True(worstGuess < 1, $"the guess is {worstGuess:F3} cells off");
    }
}
