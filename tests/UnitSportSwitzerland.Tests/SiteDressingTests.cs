using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="SiteDressings.Plan"/> (#609) dresses a site's yard. What matters: it is the same on
/// every peer, nothing solid stands in the building or its scaffolding, everything stands inside
/// the hoarding, the hoarding is there and solid, and the heap can be walked up.
/// </summary>
public class SiteDressingTests
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

    private static (ConstructionSite Site, SiteDressingPlan Yard) Make(string key, float w, float d, float h, byte floors, float turn = 0.3f)
    {
        var b = Box(w, d, h, floors, turn);
        var site = ConstructionSites.Plan(key, b, PlanBox.Of(b)!.Value, new Vector2(500, 560), _ => false)!;
        return (site, SiteDressings.Plan(site, (_, _) => 0f));
    }

    public static IEnumerable<object[]> Sites() => new[]
    {
        new object[] { "2585_1114_68", 24f, 15f, 1.2f, (byte)4 },   // foundations: a big heap
        new object[] { "2586_1115_51", 24f, 15f, 6.3f, (byte)4 },   // a shell
        new object[] { "2580_1112_61", 14f, 11f, 6.3f, (byte)2 },   // a house, topped out
        new object[] { "2582_1102_61", 40f, 18f, 18.4f, (byte)6 },  // a big block, topped out
    };

    [Theory]
    [MemberData(nameof(Sites))]
    public void Nothing_solid_stands_in_the_building_or_its_scaffolding(string key, float w, float d, float h, byte floors)
    {
        var (site, yard) = Make(key, w, d, h, floors);
        float hx = site.Box.Width / 2 + ConstructionSites.Scaffold, hz = site.Box.Depth / 2 + ConstructionSites.Scaffold;
        foreach (var b in yard.Boxes.Where(b => b.Solid))
            Assert.False(b.Max.X > -hx && b.Min.X < hx && b.Max.Z > -hz && b.Min.Z < hz,
                $"{key}: a solid {b.Part} from {b.Min} to {b.Max} in the building or its scaffolding");
        foreach (var m in yard.Mounds)
            Assert.False(MathF.Abs(m.Center.X) < hx + m.RadiusX - 0.5f && MathF.Abs(m.Center.Y) < hz + m.RadiusZ - 0.5f,
                $"{key}: a heap at {m.Center} in the building");
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void Everything_stands_inside_the_hoarding(string key, float w, float d, float h, byte floors)
    {
        var (site, yard) = Make(key, w, d, h, floors);
        foreach (var b in yard.Boxes)
        {
            var mid = (b.Min + b.Max) / 2;
            var world = site.Box.Center + site.Box.AxisU * mid.X + site.Box.AxisV * mid.Z;
            Assert.True(site.Area.Contains(world, 0.6f), $"{key}: a {b.Part} at {world} outside the site");
        }
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void The_hoarding_is_solid_along_every_run(string key, float w, float d, float h, byte floors)
    {
        var (site, yard) = Make(key, w, d, h, floors);
        float fence = yard.Boxes.Where(b => b.Part == ShellPart.Fence && b.Solid)
            .Sum(b => Math.Max(b.Max.X - b.Min.X, b.Max.Z - b.Min.Z));
        float runs = site.Hoarding.Sum(r => r.A.DistanceTo(r.B));
        Assert.Equal(runs, fence, 1);
        Assert.All(yard.Boxes.Where(b => b.Part == ShellPart.Fence), b => Assert.Equal(SiteDressings.FenceHeight, b.Max.Y, 2));
    }

    [Fact]
    public void A_site_at_its_foundations_has_a_heap_a_body_can_walk_up()
    {
        var (site, yard) = Make("2585_1114_68", 24f, 15f, 1.2f, 4);
        Assert.Equal(SitePhase.Foundations, site.Phase);
        var heap = Assert.Single(yard.Mounds);
        Assert.True(heap.Height > 1f, $"a heap {heap.Height:F1} m high");
        // the steepest stretch: from the foot to the shoulder (0.62 of the radius, 0.73 of the height)
        float run = Math.Min(heap.RadiusX, heap.RadiusZ) * (1f - 0.62f), rise = heap.Height * (0.58f + 0.15f);
        Assert.True(MathF.Atan2(rise, run) < Mathf.DegToRad(52f), $"{Mathf.RadToDeg(MathF.Atan2(rise, run)):F0} deg");
    }

    [Fact]
    public void The_site_office_is_containers_two_high()
    {
        var (site, yard) = Make("2582_1102_61", 40f, 18f, 18.4f, 6);
        var containers = yard.Boxes.Where(b => b.Part == ShellPart.Container).ToList();
        Assert.NotEmpty(containers);
        Assert.Equal(containers.Count(c => c.Min.Y < 0.1f), containers.Count(c => c.Min.Y > SiteDressings.ContainerHeight - 0.1f));
    }

    [Fact]
    public void The_same_site_is_always_dressed_alike()
    {
        var (_, a) = Make("2537_1154_266", 34f, 20f, 9.1f, 6);
        var (_, b) = Make("2537_1154_266", 34f, 20f, 9.1f, 6);
        Assert.Equal(a.Boxes, b.Boxes);
        Assert.Equal(a.Mounds, b.Mounds);
    }
}
