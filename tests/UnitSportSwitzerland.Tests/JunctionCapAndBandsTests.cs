using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Rewrite;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The Geneva playtest's junction geometry (#1770, #1771): a transition keeps the bands its street has (a sidewalk the
/// junction planner's normal section lacked was taken off every piece it covered), and a junction cap is triangulated across
/// both its kerbs instead of fanning slivers out of one vertex.
/// </summary>
public class JunctionCapAndBandsTests
{
    private static RoadTransitionStrip Car(int id, sbyte travel) => new() { Kind = StripKind.CarLane, Id = id, Travel = travel, Widths = [3.5f, 3.5f] };

    [Fact]
    public void AddOwnBands_PutsTheStreetsMissingBandsInPlace()
    {
        // a transition with a sidewalk on the left only; the street has a path and a sidewalk on the right, and on the left a
        // verge it does not have yet (a track side: verge, path, then the sidewalk already there)
        var strips = new List<RoadTransitionStrip>
        {
            new() { Kind = StripKind.Sidewalk, Id = 1, Widths = [2f, 2f] },
            Car(2, -1), Car(3, 1),
        };
        var left = new RoadSide(SidewalkDm: 20, Bike: BikeKind.Track, BikeDm: 20, KerbCm: 12, VergeDm: 10);
        var right = new RoadSide(SidewalkDm: 30, Bike: BikeKind.Track, BikeDm: 20, KerbCm: 12);
        var added = new List<RoadTransitionStrip>();
        int onLeft = TileRewriter.AddOwnBands(strips, left, right, 2, added);

        Assert.Equal(new[] { StripKind.Sidewalk, StripKind.Path, StripKind.Verge, StripKind.CarLane, StripKind.CarLane, StripKind.Path, StripKind.Sidewalk },
            strips.Select(x => x.Kind).ToArray());
        Assert.Equal(2, onLeft);   // the left's verge and path, before the carriageway: the anchor moves by two
        Assert.Equal(4, added.Count);
        Assert.Equal(3f, strips[^1].Widths[0]);
        Assert.Equal(1f, strips[2].Widths[1]);
        Assert.Equal(strips.Count, strips.Select(x => x.Id).Distinct().Count());
        // nothing more to add the second time
        Assert.Equal(0, TileRewriter.AddOwnBands(strips, left, right, 2));
        Assert.Equal(7, strips.Count);
    }

    [Fact]
    public void AddOwnBands_BareStreetAndFlushPathAddNothing()
    {
        var strips = new List<RoadTransitionStrip> { Car(1, -1), Car(2, 1) };
        // a path's bands only beside a kerb (Section.Bands); a side with nothing adds nothing
        var flush = new RoadSide(Bike: BikeKind.Track, BikeDm: 20, KerbCm: 0);
        Assert.Equal(0, TileRewriter.AddOwnBands(strips, flush, default, 2));
        Assert.Equal(2, strips.Count);
    }

    /// <summary>A cap like 2501282,1117720: a dense concave kerb arc on one side, a straight kerb of two points on the other.</summary>
    private static List<Vec2> ArcCap()
    {
        // a 16 m square less a quarter disc of 8 m round its corner (0, 16), counter-clockwise: the kerb arc from (8, 16) to
        // (0, 8) bulges into the cap, its points 0.31 m apart
        var ring = new List<Vec2> { new(0, 0), new(16, 0), new(16, 16) };
        for (int k = 0; k <= 40; k++)
        {
            double a = -k / 40.0 * Math.PI / 2;
            ring.Add(new Vec2(8 * Math.Cos(a), 16 + 8 * Math.Sin(a)));
        }
        return ring;
    }

    private static double MinAngle(IReadOnlyList<Vec2> p, List<int> tris)
    {
        double worst = Math.PI;
        for (int t = 0; t + 2 < tris.Count; t += 3)
            for (int e = 0; e < 3; e++)
            {
                var a = p[tris[t + e]];
                var b = p[tris[t + (e + 1) % 3]];
                var c = p[tris[t + (e + 2) % 3]];
                var u = b - a; var v = c - a;
                worst = Math.Min(worst, Math.Acos(Math.Clamp(u.Dot(v) / (u.Length * v.Length), -1, 1)));
            }
        return worst * 180 / Math.PI;
    }

    private static double Area(IReadOnlyList<Vec2> p, List<int> tris)
    {
        double sum = 0;
        for (int t = 0; t + 2 < tris.Count; t += 3) sum += Math.Abs((p[tris[t + 1]] - p[tris[t]]).Cross(p[tris[t + 2]] - p[tris[t]])) * 0.5;
        return sum;
    }

    [Fact]
    public void Delaunay_KeepsTheRingsAreaAndNeverWorsensTheAngles()
    {
        var ring = ArcCap();
        var ears = EarClip.Triangulate(ring);
        var flipped = EarClip.TriangulateDelaunay(ring);
        double area = Math.Abs(EarClip.SignedArea(ring));
        Assert.Equal(ears.Count, flipped.Count);
        Assert.Equal(area, Area(ring, flipped), 6);
        Assert.True(MinAngle(ring, flipped) >= MinAngle(ring, ears) - 1e-9);
        // every triangle counter-clockwise and not flat
        for (int t = 0; t + 2 < flipped.Count; t += 3)
            Assert.True((ring[flipped[t + 1]] - ring[flipped[t]]).Cross(ring[flipped[t + 2]] - ring[flipped[t]]) > 1e-9);
    }

    [Fact]
    public void Cap_PointsAlongTheLongKerbs_NoFanOutOfOneVertex()
    {
        var ring = ArcCap();
        var heights = ring.Select(p => 0.01 * p.X).ToList();
        var keep = Enumerable.Range(0, ring.Count).ToList();
        var (dense, ups) = TileRewriter.KeptEvery(keep, ring, heights, 2.0);
        // the two 16 m kerbs get 7 points each, the two 8 m ones 3 each; the arc's own points stay
        for (int i = 0; i < dense.Count; i++)
            Assert.True(dense[i].DistanceTo(dense[(i + 1) % dense.Count]) <= 2.0 + 1e-9);
        Assert.Equal(ring.Count + 2 * 7 + 2 * 3, dense.Count);
        // heights along the line, as the ring's
        for (int i = 0; i < dense.Count; i++) Assert.Equal(0.01 * dense[i].X, ups[i], 9);

        var before = EarClip.Triangulate(ring);
        var after = EarClip.TriangulateDelaunay(dense);
        int MostAtOne(List<int> tris) => tris.GroupBy(i => i).Max(g => g.Count());
        // ear clipping fanned the arc out of the straight kerb's corners; across both kerbs no vertex holds a fan
        Assert.True(MostAtOne(before) >= 20, $"fan {MostAtOne(before)}");
        Assert.True(MostAtOne(after) <= 10, $"fan {MostAtOne(after)}");
        Assert.True(MinAngle(dense, after) > MinAngle(ring, before));
        Assert.Equal(Math.Abs(EarClip.SignedArea(dense)), Area(dense, after), 6);
    }

    [Fact]
    public void KeptEvery_ShortEdgesUnchanged()
    {
        var ring = new List<Vec2> { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        var (dense, ups) = TileRewriter.KeptEvery([0, 1, 2, 3], ring, [0, 0, 0, 0], 2.0);
        Assert.Equal(ring, dense);
        Assert.All(ups, h => Assert.Equal(0, h));
    }
}
