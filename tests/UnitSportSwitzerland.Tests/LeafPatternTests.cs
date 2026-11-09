using Godot;
using UnitSport.Terrain.Earth;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Patterns from tile coordinates (#1512, G2 C3): a leaf's baked pattern points are its place on the Earth, so two
/// leaves agree where they meet, a re-anchor (which changes no leaf mesh) moves no pattern, and face edges blend.
/// </summary>
public class LeafPatternTests
{
    private static readonly CubeKey Bern = CubeKey.FromLatLon(46.95, 7.44, LeafFrames.LeafLevel);

    /// <summary>A point's stand-in coordinates in a leaf (<see cref="LeafStandIn"/>: the anchor frame, 500 m in).</summary>
    private static Vector3 Local(CubeKey leaf, EcefPos p)
    {
        var (x, y, z) = leaf.Anchor.ToLocal(p);
        return new Vector3((float)(x + LeafStandIn.Offset), (float)y, (float)(z + LeafStandIn.Offset));
    }

    private static double PeriodDiff(double a, double b)
    {
        double d = PatternSpace.Wrap(a - b);
        return Math.Min(d, PatternSpace.PeriodM - d);
    }

    /// <summary>An angle in a leaf's frame (x towards z, z south) as an ECEF direction.</summary>
    private static EcefPos Direction(CubeKey leaf, double angle)
    {
        var a = leaf.Anchor;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new EcefPos(a.East.X * c - a.North.X * s, a.East.Y * c - a.North.Y * s, a.East.Z * c - a.North.Z * s);
    }

    private static float[] At(LeafPattern p, Vector3 local)
    {
        var v = new float[7];
        p.At(local, v);
        return v;
    }

    [Fact]
    public void BakedPointsAreTheExactOnesToAMillimetre()
    {
        var leaf = Bern;
        var pattern = LeafPattern.For(leaf);
        Assert.True(pattern.Pair.Single);
        var rng = new Random(7);
        for (int k = 0; k < 200; k++)
        {
            double x = rng.NextDouble() * 1000 - 500, z = rng.NextDouble() * 1000 - 500, h = rng.NextDouble() * 3000;
            var ecef = leaf.Anchor.ToEcef(x, h, z);
            var v = At(pattern, Local(leaf, ecef));
            var (ex, ez) = PatternSpace.OnFace(pattern.Pair.A, PatternSpace.NormalOf(ecef));
            Assert.True(PeriodDiff(v[0], ex) < 1e-3, $"x off by {PeriodDiff(v[0], ex)} m at {x:F0},{h:F0},{z:F0}");
            Assert.True(PeriodDiff(v[1], ez) < 1e-3, $"z off by {PeriodDiff(v[1], ez)} m");
            Assert.Equal(PatternSpace.FarInside, v[4], 0);
            // the floats stay near [0, 9.6 km): a millimetre's precision
            Assert.InRange(v[0], -1500f, 11_100f);
            Assert.InRange(v[1], -1500f, 11_100f);
        }
    }

    [Fact]
    public void NeighbouringLeavesAgreeWhereTheyMeet()
    {
        var a = Bern;
        foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1) })
        {
            var b = a.Neighbour(dx, dy)!.Value;
            var pa = LeafPattern.For(a);
            var pb = LeafPattern.For(b);
            // points on the line between the two anchors, at a few heights: both leaves draw near it
            for (int k = 0; k <= 10; k++)
            {
                double t = 0.3 + 0.04 * k;
                var o = a.Anchor.Origin;
                var q = b.Anchor.Origin;
                var p = new EcefPos(o.X + (q.X - o.X) * t, o.Y + (q.Y - o.Y) * t, o.Z + (q.Z - o.Z) * t);
                foreach (double h in new[] { 0.0, 450.0, 2500.0 })
                {
                    var n = PatternSpace.NormalOf(p);
                    var at = new EcefPos(p.X + n.X * h, p.Y + n.Y * h, p.Z + n.Z * h);
                    var va = At(pa, Local(a, at));
                    var vb = At(pb, Local(b, at));
                    Assert.True(PeriodDiff(va[0], vb[0]) < 2e-3 && PeriodDiff(va[1], vb[1]) < 2e-3,
                        $"leaves {a} and {b} differ by ({PeriodDiff(va[0], vb[0]):F4}, {PeriodDiff(va[1], vb[1]):F4}) m");
                    // the pattern X axis, each leaf's angle turned out of its own frame, is one direction on the Earth
                    var da = Direction(a, va[5]);
                    var db = Direction(b, vb[5]);
                    // (within the two anchors' tilt: each angle lies in its own leaf's horizontal, ~1e-4 rad apart)
                    double off = Math.Abs(da.X - db.X) + Math.Abs(da.Y - db.Y) + Math.Abs(da.Z - db.Z);
                    Assert.True(off < 3e-4, $"the pattern axes differ by {off:E2}");
                }
            }
        }
    }

    [Fact]
    public void TheAngleIsTheFaceUDirectionInTheLeafFrame()
    {
        var leaf = Bern;
        var v = At(LeafPattern.For(leaf), new Vector3(500, 0, 500));
        // a step of 10 m along the pattern X axis, turned into the frame, lands where u grew by 10 m
        var (dx, dz) = PatternSpace.Turn(10, 0, v[5]);
        var start = leaf.Anchor.ToEcef(0, 0, 0);
        var moved = leaf.Anchor.ToEcef(dx, 0, dz);
        var (x0, z0) = PatternSpace.OnFace(0, PatternSpace.NormalOf(start));
        var (x1, z1) = PatternSpace.OnFace(0, PatternSpace.NormalOf(moved));
        // face metres are arc at the mean radius: within a few tenths of a percent of the ground's metres here
        Assert.True(Math.Abs(x1 - x0 - 10) < 0.05, $"along X: {x1 - x0:F3} m");
        Assert.True(Math.Abs(z1 - z0) < 0.03, $"across: {z1 - z0:F3} m");
        // and pattern Z (X turned +90°) is the other axis, the same way round (no mirror)
        (dx, dz) = PatternSpace.Turn(0, 10, v[5]);
        var (x2, z2) = PatternSpace.OnFace(0, PatternSpace.NormalOf(leaf.Anchor.ToEcef(dx, 0, dz)));
        Assert.True(Math.Abs(z2 - z0 - 10) < 0.05 && Math.Abs(x2 - x0) < 0.03, $"along Z: ({x2 - x0:F3}, {z2 - z0:F3}) m");
    }

    [Fact]
    public void AFaceEdgeBlendsBothFacesTheSameFromEitherSide()
    {
        int side = 1 << LeafFrames.LeafLevel;
        var a = new CubeKey(0, LeafFrames.LeafLevel, side - 1, side / 3);
        var b = a.Neighbour(1, 0)!.Value;
        Assert.NotEqual(a.Face, b.Face);
        var pa = LeafPattern.For(a);
        var pb = LeafPattern.For(b);
        Assert.False(pa.Pair.Single);
        Assert.Equal(pa.Pair, pb.Pair);

        // across the edge, between the two leaves' anchors: B's weight rises from 0 to 1, the same in both leaves
        var o = a.Anchor.Origin;
        var q = b.Anchor.Origin;
        float last = -1f;
        for (int k = 0; k <= 40; k++)
        {
            double t = k / 40.0;
            var p = new EcefPos(o.X + (q.X - o.X) * t, o.Y + (q.Y - o.Y) * t, o.Z + (q.Z - o.Z) * t);
            var va = At(pa, Local(a, p));
            var vb = At(pb, Local(b, p));
            float wa = PatternSpace.WeightAt(va[4]), wb = PatternSpace.WeightAt(vb[4]);
            Assert.True(Math.Abs(wa - wb) < 2e-3, $"weights {wa:F4} and {wb:F4} at {t:F3}");
            Assert.True(wa >= last - 1e-4, "B's weight only rises towards B");
            last = wa;
            Assert.True(PeriodDiff(va[0], vb[0]) < 2e-3 && PeriodDiff(va[2], vb[2]) < 2e-3, $"points at {t:F3}");
        }
        Assert.True(PatternSpace.WeightAt(At(pa, new Vector3(500, 0, 500))[4]) < 0.01f, "deep in A: A's pattern");
        Assert.True(PatternSpace.WeightAt(At(pb, new Vector3(500, 0, 500))[4]) > 0.99f, "deep in B: B's pattern");
        // on the edge itself: half and half
        var edge = CubeSphere.Normal(0, 1, CubeSphere.Lattice(LeafFrames.LeafLevel + 1, 2L * a.Y + 1));
        Assert.Equal(0.5f, PatternSpace.WeightB(pa.Pair, edge), 3);
    }

    [Fact]
    public void BakeFillsBothChannels()
    {
        var p = LeafPattern.For(Bern);
        var (c1, c2) = p.Bake([new Vector3(0, 0, 0), new Vector3(1000, 600, 1000)]);
        Assert.Equal(8, c1.Length);
        Assert.Equal(8, c2.Length);
        Assert.Equal(1f, c2[0]);
        Assert.Equal(1f, c2[4]);
        Assert.Equal((float)PatternSpace.FarInside, c2[3]);
        Assert.Equal(c1[0], c1[2]);   // B = A off a face edge
    }
}
