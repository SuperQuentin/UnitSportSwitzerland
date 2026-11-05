using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The cover on work frames (#1507, G2 B3 PR 5a): <see cref="FrameCoverSpace"/> stamps exactly the leaf vertices a ring
/// holds (against a point-in-polygon test at every vertex's own plane point), across the leaves it covers, and keeps
/// Water as <see cref="CoverExtractor.Stamped"/> says.
/// </summary>
public class FrameCoverTests
{
    private const int Level = 14;

    private static readonly CubeKey Geneva = CubeKey.FromLatLon(46.2044, 6.1432, Level);

    private static HashSet<CubeKey> Block(CubeKey centre, int r)
    {
        var set = new HashSet<CubeKey> { centre };
        for (int step = 0; step < r; step++)
            foreach (var k in set.ToList())
                foreach (var n in k.Neighbours()) set.Add(n);
        return set;
    }

    private static bool Inside(List<(double X, double Y)> poly, double x, double y)
    {
        bool inside = false;
        for (int a = 0, b = poly.Count - 1; a < poly.Count; b = a++)
            if ((poly[a].Y > y) != (poly[b].Y > y)
                && x < poly[b].X + (y - poly[b].Y) / (poly[a].Y - poly[b].Y) * (poly[a].X - poly[b].X))
                inside = !inside;
        return inside;
    }

    /// <summary>A concave ring over the corner of four leaves, as an LV95 ring at 400 m (the plane points known).</summary>
    private static (GeoPackageReader.Ring Ring, List<(double X, double Y)> Plane) Ring(WorkFrame frame, double cx, double cy)
    {
        var plane = new List<(double X, double Y)>
        {
            (cx - 300, cy - 200), (cx + 250, cy - 260), (cx + 60, cy - 20), (cx + 310, cy + 280), (cx - 120, cy + 90), (cx - 260, cy + 240),
        };
        var xyz = new List<double>();
        foreach (var (x, y) in plane.Append(plane[0]))
        {
            var (e, n, _) = SwissGeodesy.FromEcef(frame.ToEcef(x, y, 400));
            xyz.AddRange(new[] { e, n, 400.0 });
        }
        // the plane points the ring's LV95 corners come back to
        var back = new List<(double X, double Y)>();
        for (int k = 0; k < plane.Count; k++)
        {
            var (x, y, _) = frame.FromSource(Lv95Source.Instance, xyz[k * 3], xyz[k * 3 + 1], 400);
            back.Add((x, y));
        }
        return (new GeoPackageReader.Ring(xyz.ToArray()), back);
    }

    [Fact]
    public void A_ring_stamps_the_leaf_vertices_inside_it_and_no_other()
    {
        var driver = new FrameDriver(Block(Geneva, 1), 0, SwissSeaLevel.Instance);
        var plan = driver.Frames.Single(f => f.Core.Contains(Geneva));
        var space = new FrameCoverSpace(plan);
        var t = plan.Transform(Geneva);
        var corner = t.LatticeToPlane(t.Cells, t.Cells);
        var (ring, poly) = Ring(plan.Frame, corner.X, corner.Y);
        space.Stamp(ring, CoverClass.Park);

        int inside = 0, leaves = 0;
        foreach (var leaf in plan.Core)
        {
            var lt = plan.Transform(leaf);
            var cells = space.Cover[leaf];
            int side = lt.Cells + 1, here = 0;
            for (int j = 0; j <= lt.Cells; j++)
                for (int i = 0; i <= lt.Cells; i++)
                {
                    var (x, y) = lt.LatticeToPlane(i, j);
                    bool want = Inside(poly, x, y);
                    // a vertex within a millimetre of an edge may go either way
                    if (want != (cells[j * side + i] == (byte)CoverClass.Park))
                        Assert.True(poly.Select((p, k) => Geometry2.PointSegment(x, y, p.X, p.Y, poly[(k + 1) % poly.Count].X, poly[(k + 1) % poly.Count].Y)).Min() < 1e-3,
                            $"{leaf} ({i}, {j}) {(want ? "missed" : "stamped outside")}");
                    if (want) here++;
                }
            inside += here;
            if (here > 0) leaves++;
        }
        Assert.Equal(4, leaves);
        Assert.True(inside > 50_000, $"{inside} vertices");
    }

    [Fact]
    public void Water_stays_when_a_later_layer_covers_it()
    {
        var driver = new FrameDriver([Geneva], 0, SwissSeaLevel.Instance);
        var plan = driver.Frames[0];
        var space = new FrameCoverSpace(plan);
        var t = plan.Transform(Geneva);
        var mid = t.LatticeToPlane(t.Cells / 2.0, t.Cells / 2.0);
        var (ring, _) = Ring(plan.Frame, mid.X, mid.Y);
        space.Stamp(ring, CoverClass.Water);
        int water = space.Cover[Geneva].Count(c => c == (byte)CoverClass.Water);
        space.Stamp(ring, CoverClass.Industrial);
        Assert.True(water > 10_000);
        Assert.Equal(water, space.Cover[Geneva].Count(c => c == (byte)CoverClass.Water));
        Assert.Equal(0, space.Cover[Geneva].Count(c => c == (byte)CoverClass.Industrial));
    }

    [Fact]
    public void The_clip_keeps_what_lies_in_the_box()
    {
        var square = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        var clipped = FrameCoverSpace.Clip(square, (5, -5, 20, 5));
        Assert.Equal(clipped[0], clipped[^1]);
        double area = 0;
        for (int k = 0; k + 1 < clipped.Count; k++) area += clipped[k].X * clipped[k + 1].Y - clipped[k + 1].X * clipped[k].Y;
        Assert.Equal(25, Math.Abs(area) / 2, 9);
        Assert.Empty(FrameCoverSpace.Clip(square, (20, 20, 30, 30)));
    }
}

/// <summary>Trees on leaves (#1507, G2 B3 PR 5b): a scattered tree is a function of its global lattice vertex, at the LV95 density per area.</summary>
public class LeafTreeSeedTests
{
    [Fact]
    public void A_vertex_seed_is_its_own_whatever_asks_and_draws_evenly()
    {
        Assert.Equal(TreeSeed.Of(0, 4_000_000, 4_100_000), TreeSeed.Of(0, 4_000_000, 4_100_000));
        Assert.NotEqual(TreeSeed.Of(0, 4_000_000, 4_100_000), TreeSeed.Of(1, 4_000_000, 4_100_000));
        Assert.NotEqual(TreeSeed.Of(0, 4_000_001, 4_100_000), TreeSeed.Of(0, 4_000_000, 4_100_001));
        double sum = 0;
        int n = 200_000, under = 0;
        for (int k = 0; k < n; k++)
        {
            double u = TreeSeed.Unit(TreeSeed.Of(0, 4_000_000 + k % 500, 4_100_000 + k / 500));
            Assert.InRange(u, 0, 1);
            sum += u;
            if (u < 0.01) under++;
        }
        Assert.Equal(0.5, sum / n, 2);
        Assert.InRange(under, n * 0.009, n * 0.011);
    }

    [Fact]
    public void The_scatter_chance_is_the_lv95_density_per_square_metre()
    {
        Assert.Equal(CoverExtractor.ScatterChance(CoverClass.Forest, 1) * 1.42f, CoverExtractor.ScatterChance(CoverClass.Forest, 1.42), 6);
        Assert.Equal(CoverFormat.TreeDensity(CoverClass.Forest) * 0.0004f, CoverExtractor.ScatterChance(CoverClass.Forest, 1), 6);
    }
}
