using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Layout;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Which approaches get heads over their lanes (#1037, <see cref="OverheadPlanner"/>): Stadt Bern's Handbuch LSA (2025) layouts
/// Abb. 12-19 as cases, on 3 m lanes, the kerb heads half a metre out of the kerb; only on a street with 2+ lanes of its own
/// toward the junction (the user's choice).
/// </summary>
public class OverheadPlannerTests
{
    private const SignalMoves L = SignalMoves.Left, T = SignalMoves.Through, R = SignalMoves.Right;

    /// <summary>A section seen driving in: <paramref name="outLanes"/> leaving lanes, an island's width (0 none), the arriving lanes.</summary>
    private static (Section Sec, double Right, double Left) Approach(int outLanes, double island, params SignalMoves[] lanes)
    {
        var strips = new List<Strip>();
        for (int i = 0; i < outLanes; i++) strips.Add(new Strip(StripKind.CarLane, 3, StripWay.Out));
        if (island > 0) strips.Add(new Strip(StripKind.Hatch, island));
        foreach (var m in lanes) strips.Add(new Strip(StripKind.CarLane, 3, StripWay.In, m));
        double left = -(outLanes * 3 + island);
        var sec = new Section(strips, left);
        return (sec, sec.CarriagewayRight + 0.5, left - 0.5);
    }

    private static List<OverheadSpot> Heads(int outLanes, double island, bool sideRoad, int streetLanes, params SignalMoves[] lanes)
    {
        var (sec, right, left) = Approach(outLanes, island, lanes);
        // the left head on the island's middle where there is one, else at the left kerb
        double leftHead = island > 0 ? -island * 0.5 : left;
        return OverheadPlanner.Heads(sec, streetLanes, right, leftHead, island > 0, sideRoad);
    }

    [Fact]
    public void Main_road_two_lanes_without_island_both_heads_over_the_lanes()
    {
        // Abb. 14: a 2+2 main road, its left lane's head across both oncoming lanes: the left arrow and the main head overhead
        var heads = Heads(2, 0, sideRoad: false, streetLanes: 2, L, T | R);
        Assert.Equal([(1.5, L), (4.5, T | R)], heads.Select(h => (h.Offset, h.Moves)));
    }

    [Fact]
    public void Main_road_two_lanes_with_island_keeps_its_poles()
    {
        // Abb. 12/13: the island's pole beside the left lane
        Assert.Empty(Heads(2, 2, sideRoad: false, streetLanes: 2, L, T | R));
    }

    [Fact]
    public void Side_road_two_lanes_without_island_overhead()
    {
        // Abb. 18: a side road of two lanes without an island; on a 1+1 road the left kerb is near (Abb. 14 would keep poles)
        Assert.Equal(2, Heads(1, 0, sideRoad: true, streetLanes: 2, L, T | R).Count);
        Assert.Empty(Heads(1, 0, sideRoad: false, streetLanes: 2, L, T | R));
    }

    [Fact]
    public void Three_lanes_left_and_through_overhead_the_kerb_lane_keeps_its_pole()
    {
        // Abb. 19: three lanes on a side road with an island: the left arrow and the through head overhead, the kerb lane's below
        var heads = Heads(2, 2, sideRoad: true, streetLanes: 3, L, T, T | R);
        Assert.Equal([(1.5, L), (4.5, T)], heads.Select(h => (h.Offset, h.Moves)));
    }

    [Fact]
    public void A_double_left_has_one_head_over_both_lanes()
    {
        // Abb. 12: a turn on two lanes is repeated over the road, its head between them
        var heads = Heads(2, 0, sideRoad: false, streetLanes: 3, L, L, T | R);
        Assert.Equal([(3.0, L)], heads.Select(h => (h.Offset, h.Moves)));
    }

    [Fact]
    public void A_one_lane_street_keeps_its_poles_whatever_its_pockets()
    {
        // the user (Oct 21 2026): pockets do not count; a 1+1 road widened to three lanes at the lights keeps its kerb poles
        Assert.Empty(Heads(1, 0, sideRoad: true, streetLanes: 1, L, T, R));
    }

    [Fact]
    public void A_right_turn_lane_at_the_kerb_keeps_its_kerb_head()
    {
        var heads = Heads(2, 0, sideRoad: false, streetLanes: 2, L, T, R);
        Assert.DoesNotContain(heads, h => h.Moves == R);
    }
}
