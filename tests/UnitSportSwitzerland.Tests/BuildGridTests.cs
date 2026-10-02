using UnitSport.Build;
using UnitSport.Items;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Building rules (#274): slots, support and collapse, costs.</summary>
public class BuildGridTests
{
    private static Piece Floor(int x, int y, int z, bool grounded = false, BuildMaterial m = BuildMaterial.Wood) =>
        new(Slot.Floor(x, y, z), PieceKind.Floor, 0, m, grounded);

    private static Piece Wall(int x, int y, int z, int side, bool grounded = false, BuildMaterial m = BuildMaterial.Wood,
        PieceKind kind = PieceKind.Wall) =>
        new(Slot.Edge(x, y, z, side), kind, 0, m, grounded);

    [Fact]
    public void An_edge_between_two_cells_has_one_key()
    {
        Assert.Equal(Slot.Edge(0, 0, 0, 1), Slot.Edge(1, 0, 0, 3));
        Assert.Equal(Slot.Edge(0, 0, 0, 2), Slot.Edge(0, 0, 1, 0));
        Assert.NotEqual(Slot.Edge(0, 0, 0, 0), Slot.Edge(0, 0, 0, 3));
    }

    [Fact]
    public void A_box_on_a_grounded_floor_stands()
    {
        var pieces = new List<Piece> { Floor(0, 0, 0, grounded: true) };
        for (int s = 0; s < 4; s++) pieces.Add(Wall(0, 0, 0, s));
        pieces.Add(Floor(0, 1, 0));   // the ceiling rests on the walls' tops
        Assert.Empty(BuildGrid.Fallen(pieces));
        Assert.Equal(0, BuildGrid.Support(pieces)[Slot.Floor(0, 1, 0)]);
    }

    [Fact]
    public void Floors_reach_out_as_far_as_their_material_spans()
    {
        // wood spans 6: floors 1..6 out from a grounded one stand, the 7th does not
        var pieces = new List<Piece> { Floor(0, 0, 0, grounded: true) };
        for (int x = 1; x <= 7; x++) pieces.Add(Floor(x, 0, 0));
        var fallen = BuildGrid.Fallen(pieces);
        Assert.Single(fallen);
        Assert.Equal(Slot.Floor(7, 0, 0), fallen[0].Slot);
    }

    [Fact]
    public void Stone_spans_less_than_metal()
    {
        Assert.True(BuildGrid.Spec(BuildMaterial.Stone).Span < BuildGrid.Spec(BuildMaterial.Wood).Span);
        Assert.True(BuildGrid.Spec(BuildMaterial.Metal).Span > BuildGrid.Spec(BuildMaterial.Wood).Span);
    }

    [Fact]
    public void Breaking_the_support_brings_down_what_it_held()
    {
        // a tower: grounded floor, wall, floor on top, wall on that floor
        var floor = Floor(0, 0, 0, grounded: true);
        var low = Wall(0, 0, 0, 0);
        var mid = Floor(0, 1, 0);
        var high = Wall(0, 1, 0, 0);
        var pieces = new List<Piece> { floor, low, mid, high };
        Assert.Empty(BuildGrid.Fallen(pieces));

        pieces.Remove(floor);
        var fallen = BuildGrid.Fallen(pieces).Select(p => p.Slot).ToHashSet();
        Assert.Equal(new[] { low.Slot, mid.Slot, high.Slot }.ToHashSet(), fallen);
    }

    [Fact]
    public void A_corner_wall_holds_its_neighbour()
    {
        var pieces = new List<Piece>
        {
            Wall(0, 0, 0, 0, grounded: true),
            Wall(0, 0, 0, 3),   // shares the corner with the grounded wall
        };
        Assert.Empty(BuildGrid.Fallen(pieces));
    }

    [Fact]
    public void Stairs_rest_on_a_floor_and_hold_the_next_one()
    {
        var pieces = new List<Piece>
        {
            Floor(0, 0, 0, grounded: true),
            new(Slot.Volume(0, 0, 0), PieceKind.Stairs, 2, BuildMaterial.Wood, false),   // climbs toward +Z
            Floor(0, 1, 1),   // the landing at the top, beyond the stairs' high edge
        };
        var support = BuildGrid.Support(pieces);
        Assert.Equal(3, support.Count);
        Assert.Equal(0, support[Slot.Floor(0, 1, 1)]);
    }

    [Fact]
    public void A_pillar_carries_a_floor()
    {
        var pieces = new List<Piece>
        {
            Floor(0, 0, 0, grounded: true),
            new(Slot.Volume(0, 0, 0), PieceKind.Pillar, 0, BuildMaterial.Stone, false),
            Floor(0, 1, 0, m: BuildMaterial.Stone),
        };
        Assert.Equal(0, BuildGrid.Support(pieces)[Slot.Floor(0, 1, 0)]);
    }

    [Fact]
    public void Placing_checks_slot_material_and_support()
    {
        var pieces = new List<Piece> { Floor(0, 0, 0, grounded: true) };
        Assert.Null(BuildGrid.CannotPlace(pieces, Wall(0, 0, 0, 1)));
        Assert.NotNull(BuildGrid.CannotPlace(pieces, Floor(0, 0, 0)));                        // taken
        Assert.NotNull(BuildGrid.CannotPlace(pieces, Floor(0, 3, 0)));                        // floating
        Assert.NotNull(BuildGrid.CannotPlace(pieces, Floor(1, 0, 0, m: BuildMaterial.Sandbag)));   // sandbags make walls only
        Assert.Null(BuildGrid.CannotPlace(pieces, Wall(0, 0, 0, 1, m: BuildMaterial.Sandbag, kind: PieceKind.HalfWall)));
        var wrongClass = new Piece(Slot.Floor(2, 0, 0), PieceKind.Wall, 0, BuildMaterial.Wood, true);
        Assert.NotNull(BuildGrid.CannotPlace(pieces, wrongClass));
    }

    [Fact]
    public void A_half_wall_holds_nothing_on_top()
    {
        var pieces = new List<Piece>
        {
            Wall(0, 0, 0, 0, grounded: true, kind: PieceKind.HalfWall),
            Wall(0, 1, 0, 0),
        };
        Assert.Single(BuildGrid.Fallen(pieces));
    }

    [Fact]
    public void Costs_are_real_items_and_small_pieces_cost_less()
    {
        foreach (var m in BuildGrid.Materials)
        {
            Assert.All(m.Cost, c => Assert.True(c.Count > 0 && c.Id != ItemId.None));
            foreach (var k in BuildGrid.Kinds)
                Assert.True(BuildGrid.Cost(k, m.Material).All(c => c.Count > 0));
        }
        Assert.Equal(3, BuildGrid.Cost(PieceKind.HalfWall, BuildMaterial.Wood).Single().Count);
        Assert.Equal(5, BuildGrid.Cost(PieceKind.Wall, BuildMaterial.Wood).Single().Count);
    }

    [Fact]
    public void A_piece_grows_to_full_strength()
    {
        float max = BuildGrid.MaxHp(PieceKind.Wall, BuildMaterial.Stone);
        Assert.Equal(max * 0.1f, BuildGrid.GrownHp(PieceKind.Wall, BuildMaterial.Stone, 0), 3);
        Assert.Equal(max, BuildGrid.GrownHp(PieceKind.Wall, BuildMaterial.Stone, 100), 3);
    }
}
