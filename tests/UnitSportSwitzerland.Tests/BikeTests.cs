using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Bike lanes and paths (#120): lane rules, paint, paths against synthetic houses, the street profile, the codec.</summary>
public class BikeTests
{
    private static readonly TileId Tile = new(2600, 1200);

    /// <summary>A straight road along tile-local z = <paramref name="z"/>, x 100..<paramref name="x1"/>, drawn west to east (left is north).</summary>
    private static RoadSegment Road(float width, RoadSide left = default, RoadSide right = default, double z = 500, double x1 = 300,
        RoadClass cls = RoadClass.Road, sbyte oneWay = 0)
    {
        int n = (int)((x1 - 100) / 4) + 1;
        var pts = new float[n * 3];
        for (int i = 0; i < n; i++) (pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]) = ((float)(100 + (x1 - 100) * i / (n - 1)), 400f, (float)z);
        return new RoadSegment
        {
            Class = cls, Surface = RoadSurface.Paved, Width = width, Points = pts,
            Attributes = new RoadAttributes(OneWay: oneWay, WidthCm: (ushort)(width * 100), Left: left, Right: right),
        };
    }

    private static RoadSide Lane(byte dm) => new(Bike: BikeKind.Lane, BikeDm: dm);

    [Theory]
    [InlineData(10.0, false, false, 18, BikePlanner.Why.Lane)]   // 10 - 3.6 >= 2 x 2.75: lanes and a centre line
    [InlineData(9.0, false, true, 15, BikePlanner.Why.Lane)]
    [InlineData(8.0, false, false, 18, BikePlanner.Why.Kern)]    // core 4.4 m
    [InlineData(6.0, false, true, 13, BikePlanner.Why.Kern)]     // (6 - 3.5) / 2 = 1.25, stored 1.3
    [InlineData(5.0, false, true, 0, BikePlanner.Why.Narrow)]
    [InlineData(4.5, true, true, 15, BikePlanner.Why.Lane)]      // one-way: one 3 m car lane left
    [InlineData(4.0, true, true, 0, BikePlanner.Why.Narrow)]
    public void Lane_width_follows_the_carriageway(double width, bool oneWay, bool urban, int dm, BikePlanner.Why why)
    {
        var (got, gotWhy) = BikePlanner.LaneFor(width, oneWay, urban);
        Assert.Equal(dm, got);
        Assert.Equal(why, gotWhy);
    }

    [Fact]
    public void A_kernfahrbahn_has_yellow_lanes_and_no_centre_line()
    {
        var paint = new List<RoadPaint>();
        PaintEmitter.Emit(Road(6, Lane(13), Lane(13)), 0, paint, startsAtJunction: true, endsAtJunction: true);
        Assert.DoesNotContain(paint, p => p.Type == PaintType.WhiteDashed);
        Assert.DoesNotContain(paint, p => p.Type == PaintType.WhiteSolid);   // no Randlinie beside a bike lane
        var lines = paint.Where(p => p.Type == PaintType.YellowDashed).Select(p => p.Offset).Distinct().OrderBy(o => o).ToList();
        Assert.Equal(new[] { -1.7f, 1.7f }, lines);   // 3 m half width - 1.3 m lane
        var symbols = paint.Where(p => p.Type == PaintType.BikeSymbol).ToList();
        Assert.Equal(2, symbols.Count);   // one per lane, where its riders come in
        Assert.Equal(1, symbols.Count(s => (s.Variant & RoadPaintGeometry.BikeReversed) != 0));   // the left lane's traffic runs against the drawing
    }

    [Fact]
    public void A_wide_road_keeps_its_centre_line_between_the_lanes()
    {
        var paint = new List<RoadPaint>();
        PaintEmitter.Emit(Road(10, Lane(18), Lane(18), cls: RoadClass.Major), 0, paint);
        Assert.Contains(paint, p => p.Type == PaintType.WhiteDashed && Math.Abs(p.Offset) < 1e-3);
        Assert.DoesNotContain(paint, p => p.Type == PaintType.BikeSymbol);   // neither end at a junction
        // the same road without lanes is painted exactly as before #120
        var plain = new List<RoadPaint>();
        PaintEmitter.Emit(Road(10, cls: RoadClass.Major), 0, plain);
        Assert.Equal(new[] { -4.775f, 0f, 4.775f }, plain.Select(p => p.Offset).Distinct().OrderBy(o => o));
    }

    [Fact]
    public void A_road_with_a_track_alongside_gets_no_lane()
    {
        static CrossSectionPlanner.Line Line(RoadSegment s) => new() { Tile = Tile, Segment = s, Write = true };
        var road = Line(Road(6, x1: 600));
        var track = Line(Road(3, z: 480, x1: 600, cls: RoadClass.Track));   // 20 m north, all along
        var alone = Line(Road(6, z: 900, x1: 600));
        List<CrossSectionPlanner.Line> lines = [road, track, alone];
        CrossSectionPlanner.Plan(lines, null, new CrossSectionPlanner.Stats());   // plan, width (6 m class), one-way
        var stats = new BikePlanner.Stats();
        BikePlanner.PlanLines(lines, new UrbanField(new Facades(_ => null)), stats);
        Assert.Equal(BikePlanner.Why.Parallel, road.BikeWhy);
        Assert.False(road.BikeWanted);
        Assert.Equal(BikePlanner.Why.Kern, alone.BikeWhy);
        Assert.Equal(13, alone.BikeLaneDm);   // rural 6 m: (6 - 3.5) / 2
        Assert.Equal(BikePlanner.Why.NotCandidate, track.BikeWhy);
    }

    [Fact]
    public void A_street_keeps_its_layout_through_a_straight_junction()
    {
        var net = new RoadNetwork();
        var a = net.AddLink([new Vec2(0, 0), new Vec2(100, 0)], RoadProfile.Road);
        var b = net.AddLink([new Vec2(100, 0), new Vec2(200, 3)], RoadProfile.Road);
        var c = net.AddLink([new Vec2(100, 0), new Vec2(100, 100)], RoadProfile.Road);
        var node = new RoadNode { Id = 0, Position = new Vec2(100, 0) };
        node.Approaches.Add(new Approach(a.Id, LinkEnd.End, Math.PI));
        node.Approaches.Add(new Approach(b.Id, LinkEnd.Start, Math.Atan2(3, 100)));
        node.Approaches.Add(new Approach(c.Id, LinkEnd.Start, Math.PI / 2));
        net.Nodes.Add(node);
        var keys = new Dictionary<int, string> { [a.Id] = "{b}", [b.Id] = "{a}", [c.Id] = "{c}" };
        var layouts = BikePlanner.StrokeLayouts(net, l => keys[l.Id]);
        Assert.Equal(layouts[a.Id], layouts[b.Id]);
        Assert.All(layouts.Values, v => Assert.InRange(v, 1, 5));
    }

    /// <summary>10 m boxes (walls only): 4 m off both edges of the 6 m road for x 100..300, 10 m back on the north for x 300..500.</summary>
    private static Facades Houses()
    {
        var buildings = new List<Building>();
        void House(double x0, double z0, double x1, double z1)
        {
            var t = new List<float>();
            void Wall(double ax, double az, double bx, double bz)
            {
                t.AddRange([(float)ax, 400, (float)az, (float)bx, 400, (float)bz, (float)bx, 408, (float)bz]);
                t.AddRange([(float)ax, 400, (float)az, (float)bx, 408, (float)bz, (float)ax, 408, (float)az]);
            }
            Wall(x0, z0, x1, z0); Wall(x1, z0, x1, z1); Wall(x1, z1, x0, z1); Wall(x0, z1, x0, z0);
            buildings.Add(new Building { Kind = BuildingKind.House, MinY = 400, MaxY = 408, Triangles = t.ToArray() });
        }
        for (double x = 100; x < 300; x += 16) { House(x, 483, x + 10, 493); House(x, 507, x + 10, 517); }
        for (double x = 300; x < 500; x += 16) House(x, 477, x + 10, 487);
        return new Facades(id => id == Tile ? new BuildingTile { Id = Tile, Buildings = buildings } : null);
    }

    [Fact]
    public void A_street_path_narrows_to_fit_between_the_houses_and_drops_the_lanes()
    {
        var facades = Houses();
        var street = Road(6, Lane(13), Lane(13), x1: 500);
        var obstacles = new StreetPlanner.Obstacles();
        obstacles.Add(street, Tile, 3, 0, street: true);
        var bikes = new BikePlanner.Stats();
        var planner = new StreetPlanner(facades, new UrbanField(facades), obstacles, new StreetPlanner.Stats());
        var pieces = planner.Plan(street, Tile, 0, out bool urban, out bool track, new StreetPlanner.BikeRequest(5, bikes));
        Assert.True(urban);
        Assert.True(track);
        RoadAttributes At(double x) => pieces.First(p => p.Points[0] <= x && p.Points[^3] >= x).Attributes;

        // houses 4 m off: 3.8 m free; layout 5 (5.3 m with a sidewalk) and 4 (4.5 m) do not fit, 3 (3.5 m) does
        var tight = At(200);
        foreach (var side in new[] { tight.Left, tight.Right })
        {
            Assert.Equal(BikeKind.TrackMid, side.Bike);
            Assert.Equal(0, side.VergeDm);
            Assert.Equal(BikePlanner.TrackDm, side.BikeDm);
            Assert.Equal(15, side.SidewalkDm);
        }
        // a 10 m front yard north, open ground south: the whole layout 5
        var wide = At(400);
        foreach (var side in new[] { wide.Left, wide.Right })
        {
            Assert.Equal(BikeKind.TrackMid, side.Bike);
            Assert.Equal(BikePlanner.VergeDm, side.VergeDm);
            Assert.Equal(BikePlanner.BufferDm, side.BufferDm);
        }
        Assert.All(pieces, p => Assert.False(p.Attributes.Left.HasLane || p.Attributes.Right.HasLane));
        Assert.True(bikes.TrackNarrowed > 0);

        // nothing beside the road reaches a wall
        foreach (var p in pieces)
            foreach (bool right in new[] { false, true })
            {
                var side = right ? p.Attributes.Right : p.Attributes.Left;
                double off = 3 + side.OuterDm / 10.0, sign = right ? 1 : -1;
                for (int i = 0; i < p.PointCount; i++)
                    Assert.False(facades.Occupied(Tile.MinE + p.Points[i * 3], Tile.MaxN - (p.Points[i * 3 + 2] + sign * off)));
            }
    }

    [Fact]
    public void Street_profile_puts_a_mid_path_halfway_down_with_sloped_kerbs()
    {
        var side = new RoadSide(SidewalkDm: 15, Bike: BikeKind.TrackMid, BikeDm: 20, KerbCm: 12);
        var p = RoadStreetSection.For(side)!;
        Assert.Equal(new[] { 0f, 0.3f, 2.0f, 2.3f, 3.5f }, p.D.Select(d => MathF.Round(d, 3)));
        Assert.Equal(new[] { 0f, 0.06f, 0.06f, 0.12f, 0.12f }, p.H.Select(h => MathF.Round(h, 3)));
        Assert.Equal(new[] { StreetSurface.Kerb, StreetSurface.Track, StreetSurface.Kerb, StreetSurface.Sidewalk }, p.Surface);
        Assert.Equal(0.06f, RoadStreetSection.HeightAt(side, 1.0f), 4);
        Assert.Equal(0.03f, RoadStreetSection.HeightAt(side, 0.15f), 4);   // on the sloped kerb
        Assert.Equal(1.0f, RoadStreetSection.TrackCentre(side), 4);

        // a plain sidewalk: a vertical kerb, chamfered 45 degrees for the collision
        var walk = RoadStreetSection.For(new RoadSide(SidewalkDm: 20, KerbCm: 12))!;
        Assert.Equal(new[] { 0f, 0f, 2f }, walk.D);
        Assert.Equal(0.12f, RoadStreetSection.Chamfered(walk).D[1], 4);

        // layout 2: grass verge at the path's (sidewalk) height behind a vertical kerb, path flush with it
        var two = RoadStreetSection.For(new RoadSide(SidewalkDm: 15, Bike: BikeKind.Track, BikeDm: 20, KerbCm: 12, VergeDm: 10))!;
        Assert.Equal(new[] { StreetSurface.Kerb, StreetSurface.Verge, StreetSurface.Track, StreetSurface.Sidewalk }, two.Surface);
        Assert.Equal(0f, two.D[1]);
    }

    [Fact]
    public void Paint_on_a_path_lies_on_the_path()
    {
        var side = new RoadSide(SidewalkDm: 15, Bike: BikeKind.TrackMid, BikeDm: 20, KerbCm: 12);
        var road = Road(6, right: side);
        var symbol = RoadPaint.AlongSegment(road, PaintType.BikeSymbol, PaintEmitter.Yellow, 1, 0, 0, 3 + 1.0, 10, 11);
        Assert.All(Enumerable.Range(0, symbol.Vertices.Length / 3), i => Assert.Equal(400.06f, symbol.Vertices[i * 3 + 1], 3));
        Assert.Equal(52, RoadPaintGeometry.BikeSymbol(symbol).Count);
        Assert.Equal(52, RoadPaintGeometry.TriangleCount(symbol));
        // on the carriageway, nothing changes
        var centre = RoadPaint.AlongSegment(road, PaintType.WhiteDashed, PaintEmitter.White, 0.15f, 3, 6, 0);
        Assert.Equal(400f, centre.Vertices[1]);
    }

    [Fact]
    public void Path_sides_survive_the_codec()
    {
        var side = new RoadSide(SidewalkDm: 15, Bike: BikeKind.TrackMid, BikeDm: 20, KerbCm: 12, VergeDm: 10, BufferDm: 8);
        var road = Road(6, left: Lane(13), right: side);
        var tile = new RoadTile { Id = Tile, Segments = [road], Flags = RoadTileFlags.Network | RoadTileFlags.Bikes };
        tile.Paint.Add(RoadPaint.AlongSegment(road, PaintType.BikeSymbol, PaintEmitter.Yellow, 1, 0, 0, 3 + 2.0, 10, 11, RoadPaintGeometry.BikeReversed));
        using var ms = new MemoryStream();
        RoadCodec.Encode(tile, ms);
        ms.Position = 0;
        var back = RoadCodec.Decode(ms);
        Assert.Equal(road.Attributes, back.Segments[0].Attributes);
        Assert.Equal(tile.Paint[0].Vertices, back.Paint[0].Vertices);
        Assert.Equal(RoadPaintGeometry.BikeReversed, back.Paint[0].Variant);

        // a tile from before #120: OSM's raw cycleway tags in the side records are not drawn
        tile.Flags = RoadTileFlags.Network;
        using var old = new MemoryStream();
        RoadCodec.Encode(tile, old);
        old.Position = 0;
        var cleared = RoadCodec.Decode(old).Segments[0].Attributes;
        Assert.Equal(BikeKind.None, cleared.Right.Bike);
        Assert.Equal(15, cleared.Right.SidewalkDm);
        Assert.Equal(15, cleared.Right.OuterDm);
        Assert.False(cleared.Left.HasLane);
    }
}
