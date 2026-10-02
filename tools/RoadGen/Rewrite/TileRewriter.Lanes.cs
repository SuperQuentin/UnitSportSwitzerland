namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Lane records (#353, <see cref="RoadApproach"/>, section <c>LANE</c>): for every approach with a
/// left-turn pocket (#123, with or without lights), a right-turn pocket (#348) or traffic lights,
/// the lanes as built: where each lies at the stop line, where it opens and reaches full width
/// (from the widenings' actual taper and storage), which movements its arrows show, where its
/// traffic stops (a bike box), and the turns an OSM restriction forbids (#347). Traffic
/// (<c>LaneGraph</c>, <c>Traffic</c>) drives them. Rules: docs/notes/tools/turn-lanes.md.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>What one approach got from the turn-lane stage: its widenings as built.</summary>
    private sealed class ApproachPockets(TileId home)
    {
        public readonly TileId Home = home;
        /// <summary>The left pocket's approach widening (#123), null none; its storage, merged with the exit before (#325).</summary>
        public Widening? Left;
        public double Storage;
        public bool Merged;
        /// <summary>The through lane's arrow also turns right (no right pocket beside it).</summary>
        public bool ThroughRight;
        public bool Signal;
        /// <summary>The right-turn pocket's widening (#348), null none; without a left pocket, whether the own lane also turns left.</summary>
        public Widening? Right;
        public bool OwnLeft;

        public void PlacedLeft(Widening way, double storage, bool merged, bool throughRight, bool signal)
        {
            Left = way; Storage = storage; Merged = merged; ThroughRight = throughRight; Signal = signal;
        }

        public void PlacedRight(Widening way, bool ownLeft)
        {
            Right = way; OwnLeft = ownLeft; Signal = true;
        }
    }

    public sealed class LaneStats
    {
        public int Signalised, PocketsWithoutLights, CarLanes, BikeLanes, Banned, BannedLeft, BannedThrough, BannedRight;
        public int Restrictions, RestrictionsMatched, RestrictionsNoArm, RestrictionsOtherTurn, PocketsBanned;

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    lane records (#353): {Signalised + PocketsWithoutLights:N0} approaches ({Signalised:N0} at traffic lights, {PocketsWithoutLights:N0} with a pocket and no lights), lanes: car {CarLanes:N0}, bike {BikeLanes:N0}; " +
            $"banned turns on {Banned:N0} approaches (left {BannedLeft:N0}, through {BannedThrough:N0}, right {BannedRight:N0}) from {RestrictionsMatched:N0} of {Restrictions:N0} OSM restrictions at a recorded approach ({RestrictionsNoArm:N0} with no arm for the to-line, {RestrictionsOtherTurn:N0} naming another turn than the arms make), {PocketsBanned:N0} left pockets whose left turn OSM forbids\n");

        public void Count(RoadApproach a)
        {
            if (a.Signal >= 0) Signalised++; else PocketsWithoutLights++;
            foreach (var l in a.Lanes)
                if (l.Kind == ApproachLaneKind.Car) CarLanes++; else BikeLanes++;
            if (a.Banned == 0) return;
            Banned++;
            if ((a.Banned & SignalMoves.Left) != 0) BannedLeft++;
            if ((a.Banned & SignalMoves.Left) != 0 && a.Lanes.Exists(l => l.Kind == ApproachLaneKind.Car && l.Moves == SignalMoves.Left)) PocketsBanned++;
            if ((a.Banned & SignalMoves.Through) != 0) BannedThrough++;
            if ((a.Banned & SignalMoves.Right) != 0) BannedRight++;
        }
    }

    /// <summary>OSM turn restrictions (#347, <c>osm_nodes.tsv</c>) by their from-line.</summary>
    private sealed class Restrictions
    {
        /// <summary>The via node lies this close to the junction's centre (the reader snapped the ways within 30 m).</summary>
        private const double Reach = 30.0;
        private readonly Dictionary<(string Uuid, int Part), List<OsmNodesReader.Entry>> _byFrom = new();

        public static Restrictions? From(OsmNodesReader? nodes)
        {
            if (nodes is null) return null;
            var r = new Restrictions();
            foreach (var e in nodes.All)
            {
                if (e.Kind != OsmNodesReader.NodeKind.Restriction || e.ToUuid.Length == 0) continue;
                if (!r._byFrom.TryGetValue((e.Uuid, e.Part), out var list)) r._byFrom[(e.Uuid, e.Part)] = list = new();
                list.Add(e);
            }
            return r;
        }

        /// <summary>
        /// The turns forbidden from arm <paramref name="from"/> of a junction: a <c>no_*</c>
        /// restriction bans the turn to its to-line's arm, an <c>only_*</c> one every other turn
        /// (<c>no_u_turn</c> is not a movement here). Turns as the signal plans name them.
        /// </summary>
        public SignalMoves Banned(Junction j, int from, RoadNetwork net, LaneStats stats)
        {
            if (net.Links[j.Arms[from].LinkId].Tag is not Source { Key: { } key } || !_byFrom.TryGetValue((key.Uuid, key.Part), out var list))
                return SignalMoves.None;
            List<SignalArm>? arms = null;
            var banned = SignalMoves.None;
            foreach (var e in list)
            {
                if (new Vec2(e.E, e.N).DistanceTo(j.Centre) > Reach || e.Value == "no_u_turn") continue;
                stats.Restrictions++;
                arms ??= ArmsOf(j, net);
                int to = -1;
                for (int k = 0; k < j.Arms.Count && to < 0; k++)
                    if (k != from && net.Links[j.Arms[k].LinkId].Tag is Source { Key: { } tk } && tk.Uuid == e.ToUuid && tk.Part == e.ToPart) to = k;
                if (to < 0 || !arms[to].Out) { stats.RestrictionsNoArm++; continue; }
                stats.RestrictionsMatched++;
                var turn = SignalPlan.Turn(arms, from, to);
                if (!e.Value.EndsWith(turn switch { SignalMoves.Left => "_left_turn", SignalMoves.Right => "_right_turn", _ => "_straight_on" }, StringComparison.Ordinal))
                    stats.RestrictionsOtherTurn++;
                if (e.Value.StartsWith("no_", StringComparison.Ordinal)) banned |= turn;
                else if (e.Value.StartsWith("only_", StringComparison.Ordinal))
                    for (int k = 0; k < arms.Count; k++)
                        if (k != from && arms[k].Out && SignalPlan.Turn(arms, from, k) != turn) banned |= SignalPlan.Turn(arms, from, k);
            }
            return banned;
        }
    }

    /// <summary>A junction's arms as the plans see them: heading, and whether cars may leave along it.</summary>
    private static List<SignalArm> ArmsOf(Junction j, RoadNetwork net)
    {
        var arms = new List<SignalArm>(j.Arms.Count);
        foreach (var arm in j.Arms)
        {
            bool car = InfoOf(net.Links[arm.LinkId]) is { } info && PriorityPlanner.IsCarRoad(info.Class);
            arms.Add(new SignalArm(arm.OutwardHeading, In: car,
                Out: car && PriorityPlanner.Leaves(InfoOf(net.Links[arm.LinkId])!.Value, PriorityPlanner.EndAt(net, j, arm))));
        }
        return arms;
    }

    /// <summary>The stop bar across a #123 pocket without lights lies this far from the mouth (its middle).</summary>
    private const double PocketBarMiddle = 0.1 + StopBar * 0.5;

    /// <summary>
    /// The lane record of a signalised approach: its stop line's middle (as <c>SGNL</c> stores it),
    /// the junction's record and plan arm; the pockets' lanes, or one lane with every movement the
    /// approach has.
    /// </summary>
    private static RoadApproach SignalApproach(Junction j, int arm, RoadNetwork net, short signal, byte planArm, float[] stop,
        SignalPlan plan, ApproachPockets? built, Restrictions? restrictions, LaneStats stats)
    {
        var banned = restrictions?.Banned(j, arm, net, stats) ?? SignalMoves.None;
        float centre;
        List<ApproachLane> lanes;
        if (built is not null)
            (centre, lanes) = PocketLanes(built, MouthSkew(j, j.Arms[arm]) + SignalStopSetback + SignalStopLine * 0.5);
        else
        {
            var moves = SignalMoves.None;
            for (int k = 0; k < plan.Arms.Count; k++)
                if (k != planArm && plan.Arms[k].Out) moves |= SignalPlan.Turn(plan.Arms, planArm, k);
            centre = (float)OwnLaneCentre(j, arm, net);
            lanes = [new ApproachLane(0f, 0f, 0f, moves, ApproachLaneKind.Car)];
        }
        var record = new RoadApproach
        {
            X = stop[0], Y = stop[1], Z = stop[2], Heading = (float)j.Arms[arm].OutwardHeading, Signal = signal, SignalArm = planArm,
            Banned = banned, LaneCentre = centre, Lanes = lanes,
        };
        stats.Count(record);
        return record;
    }

    /// <summary>
    /// The lane records of #123 pockets at junctions without lights: their point is the middle of
    /// the approach lanes at the pocket's stop bar, in the junction's home tile.
    /// </summary>
    private static void EmitPocketApproaches(PriorityResult priority, RoadGenResult result, Dictionary<(int Node, int Arm), ApproachPockets> pockets,
        Dictionary<TileId, List<RoadApproach>> approaches, Restrictions? restrictions, LaneStats stats)
    {
        var net = result.Network;
        foreach (var (junction, _) in priority.Plans)
            for (int i = 0; i < junction.Arms.Count; i++)
            {
                if (!pockets.TryGetValue((junction.NodeId, i), out var built) || built.Signal || built.Left is not { } way) continue;
                var (centre, lanes) = PocketLanes(built, PocketBarMiddle);
                double across = way.Half + way.FullWidth;   // the approach lanes, centre line to the widened edge
                var at = way.At(built.Home, PocketBarMiddle, across * 0.5);
                var record = new RoadApproach
                {
                    X = at[0], Y = at[1], Z = at[2], Heading = (float)junction.Arms[i].OutwardHeading,
                    Banned = restrictions?.Banned(junction, i, net, stats) ?? SignalMoves.None, LaneCentre = centre, Lanes = lanes,
                };
                Get(approaches, built.Home).Add(record);
                stats.Count(record);
            }
    }

    /// <summary>The middle of the approach's own lane (no pocket): between the centre line and a painted bike lane on its right, 0 on a one-way road.</summary>
    private static double OwnLaneCentre(Junction j, int arm, RoadNetwork net)
    {
        var a = j.Arms[arm];
        if (InfoOf(net.Links[a.LinkId]) is not { } info || info.Attributes.OneWay != 0) return 0;
        // the approach drives toward this end: along the drawing when the link ends here
        var right = PriorityPlanner.EndAt(net, j, a) == LinkEnd.End ? info.Attributes.Right : info.Attributes.Left;
        return (a.HalfWidth - (right.HasLane ? right.BikeDm / 10.0 : 0)) * 0.5;
    }

    /// <summary>
    /// An approach's lanes from its widenings as built, left to right, with the original lane's
    /// centre they are measured from. <paramref name="stop"/>: the stop line's (or bar's) distance
    /// from the mouth; the widenings measure from the mouth.
    /// </summary>
    private static (float Centre, List<ApproachLane> Lanes) PocketLanes(ApproachPockets p, double stop)
    {
        var lanes = new List<ApproachLane>();
        float D(double fromMouth) => (float)Math.Max(0, fromMouth - stop);
        if (p.Left is { } lw)
        {
            double centre = lw.Car * 0.5;
            // the pocket appears beside the through lane where the hatch closes (#123), or opens out
            // of the lane-wide hatch over the entry diagonal (#325)
            double full = p.Storage, opens = p.Merged ? p.Storage + TurnEntry : p.Storage;
            bool box = p.Signal && lw.HasLeftBikeLane && lw.BikeBox;
            lanes.Add(new ApproachLane((float)(lw.PocketWidth * 0.5 - centre), D(full), D(opens), SignalMoves.Left, ApproachLaneKind.Car,
                box ? (float)BikeBoxDepth : 0f));
            if (lw.HasLeftBikeLane)   // the left-turn bike lane (#351): stops at the box's front line, or the advanced line
                lanes.Add(new ApproachLane((float)(lw.PocketWidth + lw.BikeLeft * 0.5 - centre), D(full), D(opens), SignalMoves.Left,
                    ApproachLaneKind.Bike, box ? 0f : -(float)AdvancedBikeLine));
            // the through lane moves out over the taper (and the lead-in before it); a merged strip holds it out all along
            double throughFull = p.Merged ? lw.Length : p.Storage, throughFrom = p.Merged ? lw.Length : lw.Length + lw.Lead;
            double through = lw.PocketWidth + lw.BikeLeft;   // its left edge
            lanes.Add(new ApproachLane((float)(through + TurnLane * 0.5 - centre), D(throughFull), D(throughFrom),
                SignalMoves.Through | (p.ThroughRight ? SignalMoves.Right : 0), ApproachLaneKind.Car));
            RightLanes(p, lanes, centre, through + TurnLane, lw.KerbBike, D(throughFull), D(throughFrom), D);
            return ((float)centre, lanes);
        }
        // a right pocket alone: the approach's own lane carries straight on (and left)
        var rw = p.Right!;
        double own = rw.Car * 0.5;
        lanes.Add(new ApproachLane(0f, D(rw.Length), D(rw.Length), SignalMoves.Through | (p.OwnLeft ? SignalMoves.Left : 0), ApproachLaneKind.Car));
        RightLanes(p, lanes, own, rw.Car, rw.KerbBike, D(rw.Length), D(rw.Length), D);
        return ((float)own, lanes);
    }

    /// <summary>
    /// The lanes right of the through (or own) lane: the right-turn pocket (#348) and the painted
    /// bike lane on that side (#120). The one place that knows how they are laid out: #351's layout
    /// (a), the bike lane outside the pocket (it moves out past every widening, `ShiftOffPavement`),
    /// along the kerb from the through lane's taper. A layout with the bike lane between the pocket
    /// and the through lane changes this.
    /// </summary>
    /// <param name="throughEdge">The through (or own) lane's right edge from the centre line, at the stop line.</param>
    /// <param name="bike">The painted bike lane's width on that side, 0 none.</param>
    private static void RightLanes(ApproachPockets p, List<ApproachLane> lanes, double centre, double throughEdge, double bike,
        float throughFull, float throughFrom, Func<double, float> d)
    {
        if (p.Right is not { } rw)
        {
            if (bike > 0)
                lanes.Add(new ApproachLane((float)(throughEdge + bike * 0.5 - centre), throughFull, throughFrom,
                    SignalMoves.Through | SignalMoves.Right, ApproachLaneKind.Bike));
            return;
        }
        double inner = rw.Half + rw.Base;   // the pocket's left edge: the carriageway's, or the left pocket's widening's
        float full = d(rw.Length - rw.Taper), from = d(rw.Length);
        lanes.Add(new ApproachLane((float)(inner + TurnLane * 0.5 - centre), full, from, SignalMoves.Right, ApproachLaneKind.Car));
        if (bike > 0)
            lanes.Add(new ApproachLane((float)(inner + TurnLane + bike * 0.5 - centre), full, Math.Max(from, throughFrom),
                SignalMoves.Through | SignalMoves.Right, ApproachLaneKind.Bike));
    }
}
