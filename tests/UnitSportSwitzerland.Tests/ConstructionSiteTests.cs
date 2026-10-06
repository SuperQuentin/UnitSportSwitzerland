using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="ConstructionSites.Plan"/> (#606) lays out a building site from one building, the street
/// it faces and what stands round it. Every peer has to lay out the same site from the same bytes
/// with nothing sent, so these pin that it is a pure function, that the phase is read off the data
/// the way the real sites measure, and that nothing of the site lands on a road, in a building or
/// in the way.
/// </summary>
public class ConstructionSiteTests
{
    /// <summary>A flat-topped prism, as swissBUILDINGS3D draws an <c>Im Bau</c> volume, turned by <paramref name="turn"/> radians.</summary>
    internal static Building Box(float cx, float cz, float w, float d, float h, byte floors, float turn = 0f,
        BuildingKind kind = BuildingKind.UnderConstruction)
    {
        var u = Vector2.FromAngle(turn);
        var v = new Vector2(-u.Y, u.X);
        var c = new Vector2(cx, cz);
        var corners = new[] { c - u * w / 2 - v * d / 2, c + u * w / 2 - v * d / 2, c + u * w / 2 + v * d / 2, c - u * w / 2 + v * d / 2 };
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 e) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, e.X, e.Y, e.Z });
        Vector3 P(Vector2 p, float y) => new(p.X, y, p.Y);
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            Tri(P(a, 0), P(b, 0), P(b, h));
            Tri(P(a, 0), P(b, h), P(a, h));
        }
        Tri(P(corners[0], h), P(corners[1], h), P(corners[2], h));
        Tri(P(corners[0], h), P(corners[2], h), P(corners[3], h));
        return new Building { Kind = kind, Floors = floors, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
    }

    internal static ConstructionSite Plan(string key, Building b, Vector2? street, Func<Vector2, bool>? blocked = null) =>
        ConstructionSites.Plan(key, b, PlanBox.Of(b)!.Value, street, blocked ?? (_ => false))!;

    /// <summary>Every point the site puts something on: hoarding ends and middles, zone and machine corners, crane bases.</summary>
    private static IEnumerable<Vector2> Footprints(ConstructionSite s)
    {
        foreach (var r in s.Hoarding) { yield return r.A; yield return r.B; yield return (r.A + r.B) / 2; }
        foreach (var z in s.Zones) foreach (var c in z.Rect.Corners()) yield return c;
        foreach (var m in s.Machines)
        {
            var (w, l) = ConstructionSites.MachineSize(m.Role);
            yield return m.At;
            var f = Facing(m.Yaw);
            foreach (var c in new SiteRect(m.At, new Vector2(f.Y, -f.X), w, l).Corners()) yield return c;
        }
        foreach (var c in s.Cranes) yield return c.Base;
    }

    [Fact]
    public void Only_a_building_under_construction_is_a_site()
    {
        var house = Box(100, 100, 12, 10, 7, 2, kind: BuildingKind.House);
        Assert.Null(ConstructionSites.Plan("2500_1120_1", house, PlanBox.Of(house)!.Value, null, _ => false));
    }

    [Fact]
    public void The_same_building_always_gives_the_same_site()
    {
        var b = Box(250, 300, 34, 22, 7.5f, 5, turn: 0.4f);
        for (int i = 0; i < 40; i++)
        {
            string key = $"2537_1152_{i}";
            var a = Plan(key, b, new Vector2(250, 340));
            var again = Plan(key, b, new Vector2(250, 340));
            Assert.Equal(a.Phase, again.Phase);
            Assert.Equal(a.Hoarding, again.Hoarding);
            Assert.Equal(a.Zones, again.Zones);
            Assert.Equal(a.Cranes, again.Cranes);
            Assert.Equal(a.Machines, again.Machines);
        }
    }

    [Theory]
    // measured on the real tiles: a slab with a six-storey building to come
    [InlineData(1.3f, 6, SitePhase.Foundations, 0, 6)]
    [InlineData(2.4f, 3, SitePhase.Foundations, 0, 3)]
    // three storeys of six
    [InlineData(9.1f, 6, SitePhase.Shell, 3, 6)]
    [InlineData(3.2f, 4, SitePhase.Shell, 1, 4)]
    // at full height: GWR's floors, each a little taller than a house's
    [InlineData(24.6f, 3, SitePhase.ToppedOut, 6, 6)]
    [InlineData(11.4f, 4, SitePhase.ToppedOut, 4, 4)]
    // one GWR floor 20 m high is a hall with storeys in it, not one 20 m storey
    [InlineData(20.8f, 1, SitePhase.ToppedOut, 5, 5)]
    public void The_phase_is_read_off_the_surveyed_height(float h, byte floors, SitePhase phase, int built, int target)
    {
        var b = Box(200, 200, 30, 18, h, floors);
        var (p, measured, storey, b0, t) = ConstructionSites.Progress("2500_1120_7", b, PlanBox.Of(b)!.Value);
        Assert.Equal(phase, p);
        Assert.Equal(h, measured, 2);
        Assert.Equal(built, b0);
        Assert.Equal(target, t);
        Assert.InRange(storey, 2.8f, ConstructionSites.MaxStorey);
        if (phase == SitePhase.ToppedOut) Assert.Equal(h, storey * built, 2);
    }

    [Fact]
    public void A_site_with_no_floor_count_still_has_somewhere_to_go()
    {
        for (int i = 0; i < 100; i++)
        {
            var b = Box(200, 200, 30, 18, 6.3f, 0);
            var (phase, _, _, built, target) = ConstructionSites.Progress($"2500_1120_{i}", b, PlanBox.Of(b)!.Value);
            Assert.True(target >= built);
            if (phase == SitePhase.Shell) Assert.True(target > built);
        }
    }

    [Fact]
    public void The_site_faces_its_street()
    {
        // a road runs east-west 25 m south of the building (+z is south)
        var b = Box(300, 300, 30, 16, 7, 4, turn: 0.2f);
        var street = new Vector2(300, 330);
        for (int i = 0; i < 30; i++)
        {
            var s = Plan($"2500_1120_{i}", b, street);
            Assert.True(s.Front.Dot(Vector2.Down) > 0.9f, $"front {s.Front}");
            Assert.True((s.Gate - s.Box.Center).Dot(street - s.Box.Center) > 0);
            // the yard is the deep side: more room in front of the building than behind it
            Assert.True((s.Area.Center - s.Box.Center).Dot(s.Front) > 0);
        }
    }

    [Fact]
    public void With_nothing_round_it_the_hoarding_is_closed_but_for_the_gate()
    {
        var b = Box(300, 300, 26, 14, 1.2f, 3, turn: 0.54f);   // the turned case (#524)
        var s = Plan("2500_1120_3", b, new Vector2(320, 340));
        float perimeter = 2 * (s.Area.Width + s.Area.Depth);
        float built = s.Hoarding.Sum(r => r.A.DistanceTo(r.B));
        float open = perimeter - built;
        Assert.InRange(open, s.GateWidth, s.GateWidth + 2 * ConstructionSites.Panel + 0.1f);
        // and the opening is where the gate is
        foreach (var r in s.Hoarding)
            Assert.True(SegmentDistance(s.Gate, r.A, r.B) >= s.GateWidth / 2 - 0.01f);
    }

    [Fact]
    public void Nothing_stands_on_a_road_or_in_another_building()
    {
        // a 9 m road 8 m in front of the building and a neighbour 5 m to its east
        var b = Box(400, 400, 40, 24, 6, 6);
        var neighbour = new Rect2(422, 380, 12, 40);
        bool Blocked(Vector2 p) => Math.Abs(p.Y - (412 + 8 + 4.5f)) < 4.5f + 0.3f || neighbour.Grow(0.3f).HasPoint(p);
        for (int i = 0; i < 30; i++)
        {
            var s = Plan($"2538_1155_{i}", b, new Vector2(400, 424.5f), Blocked);
            foreach (var p in Footprints(s))
                Assert.False(Blocked(p), $"site {i}: {p} is on the road or in the neighbour");
            Assert.NotEmpty(s.Hoarding);
        }
    }

    [Fact]
    public void Nothing_stands_in_the_building_its_scaffolding_or_the_way_in()
    {
        var b = Box(500, 500, 48, 30, 0.8f, 7, turn: -0.3f);
        for (int i = 0; i < 30; i++)
        {
            var s = Plan($"2499_1119_{i}", b, new Vector2(470, 560));
            var keepOut = new SiteRect(s.Box.Center, s.Box.AxisU, s.Box.Width + 2 * ConstructionSites.Scaffold,
                s.Box.Depth + 2 * ConstructionSites.Scaffold);
            foreach (var z in s.Zones)
                Assert.False(z.Rect.Overlaps(keepOut), $"{z.Kind} in the scaffolding");
            foreach (var c in s.Cranes)
                Assert.Equal(c.Inside, keepOut.Contains(c.Base));
            foreach (var m in s.Machines)
                Assert.False(keepOut.Contains(m.At), $"{m.Role} in the building");
            // everything is inside the hoarding line
            foreach (var p in Footprints(s))
                Assert.True(s.Area.Contains(p, 0.01f), $"{p} outside the site");
        }
    }

    [Fact]
    public void Cranes_reach_every_corner_and_clear_the_finished_building()
    {
        foreach (var (w, d) in new[] { (30f, 20f), (70f, 25f), (120f, 30f) })
            for (int i = 0; i < 20; i++)
            {
                var b = Box(600, 600, w, d, 4, 6);
                var s = Plan($"2537_1154_{i}", b, new Vector2(600, 650));
                if (s.Cranes.Count == 0) continue;
                Assert.All(s.Cranes, c => Assert.Equal(CraneKind.Tower, c.Kind));
                Assert.Equal(1 + (w > 55 ? 1 : 0) + (w > 100 ? 1 : 0), s.Cranes.Count);
                foreach (var corner in s.Box.Corners())
                    Assert.Contains(s.Cranes, c => c.Base.DistanceTo(corner) <= c.JibLength);
                Assert.All(s.Cranes, c => Assert.True(c.HookHeight >= s.TargetHeight + 8f));
            }
    }

    [Fact]
    public void A_crane_with_no_room_outside_stands_in_the_building()
    {
        // a long thin block hemmed in by its neighbours, 2 m off both long walls: no 4.6 m base fits
        var b = Box(300, 300, 80, 14, 6, 6);
        bool Blocked(Vector2 p) => Math.Abs(p.Y - 300) > 9.5f && Math.Abs(p.Y - 300) < 30f && Math.Abs(p.X - 300) < 60f;
        var s = Plan("2503_1120_156", b, new Vector2(300, 340), Blocked);
        Assert.Equal(2, s.Cranes.Count);
        Assert.All(s.Cranes, c => Assert.True(c.Inside));
        Assert.All(s.Cranes, c => Assert.True(s.Box.DistanceTo(c.Base) == 0));
        // each on its own half, not both on one spot
        Assert.True(s.Cranes[0].Base.DistanceTo(s.Cranes[1].Base) > 30f);
        foreach (var corner in s.Box.Corners())
            Assert.Contains(s.Cranes, c => c.Base.DistanceTo(corner) <= c.JibLength);
    }

    [Fact]
    public void A_house_plot_gets_a_self_erecting_crane_and_the_small_machines()
    {
        var kinds = new HashSet<CraneKind>();
        var roles = new HashSet<MachineRole>();
        for (int i = 0; i < 60; i++)
        {
            var b = Box(700, 700, 14, 11, 3.5f, 2);
            var s = Plan($"2582_1112_{i}", b, new Vector2(700, 725));
            Assert.True(s.Small);
            foreach (var c in s.Cranes) kinds.Add(c.Kind);
            foreach (var m in s.Machines) roles.Add(m.Role);
        }
        Assert.Equal(new[] { CraneKind.SelfErecting }, kinds.ToArray());
        Assert.DoesNotContain(MachineRole.Excavator, roles);
        Assert.DoesNotContain(MachineRole.Mixer, roles);
        Assert.Contains(MachineRole.MiniDumper, roles);
    }

    [Fact]
    public void The_machines_follow_the_work()
    {
        var b = Box(800, 800, 40, 26, 1.0f, 6);
        var s = Plan("2506_1138_171", b, new Vector2(800, 845));
        Assert.Equal(SitePhase.Foundations, s.Phase);
        Assert.Contains(s.Machines, m => m.Role == MachineRole.Excavator);
        Assert.Contains(s.Zones, z => z.Kind == SiteZoneKind.SoilHeap);
        // the excavator faces the dig, a lorry faces the gate, each square to the building
        var ex = s.Machines.First(m => m.Role == MachineRole.Excavator);
        Assert.True(Facing(ex.Yaw).Dot(s.Box.Center - ex.At) > 0);
        foreach (var m in s.Machines)
            Assert.True(Math.Abs(Facing(m.Yaw).Dot(s.Front)) > 0.99f || Math.Abs(Facing(m.Yaw).Dot(s.Front)) < 0.01f);
        foreach (var m in s.Machines.Where(m => m.Role is MachineRole.Tipper or MachineRole.Van))
            Assert.True(Facing(m.Yaw).Dot(s.Gate - m.At) >= 0);

        var topped = Plan("2506_1138_171", Box(800, 800, 40, 26, 18.2f, 6), new Vector2(800, 845));
        Assert.Equal(SitePhase.ToppedOut, topped.Phase);
        Assert.DoesNotContain(topped.Machines, m => m.Role is MachineRole.Excavator or MachineRole.Mixer);
        Assert.DoesNotContain(topped.Zones, z => z.Kind == SiteZoneKind.SoilHeap);
    }

    [Fact]
    public void A_machine_that_finds_no_room_leaves_a_gap()
    {
        foreach (var phase in new[] { SitePhase.Foundations, SitePhase.Shell, SitePhase.ToppedOut })
            foreach (bool small in new[] { false, true })
                Assert.NotEmpty(ConstructionSites.Roles(phase, small));
        var s = Plan("2500_1120_9", Box(900, 900, 40, 26, 1.0f, 6), new Vector2(900, 945));
        // ordinals are places in the phase's list, so they are distinct and below its length
        var ordinals = s.Machines.Select(m => m.Ordinal).ToList();
        Assert.Equal(ordinals.Distinct().Count(), ordinals.Count);
        Assert.All(ordinals, o => Assert.InRange(o, 0, ConstructionSites.Roles(s.Phase, s.Small).Count - 1));
        Assert.All(s.Machines, m => Assert.Equal(ConstructionSites.Roles(s.Phase, s.Small)[m.Ordinal].Role, m.Role));
    }

    [Fact]
    public void The_roles_keep_their_numbers()
    {
        // the dormant provider (#616) names a slot by its role and ordinal: append only
        Assert.Equal(0, (int)MachineRole.Excavator);
        Assert.Equal(4, (int)MachineRole.Telehandler);
        Assert.Equal(8, (int)MachineRole.Van);
    }

    /// <summary>The way a yaw faces in plan (x, z): yaw 0 faces −Z.</summary>
    private static Vector2 Facing(float yaw) => new(-MathF.Sin(yaw), -MathF.Cos(yaw));

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }
}
