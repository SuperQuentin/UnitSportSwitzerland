using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="SiteOffices.Plan"/> (#822): the site office's containers, their doors and the rooms
/// behind them. What matters: it is a function of the site alone (every peer and the server agree),
/// each door stands on the door drawn in its container's end and faces out of it, the interior's
/// plan box is the ground row of containers with every door on its front wall, and the rooms fill
/// the row one per container.
/// </summary>
public class SiteOfficeTests
{
    private static Building Box(float w, float d, float h, byte floors, float turn)
    {
        var u = Vector2.FromAngle(turn);
        var v = new Vector2(-u.Y, u.X);
        var c = new Vector2(500, 500);
        var corners = new[] { c - u * w / 2 - v * d / 2, c + u * w / 2 - v * d / 2, c + u * w / 2 + v * d / 2, c - u * w / 2 + v * d / 2 };
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 e) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, e.X, e.Y, e.Z });
        Vector3 P(Vector2 p, float y) => new(p.X, y, p.Y);
        for (int i = 0; i < 4; i++)
        {
            Tri(P(corners[i], 0), P(corners[(i + 1) % 4], 0), P(corners[(i + 1) % 4], h));
            Tri(P(corners[i], 0), P(corners[(i + 1) % 4], h), P(corners[i], h));
        }
        Tri(P(corners[0], h), P(corners[1], h), P(corners[2], h));
        Tri(P(corners[0], h), P(corners[2], h), P(corners[3], h));
        return new Building { Kind = BuildingKind.UnderConstruction, Floors = floors, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
    }

    /// <summary>Ground that slopes, so a door on the wrong ground shows.</summary>
    private static float Slope(float x, float z) => 410f + 0.06f * x - 0.03f * z;

    private static (ConstructionSite Site, SiteOffice Office, List<ShellBox> Boxes) Make(string key, float w, float d, float h, byte floors, float turn, Vector2 street)
    {
        var b = Box(w, d, h, floors, turn);
        var site = ConstructionSites.Plan(key, b, PlanBox.Of(b)!.Value, street, _ => false)!;
        var (office, boxes) = SiteOffices.Plan(site, Slope)!.Value;
        return (site, office, boxes);
    }

    public static IEnumerable<object[]> Sites() => new[]
    {
        new object[] { "2585_1114_68", 24f, 15f, 1.2f, (byte)4, 0.3f, 500f, 560f },   // foundations, street to the south
        new object[] { "2586_1115_51", 24f, 15f, 6.3f, (byte)4, 1.1f, 560f, 500f },   // a shell turned the other way, street east
        new object[] { "2580_1112_61", 14f, 11f, 6.3f, (byte)2, -0.7f, 440f, 500f },  // a house plot: one stack
        new object[] { "2582_1102_61", 52f, 30f, 18.4f, (byte)6, 2.4f, 500f, 440f },  // a big block: two stacks
    };

    private static List<ShellBox> GroundContainers(SiteOffice office, List<ShellBox> boxes) =>
        boxes.Where(b => b.Part == ShellPart.Container && b.Min.Y < office.Ground + 0.01f).ToList();

    [Theory]
    [MemberData(nameof(Sites))]
    public void Every_ground_floor_container_has_a_door(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (_, office, boxes) = Make(key, w, d, h, floors, turn, new Vector2(se, sn));
        var ground = GroundContainers(office, boxes);
        Assert.NotEmpty(ground);
        Assert.Equal(ground.Count, office.Doors.Count);
        Assert.Equal(Enumerable.Range(0, office.Doors.Count), office.Doors.Select(x => x.Slot));
        Assert.Equal(office.Doors.Count, office.Rooms.Count);
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void A_door_stands_on_the_door_drawn_in_its_container(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (_, office, boxes) = Make(key, w, d, h, floors, turn, new Vector2(se, sn));
        // the drawn doors: the thin trim panels across a container's end, on the ground floor
        var panels = boxes.Where(b => b.Part == ShellPart.ContainerTrim && b.Min.Y > office.Ground + 0.05f
            && b.Max.Y < office.Ground + SiteOffices.ContainerHeight - 0.3f).ToList();
        Assert.Equal(office.Doors.Count, panels.Count);
        foreach (var door in office.Doors)
        {
            var o = new Vector3(door.Outward.X, 0, door.Outward.Y);
            var across = new Vector3(-door.Outward.Y, 0, door.Outward.X);
            var panel = Assert.Single(panels, p => Contains(p, door.Sill - o * 0.04f + Vector3.Up * 1f, 0.001f));
            // on the sill at the ground the container stands on, the opening exactly the panel's width,
            // and the doorway plane the trim's face, just in front of the panel
            Assert.Equal(office.Ground, door.Sill.Y, 3);
            float span = Math.Abs((panel.Max - panel.Min).Dot(across));
            Assert.Equal(SiteOffices.DoorWidth, span, 2);
            Assert.Equal(SiteOffices.DoorWidth, door.Width, 3);
            Assert.True(Contains(panel, door.Sill - o * 0.02f + across * (door.Width / 2 - 0.01f) + Vector3.Up, 0.001f), $"{key}: door {door.Slot}'s side off its panel");
            Assert.True(Contains(panel, door.Sill - o * 0.02f - across * (door.Width / 2 - 0.01f) + Vector3.Up, 0.001f), $"{key}: door {door.Slot}'s other side off its panel");
            // the open door's portal quad stands DoorLink.OutsideQuadOffset (2 cm) out from the plane:
            // in front of the panel, so it covers it, and of the rails round the end
            Assert.False(Contains(panel, door.Sill + o * 0.02f + Vector3.Up, 0.001f), $"{key}: door {door.Slot}'s portal inside its panel: it would not cover it");
            Assert.DoesNotContain(boxes, x => Contains(x, door.Sill + o * 0.02f + Vector3.Up * 0.05f, 0.001f));
            Assert.True(door.Height >= panel.Max.Y - office.Ground - 0.001f, $"{key}: door {door.Slot} lower than its panel");
        }
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void A_door_faces_out_of_its_container(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (_, office, boxes) = Make(key, w, d, h, floors, turn, new Vector2(se, sn));
        var ground = GroundContainers(office, boxes);
        foreach (var door in office.Doors)
        {
            var o = new Vector3(door.Outward.X, 0, door.Outward.Y);
            Assert.Equal(1f, door.Outward.Length(), 3);
            var up = Vector3.Up * 1f;
            Assert.Single(ground, c => Contains(c, door.Sill - o * 0.3f + up, 0f));
            Assert.DoesNotContain(boxes, c => c.Solid && Contains(c, door.Sill + o * 0.3f + up, 0f));
        }
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void The_interior_is_the_ground_row_with_every_door_on_its_front_wall(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (_, office, boxes) = Make(key, w, d, h, floors, turn, new Vector2(se, sn));
        float hw = office.Width / 2, hd = office.Depth / 2;
        foreach (var c in GroundContainers(office, boxes))
            foreach (var corner in new[] { new Vector2(c.Min.X, c.Min.Z), new Vector2(c.Max.X, c.Min.Z), new Vector2(c.Min.X, c.Max.Z), new Vector2(c.Max.X, c.Max.Z) })
            {
                var p = office.ToInterior(corner);
                Assert.True(Math.Abs(p.X) <= hw + 0.01f && Math.Abs(p.Y) <= hd + 0.01f, $"{key}: a container's corner {p} outside the plan box");
            }
        foreach (var door in office.Doors)
        {
            var p = office.ToInterior(new Vector2(door.Sill.X, door.Sill.Z));
            // the trim's face, 2 cm in front of the container's end: the plan box's front wall
            Assert.Equal(-hd - SiteOffices.Trim, p.Y, 3);
            var (x0, x1) = office.Rooms[door.Slot];
            Assert.True(p.X - door.Width / 2 > x0 + 0.05f && p.X + door.Width / 2 < x1 - 0.05f, $"{key}: door {door.Slot} at {p.X:F2} not in its room {x0:F2}..{x1:F2}");
            // the interior's +Z is into the container, so a door faces its -Z
            var inward = office.ToInterior(office.Center - door.Outward);
            Assert.Equal(1f, inward.Y, 3);
            Assert.Equal(0f, inward.X, 3);
        }
        foreach (float z in office.WindowZ)
            Assert.True(Math.Abs(z) + SiteOffices.WindowLength / 2 < hd, $"{key}: a window at {z:F2} off the container");
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void The_rooms_fill_the_row_one_per_container(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (_, office, boxes) = Make(key, w, d, h, floors, turn, new Vector2(se, sn));
        var rooms = office.Rooms.OrderBy(r => r.X0).ToList();
        Assert.Equal(-office.Width / 2, rooms[0].X0, 3);
        Assert.Equal(office.Width / 2, rooms[^1].X1, 3);
        for (int i = 0; i + 1 < rooms.Count; i++) Assert.Equal(rooms[i].X1, rooms[i + 1].X0, 3);
        foreach (var r in rooms) Assert.True(r.X1 - r.X0 >= SiteOffices.ContainerWidth - 0.01f, $"{key}: a room {r.X1 - r.X0:F2} m wide");
        Assert.InRange(office.Rooms.Count, 1, 2);
        Assert.Equal(SiteOffices.ContainerLength, office.Depth, 3);
    }

    [Fact]
    public void A_big_site_has_an_office_and_a_mess_room_a_house_plot_an_office()
    {
        Assert.Equal(2, Make("2582_1102_61", 52f, 30f, 18.4f, 6, 2.4f, new Vector2(500, 440)).Office.Doors.Count);   // over 1,500 m²: two stacks
        Assert.Single(Make("2580_1112_61", 14f, 11f, 6.3f, 2, -0.7f, new Vector2(440, 500)).Office.Doors);
    }

    [Fact]
    public void The_same_site_always_has_the_same_office()
    {
        var a = Make("2537_1154_266", 34f, 20f, 9.1f, 6, 0.9f, new Vector2(520, 560));
        var b = Make("2537_1154_266", 34f, 20f, 9.1f, 6, 0.9f, new Vector2(520, 560));
        Assert.Equal(a.Boxes, b.Boxes);
        Assert.Equal(a.Office.Doors, b.Office.Doors);
        Assert.Equal(a.Office.Rooms, b.Office.Rooms);
        Assert.Equal(a.Office.Center, b.Office.Center);
    }

    [Fact]
    public void The_dressing_draws_the_office_it_plans()
    {
        // the yard's mesh and the building's doors are the same containers
        var b = Box(40f, 18f, 18.4f, 6, 2.4f);
        var site = ConstructionSites.Plan("2582_1102_61", b, PlanBox.Of(b)!.Value, new Vector2(500, 440), _ => false)!;
        var yard = SiteDressings.Plan(site, Slope);
        var (office, boxes) = SiteOffices.Plan(site, Slope)!.Value;
        Assert.NotNull(yard.Office);
        Assert.Equal(office.Doors, yard.Office!.Doors);
        Assert.Equal(boxes, yard.Boxes.Skip(yard.OfficeBoxes.Start).Take(yard.OfficeBoxes.Count));
        Assert.All(yard.Boxes.Where((x, i) => x.Part == ShellPart.Container && (i < yard.OfficeBoxes.Start || i >= yard.OfficeBoxes.Start + yard.OfficeBoxes.Count)),
            x => Assert.Fail($"a container outside the office's boxes at {x.Min}"));
    }

    private static bool Contains(ShellBox b, Vector3 p, float margin) =>
        p.X >= b.Min.X - margin && p.X <= b.Max.X + margin && p.Y >= b.Min.Y - margin && p.Y <= b.Max.Y + margin
        && p.Z >= b.Min.Z - margin && p.Z <= b.Max.Z + margin;
}
