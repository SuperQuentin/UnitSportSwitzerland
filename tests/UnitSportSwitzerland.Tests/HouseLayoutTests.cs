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
                    new HouseRoom { X0 = -6, Z0 = -4, X1 = 0, Z1 = 4, Type = "Living" },
                    new HouseRoom { X0 = 0, Z0 = -4, X1 = 6, Z1 = 4, Type = "Kitchen" },
                ],
                Openings =
                [
                    new HouseOpening { Kind = "door", X0 = 0, Z0 = -1, X1 = 0, Z1 = 0 },     // inner door
                    new HouseOpening { Kind = "window", X0 = -4, Z0 = 4, X1 = -2, Z1 = 4 },  // facade window
                    new HouseOpening { Kind = "door", X0 = -3, Z0 = -4, X1 = -2, Z1 = -4 },  // front door
                ],
                Stairs = [new HouseStair { X0 = 4, Z0 = 0, X1 = 5, Z1 = 3.5, Up = "+z" }],
            },
            new HouseFloor { Level = 1, Rooms = [new HouseRoom { X0 = -6, Z0 = -4, X1 = 6, Z1 = 4, Type = "Bedroom" }] },
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
        l.Floors[0].Rooms[1].X0 = -1;                                            // overlaps the living room
        l.Floors[0].Rooms[0].Type = "Vault";                                     // not a house room
        l.Floors[0].Openings.Add(new HouseOpening { Kind = "door", X0 = -3, Z0 = 1, X1 = -2, Z1 = 2 }); // diagonal
        l.Floors[0].Openings.Add(new HouseOpening { Kind = "door", X0 = -3, Z0 = 1, X1 = -2, Z1 = 1 }); // mid-room
        l.Floors[1].Rooms.Add(new HouseRoom { X0 = 6, Z0 = -4, X1 = 10, Z1 = 4, Type = "Bedroom" }); // outside
        l.Floors[1].Stairs.Add(new HouseStair { X0 = 0, Z0 = 0, X1 = 1, Z1 = 3, Up = "+z" });      // nothing above
        var err = string.Join("\n", l.Validate(Footprint));
        Assert.Contains("overlaps room 1", err);
        Assert.Contains("unknown type", err);
        Assert.Contains("not straight", err);
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
        l.Floors[1].Rooms[0].X1 = 4;                          // the stair now arrives outside the bedroom
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

    [Fact]
    public void The_footprint_slack_lets_a_wall_sit_on_the_outline()
    {
        Assert.True(HouseLayout.InsideOutline(Footprint, 6.2, 0));
        Assert.False(HouseLayout.InsideOutline(Footprint, 6.5, 0));
        Assert.True(HouseLayout.InsideOutline(Footprint, 0, 0));
    }
}
