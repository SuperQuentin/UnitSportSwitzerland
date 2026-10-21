using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Roof overhangs for Cartoon's High mesh detail (#1067): which roof edges get a strip, how far it
/// reaches and where the strips meet at the corners.
/// </summary>
public class RoofOverhangsTests
{
    private static readonly RoofOverhangs.Reach Reach = new(0.7f, 0.4f);

    private sealed class Solid
    {
        public readonly List<float> T = new();
        public void Tri(Vector3 a, Vector3 b, Vector3 c) => T.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Tri(a, b, c); Tri(a, c, d); }
        public Building Build(float top) => new() { Kind = BuildingKind.House, MinY = 0, MaxY = top, Triangles = T.ToArray() };
    }

    // a gabled box, ridge along x: walls to the eave, two gable ends, two roof faces
    private static Building Gabled(float x0, float x1, float z0, float z1, float eave, float ridge)
    {
        var s = new Solid();
        float zc = (z0 + z1) / 2;
        s.Quad(new(x0, 0, z1), new(x1, 0, z1), new(x1, eave, z1), new(x0, eave, z1));
        s.Quad(new(x1, 0, z0), new(x0, 0, z0), new(x0, eave, z0), new(x1, eave, z0));
        s.Quad(new(x0, 0, z0), new(x0, 0, z1), new(x0, eave, z1), new(x0, eave, z0));
        s.Tri(new(x0, eave, z0), new(x0, eave, z1), new(x0, ridge, zc));
        s.Quad(new(x1, 0, z1), new(x1, 0, z0), new(x1, eave, z0), new(x1, eave, z1));
        s.Tri(new(x1, eave, z1), new(x1, eave, z0), new(x1, ridge, zc));
        s.Quad(new(x0, eave, z1), new(x1, eave, z1), new(x1, ridge, zc), new(x0, ridge, zc));
        s.Quad(new(x1, eave, z0), new(x0, eave, z0), new(x0, ridge, zc), new(x1, ridge, zc));
        return s.Build(ridge);
    }

    private static BuildingTile Tile(params Building[] b) => new() { Id = new TileId(0, 0), Buildings = b.ToList() };

    private static List<RoofOverhangs.Strip> Plan(BuildingTile tile, int index) =>
        RoofOverhangs.Plan(tile.Buildings[index], index, Reach, new RoofCover(tile, _ => true));

    [Fact]
    public void A_gabled_house_gets_two_eaves_and_four_verges_but_nothing_along_the_ridge()
    {
        var strips = Plan(Tile(Gabled(0, 10, 0, 8, 6, 9)), 0);
        Assert.Equal(6, strips.Count);
        // the eaves run along x at the eave height; nothing at the ridge's height
        Assert.Equal(2, strips.Count(s => Mathf.IsEqualApprox(s.P.Y, 6f) && Mathf.IsEqualApprox(s.Q.Y, 6f)));
        Assert.DoesNotContain(strips, s => Mathf.IsEqualApprox(s.P.Y, 9f) && Mathf.IsEqualApprox(s.Q.Y, 9f));
    }

    [Fact]
    public void The_eave_and_the_verge_meet_at_one_corner_past_the_walls()
    {
        var strips = Plan(Tile(Gabled(0, 10, 0, 8, 6, 9)), 0);
        // the south-west corner (0, 6, 8): every strip ending there ends at the same point
        var corner = new Vector3(0, 6, 8);
        var ends = strips.SelectMany(s => new[] { (s.P, s.OuterP), (s.Q, s.OuterQ) })
            .Where(e => e.Item1.DistanceTo(corner) < 1e-3f).Select(e => e.Item2).ToList();
        Assert.Equal(2, ends.Count);
        Assert.True(ends[0].DistanceTo(ends[1]) < 1e-3f, $"{ends[0]} vs {ends[1]}");
        // west of the gable by the verge's reach (horizontal), south of and below the eave
        Assert.Equal(-Reach.Verge, ends[0].X, 3);
        Assert.True(ends[0].Z > 8.3f && ends[0].Y < 6f, ends[0].ToString());
        // 0.7 m down the slope: the slope is 3 m up over 4 m across
        Assert.Equal(8f + 0.7f * 0.8f, ends[0].Z, 3);
        Assert.Equal(6f - 0.7f * 0.6f, ends[0].Y, 3);
    }

    [Fact]
    public void Verges_meet_at_the_ridge_of_the_gable()
    {
        var strips = Plan(Tile(Gabled(0, 10, 0, 8, 6, 9)), 0);
        var apex = new Vector3(0, 9, 4);
        var ends = strips.SelectMany(s => new[] { (s.P, s.OuterP), (s.Q, s.OuterQ) })
            .Where(e => e.Item1.DistanceTo(apex) < 1e-3f).Select(e => e.Item2).ToList();
        Assert.Equal(2, ends.Count);
        Assert.True(ends[0].DistanceTo(new Vector3(-Reach.Verge, 9, 4)) < 1e-3f, ends[0].ToString());
        Assert.True(ends[1].DistanceTo(new Vector3(-Reach.Verge, 9, 4)) < 1e-3f, ends[1].ToString());
    }

    [Fact]
    public void Terraced_houses_keep_no_verge_over_the_neighbours_roof()
    {
        var tile = Tile(Gabled(0, 10, 0, 8, 6, 9), Gabled(10, 20, 0, 8, 6, 9));
        var west = Plan(tile, 0);
        // the party wall at x = 10: no verge reaches past it, the far gable keeps its own
        Assert.DoesNotContain(west, s => s.OuterP.X > 10.01f || s.OuterQ.X > 10.01f);
        Assert.Contains(west, s => s.OuterP.X < -0.3f);
        Assert.Equal(4, west.Count);
    }

    [Fact]
    public void A_lean_to_has_no_overhang_along_the_wall_it_leans_on()
    {
        // a shed's roof climbing from 2.5 m at x = 0 to 4 m at x = 3, against a wall there
        var s = new Solid();
        s.Quad(new(0, 2.5f, 0), new(0, 2.5f, 4), new(3, 4, 4), new(3, 4, 0));
        var strips = Plan(Tile(s.Build(4)), 0);
        Assert.Equal(3, strips.Count);
        Assert.DoesNotContain(strips, st => Mathf.IsEqualApprox(st.P.X, 3f) && Mathf.IsEqualApprox(st.Q.X, 3f));
    }

    [Fact]
    public void A_flat_roof_gets_none()
    {
        var s = new Solid();
        s.Quad(new(0, 5, 0), new(10, 5, 0), new(10, 5, 8), new(0, 5, 8));
        Assert.Empty(Plan(Tile(s.Build(5)), 0));
    }
}
