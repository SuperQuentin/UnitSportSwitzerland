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
    /// Signal poles (#350) stand this far past the mouth along their arm, at the kerb plus
    /// <see cref="PoleClear"/>, stepping out by <see cref="PoleStep"/> up to <see cref="PoleTries"/>
    /// times until clear of every carriageway, widening and building. With the stop line
    /// <see cref="SignalStopSetback"/> back that leaves the 4 m a driver needs to see a roadside
    /// head (Kanton Bern Handbuch Markierung).
    /// </summary>
    private const double PoleAlong = 0.4, PoleClear = 0.5, PoleStep = 0.4;
    private const int PoleTries = 6;

    /// <summary>A priority sign on a signal pole (#350) has its plate's top this high: below the heads' 2.35 m (SSV Art. 71).</summary>
    private const float SignOnPoleTop = 2.25f;

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
        double length = 0;
        for (int i = 1; i < link.Centreline.Count; i++) length += link.Centreline[i].DistanceTo(link.Centreline[i - 1]);
        return length < InternalLinkM;
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
                int other = link.StartNode == node.NodeId ? link.EndNode : link.StartNode;
                if (Internal(link, node.NodeId, signalNodes) && seen.Add(other) && signalJunctions.TryGetValue(other, out var next)) todo.Push(next);
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
        public int Poles, PolesRejected, SignsOnPoles, BikeSignals, Crossings, PathStopLines, Refuges;
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
            sb.Append(c, $"      poles (#350) {Poles:N0}, rejected (no clear spot) {PolesRejected:N0}, priority signs moved onto a pole {SignsOnPoles:N0}, approaches with a bike signal {BikeSignals:N0} (#351)").AppendLine();
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
                var bar = mid + u * (MouthSkew(junction, arm) + SignalStopSetback + SignalStopLine * 0.5);
                if (approach && !pocket && block.Contains(source.Tile))
                {
                    // across the approach's own lane: a right pocket and the bike lane beside it have their own
                    double through = layout is null ? half : half + layout.Through().To - layout.Half - layout.Shift;
                    Get(paint, source.Tile).Add(new RoadPaint
                    {
                        Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = PaintEmitter.White, Width = SignalStopLine,
                        Vertices = Local(source.Tile, [bar + right * (from + 0.1), bar + right * (through - 0.1)], source.SampleHeight, 0f),
                    });
                    stats.StopLines++;
                }
                var stop = bar + right * ((from + to) * 0.5);
                if (approach)
                {
                    var local = Local(home, [stop], source.SampleHeight, 0f);
                    stops.AddRange(local);
                    approachArms.Add((i, arms.Count, local));
                    // its painted bike lane stops at the line, solid before it (#406)
                    stopsAt[(plan.Arms[i].LinkId, plan.Arms[i].End)] = MouthSkew(junction, arm) + SignalStopSetback;
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
                    bool osmCrossing = crossings?.OnArm(junction, i, net) is not null, sidewalk = streetRight.SidewalkDm > 0 || streetLeft.SidewalkDm > 0;
                    if (osmCrossing && !sidewalk) stats.DataCrossings++;
                    if (crossingsMapped && sidewalk && !osmCrossing && stats.CrosswalksUnmapped++ < 12)
                        stats.UnmappedAt.Add(string.Create(CultureInfo.InvariantCulture, $"{junction.Centre.X:F0},{junction.Centre.Y:F0}"));
                    if (crosswalk = osmCrossing || sidewalk && !crossingsMapped && JunctionRules.Lights.Has(JunctionRule.CrosswalkOnSidewalkArms))
                        EmitCrossing(paint, source, mid, u, right, MouthSkew(junction, arm) + SignalStopSetback, -(half + (pockets.GetValueOrDefault((junction.NodeId, i))?.ExitWidening ?? 0)), to, streetRight, streetLeft, areas, stats,
                            pockets.GetValueOrDefault((junction.NodeId, i)) is { ExitWay: { } edgeWay, ExitFar: false } ? s => (edgeWay.OuterEdge(Math.Max(s, 0)).P - (mid + u * s)).Dot(right) : null,
                            insetLeft: junction.KerbInset.GetValueOrDefault((i, false)), insetRight: junction.KerbInset.GetValueOrDefault((i, true)));   // diagonal beside a tight corner (#700)
                }
                // none on a link inside a junction of several nodes: its ends are the junction's own. Pedestrian heads only
                // where the arm has a crosswalk (#711); an arm whose street is in another block keeps them
                bool pedestrians = inside || crosswalk || !block.Contains(source.Tile);
                var pedFlag = pedestrians ? SignalPoleFlags.Pedestrian : 0;
                if (!pedestrians) stats.ArmsWithoutPedestrians++;
                var mainFlags = inside ? 0 : (approach ? SignalPoleFlags.Main : 0) | pedFlag;
                var secondFlags = inside ? 0 : (approach && (pocket || rightPocket) ? SignalPoleFlags.Second : 0) | pedFlag;
                if (!inside && approach) leftGuides.Add(i);   // a left pocket's lane, or the through lane's left edge where the left turn shares it
                // the left repeater signal stands on a small island in the hatched median behind the stop line, not on the far kerb (#682)
                if (!inside && approach && pocket && pockets.GetValueOrDefault((junction.NodeId, i)) is { ExitWay: { } exitWay, ExitFar: false })
                {
                    double stopAtArm = MouthSkew(junction, arm) + SignalStopSetback, zebraTo = stopAtArm - ZebraClear;
                    if (exitWay.Islands(Get(areas, exitWay.Tile), stopAtArm, zebraTo - ZebraDepth, zebraTo, crosswalk) is { } island)
                    {
                        secondFlags &= ~SignalPoleFlags.Second;
                        exitWay.HasIsland = true;   // the through guide passes it (#711)
                        islandPoles.Add(((byte)arms.Count, island.Pole, island.Y, u, right));
                        var exitSide = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, !drawnRight);
                        islandArms[i] = new IslandExit(exitWay.HatchAt(0) + exitWay.Frame0, exitWay.ExitCar, exitSide.HasTrack || exitSide.HasLane);   // from the axis (#700)
                    }
                }
                if (!inside && leaves)
                {
                    // the exit lane at the mouth (#711): past the exit's hatch where a pocket's through lane widened it, else from the centre line
                    var exitSide = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, !drawnRight);
                    double bikeLane = exitSide.HasLane ? exitSide.BikeDm / 10.0 : 0;
                    exitArms[i] = pockets.GetValueOrDefault((junction.NodeId, i)) is { ExitWay: { } way, ExitFar: false }
                        ? new IslandExit(way.HatchAt(0) + way.Frame0, way.ExitCar, exitSide.HasTrack || exitSide.HasLane)
                        : new IslandExit(info.Attributes.OneWay != 0 ? -half : 0, info.Attributes.OneWay != 0 ? 2 * half - bikeLane : half - bikeLane, exitSide.HasTrack || exitSide.HasLane);
                }
                wantPoles.Add(new PoleWish((byte)arms.Count, source, mid + u * along, right, to, u, -right,
                    rightSide.OuterDm > 0 ? rightSide.KerbCm / 100f : 0f, mainFlags, plan.Arms[i].LinkId));
                // the left kerb stands out by the exit widening of the opposite approach's pocket (#123):
                // measured from the old edge, every spot tried was on that strip (#386)
                double exitWidening = pockets.GetValueOrDefault((junction.NodeId, i))?.ExitWidening ?? 0;
                wantPoles.Add(new PoleWish((byte)arms.Count, source, mid + u * along, -right, half + exitWidening, u, right,
                    leftSide.OuterDm > 0 ? leftSide.KerbCm / 100f : 0f, secondFlags, -1));
                // a bike head (#351) beside a separated path, or a kerbside bike lane a right pocket's cars
                // cross (layout (a)); a bike lane between the pocket and the through lane (b) goes with the
                // cars. The path or lane as built at the arm's end (the line's own sides do not know a
                // street's paths, #120)
                bool path = bikeSideAt(plan.Arms[i].LinkId, plan.Arms[i].End, drawnRight).HasTrack;
                bool bikeSignal = approach && (path || layout is { KerbsideBike: true });
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

            bool amber = PedestrianAmber(cantons?.CodeAt(junction.Centre.X, junction.Centre.Y));
            uint seed = (uint)(long)Math.Round(junction.Centre.X) * 73856093u ^ (uint)(long)Math.Round(junction.Centre.Y) * 19349663u;
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
                // the approach's priority sign (#121, for when the lights are off) on the main pole, below the heads
                if (wish.SignLink >= 0 && priority.SignsOf.TryGetValue(wish.SignLink, out var signSpots))
                    foreach (var (signTile, index) in signSpots)
                    {
                        if (!signs.TryGetValue(signTile, out var list) || index >= list.Count) continue;
                        var sign = list[index];
                        if (Math.Abs(Math.IEEERemainder(sign.Heading - pole.CarHeading, 2 * Math.PI)) > 0.5) continue;
                        list[index] = sign with
                        {
                            X = (float)(pole.X + home.MinE - signTile.MinE), Y = pole.Y, Z = (float)(pole.Z + signTile.MaxN - home.MaxN),
                            Height = SignOnPoleTop,
                        };
                        stats.SignsOnPoles++;
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
    /// car heads and its pedestrian head face (plan view), the kerb height, what it carries, and
    /// the link whose priority sign moves onto it (-1 none).
    /// </summary>
    private sealed record PoleWish(byte Arm, Source Source, Vec2 Axis, Vec2 Out, double Kerb, Vec2 CarFacing, Vec2 PedFacing,
        float KerbHeight, SignalPoleFlags Flags, int SignLink);

    /// <summary>The first spot out from the kerb that is clear of carriageways, widenings and buildings, in the home tile's frame; null when none is.</summary>
    private static SignalPole? PlacePole(PoleWish wish, TileId home, PriorityPlanner.Clearance clearance, Footprints buildings,
        Dictionary<TileId, List<RoadAreaProp>> areas)
    {
        if (wish.Flags == SignalPoleFlags.None) return null;
        for (int k = 0; k < PoleTries; k++)
        {
            var at = wish.Axis + wish.Out * (wish.Kerb + PoleClear + k * PoleStep);
            if (!clearance.IsClear(at, RoadSigns.PoleDiameter) || buildings.Contains(at) || OnPavement(at, areas)) continue;
            float road = wish.Source.SampleHeight(wish.Axis + wish.Out * wish.Kerb);
            var local = Local(home, [at], _ => road + wish.KerbHeight, 0f);
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
    /// The canton of each kilometre tile, from MapSetup's committed <c>switzerland.bin</c>
    /// (<c>tools/MapSetup/CountryData.cs</c> writes it; this reads only the canton lattice and names).
    /// </summary>
    public sealed class Cantons
    {
        private const int MinE = 2480, MinN = 1070, Width = 360, Height = 230;
        private readonly byte[] _ids;
        private readonly List<string> _codes;

        private Cantons(byte[] ids, List<string> codes) { _ids = ids; _codes = codes; }

        public string? CodeAt(double e, double n)
        {
            int te = (int)Math.Floor(e / 1000), tn = (int)Math.Floor(n / 1000);
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
