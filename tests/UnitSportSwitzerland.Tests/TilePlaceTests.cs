using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using UnitSport.World;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Where the traffic's graphs put a tile's records (#1636, G2 C6): an LV95 tile exactly as before, a leaf by its anchor, and the
/// snap keys that join a road cut at a leaf's edge to its other half in the next leaf.
/// </summary>
public class TilePlaceTests
{
    private static readonly CubeKey Geneva = LeafFrames.LeafAt(46.2075, 6.1490);

    [Fact]
    public void Lv95Tile_IsPlacedAndKeyedAsBefore()
    {
        var frame = new OriginFrame(2500300, 1117900, 372.0, 0);
        var id = TileId.FromLv95(2500956, 1117569);
        var place = TilePlace.Of(frame, id);
        Assert.False(place.OnLeaf);
        Assert.Equal(frame.TileToWorld(id, new Vector3(123.25f, 375.5f, 812.75f)), place.World(123.25f, 375.5f, 812.75f));
        var (e, n) = id.Global(123.25f, 812.75f);
        Assert.Equal(TilePlace.Lv95Key(e, n), place.Key(123.25f, 375.5f, 812.75f));
        // a heading goes in as the graphs always took it (x east, -z north)
        Assert.Equal(new Vector3(0f, 0f, -1f), place.Out(Math.PI / 2), new VectorNear(1e-6f));
    }

    [Fact]
    public void LeafPoint_IsWhereItsAnchorPutsIt()
    {
        var anchor = Geneva.Anchor;
        var frame = OriginFrame.AnchorAt(anchor.ToEcef(200, 0, -150));
        var tile = TilePlace.OnAnchor(Empty(), Geneva);
        var place = TilePlace.Of(frame, tile);
        Assert.True(place.OnLeaf);
        var (x, y, z) = frame.Axes!.FromEcef(anchor.ToEcef(-80, 372, 140));
        var world = place.World(-80f, 372f, 140f);
        Assert.True(world.DistanceTo(new Vector3((float)x, (float)y, (float)z)) < 1e-3f);
        // the same point as a stand-in tile's record (shifted by the offset) lands on the same spot
        var standIn = TilePlace.Of(frame, LeafStandIn.Id(Geneva));
        float o = (float)LeafStandIn.Offset;
        Assert.True(standIn.World(-80f + o, 372f, 140f + o).DistanceTo(world) < 1e-3f);
        Assert.Equal(place.Key(-80f, 372f, 140f), standIn.Key(-80f + o, 372f, 140f + o));
    }

    [Fact]
    public void RoadCutAtALeafEdge_JoinsAcrossIt()
    {
        var next = Geneva with { X = Geneva.X + 1 };
        // a point near the leaves' shared edge, as each leaf's records hold it in its own anchor frame
        var a = Geneva.Anchor;
        var b = next.Anchor;
        var edge = Geneva.Vertex(8, 256, 101, 375);
        var (ax, ay, az) = a.ToLocal(edge);
        var (bx, by, bz) = b.ToLocal(edge);
        var frame = OriginFrame.AnchorAt(edge);
        var pa = TilePlace.Of(frame, TilePlace.OnAnchor(Empty(), Geneva));
        var pb = TilePlace.Of(frame, TilePlace.OnAnchor(Empty(), next));
        Assert.True(pa.World((float)ax, (float)ay, (float)az).DistanceTo(pb.World((float)bx, (float)by, (float)bz)) < 2e-3f);
        // the snap cell: the same, whichever leaf (a few cm apart still meet in the 3 x 3 cells round it)
        long ka = pa.Key((float)ax, (float)ay, (float)az), kb = pb.Key((float)bx, (float)by, (float)bz);
        Assert.True(Near(ka, kb, 1), $"{ka:X} vs {kb:X}");
    }

    [Fact]
    public void EarthKey_IgnoresHeight_AndSplitsHalfAMetre()
    {
        var anchor = Geneva.Anchor;
        long k = TilePlace.EarthKey(anchor.ToEcef(10, 0, 10));
        Assert.Equal(k, TilePlace.EarthKey(anchor.ToEcef(10, 0.02, 10) + Up(anchor, 300)));
        // half a metre east or north: the next cell over, laid out as the LV95 key (high word south, low word east)
        long east = TilePlace.EarthKey(anchor.ToEcef(10.5, 0, 10));
        long north = TilePlace.EarthKey(anchor.ToEcef(10, 0, 9.5));
        Assert.Equal(1, Low(east) - Low(k));
        Assert.Equal(High(k), High(east));
        Assert.Equal(-1, High(north) - High(k));
        Assert.True(Math.Abs(Low(north) - Low(k)) <= 1);
        // 3 m apart: never the same neighbourhood
        Assert.False(Near(k, TilePlace.EarthKey(anchor.ToEcef(13, 0, 10)), 1));
    }

    [Fact]
    public void LeafHeading_TurnsWithTheAnchor()
    {
        var anchor = Geneva.Anchor;
        var frame = OriginFrame.AnchorAt(anchor.ToEcef(0, 0, 0));
        var place = TilePlace.Of(frame, TilePlace.OnAnchor(Empty(), Geneva));
        // the frame is the anchor's own ENU here: the leaf's east is the world's east
        Assert.Equal(new Vector3(1f, 0f, 0f), place.Out(0), new VectorNear(1e-4f));
        // 3 km east, the anchor and a frame there disagree by the meridians' convergence: the heading turns with it
        var far = OriginFrame.AnchorAt(anchor.ToEcef(3000, 0, 0));
        var turned = TilePlace.Of(far, TilePlace.OnAnchor(Empty(), Geneva)).Out(0);
        Assert.InRange(Math.Abs(turned.Z), 1e-4f, 0.05f);
        Assert.Equal(1f, turned.Length(), 4);
    }

    private static RoadTile Empty() => new() { Id = default, Segments = [] };

    private static EcefPos Up(EnuFrame f, double h) => new(f.Up.X * h, f.Up.Y * h, f.Up.Z * h);

    private static int High(long key) => (int)(key >> 32);

    private static int Low(long key) => (int)(uint)key;

    private static bool Near(long a, long b, int cells) => Math.Abs(High(a) - High(b)) <= cells && Math.Abs(Low(a) - Low(b)) <= cells;

    private sealed class VectorNear(float eps) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => x.DistanceTo(y) <= eps;
        public int GetHashCode(Vector3 v) => 0;
    }
}
