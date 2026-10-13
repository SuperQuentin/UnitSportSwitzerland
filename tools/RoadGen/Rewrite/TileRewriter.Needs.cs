namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// What an approach needs from the data (#818): the lanes it holds and what OSM or the inference makes of them, the turns it
/// has, whether a left or right pocket is wanted, its speed, the bike layout beside a right pocket and a bike box. Read by the
/// pocket layout (#123, #348) and by the junction planner (<c>docs/plans/junction-layout.md</c>), so both decide alike.
/// </summary>
public static partial class TileRewriter
{
    /// <param name="OwnLanes">The car lanes the carriageway already holds toward the junction (#700).</param>
    /// <param name="Can">The turns the approach has, as the signal plans name them (#386).</param>
    /// <param name="Banned">The turns an OSM restriction forbids (#347).</param>
    /// <param name="Wished">OSM's <c>turn:lanes</c> at the junction end, left to right, null none (#700).</param>
    /// <param name="Inferred">Without OSM lane data, the lanes of a multi-lane approach shared out by the exits (#711), null else.</param>
    /// <param name="Wish">The wished lanes fitted onto the carriageway (pockets, own lanes, right pocket), null without data.</param>
    /// <param name="Left">A car road leaves to the approaching driver's left (<paramref name="LeftArm"/>), more than 30 degrees off straight.</param>
    /// <param name="Exit">The arm the through traffic leaves by: the other main arm, or at the lights the straightest; -1 none.</param>
    /// <param name="Kmh">The speed OSM maps for the approach, 0 none.</param>
    /// <param name="Town">A sidewalk or path along either side at the junction (#119).</param>
    /// <param name="BikeBetween">Beside a right pocket the painted bike lane runs between it and the through lane, layout (b) (#351).</param>
    /// <param name="BikeBox">A bike box where the street the left turn goes into has no bike lane or path (#351).</param>
    internal sealed record ApproachNeeds(int OwnLanes, SignalMoves Can, SignalMoves Banned, SignalMoves[]? Wished, SignalMoves[]? Inferred,
        LaneWish? Wish, bool Left, int LeftArm, bool Right, int Exit, int Kmh, bool Town, bool WantLeft, bool WantRight, bool BikeBetween,
        bool BikeBox)
    {
        /// <summary>A right-turn pocket only on a faster road (#711): the speed OSM maps, else 50 in a town and 80 outside.</summary>
        public bool Fast => (Kmh > 0 ? Kmh : Town ? 50 : 80) > RightPocketSpeed;
    }

    /// <summary>The turns arm <paramref name="i"/>'s traffic has: to every other arm cars may leave by, as the signal plans name them (#386).</summary>
    private static SignalMoves CanOf(List<SignalArm> arms, int i)
    {
        var can = SignalMoves.None;
        for (int k = 0; k < arms.Count; k++)
            if (k != i && arms[k].Out) can |= SignalPlan.Turn(arms, i, k);
        return can;
    }

    /// <summary>
    /// The needs of arm <paramref name="i"/> of a main-road (#123) or signalised (#348) junction, null where the arm gets no
    /// pockets at all: not a main arm (without lights), not an approach or inside a junction of several nodes (at the lights),
    /// or not a road that takes turn lanes (<see cref="TurnLaneRoad"/>).
    /// </summary>
    private static ApproachNeeds? NeedsOf(Junction junction, PriorityPlanner.Plan plan, int i, JunctionRules rules, RoadNetwork net,
        HashSet<int> signalNodes, Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf,
        Func<int, LinkEnd, bool, RoadSide> streetSide, OsmOverlayReader? overlay, Restrictions? restrictions, LaneStats scratchLanes)
    {
        bool every = rules.Has(JunctionRule.PocketsOnEveryApproach);
        var arm = plan.Arms[i];
        if ((every ? !arm.Approach || Internal(net.Links[arm.LinkId], junction.NodeId, signalNodes) : arm.Role != PriorityPlanner.Role.Main)
            || !TurnLaneRoad(net.Links[arm.LinkId])) return null;
        // the lanes the carriageway already holds toward the junction (#700)
        int ownLanes = segmentOf.TryGetValue(arm.LinkId, out var laneSeg) ? CarLanesIn(laneSeg.Segment, arm.End == LinkEnd.End) : 1;

        // the approaching driver's way, and whether a car road leaves to their left / right
        var d = Vec2.FromHeading(junction.Arms[i].OutwardHeading) * -1;
        bool left = false, right = false;
        int exit = -1, leftArm = -1;
        double straightest = 0.87;
        for (int k = 0; k < junction.Arms.Count && k < plan.Arms.Count; k++)
        {
            if (k == i) continue;
            if (!every && plan.Arms[k].Role == PriorityPlanner.Role.Main) { exit = k; continue; }
            if (!every && plan.Arms[k].Role != PriorityPlanner.Role.Yield) continue;
            if (InfoOf(net.Links[plan.Arms[k].LinkId]) is not { } info || info.Class > RoadClass.Lane) continue;
            var v = Vec2.FromHeading(junction.Arms[k].OutwardHeading);
            double ahead = d.X * v.X + d.Y * v.Y;
            if (every && ahead > straightest) { straightest = ahead; exit = k; continue; }
            if (ahead > 0.87) continue;   // carries on nearly straight
            if (every && !PriorityPlanner.Leaves(info, plan.Arms[k].End)) continue;   // a one-way road in
            if (d.X * v.Y - d.Y * v.X > 0) { left = true; leftArm = k; } else right = true;
        }
        var armsHere = ArmsOf(junction, net);
        var can = CanOf(armsHere, i);
        // OSM's lane data at the junction end (#700), among the turns the approach has and OSM does not forbid:
        // assigned to the lanes the carriageway holds; the pockets it wants are built, and none it does not want
        var banned = restrictions?.Banned(junction, i, net, scratchLanes) ?? SignalMoves.None;
        var wished = WishedLanes(overlay, net.Links[arm.LinkId], arm.End, can & ~banned);
        // a right-turn pocket only on a faster road (#711, the user's decision), at every kind of junction: the speed OSM maps
        // for the approach, else Switzerland's general limits (50 in a town, 80 outside)
        int kmh = ApproachSpeed(overlay, net.Links[arm.LinkId], arm.End);
        // in town: a sidewalk or path along either side at the junction (the street planner's, #119)
        bool town = streetSide(arm.LinkId, arm.End, true).OuterDm > 0 || streetSide(arm.LinkId, arm.End, false).OuterDm > 0;
        bool fast = (kmh > 0 ? kmh : town ? 50 : 80) > RightPocketSpeed;
        var wish = Assign(wished, ownLanes, rightPocketPossible: fast && right && ownLanes == 1);
        bool wantLeft = wish is null ? left && ownLanes == 1 : left && wish.Pocket.Length > 0;
        bool wantRight = wish is null ? right && ownLanes == 1 && fast : wish.RightPocket;

        // a multi-lane approach without OSM lane data (#700, the user's review of J7; #711: at every junction, shared out
        // by the lanes each exit takes away, InferredLanes), with their arrows as OSM's would get
        SignalMoves[]? inferred = null;
        if (wished is null && ownLanes >= 2)
        {
            // (#711) the lanes each turn's exit takes away: the most of any arm making that turn
            int outL = 0, outT = 0, outR = 0;
            for (int k = 0; k < armsHere.Count; k++)
            {
                if (k == i || !armsHere[k].Out) continue;
                int n = OutLanes(junction, k, net);
                switch (SignalPlan.Turn(armsHere, i, k))
                {
                    case SignalMoves.Left: outL = Math.Max(outL, n); break;
                    case SignalMoves.Through: outT = Math.Max(outT, n); break;
                    case SignalMoves.Right: outR = Math.Max(outR, n); break;
                }
            }
            static int Known(int n) => n > 0 ? n : int.MaxValue;
            inferred = InferredLanes(can & ~banned, ownLanes, Known(outL), Known(outT), Known(outR));
        }

        // a bike box where the street the left turn goes into has no bike lane or path (#351)
        bool box = false;
        if (rules.Has(JunctionRule.BikeBoxes) && leftArm >= 0 && segmentOf.TryGetValue(plan.Arms[leftArm].LinkId, out var into))
        {
            var leaving = plan.Arms[leftArm].End == LinkEnd.Start ? into.Segment.Attributes.Right : into.Segment.Attributes.Left;
            box = !leaving.HasLane && !leaving.HasTrack;
        }
        // a painted bike lane beside a right pocket runs kerbside (a) or between the pocket and the through lane (b): at the
        // lights at random but the same on every run (#351), a bike signal guarding (a); without lights always (b), the
        // right-turning cars never crossing it at the mouth (#711); a path keeps (a)
        bool between = false;
        if (wantRight && segmentOf.TryGetValue(arm.LinkId, out var rightSeg))
        {
            var kerb = arm.End == LinkEnd.End ? rightSeg.Painted.Attributes.Right : rightSeg.Painted.Attributes.Left;
            between = kerb.HasLane && !kerb.HasTrack
                && (!rules.Has(JunctionRule.KerbsideBikeLane) || BikePlanner.Fnv(ApproachKey(net.Links[arm.LinkId], arm.End, junction.Centre)) % 2 == 1);
        }
        return new ApproachNeeds(ownLanes, can, banned, wished, inferred, wish, left, leftArm, right, exit, kmh, town, wantLeft, wantRight,
            between, box);
    }
}
