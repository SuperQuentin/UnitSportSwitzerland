using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The road stage on work frames (#1507, G2 B3 PR 2): <see cref="FrameRoadSpace"/>'s tile loop (a line cut where its
/// leaf changes, on one face and across a face edge), its hole quads near a point, and the raw kinds' codecs
/// (<see cref="LeafRoadKeys"/>, <see cref="LeafParking"/>). The LV95 instance is held byte-identical to main by a
/// run, not here (PR #1537).
/// </summary>
public class FrameRoadsTests
{
    private const int Level = 14;

    private static readonly CubeKey Geneva = CubeKey.FromLatLon(46.2044, 6.1432, Level);

    private sealed class NoGround : IGround
    {
        public double? Sample(double x, double y) => null;
    }

    private static FrameRoadSpace SpaceOver(IEnumerable<CubeKey> leaves, int frameIndex = 0)
    {
        var driver = new FrameDriver(leaves, 0, SwissSeaLevel.Instance);
        return new FrameRoadSpace(driver.Frames[frameIndex], new NoGround());
    }

    private static HashSet<CubeKey> Block(CubeKey centre, int r)
    {
        var set = new HashSet<CubeKey> { centre };
        for (int step = 0; step < r; step++)
            foreach (var k in set.ToList())
                foreach (var n in k.Neighbours()) set.Add(n);
        return set;
    }

    private static double PlanLength(List<(double E, double N, double Z)> pts)
    {
        double len = 0;
        for (int i = 1; i < pts.Count; i++) len += Math.Sqrt((pts[i].E - pts[i - 1].E) * (pts[i].E - pts[i - 1].E) + (pts[i].N - pts[i - 1].N) * (pts[i].N - pts[i - 1].N));
        return len;
    }

    /// <summary>Every piece lies on its own leaf, consecutive pieces share their cut, and the cuts lose no length.</summary>
    private static void AssertCut(FrameRoadSpace space, GeoPackageReader.Polyline line, List<RoadPiece<CubeKey>> pieces)
    {
        Assert.True(pieces[0].AtLineStart);
        Assert.True(pieces[^1].AtLineEnd);
        for (int k = 0; k < pieces.Count; k++)
        {
            var p = pieces[k].Points;
            for (int i = 1; i < p.Count; i++)
                Assert.Equal(pieces[k].Tile, space.TileAt(0.5 * (p[i - 1].E + p[i].E), 0.5 * (p[i - 1].N + p[i].N)));
            if (k > 0)
            {
                Assert.Equal(pieces[k - 1].Points[^1], p[0]);
                Assert.NotEqual(pieces[k - 1].Tile, pieces[k].Tile);
            }
        }
        var whole = new List<(double E, double N, double Z)>();
        for (int i = 0; i < line.Count; i++) whole.Add((line.E[i], line.N[i], line.Z[i]));
        Assert.Equal(PlanLength(whole), pieces.Sum(p => PlanLength(p.Points)), 6);
        Assert.Equal((line.E[0], line.N[0], line.Z[0]), pieces[0].Points[0]);
        Assert.Equal((line.E[^1], line.N[^1], line.Z[^1]), pieces[^1].Points[^1]);
    }

    [Fact]
    public void A_line_is_cut_where_its_leaf_changes_and_the_pieces_meet()
    {
        var space = SpaceOver(Block(Geneva, 4));
        var c = space.Frame.Plane.ToPlane(Geneva.Centre.LatDeg, Geneva.Centre.LonDeg);
        // a diagonal over ~2.5 leaves, a bend, and a vertex straight on through a leaf corner's neighbourhood
        var line = new GeoPackageReader.Polyline(
            new[] { c.X - 900, c.X + 300, c.X + 1200 },
            new[] { c.Y - 700, c.Y + 250, c.Y + 260 },
            new[] { 400.0, 420, 410 });
        var pieces = space.Split(line);
        Assert.True(pieces.Count >= 5, $"{pieces.Count} pieces");
        AssertCut(space, line, pieces);
        // the cut is on the edge: the leaf either side of it within a micrometre
        foreach (var piece in pieces.Skip(1))
        {
            var (x, y, _) = piece.Points[0];
            var leaf = space.TileAt(x, y);
            Assert.True(leaf == piece.Tile || piece.Tile.Neighbours().Contains(leaf));
        }
    }

    [Fact]
    public void A_line_across_a_face_edge_is_cut_there_too()
    {
        // the last leaf of face 0 along u and its neighbour on the next face
        var edge = new CubeKey(0, Level, (1 << Level) - 1, 5000);
        var other = edge.Neighbour(1, 0)!.Value;
        Assert.NotEqual(edge.Face, other.Face);
        var space = SpaceOver(Block(edge, 2));
        var a = space.Frame.Plane.ToPlane(edge.Centre.LatDeg, edge.Centre.LonDeg);
        var b = space.Frame.Plane.ToPlane(other.Centre.LatDeg, other.Centre.LonDeg);
        var line = new GeoPackageReader.Polyline(new[] { a.X, b.X }, new[] { a.Y, b.Y }, new[] { 0.0, 0 });
        var pieces = space.Split(line);
        Assert.Equal(new[] { edge, other }, pieces.Select(p => p.Tile));
        AssertCut(space, line, pieces);
    }

    [Fact]
    public void The_quads_near_a_point_are_every_quad_of_the_written_leaves_within_reach()
    {
        var core = Block(Geneva, 1);
        var space = SpaceOver(core);
        // a point near the corner of four leaves
        var t = new LeafTransform(space.Frame, Geneva);
        var (x, y) = t.LatticeToPlane(t.Cells - 0.3, t.Cells - 0.6);
        const double reach = 3.3;
        var got = space.QuadsNear(x, y, reach)
            .Where(q => Math.Sqrt((q.X - x) * (q.X - x) + (q.Y - y) * (q.Y - y)) <= reach)
            .Select(q => (q.Tile, q.Quad)).ToHashSet();
        var want = new HashSet<(CubeKey, int)>();
        foreach (var leaf in core)
        {
            var lt = new LeafTransform(space.Frame, leaf);
            for (int j = 0; j < lt.Cells; j++)
                for (int i = 0; i < lt.Cells; i++)
                {
                    var (cx, cy) = lt.LatticeToPlane(i + 0.5, j + 0.5);
                    if (Math.Sqrt((cx - x) * (cx - x) + (cy - y) * (cy - y)) <= reach) want.Add((leaf, LeafHoles.Quad(i, j, lt.Bits)));
                }
        }
        Assert.True(want.Count > 20, $"{want.Count} quads");
        Assert.Equal(4, want.Select(w => w.Item1).Distinct().Count());
        Assert.Equal(want.OrderBy(w => w.Item1.X).ThenBy(w => w.Item1.Y).ThenBy(w => w.Item2), got.OrderBy(w => w.Item1.X).ThenBy(w => w.Item1.Y).ThenBy(w => w.Item2));
    }

    [Fact]
    public void The_raw_keys_and_car_parks_round_trip()
    {
        var keys = new RoadSourceKey?[] { new("{A5EF3AF8-5473-4D3B-8081-93AB99324662}", 2, 1234.567), null, new("é-ü", 0, 0) };
        Assert.Equal(keys, LeafRoadKeys.Decode(LeafRoadKeys.Encode(keys)));
        Assert.Empty(LeafRoadKeys.Decode(LeafRoadKeys.Encode(Array.Empty<RoadSourceKey?>())));

        var lots = new List<LeafParkingLot>
        {
            new(CoverClass.ParkingPublic, "tlm", new[] { 1f, 2f, 3f, 4f, 5f, 6f, -7.25f, 8f, 9.5f }),
            new(CoverClass.ParkingPrivate, "override", new[] { 0f, 0f, 0f, 10f, 0f, 0f, 10f, 0f, 10f, 0f, 0f, 10f }),
        };
        var back = LeafParking.Decode(LeafParking.Encode(lots));
        Assert.Equal(2, back.Count);
        for (int k = 0; k < lots.Count; k++)
        {
            Assert.Equal(lots[k].Cover, back[k].Cover);
            Assert.Equal(lots[k].Source, back[k].Source);
            Assert.Equal(lots[k].Points, back[k].Points);
        }
    }
}
