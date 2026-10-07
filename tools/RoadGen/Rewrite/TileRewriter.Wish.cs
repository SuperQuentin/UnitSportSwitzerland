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
    public sealed record LaneWish(SignalMoves[] Pocket, SignalMoves[] Own, bool RightPocket, int Folded);

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
    public static LaneWish? Assign(SignalMoves[]? wish, int own, bool rightPocketPossible, bool leftPockets = true)
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
    private static int OwnArrows(RoadSegment seg, RoadSegment painted, TileId tile, bool atEnd, List<RoadPaint> paint, double centre, double laneWidth,
        SignalMoves[] own, double stop, bool signal)
    {
        var way = new Widening(seg, painted, tile, 0, atEnd, atEnd ? 1 : -1, 1, 0);
        // at the lights the lines between the lanes go solid before the stop line and stop there
        if (signal && own.Length > 1) way.SolidToStop(paint, stop - SignalStopLine * 0.5);
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

public static partial class TileRewriter
{
    /// <summary>
    /// Two lanes side by side that make the same turn (a double left, two through lanes, #700) stay apart through the
    /// junction: one dashed guide line (0.15 m, 1 m / 1 m, as #682's left-turn guide) from the crosswalk's junction edge of
    /// the approach, at the line between them, to the line between the lanes they enter on the exit arm, its departing
    /// lanes taken from the centre line out in the same order. None where the exit has too few lanes (they merge).
    /// Returns how many it drew.
    /// </summary>
    private static int EmitPairGuides(Dictionary<TileId, List<RoadPaint>> paint, TileId home, Junction junction, RoadNetwork net,
        List<(int Arm, RoadApproach Record)> records, List<(Vec2 At, float Height)> anchors)
    {
        int drawn = 0;
        foreach (var (arm, record) in records)
        {
            var cars = record.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).ToList();
            var from = junction.Arms[arm];
            var u = Vec2.FromHeading(from.OutwardHeading);
            foreach (var move in (ReadOnlySpan<SignalMoves>)[SignalMoves.Left, SignalMoves.Through, SignalMoves.Right])
            {
                // the exit: the arm on the driver's left, straight ahead or on their right (they drive along -u, their right is u.Perp)
                var way = move == SignalMoves.Left ? -u.Perp : move == SignalMoves.Right ? u.Perp : -u;
                int to = -1;
                double best = move == SignalMoves.Through ? 0.87 : 0.5;
                for (int k = 0; k < junction.Arms.Count; k++)
                {
                    if (k == arm) continue;
                    double dot = Vec2.FromHeading(junction.Arms[k].OutwardHeading).Dot(way);
                    if (dot > best) { best = dot; to = k; }
                }
                if (to < 0 || DepartingLanes(junction, to, net) is not { } exit) continue;
                // the lanes showing the turn, left to right; each neighbouring pair gets a line, the r-th pair onto the exit's r-th
                var showing = Enumerable.Range(0, cars.Count).Where(k => (cars[k].Moves & move) != 0).ToList();
                // a right turn's lanes are counted from the kerb, the others from the centre line
                for (int p = 0; p + 1 < showing.Count; p++)
                {
                    if (showing[p + 1] != showing[p] + 1) continue;
                    int rank = move == SignalMoves.Right ? showing.Count - 2 - p : p;
                    int c = move == SignalMoves.Right ? exit.Lanes - 2 - rank : rank;   // the exit's lanes from its centre line
                    if (c < 0 || c + 1 >= exit.Lanes) continue;
                    double entry = record.LaneCentre + (cars[showing[p]].Offset + cars[showing[p + 1]].Offset) * 0.5;
                    double outAt = (exit.Centre(c) + exit.Centre(c + 1)) * 0.5;
                    var target = junction.Arms[to];
                    var ut = Vec2.FromHeading(target.OutwardHeading);
                    double crossIn = MouthSkew(junction, from) + SignalStopSetback - ZebraClear - ZebraDepth;
                    double crossOut = MouthSkew(junction, target) + SignalStopSetback - ZebraClear - ZebraDepth;
                    Vec2 start = (from.Left + from.Right) * 0.5 + u.Perp * entry + u * Math.Max(0, crossIn);
                    Vec2 end = (target.Left + target.Right) * 0.5 - ut.Perp * outAt + ut * Math.Max(0, crossOut);
                    var line = new List<Vec2>();
                    double den = (-u).Cross(ut);
                    if (Math.Abs(den) < 0.1) line.AddRange([start, end]);   // straight across
                    else
                    {
                        // tangent to the way in and to the way out: the control is where their lines meet
                        Vec2 control = start + (-u) * ((end - start).Cross(ut) / den);
                        for (int k = 0; k <= 16; k++)
                        {
                            double t = k / 16.0, mt = 1 - t;
                            line.Add(start * (mt * mt) + control * (2 * mt * t) + end * (t * t));
                        }
                    }
                    Get(paint, home).Add(new RoadPaint
                    {
                        Shape = PaintShape.Polyline, Type = PaintType.WhiteDashed, Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
                        Dash = 1f, Gap = 1f, Vertices = Local(home, line, q => HeightAt(anchors, q), 0f),
                    });
                    drawn++;
                }
            }
        }
        return drawn;
    }

    /// <summary>The lanes traffic leaving by arm <paramref name="arm"/> drives on: how many, and each one's centre counted from the centre line out, metres toward the departing side.</summary>
    private sealed record Departing(int Lanes, Func<int, double> Centre);

    private static Departing? DepartingLanes(Junction j, int arm, RoadNetwork net)
    {
        var a = j.Arms[arm];
        if (InfoOf(net.Links[a.LinkId]) is not { } info || info.Attributes.OneWay != 0) return null;
        var at = info.Attributes;
        // leaving: against the drawing where the link ends at this junction
        bool atEnd = PriorityPlanner.EndAt(net, j, a) == LinkEnd.End;
        int lanes = Math.Max(1, (int)(atEnd ? at.LanesBackward : at.LanesForward)), other = Math.Max(1, (int)(atEnd ? at.LanesForward : at.LanesBackward));
        float rightBike = (atEnd ? at.Left : at.Right) is { HasLane: true } r ? r.BikeDm / 10f : 0f;
        float leftBike = (atEnd ? at.Right : at.Left) is { HasLane: true } l ? l.BikeDm / 10f : 0f;
        return new Departing(lanes, c => RoadCrossSection.TwoWayLaneOffset(info.Width, rightBike, leftBike, lanes, other, inner: lanes - 1 - c));
    }
}

public static partial class TileRewriter
{
    /// <summary>
    /// No traffic turns right from arm <paramref name="from"/> into the next arm counter-clockwise, <paramref name="to"/>
    /// (#700, the user's rules), so the corner between them can stay tight: no car drives in along <paramref name="from"/>
    /// (a path, a one-way road out); none may leave along <paramref name="to"/> (a path or track, a one-way road in); OSM
    /// forbids the turn; or OSM's turn:lanes at the junction end of <paramref name="from"/> show no lane turning right.
    /// </summary>
    private static bool NoRightTurn(RoadNetwork net, RoadNode node, Approach from, Approach to, Restrictions? restrictions, OsmOverlayReader? overlay)
    {
        if (InfoOf(net.Links[from.LinkId]) is not { } fi || InfoOf(net.Links[to.LinkId]) is not { } ti) return false;   // railways keep their corners
        bool enters = from.End == LinkEnd.Start ? fi.Attributes.OneWay <= 0 : fi.Attributes.OneWay >= 0;
        if (!PriorityPlanner.IsCarRoad(fi.Class) || !enters) return true;
        if (!PriorityPlanner.IsCarRoad(ti.Class) || !PriorityPlanner.Leaves(ti, to.End)) return true;
        if (restrictions?.Forbids(net, node, from, to) == true) return true;
        var lanes = WishedLanes(overlay, net.Links[from.LinkId], from.End, SignalMoves.Left | SignalMoves.Through | SignalMoves.Right);
        return lanes is not null && lanes.All(m => (m & SignalMoves.Right) == 0);
    }
}
