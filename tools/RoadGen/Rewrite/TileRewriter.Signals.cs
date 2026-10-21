namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using System.IO.Compression;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Traffic lights (#348): the junctions <see cref="PriorityPlanner"/> marked
/// <see cref="PriorityPlanner.Kind.Signal"/> get a white stop line (SSV 6.10, 0.50 m) across the
/// approach lanes of every arm (a left-turn pocket's own, #123, already runs across both of its
/// lanes) and a <see cref="RoadSignal"/> record in their home tile: per arm whether traffic comes
/// in and goes out, the pocket built there and the stop line's middle, and the fixed-time plan
/// <see cref="SignalPlan.Build"/> makes of it (#349). Rules and numbers: docs/notes/tools/traffic-signals.md.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>The stop line at traffic lights: 0.50 m (SSV 6.10; Kanton Bern Handbuch Markierung).</summary>
    private const float SignalStopLine = 0.5f;

    /// <summary>
    /// The stop line at traffic lights lies this far back from the junction's mouth: behind a red
    /// bike crossing there (#120, about 2 m) with a metre to spare. #292 moves it back behind a
    /// pedestrian crossing.
    /// </summary>
    private const double SignalStopSetback = 3.6;

    /// <summary>
    /// Behind a crosswalk the cars' stop line stands this much further back where the approach has room (the user's review,
    /// #711: 0.3 m from the zebra was too close); the riders' lines stay by the crosswalk (they need no setback).
    /// </summary>
    private const double StopBehindCrosswalk = 2.0;

    /// <summary>The approach keeps at least this much (m) behind the moved stop line, else the line stays (#711).</summary>
    private const double StopBackRoom = 15.0;

    /// <summary>Each lights approach's extra stop-line setback behind its crosswalk (#711), while a block is laid out.</summary>
    [ThreadStatic] private static Dictionary<(int Node, int Arm), double>? _stopBack;

    /// <summary>
    /// Where an exit's bike lane starts, out from its mouth, per link end (#711, the user's review of J3's south leg): on an arm
    /// with no crosswalk nor island, where the left-turn guides leave it (its stop line's level), not at the mouth.
    /// </summary>
    [ThreadStatic] private static Dictionary<(int Link, LinkEnd End), double>? _exitBikeFrom;

    /// <summary>The lights' arms with a crosswalk, while a block is laid out (#711).</summary>
    [ThreadStatic] private static HashSet<(int Node, int Arm)>? _crosswalkArms;

    /// <summary>
    /// Where a guide through the lights meets an arm, out from its mouth (#711, the user's review): the crosswalk's junction
    /// edge where it has one, else its stop line (where the kerb's curve begins, past the usual setback).
    /// </summary>
    private static double GuideEdge(Junction junction, int arm) =>
        _crosswalkArms?.Contains((junction.NodeId, arm)) != false
            ? MouthSkew(junction, junction.Arms[arm]) + SignalStopSetback - ZebraClear - ZebraDepth
            : MouthSkew(junction, junction.Arms[arm]) + SignalStopSetback + StopBack(junction.NodeId, arm);

    /// <summary>
    /// How much further back than <see cref="SignalStopSetback"/> the cars' stop line stands on an approach (#711): more behind a
    /// crosswalk, <c>-SignalStopSetback</c> (at the mouth, where the kerb's curve begins) where nothing crosses it.
    /// </summary>
    private static double StopBack(int node, int arm) => _stopBack?.GetValueOrDefault((node, arm)) ?? 0;

    /// <summary>
    /// Whether a lights arm gets a crosswalk (#682, #700, #711): where OSM maps a marked crossing on it; where OSM maps none round
    /// the junction, on every arm with a sidewalk. Also whether OSM maps one there and the arm has a sidewalk (the stats).
    /// </summary>
    private static (bool Crosswalk, bool Osm, bool Sidewalk) CrosswalkOn(Junction junction, int i, RoadNetwork net, CrossingNodes? crossings,
        bool crossingsMapped, RoadSide streetRight, RoadSide streetLeft)
    {
        bool osm = crossings?.OnArm(junction, i, net) is not null, sidewalk = streetRight.SidewalkDm > 0 || streetLeft.SidewalkDm > 0;
        return (osm || sidewalk && !crossingsMapped && JunctionRules.Lights.Has(JunctionRule.CrosswalkOnSidewalkArms), osm, sidewalk);
    }

    /// <summary>
    /// Where arm <paramref name="i"/>'s kerb begins to curve into the junction, out from its mouth's middle along it (#711): the
    /// furthest point of the junction's outline on either kerb line of the arm (its rounded corners run out beside the arm).
    /// </summary>
    private static double CurveStart(Junction junction, int i)
    {
        var arm = junction.Arms[i];
        var u = Vec2.FromHeading(arm.OutwardHeading);
        var mid = (arm.Left + arm.Right) * 0.5;
        double best = 0;
        foreach (var p in junction.Boundary)
        {
            double along = (p - mid).Dot(u), across = Math.Abs((p - mid).Cross(u));
            if (along > best && Math.Abs(across - arm.HalfWidth) < 0.15) best = along;
        }
        return best;
    }

    /// <summary>
    /// Whether riders going straight through the junction cross arm <paramref name="i"/>'s mouth (a red bike crossing, #120):
    /// two other arms nearly opposite each other, each with a bike lane or path on its side facing the arm.
    /// </summary>
    private static bool RidersCross(Junction junction, int i, PriorityPlanner.Plan plan, Func<int, LinkEnd, bool, RoadSide> streetSideAt)
    {
        var ui = Vec2.FromHeading(junction.Arms[i].OutwardHeading);
        RoadSide Facing(int a)
        {
            var ua = Vec2.FromHeading(junction.Arms[a].OutwardHeading);
            bool drawnRight = plan.Arms[a].End == LinkEnd.End;   // the approaching driver's right is the segment's right
            bool onRight = ui.Dot(ua.Perp) > 0;
            return streetSideAt(plan.Arms[a].LinkId, plan.Arms[a].End, onRight ? drawnRight : !drawnRight);
        }
        int n = Math.Min(junction.Arms.Count, plan.Arms.Count);
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            {
                if (a == i || b == i) continue;
                var ua = Vec2.FromHeading(junction.Arms[a].OutwardHeading);
                var ub = Vec2.FromHeading(junction.Arms[b].OutwardHeading);
                if (ua.Dot(ub) > -0.64) continue;   // not straight on (within 50 degrees)
                var (sa, sb) = (Facing(a), Facing(b));
                if ((sa.HasLane || sa.HasTrack) && (sb.HasLane || sb.HasTrack)) return true;
            }
        return false;
    }

    /// <summary>
    /// The cars' stop line of every lights approach with a crosswalk moves <see cref="StopBehindCrosswalk"/> back where the
    /// approach is long enough (#711), decided before the pockets are laid (their lines stand on it).
    /// </summary>
    private static void PlanStopBacks(PriorityResult priority, RoadNetwork net, Func<int, LinkEnd, bool, RoadSide> streetSideAt, CrossingNodes? crossings)
    {
        _stopBack = [];
        _crosswalkArms = [];
        _exitBikeFrom = [];
        var signalNodes = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Signal).Select(p => p.Junction.NodeId).ToHashSet();
        var signalJunctions = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Signal).ToDictionary(p => p.Junction.NodeId, p => p.Junction);
        foreach (var (junction, plan) in priority.Plans)
        {
            if (plan.Kind != PriorityPlanner.Kind.Signal) continue;
            bool crossingsMapped = CrossingsMapped(junction, net, signalNodes, signalJunctions, crossings);
            for (int i = 0; i < junction.Arms.Count && i < plan.Arms.Count; i++)
            {
                var link = net.Links[plan.Arms[i].LinkId];
                if (!plan.Arms[i].Approach || Internal(link, junction.NodeId, signalNodes)) continue;
                bool drawnRight = plan.Arms[i].End == LinkEnd.End;
                var right = streetSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, drawnRight);
                var left = streetSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, !drawnRight);
                // the cars stop where the kerb's curve begins (the user's review, #711: a line inside the corner's rounding, or
                // 3.6 m past it with nothing to keep clear, missed the point of the turn radius); past the usual setback relative to it
                double curve = CurveStart(junction, i) - MouthSkew(junction, junction.Arms[i]) - SignalStopSetback;
                double back;
                bool crosswalkHere = CrosswalkOn(junction, i, net, crossings, crossingsMapped, right, left).Crosswalk;
                if (crosswalkHere) _crosswalkArms.Add((junction.NodeId, i));
                if (!crosswalkHere)
                    // up to the curve only where nothing crosses the mouth (no riders going straight across it)
                    back = curve > 0 || !RidersCross(junction, i, plan, streetSideAt) ? Math.Max(curve, -SignalStopSetback) : 0;
                else
                {
                    double length = -(link.TrimStart + link.TrimEnd);
                    for (int k = 1; k < link.Centreline.Count; k++) length += link.Centreline[k].DistanceTo(link.Centreline[k - 1]);
                    bool room = length >= MouthSkew(junction, junction.Arms[i]) + SignalStopSetback + StopBehindCrosswalk + StopBackRoom;
                    back = Math.Max(room ? StopBehindCrosswalk : 0, curve);
                }
                if (Math.Abs(back) > 0.05) _stopBack[(junction.NodeId, i)] = back;
            }
        }
    }

    /// <summary>
    /// Signal poles (#350) stand this far past the mouth along their arm, at the kerb plus
    /// <see cref="PoleClear"/>, stepping out by <see cref="PoleStep"/> up to <see cref="PoleTries"/>
    /// times until clear of every carriageway, widening and building. With the stop line
    /// <see cref="SignalStopSetback"/> back that leaves the 4 m a driver needs to see a roadside
    /// head (Kanton Bern Handbuch Markierung).
    /// </summary>
    private const double PoleAlong = 0.4, PoleClear = 0.5, PoleStep = 0.4;

    /// <summary>Beside a crosswalk the poles stand this far behind its near edge (#711, the user's review).</summary>
    private const double PoleBehindCrosswalk = 0.4;

    /// <summary>The riders' stop line beside a pole at the crosswalk starts this far behind the pole (#711, the user's review).</summary>
    private const double RiderLineBehindPole = 0.5;

    /// <summary>
    /// Where the riders stop on an approach, out from the mouth (#711): by the crosswalk; where the cars' line stands back the pole
    /// stands at the crosswalk, and the riders a little behind it.
    /// </summary>
    private static double RiderStop(double mouthSkew, double back) =>
        back > 0 ? mouthSkew + SignalStopSetback - ZebraClear + PoleBehindCrosswalk + RiderLineBehindPole : mouthSkew + SignalStopSetback + back;
    private const int PoleTries = 6;

    /// <summary>Where no spot beside the kerb is clear, the pole is tried this much further out along its arm, up to four times (#711).</summary>
    private const double PoleBackStep = 1.5;

    /// <summary>A priority sign on a signal pole (#350) has its plate's top this high: below the heads' 2.35 m (SSV Art. 71).</summary>
    private const float SignOnPoleTop = 2.25f;

    /// <summary>Whether a sign was moved onto a signal pole (#350): later passes leave it there (#711).</summary>
    private static bool OnSignalPole(RoadPointProp sign) => (sign.Flags & PropFlags.OnSignalPole) != 0;

    /// <summary>
    /// The top of a pole's car heads (#350): their lower edge (2.35 m, 3.35 m above a pedestrian head), three lenses
    /// and the plate's margin, as <c>SignalBuilder</c> lays them.
    /// </summary>
    private const float CarHeadsTop = 2.35f + 3 * 0.3f + 0.1f, CarHeadsTopAbovePedestrian = 3.35f + 3 * 0.3f + 0.1f;

    /// <summary>A priority sign above the heads (#711) stands this far clear of them.</summary>
    private const float SignAboveHeads = 0.15f;

    /// <summary>
    /// A link this short between two signalised junction nodes is inside one junction (a large
    /// junction is several TLM nodes, e.g. round tram tracks or a divided road's carriageways):
    /// no stop line on it, and it is not an approach (#348).
    /// </summary>
    private const double InternalLinkM = 30.0;

    private static bool Internal(RoadLink link, int node, HashSet<int> signalNodes)
    {
        int other = link.StartNode == node ? link.EndNode : link.StartNode;
        if (other == node || !signalNodes.Contains(other)) return false;
        return LinkLength(link) < InternalLinkM;
    }

    /// <summary>As <see cref="Internal(RoadLink, int, HashSet{int})"/> from a junction that may be merged from several nodes (#818).</summary>
    private static bool Internal(RoadLink link, Junction j, HashSet<int> signalNodes)
    {
        int other = j.Holds(link.StartNode) ? link.EndNode : link.StartNode;
        if (j.Holds(other) || !signalNodes.Contains(other)) return false;
        return LinkLength(link) < InternalLinkM;
    }

    private static double LinkLength(RoadLink link)
    {
        double length = 0;
        for (int i = 1; i < link.Centreline.Count; i++) length += link.Centreline[i].DistanceTo(link.Centreline[i - 1]);
        return length;
    }

    /// <summary>
    /// Whether OSM maps a crossing (marked or unmarked) on any arm of the lights junction <paramref name="j"/> is part of: it and the
    /// signal nodes linked to it by links inside the junction (#711). Its crosswalks then follow the data.
    /// </summary>
    private static bool CrossingsMapped(Junction j, RoadNetwork net, HashSet<int> signalNodes, Dictionary<int, Junction> signalJunctions, CrossingNodes? crossings)
    {
        if (crossings is null) return false;
        var seen = new HashSet<int> { j.NodeId };
        var todo = new Stack<Junction>();
        todo.Push(j);
        while (todo.TryPop(out var node))
            for (int i = 0; i < node.Arms.Count; i++)
            {
                if (crossings.OnArm(node, i, net, unmarked: true) is not null) return true;
                var link = net.Links[node.Arms[i].LinkId];
                int other = node.Holds(link.StartNode) ? link.EndNode : link.StartNode;
                if (Internal(link, node, signalNodes) && seen.Add(other) && signalJunctions.TryGetValue(other, out var next)) todo.Push(next);
            }
        return false;
    }

    /// <summary>
    /// How much further out than its middle a skewed mouth reaches along the arm: a stop line
    /// square to the road stands that much further back, so none of it lies in the junction (#348).
    /// </summary>
    private static double MouthSkew(Junction j, JunctionArm arm)
    {
        var u = Vec2.FromHeading(arm.OutwardHeading);
        double mid = ((arm.Left + arm.Right) * 0.5 - j.Centre).Dot(u);
        return Math.Max(0, Math.Max((arm.Left - j.Centre).Dot(u), (arm.Right - j.Centre).Dot(u)) - mid);
    }

    public sealed class SignalStats
    {
        public int Junctions, Inferred, FromData, Arms, Approaches, LeftPockets, RightPockets, StopLines, Groups, TwoLensPedestrian, Invalid;
        /// <summary>Where OSM decides, what the inference rule would have said: both, rule only, OSM only (#348 tuning).</summary>
        public int RuleAndOsm, RuleOnly, OsmOnly, InternalArms;
        public int StopsBack, StopsAtCurve, SignsAboveHeads, BikeRightOnRed, Poles, PolesPastPath, PolesRejected, SignsOnPoles, BikeSignals, Crossings, PathStopLines, Refuges;
        /// <summary>Dashed lines through a junction between two lanes with the same turn (#700).</summary>
        public int PairGuides;
        /// <summary>Pedestrian crossings drawn from OSM crossing nodes (#700): at junctions without lights, and at lit arms with no sidewalk.</summary>
        public int DataCrossings;
        /// <summary>Lights junctions where OSM maps a crossing, and arms with a sidewalk they leave without a crosswalk (#711).</summary>
        public int CrossingsMapped, CrosswalksUnmapped;
        /// <summary>Lit arms with no crosswalk: no pedestrian heads and no pedestrian group (#711).</summary>
        public int ArmsWithoutPedestrians;
        /// <summary>Where the first lights junctions are that leave an arm with a sidewalk without a crosswalk (LV95, #711).</summary>
        public readonly List<string> UnmappedAt = new();
        public readonly List<string> InvalidExamples = new();
        /// <summary>Where the first inferred junctions are (LV95), to look at them (#353).</summary>
        public readonly List<string> InferredAt = new();
        public readonly SortedDictionary<int, int> Cycles = new();

        public string Format()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(c, $"    traffic lights (#348): {Junctions:N0} junctions ({Inferred:N0} inferred, {FromData:N0} from data), {Arms:N0} arms, {Approaches:N0} approaches, ");
            sb.Append(c, $"{LeftPockets:N0} with a left-turn pocket, {RightPockets:N0} with a right-turn pocket, {StopLines:N0} stop lines without a left pocket, {Groups:N0} signal groups, ");
            sb.Append(c, $"{TwoLensPedestrian:N0} with 2-lens pedestrian heads, cycles s: {string.Join(", ", Cycles.Select(kv => $"{kv.Key} x{kv.Value}"))}, invalid plans {Invalid:N0}, dashed lines through the junction between two lanes with the same turn {PairGuides:N0} (#700), crossings from OSM crossing nodes {DataCrossings:N0}, refuges where one crosses an exit hatch {Refuges:N0} (#700), junctions whose crosswalks follow OSM {CrossingsMapped:N0}, arms with a sidewalk and no mapped crossing there {CrosswalksUnmapped:N0}, arms without a pedestrian signal (no crosswalk) {ArmsWithoutPedestrians:N0} (#711)").AppendLine();
            sb.Append(c, $"      where OSM decides, the inference rule agrees on {RuleAndOsm:N0}, adds {RuleOnly:N0} OSM does not have, misses {OsmOnly:N0}; {InternalArms:N0} arms inside a junction of several nodes; inferred at LV95 {string.Join(" ", InferredAt)}; crosswalks left out by OSM at LV95 {string.Join(" ", UnmappedAt)}").AppendLine();
            sb.Append(c, $"      stop lines moved {StopBehindCrosswalk:F0} m behind their crosswalk {StopsBack:N0}, at the kerb's curve start {StopsAtCurve:N0}, approaches where riders turn right on red {BikeRightOnRed:N0} (#711), poles (#350) {Poles:N0} ({PolesPastPath:N0} main poles right of their path, #711), rejected (no clear spot) {PolesRejected:N0}, priority signs moved onto a pole {SignsOnPoles:N0} ({SignsAboveHeads:N0} above the heads, beside a bike head, #711), approaches with a bike signal {BikeSignals:N0} (#351)").AppendLine();
            foreach (var x in InvalidExamples) sb.Append("      invalid: ").Append(x).AppendLine();
            return sb.ToString();
        }
    }

    /// <summary>Why a junction has traffic lights: none, the inference rule, or OSM data (#347).</summary>
    private enum SignalSource : byte { None, Inferred, Data }

    /// <summary>
    /// Where a junction gets traffic lights (#348). Data first: where most of its car arms were
    /// matched to OSM ways (the overlay covers it) and the overlay's signal file exists, it has
    /// lights only if OSM has <c>highway=traffic_signals</c> at it (#347: on the junction's node, or
    /// on an approach of one of its arms' TLM lines, anchored at that line's end), on any junction
    /// shape that can carry them (a T too). Elsewhere the inference rule
    /// (<see cref="PriorityPlanner.InferSignal"/>).
    /// </summary>
    private static SignalSource Lights(Junction j, RoadNetwork net, UrbanField field, SignalSites? sites, SignalStats? stats = null)
    {
        var car = j.Arms.Select(a => net.Links[a.LinkId].Tag as Source)
            .Where(s => s is not null && PriorityPlanner.IsCarRoad(s.Segment.Class)).ToList();
        bool covered = sites is not null && car.Count > 0 && car.Count(s => s!.Line.Osm is not null) * 2 >= car.Count;
        bool rule = PriorityPlanner.InferSignal(j, net, InfoOf, field.Density(j.Centre.X, j.Centre.Y));
        if (!covered) return rule ? SignalSource.Inferred : SignalSource.None;
        var uuids = car.Select(s => s!.Key?.Uuid).Where(u => u is not null).ToHashSet();
        bool osm = sites!.At(j.Centre, uuids!) && PriorityPlanner.SignalShape(j, net, InfoOf, 3) is not null;
        if (stats is not null)
        {
            if (rule && osm) stats.RuleAndOsm++;
            else if (rule) stats.RuleOnly++;
            else if (osm) stats.OsmOnly++;
        }
        return osm ? SignalSource.Data : SignalSource.None;
    }

    /// <summary>
    /// OSM junction signals (#347, <c>osm_nodes.tsv</c>), by where they anchor: a signal on an
    /// approach at its TLM line's end (the junction it stands before), one on a junction node at
    /// itself. Pedestrian-only signals (<c>crossing=traffic_signals</c>) do not make a junction.
    /// </summary>
    internal sealed class SignalSites
    {
        /// <summary>A line end or node this close to a junction's centre is that junction's.</summary>
        private const double Reach = 12.0, Cell = 50.0;
        private readonly Dictionary<(long, long), List<(Vec2 At, string Uuid, bool Node)>> _grid = new();
        public int Count { get; private set; }

        public static SignalSites? From(OsmNodesReader? nodes)
        {
            if (nodes is null) return null;
            var sites = new SignalSites();
            foreach (var e in nodes.All)
            {
                if (e.Kind != OsmNodesReader.NodeKind.Signal || e.PedestrianOnly) continue;
                bool node = e.Junction == OsmNodesReader.JunctionKind.Node;
                if (!node && (e.Junction != OsmNodesReader.JunctionKind.Approach || e.LineEnd == OsmNodesReader.End.None)) continue;
                var at = node ? new Vec2(e.E, e.N) : new Vec2(e.EndE, e.EndN);
                var key = ((long)Math.Floor(at.X / Cell), (long)Math.Floor(at.Y / Cell));
                if (!sites._grid.TryGetValue(key, out var list)) sites._grid[key] = list = new();
                list.Add((at, e.Uuid, node));
                sites.Count++;
            }
            return sites;
        }

        public bool At(Vec2 centre, HashSet<string> armLines)
        {
            long cx = (long)Math.Floor(centre.X / Cell), cy = (long)Math.Floor(centre.Y / Cell);
            for (long x = cx - 1; x <= cx + 1; x++)
            for (long y = cy - 1; y <= cy + 1; y++)
                if (_grid.TryGetValue((x, y), out var list))
                    foreach (var (at, uuid, node) in list)
                        if (at.DistanceTo(centre) <= Reach && (node || armLines.Contains(uuid))) return true;
            return false;
        }
    }

    private static PriorityResult PlanPriority(RoadGenResult result, Func<Junction, SignalSource> signal, SignalStats stats)
    {
        var r = new PriorityResult();
        foreach (var junction in result.Junctions)
        {
            var source = signal(junction);
            bool lights = source != SignalSource.None;
            if (source == SignalSource.Data) r.SignalsFromData.Add(junction.NodeId);
            var plan = PriorityPlanner.Decide(junction, result.Network, InfoOf, lights);
            r.Plans.Add((junction, plan));
            foreach (var arm in plan.Arms)
                if (arm.Role == PriorityPlanner.Role.Yield && arm.Approach)
                    r.Yield[arm.LinkId] = r.FlagsOf(arm.LinkId)
                        | (arm.End == LinkEnd.Start ? RoadAttrFlags.YieldAtStart : RoadAttrFlags.YieldAtEnd);
        }
        return r;
    }

    private static void EmitSignals(PriorityResult priority, RoadGenResult result, Dictionary<(int Node, int Arm), ArmLanes> pockets,
        Func<int, LinkEnd, bool, RoadSide> bikeSideAt,
        HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, List<RoadPaint>> paint,
        Dictionary<TileId, List<RoadSignal>> signals, Cantons? cantons, UrbanField field, Footprints buildings,
        Dictionary<TileId, List<RoadAreaProp>> areas, Dictionary<TileId, List<RoadPointProp>> signs, SignalStats stats,
        Dictionary<TileId, List<RoadApproach>> approaches, Restrictions? restrictions, LaneStats laneStats,
        Dictionary<(int Link, LinkEnd End), double> stopsAt, Dictionary<int, (SignalPlan Plan, int[] PlanArm)> plans,
        Func<int, LinkEnd, bool, RoadSide> streetSideAt, CrossingNodes? crossings = null)
    {
        var net = result.Network;
        PriorityPlanner.Clearance? clearance = null;
        var signalNodes = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Signal).Select(p => p.Junction.NodeId).ToHashSet();
        var signalJunctions = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Signal).ToDictionary(p => p.Junction.NodeId, p => p.Junction);
        foreach (var (junction, plan) in priority.Plans)
        {
            if (plan.Kind != PriorityPlanner.Kind.Signal) continue;
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;

            var arms = new List<SignalArm>();
            var kerbside = new List<(int Index, ApproachLayout Lanes)>();   // layout (a) bike lanes (#351)
            var stops = new List<float>();
            var wantPoles = new List<PoleWish>();
            var islandPoles = new List<(byte Arm, Vec2 At, float Y, Vec2 Facing, Vec2 Across)>();   // #682
            var leftGuides = new List<int>();   // arms with a left pocket: their left turn is guided where its exit has an island (#682)
            var islandArms = new Dictionary<int, IslandExit>();   // arm -> where the lane after its exit island starts (#682)
            var exitArms = new Dictionary<int, IslandExit>();   // arm -> where its exit lane starts at the mouth, no island (#711)
            var approachArms = new List<(int Arm, int PlanArm, float[] Stop)>();   // their lane records (#353)
            var riderArms = new Dictionary<int, (bool OwnLine, int LinkId, LinkEnd End, bool Inside)>();   // per plan arm, for right on red (#711)
            var armInPlan = new int[junction.Arms.Count];   // each junction arm's index in the plan, -1 none (#406)
            Array.Fill(armInPlan, -1);
            // 50 km/h inside a locality: the yellow lasts 3 s (#349)
            bool urban = field.Density(junction.Centre.X, junction.Centre.Y) >= UrbanField.UrbanAt;
            // where OSM maps any crossing round the lights, crosswalks only on its mapped arms; none mapped: every arm with a sidewalk (#711)
            bool crossingsMapped = CrossingsMapped(junction, net, signalNodes, signalJunctions, crossings);
            if (crossingsMapped) stats.CrossingsMapped++;
            for (int i = 0; i < junction.Arms.Count && i < plan.Arms.Count; i++)
            {
                var link = net.Links[plan.Arms[i].LinkId];
                if (link.Tag is not Source source || InfoOf(link) is not { } info || !PriorityPlanner.IsCarRoad(info.Class)) continue;
                var arm = junction.Arms[i];
                bool inside = Internal(link, junction.NodeId, signalNodes);
                bool approach = plan.Arms[i].Approach && !inside, leaves = PriorityPlanner.Leaves(info, plan.Arms[i].End);
                if (inside) stats.InternalArms++;
                var layout = pockets.GetValueOrDefault((junction.NodeId, i))?.Approach;
                bool pocket = layout is { LeftPocket: > 0 }, rightPocket = layout is { Right: true };
                var u = Vec2.FromHeading(arm.OutwardHeading);
                var right = u.Perp;   // the approaching driver's right (they drive along -u)
                var mid = (arm.Left + arm.Right) * 0.5;
                double half = arm.HalfWidth;
                // the approach lanes: from the centre (a one-way road: its left edge) to the right
                // edge, widened by its pockets (#351: the lanes' offsets come from their layout)
                // (#700: a split lead-in moves the approach's centre line and lanes over by its Shift)
                double from = info.Attributes.OneWay != 0 ? -half : -(layout?.Shift ?? 0), to = layout is null ? half : half + layout.EdgeOut;
                // (#711) lanes in place, more toward the junction than away: the approach begins at its own centre line, past the axis
                if (layout is null && info.Attributes.OneWay == 0 && OwnLanes(junction, i, net) is { Lanes: > 1 } own)
                    from = Math.Min(from, own.Centre - own.LaneWidth * 0.5);
                double back = StopBack(junction.NodeId, i);   // behind a crosswalk, where there is room (#711)
                if (back > 0 && approach) stats.StopsBack++;
                if (Math.Abs(back) > 0.05 && Math.Abs(back - StopBehindCrosswalk) > 0.05 && approach) stats.StopsAtCurve++;
                var bar = mid + u * (MouthSkew(junction, arm) + SignalStopSetback + back + SignalStopLine * 0.5);
                if (approach && !pocket && block.Contains(source.Tile))
                {
                    // across the approach's own lane: a right pocket and the bike lane beside it have their own
                    double through = layout is null ? half : half + layout.Through().To - layout.Half - layout.Shift;
                    // (#711) where the cars' line stands back, a kerbside bike lane keeps its riders' own line by the crosswalk
                    var laneSide = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, plan.Arms[i].End == LinkEnd.End);
                    double riders = back > 0 && layout is null && laneSide.HasLane ? laneSide.BikeDm / 10.0 : 0;
                    Get(paint, source.Tile).Add(new RoadPaint
                    {
                        Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = PaintEmitter.White, Width = SignalStopLine,
                        Vertices = Local(source.Tile, [bar + right * (from + 0.1), bar + right * (through - riders - 0.1)], source.SampleHeight, 0f),
                    });
                    if (riders > 0)
                    {
                        var bikeBar = mid + u * (RiderStop(MouthSkew(junction, arm), back) + BikeStopLine * 0.5);
                        Get(paint, source.Tile).Add(new RoadPaint
                        {
                            Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = PaintEmitter.Yellow, Width = BikeStopLine,
                            Vertices = Local(source.Tile, [bikeBar + right * (through - riders + 0.05), bikeBar + right * (through - 0.05)], source.SampleHeight, 0f),
                        });
                    }
                    stats.StopLines++;
                }
                var stop = bar + right * ((from + to) * 0.5);
                if (approach)
                {
                    var local = Local(home, [stop], source.SampleHeight, 0f);
                    stops.AddRange(local);
                    approachArms.Add((i, arms.Count, local));
                    // its painted bike lane stops at the line, solid before it (#406)
                    stopsAt[(plan.Arms[i].LinkId, plan.Arms[i].End)] = RiderStop(MouthSkew(junction, arm), back);
                }
                else stops.AddRange([float.NaN, float.NaN, float.NaN]);
                // poles (#350): on the approach's right the main heads, on its left a second head
                // where the approach has more than one lane; a pedestrian head on both kerbs
                double along = MouthSkew(junction, arm) + PoleAlong;
                var drawnRight = plan.Arms[i].End == LinkEnd.End;   // +Perp(u) is the segment's right side when it ends here
                var sides = CrossSectionPlanner.Attributes(source.Line);
                var rightSide = drawnRight ? sides.Right : sides.Left;
                var leftSide = drawnRight ? sides.Left : sides.Right;
                // the yellow crossing behind the stop line (#682), over the paths beside the carriageway too
                bool crosswalk = false;   // the arm has a sidewalk or path: a crosswalk (#682)
                if (!inside && block.Contains(source.Tile))
                {
                    var streetRight = streetSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, drawnRight);
                    var streetLeft = streetSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, !drawnRight);
                    // a crosswalk where OSM maps a marked crossing on the arm (#700); where it maps none round the junction,
                    // on every arm with a sidewalk (#682). An arm with a sidewalk the mapped junction leaves out has none (#711)
                    (crosswalk, bool osmCrossing, bool sidewalk) = CrosswalkOn(junction, i, net, crossings, crossingsMapped, streetRight, streetLeft);
                    if (osmCrossing && !sidewalk) stats.DataCrossings++;
                    if (crossingsMapped && sidewalk && !osmCrossing && stats.CrosswalksUnmapped++ < 12)
                        stats.UnmappedAt.Add(string.Create(CultureInfo.InvariantCulture, $"{junction.Centre.X:F0},{junction.Centre.Y:F0}"));
                    if (crosswalk)
                        EmitCrossing(paint, source, mid, u, right, MouthSkew(junction, arm) + SignalStopSetback, -(half + (pockets.GetValueOrDefault((junction.NodeId, i))?.ExitWidening ?? 0)), to, streetRight, streetLeft, areas, stats,
                            pockets.GetValueOrDefault((junction.NodeId, i)) is { ExitWay: { } edgeWay, ExitFar: false } ? s => (edgeWay.OuterEdge(Math.Max(s, 0)).P - (mid + u * s)).Dot(right) : null,
                            insetLeft: junction.KerbInset.GetValueOrDefault((i, false)), insetRight: junction.KerbInset.GetValueOrDefault((i, true)),   // diagonal beside a tight corner (#700)
                            linkId: plan.Arms[i].LinkId, riderStop: RiderStop(MouthSkew(junction, arm), back));
                }
                // none on a link inside a junction of several nodes: its ends are the junction's own. Pedestrian heads only
                // where the arm has a crosswalk (#711); an arm whose street is in another block keeps them
                bool pedestrians = inside || crosswalk || !block.Contains(source.Tile);
                var pedFlag = pedestrians ? SignalPoleFlags.Pedestrian : 0;
                if (!pedestrians) stats.ArmsWithoutPedestrians++;
                var mainFlags = inside ? 0 : (approach ? SignalPoleFlags.Main : 0) | pedFlag;
                // (#711, the user's review) the second head wherever the approach has more than one lane: a pocket, or lanes of its own
                // from OSM's lane data (they had none)
                bool lanesOwn = pockets.GetValueOrDefault((junction.NodeId, i))?.OwnMoves is { Length: > 1 };
                var secondFlags = inside ? 0 : (approach && (pocket || rightPocket || lanesOwn) ? SignalPoleFlags.Second : 0) | pedFlag;
                if (!inside && approach) leftGuides.Add(i);   // a left pocket's lane, or the through lane's left edge where the left turn shares it
                // the left repeater signal stands on a small island in the hatched median behind the stop line, not on the far kerb (#682)
                if (!inside && approach && pocket && pockets.GetValueOrDefault((junction.NodeId, i)) is { ExitWay: { } exitWay, ExitFar: false })
                {
                    double zebraAt = MouthSkew(junction, arm) + SignalStopSetback, zebraTo = zebraAt - ZebraClear;
                    if (exitWay.Islands(Get(areas, exitWay.Tile), zebraAt + back, zebraTo - ZebraDepth, zebraTo, crosswalk) is { } island)
                    {
                        secondFlags &= ~SignalPoleFlags.Second;
                        exitWay.HasIsland = true;   // the through guide passes it (#711)
                        islandPoles.Add(((byte)arms.Count, island.Pole, island.Y, u, right));
                        var exitSide = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, !drawnRight);
                        islandArms[i] = new IslandExit(exitWay.HatchAt(0) + exitWay.Frame0, exitWay.ExitCar, exitSide.HasTrack || exitSide.HasLane);   // from the axis (#700)
                    }
                }
                if (!inside && leaves && !crosswalk && !islandArms.ContainsKey(i))
                    _exitBikeFrom![(plan.Arms[i].LinkId, plan.Arms[i].End)] = GuideEdge(junction, i);
                if (!inside && leaves)
                {
                    // the exit lane at the mouth (#711): past the exit's hatch where a pocket's through lane widened it, else from the centre line
                    var exitSide = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, !drawnRight);
                    double bikeLane = exitSide.HasLane ? exitSide.BikeDm / 10.0 : 0;
                    exitArms[i] = pockets.GetValueOrDefault((junction.NodeId, i)) is { ExitWay: { } way, ExitFar: false }
                        ? new IslandExit(way.HatchAt(0) + way.Frame0, way.ExitCar, exitSide.HasTrack || exitSide.HasLane)
                        : new IslandExit(info.Attributes.OneWay != 0 ? -half : 0, (info.Attributes.OneWay != 0 ? 2 * half - bikeLane : half - bikeLane)
                            + (pockets.GetValueOrDefault((junction.NodeId, i))?.ExitWidening ?? 0), exitSide.HasTrack || exitSide.HasLane);   // (#711: an exit widened beside its bike lane)
                }
                // (#711, the user's review) beside a crosswalk whose cars' line stands back the poles stand by it, on its near side;
                // with no room for that line they stay on its far side
                double poleAlong = crosswalk && back > 0 ? MouthSkew(junction, arm) + SignalStopSetback - ZebraClear + PoleBehindCrosswalk : along;
                // the main pole stands right of the approach's separated path where the path stops at its own line (the user's
                // rule, #711: riders wait beside it, the pole is not between them and the road); a path with no line (no
                // crosswalk) keeps it at the kerb. (The bike head is on this pole: a bike head on its own pole would keep it too.)
                var pathSide = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, drawnRight);
                bool pastPath = crosswalk && pathSide.HasTrack;
                if (pastPath) stats.PolesPastPath++;
                wantPoles.Add(new PoleWish((byte)arms.Count, source, mid + u * poleAlong, right, to, u, -right,
                    pathSide.OuterDm > 0 ? pathSide : rightSide, pastPath ? (pathSide.VergeDm + pathSide.BikeDm) / 10.0 + PoleClear : PoleClear,
                    mainFlags, plan.Arms[i].LinkId));
                // the left kerb stands out by the exit widening of the opposite approach's pocket (#123):
                // measured from the old edge, every spot tried was on that strip (#386)
                double exitWidening = pockets.GetValueOrDefault((junction.NodeId, i))?.ExitWidening ?? 0;
                var exitPathSide = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, !drawnRight);
                wantPoles.Add(new PoleWish((byte)arms.Count, source, mid + u * poleAlong, -right, half + exitWidening, u, right,
                    exitPathSide.OuterDm > 0 ? exitPathSide : leftSide, PoleClear, secondFlags, -1));
                // a bike head (#351) beside a separated path, or a kerbside bike lane a right pocket's cars
                // cross (layout (a)); a bike lane between the pocket and the through lane (b) goes with the
                // cars. The path or lane as built at the arm's end (the line's own sides do not know a
                // street's paths, #120)
                var riderSide = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, drawnRight);
                bool path = riderSide.HasTrack;
                bool bikeSignal = approach && (path || layout is { KerbsideBike: true });
                // (#711, SSV Art. 69a) riders turning right on red need a line of their own ahead of the cars': a path stopping at
                // its line by the crosswalk, a kerbside bike lane whose cars' line stands back, or a right-turn lane of its own
                bool ownLine = approach && (crosswalk && path || back > 0 && layout is null && riderSide.HasLane || rightPocket);
                riderArms[arms.Count] = (ownLine, plan.Arms[i].LinkId, plan.Arms[i].End, inside);
                if (bikeSignal) stats.BikeSignals++;
                if (approach && !path && layout is { KerbsideBike: true }) kerbside.Add((arms.Count, layout));
                armInPlan[i] = arms.Count;
                // a lane of its own for the left turn (a pocket, or one of the carriageway's lanes OSM marks left only, #700) gets its own phase
                bool leftLane = pocket || pockets.GetValueOrDefault((junction.NodeId, i))?.OwnMoves is { } ownMoves && ownMoves.Any(m => m == SignalMoves.Left);
                arms.Add(new SignalArm(arm.OutwardHeading, approach, leaves, leftLane, rightPocket, Pedestrians: pedestrians,
                    BikeSignal: bikeSignal, SpeedKmh: urban ? 50 : 60, CrossingM: (float)(to - from + (info.Attributes.OneWay != 0 ? 0 : half)),
                    Rank: (byte)Math.Clamp(PriorityPlanner.Rank(info) / 4, 1, 255), Banned: approach ? BannedTurns(junction, i, net, pockets, restrictions) : SignalMoves.None));
                stats.Arms++;
                if (approach) stats.Approaches++;
                if (leftLane) stats.LeftPockets++;
                if (rightPocket) stats.RightPockets++;
            }
            if (arms.Count(a => a.In) < 2) continue;

            string? canton = cantons?.CodeAt(junction.Centre.X, junction.Centre.Y);
            bool amber = PedestrianAmber(canton);
            uint seed = (uint)(long)Math.Round(junction.Centre.X) * 73856093u ^ (uint)(long)Math.Round(junction.Centre.Y) * 19349663u;
            // riders may turn right on red (the user's rule, #711: SSV Art. 69a, signal 5.18) where the street directly on the right
            // has a bike lane or path on the side they join and the approach gives them a line of their own
            for (int a = 0; a < arms.Count; a++)
            {
                if (!arms[a].In || !riderArms.TryGetValue(a, out var own) || !own.OwnLine) continue;
                int target = -1;
                double sharpest = 0;
                for (int k = 0; k < arms.Count; k++)
                {
                    if (k == a || !arms[k].Out || SignalPlan.Turn(arms, a, k) != SignalMoves.Right) continue;
                    double delta = Math.IEEERemainder(arms[k].Heading - (arms[a].Heading + Math.PI), 2 * Math.PI);
                    if (target < 0 || delta < sharpest) (target, sharpest) = (k, delta);
                }
                if (target < 0 || !riderArms.TryGetValue(target, out var exit) || exit.Inside) continue;
                var joined = bikeSideAt(exit.LinkId, exit.End, exit.End != LinkEnd.End);   // the side going away from the junction
                if (!joined.HasTrack && !joined.HasLane) continue;
                arms[a] = arms[a] with { BikeRightOnRed = true };
                stats.BikeRightOnRed++;
            }
            var signalPlan = SignalPlan.Build(arms, seed, amber);
            // layout (a) only where the plan gives the kerbside bike lane a phase with the right arrow
            // red; else the weave must come before the line: layout (b), and the plan again (#351)
            for (bool forced = true; forced;)
            {
                forced = false;
                foreach (var (index, lanes) in kerbside)
                {
                    if (lanes.BikeBetween || signalPlan.ThroughWithRightHeld(index)) continue;
                    lanes.BikeBetween = lanes.Forced = true;
                    arms[index] = arms[index] with { BikeSignal = false };
                    stats.BikeSignals--;
                    signalPlan = SignalPlan.Build(arms, seed, amber);
                    forced = true;
                    break;
                }
            }
            // a bike group that finds no phase (a T with a right pocket whose arrow always runs with the through lane) goes without its signal, not the junction without its plan (#682)
            if (signalPlan.Validate().Any(e => e.Contains("(Bike arm")) && arms.Any(a => a.BikeSignal))
            {
                stats.BikeSignals -= arms.Count(a => a.BikeSignal);
                for (int a = 0; a < arms.Count; a++) arms[a] = arms[a] with { BikeSignal = false };
                signalPlan = SignalPlan.Build(arms, seed, amber);
            }
            if (signalPlan.Validate() is { Count: > 0 } errors)
            {
                stats.Invalid++;
                if (stats.InvalidExamples.Count < 5)
                    stats.InvalidExamples.Add(string.Create(CultureInfo.InvariantCulture,
                        $"LV95 {junction.Centre.X:F0},{junction.Centre.Y:F0} {arms.Count} arms ({string.Join(" ", arms.Select(a => $"{a.Heading * 180 / Math.PI:F0}{(a.In ? "i" : "")}{(a.Out ? "o" : "")}{(a.LeftPocket ? "L" : "")}{(a.RightPocket ? "R" : "")}r{a.Rank}"))}): {string.Join("; ", errors.Take(2))}"));
                continue;
            }

            var anchors = Anchors(junction, net);
            var centre = Local(home, [junction.Centre], p => HeightAt(anchors, p), 0f);
            clearance ??= new PriorityPlanner.Clearance(result.Ribbons, result.Junctions);
            var poles = new List<SignalPole>();
            foreach (var wish in wantPoles)
            {
                if (wish.Flags == SignalPoleFlags.None) continue;
                if (PlacePole(wish, home, clearance, buildings, areas) is not { } pole) { stats.PolesRejected++; continue; }
                poles.Add(pole);
                stats.Poles++;
                // the approach's priority sign (#121, for when the lights are off) on the main pole, below the heads; above them
                // where a bike head takes the room below (#711, the user's review)
                bool bikeHead = signalPlan.Groups.Any(g => g.Kind == SignalGroupKind.Bike && g.Arm == wish.Arm);
                bool pedHead = (wish.Flags & SignalPoleFlags.Pedestrian) != 0 && signalPlan.Groups.Any(g => g.Kind == SignalGroupKind.Pedestrian && g.Arm == wish.Arm);
                if (wish.SignLink >= 0 && priority.SignsOf.TryGetValue(wish.SignLink, out var signSpots))
                    foreach (var (signTile, index) in signSpots)
                    {
                        if (!signs.TryGetValue(signTile, out var list) || index >= list.Count) continue;
                        var sign = list[index];
                        if (Math.Abs(Math.IEEERemainder(sign.Heading - pole.CarHeading, 2 * Math.PI)) > 0.5) continue;
                        list[index] = sign with
                        {
                            X = (float)(pole.X + home.MinE - signTile.MinE), Y = pole.Y, Z = (float)(pole.Z + signTile.MaxN - home.MaxN),
                            Height = bikeHead ? (pedHead ? CarHeadsTopAbovePedestrian : CarHeadsTop) + SignAboveHeads + RoadSigns.PlateHeight(sign.Type, sign.Variant)
                                : SignOnPoleTop,
                            Flags = sign.Flags | PropFlags.OnSignalPole,
                        };
                        stats.SignsOnPoles++;
                        if (bikeHead) stats.SignsAboveHeads++;
                    }
            }
            // where the left turn exits beside an island it is guided through the junction: two dashed lines along its path (#682)
            foreach (int gi in leftGuides)
                if (EmitLeftGuides(paint, home, junction, gi, pockets.GetValueOrDefault((junction.NodeId, gi))?.Approach, anchors, islandArms, exitArms) is int into and >= 0)
                    priority.LeftGuideInto.Add((junction.NodeId, into));   // a through guide to the same exit is left out (#711)
            foreach (var ip in islandPoles)
            {
                var local = Local(home, [ip.At], _ => ip.Y, 0f);
                poles.Add(new SignalPole(local[0], local[1], local[2], Heading(ip.Facing), Heading(ip.Across), ip.Arm, SignalPoleFlags.Second));
                stats.Poles++;
            }
            signalPlan.ArrowPlates = ArrowPlates(canton);
            plans[junction.NodeId] = (signalPlan, armInPlan);   // the bike crossings' conflicts (#406)
            Get(signals, home).Add(new RoadSignal { X = centre[0], Y = centre[1], Z = centre[2], Stops = stops.ToArray(), Plan = signalPlan, Poles = poles });
            var records = new List<(int Arm, RoadApproach Record)>();
            foreach (var (i, planArm, stopAt) in approachArms)
            {
                var record = SignalApproach(junction, i, net, (short)(Get(signals, home).Count - 1), (byte)planArm, stopAt,
                    signalPlan, pockets.GetValueOrDefault((junction.NodeId, i)), restrictions, laneStats);
                Get(approaches, home).Add(record);
                records.Add((i, record));
            }
            // two lanes side by side with the same turn stay apart through the junction: a dashed line between them (#700)
            stats.PairGuides += EmitPairGuides(paint, home, junction, net, records, anchors);
            stats.Junctions++;
            if (priority.SignalsFromData.Contains(junction.NodeId)) stats.FromData++;
            else
            {
                stats.Inferred++;
                if (stats.InferredAt.Count < 8) stats.InferredAt.Add(string.Create(CultureInfo.InvariantCulture, $"{junction.Centre.X:F0},{junction.Centre.Y:F0}"));
            }
            stats.Groups += signalPlan.Groups.Count;
            if (!amber) stats.TwoLensPedestrian++;
            int cycle = (int)MathF.Round(signalPlan.Cycle);
            stats.Cycles[cycle] = stats.Cycles.GetValueOrDefault(cycle) + 1;
        }
    }

    /// <summary>
    /// A pole wanted beside an arm (#350): the arm's plan index, its source line, the point on the
    /// arm's axis it stands beside, the way out to its kerb and the kerb's distance, the way its
    /// car heads and its pedestrian head face (plan view), the street side it stands on and how far
    /// out of the kerb it is first tried, what it carries, and the link whose priority sign moves
    /// onto it (-1 none).
    /// </summary>
    private sealed record PoleWish(byte Arm, Source Source, Vec2 Axis, Vec2 Out, double Kerb, Vec2 CarFacing, Vec2 PedFacing,
        RoadSide Side, double Start, SignalPoleFlags Flags, int SignLink);

    /// <summary>
    /// The first spot out from the kerb that is clear of carriageways, widenings, buildings and the side's bike path (#711: a pole
    /// stood on it), in the home tile's frame, its foot on the side's profile there; null when none is.
    /// </summary>
    private static SignalPole? PlacePole(PoleWish wish, TileId home, PriorityPlanner.Clearance clearance, Footprints buildings,
        Dictionary<TileId, List<RoadAreaProp>> areas)
    {
        if (wish.Flags == SignalPoleFlags.None) return null;
        var side = wish.Side;
        double r = RoadSigns.PoleDiameter * 0.5, pathFrom = side.VergeDm / 10.0, pathTo = (side.VergeDm + side.BikeDm) / 10.0;
        // out from the kerb; where every spot there is taken (another road, a widening strip), a little further out along the arm
        // (#711, the user's review: an approach was left without its pole)
        foreach (double back in (ReadOnlySpan<double>)[0, PoleBackStep, 2 * PoleBackStep, 3 * PoleBackStep, 4 * PoleBackStep])
        for (int k = 0; k < PoleTries; k++)
        {
            double off = wish.Start + k * PoleStep;
            if (side.HasTrack && off + r > pathFrom && off - r < pathTo) continue;
            var at = wish.Axis + wish.CarFacing * back + wish.Out * (wish.Kerb + off);
            if (!clearance.IsClear(at, RoadSigns.PoleDiameter) || buildings.Contains(at) || OnPavement(at, areas)) continue;
            float road = wish.Source.SampleHeight(wish.Axis + wish.CarFacing * back + wish.Out * wish.Kerb);
            float foot = side.OuterDm > 0 ? RoadStreetSection.HeightAt(side, (float)off) : 0f;
            var local = Local(home, [at], _ => road + foot, 0f);
            return new SignalPole(local[0], local[1], local[2], Heading(wish.CarFacing), Heading(wish.PedFacing), wish.Arm, wish.Flags);
        }
        return null;
    }

    /// <summary>Radians about +Y, 0 facing -Z (north), facing along a plan-view direction (as the signs).</summary>
    private static float Heading(Vec2 facing) => (float)Math.Atan2(-facing.X, facing.Y);

    /// <summary>Whether a point stands on a widening strip (#123/#348) of its tile.</summary>
    private static bool OnPavement(Vec2 p, Dictionary<TileId, List<RoadAreaProp>> areas)
    {
        var tile = TileId.FromLv95(p.X, p.Y);
        if (!areas.TryGetValue(tile, out var list)) return false;
        double x = p.X - tile.MinE, z = tile.MaxN - p.Y;
        foreach (var a in list)
        {
            if (a.Type != AreaPropType.Pavement) continue;
            var v = a.Vertices;
            for (int t = 0; t + 2 < a.Indices.Length; t += 3)
            {
                int i0 = a.Indices[t] * 3, i1 = a.Indices[t + 1] * 3, i2 = a.Indices[t + 2] * 3;
                if (InTriangle2(x, z, v[i0], v[i0 + 2], v[i1], v[i1 + 2], v[i2], v[i2 + 2])) return true;
            }
        }
        return false;
    }

    private static bool InTriangle2(double px, double pz, double ax, double az, double bx, double bz, double cx, double cz)
    {
        double d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        double d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
        double d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>
    /// Pedestrian heads depend on the canton: Geneva's have two lenses (green, then red), Vaud's
    /// three (with a yellow). Cantons not checked yet keep three (#350 records the table).
    /// </summary>
    private static bool PedestrianAmber(string? canton) => canton != "GE";

    /// <summary>
    /// Whether the canton's white plates run on below the car and bike heads with their arrows on
    /// them, its bike heads on one too (<see cref="SignalPlan.ArrowPlates"/>): Vaud does, Geneva
    /// does not (#759, the user, 2026-10-10). Cantons not checked yet do not; add a canton's code
    /// here once it is known.
    /// </summary>
    private static bool ArrowPlates(string? canton) => canton == "VD";

    /// <summary>
    /// The canton of each kilometre tile, from MapSetup's committed <c>switzerland.bin</c>
    /// (<c>tools/MapSetup/CountryData.cs</c> writes it; this reads only the canton lattice and names),
    /// or a test world's own zones (<see cref="Of"/>).
    /// </summary>
    public sealed class Cantons
    {
        private const int MinE = 2480, MinN = 1070, Width = 360, Height = 230;
        private readonly byte[] _ids;
        private readonly List<string> _codes;
        private readonly IReadOnlyDictionary<(int E, int N), string>? _tiles;

        private Cantons(byte[] ids, List<string> codes) { _ids = ids; _codes = codes; }

        private Cantons(IReadOnlyDictionary<(int E, int N), string> tiles) { _ids = []; _codes = []; _tiles = tiles; }

        /// <summary>A test world's cantons (#1038): the code of each kilometre tile listed, none elsewhere (the default zone).</summary>
        public static Cantons Of(IReadOnlyDictionary<(int E, int N), string> tiles) => new(tiles);

        public string? CodeAt(double e, double n)
        {
            int te = (int)Math.Floor(e / 1000), tn = (int)Math.Floor(n / 1000);
            if (_tiles is not null) return _tiles.GetValueOrDefault((te, tn));
            if (te < MinE || tn < MinN || te >= MinE + Width || tn >= MinN + Height) return null;
            int id = _ids[(tn - MinN) * Width + (te - MinE)];
            return id == 0 || id > _codes.Count ? null : _codes[id - 1];
        }

        /// <summary>The repository's country file, looked for upward from the tool and the working directory; null when missing.</summary>
        public static Cantons? Find()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                {
                    string path = Path.Combine(dir.FullName, "tools", "MapSetup", "switzerland.bin");
                    if (File.Exists(path)) return Load(path);
                }
            return null;
        }

        public static Cantons? Load(string path)
        {
            try
            {
                using var file = File.OpenRead(path);
                using var z = new GZipStream(file, CompressionMode.Decompress);
                using var r = new BinaryReader(z, Encoding.UTF8);
                if (r.ReadString() != "CHMAP1") return null;
                r.ReadInt64();                          // baked at
                r.ReadBytes(Width * Height * 4);        // zip sizes
                r.ReadBytes(Width * Height);            // survey years
                var ids = r.ReadBytes(Width * Height);  // cantons
                r.ReadBytes(Width * Height * 2);        // max elevations
                var codes = new List<string>();
                for (int i = r.ReadInt32(); i > 0; i--)
                {
                    r.ReadByte();
                    codes.Add(r.ReadString());
                    r.ReadString();
                }
                return new Cantons(ids, codes);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
            {
                return null;
            }
        }
    }
}
