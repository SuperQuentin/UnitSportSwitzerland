using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Layout;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Which approaches get heads over their lanes (#1037, <see cref="OverheadPlanner"/>): Stadt Bern's Handbuch LSA (2025) layouts
/// Abb. 12-19 as cases, on 3 m lanes, the kerb heads half a metre out of the kerb; only on a street with 2+ lanes of its own
/// toward the junction (the user's choice). The heads form one cluster over the middle of the approach's lanes, a head per
/// direction (#1110, the user: no more than one light per direction overhead), <see cref="SignalOverhead.ClusterPitch"/> apart.
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

    /// <summary>The heads as (offset to the centimetre, moves).</summary>
    private static List<(double, SignalMoves)> Cluster(List<OverheadSpot> heads) => heads.Select(h => (Math.Round(h.Offset, 2), h.Moves)).ToList();

    private const double P = SignalOverhead.ClusterPitch;

    [Fact]
    public void Main_road_two_lanes_without_island_both_heads_over_the_lanes()
    {
        // Abb. 14: a 2+2 main road, its left lane's head across both oncoming lanes: the left arrow and the main head overhead, side
        // by side over the middle of the two lanes (3 m)
        var heads = Heads(2, 0, sideRoad: false, streetLanes: 2, L, T | R);
        Assert.Equal([(Math.Round(3 - P / 2, 2), L), (Math.Round(3 + P / 2, 2), T | R)], Cluster(heads));
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
    public void Three_lanes_one_head_per_direction_over_their_middle()
    {
        // Abb. 19: three lanes on a side road with an island go overhead; the through and the through-and-right lane share the main
        // head (#1110: one light per direction), the left arrow beside it, over the middle of the lanes (4.5 m)
        var heads = Heads(2, 2, sideRoad: true, streetLanes: 3, L, T, T | R);
        Assert.Equal([(Math.Round(4.5 - P / 2, 2), L), (Math.Round(4.5 + P / 2, 2), T | R)], Cluster(heads));
    }

    [Fact]
    public void A_double_left_has_one_left_head_in_the_cluster()
    {
        // Abb. 12: a turn on two lanes is repeated over the road; one head for the direction, not one per lane (#1110)
        var heads = Heads(2, 0, sideRoad: false, streetLanes: 3, L, L, T | R);
        Assert.Equal([(Math.Round(4.5 - P / 2, 2), L), (Math.Round(4.5 + P / 2, 2), T | R)], Cluster(heads));
    }

    [Fact]
    public void A_one_lane_street_keeps_its_poles_whatever_its_pockets()
    {
        // the user (Oct 21 2026): pockets do not count; a 1+1 road widened to three lanes at the lights keeps its kerb poles
        Assert.Empty(Heads(1, 0, sideRoad: true, streetLanes: 1, L, T, R));
    }

    [Fact]
    public void Every_direction_has_its_head_the_right_turn_too()
    {
        // left, through and right lanes: three heads side by side, the main one over the middle
        var heads = Heads(2, 0, sideRoad: false, streetLanes: 2, L, T, R);
        Assert.Equal([(Math.Round(4.5 - P, 2), L), (4.5, T), (Math.Round(4.5 + P, 2), R)], Cluster(heads));
    }
}
