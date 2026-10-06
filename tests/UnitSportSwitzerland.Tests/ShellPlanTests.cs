using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="ShellPlans.Plan"/> (#608) builds a site's half-built structure as boxes and ramps.
/// What matters is that a player can walk it: from the ground floor up every flight to the top
/// slab, with headroom over each flight, a landing to step onto at every level, the well open in
/// every slab, nothing solid where a crane's mast stands, and a scaffold lift at every slab.
/// </summary>
public class ShellPlanTests
{
    private static Building Box(float w, float d, float h, byte floors, float turn = 0f)
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

    private static (ConstructionSite Site, ShellPlan Shell) Make(string key, float w, float d, float h, byte floors,
        IReadOnlyList<Rect2D>? wings = null, float turn = 0f, Func<Vector2, bool>? blocked = null)
    {
        var b = Box(w, d, h, floors, turn);
        var site = ConstructionSites.Plan(key, b, PlanBox.Of(b)!.Value, new Vector2(500, 560), blocked ?? (_ => false))!;
        return (site, ShellPlans.Plan(site, wings, 0.4f, 0f));
    }

    /// <summary>Whether a point is inside any solid box.</summary>
    private static bool InSolid(ShellPlan s, Vector3 p) =>
        s.Boxes.Any(b => b.Solid && p.X > b.Min.X && p.X < b.Max.X && p.Y > b.Min.Y && p.Y < b.Max.Y && p.Z > b.Min.Z && p.Z < b.Max.Z);

    /// <summary>Whether a solid box's top is at <paramref name="y"/> under (x, z): something to stand on.</summary>
    private static bool StandOn(ShellPlan s, float x, float z, float y) =>
        s.Boxes.Any(b => b.Solid && x > b.Min.X && x < b.Max.X && z > b.Min.Z && z < b.Max.Z && MathF.Abs(b.Max.Y - y) < 0.02f);

    public static IEnumerable<object[]> Sites() => new[]
    {
        new object[] { "2500_1120_1", 24f, 15f, 9.3f, (byte)4 },   // a shell, three storeys up
        new object[] { "2500_1120_2", 40f, 18f, 18.4f, (byte)6 },  // topped out
        new object[] { "2500_1120_3", 14f, 11f, 6.3f, (byte)2 },   // a house, topped out
        new object[] { "2500_1120_4", 30f, 20f, 1.2f, (byte)5 },   // foundations
    };

    [Theory]
    [MemberData(nameof(Sites))]
    public void Every_storey_has_a_flight_to_the_next_and_a_landing_to_step_onto(string key, float w, float d, float h, byte floors)
    {
        var (site, shell) = Make(key, w, d, h, floors);
        int storeys = shell.Levels.Count - 1;
        Assert.Equal(site.Phase == SitePhase.Foundations ? 0 : site.BuiltStoreys, storeys);
        Assert.Equal(2 * storeys, shell.Ramps.Count);
        for (int s = 0; s < storeys; s++)
        {
            var up = shell.Ramps[2 * s];
            var back = shell.Ramps[2 * s + 1];
            // the first flight leaves the floor, the second arrives at the next one, and they meet
            Assert.Equal(shell.Levels[s], Math.Min(up.A.Y, up.C.Y), 3);
            Assert.Equal(shell.Levels[s + 1], Math.Max(back.A.Y, back.C.Y), 3);
            Assert.Equal(Math.Max(up.A.Y, up.C.Y), Math.Min(back.A.Y, back.C.Y), 3);
            // not too steep for a body: 52 degrees is the floor limit, a stair is far under it
            foreach (var r in new[] { up, back })
            {
                float run = new Vector2(r.C.X - r.B.X, r.C.Z - r.B.Z).Length(), rise = MathF.Abs(r.C.Y - r.B.Y);
                Assert.True(MathF.Atan2(rise, run) < Mathf.DegToRad(35f), $"{key} storey {s}: {Mathf.RadToDeg(MathF.Atan2(rise, run)):F0} deg");
            }
            // the second flight arrives on a landing that is slab at the next level
            var arrive = back.C.Y > back.A.Y ? (back.C + back.D) / 2 : (back.A + back.B) / 2;
            var dirIn = new Vector2(arrive.X, arrive.Z) - new Vector2((back.A.X + back.B.X + back.C.X + back.D.X) / 4, (back.A.Z + back.B.Z + back.C.Z + back.D.Z) / 4);
            var onLanding = new Vector2(arrive.X, arrive.Z) + dirIn.Normalized() * 0.5f;
            Assert.True(StandOn(shell, onLanding.X, onLanding.Y, shell.Levels[s + 1]), $"{key}: no landing at level {s + 1}");
        }
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void Every_flight_has_headroom(string key, float w, float d, float h, byte floors)
    {
        var (_, shell) = Make(key, w, d, h, floors);
        foreach (var r in shell.Ramps)
            for (float t = 0.15f; t <= 0.85f; t += 0.1f)
                for (float across = 0.3f; across <= 0.7f; across += 0.2f)
                {
                    // a point on the ramp, and a standing body over it
                    var low = r.A.Lerp(r.B, across);
                    var high = r.D.Lerp(r.C, across);
                    var p = low.Lerp(high, t);
                    for (float up = 0.2f; up <= 1.9f; up += 0.25f)
                        Assert.False(InSolid(shell, p + Vector3.Up * up), $"{key}: something solid {up:F2} m over the flight at {p}");
                }
    }

    [Theory]
    // a long thin block hemmed in by neighbours: two cranes stand inside, a quarter from each end (#606)
    [InlineData(80f)]
    // a shorter one: one crane, inside, in the middle, where the stair core goes first (#610)
    [InlineData(30f)]
    public void A_crane_in_the_building_has_its_mast_open_through_every_slab(float length)
    {
        bool Blocked(Vector2 p) => Math.Abs(p.Y - 500) > 9.5f && Math.Abs(p.Y - 500) < 30f && Math.Abs(p.X - 500) < 60f;
        var (site, shell) = Make("2503_1120_156", length, 14f, 9.4f, 6, blocked: Blocked);
        Assert.Contains(site.Cranes, c => c.Inside);
        foreach (var c in site.Cranes.Where(c => c.Inside))
        {
            var d = c.Base - site.Box.Center;
            float x = d.Dot(site.Box.AxisU), z = d.Dot(site.Box.AxisV);
            for (int k = 1; k < shell.Levels.Count; k++)
                Assert.False(InSolid(shell, new Vector3(x, shell.Levels[k] - 0.1f, z)), $"slab {k} over the crane mast");
            // and the stair core is not where the mast is (#610: the core took the middle the crane wanted)
            var hole = new Rect2D(x - ShellPlans.CraneHole / 2, z - ShellPlans.CraneHole / 2, x + ShellPlans.CraneHole / 2, z + ShellPlans.CraneHole / 2);
            Assert.False(shell.Core.Overlaps(hole), $"the stair core {shell.Core} on the crane's mast at {x:F1},{z:F1}");
        }
    }

    [Fact]
    public void The_shell_follows_its_wings()
    {
        // an L: a 30 x 10 wing and a 10 x 14 wing down from its end, inside a 30 x 24 box
        var wings = new[] { new Rect2D(-15, -12, 15, -2), new Rect2D(5, -2, 15, 12) };
        var (_, shell) = Make("2500_1120_9", 30f, 24f, 9.2f, 4, wings);
        float top = shell.Levels[^1];
        // the notch of the L is open ground: no slab over it at any level
        for (int k = 1; k < shell.Levels.Count; k++)
            Assert.False(InSolid(shell, new Vector3(-5, shell.Levels[k] - 0.1f, 6)), $"a slab in the notch at level {k}");
        // and both wings have their slabs
        Assert.True(InSolid(shell, new Vector3(-10, top - 0.1f, -7)) || InSolid(shell, new Vector3(-9, top - 0.1f, -6)));
        Assert.True(InSolid(shell, new Vector3(10, top - 0.1f, 8)));
        // no wall where the two wings meet: the bay at z = -2 under the second wing is inside
        for (int k = 0; k + 1 < shell.Levels.Count; k++)
            Assert.False(InSolid(shell, new Vector3(10, shell.Levels[k] + 1.5f, -2.1f)), $"a wall between the wings at storey {k}");
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void A_scaffold_lift_at_every_slab_and_none_at_the_foundations(string key, float w, float d, float h, byte floors)
    {
        var (site, shell) = Make(key, w, d, h, floors);
        var decks = shell.Boxes.Where(b => b.Part == ShellPart.Deck).ToList();
        if (site.Phase == SitePhase.Foundations)
        {
            Assert.Empty(decks);
            return;
        }
        for (int k = 1; k < shell.Levels.Count; k++)
            Assert.Contains(decks, b => MathF.Abs(b.Max.Y - shell.Levels[k]) < 0.01f);
        // and every deck has a solid guard on its outer edge at its height
        Assert.All(decks, deck => Assert.Contains(shell.Boxes, g => g.Solid && g.Min.Y >= deck.Max.Y - 0.01f
            && g.Max.Y - g.Min.Y > 1f && g.Max.X >= deck.Min.X && g.Min.X <= deck.Max.X && g.Max.Z >= deck.Min.Z && g.Min.Z <= deck.Max.Z));
    }

    [Fact]
    public void The_same_site_always_gives_the_same_shell()
    {
        var (_, a) = Make("2537_1154_266", 34f, 20f, 12.1f, 6, turn: 0.5f);
        var (_, b) = Make("2537_1154_266", 34f, 20f, 12.1f, 6, turn: 0.5f);
        Assert.Equal(a.Boxes, b.Boxes);
        Assert.Equal(a.Ramps, b.Ramps);
    }

    [Fact]
    public void Subtracting_holes_keeps_the_rest_of_the_area()
    {
        var r = new Rect2D(0, 0, 20, 10);
        var holes = new[] { new Rect2D(4, 2, 7, 5), new Rect2D(12, -1, 15, 3), new Rect2D(30, 30, 31, 31) };
        var parts = ShellPlans.Subtract(r, holes);
        float area = parts.Sum(p => p.Width * p.Depth);
        Assert.Equal(200f - 9f - 9f, area, 2);
        foreach (var p in parts)
            foreach (var h in holes)
                Assert.False(p.Overlaps(h));
    }
}
