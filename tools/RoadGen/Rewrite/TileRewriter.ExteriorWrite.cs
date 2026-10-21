namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// The planner's exterior written (#818 phase 4): each arm's bars (<see cref="ExteriorPlanner"/>) as paint across the arm at their
/// stations, its refuge as an island, its signs; points from the arm's axis out from the mouth, heights from the line's own. The
/// crosswalk is <see cref="EmitCrossing"/>'s (its zebra, the path's stop line and the path surface over the grass beside it).
/// </summary>
public static partial class TileRewriter
{
    /// <summary>
    /// The street's sides at an arm's junction end, as a driver arriving sees them (left, right): its first piece's, the drawing's
    /// right being the driver's where the junction is at the link's last point.
    /// </summary>
    private static (RoadSide Left, RoadSide Right) SidesAtMouth(ArmPlan a, RoadSegment piece) =>
        a.AtEnd ? (piece.Attributes.Left, piece.Attributes.Right) : (piece.Attributes.Right, piece.Attributes.Left);

    /// <summary>
    /// A lit junction's lights (#818 phase 4): its plan from every arm's mouth section (<see cref="SignalPlan.Build"/>, validated
    /// as the old layout's), its poles at the kerb beside each cars' line (the second at the other kerb or on the refuge), the
    /// cars' line's middle per arm (<c>SGNL</c> stops), and each approach's lanes (<c>LANE</c>) as its transition lays them out.
    /// </summary>
    /// <summary>Riders following the cars' lights get a bike group of their own where they lengthen a conflict time by more than this (s, #979).</summary>
    private const float LongRiders = 2f;

    private static SignalPlan? WriteSignals(JunctionPlan jp, RoadGenResult result, Dictionary<TileId, List<RoadSignal>> signals,
        Dictionary<TileId, List<RoadApproach>> approaches, Cantons? cantons, SignalStats signalStats, LayoutWriteStats stats,
        Dictionary<TileId, List<RoadPointProp>> signs, Func<Vec2, bool> clear, Func<Vec2, bool>? building = null)
    {
        var net = result.Network;
        var home = TileId.FromLv95(jp.Centre.X, jp.Centre.Y);
        var arms = new List<SignalArm>();
        var exterior = new List<ArmExterior>();
        foreach (var a in jp.Arms)
        {
            var ex = jp.Exterior.First(x => x.Arm == a);
            exterior.Add(ex);
            var mouth = a.Mouth;
            var ins = mouth.CarLanes(StripWay.In).Select(l => l.Strip).ToList();
            bool approach = ins.Count > 0 && ex.CarStop is not null, leaves = mouth.CarLanes(StripWay.Out).Any();
            var info = InfoOf(net.Links[a.LinkId]);
            bool urban = mouth.KerbLeftCm > 0 || mouth.KerbRightCm > 0;
            // riders get their own signal on a path, or on a bike lane kerbside of a right pocket (layout a); not on layout (b)'s
            // right-turn bike lane at the kerb, its riders turning with the pocket's cars (#854: it held the right arrow back)
            int last = mouth.LastCarriageway;
            bool path = mouth.Strips.Skip(last + 1).Any(x => x.Kind == StripKind.Path);
            bool kerbside = a.HasRight && last >= 0 && mouth.Strips[last] is { Kind: StripKind.BikeLane, Way: StripWay.In } kerbLane
                && kerbLane.Moves != SignalMoves.Right;
            // the bike lane between the through lane and the right pocket (layout b): no conflict with that pocket's arrow (#979)
            bool between = a.HasRight && !kerbside && mouth.Strips.Any(x => x.Kind == StripKind.BikeLane && x.Way == StripWay.In && x.Moves != SignalMoves.Right && x.Moves != SignalMoves.Left);
            // the queue each lane holds behind its line (#982): a pocket over its storage, a lane of the street's own (the through
            // lane, a turn lane from OSM's lanes) back along the street to the junction behind; at least a car
            double stop = ex.CarStop ?? 0;
            float Room(double m) => approach ? (float)Math.Max(SignalPlan.CarSpacing, m - stop) : 0f;
            double Run(PocketRule rule) => a.Openings.Where(o => rule.Owns(o.Id)).Select(o => o.Storage).DefaultIfEmpty(a.Length).Max();
            arms.Add(new SignalArm(a.Heading, approach, leaves, ins.Any(l => l.Moves == SignalMoves.Left), ins.Any(l => l.Moves == SignalMoves.Right),
                Pedestrians: ex.Crosswalk is not null, BikeSignal: approach && (path || kerbside), SpeedKmh: urban ? 50 : 60,
                CrossingM: (float)(mouth.CarriagewayWidth), Rank: (byte)Math.Clamp(info is { } i ? PriorityPlanner.Rank(i) / 4 : 1, 1, 255),
                Banned: approach ? a.Wants.Banned : SignalMoves.None, BikeBetween: between,
                StorageM: Room(a.Length), LeftStorageM: Room(Run(PocketRules.Left)), RightStorageM: Room(Run(PocketRules.Right))));
        }
        if (arms.Count(x => x.In) < 2) return null;
        string? canton = cantons?.CodeAt(jp.Centre.X, jp.Centre.Y);
        bool amber = PedestrianAmber(canton);
        uint seed = (uint)(long)Math.Round(jp.Centre.X) * 73856093u ^ (uint)(long)Math.Round(jp.Centre.Y) * 19349663u;
        var plan = SignalPlan.Build(arms, seed, amber);
        // a kerbside bike lane keeps its signal only where a phase holds the right arrow while its riders go; a bike group that
        // finds no phase goes without its signal, not the junction without its plan (as the old layout)
        for (int k = 0; k < arms.Count; k++)
            if (arms[k].BikeSignal && !plan.ThroughWithRightHeld(k) && exterior[k].Arm.HasRight)
            {
                arms[k] = arms[k] with { BikeSignal = false };
                plan = SignalPlan.Build(arms, seed, amber);
            }
        // riders who follow the cars' lights (a bike lane with no signal of its own, a left-turn bike lane) and whose slow clearing
        // would lengthen their cars' conflict times by more than LongRiders get a bike group of their own, which can end earlier
        // (the user, #979: only where it is long)
        if (jp.Core is { } ridersCore && ridersCore.Connectors.Any(c => c.Kind == ConnectorKind.Bike))
        {
            int GroupOfPath(Connector c) => PathGroup(plan, jp.Arms.FindIndex(x => x.Arm == c.FromArm), c.Move, c.Kind);
            bool Shared(ConflictTimes.Line l) => l.Mover == ConflictTimes.Mover.Bike && plan.Groups[l.Group].Kind != SignalGroupKind.Bike;
            var with = ConflictTimes.Of(jp, plan, [], GroupOfPath);
            var without = ConflictTimes.Of(jp, plan, [], GroupOfPath, l => !Shared(l));
            bool rebuild = false;
            foreach (var c in ridersCore.Connectors.Where(c => c.Kind == ConnectorKind.Bike))
            {
                int k = jp.Arms.FindIndex(x => x.Arm == c.FromArm), g = GroupOfPath(c);
                if (k < 0 || g < 0 || plan.Groups[g].Kind == SignalGroupKind.Bike) continue;
                float most = 0;
                for (int h = 0; h < plan.Groups.Count; h++)
                    if (!float.IsNaN(with[g, h])) most = Math.Max(most, with[g, h] - (float.IsNaN(without[g, h]) ? 0f : without[g, h]));
                if (most <= LongRiders) continue;
                if (c.Move == SignalMoves.Left && !arms[k].RiderLeft) { arms[k] = arms[k] with { RiderLeft = true }; rebuild = true; }
                else if (c.Move != SignalMoves.Left && !arms[k].BikeSignal) { arms[k] = arms[k] with { BikeSignal = true }; rebuild = true; }
            }
            if (rebuild) { plan = SignalPlan.Build(arms, seed, amber); stats.RiderGroups++; }
        }
        if (plan.Validate().Any(e => e.Contains("(Bike arm")))
        {
            for (int k = 0; k < arms.Count; k++) arms[k] = arms[k] with { BikeSignal = false };
            plan = SignalPlan.Build(arms, seed, amber);
        }
        if (plan.Validate() is { Count: > 0 })
        {
            stats.SignalsInvalid++;
            return null;
        }
        plan.ArrowPlates = ArrowPlates(canton);
        plan.PoleColour = PoleColour(canton);

        float SampleAt(ArmPlan a, Vec2 p) => net.Links[a.LinkId].Tag is Source s ? s.SampleHeight(p) : 0f;
        var stops = new float[arms.Count * 3];
        var poles = new List<SignalPole>();
        var overheads = new List<SignalOverhead>();
        var records = new List<(int Arm, Vec2 At, double Stop)>();
        for (int k = 0; k < arms.Count; k++)
        {
            var ex = exterior[k];
            var a = ex.Arm;
            double m0 = jp.Core!.Mouths.First(m => m.Arm == a.Arm).Station;
            var axis = a.Axis!;
            Vec2 P(double s, double o) => axis.At(m0 + s, o);
            if (arms[k].In && ex.CarStop is { } stop)
            {
                var sec = ExteriorPlanner.SectionAt(a, stop);
                var runs = ExteriorPlanner.Runs(sec, x => x.IsCarLane && x.Way == StripWay.In);
                var at = P(stop, (runs.Min(r => r.Left) + runs.Max(r => r.Right)) * 0.5);
                var local = Local(home, [at], p => SampleAt(a, p), 0f);
                Array.Copy(local, 0, stops, k * 3, 3);
                records.Add((k, at, stop));
            }
            else for (int j = 0; j < 3; j++) stops[k * 3 + j] = float.NaN;
            var u = axis.DirAt(m0);
            var right = axis.RightAt(m0);
            (Vec2 Foot, float Y)? mainFoot = null, leftFoot = null, refugeFoot = null;   // for an overhead structure (#1037)
            (int From, int Count) mainSigns = (0, 0);   // the main pole's priority signs, moved out onto a post standing on it
            foreach (var p in ex.Poles)
            {
                // the foot (the old layout's PlacePole, #350/#711, restored by #1000): on the refuge; else half a metre out of the kerb,
                // the main pole past the approach's path where that path stops at its own line (riders wait beside it, it is not
                // between them and the road), never on a path; stepping out, then back along the arm, until clear of every
                // carriageway, junction, widening strip and building; none where nothing is clear
                Vec2 foot;
                float lift;
                if (p.OnRefuge && ex.Refuge is { } rf)
                {
                    // in the middle of the island where it stands (#1000)
                    var (il, ir) = ex.Islands.Count > 0 ? ExteriorPlanner.IslandEdges(a, p.Station) ?? (rf.Left, rf.Right) : (rf.Left, rf.Right);
                    foot = P(p.Station, (il + ir) * 0.5);
                    lift = 0.12f;
                }
                else if (ClearFoot(p, ex, a, P, clear) is var (spot, kerbLift)) (foot, lift) = (spot, kerbLift);
                else { stats.PolesRejected++; continue; }
                var local = Local(home, [foot], q => SampleAt(a, q), lift);
                // car heads face the arriving drivers; a pedestrian head faces across its crossing
                // the second pole carries the left arrow where a lane turns left only (the check: every such arm has one)
                // (on the island where there is one, the left kerb's pole keeping the walkers' head alone, #1000)
                var flags = p.Flags | (!p.RightKerb && (p.OnRefuge || ex.Refuge is null) && arms[k].LeftPocket ? SignalPoleFlags.Second : SignalPoleFlags.None);
                if (!arms[k].In) flags &= ~(SignalPoleFlags.Main | SignalPoleFlags.Second);
                var pole = new SignalPole(local[0], local[1], local[2], Heading(u), Heading(p.RightKerb ? -right : right), (byte)k, flags);
                poles.Add(pole);
                if (p.OnRefuge) refugeFoot = (foot, local[1]);
                else if (p.RightKerb) mainFoot = (foot, local[1]);
                else leftFoot = (foot, local[1]);
                stats.Poles++;
                // the approach's priority signs for when the lights are off (#121) on its main pole: below the heads, or above them
                // where a bike head takes the room below (the old layout's, #350/#711)
                if ((flags & SignalPoleFlags.Main) == 0) continue;
                mainSigns = (Get(signs, home).Count, ex.Signs.Count(x => x.OnMainPole));
                bool bikeHead = plan.Groups.Any(g => g.Kind == SignalGroupKind.Bike && g.Arm == k && g.Moves != SignalMoves.Left);
                bool pedHead = (flags & SignalPoleFlags.Pedestrian) != 0 && plan.Groups.Any(g => g.Kind == SignalGroupKind.Pedestrian && g.Arm == k);
                foreach (var sign in ex.Signs.Where(x => x.OnMainPole))
                {
                    float top = bikeHead ? (pedHead ? CarHeadsTopAbovePedestrian : CarHeadsTop) + SignAboveHeads + RoadSigns.PlateHeight(sign.Type, sign.Variant)
                        : SignOnPoleTop;
                    Get(signs, home).Add(new RoadPointProp(sign.Type, sign.Variant, PropFlags.Solid | PropFlags.OnSignalPole, pole.X, pole.Y, pole.Z,
                        pole.CarHeading, top));
                    stats.SignsOnPoles++;
                }
            }
            // the heads over its lanes (#1037): on a mast arm, a portal or a tram street's span wire; the far kerb's foot found as a
            // pole's where no pole stands there
            if (arms[k].In && ex.Overhead.Count > 0)
            {
                if (leftFoot is null && ex.Poles.Count > 0
                    && ClearFoot(new PoleSpot(ex.Poles[0].Station, RightKerb: false, OnRefuge: false, 0), ex, a, P, clear) is var (lf, lLift))
                    leftFoot = (lf, Local(home, [lf], q => SampleAt(a, q), lLift)[1]);
                bool tram = net.Links[a.LinkId].Tag is Source { Line.Osm.Tram: true };   // OSM railway=tram along the arm
                if (PlaceOverhead(ex, (byte)k, P, u, right, mainFoot, leftFoot, refugeFoot, tram, home, q => SampleAt(a, q), clear, building, stats) is { } o)
                {
                    overheads.Add(o);
                    // a post on the main pole carries its signs: their plates out in front of its face (the user)
                    var list = Get(signs, home);
                    for (int i = mainSigns.From; i < mainSigns.From + mainSigns.Count && i < list.Count; i++)
                        if (o.StandsOn(list[i].X, list[i].Z))
                        {
                            var p0 = list[i];
                            float shift = SignalOverhead.PostHalf - RoadSigns.PoleDiameter * 0.5f;
                            list[i] = p0 with { X = p0.X - MathF.Sin(p0.Heading) * shift, Z = p0.Z - MathF.Cos(p0.Heading) * shift };
                        }
                }
            }
        }
        var centre = Local(home, [jp.Centre], p => SampleAt(jp.Arms[0], p), 0f);
        var record = new RoadSignal { X = centre[0], Y = centre[1], Z = centre[2], Stops = stops, Plan = plan, Poles = poles };
        record.Overheads.AddRange(overheads);
        Get(signals, home).Add(record);
        short index = (short)(Get(signals, home).Count - 1);
        foreach (var (k, at, stop) in records)
            Get(approaches, home).Add(ApproachOf(exterior[k], at, stop, home, index, (byte)k, net));
        stats.Signals++;
        signalStats.Junctions++;
        return plan;
    }

    /// <summary>
    /// An approach's lane record (<c>LANE</c>, #353) as its transition lays the lanes out: every arriving car and bike lane at
    /// the cars' line left to right, offsets from the street's own arriving lane, each lane full from where it stops changing
    /// (out from the line) and starting where it leaves the street's normal section; riders' lanes stop at their own line.
    /// </summary>
    private static RoadApproach ApproachOf(ArmExterior ex, Vec2 at, double stop, TileId home, short signal, byte planArm, RoadNetwork net)
    {
        var a = ex.Arm;
        var sec = ExteriorPlanner.SectionAt(a, stop);
        double centre = a.Normal.CarLanes(StripWay.In).Select(l => a.Normal.Span(l.Index)).Select(s => (s.From + s.To) * 0.5).DefaultIfEmpty(0).First();
        var all = SignalMoves.None;
        foreach (var x in a.Mouth.CarLanes(StripWay.In)) all |= x.Strip.Moves;
        var lanes = new List<ApproachLane>();
        for (int k = 0; k < sec.Strips.Count; k++)
        {
            var x = sec.Strips[k];
            if (x.Way != StripWay.In || !(x.IsCarLane || x.Kind == StripKind.BikeLane)) continue;
            var (f, t) = sec.Span(k);
            var (full, from) = Reach(a, x.Id, stop);
            var moves = x.Moves != SignalMoves.None ? x.Moves : all != SignalMoves.None ? all : SignalMoves.Left | SignalMoves.Through | SignalMoves.Right;
            bool bike = x.Kind == StripKind.BikeLane;
            lanes.Add(new ApproachLane((float)((f + t) * 0.5 - centre), (float)full, (float)Math.Max(full, from), moves,
                bike ? ApproachLaneKind.Bike : ApproachLaneKind.Car, bike && ex.RiderStop is { } r ? (float)(r - stop) : 0f));
        }
        float y = net.Links[a.LinkId].Tag is Source s ? s.SampleHeight(at) : 0f;
        var local = Local(home, [at], _ => y, 0f);
        return new RoadApproach
        {
            X = local[0], Y = local[1], Z = local[2], Heading = (float)a.Heading, Signal = signal, SignalArm = planArm, Banned = a.Wants.Banned,
            LaneCentre = (float)centre, Lanes = lanes,
        };
    }

    /// <summary>
    /// How far out from the line (at <paramref name="stop"/>) a lane keeps its width and place, and from where out it starts (where
    /// the street's normal section takes over): its transition's stations; 15 m and 30 m (the old records') without one.
    /// </summary>
    private static (double Full, double From) Reach(ArmPlan a, int id, double stop)
    {
        if (a.Transition is not { } t) return (15, 30);
        var sections = new List<(double S, double Mid, double W)>();
        for (double s = 0; s <= t.Length + 1e-6; s += 0.5)
        {
            var sec = ExteriorPlanner.SectionAt(a, s);
            int k = sec.Strips.ToList().FindIndex(x => x.Id == id);
            if (k < 0) { sections.Add((s, double.NaN, 0)); continue; }
            var (f, to) = sec.Span(k);
            sections.Add((s, (f + to) * 0.5, to - f));
        }
        // a line past the transition's end stands on the street's own lanes: as without one
        int at = sections.FindIndex(x => x.S >= stop - 1e-6);
        if (at < 0 || double.IsNaN(sections[at].Mid)) return (15, 30);
        var first = sections[at];
        double full = sections.TakeWhile(x => x.S < stop - 1e-6 || (!double.IsNaN(x.Mid) && Math.Abs(x.Mid - first.Mid) < 0.02 && Math.Abs(x.W - first.W) < 0.02)).Last().S;
        double from = t.Length;
        return (Math.Max(0, full - stop), Math.Max(0, from - stop));
    }

    /// <summary>
    /// The lines painted through a core (#818 phase 2's, connector edges chosen by <c>JunctionRules</c>): the main road's centre
    /// line (the street's dashes), the edge guides (dashed across a joining road, solid else, none in towns), the left-turn, pair
    /// and through guides (dashed); on the core's surface, its heights blended from the mouths.
    /// </summary>
    private static void WriteCoreLines(JunctionPlan jp, RoadGenResult result, RoadJunction? cap, Dictionary<TileId, List<RoadPaint>> paint, LayoutWriteStats stats)
    {
        var core = jp.Core!;
        var home = TileId.FromLv95(jp.Centre.X, jp.Centre.Y);
        var anchors = new List<(Vec2 At, float Height)>();
        foreach (var m in core.Mouths)
            if (result.Network.Links[jp.Arms.First(a => a.Arm == m.Arm).LinkId].Tag is Source s)
            {
                var mid = (m.Left + m.Right) * 0.5;
                anchors.Add((mid, s.SampleHeight(mid)));
            }
        if (anchors.Count == 0) return;
        bool urban = jp.Arms.Any(a => a.Normal.KerbLeftCm > 0 || a.Normal.KerbRightCm > 0);
        float Height(Vec2 p) => cap is null ? HeightAt(anchors, p) : CapHeightAt(cap, home, p);
        // a roundabout's entries give way at the ring's edge (#854): whole teeth, centred, apexes toward the arriving driver
        foreach (var (left, right) in core.Teeth)
        {
            double period = RoadSigns.ToothBase + RoadSigns.ToothGap, length = left.DistanceTo(right);
            int count = (int)Math.Floor((length + RoadSigns.ToothGap) / period);
            if (count < 1) continue;
            double used = count * period - RoadSigns.ToothGap;
            var u = (right - left) / length;
            var a0 = left + u * ((length - used) * 0.5);
            Get(paint, home).Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.SharkTooth, Rgba = PaintEmitter.White, Width = RoadSigns.ToothHeight,
                Dash = RoadSigns.ToothBase, Gap = RoadSigns.ToothGap, Vertices = Local(home, [a0, a0 + u * used], Height, 0f),
            });
            stats.Bars++;
        }
        foreach (var line in core.Lines)
        {
            if (line.Points.Count < 2 || ClipToRing(line.Points, core.Outline, core.Mouths).Count < 2) continue;
            bool centre = line.Kind == "centre";
            if (!centre && !line.Dashed && urban) continue;
            float dash = centre ? 3f : line.Dashed ? GuideDash : 0f, gap = centre ? (urban ? 3f : 6f) : line.Dashed ? GuideDash : 0f;
            Get(paint, home).Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = dash > 0 ? PaintType.WhiteDashed : PaintType.WhiteSolid, Rgba = PaintEmitter.White,
                Width = PaintEmitter.LineWidth, Dash = dash, Gap = gap,
                // cut to the core's surface: none off the road (the user, Oct 17)
                Vertices = RoadPaintGeometry.Simplify(Local(home, ClipToRing(line.Points, core.Outline, core.Mouths), p => cap is null ? HeightAt(anchors, p) : CapHeightAt(cap, home, p), 0f)),
            });
            stats.CoreLines++;
        }
    }

    private static void WriteExterior(JunctionPlan jp, RoadGenResult result, Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf,
        Dictionary<RoadSegment, List<RoadSegment>> finalPieces, Dictionary<TileId, List<RoadPaint>> paint, Dictionary<TileId, List<RoadAreaProp>> areas,
        Dictionary<TileId, List<RoadPointProp>> signs, SignalStats signalStats, LayoutWriteStats stats)
    {
        var net = result.Network;
        foreach (var ex in jp.Exterior)
        {
            var a = ex.Arm;
            if (a.Axis is not { } axis || net.Links[a.LinkId].Tag is not Source source || jp.Core!.Mouths.FirstOrDefault(m => m.Arm == a.Arm) is not { } mouth)
                continue;
            double m0 = mouth.Station;
            Vec2 P(double s, double o) => axis.At(m0 + s, o);
            var tile = source.Tile;
            var into = Get(paint, tile);
            var so = segmentOf[a.LinkId];
            var pieces = finalPieces.TryGetValue(so.Segment, out var ps) && ps.Count > 0 ? ps : [so.Segment];
            var (leftSide, rightSide) = SidesAtMouth(a, a.AtEnd ? pieces[^1] : pieces[0]);
            RoadPaint Across(double s, double l, double r, PaintType type, uint rgba, float width) => new()
            {
                Shape = PaintShape.Polyline, Type = type, Rgba = rgba, Width = width,
                Vertices = Local(tile, [P(s, l), P(s, r)], source.SampleHeight, 0f),
            };
            // a hatch wide at the mouth (an exit's, a line-up's) closed by a solid line across it, as the old layout's (#123): where
            // its stripes and borders start (the line on their ends), past what the transition's paint keeps clear (the cars' line at the lights, a crosswalk)
            double barsTo = ex.Bars.Where(b => b.Kind is BarKind.StopLine or BarKind.RidersLine).Select(b => b.To).DefaultIfEmpty(0).Max();
            double closeAt = jp.Lights && barsTo > 0 ? barsTo : ex.Crosswalk is { } zebra && zebra.From - 0.3 < 0.5 ? zebra.To + 0.3 : 0;
            var atMouth = ExteriorPlanner.SectionAt(a, closeAt);
            for (int k = 0; k < atMouth.Strips.Count; k++)
                if (atMouth.Strips[k].Kind == StripKind.Hatch && atMouth.Strips[k].Width >= 0.5)
                {
                    var (hl, hr) = atMouth.Span(k);
                    into.Add(Across(closeAt, hl, hr, PaintType.WhiteSolid, PaintEmitter.White, PaintEmitter.LineWidth));
                    stats.Marks++;
                }
            foreach (var b in ex.Bars)
            {
                switch (b.Kind)
                {
                    case BarKind.Crosswalk:
                        EmitCrossing(paint, source, axis.PointAt(m0), axis.DirAt(m0), axis.RightAt(m0), b.To + ZebraClear, b.Left, b.Right, rightSide, leftSide,
                            areas, signalStats, gap: b.Gap is { } g ? (g.Left, g.Right) : null, riderStop: ex.RiderStop ?? ex.CarStop, pavedGrass: false);
                        break;
                    case BarKind.RidersLine:
                        into.Add(Across(b.Middle, b.Left + 0.05, b.Right - 0.05, PaintType.StopLine, PaintEmitter.Yellow, (float)(b.To - b.From)));
                        break;
                    case BarKind.StopLine:
                    case BarKind.PocketBar:
                        into.Add(Across(b.Middle, b.Left + 0.1, b.Right - 0.1, PaintType.StopLine, PaintEmitter.White, (float)(b.To - b.From)));
                        break;
                    case BarKind.BikeBox:
                    {
                        // the box's yellow sides and the bike symbol in it, the riders facing the junction; no red surface (the user, Oct 17)
                        double c = (b.Left + b.Right) * 0.5;
                        foreach (double o in (ReadOnlySpan<double>)[b.Left + 0.075, b.Right - 0.075])
                            into.Add(new RoadPaint
                            {
                                Shape = PaintShape.Polyline, Type = PaintType.YellowSolid, Rgba = PaintEmitter.Yellow, Width = BikePlanner.LineWidth,
                                Vertices = Local(tile, [P(b.From, o), P(b.To, o)], source.SampleHeight, 0f),
                            });
                        double mid = b.Middle;
                        into.Add(new RoadPaint
                        {
                            Shape = PaintShape.Polyline, Type = PaintType.BikeSymbol, Rgba = PaintEmitter.Yellow, Width = BikePlanner.SymbolSize,
                            Vertices = Local(tile, [P(mid + BikePlanner.SymbolSize * 0.5, c), P(mid - BikePlanner.SymbolSize * 0.5, c)], source.SampleHeight, 0f),
                        });
                        break;
                    }
                    case BarKind.GiveWay:
                    {
                        // whole teeth, centred in the row; apexes toward the arriving driver (on the right of left-to-right)
                        double period = RoadSigns.ToothBase + RoadSigns.ToothGap, length = b.Right - b.Left;
                        int count = (int)Math.Floor((length + RoadSigns.ToothGap) / period);
                        if (count < 1) break;
                        double used = count * period - RoadSigns.ToothGap, l = b.Left + (length - used) * 0.5;
                        into.Add(new RoadPaint
                        {
                            Shape = PaintShape.Polyline, Type = PaintType.SharkTooth, Rgba = PaintEmitter.White, Width = RoadSigns.ToothHeight,
                            Dash = RoadSigns.ToothBase, Gap = RoadSigns.ToothGap,
                            Vertices = Local(tile, [P(b.From, l), P(b.From, l + used)], source.SampleHeight, 0f),
                        });
                        break;
                    }
                }
                stats.Bars++;
            }
            // a lit arm's island in its pieces, rounded where they stand free (#1000); else the refuge whole
            foreach (var piece in ex.Islands)
            {
                var ring = IslandRing(piece, st => ExteriorPlanner.IslandEdges(a, st) ?? (piece.Left, piece.Right)).Select(q => P(q.S, q.O)).ToList();
                var indices = Junctions.EarClip.Triangulate(ring).Select(i => (ushort)i).ToList();
                Get(areas, tile).Add(new RoadAreaProp
                {
                    Type = AreaPropType.Island, Variant = 2, Flags = PropFlags.Solid, Height = 0.12f,
                    Vertices = Local(tile, ring, source.SampleHeight, 0f), Indices = indices.ToArray(),
                });
                stats.Refuges++;
            }
            if (ex.Islands.Count == 0 && ex.Refuge is { } rf)
            {
                Get(areas, tile).Add(new RoadAreaProp
                {
                    Type = AreaPropType.Island, Variant = 2, Flags = PropFlags.Solid, Height = 0.12f,
                    Vertices = Local(tile, [P(rf.From, rf.Left), P(rf.To, rf.Left), P(rf.To, rf.Right), P(rf.From, rf.Right)], source.SampleHeight, 0f),
                    Indices = [0, 1, 2, 0, 2, 3],
                });
                stats.Refuges++;
            }
            foreach (var sign in ex.Signs)
            {
                // (a lit approach's goes on its main pole: WriteSignals)
                if (sign.OnMainPole) continue;
                // beside the kerb, its plate clear of the carriageway, on the sidewalk where the side has one
                var sec = ExteriorPlanner.SectionAt(a, sign.Station);
                var side = sign.RightKerb ? rightSide : leftSide;
                bool urban = side.KerbCm > 0;
                double clear = RoadSigns.Clearance(urban) + RoadSigns.Side(sign.Type, sign.Variant) * 0.5;
                double o = sign.RightKerb ? sec.CarriagewayRight + clear : sec.CarriagewayLeft - clear;
                var at = P(sign.Station, o);
                bool onSidewalk = side.OuterDm / 10.0 >= clear;
                float road = source.SampleHeight(P(sign.Station, 0));
                float kerb = onSidewalk ? side.KerbCm / 100f : 0f;
                var facing = sign.FacesArriving ? axis.DirAt(m0 + sign.Station) : -axis.DirAt(m0 + sign.Station);
                var local = Local(tile, [at], _ => road, 0f);
                Get(signs, tile).Add(new RoadPointProp(sign.Type, sign.Variant, PropFlags.Solid, local[0], road + kerb, local[2],
                    (float)Math.Atan2(-facing.X, facing.Y), (onSidewalk ? RoadSigns.LowerEdgeSidewalk : RoadSigns.LowerEdge) + RoadSigns.PlateHeight(sign.Type, sign.Variant)));
                stats.Signs++;
            }
        }
    }

    /// <summary>
    /// A pole's foot beside its kerb (the old layout's <c>PlacePole</c>, #350/#711, restored by #1000): <see cref="PoleClear"/> out of
    /// the kerb, the main one past the approach's path where the arm has a crosswalk, never on a path, stepping out by
    /// <see cref="PoleStep"/> up to <see cref="PoleTries"/> times, then <see cref="PoleBackStep"/> further out along the arm up to four
    /// times, until <paramref name="clear"/> says the spot is clear; its kerb's lift; null when none is.
    /// </summary>
    private static (Vec2 Foot, float Lift)? ClearFoot(PoleSpot p, ArmExterior ex, ArmPlan a, Func<double, double, Vec2> at, Func<Vec2, bool> clear)
    {
        double r = RoadSigns.PoleDiameter * 0.5;
        foreach (double back in (ReadOnlySpan<double>)[0, PoleBackStep, 2 * PoleBackStep, 3 * PoleBackStep, 4 * PoleBackStep])
        {
            var sec = ExteriorPlanner.SectionAt(a, p.Station + back);
            var bands = p.RightKerb ? sec.Strips.Skip(sec.LastCarriageway + 1).ToList() : sec.Strips.Take(Math.Max(0, sec.FirstCarriageway)).Reverse().ToList();
            // the side's path, out from the kerb
            double from = 0, pathFrom = -1, pathTo = -1;
            foreach (var b in bands)
            {
                if (b.Kind == StripKind.Sidewalk) break;
                if (b.Kind == StripKind.Path && pathFrom < 0) (pathFrom, pathTo) = (from, from + b.Width);
                from += b.Width;
            }
            bool pastPath = pathFrom >= 0 && ex.Crosswalk is not null && (p.Flags & SignalPoleFlags.Main) != 0;
            double start = pastPath ? pathTo + PoleClear : PoleClear;
            for (int k = 0; k < PoleTries; k++)
            {
                double off = start + k * PoleStep;
                if (pathFrom >= 0 && off + r > pathFrom && off - r < pathTo) continue;
                var foot = at(p.Station + back, p.RightKerb ? sec.CarriagewayRight + off : sec.CarriagewayLeft - off);
                if (clear(foot)) return (foot, (p.RightKerb ? sec.KerbRightCm : sec.KerbLeftCm) / 100f);
            }
        }
        return null;
    }

    /// <summary>
    /// An overhead structure's post stands on its pole (the user: the pole's heads and signs are mounted on it, no pole beside it);
    /// an arm reaches at most this far (Basel-Landschaft's mast arms: 3-4.5 m over one lane, 5.5-7.5 m over two, to 10 m with a tie
    /// rod) and runs on this far past its last head; the plates' lower edge stands this high over the road (SSV Art. 71: 4.50-5.50
    /// m), on a span wire higher (tram wires, Bern: 5.50-6.00 m) (m, #1037).
    /// </summary>
    private const double ArmReach = 10.0, ArmBeyond = 0.4;
    /// <summary>A span wire's facades stand at most this far out from the kerb poles (m), looked for in steps of <see cref="FacadeStep"/>.</summary>
    private const double FacadeReach = 8.0, FacadeStep = 0.25;
    private const float HeadLowerEdge = 5.0f, SpanLowerEdge = 5.5f;

    /// <summary>
    /// The approach's overhead structure (#1037, <c>docs/plans/overhead-traffic-lights.md</c>), first that fits: a mast arm from the
    /// post by the main pole, reaching left over every head; one from the refuge reaching right; on a tram street between two
    /// facades, a span wire anchored on their walls; else a portal from the main pole's post to the far kerb's. Span wires only where
    /// an arm cannot reach, on tram streets, between buildings, never on poles (the user, Oct 21 2026: rare in Switzerland; Bern's
    /// handbook mentions carriers hung from span points and heads on house fronts). Each head over its lanes
    /// (<see cref="OverheadSpot.Offset"/>), measured along the structure from its post. Null (counted) where none fits.
    /// </summary>
    private static SignalOverhead? PlaceOverhead(ArmExterior ex, byte arm, Func<double, double, Vec2> P, Vec2 u, Vec2 right,
        (Vec2 Foot, float Y)? main, (Vec2 Foot, float Y)? left, (Vec2 Foot, float Y)? refuge, bool tram, TileId home,
        Func<Vec2, float> height, Func<Vec2, bool> clear, Func<Vec2, bool>? building, LayoutWriteStats stats)
    {
        // the facade outward from a kerb pole: the last point before a building, within FacadeReach
        Vec2? Facade(Vec2 from, Vec2 away)
        {
            if (building is null) return null;
            for (double d = FacadeStep; d <= FacadeReach; d += FacadeStep)
                if (building(from + away * d)) return from + away * (d - FacadeStep);
            return null;
        }
        if (main is not var (m, mY)) { stats.OverheadsRejected++; return null; }
        var origin = P(0, 0);
        double Off(Vec2 q) => (q - origin).Dot(right);
        var heads = ex.Overhead;
        var post = m;
        float y = mY, endY = mY;
        double reach = heads.Max(h => Off(post) - h.Offset);
        OverheadKind kind;
        Vec2 end;
        if (reach + ArmBeyond <= ArmReach) (kind, end) = (OverheadKind.Arm, post - right * (reach + ArmBeyond));
        else if (refuge is var (r, rY) && r is var rPost && heads.All(h => h.Offset > Off(rPost))
                 && heads.Max(h => h.Offset - Off(rPost)) + ArmBeyond <= ArmReach)
        {
            (kind, post, y, endY) = (OverheadKind.RefugeArm, rPost, rY, rY);
            end = post + right * (heads.Max(h => h.Offset - Off(rPost)) + ArmBeyond);
        }
        else if (tram && left is var (lf, _) && Facade(m, right) is { } wallA && Facade(lf, right * -1) is { } wallB)
            (kind, post, end, y, endY) = (OverheadKind.Span, wallA, wallB, height(wallA), height(wallB));
        else if (left is var (l2, l2Y)) (kind, end, endY) = (OverheadKind.Portal, l2, l2Y);
        else { stats.OverheadsRejected++; return null; }

        // along the structure: a metre of it moves (dir . right) across the arm
        var dir = end - post;
        double length = Math.Sqrt(dir.Dot(dir));
        dir = dir * (1 / length);
        double across = dir.Dot(right);
        var placed = heads.Select(h => (Along: (h.Offset - Off(post)) / across, h.Moves)).ToList();
        // the plates' lower edge over the highest road under them
        float road = placed.Max(h => Local(home, [post + dir * h.Along], height, 0f)[1]);
        var a = Local(home, [post], _ => y, 0f);
        var b = Local(home, [end], _ => endY, 0f);
        switch (kind)
        {
            case OverheadKind.Arm: stats.OverheadArms++; break;
            case OverheadKind.RefugeArm: stats.OverheadRefugeArms++; break;
            case OverheadKind.Portal: stats.OverheadPortals++; break;
            default: stats.OverheadSpans++; break;
        }
        return new SignalOverhead
        {
            Kind = kind, Arm = arm, X = a[0], Y = a[1], Z = a[2], EndX = b[0], EndY = b[1], EndZ = b[2],
            Bottom = road + (kind == OverheadKind.Span ? SpanLowerEdge : HeadLowerEdge), Heading = Heading(u),
            Heads = placed.Select(h => new OverheadHead((float)h.Along, h.Moves)).ToList(),
        };
    }

    /// <summary>
    /// An island piece's outline (#1000) in its arm's stations and offsets, going round: its two long sides along
    /// <paramref name="edges"/> (the gore's, so it tapers with it), a half circle at an end that stands free (as wide as the piece
    /// there, no longer than half of it), square at one the crosswalk cuts.
    /// </summary>
    private static List<(double S, double O)> IslandRing(IslandPiece p, Func<double, (double Left, double Right)> edges)
    {
        const int Steps = 8;
        double len = p.To - p.From;
        var (fl, fr) = edges(p.From);
        var (tl, tr) = edges(p.To);
        double rFrom = p.RoundFrom ? Math.Min((fr - fl) * 0.5, len * 0.5) : 0, rTo = p.RoundTo ? Math.Min((tr - tl) * 0.5, len * 0.5) : 0;
        double from = p.From + rFrom, to = p.To - rTo;
        var ring = new List<(double, double)>();
        // along the left side, tapering with the gore, round the far end, back along the right side, round the near end
        for (double s = from; s < to; s += 0.5) ring.Add((s, edges(s).Left));
        var (el, er) = edges(to);
        ring.Add((to, el));
        if (p.RoundTo)
            for (int k = 1; k < Steps; k++)
            {
                double t = -Math.PI / 2 + Math.PI * k / Steps;
                ring.Add((to + rTo * Math.Cos(t), (el + er) * 0.5 + (er - el) * 0.5 * Math.Sin(t)));
            }
        ring.Add((to, er));
        for (double s = to - 0.5; s > from; s -= 0.5) ring.Add((s, edges(s).Right));
        var (sl, sr) = edges(from);
        ring.Add((from, sr));
        if (p.RoundFrom)
            for (int k = 1; k < Steps; k++)
            {
                double t = Math.PI / 2 + Math.PI * k / Steps;
                ring.Add((from + rFrom * Math.Cos(t), (sl + sr) * 0.5 + (sr - sl) * 0.5 * Math.Sin(t)));
            }
        if (Math.Abs(ring[0].Item2 - sl) > 1e-6 || Math.Abs(ring[0].Item1 - from) > 1e-6) ring.Add((from, sl));
        // (no point twice in a row, nor the first again at the end)
        var outline = ring.Where((q, i) => i == 0 || Math.Abs(q.Item1 - ring[i - 1].Item1) + Math.Abs(q.Item2 - ring[i - 1].Item2) > 1e-6).ToList();
        if (outline.Count > 1 && Math.Abs(outline[^1].Item1 - outline[0].Item1) + Math.Abs(outline[^1].Item2 - outline[0].Item2) < 1e-6) outline.RemoveAt(outline.Count - 1);
        return outline;
    }
}
