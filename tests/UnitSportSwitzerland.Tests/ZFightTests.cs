using Godot;
using UnitSport.Core;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The z-fight detector (src/Core/ZFight.cs, linked in, #788): coplanar same-facing overlaps only.</summary>
public class ZFightTests
{
    /// <summary>A horizontal square at height y, facing up, as two triangles.</summary>
    private static IEnumerable<ZFight.Tri> Square(float x0, float z0, float size, float y, int look, int depth = 0, bool down = false, bool twoSided = false)
    {
        var a = new Vector3(x0, y, z0);
        var b = new Vector3(x0 + size, y, z0);
        var c = new Vector3(x0 + size, y, z0 + size);
        var d = new Vector3(x0, y, z0 + size);
        // Godot's front face: clockwise as seen from the side it faces
        if (down)
        {
            yield return new ZFight.Tri(a, c, b, look, look, depth, twoSided);
            yield return new ZFight.Tri(a, d, c, look, look, depth, twoSided);
        }
        else
        {
            yield return new ZFight.Tri(a, b, c, look, look, depth, twoSided);
            yield return new ZFight.Tri(a, c, d, look, look, depth, twoSided);
        }
    }

    private static List<ZFight.Hit> Find(params IEnumerable<ZFight.Tri>[] parts) =>
        ZFight.Find(parts.SelectMany(p => p).ToList());

    [Fact]
    public void Coplanar_overlap_of_two_looks_fights_over_the_shared_area()
    {
        var hits = Find(Square(0, 0, 1, 0, look: 1), Square(0.5f, 0.5f, 1, 0.002f, look: 2));
        Assert.NotEmpty(hits);
        Assert.Equal(0.25f, hits.Sum(h => h.Area), 3);
        Assert.All(hits, h => Assert.InRange(h.Gap, 0.0019f, 0.0021f));
        Assert.All(hits, h => Assert.All(h.Region, p => Assert.InRange(p.Y, 0.0009f, 0.0011f)));   // midway
    }

    [Fact]
    public void A_centimetre_proud_is_clear() =>
        Assert.Empty(Find(Square(0, 0, 1, 0, look: 1), Square(0.25f, 0.25f, 0.5f, 0.01f, look: 2)));

    [Fact]
    public void Same_look_draws_the_same_pixels() =>
        Assert.Empty(Find(Square(0, 0, 1, 0, look: 1), Square(0.5f, 0.5f, 1, 0, look: 1)));

    [Fact]
    public void Different_depth_classes_are_biased_apart() =>
        Assert.Empty(Find(Square(0, 0, 1, 0, look: 1), Square(0, 0, 1, 0, look: 2, depth: 1)));

    [Fact]
    public void Faces_turned_away_only_fight_when_drawn_both_sides()
    {
        Assert.Empty(Find(Square(0, 0, 1, 0, look: 1), Square(0, 0, 1, 0, look: 2, down: true)));
        Assert.NotEmpty(Find(Square(0, 0, 1, 0, look: 1), Square(0, 0, 1, 0, look: 2, down: true, twoSided: true)));
    }

    [Fact]
    public void Neighbours_sharing_an_edge_do_not_fight() =>
        Assert.Empty(Find(Square(0, 0, 1, 0, look: 1), Square(1, 0, 1, 0, look: 2)));

    [Fact]
    public void Crossing_at_a_shallow_angle_fights_in_a_strip_round_the_crossing()
    {
        // a 2 m square tilted 2° about the z axis through x = 1: within 8 mm of y = 0 only for
        // |x - 1| < 0.008 / tan 2° (0.229 m), a 0.458 m strip, 2 m long
        float slope = Mathf.Tan(Mathf.DegToRad(2f));
        var tilted = Square(0, 0, 2, 0, look: 2).Select(t => t with
        {
            A = Tilt(t.A), B = Tilt(t.B), C = Tilt(t.C),
        });
        Vector3 Tilt(Vector3 p) => new(p.X, (p.X - 1) * slope, p.Z);
        var hits = Find(Square(0, 0, 2, 0, look: 1), tilted);
        Assert.Equal(2 * 2 * ZFight.MaxGap / slope, hits.Sum(h => h.Area), 2);
    }

    [Fact]
    public void A_steep_crossing_is_a_line_not_a_fight()
    {
        var steep = Square(0, 0, 1, 0, look: 2).Select(t => t with { A = Up(t.A), B = Up(t.B), C = Up(t.C) });
        Vector3 Up(Vector3 p) => new(p.X, (p.X - 0.5f) * 0.5f, p.Z);   // 26°
        Assert.Empty(Find(Square(0, 0, 1, 0, look: 1), steep));
    }

    [Fact]
    public void A_huge_triangle_is_still_tested()
    {
        // ground-sized: past the grid's cell budget, tested against everything
        var ground = new ZFight.Tri(new Vector3(-500, 0, -500), new Vector3(500, 0, -500), new Vector3(500, 0, 500), 0, 1);
        var hits = ZFight.Find(new[] { ground }.Concat(Square(3, 2, 0.1f, 0.001f, look: 2)).ToList());
        Assert.Equal(0.01f, hits.Sum(h => h.Area), 4);
    }

    [Fact]
    public void Each_pair_is_reported_once()
    {
        // small triangles over many cells: the two squares are 2 triangles each, at most 4 pairs
        var hits = Find(Square(0, 0, 1, 0, look: 1), Square(0, 0, 1, 0.001f, look: 2));
        Assert.True(hits.Count <= 4);
        Assert.Equal(1f, hits.Sum(h => h.Area), 3);
        Assert.Equal(hits.Count, hits.Select(h => (Math.Min(h.First, h.Second), Math.Max(h.First, h.Second))).Distinct().Count());
    }

    [Fact]
    public void Degenerate_triangles_are_skipped()
    {
        var line = new ZFight.Tri(Vector3.Zero, Vector3.Right, Vector3.Right * 2, 0, 2);
        Assert.Empty(ZFight.Find(Square(0, 0, 1, 0, look: 1).Append(line).ToList()));
    }

    [Fact]
    public void Visible_only_drops_a_fight_under_a_lid_but_not_under_glass()
    {
        var fight = Square(0, 0, 1, 0, look: 1).Concat(Square(0.25f, 0.25f, 0.5f, 0.002f, look: 2)).ToList();
        // a 4 m lid 1 m over the fight, facing down: every ray, straight or at 50 degrees, meets it
        var lid = Square(-1.5f, -1.5f, 4, 1, look: 3, down: true).ToList();
        Assert.NotEmpty(ZFight.Find(fight, visibleOnly: true));
        Assert.Empty(ZFight.Find(fight.Concat(lid).ToList(), visibleOnly: true));
        Assert.NotEmpty(ZFight.Find(fight.Concat(lid).ToList()));
        var glass = lid.Select(t => t with { Opaque = false });
        Assert.NotEmpty(ZFight.Find(fight.Concat(glass).ToList(), visibleOnly: true));
    }

    [Fact]
    public void Visible_only_sees_past_a_small_cover_at_an_angle()
    {
        var fight = Square(0, 0, 1, 0, look: 1).Concat(Square(0.25f, 0.25f, 0.5f, 0.002f, look: 2)).ToList();
        var cap = Square(0.4f, 0.4f, 0.2f, 0.5f, look: 3, down: true);
        Assert.NotEmpty(ZFight.Find(fight.Concat(cap).ToList(), visibleOnly: true));
    }
}
