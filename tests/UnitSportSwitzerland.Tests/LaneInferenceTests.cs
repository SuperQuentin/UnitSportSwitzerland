using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The lanes of a multi-lane approach without OSM turn:lanes (#700, #711, <see cref="TileRewriter.InferredLanes"/>): OSM's lane
/// count is the truth, shared out by the lanes each turn's exit takes away; a turn with no lane of its own shares the outermost
/// through lane; a forbidden turn (left out of <c>allowed</c>) gets none.
/// </summary>
public class LaneInferenceTests
{
    private const SignalMoves L = SignalMoves.Left, T = SignalMoves.Through, R = SignalMoves.Right;

    [Fact]
    public void Two_lanes_into_a_two_lane_exit_both_go_straight_the_outer_ones_also_turn()
    {
        // Sion's 2+1 approach into a 2+1 exit: the leftmost also left, the rightmost also right
        Assert.Equal([L | T, T | R], TileRewriter.InferredLanes(L | T | R, 2, outLeft: 1, outThrough: 2, outRight: 1));
    }

    [Fact]
    public void Two_lanes_into_a_one_lane_exit_give_a_left_turn_lane()
    {
        Assert.Equal([L, T | R], TileRewriter.InferredLanes(L | T | R, 2, outLeft: 1, outThrough: 1, outRight: 1));
    }

    [Fact]
    public void Three_lanes_into_one_give_a_lane_per_turn()
    {
        Assert.Equal([L, T, R], TileRewriter.InferredLanes(L | T | R, 3, outLeft: 1, outThrough: 1, outRight: 1));
    }

    [Fact]
    public void A_two_lane_side_road_takes_two_left_lanes()
    {
        Assert.Equal([L, L, T | R], TileRewriter.InferredLanes(L | T | R, 3, outLeft: 2, outThrough: 1, outRight: 1));
    }

    [Fact]
    public void A_forbidden_turn_gets_no_lane()
    {
        // no left turn (an OSM restriction): the spare lane turns right
        Assert.Equal([T, R], TileRewriter.InferredLanes(T | R, 2, outThrough: 1, outRight: 1));
        // nothing to the right either: both go straight on, the lanes merge after the junction
        Assert.Equal([T, T], TileRewriter.InferredLanes(T, 2, outThrough: 1));
    }

    [Fact]
    public void Exits_unknown_keep_the_old_rule()
    {
        // #700's rule: straight on in every lane, the leftmost also left, the rightmost also right
        Assert.Equal([L | T, T, T | R], TileRewriter.InferredLanes(L | T | R, 3));
    }

    [Fact]
    public void The_stem_of_a_tee_splits_by_the_exits()
    {
        Assert.Equal([L, R], TileRewriter.InferredLanes(L | R, 2));
        Assert.Equal([L, L, R], TileRewriter.InferredLanes(L | R, 3, outLeft: 2, outRight: 1));
        Assert.Equal([L, R, R], TileRewriter.InferredLanes(L | R, 3, outLeft: 1, outRight: 2));
    }
}
