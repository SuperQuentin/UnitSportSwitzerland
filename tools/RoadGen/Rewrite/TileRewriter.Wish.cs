namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// The lanes OSM gives an approach (#700): <c>turn:lanes</c> at the junction end of the arm's TLM
/// line, assigned to the lanes the carriageway holds and, where it holds fewer than the data
/// shows, built as left pockets. Plan: docs/plans/intersection-lanes.md.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>Most lanes a left pocket is built with from data (a triple left is as many as OSM maps).</summary>
    private const int MaxPocketLanes = 3;

    /// <summary>
    /// What an approach gets from its lane data: the moves of the left pocket's lanes to build and of the
    /// carriageway's own lanes (left to right), whether the right-most wished lane is a right pocket, and
    /// how many wished lanes did not fit (their moves are folded into the lane beside them).
    /// </summary>
    private sealed record LaneWish(SignalMoves[] Pocket, SignalMoves[] Own, bool RightPocket, int Folded);

    /// <summary>
    /// The lanes the OSM row at the junction end of <paramref name="link"/> shows, left to right, as the moves they
    /// allow among <paramref name="allowed"/> (a lane with no marked move, or none that the approach has, shows every
    /// one of them); null where the row has no <c>turn:lanes</c> for the traffic arriving at that end.
    /// </summary>
    private static SignalMoves[]? WishedLanes(OsmOverlayReader? overlay, RoadLink link, LinkEnd end, SignalMoves allowed)
    {
        if (overlay is null || link.Tag is not Source { Key: { } key } source || source.Plan.Length < 2) return null;
        bool atEnd = end == LinkEnd.End;
        double m = key.FromM + source.AlongOf(atEnd ? source.Plan[^1] : source.Plan[0]);
        if (overlay.AtEnd(key.Uuid, key.Part, m, atEnd) is not { } row) return null;
        var lanes = atEnd ? row.TurnLanesFwd : row.TurnLanesBwd;
        if (lanes.Length == 0 || allowed == SignalMoves.None) return null;
        var result = new SignalMoves[lanes.Length];
        for (int i = 0; i < lanes.Length; i++)
        {
            var moves = SignalMoves.None;
            if ((lanes[i] & TurnLanes.AnyLeft) != 0) moves |= SignalMoves.Left;
            if ((lanes[i] & TurnMove.Through) != 0) moves |= SignalMoves.Through;
            if ((lanes[i] & TurnLanes.AnyRight) != 0) moves |= SignalMoves.Right;
            moves &= allowed;
            result[i] = moves == SignalMoves.None ? allowed : moves;
        }
        return result;
    }

    /// <summary>
    /// Fits wished lanes onto an approach whose carriageway holds <paramref name="own"/> lanes toward the
    /// junction: the rightmost wished lanes are the carriageway's own, left of them the lanes that turn left are
    /// built as pockets (on a one-lane approach only), a right-only lane at the far right is a right pocket
    /// where one can be built; a wished lane that fits nowhere is folded into its neighbour. Null without data.
    /// </summary>
    private static LaneWish? Assign(SignalMoves[]? wish, int own, bool rightPocketPossible, bool leftPockets = true)
    {
        if (wish is null) return null;
        int m = wish.Length, left = 0;
        if (own == 1 && leftPockets)
            while (left < m - 1 && left < MaxPocketLanes && (wish[left] & SignalMoves.Left) != 0) left++;
        bool right = rightPocketPossible && m - left >= 2 && wish[m - 1] == SignalMoves.Right;
        var middle = wish[left..(m - (right ? 1 : 0))];
        var lanes = new SignalMoves[own];
        int folded = Math.Max(0, middle.Length - own);
        if (middle.Length >= own)
        {
            for (int k = 0; k <= folded; k++) lanes[0] |= middle[k];
            for (int k = 1; k < own; k++) lanes[k] = middle[folded + k];
        }
        else
        {
            int pad = own - middle.Length;
            for (int k = 0; k < own; k++) lanes[k] = middle[Math.Max(0, k - pad)];
        }
        return new LaneWish(wish[..left], lanes, right, folded);
    }

    /// <summary>
    /// Arrows over the carriageway's own lanes where OSM's lane data assigned them (#700), two per lane at
    /// the same distances as a pocket's, from the stop line. <paramref name="centre"/> is the leftmost lane's centre
    /// from the centre line toward the approaching driver's right, <paramref name="laneWidth"/> one lane's width.
    /// </summary>
    private static int OwnArrows(RoadSegment seg, TileId tile, bool atEnd, List<RoadPaint> paint, double centre, double laneWidth,
        SignalMoves[] own, double stop)
    {
        var way = new Widening(seg, seg, tile, 0, atEnd, atEnd ? 1 : -1, 1, 0);
        double total = SegmentLength(seg);
        int made = 0;
        foreach (double tip in (ReadOnlySpan<double>)[5 + PaintEmitter.ArrowLength, 20 + PaintEmitter.ArrowLength])
        {
            double back = tip + stop - 0.1;
            if (back > total - 2) continue;
            for (int k = 0; k < own.Length; k++)
            {
                if (ArrowOf(own[k]) is not { } kind) continue;
                double offset = centre + k * laneWidth;
                var tail = way.At(tile, back, offset);
                var head = way.At(tile, back - PaintEmitter.ArrowLength, offset);
                paint.Add(PaintEmitter.Arrow(tail[0], tail[1], tail[2], head[0] - tail[0], head[2] - tail[2], kind, head[1]));
                made++;
            }
        }
        return made;
    }
}
