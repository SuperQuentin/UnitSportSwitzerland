namespace UnitSport.Tools.RoadGen.Network;

using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Rewrite;

/// <summary>
/// Width, lanes, one-way and priority of every carriageway (#117), decided on the raw extractor
/// lines of a block (plus halo) BEFORE the geometry pipeline, because the width sizes the junction
/// trims and the motorway offset moves the centrelines the graph is built from.
///
/// <para>
/// Rules (docs/notes/tools/road-widths-lanes-oneway.md):
/// width = TLM nominal class width (10/8/6/4/3 m), or built from lanes for motorways, expressways
/// and ramps (<see cref="RoadCrossSection"/>), OSM <c>width</c> when plausible;
/// lanes from the class, OSM <c>lanes</c> when present;
/// one-way: roundabout rings counter-clockwise, divided carriageways from the partner side
/// (right-hand traffic), then from connectivity (ramps, lines with no partner), else OSM;
/// priority: TLM <c>verkehrsbedeutung</c> + class (extractor), raised by OSM <c>highway</c>
/// where TLM has no importance.
/// </para>
///
/// <para>
/// Motorway median: swissTLM3D draws the two carriageways of a motorway a median 2.3 m apart
/// (Martigny-Sion), where OSM, traced on orthophotos, has them 9.9 m apart: each OSM carriageway
/// lies 3.8 m outward of its TLM line. A real carriageway (2 x 3.75 m + shoulder) is 10.5 m, so
/// drawn on TLM's lines the two overlap almost entirely. Each one-way high-speed carriageway is
/// shifted outward so its inner edge sits <see cref="InnerEdgeFromAxis"/> from the axis; ramps
/// and roads attached to a shifted end are dragged along and taper back over
/// <see cref="TaperM"/>; tunnels never move (their bore is carved into the terrain).
/// </para>
/// </summary>
public static class CrossSectionPlanner
{
    /// <summary>Half the measured TLM separation of the two motorway carriageways (median 2.3 m).</summary>
    public const double TlmHalfSeparation = 1.15;
    /// <summary>
    /// Inner paved edge to the axis between the carriageways. From the OSM measurement: lanes
    /// middle 3.8 m outward of TLM gives an inner lane edge 1.2 m, inner paved edge (0.5 m margin)
    /// 0.7 m from the axis.
    /// </summary>
    public const double InnerEdgeFromAxis = 0.7;
    /// <summary>Length over which a dragged or pinned end returns to its own line.</summary>
    public const double TaperM = 60.0;

    /// <summary>Off (<c>--rewrite --no-shift</c>) keeps every centreline where TLM drew it: the "before" of the median measurement.</summary>
    public static bool ShiftCarriageways { get; set; } = true;

    public enum Origin : byte { None, Source, Roundabout, Partner, Connectivity, PartnerFallback, Osm }

    /// <summary>One raw extractor segment of the block or its halo.</summary>
    public sealed class Line
    {
        public required TileId Tile { get; init; }
        public required RoadSegment Segment { get; init; }
        public RawRoads.Key? Key { get; init; }
        /// <summary>In the block (written), not halo.</summary>
        public bool Write { get; init; }

        public Vec2[] Plan { get; internal set; } = [];
        public OsmOverlayReader.Row? Osm { get; internal set; }

        // decisions
        public sbyte OneWay { get; internal set; }
        public Origin Why { get; internal set; }
        public int LanesOneWay { get; internal set; }
        public int LanesFwd { get; internal set; }
        public int LanesBwd { get; internal set; }
        /// <summary>An odd OSM lane count with no direction split (#711), 0 none: the extra lane goes toward a junction.</summary>
        public int OddOsmLanes { get; internal set; }
        public float Width { get; internal set; }
        public byte Priority { get; internal set; }
        /// <summary>The plan the geometry is built from: <see cref="Plan"/>, shifted for a motorway carriageway.</summary>
        public Vec2[] Shifted { get; internal set; } = [];
        public bool IsShifted { get; internal set; }
        /// <summary>Bike infrastructure wanted here (#120: a candidate with no parallel alternative).</summary>
        public bool BikeWanted { get; internal set; }
        /// <summary>Painted bike lane width (dm) the carriageway holds, 0 none (#120).</summary>
        public byte BikeLaneDm { get; internal set; }
        public BikePlanner.Why BikeWhy { get; internal set; }

        internal sbyte PartnerVote;
        internal double Length;
        internal bool OsmLanes, OsmWidth, OsmPriority;
        /// <summary>More lanes than the class default (#700) and the carriageway wider than its class width for them.</summary>
        internal bool Crowded, LaneWidened;
        internal int CrowdedLanes;
        internal float ClassWidth;
    }

    /// <summary>Region numbers, over written lines only.</summary>
    public sealed class Stats
    {
        public int Divided, DividedPartner, DividedConnectivity, DividedFallback, DividedOsm, DividedNone;
        public int Ramps, RampsOsm, RampsConnectivity, RampsFallback, RampsBoth, RampsAgree, OsmChecked, OsmAgree;
        public int RoundaboutLines, OsmOneWay, OsmLanes, OsmWidth, OsmPriority, ConnectivityConflicts;
        public int ShiftedLines, PinnedEnds, DraggedLines, LooseAttachments;
        public double ShiftedKm;
        /// <summary>km per (class, width to 0.1 m).</summary>
        public readonly SortedDictionary<(RoadClass, double), double> WidthKm = new();
        /// <summary>Lines with more lanes than the class default (#700): per (class, lanes, class width m, width m) lines and km, and how many OSM widths were refused for leaving a lane under MinCarLane.</summary>
        public readonly SortedDictionary<(RoadClass, string, double, double), (int Lines, double Km)> CrowdedKm = new();
        public int CrowdedLines, CrowdedWidened;
        public readonly SortedSet<string> CrowdedTiles = new();

        public string Format()
        {
            var c = CultureInfo.InvariantCulture;
            string Pct(int n, int of) => of == 0 ? "-" : (100.0 * n / of).ToString("F1", c) + "%";
            var sb = new StringBuilder();
            sb.Append(c, $"  carriageways (#117, raw lines):\n");
            sb.Append(c, $"    divided {Divided:N0}: direction {Divided - DividedNone:N0} ({Pct(Divided - DividedNone, Divided)}) = partner {DividedPartner:N0}, connectivity {DividedConnectivity:N0}, partner fallback {DividedFallback:N0}, OSM {DividedOsm:N0}; none {DividedNone:N0}\n");
            sb.Append(c, $"    check against OSM one-way on divided lines (OSM rows left after the overlay's conflict rule): agree {OsmAgree}/{OsmChecked}\n");
            sb.Append(c, $"    ramps {Ramps:N0}: OSM {RampsOsm:N0}, connectivity {RampsConnectivity:N0} (partner rule agrees {RampsAgree}/{RampsBoth}), partner fallback {RampsFallback:N0}\n");
            sb.Append(c, $"    roundabout lines oriented {RoundaboutLines:N0}; connectivity conflicts {ConnectivityConflicts}\n");
            sb.Append(c, $"    OSM overrides: one-way {OsmOneWay:N0}, lanes {OsmLanes:N0}, width {OsmWidth:N0}, priority {OsmPriority:N0}\n");
            sb.Append(c, $"    motorway offset: {ShiftedLines:N0} lines ({ShiftedKm:F1} km) shifted, {PinnedEnds} ends pinned at tunnels, {DraggedLines} attached lines dragged, {LooseAttachments} ends touching a shifted line off its nodes\n");
            sb.Append(c, $"    lanes above the class default (#700): {CrowdedLines:N0} lines, {CrowdedWidened:N0} wider than their class width; class, lanes (back+fwd), class width -> width: lines, km\n");
            foreach (var (k, v) in CrowdedKm)
                sb.Append(c, $"      {k.Item1,-8} {k.Item2,-10} {k.Item3:0.0} -> {k.Item4:0.0} m: {v.Lines:N0} lines, {v.Km:F2} km\n");
            sb.Append(c, $"      tiles with such a line: {string.Join(" ", CrowdedTiles)}\n");
            sb.Append("    width histogram, km per class (width m: km):\n");
            foreach (var cls in WidthKm.Keys.Select(k => k.Item1).Distinct())
                sb.Append(c, $"      {cls,-10} ").Append(string.Join("  ", WidthKm.Where(kv => kv.Key.Item1 == cls)
                    .Select(kv => string.Create(c, $"{kv.Key.Item2:0.0}: {kv.Value:F1}")))).Append('\n');
            return sb.ToString();
        }
    }

    public static bool IsCarRoad(RoadSegment s) =>
        (s.Flags & RoadFlags.Stairs) == 0
        && s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp or RoadClass.Major
            or RoadClass.Road or RoadClass.Minor or RoadClass.Lane;

    public static void Plan(List<Line> lines, OsmOverlayReader? overlay, Stats stats)
    {
        foreach (var line in lines) Prepare(line, overlay);

        var ends = new Dictionary<(long, long), List<(Line Line, bool AtStart)>>();
        foreach (var line in lines)
        {
            Add(ends, Key(line.Plan[0]), (line, true));
            Add(ends, Key(line.Plan[^1]), (line, false));
        }

        OddLanesTowardJunctions(lines, ends);
        OrientRoundabouts(lines, ends);

        var divided = lines.Where(l => TileRewriter.IsDividedCarRoad(l.Segment)).ToList();
        var partners = new PartnerGrid(divided.Select(l => (l.Plan, l.Segment.Class)));
        foreach (var line in divided) line.PartnerVote = partners.OneWay(line.Plan, line.Segment.Class);
        foreach (var line in divided)
            if (line.OneWay == 0 && line.Segment.Class != RoadClass.Ramp && line.PartnerVote != 0)
                Set(line, line.PartnerVote, Origin.Partner);

        // ramps: OSM first where it maps them (checked at Riddes: the partner rule is a coin toss
        // inside an interchange, and the overlay already dropped OSM directions contradicting a
        // carriageway's partner side), so connectivity then starts from right answers
        foreach (var line in divided)
            if (line.OneWay == 0 && line.Segment.Class == RoadClass.Ramp && line.Osm is { OneWay: "1" or "-1" } r)
                Set(line, (sbyte)(r.OneWay == "1" ? 1 : -1), Origin.Osm);

        int conflicts = Propagate(divided, ends);

        foreach (var line in divided)
            if (line.OneWay == 0 && line.PartnerVote != 0) Set(line, line.PartnerVote, Origin.PartnerFallback);

        foreach (var line in lines)
            if (line.OneWay == 0 && line.Osm is { OneWay: "1" or "-1" } row && IsCarRoad(line.Segment))
                Set(line, (sbyte)(row.OneWay == "1" ? 1 : -1), Origin.Osm);

        Shift(lines, ends, stats);

        stats.ConnectivityConflicts += conflicts;
        foreach (var line in lines) if (line.Write) Count(line, stats);
    }

    /// <summary>
    /// An odd OSM lane count with no <c>lanes:forward/backward</c> (#711, the user's rule): the extra lane goes to the traffic
    /// arriving at a junction (the usual case: a turn lane before it), at the end where more car roads meet; where neither
    /// end is a junction, or both alike, the fixed split stays (the extra lane against the drawing).
    /// </summary>
    private static void OddLanesTowardJunctions(List<Line> lines, Dictionary<(long, long), List<(Line Line, bool AtStart)>> ends)
    {
        int Arms(Vec2 p) => ends.TryGetValue(Key(p), out var list) ? list.Count(e => IsCarRoad(e.Line.Segment)) : 0;
        foreach (var line in lines)
        {
            if (line.OddOsmLanes is not (int n and >= 3)) continue;
            int atStart = Arms(line.Plan[0]), atEnd = Arms(line.Plan[^1]);
            // forward traffic arrives at the end, backward at the start
            if (atEnd >= 3 && atEnd > atStart) (line.LanesFwd, line.LanesBwd) = (n - n / 2, n / 2);
            else if (atStart >= 3 && atStart > atEnd) (line.LanesFwd, line.LanesBwd) = (n / 2, n - n / 2);
        }
    }

    /// <summary>The v3 attributes of a piece of <paramref name="line"/>, after the OSM row of that piece (if any) was applied.</summary>
    public static RoadAttributes Finish(RoadAttributes a, Line line)
    {
        int fwd, bwd;
        if (a.OneWay > 0) (fwd, bwd) = (line.LanesOneWay, 0);
        else if (a.OneWay < 0) (fwd, bwd) = (0, line.LanesOneWay);
        else (fwd, bwd) = (line.LanesFwd, line.LanesBwd);
        return BikePlanner.Apply(a with
        {
            LanesForward = (byte)Math.Min(fwd, 15), LanesBackward = (byte)Math.Min(bwd, 15),
            WidthCm = (ushort)Math.Round(line.Width * 100), Priority = line.Priority,
        }, line);
    }

    /// <summary>Attributes of the whole line, before any piece-level OSM row.</summary>
    public static RoadAttributes Attributes(Line line) =>
        Finish(line.Segment.Attributes with { OneWay = line.OneWay }, line);

    // ---- per line: plan, OSM row, lanes, width, priority -------------------------------------

    private static void Prepare(Line line, OsmOverlayReader? overlay)
    {
        var s = line.Segment;
        var id = line.Tile;
        var plan = new Vec2[s.PointCount];
        for (int i = 0; i < plan.Length; i++)
            plan[i] = new Vec2(id.MinE + s.Points[i * 3], id.MaxN - s.Points[i * 3 + 2]);
        line.Plan = plan;
        line.Shifted = plan;
        for (int i = 1; i < plan.Length; i++) line.Length += plan[i].DistanceTo(plan[i - 1]);

        if (overlay is not null && line.Key is { } key)
            line.Osm = overlay.Best(key.Uuid, key.Part, key.FromM, key.FromM + line.Length);
        var row = line.Osm;
        var a = s.Attributes;
        line.OneWay = a.OneWay;   // a source that already knows (France) wins
        if (a.OneWay != 0) line.Why = Origin.Source;

        // lanes: what the source says, else OSM, else the class
        bool oneWayCarriageway = (s.Flags & RoadFlags.Divided) != 0;
        int osmLanes = Int(row?.Lanes), osmFwd = Int(row?.LanesFwd), osmBwd = Int(row?.LanesBwd);
        int oneWayLanes = RoadCrossSection.DefaultLanes(s.Class, true);
        int each = RoadCrossSection.DefaultLanes(s.Class, false);
        int fwd = each, bwd = each;
        if (a.LanesForward + a.LanesBackward > 0)
        {
            (fwd, bwd) = (a.LanesForward, a.LanesBackward);
            oneWayLanes = Math.Max(fwd, bwd);
        }
        else if (osmLanes > 0 || osmFwd + osmBwd > 0)
        {
            line.OsmLanes = true;
            if (osmFwd + osmBwd > 0) (fwd, bwd) = (osmFwd, osmBwd);
            else if (row!.OneWay is "1" or "-1" || oneWayCarriageway) oneWayLanes = osmLanes;
            else if (osmLanes >= 2)
            {
                (fwd, bwd) = (osmLanes / 2, osmLanes - osmLanes / 2);
                if (osmLanes % 2 == 1) line.OddOsmLanes = osmLanes;
            }
            if (row!.OneWay is "1" or "-1" || oneWayCarriageway) oneWayLanes = Math.Max(osmLanes, Math.Max(osmFwd, osmBwd));
        }
        oneWayLanes = Math.Clamp(oneWayLanes, 0, 6);
        line.LanesOneWay = oneWayLanes;
        (line.LanesFwd, line.LanesBwd) = (Math.Clamp(fwd, 0, 6), Math.Clamp(bwd, 0, 6));

        // width: lanes for the high-speed classes, TLM's nominal class width for the rest; an ordinary
        // road with more lanes than its class default is at least lanes x LaneWidth wide (#700)
        bool oneWayRoad = oneWayCarriageway || a.OneWay != 0 || row?.OneWay is "1" or "-1";
        // lanes in one direction only (OSM lanes:backward=2 and nothing forward) or a roundabout ring: the line turns out
        // one-way later (ring orientation, connectivity), so it carries those lanes, not one more the other way
        bool oneSided = !oneWayRoad && ((fwd == 0) != (bwd == 0) || row?.Roundabout == true || a.Has(RoadAttrFlags.Roundabout));
        int crowdedLanes = oneWayRoad ? oneWayLanes : oneSided ? Math.Max(fwd, bwd) : Math.Max(1, fwd) + Math.Max(1, bwd);   // as the paint counts them
        // not a divided carriageway: TLM draws the pair a few metres apart and ordinary roads are not shifted apart (#117), so lanes-wide halves would overlap
        bool crowded = !RoadCrossSection.IsHighSpeed(s.Class) && IsCarRoad(s) && !oneWayCarriageway
            && (oneWayRoad || oneSided ? crowdedLanes > 1 : fwd > 1 || bwd > 1);
        float width;
        if (RoadCrossSection.IsHighSpeed(s.Class))
            width = oneWayCarriageway
                ? RoadCrossSection.OneWayWidth(s.Class, Math.Max(1, oneWayLanes))
                : RoadCrossSection.TwoWayWidth(s.Class, Math.Max(1, Math.Max(fwd, bwd)));
        else if (a.WidthCm > 0 && !oneWayCarriageway && IsCarRoad(s))
            width = a.WidthCm / 100f;
        else
            width = s.Width;   // divided ordinary roads keep the extractor's halved width; paths etc. their class width
        float classWidth = width;
        if (crowded) width = Math.Max(width, crowdedLanes * RoadCrossSection.LaneWidth(s.Class));
        if (double.TryParse(row?.Width, NumberStyles.Float, CultureInfo.InvariantCulture, out double osmWidth)
            && osmWidth >= 0.6 * width && osmWidth <= 1.6 * width && IsCarRoad(s)
            && (!crowded || osmWidth >= crowdedLanes * BikePlanner.MinCarLane))
        {
            width = (float)osmWidth;
            line.OsmWidth = true;
        }
        (line.Crowded, line.CrowdedLanes, line.ClassWidth) = (crowded, crowdedLanes, classWidth);
        line.LaneWidened = crowded && width > classWidth + 0.01f;
        line.Width = width;

        // priority: TLM verkehrsbedeutung + class (extractor); OSM's road hierarchy where TLM has none
        byte priority = a.Priority;
        if (priority >> 4 == 0 && row is not null)
        {
            int importance = row.Highway switch
            {
                "motorway" or "trunk" or "primary" => 2,
                "secondary" or "tertiary" => 1,
                _ => 0,
            };
            if (importance > 0) { priority |= (byte)(importance << 4); line.OsmPriority = true; }
        }
        line.Priority = priority;
    }

    // ---- one-way ------------------------------------------------------------------------------

    private static void Set(Line line, sbyte oneWay, Origin why)
    {
        line.OneWay = oneWay;
        line.Why = why;
    }

    /// <summary>
    /// Roundabout rings run counter-clockwise (right-hand traffic, #122). Ring lines are grouped by
    /// shared ends; each line's direction is the one that turns it counter-clockwise about the
    /// centroid of its ring (for a single arc, the centroid still lies on its inner side).
    /// </summary>
    private static void OrientRoundabouts(List<Line> lines, Dictionary<(long, long), List<(Line Line, bool AtStart)>> ends)
    {
        static bool IsRing(Line l) => IsCarRoad(l.Segment)
            && (l.Segment.Attributes.Has(RoadAttrFlags.Roundabout) || l.Osm is { Roundabout: true });
        var seen = new HashSet<Line>(ReferenceEqualityComparer.Instance);
        foreach (var start in lines)
        {
            if (!IsRing(start) || !seen.Add(start)) continue;
            var ring = new List<Line> { start };
            for (int i = 0; i < ring.Count; i++)
                foreach (var p in new[] { ring[i].Plan[0], ring[i].Plan[^1] })
                    foreach (var (other, _) in ends[Key(p)])
                        if (IsRing(other) && seen.Add(other)) ring.Add(other);

            var centre = Vec2.Zero;
            int n = 0;
            foreach (var l in ring) foreach (var p in l.Plan) { centre += p; n++; }
            centre /= n;
            foreach (var l in ring)
            {
                if (l.OneWay != 0 && l.Why == Origin.Source) continue;
                double turn = 0;
                for (int i = 1; i < l.Plan.Length; i++)
                {
                    var r = l.Plan[i - 1] - centre;
                    var t = l.Plan[i] - l.Plan[i - 1];
                    turn += r.X * t.Y - r.Y * t.X;   // > 0: counter-clockwise (X east, Y north)
                }
                if (turn != 0) Set(l, (sbyte)(turn > 0 ? 1 : -1), Origin.Roundabout);
            }
        }
    }

    /// <summary>
    /// Directions from the network: a divided line (or ramp) with no direction takes it from the
    /// one-way lines it meets. At each end, the known line whose outward heading is most nearly
    /// parallel (diverging or merging alongside, angle under 45 deg: same sense) or anti-parallel
    /// (carrying straight on, over 135 deg: opposite sense) decides. Repeats until nothing changes.
    /// An exit ramp leaves the node next to the carriageway that leaves it, so it leaves too.
    /// </summary>
    private static int Propagate(List<Line> divided, Dictionary<(long, long), List<(Line Line, bool AtStart)>> ends)
    {
        int conflicts = 0;
        for (int pass = 0; pass < 50; pass++)
        {
            bool changed = false;
            foreach (var line in divided)
            {
                if (line.OneWay != 0) continue;
                var votes = new List<(double Score, sbyte Dir)>();
                foreach (bool atStart in new[] { true, false })
                {
                    var p = atStart ? line.Plan[0] : line.Plan[^1];
                    var outward = Outward(line, atStart);
                    foreach (var (v, vStart) in ends[Key(p)])
                    {
                        if (ReferenceEquals(v, line) || v.OneWay == 0 || !IsCarRoad(v.Segment)) continue;
                        bool vLeaves = vStart == v.OneWay > 0;
                        var vOut = Outward(v, vStart);
                        double cos = outward.Dot(vOut);
                        bool leaves;
                        double score;
                        if (cos > 0.707)
                        {
                            // alongside: a ramp diverges or merges on the RIGHT of the traffic it
                            // joins (same sense); the other carriageway lies on its LEFT (opposite)
                            var travel = vLeaves ? vOut : -vOut;
                            var d = Ahead(line, atStart) - Ahead(v, vStart);
                            bool right = travel.X * d.Y - travel.Y * d.X < 0;
                            (leaves, score) = (right ? vLeaves : !vLeaves, 1 - cos);
                        }
                        else if (cos < -0.707) (leaves, score) = (!vLeaves, 1 + cos);
                        else continue;
                        // a motorway decides before a ramp before a road
                        votes.Add((score + 0.01 * (int)v.Segment.Class, (sbyte)(leaves == atStart ? 1 : -1)));
                    }
                }
                if (votes.Count == 0) continue;
                var best = votes.MinBy(x => x.Score).Dir;
                if (votes.Any(x => x.Dir != best)) conflicts++;
                Set(line, best, Origin.Connectivity);
                changed = true;
            }
            if (!changed) break;
        }
        return conflicts;
    }

    /// <summary>The point ~10 m in from the line's end (where <see cref="Outward"/> aims).</summary>
    private static Vec2 Ahead(Line line, bool atStart) =>
        (atStart ? line.Plan[0] : line.Plan[^1]) + Outward(line, atStart) * Math.Min(10, line.Length);

    /// <summary>Unit heading pointing away from the line's end, over its first ~10 m.</summary>
    private static Vec2 Outward(Line line, bool atStart)
    {
        var plan = line.Plan;
        var from = atStart ? plan[0] : plan[^1];
        var to = from;
        for (int k = 1; k < plan.Length; k++)
        {
            to = atStart ? plan[k] : plan[^(k + 1)];
            if (to.DistanceTo(from) >= 10) break;
        }
        var d = to - from;
        return d.LengthSquared > 1e-12 ? d.Normalized() : new Vec2(1, 0);
    }

    // ---- motorway offset ----------------------------------------------------------------------

    private static bool Shifts(Line l) =>
        ShiftCarriageways && l.OneWay != 0 && (l.Segment.Flags & RoadFlags.Divided) != 0
        && l.Segment.Class is RoadClass.Motorway or RoadClass.Expressway
        && (l.Segment.Flags & RoadFlags.Tunnel) == 0;

    private static bool Pinned(Line l) => (l.Segment.Flags & RoadFlags.Tunnel) != 0;

    private static void Shift(List<Line> lines, Dictionary<(long, long), List<(Line Line, bool AtStart)>> ends, Stats stats)
    {
        // 1. each carriageway's own shift, tapered to nothing at a tunnel
        var own = new Dictionary<Line, Vec2[]>(ReferenceEqualityComparer.Instance);
        foreach (var line in lines)
        {
            if (!Shifts(line)) continue;
            double amount = line.Width * 0.5 + InnerEdgeFromAxis - TlmHalfSeparation;
            var plan = line.Plan;
            var d = new Vec2[plan.Length];
            bool pinStart = ends[Key(plan[0])].Any(e => Pinned(e.Line));
            bool pinEnd = ends[Key(plan[^1])].Any(e => Pinned(e.Line));
            if (line.Write) stats.PinnedEnds += (pinStart ? 1 : 0) + (pinEnd ? 1 : 0);
            double taper = Math.Min(TaperM, line.Length), s = 0;
            for (int i = 0; i < plan.Length; i++)
            {
                if (i > 0) s += plan[i].DistanceTo(plan[i - 1]);
                double f = 1;
                if (pinStart) f = Math.Min(f, s / taper);
                if (pinEnd) f = Math.Min(f, (line.Length - s) / taper);
                // right of the drawing direction, flipped for traffic against it: always outward
                d[i] = Normal(plan, i) * (amount * Math.Clamp(f, 0, 1) * line.OneWay);
            }
            own[line] = d;
        }

        // 2. one displacement per node: the shifted lines meeting there agree on it
        var node = new Dictionary<(long, long), Vec2>();
        foreach (var (key, list) in ends)
        {
            var sum = Vec2.Zero;
            int n = 0;
            foreach (var (l, atStart) in list)
                if (own.TryGetValue(l, out var d)) { sum += atStart ? d[0] : d[^1]; n++; }
            if (n > 0) node[key] = sum / n;
        }

        foreach (var line in lines)
        {
            var plan = line.Plan;
            if (own.TryGetValue(line, out var d))
            {
                d[0] = node[Key(plan[0])];
                d[^1] = node[Key(plan[^1])];
                Apply(line, d);
                if (line.Write) { stats.ShiftedLines++; stats.ShiftedKm += line.Length / 1000; }
                continue;
            }
            if (Pinned(line)) continue;

            // 3. anything else attached to a shifted node is dragged along, tapering back
            node.TryGetValue(Key(plan[0]), out var a);
            node.TryGetValue(Key(plan[^1]), out var b);
            if (a.LengthSquared < 1e-6 && b.LengthSquared < 1e-6) continue;
            var drag = new Vec2[plan.Length];
            double taper = Math.Min(TaperM, line.Length), s = 0;
            bool lerp = line.Length < 2 * TaperM;
            for (int i = 0; i < plan.Length; i++)
            {
                if (i > 0) s += plan[i].DistanceTo(plan[i - 1]);
                double t = line.Length < 1e-9 ? 0 : s / line.Length;
                drag[i] = lerp ? a * (1 - t) + b * t
                    : a * Math.Max(0, 1 - s / taper) + b * Math.Max(0, 1 - (line.Length - s) / taper);
            }
            Apply(line, drag);
            if (line.Write) stats.DraggedLines++;
        }

        // ends touching a shifted line away from its nodes (a T onto the carriageway): reported only
        var shifted = lines.Where(l => own.ContainsKey(l)).ToList();
        foreach (var line in lines)
        {
            if (!line.Write || own.ContainsKey(line) || !IsCarRoad(line.Segment)) continue;
            foreach (var p in new[] { line.Plan[0], line.Plan[^1] })
                if (!node.ContainsKey(Key(p)) && shifted.Any(sl => !ReferenceEquals(sl, line) && Near(sl.Plan, p, 1.0)))
                    stats.LooseAttachments++;
        }
    }

    private static void Apply(Line line, Vec2[] d)
    {
        var moved = new Vec2[line.Plan.Length];
        for (int i = 0; i < moved.Length; i++) moved[i] = line.Plan[i] + d[i];
        line.Shifted = moved;
        line.IsShifted = true;
    }

    /// <summary>Unit right-hand normal of the drawing direction at vertex i (mitred, capped at 2x).</summary>
    private static Vec2 Normal(Vec2[] plan, int i)
    {
        Vec2 Seg(int a) => (plan[a + 1] - plan[a]).LengthSquared > 1e-12 ? (plan[a + 1] - plan[a]).Normalized() : Vec2.Zero;
        var t = (i > 0 ? Seg(i - 1) : Vec2.Zero) + (i < plan.Length - 1 ? Seg(i) : Vec2.Zero);
        if (t.LengthSquared < 1e-12) return Vec2.Zero;
        var tn = t.Normalized();
        var right = new Vec2(tn.Y, -tn.X);
        // keep the offset perpendicular to both neighbouring segments: scale by 1/cos(half turn)
        var s0 = i < plan.Length - 1 ? Seg(i) : Seg(i - 1);
        double cos = Math.Abs(new Vec2(s0.Y, -s0.X).Dot(right));
        return right * (1 / Math.Max(0.5, cos));
    }

    private static bool Near(Vec2[] plan, Vec2 p, double r)
    {
        for (int i = 1; i < plan.Length; i++)
        {
            var a = plan[i - 1];
            var ab = plan[i] - a;
            double t = ab.LengthSquared < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared, 0, 1);
            if (p.DistanceTo(a + ab * t) <= r) return true;
        }
        return false;
    }

    // ---- stats --------------------------------------------------------------------------------

    private static void Count(Line line, Stats st)
    {
        var s = line.Segment;
        if (TileRewriter.IsDividedCarRoad(s))
        {
            st.Divided++;
            switch (line.Why)
            {
                case Origin.Partner: st.DividedPartner++; break;
                case Origin.Connectivity: st.DividedConnectivity++; break;
                case Origin.PartnerFallback: st.DividedFallback++; break;
                case Origin.Osm: st.DividedOsm++; break;
                case Origin.None: st.DividedNone++; break;
                default: if (line.OneWay == 0) st.DividedNone++; else st.DividedPartner++; break;
            }
            if (line.Why != Origin.Osm && line.OneWay != 0 && line.Osm is { OneWay: "1" or "-1" } row)
            {
                st.OsmChecked++;
                if ((row.OneWay == "1") == (line.OneWay > 0)) st.OsmAgree++;
            }
            if (s.Class == RoadClass.Ramp)
            {
                st.Ramps++;
                if (line.Why == Origin.Osm) st.RampsOsm++;
                if (line.Why == Origin.PartnerFallback) st.RampsFallback++;
                if (line.Why == Origin.Connectivity)
                {
                    st.RampsConnectivity++;
                    if (line.PartnerVote != 0) { st.RampsBoth++; if (line.PartnerVote == line.OneWay) st.RampsAgree++; }
                }
            }
        }
        if (line.Why == Origin.Roundabout) st.RoundaboutLines++;
        if (line.Why == Origin.Osm) st.OsmOneWay++;
        if (line.OsmLanes) st.OsmLanes++;
        if (line.OsmWidth) st.OsmWidth++;
        if (line.OsmPriority) st.OsmPriority++;
        if (line.Crowded)
        {
            st.CrowdedLines++;
            st.CrowdedTiles.Add(line.Tile.ToString());
            if (line.LaneWidened) st.CrowdedWidened++;
            var ck = (s.Class, line.OneWay != 0 || (s.Flags & RoadFlags.Divided) != 0 ? $"{line.CrowdedLanes} one-way" : $"{line.LanesBwd}+{line.LanesFwd}", Math.Round((double)line.ClassWidth, 1), Math.Round(line.Width, 1));
            var (n, km) = st.CrowdedKm.GetValueOrDefault(ck);
            st.CrowdedKm[ck] = (n + 1, km + line.Length / 1000);
        }
        if (s.Class <= RoadClass.Square)
        {
            var k = (s.Class, Math.Round(line.Width, 1));
            st.WidthKm[k] = st.WidthKm.GetValueOrDefault(k) + line.Length / 1000;
        }
    }

    // ---- self-check ---------------------------------------------------------------------------

    /// <summary>
    /// <c>RoadGen --plan-check</c>: a motorway pair 2.3 m apart (TLM's spacing) running east, an
    /// exit ramp leaving the southern carriageway, a roundabout drawn clockwise and an 8 m road.
    /// Fails if a direction, a width, the outward shift or the ramp drag breaks.
    /// </summary>
    public static bool SelfCheck(Action<string> log)
    {
        var tile = new TileId(2600, 1200);
        Line Make(RoadClass c, RoadFlags f, RoadAttributes a, params (double E, double N)[] corners)
        {
            // densified every <= 4 m like the extractor's output: the partner rule votes with vertices
            var pts = new List<(double E, double N)> { corners[0] };
            for (int k = 1; k < corners.Length; k++)
            {
                var (p, q) = (corners[k - 1], corners[k]);
                int n = (int)Math.Ceiling(Math.Sqrt((q.E - p.E) * (q.E - p.E) + (q.N - p.N) * (q.N - p.N)) / 4);
                for (int j = 1; j <= n; j++) pts.Add((p.E + (q.E - p.E) * j / n, p.N + (q.N - p.N) * j / n));
            }
            var points = new float[pts.Count * 3];
            for (int i = 0; i < pts.Count; i++)
                (points[i * 3], points[i * 3 + 2]) = ((float)(pts[i].E - tile.MinE), (float)(tile.MaxN - pts[i].N));
            return new Line
            {
                Tile = tile, Write = true,
                Segment = new RoadSegment { Class = c, Flags = f, Width = RoadFormat.WidthFor(c, f), Points = points, Attributes = a },
            };
        }
        var div = RoadFlags.Divided;
        // north carriageway drawn west -> east (partner on its right: traffic runs west),
        // south one drawn east -> west (partner on its right too: traffic runs east)
        var north = Make(RoadClass.Motorway, div, default, (2600100, 1199501.15), (2600900, 1199501.15));
        var southA = Make(RoadClass.Motorway, div, default, (2600900, 1199498.85), (2600500, 1199498.85));
        var south = Make(RoadClass.Motorway, div, default, (2600500, 1199498.85), (2600100, 1199498.85));
        // exit ramp from the node at E 2600500: the south carriageway runs east, so a ramp
        // diverging on its right heads east-south-east
        var ramp = Make(RoadClass.Ramp, div, default, (2600500, 1199498.85), (2600560, 1199490), (2600700, 1199450));
        var ring = Make(RoadClass.Road, RoadFlags.None, new RoadAttributes(Flags: RoadAttrFlags.Roundabout),
            (2600300, 1199700), (2600320, 1199680), (2600300, 1199660), (2600280, 1199680), (2600300, 1199700));
        var road = Make(RoadClass.Major, RoadFlags.None, new RoadAttributes(WidthCm: 800), (2600100, 1199800), (2600900, 1199800));
        var lines = new List<Line> { north, southA, south, ramp, ring, road };
        var stats = new Stats();
        Plan(lines, null, stats);

        bool ok = true;
        void Check(bool cond, string what) { log((cond ? "  ok    " : "  FAIL  ") + what); ok &= cond; }
        Check(north.OneWay == -1 && south.OneWay == -1, "partner rule at 2.3 m: both carriageways run against their drawing (west / east)");
        Check(ramp.OneWay == 1 && ramp.Why == Origin.Connectivity, "exit ramp leaves the node beside the carriageway it diverges from");
        Check(ring.OneWay == -1, "a ring drawn clockwise runs against its drawing (counter-clockwise)");
        Check(Math.Abs(north.Width - 10.5f) < 0.01f && Math.Abs(ramp.Width - 5.75f) < 0.01f && Math.Abs(road.Width - 8f) < 0.01f,
            "widths: motorway carriageway 10.5 m, ramp 5.75 m, 8m Strasse 8 m");
        double gap = north.Shifted[0].Y - south.Shifted[^1].Y - north.Width;
        Check(Math.Abs(gap - 2 * InnerEdgeFromAxis) < 0.01, $"carriageways shifted apart, paved gap {gap:F2} m");
        Check(ramp.Shifted[0].DistanceTo(south.Shifted[0]) < 0.01 && ramp.Shifted[0].DistanceTo(ramp.Plan[0]) > 4 && ramp.Shifted[^1].DistanceTo(ramp.Plan[^1]) < 0.01,
            "ramp end dragged with the carriageway node, far end untouched");
        var a = Finish(new RoadAttributes(OneWay: north.OneWay), north);
        Check(a.LanesForward == 0 && a.LanesBackward == 2 && a.WidthCm == 1050, "one-way lanes all in the travel direction");
        Check(Math.Abs(RoadCrossSection.RightLaneOffset(RoadClass.Motorway, 10.5f, 2) - 0.875f) < 1e-4
            && RoadCrossSection.RightLaneOffset(RoadClass.Motorway, 6.05f, 2) == 0, "right lane 0.875 m right of a 10.5 m carriageway's centre; 0 on v2 widths");
        return ok;
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static (long, long) Key(Vec2 p) => ((long)Math.Round(p.X * 2), (long)Math.Round(p.Y * 2));

    private static void Add<T>(Dictionary<(long, long), List<T>> map, (long, long) key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
        list.Add(value);
    }

    private static int Int(string? s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// <summary>
    /// Direction of one carriageway of a divided road, from where its partner lies. swissTLM3D
    /// records none; Switzerland drives on the right, so each carriageway runs with the other on
    /// its LEFT. <c>LaneGraph.OrientDivided</c> (the runtime fallback for v1/v2 tiles) at build
    /// time: vote with every partner point beside the line's middle, 3..30 m to the side and
    /// within 25 m along, on the raw lines. Exact band query, independent of the world origin.
    /// </summary>
    private sealed class PartnerGrid
    {
        private const double Cell = 30;
        private readonly Dictionary<(long, long), List<(int Line, Vec2 P)>> _cells = new();
        private readonly Dictionary<Vec2[], int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly List<RoadClass> _class = new();

        public PartnerGrid(IEnumerable<(Vec2[] Plan, RoadClass Class)> lines)
        {
            foreach (var (line, cls) in lines)
            {
                int id = _ids.Count;
                _ids[line] = id;
                _class.Add(cls);
                foreach (var p in line)
                    Add(_cells, ((long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell)), (id, p));
            }
        }

        /// <summary>
        /// Who may be a partner: a motorway or expressway carriageway only pairs with another one
        /// (a ramp running alongside at an interchange outvoted the real partner), an ordinary
        /// road with anything but a ramp, a ramp with anything (its fallback).
        /// </summary>
        private static bool Pairs(RoadClass self, RoadClass other) => self switch
        {
            RoadClass.Motorway or RoadClass.Expressway => other is RoadClass.Motorway or RoadClass.Expressway,
            RoadClass.Ramp => true,
            _ => other != RoadClass.Ramp && !RoadCrossSection.IsHighSpeed(other),
        };

        public sbyte OneWay(Vec2[] line, RoadClass cls)
        {
            int self = _ids.TryGetValue(line, out int id) ? id : -1;
            var (mid, t) = Middle(line);
            var right = new Vec2(t.Y, -t.X);   // plan view: X east, Y north
            int votes = 0;
            long cx = (long)Math.Floor(mid.X / Cell), cy = (long)Math.Floor(mid.Y / Cell);
            for (long dx = -2; dx <= 2; dx++)
            for (long dy = -2; dy <= 2; dy++)
            {
                if (!_cells.TryGetValue((cx + dx, cy + dy), out var list)) continue;
                foreach (var (other, p) in list)
                {
                    if (other == self || !Pairs(cls, _class[other])) continue;
                    var d = p - mid;
                    double along = Math.Abs(d.Dot(t)), side = d.Dot(right);
                    if (along > 25 || Math.Abs(side) < MinSide || Math.Abs(side) > 30) continue;
                    votes += side > 0 ? -1 : 1;   // partner on the right: we run against the drawing
                }
            }
            return (sbyte)Math.Sign(votes);
        }

        /// <summary>
        /// Nearer than this is not a partner (the next piece of the same line lies ON it). The
        /// runtime rule used 3 m, but TLM draws the A9's carriageways a median 2.3 m apart, so
        /// that missed the partner wherever the two run straight (only bends voted).
        /// </summary>
        private const double MinSide = 1.0;

        private static (Vec2 Mid, Vec2 Tangent) Middle(Vec2[] line)
        {
            double total = 0;
            for (int i = 1; i < line.Length; i++) total += line[i].DistanceTo(line[i - 1]);
            double half = total * 0.5, run = 0;
            for (int i = 1; i < line.Length; i++)
            {
                double len = line[i].DistanceTo(line[i - 1]);
                if (run + len >= half && len > 1e-9)
                {
                    var dir = (line[i] - line[i - 1]) * (1 / len);
                    return (line[i - 1] + dir * (half - run), dir);
                }
                run += len;
            }
            var d = line[^1] - line[0];
            return (line[0], d.LengthSquared > 1e-18 ? d.Normalized() : new Vec2(1, 0));
        }
    }
}
