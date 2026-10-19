namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// The junction planner (#818, <c>docs/plans/junction-layout.md</c>), step 1: each arm's mouth section from the data
/// (<see cref="NeedsOf"/>) on its street's normal section, the facing through lanes lined up across the junction, the
/// room measured beside each arm and the section shrunk to it. Runs beside the pocket layout behind <c>--junctions new</c>
/// until it replaces it; its plans are reported, nothing is written from them yet.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>Print the plan of the junction nearest this LV95 point (<c>--debug-junction E,N</c>), within 30 m.</summary>
    public static (double E, double N)? DebugJunction;

    /// <summary>Every single-node core failing a check (kerb outside its mouth, connector outside, outline crossing itself) described here (<c>--junction-failures FILE</c>).</summary>
    public static string? JunctionFailures;

    /// <summary>Every core written listed here, one line each (<c>--junction-list FILE</c>, for a review): LV95 centre, arms, lights, pockets, merged nodes.</summary>
    public static string? JunctionList;

    /// <summary>With <see cref="DebugJunction"/>, its plan drawn here as an SVG (<c>--junction-svg FILE</c>).</summary>
    public static string? JunctionSvg;

    /// <summary>The room beside an arm is measured this far out from its axis at most (m), every <see cref="TurnStation"/>.</summary>
    private const double RoomReach = 20, RoomStep = 0.25;

    /// <summary>The other side's share of a split lead-in, tried in turn (#700): half, a quarter, none.</summary>
    private static readonly double[] SplitShares = [0.5, 0.25];

    private static List<JunctionPlan> PlanLayouts(PriorityResult priority, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf, Dictionary<TileId, List<RoadSegment>> output,
        HashSet<TileId> block, HashSet<TileId> wanted, HashSet<int> near, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings,
        Func<int, LinkEnd, bool, RoadSide> streetSide, OsmOverlayReader? overlay, Restrictions? restrictions, Action<string>? log,
        CrossingNodes? crossings = null, IReadOnlyDictionary<int, JunctionPlan>? alone = null)
    {
        var net = result.Network;
        // a merged junction's members are all lit where it is (#818 phase 5c)
        var signalNodes = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Signal)
            .SelectMany(p => p.Junction.Members ?? (IEnumerable<int>)[p.Junction.NodeId]).ToHashSet();
        var indexes = new Dictionary<TileId, EmbankmentPlanner.LineIndex>();
        EmbankmentPlanner.LineIndex Lines(TileId t) =>
            indexes.TryGetValue(t, out var l) ? l : indexes[t] = new EmbankmentPlanner.LineIndex(output[t]);
        var scratchLanes = new LaneStats();
        var plans = new List<JunctionPlan>();
        // today's caps by node: a road joining an arm's street keeps its own (JoinHold)
        var capOf = result.Junctions.GroupBy(j => j.NodeId).ToDictionary(g => g.Key, g => g.First());

        // 1. what the data wants at every mouth of the block
        foreach (var (junction, plan) in priority.Plans)
        {
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            // the block's junctions, and those near it (#818: they may reach into it)
            bool beyond = !block.Contains(home) || !wanted.Contains(home);
            if (beyond && !near.Contains(junction.NodeId) || junction.Arms.Count < 3 || plan.Arms.Count != junction.Arms.Count) continue;
            var rules = JunctionRules.Of(plan.Kind);
            bool lights = plan.Kind == PriorityPlanner.Kind.Signal;
            var main = Enumerable.Range(0, plan.Arms.Count).Where(k => plan.Arms[k].Role == PriorityPlanner.Role.Main).ToArray();
            var oldTight = junction.TightCorners.Select(k => (k, (k + 1) % junction.Arms.Count)).ToHashSet();
            var capAnchors = CapAnchors(junction, net);
            double oldSteepest = 0;
            foreach (var p in capAnchors)
                foreach (var q in capAnchors)
                    if (p.At.DistanceTo(q.At) > 1) oldSteepest = Math.Max(oldSteepest, Math.Abs(p.Height - q.Height) / p.At.DistanceTo(q.At));
            var jp = new JunctionPlan
            {
                Node = junction.NodeId, Merged = junction.Members is null ? null : junction, Centre = junction.Centre, Lights = lights, Beyond = beyond, MainArms = main, CentreLineThrough = plan.CentreLine is not null,
                EdgeGuides = plan.Guides.Count > 0, OldOutline = junction.Boundary.ToList(), OldArea = junction.Area, OldTight = oldTight, OldSteepest = oldSteepest,
            };
            var armsHere = ArmsOf(junction, net);
            bool layout = plan.Kind is PriorityPlanner.Kind.Main or PriorityPlanner.Kind.Signal;
            for (int i = 0; i < junction.Arms.Count; i++)
            {
                var arm = plan.Arms[i];
                if (InfoOf(net.Links[arm.LinkId]) is not { } info || !PriorityPlanner.IsCarRoad(info.Class)) continue;
                if (!segmentOf.TryGetValue(arm.LinkId, out var so)) continue;
                bool atEnd = arm.End == LinkEnd.End;
                var normal = Section.Normal(ArmAttributes(so, arm.LinkId, arm.End, streetSide), so.Segment.Width, atEnd);
                var need = layout ? NeedsOf(junction, plan, i, rules, net, signalNodes, segmentOf, streetSide, overlay, restrictions, scratchLanes) : null;
                var can = CanOf(armsHere, i);
                var banned = need?.Banned ?? restrictions?.Banned(junction, i, net, scratchLanes) ?? SignalMoves.None;
                ArmWants wants;
                if (need?.Wished is { } wished)
                {
                    // OSM's turn:lanes are the truth (the user): its lanes beyond the carriageway's are the pockets, at any speed
                    var data = DataLanes.Of(wished, need.OwnLanes, can & ~banned);
                    wants = new ArmWants(lights, can, banned, data.Left, data.Own, data.Right, need.BikeBetween, LeftTurnBikeLane: false, FromData: true);
                }
                else
                {
                    // from the rules: a left pocket where it is wanted and its through lane has an exit that takes turn lanes; a
                    // right pocket where the approach is fast enough (PocketRules)
                    bool leftPocket = need is { WantLeft: true, Exit: >= 0 } && TurnLaneRoad(net.Links[plan.Arms[need.Exit].LinkId]);
                    wants = new ArmWants(lights, can, banned, leftPocket ? [SignalMoves.Left] : [], need?.Inferred,
                        need?.WantRight == true ? [SignalMoves.Right] : [], need?.BikeBetween ?? false, LeftTurnBikeLane: false);
                }
                var wantedSection = MouthPlanner.Wanted(normal, wants);
                jp.Arms.Add(new ArmPlan
                {
                    Arm = i, LinkId = arm.LinkId, AtEnd = atEnd, Heading = junction.Arms[i].OutwardHeading, Normal = normal, Wants = wants,
                    Wanted = wantedSection, Mouth = wantedSection, Length = SegmentLength(so.Segment), StreetLength = SegmentLength(so.Segment), Exit = need?.Exit ?? -1,
                    Major = info.Class <= RoadClass.Major, Axis = AxisOf(net.Links[arm.LinkId], atEnd), OldTrim = junction.Arms[i].Trim,
                    Reach = Math.Min(ReachOf(net, net.Links[arm.LinkId], atEnd, out bool inner), JoinHold(net, AxisOf(net.Links[arm.LinkId], atEnd), arm.LinkId, atEnd, capOf)),
                    Internal = inner, StreetOn = StreetOn(net, arm.LinkId, atEnd).Links,
                    InsideLights = Internal(net.Links[arm.LinkId], junction, signalNodes),
                    // round a merged core the arms lie in the order of where they leave it, not of their headings (#818 phase 5c)
                    Ring = junction.Members is null ? null : RingOf(junction, junction.Arms[i]),
                });
            }
            // beside a right pocket the through riders' lane runs between it and the through lane (layout b): without lights always;
            // at the lights only where the traffic arriving from the arm the pocket turns into cannot turn left (one-way out,
            // forbidden, no lane shows it), else at the kerb (the user, Oct 13 2026; replaces #351's coin flip). The signal plan
            // follows the layout: a kerbside lane beside a right pocket gets its bike signal there
            // a through bike lane added only where the straight-on exit carries one (a departing bike lane or path) on, or at the
            // lights, where every bike lane ends at an advanced line or in a bike box (the user)
            ArmPlan? Toward(ArmPlan a, SignalMoves turn)
            {
                int k = Enumerable.Range(0, armsHere.Count).FirstOrDefault(k => k != a.Arm && armsHere[k].Out
                    && SignalPlan.Turn(armsHere, a.Arm, k) == turn, -1);
                return jp.Arms.FirstOrDefault(x => x.Arm == k);
            }
            foreach (var a in jp.Arms)
            {
                foreach (var move in (ReadOnlySpan<SignalMoves>)[SignalMoves.Left, SignalMoves.Through, SignalMoves.Right])
                    if (Toward(a, move) is { } to) a.Toward[move] = to.Arm;
                var straight = Toward(a, SignalMoves.Through);
                bool carriesOn = straight is not null && straight.Normal.CarriesBikesAway;
                bool between = a.Wants.BikeBetween;
                if (a.Wants.RightPocket.Length > 0)
                {
                    var into = Toward(a, SignalMoves.Right);
                    bool leftThere = into is not null && into.Wanted.CarLanes(StripWay.In).Any(l => (l.Strip.Moves & SignalMoves.Left) != 0);
                    between = !lights || !leftThere;
                }
                // one rule for the three bike moves (the user): beside the car lane making the move, where the street it goes into
                // carries riders on, or at the lights (an advanced line or a bike box ends it)
                bool Carries(SignalMoves move) => lights || Toward(a, move) is { Normal.CarriesBikesAway: true };
                a.Wants = a.Wants with
                {
                    BikeBetween = between, ThroughBike = carriesOn || lights,
                    LeftTurnBikeLane = Carries(SignalMoves.Left), RightTurnBike = Carries(SignalMoves.Right),
                };
                a.Wanted = a.Mouth = MouthPlanner.Wanted(a.Normal, a.Wants);
            }
            if (jp.Arms.Count >= 3) plans.Add(jp);
        }

        var byEnd = new Dictionary<(int Link, bool AtEnd), ArmPlan>();
        foreach (var jp in plans)
            foreach (var a in jp.Arms) byEnd[(a.LinkId, a.AtEnd)] = a;
        ArmPlan? Far(ArmPlan a) => byEnd.GetValueOrDefault((a.LinkId, !a.AtEnd));
        foreach (var a in plans.SelectMany(p => p.Arms)) a.Keep();
        var planOf0 = plans.SelectMany(p => p.Arms.Select(a => (a, p))).ToDictionary(x => x.a, x => x.p);
        // the arms are planned twice: from today's trims, then from the cores' mouths: a transition has the street between its
        // mouth and the far junction's (lengths, room), mouth first
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass > 0)
                foreach (var a in plans.SelectMany(p => p.Arms))
                {
                    a.Reset();
                    a.Length = Math.Max(0, a.StreetLength - a.MouthShift - (Far(a)?.MouthShift ?? 0));
                }
            // 2. the exits each junction's pockets want (lined up as if there were room everywhere)
            foreach (var jp in plans.Where(p => p.Merged?.Ring is null)) Align(jp, a => a.Wanted);
            // 3. the pockets' lengths, the street shared with the junction at its other end
            foreach (var jp in plans) Lengths(jp, Far);
            // an exit hatch closes over TurnExit with TurnClear to spare, unless the pocket beyond merges with it (#325): else no pocket feeds it
            var mergedInto = plans.SelectMany(p => p.Arms).Where(a => a.MergedWith is not null).Select(a => a.MergedWith!).ToHashSet();
            foreach (var jp in plans)
                foreach (var b in jp.Arms)
                    if (b.Mouth.Strips.Any(x => x.Kind == StripKind.Hatch) && b.Length < TurnExit + TurnClear && !mergedInto.Contains(b)
                        && jp.Arms.FirstOrDefault(f => f.Exit == b.Arm && f.HasLeft && !f.Wants.FromData) is { } feeder)
                    {
                        NoPocket(feeder, PocketRules.Left);
                        feeder.Mouth = feeder.Base;
                        feeder.Steps.Add("7 left pocket: its exit is too short");
                        b.Mouth = MouthPlanner.WithExitHatch(b.Mouth, 0);
                    }
            // 4. the room beside every arm, as far out as its longest transition
            foreach (var jp in plans)
                foreach (var a in jp.Arms)
                {
                    var so = segmentOf[a.LinkId];
                    double reach = Math.Max(TurnExit, a.Sizes.Count > 0 ? a.Sizes.Max(z => z.Max(o => o.Taper * Lanes(z, o) + o.Storage)) : 0);
                    a.Room = MeasureRoom(so.Segment, so.Tile, output[so.Tile].IndexOf(so.Segment), a.AtEnd, a.MouthShift, Math.Min(reach, a.Length),
                        Lines(so.Tile), grids, buildings);
                }
            // 5. line the through lanes up and fit each section to its room, trying the pockets' sizes longest first (step 6: a
            // shorter storage before no pocket); then line them up again as they fitted and fit once more. An exit that cannot
            // hold its hatch takes the pocket feeding it (its through lane would jog a lane across the junction; moving the pocket
            // all to the far side would jog the oncoming traffic as much: a share stays at most half), and all again
            foreach (var jp in plans)
            {
                // (a roundabout's entries are not lined up across it: each meets the ring)
                bool ring = jp.Merged?.Ring is not null;
                for (int round = 0; round < 6; round++)
                {
                    if (!ring) Align(jp, a => a.Base);
                    FitAll(jp, search: true);
                    if (!ring) Align(jp, a => MouthPlanner.Moved(MouthPlanner.WithExitHatch(a.Mouth, 0), a.Split));
                    FitAll(jp, search: false);
                    bool dropped = false;
                    foreach (var b in jp.Arms)
                        if ((b.FitSteps.Contains("does not fit") || b.FitSteps.Contains(MouthPlanner.FeederStep))
                            && jp.Arms.FirstOrDefault(f => f.Exit == b.Arm && !f.Wants.FromData && f.Base.Strips.Any(x => PocketRules.Left.Owns(x.Id))) is { } feeder)
                        {
                            NoPocket(feeder, PocketRules.Left);
                            feeder.Steps.Add("7 left pocket: no room for its exit");
                            dropped = true;
                        }
                    // a fit that gave up an arm's own left pocket: the arm planned again without it (its lanes were narrowed and
                    // moved for the pocket)
                    foreach (var a in jp.Arms)
                        foreach (var rule in PocketRules.All)
                            if (a.FitSteps.Contains(rule.DropStep) && a.Base.Strips.Any(x => rule.Owns(x.Id)))
                            {
                                NoPocket(a, rule);
                                a.Steps.Add(rule.DropStep + ": no room");
                                dropped = true;
                            }
                    if (!dropped) break;
                }
                if (jp.Lights) BikeStops(jp);
                // 6. the core between the mouths (step 2): connectors, kerb corners, the outline
                jp.Core = jp.Merged?.Ring is { } circle ? RoundaboutCore.Plan(jp, circle) : MergedOrWhole(jp, alone, buildings.Contains);
            // its surface on the arms' heights at the mouths
            CorePlanner.Lift(jp.Core, m => net.Links[jp.Arms.First(a => a.Arm == m.Arm).LinkId].Tag is Source source
                ? source.SampleHeight((m.Left + m.Right) * 0.5) : 0);
            }
            // where each mouth stands against today's trim; the second pass keeps the first's (it planned the transitions from
            // it) and records how far the final core moved it
            foreach (var jp in plans)
                foreach (var m in jp.Core!.Mouths)
                    if (jp.Arms.FirstOrDefault(a => a.Arm == m.Arm) is { } a)
                    {
                        if (pass == 0) a.MouthShift = m.Station - a.OldTrim;
                        else a.MouthDrift = m.Station - a.OldTrim - a.MouthShift;
                    }
            // a street between two junctions held each mouth at half of it: where both need less than all of it, each may take what
            // the other leaves (#854: one side's big turns beside the other's small T), the planning pass after laid with it
            if (pass == 0)
                foreach (var a in plans.SelectMany(p => p.Arms))
                    if (a.Reach < 1e9 && Far(a) is { } f && f.Reach < 1e9 && planOf0.TryGetValue(a, out var pa) && planOf0.TryGetValue(f, out var pf)
                        && pa.Core!.Needs.TryGetValue(a.Arm, out double na) && pf.Core!.Needs.TryGetValue(f.Arm, out double nf))
                    {
                        double street = a.Reach * 2;
                        if (na + nf + StreetSpare <= street) a.Reach = Math.Max(a.Reach, street - nf - StreetSpare * 0.5);
                    }
        }
        // 7. each arm's transition from its mouth out (step 4), one for both ends where the street between is too short for two;
        // the street runs on past nodes where only two car roads meet
        foreach (var a in plans.SelectMany(p => p.Arms))
        {
            double on = 0;
            foreach (var (link, _) in a.StreetOn)
                on += net.Links[link].Alignment is { IsEmpty: false } al ? al.Length : PolylineLength(net.Links[link].Centreline);
            a.FarArm = a.StreetOn.Count > 0 ? byEnd.GetValueOrDefault((a.StreetOn[^1].Link, !a.StreetOn[^1].AtEnd)) : Far(a);
            a.TransitionRoom = Math.Max(0, a.Length + on - (a.StreetOn.Count > 0 ? a.FarArm?.MouthShift ?? 0 : 0));
        }
        TransitionPlanner.PlanAll(plans);
        var planOf = plans.SelectMany(p => p.Arms.Select(a => (a, p))).ToDictionary(x => x.a, x => x.p);
        // 8. each arm's exterior (phase 4): its stack of bars from the mouth out
        var signalJunctions = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Signal).ToDictionary(p => p.Junction.NodeId, p => p.Junction);
        var priorityOf = priority.Plans.ToDictionary(p => p.Junction.NodeId);
        foreach (var jp in plans)
        {
            if (jp.Core is null || !priorityOf.TryGetValue(jp.Node, out var pp)) continue;
            var (junction, plan) = pp;
            bool mapped = jp.Lights && CrossingsMapped(junction, net, signalNodes, signalJunctions, crossings);
            jp.Exterior.Clear();
            foreach (var a in jp.Arms)
            {
                double mouth = jp.Core.Mouths.FirstOrDefault(m => m.Arm == a.Arm)?.Station ?? a.OldTrim;
                // OSM's crossing node is the crosswalk's middle; one within a few metres of the mouth goes right outside it
                double? osm = crossings?.OnArm(junction, a.Arm, net) is { } d ? d + junction.Arms[a.Arm].Trim - mouth - ExteriorPlanner.CrosswalkDepth * 0.5 : null;
                if (osm is < ExteriorPlanner.CrosswalkFromMouth + 4) osm = ExteriorPlanner.CrosswalkFromMouth;
                bool sidewalk = a.Normal.Strips.Any(x => x.Kind == StripKind.Sidewalk);
                // none on a street inside the lights (#348): its ends are the junction's own
                double? at = a.InsideLights ? null : osm ?? (jp.Lights && !mapped && sidewalk && JunctionRules.Lights.Has(JunctionRule.CrosswalkOnSidewalkArms) ? ExteriorPlanner.CrosswalkFromMouth : null);
                var u = Vec2.FromHeading(a.Heading);
                var signs = plan.Signs.Where(x => x.LinkId == a.LinkId).Select(x => (x.Type, x.Variant, FacesArriving: x.Facing.Dot(u) > 0)).ToList();
                jp.Exterior.Add(ExteriorPlanner.Plan(jp, a, at, yields: jp.Merged?.Ring is null && plan.Teeth.Any(t => t.LinkId == a.LinkId), signs));
            }
        }
        // 9. each transition's paint, its solid stretches and arrows counted from where its arms' traffic stops (the exterior)
        double StopOf(ArmPlan? a) => a is null ? 0 : planOf[a].Exterior.FirstOrDefault(x => x.Arm == a)?.Bars
            .Where(b => b.Kind is BarKind.StopLine or BarKind.PocketBar or BarKind.GiveWay).Select(b => b.To).DefaultIfEmpty(0).Max() ?? 0;
        foreach (var t in plans.SelectMany(p => p.Arms).Select(a => a.Transition).OfType<Transition>().Distinct())
            t.Markings = TransitionPaint.Of(t, StreetPaint.Of(segmentOf[t.Arm.LinkId].Segment), planOf[t.Arm].Lights,
                t.Far is { } f && planOf[f].Lights, StopOf(t.Arm), StopOf(t.Far));
        if (JunctionFailures is { } failures)
            File.AppendAllText(failures, string.Concat(plans.Where(jp => !jp.Beyond && jp.Core is { } c && !jp.CoresOverlap
                && (c.KerbsNotTangent > 0 || c.ConnectorsOutside > 0 || !c.Simple)).Select(jp => jp.Describe() + "\n")));
        if (DebugJunction is { } dbg)
            foreach (var jp in plans.Where(jp => !jp.Beyond && Math.Abs(dbg.E - jp.Centre.X) < 30 && Math.Abs(dbg.N - jp.Centre.Y) < 30))
            {
                (log ?? Console.WriteLine)(jp.Describe());
                if (JunctionSvg is { } svg)
                    File.WriteAllText(svg, PlanSvg.Draw(jp, FormattableString.Invariant($"junction plan at LV95 {jp.Centre.X:F0},{jp.Centre.Y:F0}{(jp.Lights ? ", lights" : "")} (#818)")));
            }
        return plans;
    }

    /// <summary>
    /// A core as <see cref="CorePlanner"/> lays it; a merged junction's laid as one node fails its checks (elongated groups,
    /// parallel arms): member by member (<see cref="MergedCore"/>, #854) from its members' plans alone (<paramref name="alone"/>,
    /// by node) where that passes them. Where both pass, one node's is kept: its through lanes run straight from mouth to mouth.
    /// </summary>
    private static JunctionCore MergedOrWhole(JunctionPlan jp, IReadOnlyDictionary<int, JunctionPlan>? alone, Func<Vec2, bool> blocked)
    {
        var whole = CorePlanner.Plan(jp, blocked);
        if (alone is null || jp.Merged is null || MergedCore.Plan(jp, alone, blocked) is not { } byMembers) return whole;
        static bool Fails(JunctionCore c) => !c.Simple || c.KerbsNotTangent > 0 || c.ConnectorsOutside > 0;
        if (Environment.GetEnvironmentVariable("MERGEDDBG") is not null)
            Console.WriteLine(FormattableString.Invariant($"[merged] {jp.Centre.X:F0},{jp.Centre.Y:F0} {jp.Merged.Members?.Count} members: by members simple {byMembers.Simple} kerbs {byMembers.KerbsNotTangent} outside {byMembers.ConnectorsOutside} (most {byMembers.Connectors.Select(c => c.Outside).DefaultIfEmpty(0).Max():F2} m) area {byMembers.Area:F0}; whole simple {whole.Simple} kerbs {whole.KerbsNotTangent} outside {whole.ConnectorsOutside} area {whole.Area:F0}"));
        if (Environment.GetEnvironmentVariable("MERGEDFORCE") is not null) return byMembers;
        return Fails(whole) && !Fails(byMembers) ? byMembers : whole;
    }

    /// <summary>
    /// How far out an arm's mouth may go before a road that is no car road joins its street (#854, the user, Oct 19: a slip lane,
    /// TLM's Verbindung, is its own roadway outside the core, leaving and joining the arm past its mouth; a footpath crossing too):
    /// <see cref="JoinMargin"/> (or today's cap there, whichever cuts the street further back) and half a metre short of the first such node along the street (on past nodes where only two
    /// car roads meet), unbounded where none comes before a junction of car roads or that leaves under <see cref="LeastHold"/>.
    /// </summary>
    private static double JoinHold(RoadNetwork net, ArmAxis axis, int linkId, bool atEnd, IReadOnlyDictionary<int, Junction> capOf)
    {
        var (links, _) = StreetOn(net, linkId, atEnd);
        foreach (var (link, end) in links.Prepend((linkId, atEnd)))
        {
            var l = net.Links[link];
            int node = end ? l.StartNode : l.EndNode;
            if (node < 0 || node >= net.Nodes.Count) break;
            var n = net.Nodes[node];
            if (n.Approaches.Count(a => InfoOf(net.Links[a.LinkId]) is { } i && PriorityPlanner.IsCarRoad(i.Class)) >= 3) break;
            // (nearer, it is the junction's own: a link between a divided road's carriageways at the node, no core held short of it)
            if (n.Degree <= 2) continue;
            // short of today's cap there too, which the street's lanes stop at (its arm's trim on this link)
            double trim = capOf.TryGetValue(node, out var cap) ? cap.Arms.Where(x => x.LinkId == link).Select(x => x.Trim).DefaultIfEmpty(0).Max() : 0;
            double hold = axis.StationPast(n.Position) - Math.Max(JoinMargin, trim + 0.5) - 0.5;
            return hold >= LeastHold ? hold : double.MaxValue;
        }
        return double.MaxValue;
    }

    /// <summary>The least a mouth is held at short of a road joining its street (m, <see cref="JoinHold"/>).</summary>
    private const double LeastHold = 4;

    /// <summary>Two mouths sharing a street keep at least this much of it between them (m).</summary>
    private const double StreetSpare = 1;

    /// <summary>A street shorter than this between two junction nodes makes them one core (the plan: phase 5's multi-node cores).</summary>
    private const double InternalLink = 30;

    /// <summary>
    /// How far out an arm's mouth may go (#818): half its street where a junction stands at the far end, else unbounded. A
    /// street under <see cref="InternalLink"/> to another junction is <paramref name="inner"/> (unbounded until phase 5 joins the two).
    /// </summary>
    private static double ReachOf(RoadNetwork net, RoadLink link, bool atEnd, out bool inner)
    {
        double length = link.Alignment is { IsEmpty: false } al ? al.Length : PolylineLength(link.Centreline);
        int far = atEnd ? link.StartNode : link.EndNode;
        // a junction of car roads (paths and tracks joining a street make a node, not a junction core)
        bool junction = far >= 0 && far < net.Nodes.Count
            && net.Nodes[far].Approaches.Count(a => InfoOf(net.Links[a.LinkId]) is { } info && PriorityPlanner.IsCarRoad(info.Class)) >= 3;
        inner = junction && length < InternalLink;
        // half the street where a junction stands at its other end (a link under InternalLink too: its two cores stay apart where
        // both fit, the user, Oct 15); on past a node that is no junction of car roads (an end may only be the block's edge)
        return junction ? length * 0.5 : double.MaxValue;
    }

    /// <summary>A transition runs on along its street at most this far past its own link (m).</summary>
    private const double StreetOnMost = 150;

    /// <summary>
    /// The halo's links the junction planner reads (#818: a junction is laid out alike from every block it reaches, each writing its
    /// share): the arms and the street on (<see cref="StreetOn"/>) of every junction of the block, of every junction outside it
    /// one of whose arms or streets on lies in it (what it writes goes on those), and of the junction one street on from either
    /// (a transition may be shared with it); and the lines beside them in their tiles (the room beside an arm). Built as their
    /// own block builds them, never written. <c>Near</c>: those junctions homed outside the block.
    /// </summary>
    private static (HashSet<int> Links, HashSet<int> Near) Band(PriorityResult priority, RoadGenResult result, HashSet<TileId> block, HashSet<TileId> wanted)
    {
        var net = result.Network;
        bool InBlock(int link) => net.Links[link].Tag is Source s && block.Contains(s.Tile);
        var planOf = new Dictionary<int, (Junction Junction, PriorityPlanner.Plan Plan)>();
        foreach (var p in priority.Plans) planOf[p.Junction.NodeId] = p;
        var near = new HashSet<int>();
        var planned = new HashSet<int>();
        foreach (var p in priority.Plans)
        {
            var home = Home(p.Junction);
            if (block.Contains(home) && wanted.Contains(home)) planned.Add(p.Junction.NodeId);
            else if (p.Plan.Arms.Any(arm => InBlock(arm.LinkId) || StreetOn(net, arm.LinkId, arm.End == LinkEnd.End).Links.Any(x => InBlock(x.Link))))
            {
                near.Add(p.Junction.NodeId);
                planned.Add(p.Junction.NodeId);
            }
        }
        var links = new HashSet<int>();
        void Arms(PriorityPlanner.Plan plan, bool onward)
        {
            foreach (var arm in plan.Arms)
            {
                links.Add(arm.LinkId);
                var on = StreetOn(net, arm.LinkId, arm.End == LinkEnd.End).Links;
                foreach (var (l, _) in on) links.Add(l);
                if (!onward) continue;
                // the junction where the street on ends: a transition may be shared with it
                var (last, atEnd) = on.Count > 0 ? on[^1] : (arm.LinkId, arm.End == LinkEnd.End);
                int node = atEnd ? net.Links[last].StartNode : net.Links[last].EndNode;
                if (planOf.TryGetValue(node, out var far) && planned.Add(node))
                {
                    near.Add(node);
                    Arms(far.Plan, false);
                }
            }
        }
        foreach (int node in planned.ToList()) Arms(planOf[node].Plan, true);

        // the lines beside them in their own tiles: the room beside an arm is measured against those
        var byTile = new Dictionary<TileId, List<int>>();
        for (int l = 0; l < net.Links.Count; l++)
            if (net.Links[l].Tag is Source s && !block.Contains(s.Tile) && net.Links[l].Centreline.Count > 0)
                (byTile.TryGetValue(s.Tile, out var list) ? list : byTile[s.Tile] = []).Add(l);
        static (double X0, double Y0, double X1, double Y1) Box(List<Vec2> line, double grow) =>
            (line.Min(p => p.X) - grow, line.Min(p => p.Y) - grow, line.Max(p => p.X) + grow, line.Max(p => p.Y) + grow);
        var boxes = new Dictionary<int, (double X0, double Y0, double X1, double Y1)>();
        foreach (var list in byTile.Values)
            foreach (int o in list) boxes[o] = Box(net.Links[o].Centreline, 0);
        foreach (int l in links.ToList())
        {
            if (net.Links[l].Tag is not Source s || block.Contains(s.Tile) || !byTile.TryGetValue(s.Tile, out var others)) continue;
            var box = Box(net.Links[l].Centreline, RoomReach + 5);
            foreach (int o in others)
            {
                var b = boxes[o];
                if (b.X1 >= box.X0 && b.X0 <= box.X1 && b.Y1 >= box.Y0 && b.Y0 <= box.Y1) links.Add(o);
            }
        }
        links.RemoveWhere(l => net.Links[l].Tag is not Source s || block.Contains(s.Tile));
        return (links, near);
    }

    /// <summary>
    /// The links a street runs on past an arm's own link, junction side first (#818: a transition may run on there): on through
    /// every node where only two car roads meet (TLM splits a street wherever an attribute changes, or a path joins), up to
    /// <see cref="StreetOnMost"/> m, each with whether the junction side is its last point; and their length.
    /// </summary>
    private static (List<(int Link, bool AtEnd)> Links, double Length) StreetOn(RoadNetwork net, int linkId, bool atEnd)
    {
        var links = new List<(int, bool)>();
        double length = 0;
        var seen = new HashSet<int> { linkId };
        int link = linkId;
        bool end = atEnd;
        while (length < StreetOnMost)
        {
            var l = net.Links[link];
            int node = end ? l.StartNode : l.EndNode;
            if (node < 0 || node >= net.Nodes.Count) break;
            var cars = net.Nodes[node].Approaches.Where(a => InfoOf(net.Links[a.LinkId]) is { } info && PriorityPlanner.IsCarRoad(info.Class)).ToList();
            if (cars.Count != 2) break;
            var here = end ? LinkEnd.Start : LinkEnd.End;   // where this link meets the node
            var next = cars.FirstOrDefault(a => a.LinkId != link || a.End != here);
            if (next.LinkId == link || !seen.Add(next.LinkId)) break;
            // the next link leaves this node: its junction side is the end at this node
            link = next.LinkId;
            end = next.End == LinkEnd.End;
            links.Add((link, end));
            var nl = net.Links[link];
            length += nl.Alignment is { IsEmpty: false } al ? al.Length : PolylineLength(nl.Centreline);
        }
        return (links, length);
    }

    private static double PolylineLength(List<Vec2> line)
    {
        double sum = 0;
        for (int k = 1; k < line.Count; k++) sum += line[k].DistanceTo(line[k - 1]);
        return sum;
    }

    /// <summary>An arm's axis from its node outward (#818): its link's alignment (its drawing where it has none), a point a metre.</summary>
    private static ArmAxis AxisOf(RoadLink link, bool atEnd)
    {
        var points = new List<Vec2>();
        if (link.Alignment is { IsEmpty: false } al)
        {
            double length = al.Length;
            for (double s = 0; ; s += 1)
            {
                double at = Math.Min(s, length);
                points.Add(al.PointAt(atEnd ? length - at : at));
                if (at >= length || s >= 150) break;
            }
        }
        else
        {
            points.AddRange(link.Centreline);
            if (atEnd) points.Reverse();
        }
        return new ArmAxis(points);
    }

    /// <summary>
    /// Where every arriving bike lane's riders stop at the lights (the user's rule, the same for the three moves): at an
    /// advanced line where the street their move goes into carries their way on (a departing bike lane or path), else in a
    /// bike box. A lane for several moves (kerbside, straight on and right) goes by its straight-on move.
    /// </summary>
    private static void BikeStops(JunctionPlan jp)
    {
        foreach (var a in jp.Arms)
        {
            var strips = a.Mouth.Strips.ToList();
            bool changed = false;
            for (int k = 0; k < strips.Count; k++)
            {
                var s = strips[k];
                if (s.Kind != StripKind.BikeLane || s.Way != StripWay.In || s.Moves == SignalMoves.None) continue;
                var move = (s.Moves & SignalMoves.Through) != 0 ? SignalMoves.Through : (s.Moves & SignalMoves.Left) != 0 ? SignalMoves.Left : SignalMoves.Right;
                bool carries = a.Toward.TryGetValue(move, out int to) && jp.Arms.FirstOrDefault(x => x.Arm == to) is { } dest && dest.Normal.CarriesBikesAway;
                // a box only where the bike lane lies beside a car lane making its riders' move, the lane the box covers (the user,
                // Oct 17: J1 south's kerb lane lies beside the right-turn lane, its riders going straight on)
                bool Serves(int j) => j >= 0 && j < strips.Count && strips[j].IsCarLane && strips[j].Way == StripWay.In
                    && (strips[j].Moves == SignalMoves.None || (strips[j].Moves & move) != 0);
                bool beside = Serves(k - 1) || Serves(k + 1);
                strips[k] = s with { Stop = !carries && beside ? BikeStop.Box : BikeStop.Advanced };
                changed = true;
            }
            if (changed) a.Mouth = new Section(strips, a.Mouth.CarriagewayLeft) { KerbLeftCm = a.Mouth.KerbLeftCm, KerbRightCm = a.Mouth.KerbRightCm };
        }
    }

    /// <summary>
    /// Fits every arm's aligned section to its room (<see cref="MouthPlanner.Fit"/>): with <paramref name="search"/> the pockets'
    /// sizes from the longest still allowed, the first that keeps every pocket; else the size settled on.
    /// </summary>
    private static void FitAll(JunctionPlan jp, bool search)
    {
        foreach (var a in jp.Arms)
        {
            var aligned = a.Mouth;
            var tries = a.Sizes.Count > 0 ? a.Sizes : [new List<Opening>()];
            int from = search ? 0 : Math.Min(a.SizeTried, tries.Count - 1);
            for (int t = from; t < tries.Count; t++)
            {
                a.Openings.Clear();
                a.Openings.AddRange(tries[t]);
                var steps = new List<string>();
                // OSM's pockets are kept, and the hatch of an exit one of them feeds (the user: the data is the truth)
                bool fedByData = jp.Arms.Any(f => f.Exit == a.Arm && f.Wants.FromData && f.HasLeft);
                a.Mouth = MouthPlanner.Fit(aligned, a.Normal, a.Room, LeftStretch(a), RightStretch(a), steps, jp.Lights,
                    keepPockets: a.Wants.FromData, keepHatch: fedByData);
                bool kept = !PocketRules.All.Any(r => steps.Contains(r.DropStep));
                if (!kept && t + 1 < tries.Count) continue;
                a.SizeTried = t;
                if (t > 0) steps.Insert(0, FormattableString.Invariant($"6 pockets shorter: {string.Join(", ", tries[t].Select(o => $"{o.Taper:F0}+{o.Storage:F0}"))}"));
                // the second fit starts from the first's lanes: its steps add to the first's
                if (search) a.FitSteps.Clear();
                a.FitSteps.RemoveAll(x => x.StartsWith("6 ", StringComparison.Ordinal));
                foreach (var step in steps)
                    if (!a.FitSteps.Contains(step)) a.FitSteps.Add(step);
                // what the fit dropped no longer opens
                a.Openings.RemoveAll(o => !a.Mouth.Strips.Any(x => x.Id == o.Id));
                break;
            }
        }
    }

    /// <summary>
    /// The street's attributes at the arm's end as built: the sides the street planner cut there (#119, #120), with the
    /// painted bike lane of the paint copy (a street that got paths has none, #351).
    /// </summary>
    private static RoadAttributes ArmAttributes((RoadSegment Segment, TileId Tile, RoadSegment Painted) so, int linkId, LinkEnd end,
        Func<int, LinkEnd, bool, RoadSide> streetSide)
    {
        RoadSide Side(bool right)
        {
            var built = streetSide(linkId, end, right);
            var painted = right ? so.Painted.Attributes.Right : so.Painted.Attributes.Left;
            return !built.HasTrack && painted.HasLane ? built with { Bike = BikeKind.Lane, BikeDm = painted.BikeDm } : built;
        }
        return so.Segment.Attributes with { Left = Side(false), Right = Side(true) };
    }

    /// <summary>
    /// The pockets' lengths (<see cref="PocketRule.Sizes"/>, longest first): every combination whose taper (per lane) and
    /// storage fit the street with <see cref="TurnClear"/> to spare, the kerb side's pocket reaching at most
    /// <see cref="RightEarlier"/> past the centre side's storage (#682); the fit tries them in turn (step 6). Where the junction
    /// at the street's other end (<paramref name="far"/>) lays an exit hatch on the same side, a centre-side pocket leaves it
    /// <see cref="TurnExit"/> + <see cref="TurnRejoin"/>; where then no size fits, the two merge (#325): one transition from that
    /// exit to this pocket, the pocket opening out of the hatch over <see cref="TurnEntry"/>. A pocket no size fits is dropped
    /// ("street too short"), unless OSM's lanes ask for it: then its shortest size, however long the street.
    /// </summary>
    private static void Lengths(JunctionPlan jp, Func<ArmPlan, ArmPlan?> far)
    {
        foreach (var a in jp.Arms)
        {
            var other = far(a);
            bool exitBeyond = other is not null && other.Mouth.Strips.Any(x => x.Kind == StripKind.Hatch);
            double room = a.Length - TurnClear;
            // the centre side's sizes first (its storage bounds the kerb side's), then the kerb side's beside each
            var inner = PocketRules.Left;
            var outer = PocketRules.Right;
            int innerLanes = a.Mouth.Strips.Count(x => inner.Owns(x.Id)), outerLanes = a.Mouth.Strips.Count(x => outer.Owns(x.Id));
            var inners = new List<(double Taper, double Storage)?>();
            if (innerLanes > 0)
            {
                double innerRoom = room - (exitBeyond ? TurnExit + TurnRejoin : 0);
                inners.AddRange(inner.Sizes.Where(z => z.Taper * innerLanes + z.Storage <= innerRoom).Select(z => ((double, double)?)z));
                double merged = Math.Min(inner.Sizes[0].Storage, a.Length - TurnEntry - TurnMinHatch);
                if (inners.Count == 0 && exitBeyond && merged >= inner.Sizes[^1].Storage)
                {
                    // one strip mouth to mouth (2+1 kept): no taper, the pocket opens out of the exit's hatch
                    inners.Add((0, merged));
                    a.MergedWith = other;
                    a.Steps.Add(FormattableString.Invariant($"merged with the exit beyond (#325), storage {merged:F0} m"));
                }
                if (inners.Count == 0 && a.Wants.FromData)
                {
                    inners.Add(inner.Sizes[^1]);
                    a.Steps.Add(inner.DropStep + ": street too short, kept (OSM lanes)");
                }
                if (inners.Count == 0) { a.Steps.Add(inner.DropStep + ": street too short"); NoPocket(a, inner); innerLanes = 0; }
            }
            if (inners.Count == 0) inners.Add(null);
            bool anyOuter = false;
            foreach (var l in inners)
            {
                var set = new List<Opening>();
                if (l is { } ls) set.AddRange(a.Mouth.Strips.Where(x => inner.Owns(x.Id)).Select(lane => new Opening(lane.Id, ls.Taper, ls.Storage)));
                (double Taper, double Storage)? r = null;
                // never beside a merged strip (#348)
                if (outerLanes > 0 && a.MergedWith is null)
                {
                    foreach (var z in outer.Sizes)
                        if (z.Taper * outerLanes + z.Storage <= room && (l is null || z.Taper * outerLanes + z.Storage <= l.Value.Storage + RightEarlier)) { r = z; break; }
                    if (r is null && a.Wants.FromData) r = outer.Sizes[^1];
                }
                if (r is { } rs)
                {
                    set.AddRange(a.Mouth.Strips.Where(x => outer.Owns(x.Id)).Select(lane => new Opening(lane.Id, rs.Taper, rs.Storage)));
                    anyOuter = true;
                }
                if (set.Count > 0) a.Sizes.Add(set);
            }
            if (outerLanes > 0 && !anyOuter) { a.Steps.Add(outer.DropStep + ": street too short"); NoPocket(a, outer); }
            a.Base = MouthPlanner.Wanted(a.Normal, a.Wants);
            a.Openings.Clear();
            if (a.Sizes.Count > 0) a.Openings.AddRange(a.Sizes[0]);
        }
    }

    /// <summary>
    /// The arm without the pocket of <paramref name="rule"/>: planned again from its data without it (the street's own lanes,
    /// as wide as they are), its sizes without it.
    /// </summary>
    private static void NoPocket(ArmPlan a, PocketRule rule)
    {
        a.Wants = a.Wants.Without(rule);
        a.Base = MouthPlanner.Wanted(a.Normal, a.Wants);
        for (int k = 0; k < a.Sizes.Count; k++) a.Sizes[k] = a.Sizes[k].Where(o => !rule.Owns(o.Id)).ToList();
        a.Sizes.RemoveAll(z => z.Count == 0);
        // sizes that differed only by the dropped pocket are now the same: keep one of each
        for (int k = a.Sizes.Count - 1; k > 0; k--)
            if (a.Sizes.Take(k).Any(z => z.SequenceEqual(a.Sizes[k]))) a.Sizes.RemoveAt(k);
        a.Openings.RemoveAll(o => rule.Owns(o.Id));
        a.SizeTried = 0;
    }

    /// <summary>The right side's transition: the pockets' storage held, then their longest taper (an exit alone: <see cref="TurnExit"/>).</summary>
    private static Stretch RightStretch(ArmPlan a) =>
        a.Openings.Count > 0 ? new Stretch(a.Openings.Max(o => o.Storage), a.Openings.Max(o => o.Taper * Lanes(a, o)))
        : a.Mouth.Strips.Any(x => x.Id == MouthPlanner.AddedBikeId) ? AddedBikeStretch : new Stretch(0, TurnExit);

    /// <summary>
    /// A through bike lane the mouth adds (#818) runs as far back as the lights' solid centre line (<see cref="CentreSolidAtLights"/>)
    /// and comes in over 1:6 before it.
    /// </summary>
    internal static readonly Stretch AddedBikeStretch = new(CentreSolidAtLights, MouthPlanner.LeftBikeLane * LeadInPerMetre);

    /// <summary>The left side's: a split lead-in's as the pocket's, an exit hatch closing over <see cref="TurnExit"/>.</summary>
    private static Stretch LeftStretch(ArmPlan a)
    {
        var pocket = RightStretch(a);
        return a.Split > 0 && a.Openings.Count > 0 ? new Stretch(pocket.Hold, Math.Max(pocket.Ease, TurnExit)) : new Stretch(0, TurnExit);
    }

    private static int Lanes(ArmPlan a, Opening o) => Lanes(a.Openings, o);
    private static int Lanes(IReadOnlyList<Opening> openings, Opening o) =>
        PocketRules.Of(o.Id) is { } rule ? openings.Count(x => rule.Owns(x.Id)) : 1;

    /// <summary>What the centre side's pockets (and the left-turn bike lane beside them) add: the through lane moves out by it.</summary>
    private static double PocketWidth(Section s) =>
        s.Strips.Where(x => PocketRules.Of(x.Id) is { CentreSide: true } || x.Id == MouthPlanner.LeftBikeId).Sum(x => x.Width);

    /// <summary>
    /// The facing through lanes on one line (#818, replacing #700's veer share and #682's exit hatch): an approach with a
    /// left pocket moves its section left by a share of the pocket where the room on its left allows (half, a quarter,
    /// none: the approach side always takes at least half); the exit it feeds gets a hatch between its centre line and its
    /// departing lanes as wide as the through lane stands further right than that exit's lane (at the lights at least
    /// <see cref="IslandKeepHatch"/> where an island will stand, the shares giving way alike). Starts from the sections
    /// <paramref name="from"/> gives, unmoved and without a hatch: the planned ones, or the fitted ones to line them up as they fitted.
    /// </summary>
    private static void Align(JunctionPlan jp, Func<ArmPlan, Section> from)
    {
        static int ThroughLane(Section s) =>
            s.Strips.Select((x, k) => (x, k)).Where(p => p.x.IsCarLane && p.x.Way == StripWay.In && PocketRules.Of(p.x.Id) is null)
                .Select(p => p.k).DefaultIfEmpty(-1).First();
        static int InnerOut(Section s) =>
            s.Strips.Select((x, k) => (x, k)).Where(p => p.x.IsCarLane && p.x.Way == StripWay.Out).Select(p => p.k).DefaultIfEmpty(-1).Last();

        // each section where the street's axis has it, without a hatch
        var bare = new Dictionary<ArmPlan, Section>();
        foreach (var a in jp.Arms) bare[a] = from(a);
        // the shares: what the room on the approach's left takes
        foreach (var a in jp.Arms)
        {
            a.Split = 0;
            double pocket = PocketWidth(bare[a]);
            if (pocket <= 0) continue;
            foreach (double share in SplitShares)
            {
                a.Split = share * pocket;
                var (roomL, _) = MouthPlanner.MouthRoom(a.Normal, a.Room, LeftStretch(a), RightStretch(a));
                if (MouthPlanner.Reach(MouthPlanner.Moved(bare[a], -a.Split)).Left <= roomL + 1e-6) break;
                a.Split = 0;
            }
        }
        // the hatches, and at the lights the island's width they keep
        var hatch = new Dictionary<ArmPlan, double>();
        for (int pass = 0; pass < 2; pass++)
        {
            hatch.Clear();
            foreach (var b in jp.Arms)
            {
                var feeder = jp.Arms.FirstOrDefault(a => a.Exit == b.Arm && ThroughLane(bare[a]) >= 0 && PocketWidth(bare[a]) > 0);
                if (feeder is null) continue;
                var aSec = MouthPlanner.Moved(bare[feeder], -feeder.Split);
                var (f0, f1) = aSec.Span(ThroughLane(aSec));
                var bSec = MouthPlanner.Moved(bare[b], -b.Split);
                int o = InnerOut(bSec);
                if (o < 0) continue;
                var (o0, o1) = bSec.Span(o);
                double h = (o0 + o1) * 0.5 + (f0 + f1) * 0.5;   // the exit's lane stands at minus the through lane's offset, mirrored
                if (jp.Lights && pass == 0 && h < IslandKeepHatch && feeder.Split + b.Split > 0)
                {
                    double f = Math.Max(0, 1 - (IslandKeepHatch - h) / (feeder.Split + b.Split));
                    feeder.Split *= f;
                    b.Split *= f;
                }
                hatch[b] = h;
            }
        }
        foreach (var a in jp.Arms)
        {
            double h = hatch.GetValueOrDefault(a);
            a.Mouth = MouthPlanner.Moved(MouthPlanner.WithExitHatch(bare[a], Math.Max(0, h)), -a.Split);
        }
        // the facing edges in line across the whole junction (the user, Oct 14 2026): where an arm's arriving side stops short of
        // the departing side of the arm whose through traffic comes into it (a split lead-in moved that one out), its arriving
        // lanes move out behind a hatch right of its centre line by its share of the jog: one slope from LineUpTaper out on it
        // to the other mouth, the core taking the rest; as far as its room allows
        foreach (var b in jp.Arms)
        {
            if (jp.Arms.FirstOrDefault(x => x != b && x.Exit == b.Arm) is not { } a || b.Mouth.CentreLine is null) continue;
            double jog = -a.Mouth.CarriagewayLeft - b.Mouth.CarriagewayRight;
            if (jog < LineUpMin) continue;
            // (a merged core: plus the way between the two arms' nodes)
            double core = Math.Max(0, a.OldTrim + a.MouthShift) + Math.Max(0, b.OldTrim + b.MouthShift)
                + (a.Axis is { } aa && b.Axis is { } ba ? aa.PointAt(0).DistanceTo(ba.PointAt(0)) : 0);
            var (_, roomR) = MouthPlanner.MouthRoom(b.Normal, b.Room, LeftStretch(b), RightStretch(b));
            double w = Math.Min(jog * LineUpTaper / (LineUpTaper + core), roomR - MouthPlanner.Reach(b.Mouth).Right);
            if (w >= LineUpMin) b.Mouth = MouthPlanner.WithLineUpHatch(b.Mouth, w);
        }
    }

    /// <summary>A jog between facing edges under this is left to the core (m); a line-up hatch closes over <see cref="LineUpTaper"/> (m).</summary>
    internal const double LineUpMin = 0.25, LineUpTaper = TransitionPlanner.ExitTaper;

    /// <summary>
    /// The room beside an arm (#818): at every <see cref="TurnStation"/> from its junction end out to <paramref name="reach"/>,
    /// the first offset from the axis on each side where a building stands, another line's ribbon lies, the ground is
    /// more than <see cref="TurnGroundStep"/> off the road or the tile ends; per station (the transition checks its own width there).
    /// </summary>
    private static ArmRoom MeasureRoom(RoadSegment seg, TileId tile, int self, bool atEnd, double from, double reach, EmbankmentPlanner.LineIndex lines,
        Dictionary<TileId, ChunkGrid>? grids, Footprints buildings)
    {
        double half = seg.Width * 0.5;
        double length = SegmentLength(seg);
        var stations = new List<double>();
        var lefts = new List<double>();
        var rights = new List<double>();
        for (double d = 0; d <= Math.Max(0, Math.Min(reach, length - Math.Max(0, from))) + 1e-6; d += TurnStation)
        {
            var (x, y, z, rx, rz) = ArmPoint(seg, atEnd, Math.Max(0, from + d));
            double left = RoomReach, right = RoomReach;
            for (int side = -1; side <= 1; side += 2)
            {
                double limit = RoomReach;
                for (double o = half + RoomStep; o <= limit; o += RoomStep)
                {
                    double px = x + rx * o * side, pz = z + rz * o * side;
                    var lv95 = new Vec2(tile.MinE + px, tile.MaxN - pz);
                    bool blocked = px < 0 || pz < 0 || px > TileSizeM || pz > TileSizeM
                        || buildings.Contains(lv95)
                        || lines.Covering(px, pz, self, x, z, half + 1.0) is not null
                        || grids is not null && SampleGround(grids, lv95.X, lv95.Y) is var g && !double.IsNaN(g) && Math.Abs(g - y) > TurnGroundStep;
                    if (!blocked) continue;
                    limit = o - RoomStep;
                    break;
                }
                if (side < 0) left = limit; else right = limit;
            }
            stations.Add(d);
            lefts.Add(left);
            rights.Add(right);
        }
        return new ArmRoom(stations.ToArray(), lefts.ToArray(), rights.ToArray());
    }

    /// <summary>
    /// The point <paramref name="d"/> metres along a segment from its junction end (its last point when <paramref name="atEnd"/>),
    /// tile-local, and the unit vector to the right of a driver arriving there.
    /// </summary>
    private static (double X, double Y, double Z, double Rx, double Rz) ArmPoint(RoadSegment seg, bool atEnd, double d)
    {
        var p = seg.Points;
        int n = seg.PointCount;
        int Idx(int k) => atEnd ? n - 1 - k : k;
        double run = 0;
        for (int k = 0; k + 1 < n; k++)
        {
            int a = Idx(k), b = Idx(k + 1);
            double ax = p[a * 3], az = p[a * 3 + 2], bx = p[b * 3], bz = p[b * 3 + 2];
            double len = Math.Sqrt(Sq(bx - ax) + Sq(bz - az));
            if (len < 1e-9) continue;
            if (run + len >= d || k + 2 == n)
            {
                double t = Math.Clamp((d - run) / len, 0, 1);
                // walking away from the junction (a -> b); the driver arriving goes b -> a
                double dx = (ax - bx) / len, dz = (az - bz) / len;
                return (ax + (bx - ax) * t, p[a * 3 + 1] + (p[b * 3 + 1] - p[a * 3 + 1]) * t, az + (bz - az) * t, -dz, dx);
            }
            run += len;
        }
        return (p[Idx(0) * 3], p[Idx(0) * 3 + 1], p[Idx(0) * 3 + 2], 0, 0);
    }
}
