using UnitSport.BattleRoyale;
using UnitSport.Build;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Battle Royale's ready-made structures (#276) are made of real pieces that stand by the building rules.</summary>
public class BrPrefabsTests
{
    public static IEnumerable<object[]> Prefabs => BrPrefabs.All.Select(p => new object[] { p.Name });

    private static Prefab Get(string name) => BrPrefabs.All.Single(p => p.Name == name);

    [Theory, MemberData(nameof(Prefabs))]
    public void Every_piece_is_valid_and_has_its_own_slot(string name)
    {
        var p = Get(name);
        Assert.NotEmpty(p.Pieces);
        Assert.Equal(p.Pieces.Length, p.Pieces.Select(q => q.Slot).Distinct().Count());
        Assert.All(p.Pieces, q => Assert.Equal(BuildGrid.ClassOf(q.Kind), q.Slot.Class));
        Assert.All(p.Pieces, q => Assert.True(BuildGrid.Allowed(q.Kind, q.Material), $"{q.Kind} in {q.Material}"));
        Assert.Contains(p.Pieces, q => q.Grounded);
        Assert.True(p.Pieces.Length <= BuildGrid.MaxPiecesMatch);
    }

    [Theory, MemberData(nameof(Prefabs))]
    public void It_stands(string name)
    {
        var fallen = BuildGrid.Fallen(Get(name).Pieces);
        Assert.True(fallen.Count == 0, $"{name}: {string.Join(", ", fallen.Select(f => $"{f.Kind} {f.Slot}"))} would fall");
    }

    [Fact]
    public void The_lookout_tower_is_tall_and_has_a_zipline_on_top()
    {
        var t = BrPrefabs.LookoutTower;
        Assert.Equal(6, t.Pieces.Max(q => q.Slot.Y));
        var zip = Assert.Single(t.Gadgets);
        Assert.Equal(PrefabGadget.Zipline, zip.Kind);
        Assert.Equal(6 * BuildGrid.Storey, zip.Y, 3);
    }

    [Fact]
    public void Breaking_a_bridge_end_leaves_the_rest_on_the_other_bank()
    {
        var deck = BrPrefabs.SuspensionBridge.Pieces.ToList();
        deck.RemoveAll(q => q.Slot == Slot.Floor(0, 0, 0));
        // the far bank (cell 11) still carries cells 5-10 (wood spans 6); cells 1-4 fall
        var fallen = BuildGrid.Fallen(deck).Where(q => q.Kind == PieceKind.Floor).Select(q => q.Slot.X).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, fallen);
    }

    [Fact]
    public void The_checkpoint_leaves_the_road_open()
    {
        var c = BrPrefabs.Checkpoint;
        Assert.DoesNotContain(c.Pieces, q => q.Slot == Slot.Edge(0, 0, -1, 0));
        Assert.DoesNotContain(c.Pieces, q => q.Slot == Slot.Edge(0, 0, 1, 2));
    }
}
