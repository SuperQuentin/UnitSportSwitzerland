namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Left-turn pockets (#123) on main-road approaches. Where a two-way Major or Road approach of a
/// main road (#121) has a yielding car road to its left, the approach is widened by one lane on
/// its right over a taper and a storage length: the original approach lane becomes the left-turn
/// pocket and through traffic moves into the new lane. Past the junction the main road's other
/// arm takes that lane on, widened by the same lane at the mouth and tapering back, with a hatched
/// median between the centre line and the through lane's left edge that brings the lane back to
/// its place. The ribbon keeps one width per segment, so each widening is a flush
/// <see cref="AreaPropType.Pavement"/> strip along a segment's edge (the road blend holds the
/// ground under it at the road's height, so it is in the collision too). A pocket is placed only
/// when both its approach and its exit fit. What each approach got is returned for the signal
/// plans (#348) and the lane records traffic drives (#353, <c>TileRewriter.Lanes</c>).
/// </summary>
public static partial class TileRewriter
{
    public sealed class TurnLaneStats
    {
        public int AtSignals, RightPockets, RightRejected;
        public int LeftBikeLanes, BikeBoxes, AdvancedBikeLines, KerbsideBike, BetweenBike, BetweenForced;
        public int Candidates, Placed, Merged, Short, Building, OtherLine, Ground, Seam, NoSegment, NoExit, Arrows, Stripes, StopBars, SignsMoved, BesideBike, LeadIns;
        /// <summary>Approaches whose carriageway already holds two or more lanes toward the junction: no pocket is built (#700).</summary>
        public int MultiLane;
        /// <summary>Approaches with OSM lane data at the junction end (#700), lanes of it that fitted nowhere, a road leaves left but no lane wishes a left turn, lanes assigned in place with arrows, arrows painted on them.</summary>
        public int Wished, WishFolded, WishNoLeft, WishInPlace, WishArrows;
        /// <summary>Junction corners left tight: no right turn rounds them (#700).</summary>
        public int TightCorners;
        /// <summary>Lead-ins split between both edges (#700).</summary>
        public int SplitLeadIns, ThroughGuides, MirrorGuides, PairGuidesNoLights, SolidCentres, SpeedFast, SpeedSlow, SpeedNoneTown, SpeedNoneRural;
        /// <summary>Hatched medians left out as too narrow or too short (#406).</summary>
        public int HatchesSkipped;
        /// <summary>Kerb corners paved beside a widening (#406), and those whose outline did not work out.</summary>
        public int Corners, CornersRejected, CornersInTown;
        /// <summary>Pockets placed per storage length, metres.</summary>
        public readonly SortedDictionary<double, int> Storage = new();
        /// <summary>Where the right pockets beside a bike lane are, by layout (#351): up to 5 junctions each.</summary>
        public readonly SortedDictionary<string, List<string>> LayoutExamples = new();

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    turn lanes (#123): {Candidates:N0} main-road approaches with a left turn, {Placed:N0} pockets placed with their exit taper (storage m: {string.Join(", ", Storage.Select(kv => $"{kv.Key:F0} x{kv.Value}"))}), {Merged:N0} of them merged with the exit of the junction before (#325), {AtSignals:N0} at traffic lights (#348), {RightPockets:N0} right-turn pockets (approaches with a right turn faster than 50 km/h {SpeedFast:N0} / not {SpeedSlow:N0} / no OSM speed in town {SpeedNoneTown:N0}, outside {SpeedNoneRural:N0}, #711; {RightRejected:N0} rejected; beside a bike lane: kerbside (a) {KerbsideBike:N0}, between (b) by hash {BetweenBike:N0}, (b) forced by the plan {BetweenForced:N0}, #351), {LeftBikeLanes:N0} left-turn bike lanes ({BikeBoxes:N0} bike boxes, {AdvancedBikeLines:N0} advanced bike lines, #351), {Arrows:N0} arrows, {StopBars:N0} stop bars, {Stripes:N0} median stripes ({HatchesSkipped:N0} hatches left out: narrower than 1.5 m or shorter than 20 m, #406), {SignsMoved:N0} signs moved off the widening, {Corners:N0} corners rounded beside a widening ({CornersRejected:N0} failed, {CornersInTown:N0} left square beside a sidewalk or path, #406), {BesideBike:N0} approaches widened for a bike lane ({LeadIns:N0} with a lead-in, #120), {TightCorners:N0} junction corners left tight (no right turn rounds them, #700), {SplitLeadIns:N0} lead-ins split between both edges ({MirrorGuides:N0} edge guides moved out onto the other edge, #711), {ThroughGuides:N0} through-lane guides across the junction past an island (#700, #711), {PairGuidesNoLights:N0} lines between two same-turn lanes through a junction without lights (#711), {SolidCentres:N0} centre lines solid before a stop line (#711); " +
            $"rejected (approach or exit): too short {Short:N0}, building {Building:N0}, another line {OtherLine:N0}, ground off the road {Ground:N0}, tile seam {Seam:N0}, no segment {NoSegment:N0}, no main road out {NoExit:N0}, two lanes or more already and no lane data {MultiLane:N0}; OSM lane data (#700): {Wished:N0} approaches, {WishFolded:N0} wished lanes folded, {WishNoLeft:N0} with a road to the left and no left lane, {WishInPlace:N0} assigned in place, {WishArrows:N0} arrows over them\n") +
            string.Concat(LayoutExamples.Select(kv => $"      right pockets beside a bike lane, layout {kv.Key} at LV95 {string.Join("; ", kv.Value)}\n"));

        public void Reject(string why)
        {
            switch (why)
            {
                case "short": Short++; break;
                case "building": Building++; break;
                case "line": OtherLine++; break;
                case "ground": Ground++; break;
                default: Seam++; break;
            }
        }
    }

    private const double TurnLane = 3.0, TurnSolid = 10, TurnClear = 5;
    /// <summary>At the lights the centre line goes solid this far before the stop line on an approach without a left pocket (#711, the user's rule).</summary>
    private const double CentreSolidAtLights = 20;

    /// <summary>A right-turn pocket only where the approach is faster than this, km/h (#711, the user's decision).</summary>
    private const int RightPocketSpeed = 50;
    /// <summary>The angled closing line of a lead-in hatch runs this far along the road (#700).</summary>
    private const double LeadInClose = 10;
    /// <summary>The kerb leg of a tight corner (#700), as <see cref="JunctionOptions.TightKerb"/> leaves it.</summary>
    private static readonly double TightCornerLeg = new JunctionOptions().TightKerb;
    /// <summary>The other side's share of a split lead-in, tried in turn (#700): half, a quarter, none.</summary>
    private static readonly double[] VeerShares = [0.5, 0.25];
    /// <summary>The narrowest exit hatch that still holds a centre island (#682: 1.2 m island, 0.25 m clear each side).</summary>
    private const double IslandKeepHatch = 1.7;

    /// <summary>The arrow a lane with these moves shows: one of the five shapes, none for a lane that turns left and right or does all three.</summary>
    internal static PaintArrow? ArrowOf(SignalMoves m)
    {
        bool left = (m & SignalMoves.Left) != 0, through = (m & SignalMoves.Through) != 0, right = (m & SignalMoves.Right) != 0;
        return (left, through, right) switch
        {
            (true, false, false) => PaintArrow.Left,
            (false, true, false) => PaintArrow.Straight,
            (false, false, true) => PaintArrow.Right,
            (false, true, true) => PaintArrow.Straight | PaintArrow.Right,
            (true, true, false) => PaintArrow.Straight | PaintArrow.Left,
            _ => null,
        };
    }

    /// <summary>
    /// At traffic lights (#351) a left-turn bike lane runs between the left pocket and the through
    /// lane (1.50 m in town, SSV 6.09; #120's Radstreifen width), opening with the pocket. Where the
    /// street it turns into has no bike lane or path, a bike box (Aufstellbereich, SSV 6.26) of
    /// <see cref="BikeBoxDepth"/> lies in front of the pocket and the bike lane, behind a yellow
    /// line; where it has one, a yellow advanced stop line <see cref="AdvancedBikeLine"/> ahead of
    /// the cars' (ASTRA Handbuch Veloverkehr in Kreuzungen 2021: box 4.0 m, advanced line 3.0 m).
    /// </summary>
    private const double LeftBikeLane = 1.5, BikeBoxDepth = 4.0, AdvancedBikeLine = 3.0;

    /// <summary>A bike stop line, yellow (SSV Art. 75 al. 6-7; Kanton Bern: 0.30 m).</summary>
    private const float BikeStopLine = 0.3f;

    /// <summary>
    /// Pocket sizes tried longest first, (taper, storage) in metres: the first that fits is built.
    /// The last is an urban-sized one; 5 m stay clear of whatever is at the approach's other end.
    /// </summary>
    private static readonly (double Taper, double Storage)[] PocketSizes = [(30, 40), (25, 30), (20, 20)];

    /// <summary>The stop bar across the end of the left-turn lane.</summary>
    private const float StopBar = 0.4f;

    /// <summary>Past the junction the through lane comes back to its place over this length.</summary>
    private const double TurnExit = 30;

    /// <summary>
    /// Between an exit and the next approach on the same side (#325), the road at normal width for
    /// at least this long; else they merge, and the merged pocket opens out of the hatched median
    /// over <see cref="TurnEntry"/>, behind at least <see cref="TurnMinHatch"/> of it.
    /// </summary>
    private const double TurnRejoin = 30, TurnEntry = 15, TurnMinHatch = 5;

    private const double TurnStation = 2.5, TurnGroundStep = 1.2, TileSizeM = 1000;

    /// <summary>
    /// With a painted bike lane on the widened side (#120), every car lane beside it along the
    /// solid centre line is at least this wide: a car must pass a cyclist without crossing the
    /// solid line (Kanton Zürich Standards Veloverkehr: 3.00 m beside a Radstreifen).
    /// </summary>
    private const double CarLaneBesideBike = 3.0;

    /// <summary>The lead-in that widens a narrow approach for that runs at 1:6 (a town street), at least this long.</summary>
    private const double LeadInMin = 6, LeadInPerMetre = 6;
    private const double HatchStep = 2.5, HatchWidth = 0.3;

    /// <summary>
    /// A hatched median narrower than this at its widest, or shorter than <see cref="HatchMinLength"/>,
    /// is left out (#406, the user's rule): the widening keeps only its centre line.
    /// </summary>
    private const double HatchMinWidth = 1.5, HatchMinLength = 20;

    /// <summary>
    /// How far from an exit arm's mouth its hatched median starts (#406): at traffic lights the
    /// middle of the stop line of that arm's own left pocket (behind its bike box, if it has one),
    /// else of the stop line across its approach, so no hatch reaches further into the junction;
    /// without lights the mouth (no stop line across that arm). <paramref name="facing"/> is the
    /// left pocket on the exit arm, if any.
    /// </summary>
    private static double ExitStop(PocketPlan? exiting, PocketPlan? facing) =>
        exiting is not { ExitStopLine: true } p ? 0
        : SignalStopSetback + p.ExitSkew + SignalStopLine * 0.5
          + (facing is { Dropped: false, In.ApproachWay.HasLeftBikeLane: true } ? (facing.BikeBox ? BikeBoxDepth : AdvancedBikeLine) : 0);

    private static double Sq(double v) => v * v;

    /// <returns>
    /// The lanes of each widened arm, by (junction node, arm index): signal plans and poles (#348),
    /// bike crossings (#351) and lane-level traffic (#353) read them.
    /// </returns>
    private static Dictionary<(int Node, int Arm), ArmLanes> EmitTurnLanes(PriorityResult priority, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf, Dictionary<TileId, List<RoadSegment>> output,
        HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings,
        Dictionary<TileId, List<RoadPaint>> paint, Dictionary<TileId, List<RoadAreaProp>> areas,
        Dictionary<TileId, List<RoadPointProp>> signs, List<(RoadSegment Segment, bool Right, (double From, double To) Along)> bikeBetween,
        Dictionary<RoadAreaProp, RoadSegment> stripOwners, TurnLaneStats stats, Func<int, LinkEnd, bool, RoadSide> streetSide,
        List<PocketOpening>? openings = null, Dictionary<(int Node, int Arm), CornerArc>? arcs = null,
        OsmOverlayReader? overlay = null, Restrictions? restrictions = null, CrossingNodes? crossings = null,
        List<(TileId Home, RoadPaint Guide, Widening Exit, int Node, int ExitArm)>? pendingGuides = null)
    {
        var net = result.Network;
        var signalNodes = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Signal).Select(p => p.Junction.NodeId).ToHashSet();
        var indexes = new Dictionary<TileId, EmbankmentPlanner.LineIndex>();
        EmbankmentPlanner.LineIndex Lines(TileId t) =>
            indexes.TryGetValue(t, out var l) ? l : indexes[t] = new EmbankmentPlanner.LineIndex(output[t]);

        // plan every pocket first: an exit and the next junction's approach can share a side of a
        // segment (#325), so neither is laid out before both are known
        var pockets = new List<PocketPlan>();
        var rights = new List<RightPlan>();   // right-turn pockets at traffic lights (#348)
        var slots = new Dictionary<(RoadSegment, int), Slot>();
        var order = new List<Slot>();
        var turns = new Dictionary<(int Node, int Arm), SignalMoves>();   // what each approach can do (#386)
        var wishes = new Dictionary<(int Node, int Arm), (TileId Home, SignalMoves[] Wished, int Own, int Link, bool AtEnd)>();   // OSM's lanes per approach (#700)
        var scratchLanes = new LaneStats();
        var plansByNode = priority.Plans.ToDictionary(p => p.Junction.NodeId);
        Slot SlotOf((RoadSegment Segment, TileId Tile, RoadSegment Painted) s, int side)
        {
            if (!slots.TryGetValue((s.Segment, side), out var slot))
            {
                slots[(s.Segment, side)] = slot = new Slot(s.Segment, s.Tile, s.Painted);
                order.Add(slot);
            }
            return slot;
        }

        foreach (var (junction, plan) in priority.Plans)
        {
            // a main road with side roads (#123), or a signalised junction (#348); what differs between them, its rules (#711)
            if (plan.Kind is not (PriorityPlanner.Kind.Main or PriorityPlanner.Kind.Signal)) continue;
            var rules = JunctionRules.Of(plan.Kind);
            bool every = rules.Has(JunctionRule.PocketsOnEveryApproach);
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;
            for (int i = 0; i < junction.Arms.Count && i < plan.Arms.Count; i++)
            {
                var arm = plan.Arms[i];
                if ((every ? !arm.Approach || Internal(net.Links[arm.LinkId], junction.NodeId, signalNodes) : arm.Role != PriorityPlanner.Role.Main)
                    || !TurnLaneRoad(net.Links[arm.LinkId])) continue;
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
                // the turns the approach has, as the signal plans name them (#386)
                var armsHere = ArmsOf(junction, net);
                var can = SignalMoves.None;
                for (int k = 0; k < armsHere.Count; k++)
                    if (k != i && armsHere[k].Out) can |= SignalPlan.Turn(armsHere, i, k);
                turns[(junction.NodeId, i)] = can;
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
                if (right && ownLanes == 1)
                {
                    if (kmh > RightPocketSpeed) stats.SpeedFast++; else if (kmh > 0) stats.SpeedSlow++; else if (town) stats.SpeedNoneTown++; else stats.SpeedNoneRural++;
                }
                if (wished is not null) wishes[(junction.NodeId, i)] = (home, wished, ownLanes, arm.LinkId, arm.End == LinkEnd.End);
                // a multi-lane approach without OSM lane data (#700, the user's review of J7; #711: at every junction, shared out
                // by the lanes each exit takes away, InferredLanes), with their arrows as OSM's would get
                else if (ownLanes >= 2)
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
                    wishes[(junction.NodeId, i)] = (home, InferredLanes(can & ~banned, ownLanes, Known(outL), Known(outT), Known(outR)),
                        ownLanes, arm.LinkId, arm.End == LinkEnd.End);
                }
                if (wish is not null) { stats.Wished++; stats.WishFolded += wish.Folded; }
                else if (ownLanes >= 2) stats.MultiLane++;
                // a bike box where the street the left turn goes into has no bike lane or path (#351)
                bool box = false;
                if (rules.Has(JunctionRule.BikeBoxes) && leftArm >= 0 && segmentOf.TryGetValue(plan.Arms[leftArm].LinkId, out var into))
                {
                    var leaving = plan.Arms[leftArm].End == LinkEnd.Start ? into.Segment.Attributes.Right : into.Segment.Attributes.Left;
                    box = !leaving.HasLane && !leaving.HasTrack;
                }
                RightPlan? rightPlan = null;
                if (wantRight && segmentOf.TryGetValue(arm.LinkId, out var rightSeg))
                {
                    // a painted bike lane on that side runs kerbside (a) or between the pocket and the through lane (b): at the
                    // lights at random but the same on every run (#351), a bike signal guarding (a); without lights always (b),
                    // the right-turning cars never crossing it at the mouth (#711); a path keeps (a)
                    var kerb = arm.End == LinkEnd.End ? rightSeg.Painted.Attributes.Right : rightSeg.Painted.Attributes.Left;
                    bool between = kerb.HasLane && !kerb.HasTrack
                        && (!rules.Has(JunctionRule.KerbsideBikeLane) || BikePlanner.Fnv(ApproachKey(net.Links[arm.LinkId], arm.End, junction.Centre)) % 2 == 1);
                    rights.Add(rightPlan = new RightPlan(junction.NodeId, i, home, rightSeg, arm.End == LinkEnd.End, left)
                        { Skew = MouthSkew(junction, junction.Arms[i]), BikeBetween = between, At = junction.Centre, Through = (can & SignalMoves.Through) != 0, Rules = rules });
                }
                if (!left) continue;
                stats.Candidates++;
                if (!wantLeft) { if (wish is not null) stats.WishNoLeft++; continue; }
                if (exit < 0 || !TurnLaneRoad(net.Links[plan.Arms[exit].LinkId])) { stats.NoExit++; continue; }
                if (!segmentOf.TryGetValue(arm.LinkId, out var inSeg) || !segmentOf.TryGetValue(plan.Arms[exit].LinkId, out var outSeg))
                { stats.NoSegment++; continue; }

                // approach: traffic drives toward the junction and keeps right; exit: away from it, keeping right
                bool inAtEnd = arm.End == LinkEnd.End, outAtEnd = plan.Arms[exit].End == LinkEnd.End;
                var approach = SlotOf(inSeg, inAtEnd ? 1 : -1);
                var departure = SlotOf(outSeg, outAtEnd ? -1 : 1);
                // a slot holds one approach and one exit; a second would be another arm on the same segment
                if (approach.Approach is not null || departure.Exit is not null) { stats.NoSegment++; continue; }
                // the exit arm's own stop line (#406: the departing side's hatch stops in line with it)
                bool exitStop = rules.Has(JunctionRule.StopLine) && plan.Arms[exit].Approach && !Internal(net.Links[plan.Arms[exit].LinkId], junction.NodeId, signalNodes);
                var pocket = new PocketPlan(home, approach, inAtEnd, departure, outAtEnd, right)
                {
                    Node = junction.NodeId, Arm = i, ExitArm = exit, Rules = rules, Skew = rules.Has(JunctionRule.StopLine) ? MouthSkew(junction, junction.Arms[i]) : 0, BikeBox = box,
                    ExitStopLine = exitStop, ExitSkew = exitStop ? MouthSkew(junction, junction.Arms[exit]) : 0,
                    Lanes = wish?.Pocket.Length ?? 1, PocketMoves = wish?.Pocket, OwnMoves = wish?.Own,
                };
                if (rightPlan is not null) rightPlan.Left = pocket;
                approach.Approach = departure.Exit = pocket;
                pockets.Add(pocket);
            }
        }

        // lay the slots out until no pocket is dropped: dropping one only frees room for the rest
        for (bool dropped = true; dropped;)
        {
            dropped = false;
            foreach (var slot in order)
                if (slot.Layout(Lines(slot.Tile), output[slot.Tile], grids, buildings) is { } fail)
                {
                    fail.Pocket.Dropped = true;
                    stats.Reject(fail.Why);
                    dropped = true;
                }
        }


        // the veer share (#700, the user's rule): a left pocket's lead-in widens both edges, the approach's lanes and centre line
        // moving over by the other side's share, so the through lane veers in about as much as the oncoming traffic veers out.
        // Half each side where the other edge has room, else more on the approach side, down to all of it; none on a merged
        // strip (#325) or where the exit is one (it cannot shrink)
        var shares = new Dictionary<PocketPlan, double>();   // the other side's share of each split lead-in
        var mirrors = new Dictionary<PocketPlan, Widening>();
        foreach (var p in pockets)
        {
            if (p.Dropped || p.In.Merged || p.Out.Merged || p.In.ApproachWay is not { } way) continue;
            var (seg, tile, painted) = (p.In.Segment, p.In.Tile, p.In.Painted);
            // the other side of the segment: no strip coming the other way from the far end that the mirror would run into
            if (slots.GetValueOrDefault((seg, -way.Side)) is { } other
                && (other.Merged || other.ApproachWay is { } far && SegmentLength(seg) < way.Reach + far.Reach + TurnClear)) continue;
            int self = output[tile].IndexOf(seg);
            foreach (double otherShare in VeerShares)
            {
                double share = otherShare;
                var mirror = new Widening(seg, painted, tile, self, junctionAtEnd: p.InAtEnd, side: -way.Side, way.Length, way.Taper, leadIn: false);
                mirror.SetWidth(d => shares.GetValueOrDefault(p) * way.FullAt(d));
                shares[p] = share;
                if (mirror.Check(Lines(tile), grids, buildings) is not null) { shares.Remove(p); continue; }
                way.SetFrame(d => -shares.GetValueOrDefault(p) * way.FullAt(d));
                if (Environment.GetEnvironmentVariable("SPLITDBG") == "1") Console.WriteLine($"[split] at {plansByNode[p.Node].Junction.Centre.X:F0},{plansByNode[p.Node].Junction.Centre.Y:F0} arm {p.Arm} share {share} {p.Rules.Name}");
                mirrors[p] = mirror;
                break;
            }
        }
        // right-turn pockets (#348): outside the through lane, beside the left pocket's full width
        // (or on the right of an approach without one); never on a strip merged with an exit
        foreach (var r in rights)
        {
            var lp = r.Left is { Dropped: false } p ? p : null;
            if (lp is { In.Merged: true }) { stats.RightRejected++; continue; }
            var (seg, tile, painted) = r.Segment;
            int side = r.InAtEnd ? 1 : -1;
            bool exitHere = slots.TryGetValue((seg, side), out var slot) && slot.ExitWay is not null && slot.Exit is { Dropped: false };
            double baseOffset = lp?.In.ApproachWay!.FullWidth ?? 0;
            // a little earlier than the left pocket is full (#682): the two widenings run into each other, no straight edge between
            double reach = lp is null ? double.MaxValue : lp.In.Storage + RightEarlier;
            int self = output[tile].IndexOf(seg);
            // beside a left pocket the right one starts to widen where the left one has finished (#682): no stretch of straight edge between them
            var sizes = lp is not null && reach >= 25 ? [(Math.Min(15.0, Math.Round(reach * 0.35)), reach - Math.Min(15.0, Math.Round(reach * 0.35))), .. RightPocketSizes] : RightPocketSizes;
            foreach (var (taper, storage) in sizes)
            {
                if (taper + storage > reach) continue;
                var way = new Widening(seg, painted, tile, self, junctionAtEnd: r.InAtEnd, side, taper + storage, taper,
                    clear: exitHere ? TurnExit + TurnRejoin : TurnClear, leadIn: false, baseOffset: baseOffset);
                if (lp?.In.ApproachWay is { } leftWay) way.SetBase(d => leftWay.FullAt(d));   // on the left pocket's edge as it is there (#700)
                if (way.Check(Lines(tile), grids, buildings) is not null) continue;
                r.Way = way;
                break;
            }
            if (r.Way is null) { stats.RightRejected++; continue; }
            lp?.In.ApproachWay!.EdgeFrom(r.Way.Reach);
            if (lp?.In.ApproachWay?.FrameFunc is { } frame) r.Way.SetFrame(frame);   // beside a split lead-in, in its lane frame (#700)
        }
        var rightOf = rights.Where(r => r.Way is not null).ToDictionary(r => (r.Node, r.Arm));

        // an exit hatch that carries a centre island at the lights (#682) keeps its minimum width (the user's rule): where the two
        // facing split lead-ins would close it, both shares give way alike. The hatch at the mouth as the lights will make it:
        // as wide as the facing pocket's lanes and its bike lane (#682, equal lanes)
        foreach (var p in pockets)
        {
            if (!shares.ContainsKey(p) || !p.Rules.Has(JunctionRule.ExitIslands) || p.Out.ExitWay is not { } exitWay) continue;
            var q = pockets.FirstOrDefault(x => !x.Dropped && x.Node == p.Node && x.Arm == p.ExitArm);
            if (q is null) continue;
            var layout = LanesOf(p.In.ApproachWay!, rightOf.GetValueOrDefault((p.Node, p.Arm)), equal: true);
            // (beside an on-street bike lane the exit keeps the yield junction's own hatch, #682)
            double hatch = exitWay.BikeWidth <= 0 && layout.LeftPocketLane is { } own ? own.To + layout.LeftBike : exitWay.HatchAtMouth;
            double cp = shares[p] * p.In.ApproachWay!.FullAt(0), cq = shares.TryGetValue(q, out var sq) ? sq * q.In.ApproachWay!.FullAt(0) : 0;
            // where the island stands, behind the stop line (and an advanced bike line) and 2 m on, the hatch has closed that much
            double islandAt = SignalStopSetback + AdvancedBikeLine + 2, open = Math.Max(0.2, 1 - islandAt / exitWay.Length);
            double room = Math.Max(0, hatch - IslandKeepHatch / open);
            if (Environment.GetEnvironmentVariable("SPLITDBG") == "1") Console.WriteLine($"[split] node {p.Node} arm {p.Arm}->{p.ExitArm} hatch {hatch:F2} cp {cp:F2} cq {cq:F2} room {room:F2} exitMouth {exitWay.HatchAtMouth:F2}");
            if (cp + cq <= room) continue;
            double f = room / (cp + cq);
            shares[p] *= f;
            if (shares.ContainsKey(q)) shares[q] *= f;
        }
        // the exits shrink (#700): a split lead-in's through lane arrives less shifted, and where the exit's arm has its own split
        // lead-in its lanes have moved out already: the exit's lanes stand in that frame and its strip is only what is still
        // missing, drawn as part of that arm's mirror strip (one strip on that side)
        foreach (var p in pockets)
        {
            if (p.Dropped || p.Out.Merged || p.Out.ExitWay is not { } exitWay) continue;
            double less = shares.TryGetValue(p, out var sp) ? sp * p.In.ApproachWay!.FullAt(0) : 0;
            var q = pockets.FirstOrDefault(x => !x.Dropped && x.Node == p.Node && x.Arm == p.ExitArm);
            if (q is not null && mirrors.TryGetValue(q, out var qMirror))
            {
                var qWay = q.In.ApproachWay!;
                exitWay.SetFrame(d => shares.GetValueOrDefault(q) * qWay.FullAt(d));
                qMirror.SetWidth(d => shares.GetValueOrDefault(q) * qWay.FullAt(d) + exitWay.WidthAt(d));
            }
            if (less > 0 || exitWay.Framed) exitWay.SetShrink(less, 0);
        }

        // what each arm got, for the signal plans and poles (#348), bike crossings (#351) and lane records (#353)
        var placed = new Dictionary<(int Node, int Arm), ArmLanes>();
        var leftPocketAt = pockets.Where(p => !p.Dropped).ToDictionary(p => (p.Node, p.Arm));
        double StopBefore(PocketPlan? exiting) =>
            ExitStop(exiting, exiting is null ? null : leftPocketAt.GetValueOrDefault((exiting.Node, exiting.ExitArm)));
        ArmLanes Arm(int node, int arm, TileId home) =>
            placed.TryGetValue((node, arm), out var l) ? l : placed[(node, arm)] = new ArmLanes(home) { Turns = turns.GetValueOrDefault((node, arm)) };
        foreach (var pocket in pockets)
        {
            if (pocket.Dropped) continue;
            // the main road's centre line through the junction would start beside the hatch, at the mouth, and run past
            // its island (#700, the user's review of Sion): the left-turn guide takes its place
            if (priority.CentreAt.Remove(pocket.Node, out var centreLine)) Get(paint, centreLine.Tile).Remove(centreLine.Paint);
            var right = rightOf.GetValueOrDefault((pocket.Node, pocket.Arm));
            var (inSlot, outSlot) = (pocket.In, pocket.Out);
            // where the left-turn lane appears (#352): the end of the hatch, or of a merged strip's entry
            openings?.Add(new PocketOpening(inSlot.Segment, pocket.InAtEnd, inSlot.Merged ? inSlot.Storage + TurnEntry : inSlot.Storage));
            var approach = inSlot.ApproachWay!;
            var departure = outSlot.ExitWay!;
            var armLanes = Arm(pocket.Node, pocket.Arm, pocket.Home);
            // every car lane across the approach as wide as the others, and the exit's lane continuing the through lane, at every
            // junction (#711, the user's decision; #682 had it at the lights only)
            approach.Layout = armLanes.Approach = LanesOf(approach, right, equal: true);
            if (approach.Layout is { Equal: true, LeftPocketLane: { } facing })
            {
                double hatch = facing.To + approach.Layout.LeftBike, lane = approach.Layout.LaneWidth;
                if (pocket.Rules.Has(JunctionRule.ExitLaneContinuesThrough) && pocket.ExitArm >= 0)
                {
                    // without lights (#711) the exit lane continues the through lane; where an OSM crosswalk crosses the exit it
                    // narrows to a turn lane's width and the hatch takes the rest, room for a refuge (the user's rule)
                    if (lane > TurnLane && crossings?.OnArm(plansByNode[pocket.Node].Junction, pocket.ExitArm, net) is not null)
                        (hatch, lane) = (hatch + lane - TurnLane, TurnLane);
                    departure.SetExitLane(lane);
                }
                departure.SetExit(hatch, lane);
            }
            armLanes.AdvancedBikeLine = approach.HasLeftBikeLane && !pocket.BikeBox;
            armLanes.PlacedLeft(approach, inSlot.Storage, inSlot.Merged, pocket.RightTurn && right is null, pocket.Rules);
            (armLanes.PocketMoves, armLanes.OwnMoves) = (approach.PocketMoves, approach.OwnMoves) = (pocket.PocketMoves, pocket.OwnMoves);
            if (pocket.ExitArm >= 0)
            {
                var exitLanes = Arm(pocket.Node, pocket.ExitArm, pocket.Home);
                exitLanes.ExitWidening = Math.Max(exitLanes.ExitWidening, departure.MouthWidth(far: outSlot.Merged));   // a split lead-in's mirror there may reach further (#700)
                (exitLanes.ExitWay, exitLanes.ExitFar) = (departure, outSlot.Merged);
            }
            if (!approach.Emitted) approach.Emit(Get(paint, inSlot.Tile), Get(areas, inSlot.Tile), stripOwners);
            // a split lead-in's other side (#700): its strip moves that edge out, the arm's departing side starts that much further out
            if (mirrors.TryGetValue(pocket, out var mirror))
            {
                mirror.Emit(Get(paint, inSlot.Tile), Get(areas, inSlot.Tile), stripOwners);
                armLanes.Mirror = mirror;
                armLanes.ExitWidening = Math.Max(armLanes.ExitWidening, mirror.MouthWidth());
                stats.SignsMoved += mirror.PushOut(Get(signs, inSlot.Tile));
                stats.SplitLeadIns++;
                if (pocket.Rules.Has(JunctionRule.EdgeGuides) && GuideOut(mirror, Get(paint, pocket.Home), pocket.Home, priority)) stats.MirrorGuides++;
            }
            if (!approach.Painted)
            {
                // with a right-turn pocket beside it, the through lane goes straight only
                bool rightTurn = pocket.RightTurn && right is null;
                approach.StopShift = pocket.Skew;
                approach.BikeBox = pocket.BikeBox;
                // a merged strip's hatch runs to the junction before, up to its stop line there (#406)
                if (inSlot.Merged) approach.Through(Get(paint, inSlot.Tile), inSlot.Storage, rightTurn, stats, pocket.Rules, StopBefore(inSlot.Exit));
                else approach.Pocket(Get(paint, inSlot.Tile), rightTurn, stats, pocket.Rules);
            }
            // an exit beside its arm's split lead-in is part of that arm's mirror strip (#700)
            if (departure.Framed) departure.MarkEmitted();
            else if (!departure.Emitted) departure.Emit(Get(paint, outSlot.Tile), Get(areas, outSlot.Tile), stripOwners);
            // a merged strip is painted by the pocket at its other end, as its approach (#406: it was
            // painted here when this pocket came first, without the lights' stop line)
            if (!outSlot.Merged && !departure.Painted) departure.Median(Get(paint, outSlot.Tile), stats, StopBefore(pocket));
            Across(approach, departure, exitFar: outSlot.Merged, inSlot.Tile, Get(areas, inSlot.Tile), Get(paint, pocket.Home),
                pocket.Home, priority.Guides, joined: pocket.RightTurn, guide: pocket.Rules.Has(JunctionRule.EdgeGuides));
            // the through lane's guide past the pocket, at every junction, but only where the exit's hatch holds an island to
            // keep off (#711, the user's rule: a guide only for the main road's continuity, an island to avoid, or several
            // lanes turning alike): drawn once the islands are known (the lights' and the refuges, later)
            if (!outSlot.Merged && ThroughGuide(approach, departure, pocket.Home) is { } throughGuide)
                pendingGuides?.Add((pocket.Home, throughGuide, departure, pocket.Node, pocket.ExitArm));
            // a sign beside the old edge (#121's 3.03) would now stand on the widening
            stats.SignsMoved += approach.PushOut(Get(signs, inSlot.Tile)) + (departure.Framed ? 0 : departure.PushOut(Get(signs, outSlot.Tile)));
            stats.Storage[inSlot.Storage] = stats.Storage.GetValueOrDefault(inSlot.Storage) + 1;
            if (approach.BesideBike) { stats.BesideBike++; if (approach.HasLeadIn) stats.LeadIns++; }
            if (inSlot.Merged) stats.Merged++;
            if (pocket.Rules == JunctionRules.Lights) stats.AtSignals++;
            stats.Placed++;
        }
        foreach (var r in rightOf.Values)
        {
            var (seg, tile, _) = r.Segment;
            var lanes = Arm(r.Node, r.Arm, r.Home);
            // without a left pocket the layout is the right pocket's alone
            lanes.Approach ??= LanesOf(null, r, true);
            r.Way!.Emit(Get(paint, tile), Get(areas, tile), stripOwners);
            r.Way.StopShift = r.Skew;
            r.Way.Layout = lanes.Approach;
            lanes.RightWay = r;   // its lanes are painted once the signal plan has settled layout (a) or (b)
            lanes.Rules = r.Rules;   // (#711: right pockets without lights too)
            stats.SignsMoved += r.Way.PushOut(Get(signs, tile));
            stats.RightPockets++;
        }
        // an approach with OSM lane data and no pocket (none wished, or none that fitted): the wished lanes go onto the
        // carriageway's own lanes in place, with their arrows (#700); a right pocket that was built keeps the own lane's
        var solidDone = new HashSet<(int Node, int Arm)>();   // approaches whose lines went solid with their arrows
        foreach (var ((node, arm), w) in wishes)
        {
            var built = leftPocketAt.GetValueOrDefault((node, arm));
            if (built is not null) continue;
            var fit = Assign(w.Wished, w.Own, rightPocketPossible: false, leftPockets: false)!;
            var lanes = Arm(node, arm, w.Home);
            lanes.OwnMoves = fit.Own;
            stats.WishInPlace++;
            if (!segmentOf.TryGetValue(w.Link, out var so)) continue;
            var (centre, laneWidth, count) = OwnLanes(plansByNode[node].Junction, arm, net);
            var allowed = turns.GetValueOrDefault((node, arm));
            // (#711) without lights too, two lanes side by side making the same turn are led through the junction into the
            // exit's lanes (the lights draw theirs from the lane records)
            if (plansByNode[node].Plan.Kind != PriorityPlanner.Kind.Signal && count >= 2 && count == fit.Own.Length)
            {
                var j = plansByNode[node].Junction;
                var record = new RoadApproach
                {
                    LaneCentre = (float)centre,
                    Lanes = [.. fit.Own.Select((m, k) => new ApproachLane(k * laneWidth, 0f, 0f, m, ApproachLaneKind.Car))],
                };
                if (Anchors(j, net) is { Count: > 0 } anchors)
                    stats.PairGuidesNoLights += EmitPairGuides(paint, w.Home, j, net, [(arm, record)], anchors, JunctionRules.NoLights);
            }
            // arrows only where the data restricts a lane: a lane of every turn the approach has shows none
            if (fit.Own.All(m => m == allowed) || count != fit.Own.Length) continue;
            var ownRules = JunctionRules.Of(plansByNode[node].Plan.Kind);
            double stop = ownRules.StopAt(MouthSkew(plansByNode[node].Junction, plansByNode[node].Junction.Arms[arm]));
            stats.WishArrows += OwnArrows(so.Segment, so.Painted, so.Tile, w.AtEnd, Get(paint, so.Tile), centre, laneWidth, fit.Own, stop, ownRules);
            if (fit.Own.Length > 1) solidDone.Add((node, arm));
        }
        // (#711, the user's rule) at the lights the centre line goes solid before the stop line on every two-way approach without a
        // left pocket (a pocket's is solid along it; lanes in place had theirs above)
        foreach (var (junction, plan) in priority.Plans)
        {
            var rules = JunctionRules.Of(plan.Kind);
            if (!rules.Has(JunctionRule.SolidCentreBeforeStop)) continue;
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;
            for (int i = 0; i < junction.Arms.Count && i < plan.Arms.Count; i++)
            {
                var arm = plan.Arms[i];
                if (!arm.Approach || Internal(net.Links[arm.LinkId], junction.NodeId, signalNodes) || leftPocketAt.ContainsKey((junction.NodeId, i))
                    || solidDone.Contains((junction.NodeId, i)) || InfoOf(net.Links[arm.LinkId]) is not { Attributes.OneWay: 0 }
                    || !segmentOf.TryGetValue(arm.LinkId, out var so) || !block.Contains(so.Tile)) continue;
                bool atEnd = arm.End == LinkEnd.End;
                var way = new Widening(so.Segment, so.Painted, so.Tile, 0, atEnd, atEnd ? 1 : -1, 1, 0);
                double stopEdge = MouthSkew(junction, junction.Arms[i]) + SignalStopSetback;
                // a core lane between bike lanes (no centre line of its own) gets one there; a road too narrow for one keeps none
                var sides = so.Painted.Attributes;
                if (way.SolidToStop(Get(paint, so.Tile), stopEdge, CentreSolidAtLights, centreOnly: true) > 0) stats.SolidCentres++;
                else if (sides.Left.HasLane || sides.Right.HasLane)
                {
                    way.CentreLine(Get(paint, so.Tile), stopEdge, stopEdge + CentreSolidAtLights);
                    stats.SolidCentres++;
                }
            }
        }
        Corners(priority, net, placed, block, wanted, areas, stats, streetSide, arcs);
        return placed;
    }

    /// <summary>
    /// Kerb corners at widened arms (#406): the junction polygon rounds each corner between two
    /// arms' original edges (<see cref="JunctionBuilder"/>), so where a pocket's strip or an exit's
    /// widening stands out past an edge the corner was square (a right turn out of a right pocket
    /// could not be driven). Each corner beside a widening gets a flush pavement patch: from the
    /// junction's own corner out to the widened edges, rounded as the junction polygon rounds an
    /// unwidened one (a quadratic curve with the widened edges' meeting point as its control, each
    /// end a kerb allowance past it: <see cref="JunctionOptions.KerbFactor"/> of the arm's half width).
    /// </summary>
    private static void Corners(PriorityResult priority, RoadNetwork net, Dictionary<(int Node, int Arm), ArmLanes> placed,
        HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, List<RoadAreaProp>> areas, TurnLaneStats stats,
        Func<int, LinkEnd, bool, RoadSide> streetSide, Dictionary<(int Node, int Arm), CornerArc>? arcs)
    {
        var kerb = new JunctionOptions();
        double Kerb(double half) => kerb.Kerb(half);
        foreach (var (junction, plan) in priority.Plans)
        {
            int n = junction.Arms.Count;
            if (n < 3 || plan.Arms.Count != n || !Enumerable.Range(0, n).Any(i => placed.ContainsKey((junction.NodeId, i)))) continue;
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;
            var anchors = Anchors(junction, net);
            for (int i = 0; i < n; i++)
            {
                // arm i's approach side (its left looking out) faces arm j's departing side (its right)
                int j = (i + 1) % n;
                var ai = junction.Arms[i];
                var aj = junction.Arms[j];
                var inWay = placed.GetValueOrDefault((junction.NodeId, i)) is { } li ? li.RightWay?.Way ?? li.LeftWay : null;
                var outLanes = placed.GetValueOrDefault((junction.NodeId, j));
                var outWay = outLanes?.ExitWay;
                // (#711: a split lead-in's mirror strip widens arm j's departing side too)
                if (inWay is null && outWay is null && outLanes?.Mirror is null) continue;
                if (net.Links[ai.LinkId].Tag is not Source si || net.Links[aj.LinkId].Tag is not Source sj) continue;
                // in town a sidewalk or path runs round the corner (#119, #120): its corner follows the kerb arc
                // (#682 bands at the lights, #711 CornerPlanner elsewhere), clamped to the mouths where the sidewalk begins
                var ei = plan.Arms[i].End;
                var ej = plan.Arms[j].End;
                bool town = streetSide(ai.LinkId, ei, ei == LinkEnd.End).OuterDm > 0 || streetSide(aj.LinkId, ej, ej != LinkEnd.End).OuterDm > 0;
                if (town) stats.CornersInTown++;
                Vec2 ui = Vec2.FromHeading(ai.OutwardHeading), uj = Vec2.FromHeading(aj.OutwardHeading);
                (Vec2 P, float Y) EdgeI(double d) => inWay?.OuterEdge(d) ?? (ai.Left + ui * d, si.SampleHeight(ai.Left + ui * d));
                // (#700: beside a split lead-in with no exit there, its mirror strip is that edge)
                (Vec2 P, float Y) EdgeJ(double d) => outWay?.OuterEdge(d, outLanes!.ExitFar) ?? outLanes?.Mirror?.OuterEdge(d) ?? (aj.Right + uj * d, sj.SampleHeight(aj.Right + uj * d));
                const bool pastMouth = true;   // #711: no path bands round the arc, at the lights neither (the user's review)
                if (town && arcs is not null && ArcOf(junction, i, EdgeI, EdgeJ, Kerb(ai.HalfWidth), Kerb(aj.HalfWidth), true, pastMouth) is { } arc) arcs[(junction.NodeId, i)] = arc;
                if (CornerPatch(junction, i, EdgeI, EdgeJ, Kerb(ai.HalfWidth), Kerb(aj.HalfWidth), anchors, town, town && pastMouth) is not { } patches)
                {
                    stats.CornersRejected++; if (Environment.GetEnvironmentVariable("CORNERDBG") == "1") Console.WriteLine($"[corner] rejected (simple) at {junction.Centre.X:F0},{junction.Centre.Y:F0} corner {i}");
                    continue;
                }
                if (patches.Count == 0) continue;
                foreach (var patch in patches)
                {
                    var outline = patch.Select(p => p.P).ToList();
                    var tris = Junctions.EarClip.Triangulate(outline);
                    if (tris.Count != 3 * (outline.Count - 2)) { stats.CornersRejected++; if (Environment.GetEnvironmentVariable("CORNERDBG") == "1") Console.WriteLine($"[corner] rejected (tris) at {junction.Centre.X:F0},{junction.Centre.Y:F0} corner {i}"); continue; }
                    var v = new float[patch.Count * 3];
                    for (int k = 0; k < patch.Count; k++)
                        (v[k * 3], v[k * 3 + 1], v[k * 3 + 2]) = ((float)(patch[k].P.X - home.MinE), patch[k].Y, (float)(home.MaxN - patch[k].P.Y));
                    Get(areas, home).Add(new RoadAreaProp
                    {
                        Type = AreaPropType.Pavement, Flags = PropFlags.None, Height = 0f,
                        Vertices = v, Indices = tris.Select(t => (ushort)t).ToArray(),
                    });
                }
                stats.Corners++;
            }
        }
    }

    /// <summary>
    /// The pavement of one corner (#406), as LV95 outlines with heights, between arm i's approach
    /// edge and arm j's departing edge (<c>edge(d)</c>: the widened edge <c>d</c> metres out from
    /// the mouth). The widened edges meet at a corner point; the kerb rounds it as the junction
    /// polygon rounds its own corners (a quadratic curve with the corner point as its control,
    /// each end a kerb allowance past it), so a patch fills the corner point's side of that curve.
    /// A second patch fills what lies between the junction polygon's corner, the two mouths and
    /// the corner point (a widening that stops at its mouth short of the corner point leaves a
    /// notch); where both widened edges meet past both mouths the strips overlap there and only
    /// the part inside the mouths is needed. None when the edges do not meet near the junction
    /// (a straight road's two sides carried on); null when an outline is not simple.
    /// </summary>
    private static List<List<(Vec2 P, float Y)>>? CornerPatch(Junction junction, int i, Func<double, (Vec2 P, float Y)> edgeI,
        Func<double, (Vec2 P, float Y)> edgeJ, double kerbI, double kerbJ, List<(Vec2 At, float Height)> anchors, bool clamp, bool pastMouth)
    {
        int n = junction.Arms.Count, j = (i + 1) % n;
        Vec2 ai = junction.Arms[i].Left, aj = junction.Arms[j].Right;
        if (ArcOf(junction, i, edgeI, edgeJ, kerbI, kerbJ, clamp, pastMouth) is not { } arc) return [];
        var (c, fi, fyi, fj, fyj) = (arc.C, arc.Fi, arc.Yi, arc.Fj, arc.Yj);
        // the pavement the corner adds: from the junction out along each mouth and widened edge, round the kerb arc
        var head = new List<(Vec2 P, float Y)> { (ai, HeightAt(anchors, ai)), edgeI(0), (fi, fyi) };
        var arcPoints = arc.Arc(0, 0);
        for (int k = 0; k < arcPoints.Count; k++)
            head.Add((arcPoints[k], (float)(fyi + (fyj - fyi) * k / (arcPoints.Count - 1))));
        head.Add(edgeJ(0));
        // back along the junction polygon's own corner, from arm j's right end to arm i's left end (it
        // is paved inside it already). Its curve and the arc leave the arms together, so a point or two
        // at each end may have to go for the outline to stay simple; through the centre as a last resort
        var ring = junction.Boundary;
        int ri = NearestOnRing(ring, ai), rj = NearestOnRing(ring, aj);
        var back = new List<(Vec2 P, float Y)>();
        for (int k = rj; k != ri; k = (k - 1 + ring.Count) % ring.Count) back.Add((ring[k], HeightAt(anchors, ring[k])));
        back.Add((ai, HeightAt(anchors, ai)));
        List<(Vec2 P, float Y)>? clean = null;
        for (int skip = 0; skip <= 3 && clean is null; skip++)
        {
            if (back.Count - 2 * skip < 2) break;
            var outline = Clean([.. head, .. back.Skip(skip).Take(back.Count - 2 * skip)]);
            if (outline.Count >= 3 && Simple(outline)) clean = outline;
        }
        if (clean is null)
        {
            var viaCentre = Clean([.. head, (aj, HeightAt(anchors, aj)), (junction.Centre, HeightAt(anchors, junction.Centre))]);
            if (viaCentre.Count >= 3 && Simple(viaCentre)) clean = viaCentre;
        }
        if (clean is null) return null;
        if (Area(clean) < 0.05) return [];   // nothing there
        return [clean];

        static int NearestOnRing(List<Vec2> ring, Vec2 p)
        {
            int best = 0;
            for (int k = 1; k < ring.Count; k++) if (ring[k].DistanceSquaredTo(p) < ring[best].DistanceSquaredTo(p)) best = k;
            return best;
        }
    }

    /// <summary>
    /// The kerb arc of one corner (#682): a circle of radius <c>R</c> round <c>O</c>, tangent to
    /// both widened edges at <c>Fi</c> and <c>Fj</c>, which lie the same distance from their meeting
    /// point <c>C</c> (<c>R</c> = that tangent length x tan(half the angle between the edges)); the
    /// mouths <c>Mi</c>, <c>Mj</c>; each edge's direction out from its mouth (<c>Ei</c>, <c>Ej</c>) and
    /// heights. A sidewalk or path round the corner is a concentric arc, its radius less by its offset.
    /// </summary>
    private sealed record CornerArc(Vec2 C, Vec2 O, double R, Vec2 Fi, float Yi, Vec2 Fj, float Yj, Vec2 Mi, Vec2 Mj, Vec2 Ei, Vec2 Ej)
    {
        private const int Samples = 12;

        /// <summary>The arc <paramref name="di"/> / <paramref name="dj"/> outward from the kerb at its two ends (towards the circle's centre), radius by radius.</summary>
        public List<Vec2> Arc(double di, double dj)
        {
            double ai = Math.Atan2(Fi.Y - O.Y, Fi.X - O.X), aj = Math.Atan2(Fj.Y - O.Y, Fj.X - O.X);
            double delta = Math.IEEERemainder(aj - ai, 2 * Math.PI);
            double ri = Math.Max(R - di, 0.1), rj = Math.Max(R - dj, 0.1);
            var line = new List<Vec2>(Samples + 1);
            for (int k = 0; k <= Samples; k++)
            {
                double t = (double)k / Samples, a = ai + delta * t, r = ri + (rj - ri) * t;
                line.Add(new Vec2(O.X + Math.Cos(a) * r, O.Y + Math.Sin(a) * r));
            }
            return line;
        }

        /// <summary>The arc offset <paramref name="di"/> / <paramref name="dj"/>, led in and out along the edges from the mouths when it ends short of them.</summary>
        public List<Vec2> Offset(double di, double dj)
        {
            var line = Arc(di, dj);
            if ((Fi - Mi).Dot(Ei) < -0.05) line.Insert(0, Mi + Ei.Perp * di);   // (#711: a tangent point past the mouth needs none)
            if ((Fj - Mj).Dot(Ej) < -0.05) line.Add(Mj - Ej.Perp * dj);
            return line;
        }
    }

    /// <summary>
    /// The kerb arcs as the sidewalk corners take them (#711), tile-local in the junction's home tile (where its cap is):
    /// the kerb from arm i's mouth round to arm j's, its heights blended from one tangent point's to the other's.
    /// </summary>
    private static void KerbArcs(PriorityResult priority, Dictionary<(int Node, int Arm), CornerArc> arcs,
        Dictionary<TileId, List<CornerPlanner.KerbArc>> into)
    {
        foreach (var (junction, _) in priority.Plans)
        {
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            for (int i = 0; i < junction.Arms.Count; i++)
            {
                if (!arcs.TryGetValue((junction.NodeId, i), out var arc)) continue;
                var line = arc.Offset(0, 0);
                var local = new List<(Vec2 P, float Y)>(line.Count);
                for (int k = 0; k < line.Count; k++)
                    local.Add((new Vec2(line[k].X - home.MinE, line[k].Y - home.MaxN), arc.Yi + (arc.Yj - arc.Yi) * k / Math.Max(line.Count - 1, 1)));
                Get(into, home).Add(new CornerPlanner.KerbArc(local, arc.Ei, arc.Ej));
            }
        }
    }

    /// <summary>
    /// The kerb arc between arm i's widened approach edge and arm j's widened departing edge, null where
    /// they do not meet near the junction. Its tangent length is the shorter kerb allowance past the
    /// corner point; in town (<paramref name="clamp"/>) never past a mouth, where the arm's own sidewalk
    /// or path begins: no longer than the corner point's distance back to either mouth, and no
    /// shorter than 1 m (a widening that leaves no room: the corner stays square). With
    /// <paramref name="pastMouth"/> (#711, a junction whose sidewalk corners <see cref="CornerPlanner"/> lays)
    /// a corner point at or past one mouth takes a kerb allowance, at most its distance back to the other.
    /// </summary>
    private static CornerArc? ArcOf(Junction junction, int i, Func<double, (Vec2 P, float Y)> edgeI, Func<double, (Vec2 P, float Y)> edgeJ,
        double kerbI, double kerbJ, bool clamp, bool pastMouth = false)
    {
        int j = (i + 1) % junction.Arms.Count;
        var (mi, myi) = edgeI(0);
        var (mj, myj) = edgeJ(0);
        // the widened edges as lines near the mouths (a taper leans in a little)
        var ei = (edgeI(5).P - mi).Normalized();
        var ej = (edgeJ(5).P - mj).Normalized();
        double den = ei.Cross(ej);
        if (Math.Abs(den) < 0.2) return null;   // within ~12 degrees of parallel: the two sides of a road carried on
        double ci = (mj - mi).Cross(ej) / den, cj = (mj - mi).Cross(ei) / den;
        var c = mi + ei * ci;
        double reach = Math.Max(junction.Arms[i].Trim, junction.Arms[j].Trim) * 1.5 + 10;
        if (c.DistanceTo(junction.Centre) > reach || ci < -reach || cj < -reach) return null;
        // the legs run from the corner point back to the mouths where there is room (the trims decided how much), else a kerb allowance
        double leg = Math.Min(-ci, -cj);
        if (!clamp && leg < 1) leg = Math.Min(kerbI, kerbJ);
        // (#711) a widening reaching the other arm's mouth: the arc runs out past that mouth, along its kerb before its
        // sidewalk starts (the sidewalk corner keeps to the square corner where it starts sooner)
        else if (pastMouth && leg < 1) leg = Math.Min(Math.Max(-ci, -cj), Math.Min(kerbI, kerbJ));
        // a tight corner (#700: no right turn rounds it) keeps the junction's small kerb
        if (junction.TightCorners.Contains(i)) leg = Math.Min(Math.Max(leg, 1.0), TightCornerLeg);
        if (leg < 1) return null;
        // tangent points a leg past the corner point along each widened edge (before a mouth: on the edge's line)
        (Vec2 P, float Y) At(Func<double, (Vec2 P, float Y)> edge, Vec2 m, float my, Vec2 e, double d) =>
            d >= 0 ? edge(d) : (m + e * d, my);
        var (fi, fyi) = At(edgeI, mi, myi, ei, ci + leg);
        var (fj, fyj) = At(edgeJ, mj, myj, ej, cj + leg);
        // the angle between the legs (out from the corner point) and the circle that touches both
        double alpha = Math.Acos(Math.Clamp(ei.Dot(ej), -1, 1));
        if (alpha < 0.3 || alpha > Math.PI - 0.3) return null;
        double radius = leg * Math.Tan(alpha / 2);
        var centre = c + (ei + ej).Normalized() * (radius / Math.Sin(alpha / 2));
        return new CornerArc(c, centre, radius, fi, fyi, fj, fyj, mi, mj, ei, ej);
    }
    private static double Area(List<(Vec2 P, float Y)> poly)
    {
        double area = 0;
        for (int k = 0; k < poly.Count; k++) area += poly[k].P.Cross(poly[(k + 1) % poly.Count].P);
        return Math.Abs(area) * 0.5;
    }

    /// <summary>Consecutive points under 2 cm apart are one.</summary>
    private static List<(Vec2 P, float Y)> Clean(List<(Vec2 P, float Y)> poly)
    {
        var result = new List<(Vec2 P, float Y)>(poly.Count);
        foreach (var p in poly)
            if (result.Count == 0 || result[^1].P.DistanceTo(p.P) > 0.02) result.Add(p);
        while (result.Count > 2 && result[0].P.DistanceTo(result[^1].P) <= 0.02) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>A polygon with at least 3 distinct points and no two edges crossing.</summary>
    private static bool Simple(List<(Vec2 P, float Y)> poly)
    {
        var p = Clean(poly).Select(q => q.P).ToList();
        int n = p.Count;
        if (n < 3) return false;
        for (int a = 0; a < n; a++)
            for (int b = a + 2; b < n; b++)
            {
                if (a == 0 && b == n - 1) continue;
                if (SegmentsCross(p[a], p[(a + 1) % n], p[b], p[(b + 1) % n]) is { } hit) { if (Environment.GetEnvironmentVariable("CORNERDBG") == "1") Console.WriteLine($"[corner]   edges {a}-{a + 1} ({p[a].X:F1},{p[a].Y:F1})-({p[(a + 1) % n].X:F1},{p[(a + 1) % n].Y:F1}) and {b}-{b + 1} ({p[b].X:F1},{p[b].Y:F1})-({p[(b + 1) % n].X:F1},{p[(b + 1) % n].Y:F1}) cross at ({hit.X:F1},{hit.Y:F1})"); return false; }
            }
        return true;
    }

    /// <summary>Where segment a-b crosses segment c-d strictly inside both, or null.</summary>
    private static Vec2? SegmentsCross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        var r = b - a;
        var s = d - c;
        double den = r.Cross(s);
        if (Math.Abs(den) < 1e-12) return null;
        double t = (c - a).Cross(s) / den, u = (c - a).Cross(r) / den;
        return t > 1e-6 && t < 1 - 1e-6 && u > 1e-6 && u < 1 - 1e-6 ? a + r * t : null;
    }

    /// <summary>
    /// The right-turn pockets' lanes (#348), once the signal plans are built: they may have forced
    /// a painted bike lane between the pocket and the through lane (layout (b), #351).
    /// </summary>
    private static void EmitRightLanes(Dictionary<(int Node, int Arm), ArmLanes> lanes, Dictionary<TileId, List<RoadPaint>> paint,
        List<(RoadSegment Segment, bool Right, (double From, double To) Along)> bikeBetween, TurnLaneStats stats)
    {
        foreach (var arm in lanes.Values)
        {
            if (arm.RightWay is not { Way: { } way } r || arm.Approach is not { } layout) continue;
            var (seg, tile, _) = r.Segment;
            // without a left pocket the approach's own lane carries straight on and turns left, where
            // it can: the stem of a T has no straight on (#386)
            var inferred = (r.Through ? PaintArrow.Straight : 0) | (r.LeftTurn ? PaintArrow.Left : 0);
            var own = arm.OwnMoves is { Length: > 0 } given ? ArrowOf(given[0]) ?? PaintArrow.None : inferred;   // #700: what OSM gave the own lane
            way.RightLane(Get(paint, tile), stats, r.Left is { Dropped: false } || own == 0 ? (PaintArrow?)null : own, r.Rules);
            // layout (b): the pocket paints the bike lane along its reach, where it does not move out with the kerb
            if (layout.BikeBetween) bikeBetween.Add((seg, way.Side > 0, way.AlongRange()));
            if (layout.Bike <= 0) continue;
            if (layout.Forced) stats.BetweenForced++;
            else if (layout.BikeBetween) stats.BetweenBike++;
            else stats.KerbsideBike++;
            string key = layout.Forced ? "(b) forced" : layout.BikeBetween ? "(b)" : "(a)";
            if (!stats.LayoutExamples.TryGetValue(key, out var list)) stats.LayoutExamples[key] = list = new();
            if (list.Count < 5) list.Add(string.Create(CultureInfo.InvariantCulture, $"{r.At.X:F0},{r.At.Y:F0}"));
        }
    }

    /// <summary>A stable key for an approach (#351): its TLM line and the end at the junction, else the junction's place.</summary>
    private static string ApproachKey(RoadLink link, LinkEnd end, Vec2 centre)
    {
        char at = end == LinkEnd.End ? 'e' : 's';
        return link.Tag is Source { Key: { } k }
            ? string.Create(CultureInfo.InvariantCulture, $"{k.Uuid}:{k.Part}:{at}")
            : string.Create(CultureInfo.InvariantCulture, $"~{centre.X:F1},{centre.Y:F1}:{at}");
    }

    /// <summary>
    /// What a junction's arm got from its pockets: the approach's lanes (where each lies:
    /// <see cref="Approach"/>; how long each is: its widenings as built), and the widening of its
    /// departing side at the mouth. Signal plans and poles (#348), bike crossings (#351) and the
    /// lane records (#353, <c>TileRewriter.Lanes</c>) read it.
    /// </summary>
    private sealed class ArmLanes(TileId home)
    {
        /// <summary>The junction's home tile, where its lane records go.</summary>
        public readonly TileId Home = home;
        /// <summary>The approach's lanes where it got a pocket, else null.</summary>
        public ApproachLayout? Approach;
        /// <summary>The left pocket's approach widening (#123), null none; its storage, merged with the exit before (#325).</summary>
        public Widening? LeftWay;
        public double Storage;
        public bool Merged;
        /// <summary>Beside a left pocket, the through lane's arrow also turns right (no right pocket beside it).</summary>
        public bool ThroughRight;
        /// <summary>Its junction's rules (#711); at traffic lights its lane record is written with the junction's signal (#353).</summary>
        public JunctionRules Rules = JunctionRules.NoLights;
        /// <summary>The departing side's widening at the mouth (a left pocket's through lane carried on, #123), metres.</summary>
        public double ExitWidening;
        /// <summary>That widening (#406: the corner beside it), seen from its far end when it is a strip merged with the next pocket (#325).</summary>
        public Widening? ExitWay;
        public bool ExitFar;
        /// <summary>A yellow advanced bike stop line <see cref="AdvancedBikeLine"/> ahead of the cars' (#351).</summary>
        public bool AdvancedBikeLine;
        /// <summary>The right-turn pocket, its lanes painted after the signal plan (#351).</summary>
        public RightPlan? RightWay;
        /// <summary>The turns the approach can make (#386): a lane's arrows never show one it cannot.</summary>
        public SignalMoves Turns;
        /// <summary>
        /// What OSM's lane data gave the approach (#700), null where it gave none: the moves of the
        /// left pocket's lanes and of the carriageway's own lanes, each left to right.
        /// </summary>
        public SignalMoves[]? PocketMoves, OwnMoves;
        /// <summary>A split lead-in's strip on the departing side (#700), null none.</summary>
        public Widening? Mirror;
        public bool Left => Approach is { LeftPocket: > 0 };
        public bool Right => Approach is { Right: true };

        public void PlacedLeft(Widening way, double storage, bool merged, bool throughRight, JunctionRules rules)
        {
            LeftWay = way; Storage = storage; Merged = merged; ThroughRight = throughRight; Rules = rules;
        }
    }

    /// <summary>
    /// The lanes across an approach with pockets (#348, #351): the one place their lateral positions
    /// are worked out, as offsets from the carriageway's centre line toward the approaching driver's
    /// right, metres. <c>open</c> is how far the right pocket has opened (1 along its storage and at
    /// the stop line, 0 before its taper). From the centre: the left pocket, the left-turn bike lane
    /// (#351), the through lane; then, with a right pocket, layout (a) the pocket and the painted
    /// bike lane kerbside of it, or layout (b) the bike lane between the through lane and the pocket
    /// (Velostreifen zwischen den Fahrstreifen). Without a left pocket the approach's own lane is
    /// the through lane. Paint, stop lines, poles, bike crossings and the lane records traffic
    /// drives (#353, <c>TileRewriter.Lanes</c>) read them.
    /// </summary>
    /// <param name="Half">The carriageway's half width (a painted bike lane included).</param>
    /// <param name="Bike">The painted bike lane on the approach side, 0 none (#120).</param>
    /// <param name="LeftPocket">The left pocket's width, 0 none; it runs from the centre line.</param>
    /// <param name="LeftBike">The left-turn bike lane beside it, 0 none (#351).</param>
    /// <param name="LeftFull">What the left pocket's widening adds at full width.</param>
    /// <param name="RightExtra">The right pocket's extra that makes the through lane beside a bike lane a full lane (#120), at full width.</param>
    public sealed record ApproachLayout(double Half, double Bike, double LeftPocket, double LeftBike, double LeftFull,
        bool Right, double RightExtra)
    {
        /// <summary>Layout (b): the painted bike lane between the through lane and the right pocket.</summary>
        public bool BikeBetween { get; set; }
        /// <summary>Layout (b) forced: the signal plan has no phase with the bike lane green and the right arrow red (#351).</summary>
        public bool Forced { get; set; }

        public readonly record struct Lane(double From, double To)
        {
            public double Mid => (From + To) * 0.5;
        }

        /// <summary>
        /// At traffic lights (#682) every car lane across the approach is the same width: what the approach's widened
        /// edge leaves once the bike lanes are out, over its car lanes (the left pocket's, the through lane, the right
        /// pocket's). Else the left pocket is the carriageway lane it was cut out of, the through lane what is left of
        /// it and the right pocket a <see cref="TurnLane"/>.
        /// </summary>
        public bool Equal { get; set; }

        /// <summary>Lanes in the left pocket (#700: a double left is two), each beside the next; the through lane follows them.</summary>
        public int LeftLanes { get; set; } = 1;

        /// <summary>
        /// A split lead-in (#700): how far the approach's lane frame (its centre line, the lanes, all offsets of this layout) stands
        /// toward the other side at the stop line. Readers outside the widening subtract it to get offsets from the carriageway's axis.
        /// </summary>
        public double Shift { get; set; }

        /// <summary>The approach's widened edge beyond the carriageway's own, at full width: its share of the widening (#700).</summary>
        public double EdgeOut => Edge() - Half - Shift;

        private int CarLanes => (LeftPocket > 0 ? LeftLanes : 0) + 1 + (Right ? 1 : 0);

        /// <summary>The width of a car lane when <see cref="Equal"/>.</summary>
        public double LaneWidth => (Half + LeftFull + (Right ? TurnLane + RightExtra : 0) - LeftBike - Bike) / CarLanes;

        /// <summary>Where the left pocket's lane ends (its outer edge from the centre line).</summary>
        private double PocketTo => LeftPocket <= 0 ? 0 : Equal ? LaneWidth * LeftLanes : LeftPocket + (LeftLanes - 1) * TurnLane;

        /// <summary>Where the car lanes end on the right: the through lane's outer edge.</summary>
        private double CarEdge(double open)
        {
            double old = Half - Bike + LeftFull + RightExtra * open;
            if (!Equal) return old;
            // the lane widens to its width as the right pocket opens
            double open0 = Half - Bike + LeftFull, full = PocketTo + (LeftPocket > 0 ? LeftBike : 0) + LaneWidth;
            return Right ? open0 + (full - open0) * open : full;
        }

        private double RightWidth => Equal ? LaneWidth : TurnLane;

        public Lane? LeftPocketLane => LeftPocket > 0 ? new Lane(0, PocketTo) : null;
        /// <summary>Lane <paramref name="k"/> (from the centre line) of the left pocket (#700).</summary>
        public Lane LeftLane(int k) => new(PocketTo * k / LeftLanes, PocketTo * (k + 1) / LeftLanes);
        public Lane? LeftBikeLane => LeftBike > 0 ? new Lane(PocketTo, PocketTo + LeftBike) : null;
        public Lane Through(double open = 1) => new(PocketTo + LeftBike, CarEdge(open));
        public Lane? RightPocket(double open = 1) => !Right ? null
            : BikeBetween ? new Lane(CarEdge(open) + Bike, CarEdge(open) + Bike + RightWidth * open)
            : new Lane(CarEdge(open), CarEdge(open) + RightWidth * open);
        /// <summary>The painted bike lane carried on through the junction (kerbside, or between in layout (b)).</summary>
        public Lane? BikeLane(double open = 1) => Bike <= 0 ? null
            : Right && !BikeBetween ? new Lane(CarEdge(open) + RightWidth * open, CarEdge(open) + RightWidth * open + Bike)
            : new Lane(CarEdge(open), CarEdge(open) + Bike);
        /// <summary>The carriageway's edge on the approach side, widening included.</summary>
        public double Edge(double open = 1) => Half + LeftFull + (Right ? (TurnLane + RightExtra) * open : 0);
        /// <summary>A painted bike lane kerbside of a right pocket: right-turning cars cross it, so it gets a bike signal (#351).</summary>
        public bool KerbsideBike => Right && Bike > 0 && !BikeBetween;
    }

    /// <summary>The lanes of an approach from its left pocket's widening and its right pocket, either may be missing.</summary>
    private static ApproachLayout LanesOf(Widening? left, RightPlan? right, bool equal = false)
    {
        var way = left ?? right!.Way!;
        // a right pocket beside a left one does not see the bike lane (it lies outside it): the left does
        double bike = left?.BikeWidth ?? right!.Way!.BikeWidth;
        var dbg = new ApproachLayout(way.Half, bike, left?.PocketWidth ?? 0, left?.LeftBikeWidth ?? 0, left?.FullWidth ?? 0,
            right is not null, right?.Way!.Extra ?? 0) { BikeBetween = right is { BikeBetween: true } && bike > 0, Equal = equal, LeftLanes = left?.LaneCount ?? 1, Shift = -(left?.Frame0 ?? 0) };
        if (Environment.GetEnvironmentVariable("LAYDBG") == "1") Console.WriteLine($"[layout] half {dbg.Half:F2} bike {dbg.Bike:F2} pocket {dbg.LeftPocket:F2} leftBike {dbg.LeftBike:F2} full {dbg.LeftFull:F2} right {dbg.Right} extra {dbg.RightExtra:F2} through {dbg.Through().From:F2}-{dbg.Through().To:F2} edge {dbg.Edge():F2}");
        return dbg;
    }

    /// <summary>A right-turn pocket being planned at traffic lights (#348): the approach's segment, and the left pocket beside it, if any.</summary>
    private sealed class RightPlan(int node, int arm, TileId home, (RoadSegment Segment, TileId Tile, RoadSegment Painted) segment, bool inAtEnd, bool leftTurn)
    {
        public readonly int Node = node, Arm = arm;
        public readonly TileId Home = home;
        public readonly (RoadSegment Segment, TileId Tile, RoadSegment Painted) Segment = segment;
        public readonly bool InAtEnd = inAtEnd, LeftTurn = leftTurn;
        public PocketPlan? Left;
        public Widening? Way;
        public double Skew { get; init; }
        /// <summary>Layout (b) where a painted bike lane runs on that side (#351): it lies between the through lane and the pocket.</summary>
        public bool BikeBetween { get; init; }
        /// <summary>The junction's centre, LV95.</summary>
        public Vec2 At { get; init; }
        /// <summary>The approach has a straight-on exit (the stem of a T has none, #386).</summary>
        public bool Through { get; init; }
        /// <summary>Its junction's rules (#711): at traffic lights (#348), without them since #711 (on a road faster than <see cref="RightPocketSpeed"/>).</summary>
        public JunctionRules Rules { get; init; } = JunctionRules.NoLights;
    }

    /// <summary>Right-turn pockets, (taper, storage) in metres, longest first: shorter than a left pocket, beside its full width.</summary>
    private const double RightEarlier = 8;
    private static readonly (double Taper, double Storage)[] RightPocketSizes = [(15, 30), (10, 25), (10, 15)];

    /// <summary>
    /// Where a placed left-turn pocket's lane appears (#352): its approach segment, whether the
    /// junction is at the segment's end, and the distance from the mouth. The approach's right side
    /// (right of the drawing when the junction is at the end) is the one cyclists cross from.
    /// </summary>
    public sealed record PocketOpening(RoadSegment Segment, bool AtEnd, double FromMouth);

    /// <summary>A left-turn pocket being planned: the slot its approach runs along, and its exit's.</summary>
    private sealed record PocketPlan(TileId Home, Slot In, bool InAtEnd, Slot Out, bool OutAtEnd, bool RightTurn)
    {
        public bool Dropped { get; set; }
        /// <summary>The junction node and the approach's arm index there.</summary>
        public int Node { get; init; }
        public int Arm { get; init; }
        /// <summary>The arm the through lane leaves by.</summary>
        public int ExitArm { get; init; } = -1;
        /// <summary>Its junction's rules (#711): at traffic lights (#348) the stop line runs across both lanes.</summary>
        public JunctionRules Rules { get; init; } = JunctionRules.NoLights;
        /// <summary>How much further back the stop line stands for a skewed mouth (#348).</summary>
        public double Skew { get; init; }
        /// <summary>A bike box in front of the pocket and its bike lane, else an advanced bike stop line (#351).</summary>
        public bool BikeBox { get; init; }
        /// <summary>The exit arm has a stop line across its own approach (traffic lights), and its mouth's skew (#406).</summary>
        public bool ExitStopLine { get; init; }
        public double ExitSkew { get; init; }
        /// <summary>Lanes the pocket adds (#700: a double left is two), and the moves OSM gave its lanes and the carriageway's own, left to right (null: inferred).</summary>
        public int Lanes { get; init; } = 1;
        public SignalMoves[]? PocketMoves { get; init; }
        public SignalMoves[]? OwnMoves { get; init; }
    }

    /// <summary>
    /// One side of a segment (#325): the pocket whose approach runs along it toward the junction
    /// at one end, and the pocket whose exit runs along it away from the junction at the other.
    /// Where both are there and the segment cannot hold the exit, a stretch back at normal width
    /// and the approach, they merge: the through lane stays out the whole way (2+1), the exit's
    /// hatched median stays a lane wide and opens into the next pocket.
    /// </summary>
    private sealed class Slot(RoadSegment segment, TileId tile, RoadSegment painted)
    {
        public readonly RoadSegment Segment = segment;
        /// <summary>The segment its lines were laid on: an Urban-flagged copy in a built-up stretch (#119).</summary>
        public readonly RoadSegment Painted = painted;
        public readonly TileId Tile = tile;
        public PocketPlan? Approach, Exit;
        public Widening? ApproachWay, ExitWay;
        public bool Merged;
        public double Storage;

        /// <summary>Lays the slot out for its pockets still planned; null when it fits, else the pocket to drop and why.</summary>
        public (PocketPlan Pocket, string Why)? Layout(EmbankmentPlanner.LineIndex lines, List<RoadSegment> tileSegments,
            Dictionary<TileId, ChunkGrid>? grids, Footprints buildings)
        {
            var a = Approach is { Dropped: false } ? Approach : null;
            var e = Exit is { Dropped: false } ? Exit : null;
            ApproachWay = ExitWay = null;
            Merged = false;
            int self = tileSegments.IndexOf(Segment);
            Widening Way(PocketPlan p, bool exit, double length, double taper, double clear = TurnClear, double[]? stations = null,
                bool lead = true, double bikeLeft = 0) =>
                new(Segment, Painted, Tile, self, junctionAtEnd: exit ? p.OutAtEnd : p.InAtEnd,
                    side: (exit ? p.OutAtEnd : p.InAtEnd) == exit ? -1 : 1, length, taper, clear, stations, exit, lead, bikeLeft: bikeLeft, lanes: p.Lanes);

            if (e is not null)
            {
                // a lead-out (#120: a bike lane beside the through lane) where the segment has room, else none
                string? exitWhy = null;
                foreach (bool lead in (ReadOnlySpan<bool>)[true, false])
                {
                    ExitWay = Way(e, exit: true, TurnExit, TurnExit, lead: lead);
                    exitWhy = ExitWay.Check(lines, grids, buildings);
                    if (exitWhy is null) break;
                }
                if (exitWhy is not null) { ExitWay = null; return (e, exitWhy); }
            }
            if (a is null) return null;

            // as long a pocket as fits; beside an exit it leaves room for that and a stretch back at
            // normal width, else the two merge (first, where the segment is too short for both apart)
            string? why = null;
            double total = SegmentLength(Segment);
            // a pocket of several lanes moves the through lane over by each of them: its taper is as many times as long, the same slope (#700)
            int lanes = Math.Max(1, a.Lanes);
            bool close = e is not null && total < TurnExit + TurnRejoin + PocketSizes[0].Taper * lanes + PocketSizes[0].Storage + TurnClear;
            bool[] modes = e is null ? [false] : close ? [true, false] : [false, true];
            // a pocket apart tries a lead-in first (#120: a bike lane beside it), then none
            foreach (bool merge in modes)
                foreach (bool lead in merge ? [false] : (ReadOnlySpan<bool>)[true, false])
                    foreach (var (taper, storage) in PocketSizes)
                    {
                        Widening way;
                        if (merge)
                        {
                            if (storage + TurnEntry + TurnMinHatch > total) { why = "short"; continue; }
                            way = Way(a, exit: false, total, 0, clear: 0, stations: [storage, storage + TurnEntry], lead: false);
                        }
                        else way = Way(a, exit: false, taper * lanes + storage, taper * lanes, clear: e is null ? TurnClear : TurnExit + TurnRejoin, lead: lead,
                            bikeLeft: a.Rules.Has(JunctionRule.LeftTurnBikeLane) ? LeftBikeLane : 0);
                        why = way.Check(lines, grids, buildings);
                        if (why is not null) { if (merge) break; continue; }   // a merged strip is the same for every storage
                        ApproachWay = way;
                        Storage = storage;
                        if (merge) { Merged = true; ExitWay = way; }
                        return null;
                    }
            return (a, why!);
        }
    }

    private static double SegmentLength(RoadSegment seg)
    {
        double total = 0;
        var p = seg.Points;
        for (int k = 1; k < seg.PointCount; k++) total += Math.Sqrt(Sq(p[k * 3] - p[k * 3 - 3]) + Sq(p[k * 3 + 2] - p[k * 3 - 1]));
        return total;
    }

    /// <summary>
    /// The through lane across the junction: the junction polygon only covers the original road,
    /// so the lane's outer part, from the approach strip's mouth to the exit strip's, is paved as
    /// one more strip (in the approach's tile). The junction's guide line on that side (#121) runs
    /// along the old edge, now inside the lane: it moves out onto the strip's edge, dashed where a
    /// road joins on that side, solid where none does (and only if there was one).
    /// </summary>
    private static void Across(Widening approach, Widening exit, bool exitFar, TileId tile, List<RoadAreaProp> areas,
        List<RoadPaint> homePaint, TileId home, HashSet<RoadPaint> guides, bool joined, bool guide = true)
    {
        var (ai, ao) = approach.Mouth(tile);
        var (ei, eo) = exit.Mouth(tile, far: exitFar);
        areas.Add(new RoadAreaProp
        {
            Type = AreaPropType.Pavement, Flags = PropFlags.None, Height = 0f,
            Vertices = [.. ai, .. ao, .. eo, .. ei], Indices = [0, 1, 2, 0, 2, 3],
        });

        if (!guide) return;   // traffic lights (#348): no guide line through the junction
        var (corner, aEdge) = approach.Mouth(home, PaintEmitter.EdgeLineInset);
        var (_, eEdge) = exit.Mouth(home, PaintEmitter.EdgeLineInset, exitFar);
        static double Gap(float[] v, int i, float[] p) => Math.Sqrt(Sq(v[i] - p[0]) + Sq(v[i + 2] - p[2]));
        var old = homePaint.FirstOrDefault(q => guides.Contains(q)
            && (Gap(q.Vertices, 0, corner) < 1.5 || Gap(q.Vertices, q.Vertices.Length - 3, corner) < 1.5));
        if (old is not null) homePaint.Remove(old);
        if (old is null && !joined) return;
        homePaint.Add(new RoadPaint
        {
            Shape = PaintShape.Polyline, Type = joined ? PaintType.WhiteDashed : PaintType.WhiteSolid,
            Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
            Dash = joined ? GuideDash : 0, Gap = joined ? GuideDash : 0,
            Vertices = [.. aEdge, .. eEdge],
        });
    }


    /// <summary>
    /// The junction's guide line (#121) beside a split lead-in's mirror strip (#711, the user's rule: the edge line follows
    /// the widening): its end at the old edge moves out onto the strip's edge, edge-line inset, the move fading to nothing
    /// at its other end, so the line keeps its curve and still meets the other arm's edge. False where no guide ends there.
    /// </summary>
    private static bool GuideOut(Widening mirror, List<RoadPaint> homePaint, TileId home, PriorityResult priority)
    {
        var (corner, edge) = mirror.Mouth(home, PaintEmitter.EdgeLineInset);
        static double Gap(float[] v, int i, float[] p) => Math.Sqrt(Sq(v[i] - p[0]) + Sq(v[i + 2] - p[2]));
        var old = homePaint.FirstOrDefault(q => priority.Guides.Contains(q)
            && (Gap(q.Vertices, 0, corner) < 1.5 || Gap(q.Vertices, q.Vertices.Length - 3, corner) < 1.5));
        if (old is null) return false;
        var v = (float[])old.Vertices.Clone();
        int n = v.Length / 3, end = Gap(v, 0, corner) <= Gap(v, v.Length - 3, corner) ? 0 : n - 1;
        float dx = edge[0] - v[end * 3], dz = edge[2] - v[end * 3 + 2];
        if (Math.Sqrt(dx * dx + dz * dz) < 0.05) return false;
        // the share of the move each vertex takes: 1 at the moved end, 0 at the other, by length along the line
        var along = new double[n];
        for (int i = 1; i < n; i++)
        {
            int a = end == 0 ? i - 1 : n - i, b = end == 0 ? i : n - 1 - i;
            along[i] = along[i - 1] + Math.Sqrt(Sq(v[a * 3] - v[b * 3]) + Sq(v[a * 3 + 2] - v[b * 3 + 2]));
        }
        double total = Math.Max(along[^1], 1e-6);
        for (int i = 0; i < n; i++)
        {
            int k = end == 0 ? i : n - 1 - i;
            float w = (float)(1 - along[i] / total);
            v[k * 3] += dx * w;
            v[k * 3 + 2] += dz * w;
        }
        var moved = new RoadPaint
        {
            Shape = old.Shape, Type = old.Type, Variant = old.Variant, Rgba = old.Rgba,
            Width = old.Width, Dash = old.Dash, Gap = old.Gap, Vertices = v,
        };
        homePaint[homePaint.IndexOf(old)] = moved;
        priority.Guides.Remove(old);
        priority.Guides.Add(moved);
        foreach (var key in priority.GuideAt.Where(kv => ReferenceEquals(kv.Value.Paint, old)).Select(kv => kv.Key).ToList())
            priority.GuideAt[key] = (home, moved);
        return true;
    }

    /// <summary>
    /// The through lane's guide on its left across the junction (#700, the user's rule): a dashed line (0.15 m, 1 m / 1 m, as
    /// #682's left-turn guide) from the line between the left pocket and the through lane at the approach's mouth to the left
    /// edge of the exit's lane at the far mouth, beside its hatch, so the through traffic keeps off the pocket and the
    /// island. Tangent to both ways; null where the exit has no hatch to keep off.
    /// </summary>
    /// <summary>The through lane jogs at least this far sideways across the junction before it gets a guide (#711), metres.</summary>
    private const double ThroughGuideJog = 0.3;

    private static RoadPaint? ThroughGuide(Widening approach, Widening exit, TileId home)
    {
        if (approach.Layout is not { } layout || exit.HatchAt(0) < 0.5) return null;
        double inner = layout.Through().From, outer = exit.HatchAt(0);
        float[] a0 = approach.At(home, 0, inner), a1 = approach.At(home, 1, inner);
        float[] e0 = exit.At(home, 0, outer), e1 = exit.At(home, 1, outer);
        Vec2 P(float[] p) => new(p[0], p[2]);
        Vec2 start = P(a0), end = P(e0), din = (P(a0) - P(a1)).Normalized(), dout = (P(e1) - P(e0)).Normalized();
        // (#711, the user's rule: a guide only where there is an island to avoid) none where the lane runs on straight into the
        // exit lane beside the hatch: the straight line from the approach's edge passes the exit's within ThroughGuideJog
        if (Math.Abs((end - start).Cross(din)) < ThroughGuideJog) return null;
        var line = new List<float>();
        double den = din.Cross(dout);
        Vec2 control = Math.Abs(den) < 0.05 ? (start + end) * 0.5 : start + din * ((end - start).Cross(dout) / den);
        // a control behind either end (the ways diverge) would loop: straight across instead
        if ((control - start).Dot(din) < 0 || (end - control).Dot(dout) < 0) control = (start + end) * 0.5;
        for (int k = 0; k <= 16; k++)
        {
            double t = k / 16.0, mt = 1 - t;
            var p = start * (mt * mt) + control * (2 * mt * t) + end * (t * t);
            line.AddRange([(float)p.X, (float)(a0[1] + (e0[1] - a0[1]) * t), (float)p.Y]);
        }
        return new RoadPaint
        {
            Shape = PaintShape.Polyline, Type = PaintType.WhiteDashed, Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
            Dash = 1f, Gap = 1f, Vertices = line.ToArray(),
        };
    }

    /// <summary>The car lanes a two-way segment holds in the direction toward a junction at its end (<paramref name="atEnd"/>) or its start (#700).</summary>
    private static int CarLanesIn(RoadSegment seg, bool atEnd) =>
        Math.Max(1, (int)(atEnd ? seg.Attributes.LanesForward : seg.Attributes.LanesBackward));
    /// <summary>A main-road arm a pocket can be built on: two-way, paved, at grade, Major or Road.</summary>
    private static bool TurnLaneRoad(RoadLink link) =>
        link.Tag is Source { Segment: var s } source
        && s.Class is RoadClass.Major or RoadClass.Road && s.Surface == RoadSurface.Paved
        && (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel | RoadFlags.Divided)) == 0
        && CrossSectionPlanner.Attributes(source.Line).OneWay == 0;

    /// <summary>
    /// One lane's widening along the junction end of a segment, on one side: full lane width from
    /// the junction mouth out to <c>length - taper</c>, then tapering to nothing at
    /// <c>length</c> (a <c>taper</c> of 0: full width all along, #325). Distances (<c>dist</c>) run
    /// from the mouth outward; <c>side</c> is +1 right of the drawing direction, -1 left of it.
    /// </summary>
    private sealed class Widening
    {
        private readonly RoadSegment _seg, _painted;
        private readonly TileId _tile;
        private readonly int _self, _side;
        private readonly bool _atEnd;
        private readonly double[] _along;
        private readonly double _half, _length, _taper, _total, _clear;
        private readonly List<double> _dists = new();
        private readonly bool _exit;
        /// <summary>
        /// The widened side's bike lane (#120, 0 none), the car width between the centre and it,
        /// the extra widening that makes that a full lane (<see cref="CarLaneBesideBike"/>), the
        /// lead-in that brings it on before the taper (0: with the taper itself), and the pocket's
        /// width (the approach lane once widened; the hatch's width at an exit's mouth).
        /// </summary>
        private readonly double _bike, _car, _extra, _lead, _pocket;

        /// <summary>
        /// How far outside the carriageway's edge the strip starts: 0, or the left pocket's
        /// through lane for a right-turn pocket beside it (#348).
        /// </summary>
        private readonly double _base;

        /// <summary>
        /// Where the strip starts at a distance from the mouth (#700): a right pocket beside a left one stands on the left
        /// one's edge as it is there, still widening over its taper and lead-in, not on its full width (a gap, else).
        /// </summary>
        private Func<double, double>? _baseAt;
        private double BaseAt(double dist) => _baseAt?.Invoke(Math.Max(0, dist)) ?? _base;
        public void SetBase(Func<double, double> baseAt) => _baseAt = baseAt;

        /// <summary>The strip's own outer edge line starts this far from the mouth: a right-turn pocket draws the nearer part (#348).</summary>
        private double _edgeFrom;

        /// <summary>At traffic lights, the stop line's extra setback for a skewed mouth (#348).</summary>
        public double StopShift { get; set; }

        /// <summary>The left-turn bike lane between the pocket and the through lane (#351), 0 none; and whether a bike box lies in front.</summary>
        private readonly double _bikeLeft;
        public bool BikeBox { get; set; }

        /// <summary>The moves OSM's lane data gave the pocket's lanes and the carriageway's own (#700), null: inferred.</summary>
        public SignalMoves[]? PocketMoves { get; set; }
        public SignalMoves[]? OwnMoves { get; set; }

        /// <summary>
        /// At traffic lights (#682) the exit's through lane continues the facing approach's through lane: the hatch is as wide as its pocket
        /// and bike lane at the mouth and the lane after it as wide as an approach lane (<see cref="ApproachLayout.LaneWidth"/>), so it never starts narrow.
        /// </summary>
        public void SetExit(double hatch, double lane) => (_exitHatch, _exitLane) = (hatch, lane);
        private double? _exitHatch, _exitLane;
        /// <summary>
        /// (#711, junctions without lights) the exit's car lane at the mouth is <paramref name="lane"/> wide, not the carriageway's
        /// own lane: the widening is the hatch and that difference, the lane easing back to the carriageway's as the hatch closes.
        /// </summary>
        public void SetExitLane(double lane) => _exitLaneSet = lane;
        /// <summary>An island stands in the exit's hatch (#711): the lights' repeater island (#682) or a refuge (#700); a through guide passes it.</summary>
        public bool HasIsland { get; set; }
        private double? _exitLaneSet;
        public double HatchAtMouth => _bike > 0 ? PocketRegion : _exitHatch ?? PocketRegion;   // beside an on-street bike lane the exit is as at a yield junction (#123, #120)
        /// <summary>The width of the exit's car lane (the carriageway's half less a painted bike lane).</summary>
        public double ExitCar => _bike > 0 ? TurnLane : _car;   // beside an on-street bike lane the exit keeps a turn-lane wide car lane and the bike lane (as at a yield junction)
        public bool HasLeftBikeLane => _bikeLeft > 0;

        /// <summary>What the widening adds at full width: the through lane, and on an approach the left-turn bike lane (#351).</summary>
        private double Lane => _lanes * TurnLane + _bikeLeft;

        /// <summary>Lanes the widening adds (#700: a double left pocket adds two).</summary>
        public int LaneCount => _lanes;
        private readonly int _lanes;

        /// <summary>The left pocket region at full width: the approach lane and the lanes added beside it, bike lane not counted.</summary>
        private double PocketRegion => _pocket + (_lanes - 1) * TurnLane;

        /// <summary>The lanes across the approach (#351), set before its paint.</summary>
        public ApproachLayout? Layout { get; set; }

        public double Half => _half;
        public int Side => _side;
        /// <summary>The painted bike lane on the widened side it reckons with (#120), 0 none.</summary>
        public double BikeWidth => _bike;
        /// <summary>The left pocket: the approach lane, widened to a full lane beside a bike lane.</summary>
        public double PocketWidth => _pocket;
        public double LeftBikeWidth => _bikeLeft;
        public double Extra => _extra;

        /// <summary>The widening's width at the mouth (or at its far end, for a merged strip seen from the junction it leaves).</summary>
        public double MouthWidth(bool far = false) => Widen(far ? _length : 0) + FrameAt(far ? _length : 0);   // beyond the carriageway's own edge

        /// <summary>
        /// The strip's outer edge <paramref name="dist"/> metres out from the mouth (from its far end
        /// for a merged strip seen from the junction it leaves, #325): LV95 plan and road height.
        /// </summary>
        public (Vec2 P, float Y) OuterEdge(double dist, bool far = false)
        {
            double d = far ? _length - dist : dist;
            var p = Point(d, _half + BaseAt(d) + Widen(d));
            return (new Vec2(_tile.MinE + p[0], _tile.MaxN - p[2]), p[1]);
        }

        /// <summary>The along-segment metres the widening covers, mouth to its far end.</summary>
        public (double From, double To) AlongRange() => _atEnd ? (_total - Reach, _total) : (0, Reach);

        /// <summary>Its width along the storage: the through lane, the bike lanes' extras (#120, #351).</summary>
        public double FullWidth => Lane + _extra;

        /// <summary>Leaves the strip's outer edge line to whatever runs outside it up to <paramref name="dist"/> from the mouth.</summary>
        public void EdgeFrom(double dist)
        {
            _edgeFrom = dist;
            if (!_dists.Any(x => Math.Abs(x - dist) < 1e-6)) { _dists.Add(dist); _dists.Sort(); }
        }

        /// <summary>A bike lane on the widened side (#120), and whether a lead-in brings its extra width on.</summary>
        public bool BesideBike => _extra > 0.05;
        public bool HasLeadIn => _lead > 0;

        /// <summary>How far from the mouth the widening reaches: the taper and storage (or exit taper), then the lead-in.</summary>
        public double Reach => _length + _lead;

        /// <summary>Set once its strip and paint are out (a merged one serves two pockets).</summary>
        public bool Emitted { get; private set; }

        // what the approach's lane record (#353, TileRewriter.Lanes) reads of the built widening (where
        // its lanes lie is the layout's): its length, taper, lead-in
        public double Length => _length;
        public double Taper => _taper;
        public double Lead => _lead;

        /// <summary>A point beside the segment, <paramref name="offset"/> out from its centre line toward the widened side, <paramref name="dist"/> from the mouth, in <paramref name="frame"/>'s tile-local frame.</summary>
        public float[] At(TileId frame, double dist, double offset)
        {
            var p = Point(dist, offset);
            return [(float)(p[0] + _tile.MinE - frame.MinE), p[1], (float)(p[2] + frame.MaxN - _tile.MaxN)];
        }

        /// <param name="painted">The segment the road's lines were laid on (the one they name), <paramref name="seg"/> or an Urban-flagged copy of it.</param>
        public Widening(RoadSegment seg, RoadSegment painted, TileId tile, int self, bool junctionAtEnd, int side, double length, double taper,
            double clear = TurnClear, double[]? stations = null, bool exit = false, bool leadIn = true, double baseOffset = 0,
            double bikeLeft = 0, int lanes = 1)
        {
            _bikeLeft = exit ? 0 : bikeLeft;
            _lanes = lanes;
            _seg = seg; _painted = painted; _tile = tile; _self = self; _atEnd = junctionAtEnd; _side = side;
            _length = length; _taper = taper; _clear = clear; _half = seg.Width * 0.5; _exit = exit; _base = baseOffset;
            // the lines as painted: a street that got separated paths has no painted lane (#120)
            var widened = side > 0 ? painted.Attributes.Right : painted.Attributes.Left;
            // a right-turn pocket (#348) is a plain lane: a bike lane stays outside it (#351)
            _bike = widened.HasLane && baseOffset <= 0 ? widened.BikeDm / 10.0 : 0;
            _car = _half - _bike;
            _extra = _bike > 0 ? Math.Max(0, CarLaneBesideBike - _car) : 0;
            // no room for a lead-in (a short street), or a strip full width all along: the extra comes with the taper
            _lead = _extra > 0.05 && leadIn && taper > 0 ? Math.Max(LeadInMin, _extra * LeadInPerMetre) : 0;
            _pocket = _car + _extra;
            int n = seg.PointCount;
            var p = seg.Points;
            _along = new double[n];
            for (int k = 1; k < n; k++)
                _along[k] = _along[k - 1] + Math.Sqrt(Sq(p[k * 3] - p[k * 3 - 3]) + Sq(p[k * 3 + 2] - p[k * 3 - 1]));
            _total = _along[^1];
            for (double dd = 0; dd < Reach - 1e-6; dd += TurnStation) _dists.Add(dd);
            foreach (double extra in stations ?? [length - taper])
                if (!_dists.Any(x => Math.Abs(x - extra) < 1e-6)) _dists.Add(extra);
            foreach (double at in (ReadOnlySpan<double>)[length, Reach])
                if (!_dists.Any(x => Math.Abs(x - at) < 1e-6)) _dists.Add(at);
            _dists.Sort();
        }

        /// <summary>
        /// The widening at a distance from the mouth. Without a bike lane on the widened side, one
        /// lane (#123). With one (#120): on an approach, the lead-in's extra (the car lane beside
        /// the bike lane brought to a full lane) and then the lane; at an exit, the through lane
        /// and the bike lane beside the closing hatch, then the extra easing back over the lead-out;
        /// with no room for a lead-in or lead-out, the extra rides on the taper.
        /// </summary>
        private double Widen(double dist)
        {
            if (_width is { } width) return Math.Max(0, width(dist));
            double full = WidenFull(dist);
            if (!_exit || (_arrivesLess <= 0 && _frame is null)) return full;
            // an exit shrunk by the split lead-ins (#700): what the through lane still needs beyond the arm's own moved edge
            return Math.Max(0, full - ShrinkAt(dist));
        }

        private double WidenFull(double dist)
        {
            // #682: the exit lane keeps its width (the carriageway's lane) from the mouth to where the hatch has closed: the edge moves in as fast as the hatch
            if (_exit && _bike <= 0 && _exitHatch is { } hatch) return Math.Max(0, (hatch + (_exitLaneSet - _car ?? 0)) * (1 - dist / _length));   // #682, #711; with an on-street bike lane the yield junction's exit below (a car lane and the bike lane beside the closing hatch, then the lead-out)
            if (_taper <= 0) return Lane + _extra;
            double main = Math.Clamp((_length - dist) / _taper, 0, 1);
            if (_lead <= 0 || (_exit && _bike <= 0)) return (Lane + _extra) * main;
            double lead = _extra * Math.Clamp((Reach - dist) / _lead, 0, 1);
            if (!_exit) return Lane * main + lead;
            if (dist > _length) return lead;
            // the hatch closes from the pocket's width to nothing; the through lane and the bike lane keep their widths
            return Math.Max(0, _pocket * (1 - dist / _length) + TurnLane - _car);
        }


        // ---- the veer share (#700): a lead-in split between both edges --------------------------------------------
        /// <summary>
        /// How far the lane frame stands toward the widened side at a distance from the mouth (#700): an approach whose lead-in
        /// is split moves its lanes, centre line and hatch toward the other side by the other side's share (negative); an exit
        /// beside its own arm's split lead-in moves out with that arm's centre line (positive). Null: 0, the carriageway's axis.
        /// </summary>
        private Func<double, double>? _frame;
        /// <summary>The widening's width overridden (a mirror strip: the other side's share of a split lead-in).</summary>
        private Func<double, double>? _width;
        /// <summary>An exit's shrink (#700): the through lane arrives this much less shifted at the mouth, closing over the exit.</summary>
        private double _arrivesLess;
        /// <summary>... and the narrowest its hatch may get where it carries a centre island (#682), 0 none.</summary>
        private double _keepHatch;

        private double FrameAt(double dist) => _frame?.Invoke(Math.Clamp(dist, 0, Reach)) ?? 0;
        public double Frame0 => FrameAt(0);
        public Func<double, double>? FrameFunc => _frame;
        public double WidthAt(double dist) => Widen(dist);
        public bool Framed => _frame is not null;
        public void MarkEmitted() => Emitted = true;
        /// <summary>An exit's hatch at a distance from the mouth, shrunk by the split lead-ins (#700).</summary>
        public double HatchAt(double dist) => Math.Max(0, HatchAtMouth * Math.Clamp(1 - dist / _length, 0, 1) - ShrinkAt(dist));

        /// <summary>Splits the lead-in (#700): the lanes stand <paramref name="shift"/>(d) toward the other side.</summary>
        public void SetFrame(Func<double, double>? shift) => _frame = shift;

        /// <summary>Shrinks an exit (#700): the through lane arrives <paramref name="less"/> m less shifted; <paramref name="keepHatch"/> the island's minimum hatch.</summary>
        public void SetShrink(double less, double keepHatch) => (_arrivesLess, _keepHatch) = (less, keepHatch);

        /// <summary>A strip for the other side's share of a split lead-in: its width at a distance from the mouth.</summary>
        public void SetWidth(Func<double, double> width) => _width = width;

        /// <summary>The full lane widening at a distance from the mouth, before any split (an approach's through-lane shift).</summary>
        public double FullAt(double dist) => WidenFull(dist);

        /// <summary>What an exit's shrink takes off at a distance from the mouth: the through lane's lesser shift, closing over the exit, and the arm's own centre line moved out.</summary>
        private double ShrinkAt(double dist) => _exit ? (_arrivesLess + Math.Max(0, FrameAt(0))) * Math.Clamp(1 - dist / _length, 0, 1) : 0;   // in the frame: both close over the exit
        /// <summary>The segment at a distance from the mouth: position, road height, unit vector to the widened side.</summary>
        private (double X, double Y, double Z, double Sx, double Sz) At(double dist, bool shifted = true)
        {
            double a = AlongOf(dist);
            int n = _seg.PointCount, k = 1;
            var p = _seg.Points;
            while (k < n - 1 && _along[k] < a) k++;
            double span = Math.Max(1e-9, _along[k] - _along[k - 1]), t = Math.Clamp((a - _along[k - 1]) / span, 0, 1);
            double x = p[k * 3 - 3] + (p[k * 3] - p[k * 3 - 3]) * t, z = p[k * 3 - 1] + (p[k * 3 + 2] - p[k * 3 - 1]) * t;
            double y = p[k * 3 - 2] + (p[k * 3 + 1] - p[k * 3 - 2]) * t;
            double fx = (p[k * 3] - p[k * 3 - 3]) / span, fz = (p[k * 3 + 2] - p[k * 3 - 1]) / span;
            double sx = -fz * _side, sz = fx * _side;   // right of the drawing is (-fz, fx), X east and Z south
            if (_frame is null || !shifted) return (x, y, z, sx, sz);
            double shift = FrameAt(dist);   // the lane frame of a split lead-in (#700)
            return (x + sx * shift, y, z + sz * shift, sx, sz);
        }

        private float[] Point(double dist, double offset)
        {
            var (x, y, z, sx, sz) = At(dist);
            return [(float)(x + sx * offset), (float)y, (float)(z + sz * offset)];
        }

        /// <summary>A point beside the segment at an offset from its own centre line, whatever the lane frame (#700).</summary>
        private float[] PointRaw(double dist, double offset)
        {
            var (x, y, z, sx, sz) = At(dist, shifted: false);
            return [(float)(x + sx * offset), (float)y, (float)(z + sz * offset)];
        }

        /// <summary>
        /// The strip's inner edge: the carriageway's edge (beside a split lead-in's frame moved in, still the edge: the strip
        /// never lies on the carriageway; moved out with an exit's frame, past the arm's own strip), or a pocket beside another's.
        /// </summary>
        private float[] Inner(double dist) =>
            _base > 0 || _frame is null ? Point(dist, _half + BaseAt(dist)) : PointRaw(dist, _half + Math.Max(0, FrameAt(dist)));

        /// <summary>
        /// The strip's inner and outer corners at the mouth (less <paramref name="inset"/> on the
        /// outer one), as points in <paramref name="frame"/>'s tile-local frame; at its far end for
        /// a merged strip seen from the junction it leaves (#325).
        /// </summary>
        public (float[] Inner, float[] Outer) Mouth(TileId frame, double inset = 0, bool far = false)
        {
            float[] In(float[] p) =>
                [(float)(p[0] + _tile.MinE - frame.MinE), p[1], (float)(p[2] + frame.MaxN - _tile.MaxN)];
            double at = far ? _length : 0;
            return (In(Inner(at)), In(Point(at, _half + BaseAt(at) + Widen(at) - inset)));
        }

        /// <summary>
        /// Moves the point props of the segment's tile that stand beside the old edge along the
        /// widening out by the widening there, so they keep their clearance from the new edge.
        /// Returns how many moved.
        /// </summary>
        public int PushOut(List<RoadPointProp> props)
        {
            int moved = 0;
            var p = _seg.Points;
            for (int i = 0; i < props.Count; i++)
            {
                var prop = props[i];
                // nearest point of the segment, and the prop's offset toward the widened side
                double best = double.MaxValue, at = 0, offset = 0;
                for (int k = 1; k < _seg.PointCount; k++)
                {
                    double ax = p[k * 3 - 3], az = p[k * 3 - 1], dx = p[k * 3] - ax, dz = p[k * 3 + 2] - az;
                    double l2 = dx * dx + dz * dz;
                    if (l2 < 1e-12) continue;
                    double t = Math.Clamp(((prop.X - ax) * dx + (prop.Z - az) * dz) / l2, 0, 1);
                    double px = ax + dx * t, pz = az + dz * t, d2 = Sq(prop.X - px) + Sq(prop.Z - pz);
                    if (d2 >= best) continue;
                    best = d2;
                    at = _along[k - 1] + Math.Sqrt(l2) * t;
                    double len = Math.Sqrt(l2);
                    offset = ((prop.X - px) * (-dz / len) + (prop.Z - pz) * (dx / len)) * _side;   // right of the drawing is (-dz, dx)
                }
                double dist = _atEnd ? _total - at : at;
                if (dist < -1 || dist > Reach) continue;
                double w = Widen(Math.Clamp(dist, 0, _length));
                double edgeMove = w + FrameAt(Math.Clamp(dist, 0, _length));   // how far the edge moved out there (a split lead-in, #700: its share)
                if (edgeMove < 0.05 || offset < _half + BaseAt(dist) - 0.3 || offset > _half + BaseAt(dist) + edgeMove + 2.0) continue;
                var (x, _, z, sx, sz) = At(Math.Clamp(dist, 0, Reach), shifted: false);
                double o = offset + edgeMove;
                props[i] = prop with { X = (float)(x + sx * o), Z = (float)(z + sz * o) };
                moved++;
            }
            return moved;
        }

        /// <summary>Along-segment metres of a distance from the mouth.</summary>
        private double AlongOf(double dist) => _atEnd ? _total - dist : dist;

        /// <summary>Null when it fits; else why not.</summary>
        public string? Check(EmbankmentPlanner.LineIndex lines, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings)
        {
            if (_total < Reach + _clear) return "short";
            foreach (double dist in _dists)
            {
                double w = Widen(dist);
                if (w < 0.3) continue;
                var (x, y, z, sx, sz) = At(dist);
                foreach (double o in (ReadOnlySpan<double>)[_half + BaseAt(dist) + w * 0.5, _half + BaseAt(dist) + w + 0.5])
                {
                    double px = x + sx * o, pz = z + sz * o;
                    if (px < 0 || pz < 0 || px > TileSizeM || pz > TileSizeM) return "seam";
                    var lv95 = new Vec2(_tile.MinE + px, _tile.MaxN - pz);
                    if (buildings.Contains(lv95)) return "building";
                    if (lines.Covering(px, pz, _self, x, z, _half + 1.0) is not null) return "line";
                    if (grids is not null && SampleGround(grids, lv95.X, lv95.Y) is var g && !double.IsNaN(g)
                        && Math.Abs(g - y) > TurnGroundStep) return "ground";
                }
            }
            return null;
        }

        /// <summary>The strip, and the edge line moved out onto it; <paramref name="owners"/> learns whose strip it is.</summary>
        public void Emit(List<RoadPaint> paint, List<RoadAreaProp> areas, Dictionary<RoadAreaProp, RoadSegment> owners)
        {
            Emitted = true;
            var v = new List<float>();
            var outer = new List<float>();
            foreach (double dist in _dists)
            {
                double w = Widen(dist);
                v.AddRange(Inner(dist));
                v.AddRange(Point(dist, _half + BaseAt(dist) + w));
                if (dist >= _edgeFrom - 1e-6) outer.AddRange(Point(dist, _half + BaseAt(dist) + w - PaintEmitter.EdgeLineInset));
            }
            var idx = new List<ushort>();
            for (int k = 0; k + 1 < _dists.Count; k++)
            {
                ushort i0 = (ushort)(k * 2), i1 = (ushort)(k * 2 + 1), i2 = (ushort)(k * 2 + 2), i3 = (ushort)(k * 2 + 3);
                idx.AddRange([i0, i2, i3, i0, i3, i1]);
            }
            var strip = new RoadAreaProp
            {
                Type = AreaPropType.Pavement, Flags = PropFlags.None, Height = 0f,
                Vertices = v.ToArray(), Indices = idx.ToArray(),
            };
            areas.Add(strip);
            owners[strip] = _seg;

            // the old edge line stops where the widening starts; a new one follows the strip (a
            // right-turn pocket beside a left one: the old line is already cut, its edge is new)
            var edge = _base > 0 ? null
                : paint.FirstOrDefault(q => q.Segment == _painted && q.Dash == 0 && Math.Sign(q.Offset) == _side && Math.Abs(q.Offset) > _half * 0.5);
            if (edge is null && _base <= 0) return;
            if (edge is not null) Cut(paint, edge);
            if (outer.Count >= 6)
                paint.Add(new RoadPaint
                {
                    Shape = PaintShape.Polyline, Type = PaintType.WhiteSolid, Rgba = PaintEmitter.White, Width = edge?.Width ?? PaintEmitter.LineWidth,
                    Vertices = outer.ToArray(),
                });
        }

        /// <summary>Removes a line along the segment where the widening runs, keeping the rest.</summary>
        private void Cut(List<RoadPaint> paint, RoadPaint line)
        {
            paint.Remove(line);
            double far = AlongOf(_length);   // the widening's far end, in along-segment metres
            double from = _atEnd ? line.From : Math.Max(far, line.From), to = _atEnd ? Math.Min(far, line.To) : line.To;   // #700: never more than was left of it (a widening cut before kept its part)
            // a line to the end has To = infinity: none is left past a strip as long as the segment (#325)
            if (Math.Min(to, _total) - from > 1)
                paint.Add(RoadPaint.AlongSegment(line.Segment ?? _seg, line.Type, line.Rgba, line.Width, line.Dash, line.Gap, line.Offset, from, to, line.Variant));   // #700: on the segment it was laid on, where the next cut looks
        }


        /// <summary>
        /// The dashed lines between the approach's own lanes, and the centre line (#700: lanes assigned in place at traffic lights):
        /// solid over the <see cref="TurnSolid"/> metres before the stop line <paramref name="stop"/> m from the mouth, the centre
        /// line over <paramref name="centreSolid"/> (#711), and none past it. <paramref name="centreOnly"/>: the centre line alone.
        /// Returns how many lines it changed.
        /// </summary>
        public int SolidToStop(List<RoadPaint> paint, double stop, double centreSolid = TurnSolid, bool centreOnly = false, double? centreAt = null)
        {
            // the centre line: where the approach's lanes begin (#711: with more lanes toward the junction than away it lies past
            // the axis, on the far side), else the dashed line nearest the axis (a 2+1 road's is off its middle, never by a lane)
            bool Centre(double across) => centreAt is { } c ? Math.Abs(across - c) < 0.3 : across < 1.0;
            double nearest = Math.Min(-0.05, (centreAt ?? 0) - 0.3);
            var lines = paint.Where(q => (q.Segment == _painted || q.Segment == _seg) && q.Type == PaintType.WhiteDashed && q.Dash > 0
                && q.Offset * _side > nearest && q.Offset * _side < _half - 0.5 && (!centreOnly || Centre(q.Offset * _side))).ToList();
            foreach (var line in lines)
            {
                double near = AlongOf(stop), far = AlongOf(stop + (Centre(line.Offset * _side) ? centreSolid : TurnSolid));
                paint.Remove(line);
                // the dashes as they were, away from the junction
                double from = _atEnd ? line.From : Math.Max(far, line.From), to = _atEnd ? Math.Min(far, line.To) : line.To;   // #700: never more than was left of it (a widening cut before kept its part)
                if (Math.Min(to, _total) - from > 1)
                    paint.Add(RoadPaint.AlongSegment(line.Segment ?? _seg, line.Type, line.Rgba, line.Width, line.Dash, line.Gap, line.Offset, from, to, line.Variant));   // #700: on the segment it was laid on, where the next cut looks
                // (#711: no further than the segment, a short one ends before the solid stretch would)
                double a = Math.Clamp(Math.Min(near, far), 0, _total), b = Math.Clamp(Math.Max(near, far), 0, _total);
                if (b - a > 1) paint.Add(RoadPaint.AlongSegment(_seg, PaintType.WhiteSolid, line.Rgba, line.Width, 0, 0, line.Offset, a, b));
            }
            return lines.Count;
        }
        /// <summary>A line along the segment between two distances from the mouth.</summary>
        private RoadPaint Line(PaintType type, float dash, float gap, double offset, double d0, double d1)
        {
            double a = AlongOf(d0), b = AlongOf(d1);
            return Along(type, PaintEmitter.White, PaintEmitter.LineWidth, dash, gap, offset, Math.Min(a, b), Math.Max(a, b));
        }

        /// <summary>
        /// A line along the segment, <paramref name="offset"/> right of the drawing, between two along-segment metres: as the
        /// segment's own paint where the lanes keep the axis; in a split lead-in's lane frame (#700) a polyline that follows it.
        /// </summary>
        private RoadPaint Along(PaintType type, uint rgba, float width, float dash, float gap, double offset, double from, double to, byte variant = 0)
        {
            if (_frame is null) return RoadPaint.AlongSegment(_seg, type, rgba, width, dash, gap, offset, from, to, variant);
            double Dist(double along) => _atEnd ? _total - along : along;
            to = Math.Min(to, _total);
            var line = new List<float>();
            line.AddRange(Point(Dist(from), offset * _side));
            foreach (double d in _dists.Select(Dist).Where(a => a > from + 1e-3 && a < to - 1e-3).OrderBy(a => a)) line.AddRange(Point(Dist(d), offset * _side));
            line.AddRange(Point(Dist(to), offset * _side));
            return new RoadPaint { Shape = PaintShape.Polyline, Type = type, Rgba = rgba, Width = width, Dash = dash, Gap = gap, Variant = variant, Vertices = line.ToArray() };
        }

        /// <summary>
        /// The approach, from far to near. Over the taper the strip widens on the right while a
        /// hatched median opens between the centre line and the through lane, carrying the lane
        /// across by a lane width. Where the hatch closes, the left-turn lane appears beside the
        /// through lane, whose left edge carries on as a dashed line (taking the pocket is a lane
        /// change), solid for the last <see cref="TurnSolid"/> m. A stop bar closes the pocket at
        /// the mouth; arrows in both lanes; the centre line is solid along all of it.
        /// </summary>
        public void Pocket(List<RoadPaint> paint, bool rightTurn, TurnLaneStats stats, JunctionRules rules)
        {
            Painted = true;
            double storage = _length - _taper;
            SolidCentre(paint);
            double wide = Layout is { Equal: true, LeftPocketLane: { } own } ? own.To + _bikeLeft : PocketRegion + _bikeLeft;   // #682: equal lanes
            double Border(double d) => wide * Math.Clamp((_length - d) / _taper, 0, 1);
            // a pocket of several lanes (#700): the hatch is at most the leftmost lane wide, so it opens one lane only; the
            // lanes right of it carry the approach's own lane on, and the through lane leaves them over the taper behind a dashed line
            double cap = LeadCap;
            Hatch(paint, stats, storage, _length, d => Math.Min(cap, Border(d)), slant: LeadSlant, towardMouth: true);
            if (cap < wide - 1e-6) ThroughEdge(paint, storage, Border, cap);
            // the through lane's edge runs on to the angled line's outer end for one lane; for several, the lines between the pocket's
            // lanes start where the left-turn bike lane's do, at the storage (the user's spec, #700)
            Lanes(paint, storage, cap < wide - 1e-6 ? storage : storage + LeadSlant, rightTurn, stats, rules);
        }


        /// <summary>
        /// How far along the road a lead-in hatch's closing line runs, from the centre line at the start of the storage back to the
        /// hatch's border (#700, the user's spec: an angled line, a gentle lead into the left pocket): 10 m, at most half the taper.
        /// </summary>
        public double LeadSlant => Math.Min(LeadInClose, _taper * 0.5);
        /// <summary>The hatch's widest: the whole pocket for one lane, the leftmost lane for several (#700).</summary>
        private double LeadCap => _lanes <= 1 ? double.MaxValue
            : Layout is { LeftLanes: > 1 } l ? l.LeftLane(0).To : _pocket;

        /// <summary>The through lane's left edge along a several-lane pocket's taper (#700): a dashed line where it runs right of the hatch.</summary>
        private void ThroughEdge(List<RoadPaint> paint, double storage, Func<double, double> border, double cap)
        {
            // the taper's metre where the edge leaves the capped hatch, then on to where the pocket's lanes are full
            double from = _length - _taper * Math.Clamp(cap / Math.Max(1e-6, border(storage)), 0, 1);
            var line = new List<float>();
            line.AddRange(Point(storage, border(storage)));
            foreach (double d in _dists.Where(x => x > storage + 1e-3 && x < from - 1e-3)) line.AddRange(Point(d, border(d)));
            line.AddRange(Point(from, border(from)));
            paint.Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.WhiteDashed, Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
                Dash = 3f, Gap = 3f, Vertices = line.ToArray(),
            });
        }

        /// <summary>
        /// A merged strip (#325), from the pocket's mouth to the junction before: the through lane
        /// stays out all along; the left-turn lane, <paramref name="storage"/> m long, opens over
        /// <see cref="TurnEntry"/> out of a lane-wide hatched median that runs on to the junction
        /// before (its exit's median, which no longer closes), up to <paramref name="farStop"/> short
        /// of that junction's mouth (its stop line, #406). The centre line is solid all along.
        /// </summary>
        public void Through(List<RoadPaint> paint, double storage, bool rightTurn, TurnLaneStats stats, JunctionRules rules, double farStop)
        {
            Painted = true;
            SolidCentre(paint);
            Hatch(paint, stats, storage, _length - farStop, d => Math.Min(LeadCap, PocketRegion * Math.Clamp((d - storage) / TurnEntry, 0, 1)), towardMouth: true);   // #700: one lane at most; leaning with the traffic driving in
            Lanes(paint, storage, storage + TurnEntry, rightTurn, stats, rules);
        }

        /// <summary>
        /// The left-turn lane's markings: the through lane's left edge dashed from
        /// <paramref name="dashedTo"/> in to <see cref="TurnSolid"/> m, then solid; the stop bar; the arrows.
        /// </summary>
        private void Lanes(List<RoadPaint> paint, double storage, double dashedTo, bool rightTurn, TurnLaneStats stats, JunctionRules rules)
        {
            // at traffic lights the stop line stands back from the mouth (#348): the lanes end there
            bool signal = rules.Has(JunctionRule.StopLine);
            float width = rules.StopWidth;
            double setback = rules.StopSetback + StopShift;
            // the lanes across the approach (#351): pocket, left-turn bike lane, through lane
            var lanes = Layout ?? new ApproachLayout(_half, _bike, _pocket, _bikeLeft, FullWidth, false, 0);
            double pocket = lanes.LeftPocketLane?.To ?? lanes.LeftPocket, through = lanes.Through().From;   // #682: the equal lane widths, not the carriageway lane it was cut from
            bool box = _bikeLeft > 0 && BikeBox;
            // the pocket's arrows and its stop line stand behind a bike box (#351)
            bool advanced = _bikeLeft > 0 && !BikeBox;
            double pocketBack = setback + (box ? BikeBoxDepth : advanced ? AdvancedBikeLine : 0);   // #682: the advanced line stands level with the through lane's, the cars' behind it
            if (_bikeLeft > 0)
            {
                // the left-turn bike lane (#351): yellow lines both sides, from where the pocket opens
                // to the line it stops at (the bike box, or its advanced line ahead of the cars'),
                // dashed, then solid over the last TurnSolid metres before the cars' line (#406: as
                // the car lanes' lines), and its symbol where riders come in
                double bikeLine = box ? pocketBack : setback;
                double solidTo = pocketBack + TurnSolid;
                void Edge(PaintType type, float dash, double o, double d0, double d1) =>
                    paint.Add(Along(type, PaintEmitter.Yellow, BikePlanner.LineWidth, dash, dash == 0 ? 0 : BikePlanner.Gap,
                        _side * o, Math.Min(AlongOf(d0), AlongOf(d1)), Math.Max(AlongOf(d0), AlongOf(d1))));
                foreach (double o in (ReadOnlySpan<double>)[pocket, through])
                {
                    Edge(PaintType.YellowSolid, 0, o, bikeLine, solidTo);
                    Edge(PaintType.YellowDashed, BikePlanner.Dash, o, solidTo, dashedTo);
                }
                // a box: a yellow line closes it from the through lane beside it, which has none (#682)
                if (box) Edge(PaintType.YellowSolid, 0, through, setback, pocketBack);
                BikeSymbol(paint, lanes.LeftBikeLane!.Value.Mid, dashedTo - 2);
                stats.LeftBikeLanes++;
            }
            else
            {
                double offset = _side * pocket;
                paint.Add(Line(PaintType.WhiteDashed, 3f, 3f, offset, TurnSolid + setback - 0.1, dashedTo));
                paint.Add(Line(PaintType.WhiteSolid, 0, 0, offset, signal ? setback : 0, TurnSolid + setback - 0.1));
            }

            // the lines between the pocket's own lanes (#700): dashed, solid over the last stretch before the stop line
            for (int k = 1; k < lanes.LeftLanes; k++)
            {
                double between = _side * lanes.LeftLane(k).From;
                paint.Add(Line(PaintType.WhiteDashed, 3f, 3f, between, TurnSolid + pocketBack - 0.1, dashedTo));
                paint.Add(Line(PaintType.WhiteSolid, 0, 0, between, signal ? pocketBack : 0, TurnSolid + pocketBack - 0.1));   // to the pocket's own stop line
            }

            // across the pocket, just short of the mouth; at traffic lights across the through lane
            // too, as wide as SSV 6.10 asks
            void Bar(double dist, double from, double to, float w, uint rgba) => paint.Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = rgba, Width = w,
                Vertices = [.. Point(dist + w * 0.5, from), .. Point(dist + w * 0.5, to)],
            });
            if (box)
            {
                // the box: the cars' line behind it across the pocket and the bike lane, the yellow one
                // in front, a bike symbol in it; the through lane's line where it was
                // the cars' line only across the pocket: the left-turn bike lane reaches into the box (#682)
                Bar(pocketBack, 0.1, pocket - 0.1, width, PaintEmitter.White);
                Bar(setback, through + 0.1, lanes.Through().To - 0.1, width, PaintEmitter.White);
                Bar(setback, 0.1, through - 0.1, BikeStopLine, PaintEmitter.Yellow);
                BikeSymbol(paint, through * 0.5, setback + BikeBoxDepth * 0.5 + BikePlanner.SymbolSize * 0.5);
                stats.BikeBoxes++;
            }
            else
            {
                if (_bikeLeft > 0)
                {
                    // the cars' line stops at the bike lane's edges (#406): riders go on to their own line
                    Bar(pocketBack, 0.1, pocket - 0.1, width, PaintEmitter.White);
                    Bar(setback, through + 0.1, lanes.Through().To - 0.1, width, PaintEmitter.White);
                    // an advanced bike stop line across the bike lane, level with the through lane's; the pocket's cars stop behind it (#682)
                    Bar(setback, pocket + 0.05, through - 0.05, BikeStopLine, PaintEmitter.Yellow);
                    stats.AdvancedBikeLines++;
                }
                else Bar(setback, 0.1, (signal ? lanes.Through().To : pocket) - 0.1, width, PaintEmitter.White);
            }
            stats.StopBars++;

            // two per lane in the storage length, tips 5 m from the stop bar and 15 m apart (Bern
            // Normalien C 2.10.17), closer in a short pocket: left in the pocket, straight (and
            // right) in the through lane
            double second = storage >= 20 + PaintEmitter.ArrowLength ? 20 : 13;
            foreach (double tip in (ReadOnlySpan<double>)[5 + PaintEmitter.ArrowLength, second + PaintEmitter.ArrowLength])
            {
                double back = tip + setback - 0.1;   // from the stop line, wherever it stands
                var (x, y, z, sx, sz) = At(back);
                // the through lane lies on the driver's right (sx, sz): their forward is that turned a quarter left
                double fx = sz, fz = -sx;
                var (px, py, pz, _, _) = At(tip + pocketBack - 0.1);
                // each arrow climbs with the road to its tip, ArrowLength nearer the mouth (#639)
                double tipY = At(back - PaintEmitter.ArrowLength).Y, pocketTipY = At(tip + pocketBack - 0.1 - PaintEmitter.ArrowLength).Y;
                double ahead = lanes.Through().Mid;
                // the pocket's lanes (#700: a double left has two), each with the arrow OSM's lane data gave it, else left
                for (int k = 0; k < lanes.LeftLanes; k++)
                {
                    var arrow = PocketMoves is { } pm && k < pm.Length ? ArrowOf(pm[k]) : PaintArrow.Left;
                    if (arrow is not { } kindOf) continue;
                    double left = lanes.LeftLane(k).Mid;
                    paint.Add(PaintEmitter.Arrow(px + sx * left, py, pz + sz * left, fx, fz, kindOf, pocketTipY));
                    stats.Arrows++;
                }
                var own = OwnMoves is { Length: > 0 } om ? ArrowOf(om[0]) : rightTurn ? PaintArrow.Straight | PaintArrow.Right : PaintArrow.Straight;
                if (own is { } ownKind)
                {
                    paint.Add(PaintEmitter.Arrow(x + sx * ahead, y, z + sz * ahead, fx, fz, ownKind, tipY));
                    stats.Arrows++;
                }
            }
        }

        /// <summary>A yellow bike symbol (#120's glyph) at <paramref name="offset"/> from the centre, its front <paramref name="dist"/> from the mouth, facing the approach's traffic.</summary>
        private void BikeSymbol(List<RoadPaint> paint, double offset, double dist)
        {
            float size = BikePlanner.SymbolSize;
            double a = AlongOf(dist), b = AlongOf(dist - size);
            // the approach drives toward the mouth: with the drawing when the junction is at the end
            paint.Add(Along(PaintType.BikeSymbol, PaintEmitter.Yellow, size, 0, 0, _side * offset,
                Math.Min(a, b), Math.Max(a, b), _atEnd ? (byte)0 : RoadPaintGeometry.BikeReversed));
        }

        /// <summary>
        /// A right-turn pocket's markings (#348), on the lanes of <see cref="Layout"/> (#351): the line
        /// between it and the through lane dashed, solid for the last <see cref="TurnSolid"/> m, its
        /// part of the stop line, right arrows in it and, with no left pocket, <paramref name="through"/>
        /// arrows in the approach's own lane. Layout (b): the painted bike lane runs between the
        /// through lane and the pocket, drawn here along the pocket's reach (#120's paint stops
        /// short of it): yellow lines both sides, dashed where the pocket opens (cars cross it there
        /// to reach the pocket), solid along the storage; a bike symbol; the stop line across it too.
        /// </summary>
        public void RightLane(List<RoadPaint> paint, TurnLaneStats stats, PaintArrow? through, JunctionRules rules)
        {
            var lanes = Layout!;
            // (#711) without lights the main road's right turners give way to no one: no stop line, the lanes run on to the mouth
            double storage = _length - _taper, setback = rules.StopSetback + StopShift;
            double Open(double d) => Math.Clamp((_length - d) / Math.Max(_taper, 1e-6), 0, 1);
            var pocket = lanes.RightPocket()!.Value;
            double inner = lanes.Through().To;   // the through lane's right edge, along the storage
            if (lanes.BikeBetween)
            {
                var bike = lanes.BikeLane()!.Value;
                foreach (double o in (ReadOnlySpan<double>)[bike.From, bike.To])
                    paint.Add(Along(PaintType.YellowSolid, PaintEmitter.Yellow, BikePlanner.LineWidth, 0, 0,
                        _side * o, Math.Min(AlongOf(setback), AlongOf(storage)), Math.Max(AlongOf(setback), AlongOf(storage))));
                // over the taper, where right-turning cars cross the bike lane: dashed, following its edges
                foreach (bool outer in (ReadOnlySpan<bool>)[false, true])
                {
                    var v = new List<float>();
                    foreach (double dist in _dists.Where(x => x >= storage - 1e-6 && x <= _length + 1e-6))
                    {
                        var lane = lanes.BikeLane(Open(dist))!.Value;
                        v.AddRange(Point(dist, outer ? lane.To : lane.From));
                    }
                    if (v.Count >= 6)
                        paint.Add(new RoadPaint
                        {
                            Shape = PaintShape.Polyline, Type = PaintType.YellowDashed, Rgba = PaintEmitter.Yellow, Width = BikePlanner.LineWidth,
                            Dash = BikePlanner.JunctionDash, Gap = BikePlanner.JunctionDash, Vertices = v.ToArray(),
                        });
                }
                BikeSymbol(paint, bike.Mid, setback + TurnSolid);
            }
            else
            {
                paint.Add(Line(PaintType.WhiteDashed, 3f, 3f, _side * inner, TurnSolid + setback, storage));
                paint.Add(Line(PaintType.WhiteSolid, 0, 0, _side * inner, setback, TurnSolid + setback));
            }
            // the stop line across the pocket (layout (b): the riders' own yellow one across the bike lane before it, #700); at the lights only
            if (rules.Has(JunctionRule.StopLine))
            {
                double bar = SignalStopLine * 0.5 + setback;
                paint.Add(new RoadPaint
                {
                    Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = PaintEmitter.White, Width = SignalStopLine,
                    Vertices = [.. Point(bar, pocket.From + 0.1), .. Point(bar, pocket.To - 0.1)],
                });
                if (lanes.BikeBetween)
                {
                    var bike = lanes.BikeLane()!.Value;
                    double bikeBar = setback + BikeStopLine * 0.5;
                    paint.Add(new RoadPaint
                    {
                        Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = PaintEmitter.Yellow, Width = BikeStopLine,
                        Vertices = [.. Point(bikeBar, bike.From + 0.05), .. Point(bikeBar, bike.To - 0.05)],
                    });
                }
                stats.StopBars++;
            }
            double second = storage >= 20 + PaintEmitter.ArrowLength ? 20 : 13;
            // the first arrow always: where a short storage behind a skewed line has no room for it 5 m
            // back, it moves up to 1 m from the line; the second only where the storage holds it
            double first = Math.Clamp(storage + 1 - setback, 1 + PaintEmitter.ArrowLength, 5 + PaintEmitter.ArrowLength);
            foreach (double tip in (ReadOnlySpan<double>)[first, second + PaintEmitter.ArrowLength])
            {
                double back = tip + setback;
                if (tip > first && back > storage + 1) continue;
                var (x, y, z, sx, sz) = At(back);
                double fx = sz, fz = -sx;
                double tipY = At(back - PaintEmitter.ArrowLength).Y;   // climbing with the road (#639)
                paint.Add(PaintEmitter.Arrow(x + sx * pocket.Mid, y, z + sz * pocket.Mid, fx, fz, PaintArrow.Right, tipY));
                stats.Arrows++;
                if (through is { } kind)
                {
                    double own = lanes.Through().Mid;
                    paint.Add(PaintEmitter.Arrow(x + sx * own, y, z + sz * own, fx, fz, kind, tipY));
                    stats.Arrows++;
                }
            }
        }

        /// <summary>
        /// The exit: the through lane comes out of the junction on the strip and eases back over
        /// the taper. Between the centre line and the lane's left edge, a hatched median a lane
        /// wide at the mouth, closing where the lane is back in place; the centre line is solid
        /// along it. At traffic lights the hatch starts in line with the stop line across the arm's
        /// own approach (<paramref name="near"/> from the mouth, #406), not in the junction.
        /// </summary>
        public void Median(List<RoadPaint> paint, TurnLaneStats stats, double near)
        {
            Painted = true;
            SolidCentre(paint);
            Hatch(paint, stats, near, _length, HatchAt);   // shrunk where split lead-ins moved the lanes (#700)
        }

        /// <summary>
        /// Small kerbed islands (#682) in the exit's hatched median, cut through by the crosswalk (which stays level):
        /// one in the junction past the crosswalk, <see cref="IslandInside"/> m deep, carrying the left repeater signal,
        /// and one behind it from the bars to just behind the stop line (<paramref name="near"/> from the mouth).
        /// Each lies between the centre line and the through lane's edge, a quarter metre clear of both. Returns where
        /// the pole stands (LV95 and the island's top height), null when there is no room.
        /// </summary>
        public TileId Tile => _tile;

        public (Vec2 Pole, float Y)? Islands(List<RoadAreaProp> areas, double near, double zebraFrom, double zebraTo, bool crosswalk = true)
        {
            const double Margin = 0.25, IslandMinWidth = 1.2, Top = 0.12;
            const double IslandInside = IslandInsideM;
            double Width(double d) => HatchAt(d) - 2 * Margin;
            // one width all along, before the crosswalk and after it: what the narrowest end leaves (not a triangle)
            double iw = Width(Math.Max(near + 2.0, zebraTo));
            if (iw < IslandMinWidth) return null;
            // a point at distance d from the mouth, inside the junction (d < 0) along the arm's line on
            float[] At2(double d, double offset)
            {
                if (d >= 0) return Point(d, offset);
                var a = Point(0, offset);
                var b = Point(1, offset);
                double fx = b[0] - a[0], fz = b[2] - a[2], len = Math.Sqrt(fx * fx + fz * fz);
                return len < 1e-6 ? a : [(float)(a[0] + fx / len * d), a[1], (float)(a[2] + fz / len * d)];
            }
            void Island(double a, double b)
            {
                if (b - a < 0.8) return;
                var v = new List<float>();
                var idx = new List<ushort>();
                int steps = Math.Max(1, (int)Math.Round(b - a));
                for (int k = 0; k <= steps; k++)
                {
                    double d = a + (b - a) * k / steps, w = iw;
                    v.AddRange(At2(d, Margin));
                    v.AddRange(At2(d, Margin + w));
                    if (k == 0) continue;
                    ushort p = (ushort)(2 * k - 2);
                    idx.AddRange([p, (ushort)(p + 1), (ushort)(p + 3), p, (ushort)(p + 3), (ushort)(p + 2)]);
                }
                areas.Add(new RoadAreaProp
                {
                    Type = AreaPropType.Island, Variant = 2, Flags = PropFlags.Solid, Height = (float)Top,
                    Vertices = v.ToArray(), Indices = idx.ToArray(),
                });
            }
            // no crosswalk (no sidewalk or path on the arm): one island all along
            double after = crosswalk ? zebraFrom - 0.15 : near + 2.0;
            Island(-IslandInside, after);
            if (crosswalk) Island(zebraTo + 0.15, near + 2.0);
            double at = -IslandInside * 0.5 + after * 0.1;
            var spot = At2(at, Margin + iw * 0.5);
            return (new Vec2(_tile.MinE + spot[0], _tile.MaxN - spot[2]), spot[1] + (float)Top);
        }

        /// <summary>Set once its lane markings are painted (a merged strip is painted once, by the pocket it leads to).</summary>
        public bool Painted { get; private set; }

        /// <summary>The dashed centre line along the widening turned solid: no overtaking into the junction (#120: drawn where the road has none).</summary>
        private void SolidCentre(List<RoadPaint> paint)
        {
            // the middle of the car lanes (#120: bike lanes off the edges); a road with none there
            // (a Kernfahrbahn, or too narrow for one) gets a solid line: the pocket must be kept off
            // the oncoming lane. Along the taper and storage only: over a lead-in the lane beside
            // the bike lane is not yet full width, the road's own line carries on there.
            var a = _painted.Attributes;
            double middle = ((a.Left.HasLane ? a.Left.BikeDm : 0) - (a.Right.HasLane ? a.Right.BikeDm : 0)) / 20.0;
            var centre = paint.FirstOrDefault(q => q.Segment == _painted && q.Dash > 0 && Math.Abs(q.Offset - middle) < 0.3);
            if (centre is not null) Cut(paint, centre);
            _centre = (Line(PaintType.WhiteSolid, 0, 0, centre?.Offset ?? middle, 0, _length), centre?.Offset ?? middle);
            paint.Add(_centre.Value.Paint);
        }

        /// <summary>A solid centre line between two distances from the mouth, in the middle of the car lanes, where the road has none (#711).</summary>
        public void CentreLine(List<RoadPaint> paint, double d0, double d1)
        {
            var a = _painted.Attributes;
            double middle = ((a.Left.HasLane ? a.Left.BikeDm : 0) - (a.Right.HasLane ? a.Right.BikeDm : 0)) / 20.0;
            d1 = Math.Min(d1, _total - 0.1);
            if (d1 - d0 > 1) paint.Add(Line(PaintType.WhiteSolid, 0, 0, middle, d0, d1));
        }

        /// <summary>The solid centre line along the widening and its offset, for <see cref="OpenAtCrosswalk"/>.</summary>
        private (RoadPaint Paint, double Offset)? _centre;
        /// <summary>The hatches' outlines: each one's points (distance from the mouth, offset) and whether it is a closing line.</summary>
        private readonly List<(RoadPaint Paint, List<(double D, double O)> At, bool Closing)> _outlines = new();

        private RoadPaint OutlinePaint(List<(double D, double O)> at)
        {
            var v = new List<float>(at.Count * 3);
            foreach (var (d, o) in at) v.AddRange(Point(d, o));
            return new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.WhiteSolid, Rgba = PaintEmitter.White, Width = PaintEmitter.LineWidth,
                Vertices = v.ToArray(),
            };
        }

        /// <summary>
        /// A crosswalk over the hatch between <paramref name="from"/> and <paramref name="to"/> m from the mouth (#700, the user's
        /// rule): the hatch's outline and the centre line stop at it (a band from minus infinity: nothing on the mouth side of it); with a centre island (<paramref name="island"/>) the
        /// hatch does not close at its mouth end either.
        /// </summary>
        public void OpenAtCrosswalk(List<RoadPaint> paint, double from, double to, bool island)
        {
            for (int k = _outlines.Count - 1; k >= 0; k--)
            {
                var (mesh, at, closing) = _outlines[k];
                int index = paint.IndexOf(mesh);
                if (index < 0) continue;
                paint.RemoveAt(index);
                _outlines.RemoveAt(k);
                if (closing && (island || at.All(p => p.D > from && p.D < to))) continue;
                foreach (var piece in Outside(at, from, to))
                {
                    var cut = OutlinePaint(piece);
                    paint.Add(cut);
                    _outlines.Add((cut, piece, closing));
                }
            }
            if (_centre is { } c && paint.Remove(c.Paint))
            {
                if (from > 0.3) paint.Add(Line(PaintType.WhiteSolid, 0, 0, c.Offset, 0, from));
                if (_length - to > 0.3) paint.Add(Line(PaintType.WhiteSolid, 0, 0, c.Offset, to, _length));
                _centre = null;
            }
        }

        /// <summary>The pieces of a polyline of (distance, offset) points outside the band from..to.</summary>
        private static List<List<(double D, double O)>> Outside(List<(double D, double O)> at, double from, double to)
        {
            var pieces = new List<List<(double D, double O)>>();
            List<(double D, double O)>? cur = null;
            bool In(double d) => d > from && d < to;
            for (int i = 0; i < at.Count; i++)
            {
                if (i > 0)
                {
                    var (a, b) = (at[i - 1], at[i]);
                    // the band's edges this step crosses, in the order it meets them
                    var edges = new List<double>();
                    foreach (double edge in (ReadOnlySpan<double>)[from, to])
                        if ((a.D - edge) * (b.D - edge) < 0) edges.Add(edge);
                    if (edges.Count == 2 && Math.Abs(edges[1] - a.D) < Math.Abs(edges[0] - a.D)) edges.Reverse();
                    foreach (double edge in edges)
                    {
                        double t = (edge - a.D) / (b.D - a.D);
                        (double D, double O) p = (edge, a.O + (b.O - a.O) * t);
                        if (cur is not null) { cur.Add(p); pieces.Add(cur); cur = null; }
                        else cur = [p];
                    }
                }
                if (In(at[i].D)) { if (cur is not null) { pieces.Add(cur); cur = null; } continue; }
                (cur ??= new()).Add(at[i]);
            }
            if (cur is not null) pieces.Add(cur);
            return pieces.Where(p => p.Count >= 2 && Math.Abs(p[^1].D - p[0].D) + Math.Abs(p[^1].O - p[0].O) > 0.2).ToList();
        }

        /// <summary>
        /// A hatched median between the centre line and <paramref name="border"/> (an offset that
        /// closes to 0 at one end), from <paramref name="near"/> to <paramref name="far"/> metres from
        /// the mouth: the solid border, a solid line across its wide end (#406: a Sperrfläche is
        /// enclosed all round), and stripes at 45 degrees from the centre line outward and away from
        /// the mouth, cut at the ends. Left out, the centre line alone, where it would be narrower
        /// than <see cref="HatchMinWidth"/> or shorter than <see cref="HatchMinLength"/> (#406).
        /// </summary>
        private void Hatch(List<RoadPaint> paint, TurnLaneStats stats, double near, double far, Func<double, double> border, double slant = 0,
            bool towardMouth = false)
        {
            double wideEnd = border(near) >= border(far) ? near : far;
            if (border(wideEnd) < HatchMinWidth || far - near < HatchMinLength) { stats.HatchesSkipped++; return; }
            // an angled closing line (#700, a lead-in hatch's near end): from the centre line at near, back along the road to the
            // border slant metres further out, so the pocket opens from its right side; s = metres along per metre across
            double width = border(wideEnd);
            // the line runs to the border slant m on, where the hatch may already be narrower than at its widest (on the taper)
            double s = wideEnd == near && slant > 0 ? slant / Math.Max(0.1, border(near + slant)) : 0;
            double outer = near + slant * (s > 0 ? 1 : 0);   // where the border starts
            // each outline keeps its points as (distance from the mouth, offset), for OpenAtCrosswalk (#700)
            void Solid(List<(double D, double O)> at, bool closing)
            {
                var mesh = OutlinePaint(at);
                paint.Add(mesh);
                _outlines.Add((mesh, at, closing));
            }
            var line = new List<(double D, double O)> { (outer, border(outer)) };
            foreach (double dist in _dists.Where(x => x > outer + 1e-3 && x < far - 1e-3))
                line.Add((dist, border(dist)));
            line.Add((far, border(far)));
            Solid(line, false);
            if (s > 0) Solid([(near, 0), (outer, border(outer))], true);
            else Solid([(wideEnd, 0), (wideEnd, border(wideEnd))], true);

            var v = new List<float>();
            var idx = new List<ushort>();
            double h = HatchWidth * Math.Sqrt(0.5);   // half the stripe's width, along the road
            // each stripe edge (d0 + t, t), t metres out from the centre line: where it enters the
            // median (the centre line, or the closing line at its near end) and where it meets the
            // border (border(d0 + t) = t, by bisection), or the closing line at its far end
            // a stripe runs from the centre line outward, away from the mouth (sigma +1: an exit's, along its traffic) or toward
            // it (sigma -1: a lead-in's, along the traffic driving in): either way it leans the way the hatch pushes a car (#700)
            double sigma = towardMouth ? -1 : 1;
            (double From, double To)? Edge(double d0)
            {
                double t0, tMax;
                if (!towardMouth)
                {
                    t0 = Math.Max(0, near - d0); tMax = far - d0;
                    if (s > 0)
                    {
                        // inside the angled line: d0 + t >= near + s t
                        if (s < 1) t0 = Math.Max(0, (near - d0) / (1 - s));
                        else if (s > 1) { if (d0 < near) return null; t0 = 0; tMax = Math.Min(tMax, (d0 - near) / (s - 1)); }
                    }
                }
                else
                {
                    // d0 - t within [near + s t, far]: t <= (d0 - near) / (1 + s), t >= d0 - far
                    t0 = Math.Max(0, d0 - far); tMax = (d0 - near) / (1 + s);
                }
                if (tMax <= t0 || border(d0 + sigma * t0) <= t0) return null;
                if ((s > 0 || towardMouth ? border(d0 + sigma * tMax) : border(far)) > tMax) return (t0, tMax);
                double lo = t0, hi = tMax;
                for (int k = 0; k < 40; k++)
                {
                    double mid = (lo + hi) * 0.5;
                    if (border(d0 + sigma * mid) > mid) lo = mid; else hi = mid;
                }
                return (t0, lo);
            }
            // stripes keep their phase from near + 0.6; those starting before the wide near end come in through its closing line
            // (leaning toward the mouth, those past the far end come in through the border)
            double first = near + 0.6 - Math.Ceiling(border(near) / HatchStep) * HatchStep;
            double last = towardMouth ? far + Math.Ceiling(width / HatchStep + 1) * HatchStep : far;
            var quads = new List<(double From, double To)>();
            for (double d0 = first; d0 < last; d0 += HatchStep)
            {
                if (Edge(d0 - h) is not { } a || Edge(d0 + h) is not { } c) continue;
                if (Math.Max(a.To - a.From, c.To - c.From) < 0.4) continue;   // where the border is still near the centre line
                ushort b = (ushort)(v.Count / 3);
                v.AddRange(Point(d0 - h + sigma * a.From, a.From)); v.AddRange(Point(d0 - h + sigma * a.To, a.To));
                v.AddRange(Point(d0 + h + sigma * c.To, c.To)); v.AddRange(Point(d0 + h + sigma * c.From, c.From));
                idx.AddRange([b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3)]);
                double[] ds = [d0 - h + sigma * a.From, d0 - h + sigma * a.To, d0 + h + sigma * c.To, d0 + h + sigma * c.From];
                quads.Add((ds.Min(), ds.Max()));
                stats.Stripes++;
            }
            if (idx.Count > 0)
            {
                var mesh = new RoadPaint
                {
                    Shape = PaintShape.Triangles, Type = PaintType.Hatch, Rgba = PaintEmitter.White,
                    Vertices = v.ToArray(), Indices = idx.ToArray(),
                };
                paint.Add(mesh);
                _hatches.Add((mesh, quads));
            }
        }

        /// <summary>The hatch meshes this widening painted, with each stripe's reach along the road (from the mouth), for <see cref="ClearHatch"/>.</summary>
        private readonly List<(RoadPaint Mesh, List<(double From, double To)> Quads)> _hatches = new();

        /// <summary>
        /// Leaves out the hatch stripes that reach between <paramref name="from"/> and <paramref name="to"/> m from the mouth
        /// (#700: a zebra across the hatch, and its refuge islands, keep it clear). Returns how many went.
        /// </summary>
        public int ClearHatch(List<RoadPaint> paint, double from, double to)
        {
            int cleared = 0;
            for (int k = 0; k < _hatches.Count; k++)
            {
                var (mesh, quads) = _hatches[k];
                int at = paint.IndexOf(mesh);
                if (at < 0) continue;
                var v = new List<float>();
                var idx = new List<ushort>();
                var kept = new List<(double From, double To)>();
                for (int q = 0; q < quads.Count; q++)
                {
                    if (quads[q].To > from && quads[q].From < to) { cleared++; continue; }
                    ushort b = (ushort)(v.Count / 3);
                    v.AddRange(mesh.Vertices.AsSpan(q * 12, 12).ToArray());
                    idx.AddRange([b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3)]);
                    kept.Add(quads[q]);
                }
                if (kept.Count == quads.Count) continue;
                var replaced = new RoadPaint
                {
                    Shape = PaintShape.Triangles, Type = PaintType.Hatch, Rgba = PaintEmitter.White,
                    Vertices = v.ToArray(), Indices = idx.ToArray(),
                };
                if (kept.Count == 0) paint.RemoveAt(at); else paint[at] = replaced;
                _hatches[k] = (replaced, kept);
            }
            return cleared;
        }

        /// <summary>The near refuge island starts at least this far out from the mouth.</summary>
        private const double RefugeClear = 0.5;

        /// <summary>
        /// Where across the arm the refuge for a zebra between <paramref name="zebraFrom"/> and <paramref name="zebraTo"/> stands,
        /// metres out from the arm's axis toward this side (its near and far edge), null where none fits: the zebra leaves its
        /// bars out there (#700).
        /// </summary>
        public (double Near, double Far)? RefugeSpan(double zebraFrom, double zebraTo)
        {
            double a = zebraFrom - 0.15 - RefugeLength, b = zebraTo + 0.15 + RefugeLength;
            if (a < RefugeClear - 1e-6 || b > _length - 0.5) return null;
            double iw = Math.Min(HatchAt(a), HatchAt(b)) - 0.5;
            if (iw < 1.2) return null;
            return (Frame0 + 0.25, Frame0 + 0.25 + iw);
        }

        /// <summary>
        /// How far out a zebra between <paramref name="zebraFrom"/> and <paramref name="zebraTo"/> must move for a refuge to fit
        /// on the arm (0: it fits where it is), null where none would fit anyway (the hatch too narrow or too short there).
        /// </summary>
        public double? RefugeShift(double zebraFrom, double zebraTo)
        {
            double shift = Math.Max(0, RefugeClear + 0.15 + RefugeLength - zebraFrom);
            double a = zebraFrom + shift - 0.15 - RefugeLength, b = zebraTo + shift + 0.15 + RefugeLength;
            if (b > _length - 0.5 || Math.Min(HatchAt(a), HatchAt(b)) - 0.5 < 1.2) return null;
            return shift;
        }

        /// <summary>
        /// A pedestrian refuge (#700) in the hatch where a zebra crosses it between <paramref name="zebraFrom"/> and
        /// <paramref name="zebraTo"/> m from the mouth: a kerbed island <see cref="RefugeLength"/> m long on each side of the
        /// crosswalk, which stays level through it, a quarter metre clear of the hatch's edges. False where the hatch is
        /// narrower there than an island needs.
        /// </summary>
        public bool Refuge(List<RoadAreaProp> areas, double zebraFrom, double zebraTo)
        {
            const double Margin = 0.25, MinWidth = 1.2, Top = 0.12;
            double a = zebraFrom - 0.15 - RefugeLength, b = zebraTo + 0.15 + RefugeLength;
            if (a < RefugeClear - 1e-6 || b > _length - 0.5) return false;   // both on the arm: none in the junction, where cars turn into the arm (#700, the user's review of Sion)
            double iw = Math.Min(HatchAt(Math.Max(0, a)), HatchAt(b)) - 2 * Margin;
            if (iw < MinWidth) return false;
            float[] At2(double d, double offset)
            {
                if (d >= 0) return Point(d, offset);
                var p0 = Point(0, offset);
                var p1 = Point(1, offset);
                double fx = p1[0] - p0[0], fz = p1[2] - p0[2], len = Math.Sqrt(fx * fx + fz * fz);
                return len < 1e-6 ? p0 : [(float)(p0[0] + fx / len * d), p0[1], (float)(p0[2] + fz / len * d)];
            }
            void Island(double d0, double d1)
            {
                var v = new List<float>();
                var idx = new List<ushort>();
                int steps = Math.Max(1, (int)Math.Round(d1 - d0));
                for (int k = 0; k <= steps; k++)
                {
                    double d = d0 + (d1 - d0) * k / steps;
                    v.AddRange(At2(d, Margin));
                    v.AddRange(At2(d, Margin + iw));
                    if (k == 0) continue;
                    ushort p = (ushort)(2 * k - 2);
                    idx.AddRange([p, (ushort)(p + 1), (ushort)(p + 3), p, (ushort)(p + 3), (ushort)(p + 2)]);
                }
                areas.Add(new RoadAreaProp
                {
                    Type = AreaPropType.Island, Variant = 2, Flags = PropFlags.Solid, Height = (float)Top,
                    Vertices = v.ToArray(), Indices = idx.ToArray(),
                });
            }
            Island(a, zebraFrom - 0.15);
            Island(zebraTo + 0.15, b);
            return true;
        }

        /// <summary>A refuge island's length on each side of the crosswalk (VSS 40 241: 2 m and more).</summary>
        private const double RefugeLength = 2.0;
    }
}
