using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>House layouts drawn on the web (#1186): the footprint frame and the layout's rules.</summary>
public class HouseLayoutTests
{
    /// <summary>A box building, <paramref name="len"/> x <paramref name="wid"/> metres, its long side turned
    /// <paramref name="yawDeg"/> from east, centred on tile-local (500, 500), 6 m tall.</summary>
    private static Building Box(double len, double wid, double yawDeg)
    {
        double a = yawDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        // tile-local: X east, Z south, so a counter-clockwise yaw (toward north) is -Z
        (float X, float Z) P(double u, double v) => ((float)(500 + u * c + v * s), (float)(500 - (u * s - v * c)));
        var p = new[] { P(-len / 2, -wid / 2), P(len / 2, -wid / 2), P(len / 2, wid / 2), P(-len / 2, wid / 2) };
        var tri = new List<float>();
        void T(int i, int j, int k, float y)
        {
            foreach (var q in new[] { p[i], p[j], p[k] }) tri.AddRange([q.X, y, q.Z]);
        }
        T(0, 1, 2, 0); T(0, 2, 3, 0);   // floor
        T(0, 1, 2, 6); T(0, 2, 3, 6);   // flat roof
        for (int i = 0; i < 4; i++)      // walls
        {
            var q0 = p[i];
            var q1 = p[(i + 1) % 4];
            tri.AddRange([q0.X, 0, q0.Z, q1.X, 0, q1.Z, q1.X, 6, q1.Z]);
            tri.AddRange([q0.X, 0, q0.Z, q1.X, 6, q1.Z, q0.X, 6, q0.Z]);
        }
        return new Building { Kind = BuildingKind.House, Egid = 42, MinY = 0, MaxY = 6, Triangles = tri.ToArray() };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(120)]
    public void Outline_finds_the_box_and_its_long_axis(double yaw)
    {
        var tile = new TileId(2594, 1120);
        var o = BuildingOutline.Compute(Box(12, 8, yaw), tile);
        Assert.Single(o.Rings);
        Assert.Equal(4, o.Rings[0].Length);
        Assert.Equal(12, o.Length, 1);
        Assert.Equal(8, o.Width, 1);
        Assert.Equal(yaw % 180, o.YawDeg, 1);
        Assert.Equal(tile.MinE + 500, o.CenterE, 1);
        Assert.Equal(tile.MaxN - 500, o.CenterN, 1);
        // every corner lands on the box's corners in the local frame
        foreach (var (x, z) in o.LocalRings()[0])
        {
            Assert.Equal(6, Math.Abs(x), 1);
            Assert.Equal(4, Math.Abs(z), 1);
        }
    }

    [Fact]
    public void Local_and_lv95_round_trip()
    {
        var (e, n) = BuildingOutline.ToLv95(2600000, 1200000, 37, 3.5, -2);
        var (x, z) = BuildingOutline.ToLocal(2600000, 1200000, 37, e, n);
        Assert.Equal(3.5, x, 6);
        Assert.Equal(-2, z, 6);
        // yaw 0: x east, z south
        Assert.Equal((2600001.0, 1199998.0), BuildingOutline.ToLv95(2600000, 1200000, 0, 1, 2));
    }

    private static readonly List<(double X, double Z)[]> Footprint = [[(-6, -4), (6, -4), (6, 4), (-6, 4)]];

    private static HouseLayout Good() => new()
    {
        Egid = 42,
        Floors =
        [
            new HouseFloor
            {
                Level = 0,
                Rooms =
                [
                    HouseRoom.Rect(-6, -4, 0, 4, "Living"),
                    HouseRoom.Rect(0, -4, 6, 4, "Kitchen"),
                ],
                Openings =
                [
                    new HouseOpening { Kind = "door", X0 = 0, Z0 = -1, X1 = 0, Z1 = 0 },     // inner door
                    new HouseOpening { Kind = "window", X0 = -4, Z0 = 4, X1 = -2, Z1 = 4 },  // facade window
                    new HouseOpening { Kind = "door", X0 = -3, Z0 = -4, X1 = -2, Z1 = -4 },  // front door
                ],
                Stairs = [new HouseStair { X0 = 4, Z0 = 0, X1 = 5, Z1 = 3.5, Up = "+z" }],
            },
            new HouseFloor { Level = 1, Rooms = [HouseRoom.Rect(-6, -4, 6, 4, "Bedroom")] },
        ],
    };

    [Fact]
    public void A_good_layout_passes_and_survives_json()
    {
        var l = Good();
        Assert.Empty(l.Validate(Footprint));
        var back = HouseLayout.FromJson(l.ToJson())!;
        Assert.Empty(back.Validate(Footprint));
        Assert.Equal("Kitchen", back.Floors[0].Rooms[1].Type);
        Assert.Contains("\"storeyHeight\"", l.ToJson());
    }

    [Fact]
    public void Rules_catch_the_usual_mistakes()
    {
        var l = Good();
        l.Floors[0].Rooms[1] = HouseRoom.Rect(-1, -4, 6, 4, "Kitchen");          // overlaps the living room
        l.Floors[0].Rooms[0].Type = "Vault";                                     // not a house room
        l.Floors[0].Openings.Add(new HouseOpening { Kind = "door", X0 = -3, Z0 = 1, X1 = -2, Z1 = 1 }); // mid-room
        l.Floors[1].Rooms.Add(HouseRoom.Rect(6, -4, 10, 4, "Bedroom")); // outside
        l.Floors[1].Stairs.Add(new HouseStair { X0 = 0, Z0 = 0, X1 = 1, Z1 = 3, Up = "+z" });      // nothing above
        var err = string.Join("\n", l.Validate(Footprint));
        Assert.Contains("overlaps room 1", err);
        Assert.Contains("unknown type", err);
        Assert.Contains("not on a room's wall", err);
        Assert.Contains("outside the building", err);
        Assert.Contains("no floor above", err);
    }

    [Fact]
    public void Every_room_needs_a_way_in()
    {
        var l = Good();
        l.Floors[0].Openings.RemoveAt(0);                     // the kitchen's door
        var err = string.Join("\n", l.Validate(Footprint));
        Assert.Contains("room 2 (Kitchen): no way in", err);
        Assert.Contains("room 1 (Bedroom): no way in", err);  // its only way up is the kitchen's stair

        l = Good();
        l.Floors[0].Openings.RemoveAt(2);                     // no front door
        Assert.Contains("no front door", string.Join("\n", l.Validate(Footprint)));

        l = Good();
        l.Floors[1].Rooms[0] = HouseRoom.Rect(-6, -4, 4, 4, "Bedroom"); // the stair now arrives outside the bedroom
        Assert.Contains("arrives outside any room", string.Join("\n", l.Validate(Footprint)));
    }

    [Fact]
    public void The_export_file_is_read_by_egid_and_bad_layouts_are_left_out()
    {
        var bad = Good();
        bad.Egid = 7;
        bad.Floors[0].Rooms.Clear();
        string json = "{\"version\":1,\"layouts\":[" + Good().ToJson() + "," + bad.ToJson() + "]}";
        var warned = new List<string>();
        var all = HouseLayouts.Parse(json, warned.Add);
        Assert.True(all.ContainsKey(42));
        Assert.False(all.ContainsKey(7));
        Assert.Single(warned);
        HouseLayouts.Set(all.Values);
        Assert.Equal(Good().Hash(), HouseLayouts.HashFor(42));
        Assert.Equal("", HouseLayouts.HashFor(7));
        HouseLayouts.Set([]);
    }

    /// <summary>An L-shaped living room and a kitchen behind an angled wall, a door in that wall.</summary>
    private static HouseLayout Shaped() => new()
    {
        Egid = 42,
        Floors =
        [
            new HouseFloor
            {
                Level = 0,
                Rooms =
                [
                    new HouseRoom { Type = "Living", Points = [[-6, -4], [2, -4], [-1, 4], [-6, 4]] },
                    new HouseRoom { Type = "Kitchen", Points = [[2, -4], [6, -4], [6, 4], [-1, 4]] },
                ],
                Openings =
                [
                    new HouseOpening { Kind = "door", X0 = 0.875, Z0 = -1, X1 = 0.5, Z1 = 0 },  // in the angled wall
                    new HouseOpening { Kind = "door", X0 = -5, Z0 = -4, X1 = -4, Z1 = -4 },     // front door
                ],
            },
        ],
    };

    [Fact]
    public void Polygon_rooms_with_an_angled_wall_and_a_door_in_it()
    {
        Assert.Empty(Shaped().Validate(Footprint));
        var l = Shaped();
        l.Floors[0].Rooms[1].Points[3] = [-1.5, 4];  // the kitchen now reaches 0.5 m into the living room along the wall
        Assert.Contains("overlaps room 1", string.Join("\n", l.Validate(Footprint)));
        l = Shaped();
        l.Floors[0].Rooms[0].Points = [[-6, -4], [0, 4], [0, -4], [-6, 4]]; // a bow tie
        Assert.Contains("cross each other", string.Join("\n", l.Validate(Footprint)));
        l = Shaped();
        l.Floors[0].Openings[0] = new HouseOpening { Kind = "door", X0 = 0.6, Z0 = -1, X1 = 0.5, Z1 = 0 }; // off the wall
        Assert.Contains("not on a room's wall", string.Join("\n", l.Validate(Footprint)));
    }

    [Fact]
    public void Cutting_a_right_angled_room_is_exact()
    {
        // an L: 6 x 4 with a 3 x 2 notch out of a corner
        (double, double)[] l = [(0, 0), (6, 0), (6, 2), (3, 2), (3, 4), (0, 4)];
        var (pieces, contacts) = RoomStrips.Cut([l], []);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(6 * 4 - 3 * 2, pieces.Sum(p => p.Width * p.Depth), 6);
        Assert.Single(contacts, c => c.Length >= 2.99);
        // a rectangle stays one piece, and a stair in it is carved out as a lane of its own
        var (one, _) = RoomStrips.Cut([[(0, 0), (5, 0), (5, 4), (0, 4)]], []);
        Assert.Single(one);
        var (carved, links) = RoomStrips.Cut([[(0, 0), (5, 0), (5, 4), (0, 4)]], [(1, 0.5, 2, 3.5)]);
        Assert.Contains(carved, p => p is { X0: 1, X1: 2, Z0: 0.5, Z1: 3.5 });
        Assert.Equal(20, carved.Sum(p => p.Width * p.Depth), 6);
        Assert.True(links.Count >= carved.Count - 1);
    }

    [Fact]
    public void Two_rooms_sharing_an_angled_wall_still_meet_after_the_cut()
    {
        var shaped = Shaped().Floors[0].Rooms.Select(r => r.Shape()!).ToList();
        var (pieces, contacts) = RoomStrips.Cut(shaped, []);
        // the steps follow the wall: each room keeps nearly its area, and nothing is lost between them
        Assert.Equal(96, pieces.Sum(p => p.Width * p.Depth), 1);
        Assert.Equal(Math.Abs(Poly.Area(shaped[0])), pieces.Where(p => p.Room == 0).Sum(p => p.Width * p.Depth), 0);
        // every strip's living piece touches its kitchen piece
        Assert.True(contacts.Count(c => pieces[c.A].Room != pieces[c.B].Room && !c.AlongX) >= 8);
        // no two pieces overlap
        for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
            {
                var a = pieces[i];
                var b = pieces[j];
                Assert.False(a.X0 < b.X1 - 1e-6 && b.X0 < a.X1 - 1e-6 && a.Z0 < b.Z1 - 1e-6 && b.Z0 < a.Z1 - 1e-6, $"{a} and {b}");
            }
    }

    [Fact]
    public void A_drawing_a_few_centimetres_off_is_evened_out()
    {
        // the kitchen's wall drawn 4 cm into the living room, the bedroom's 6 cm short of it
        (double, double)[] living = [(0, 0), (4, 0), (4, 5), (0, 5)];
        (double, double)[] kitchen = [(3.96, 0), (8, 0), (8, 2.5), (3.96, 2.5)];
        (double, double)[] bedroom = [(4.06, 2.5), (8, 2.5), (8, 5), (4.06, 5)];
        var (pieces, contacts) = RoomStrips.Cut([living, kitchen, bedroom], []);
        // the living room's wall meets each neighbour halfway, so it jogs by 5 cm: two pieces of it, joined
        Assert.Equal(4, pieces.Count);
        Assert.Contains(contacts, c => pieces[c.A].Room == 0 && pieces[c.B].Room == 0);
        Assert.Contains(contacts, c => pieces[c.A].Room == 0 && pieces[c.B].Room == 1);
        Assert.Contains(contacts, c => pieces[c.A].Room == 0 && pieces[c.B].Room == 2);
        Assert.Contains(contacts, c => pieces[c.A].Room == 1 && pieces[c.B].Room == 2);
    }

    [Fact]
    public void The_footprint_slack_lets_a_wall_sit_on_the_outline()
    {
        Assert.True(HouseLayout.InsideOutline(Footprint, 6.2, 0));
        Assert.False(HouseLayout.InsideOutline(Footprint, 6.5, 0));
        Assert.True(HouseLayout.InsideOutline(Footprint, 0, 0));
    }
}
