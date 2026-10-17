using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>One road or rail centreline in world space, ready to be driven along.</summary>
public sealed class LaneEdge
{
    public required Vector3[] Points { get; init; }
    /// <summary>Cumulative length to each point; <c>[^1]</c> is the whole edge.</summary>
    public required float[] Cumulative { get; init; }
    public RoadClass Class { get; init; }
    public RoadFlags Flags { get; init; }
    public float Width { get; init; }
    public long KeyStart { get; init; }
    public long KeyEnd { get; init; }

    /// <summary>
    /// Which way traffic may use it: +1 only in drawing order, −1 only against it, 0 both.
    /// Read from the tile (v3 <see cref="RoadAttributes.OneWay"/>); a divided carriageway the
    /// tile gives no direction (v1/v2) gets one from where its partner lies (see
    /// <see cref="LaneGraph.OrientDivided"/>).
    /// </summary>
    public int OneWay { get; set; }

    /// <summary>
    /// Metres right of the centreline, in the direction of travel, of the rightmost lane when the
    /// edge is one-way (<see cref="RoadCrossSection.RightLaneOffset"/>, #117); 0 for a v1/v2 tile's
    /// narrow carriageway.
    /// </summary>
    public float RightLane { get; init; }

    /// <summary>
    /// An undivided two-way road (#700): the rightmost car lane's centre, metres right of the centreline in the
    /// direction of travel (drawing order <c>Fwd</c>, against it <c>Bwd</c>), clear of a painted bike lane; the car
    /// lanes each way and one lane's width (also the lane width of a one-way edge with <see cref="OneWayLanes"/> lanes).
    /// </summary>
    public float RightLaneFwd { get; init; }
    public float RightLaneBwd { get; init; }
    public int LanesFwd { get; init; } = 1;
    public int LanesBwd { get; init; } = 1;
    public int OneWayLanes { get; init; } = 1;
    public float LaneWidth { get; init; }

    /// <summary>
    /// Traffic leaving this edge at its first (<see cref="RoadAttrFlags.YieldAtStart"/>) or last
    /// point (<see cref="RoadAttrFlags.YieldAtEnd"/>) gives way there: the side road of a junction
    /// with a main road (#121, v3 tiles; none in v1/v2).
    /// </summary>
    public RoadAttrFlags Yield { get; init; }

    /// <summary>
    /// The approach (#353) this edge's last (<see cref="ApproachAtEnd"/>) or first point
    /// (<see cref="ApproachAtStart"/>) leads into: its lanes, its traffic lights if any, and how
    /// far before that end its stop line lies. Null where none ends.
    /// </summary>
    public (LaneApproach Approach, float StopBack)? ApproachAtEnd { get; set; }
    public (LaneApproach Approach, float StopBack)? ApproachAtStart { get; set; }

    /// <summary>A straight link across a junction between two trimmed road ends (<see cref="LaneGraph.JoinTrimmedEnds"/>), or a junction path.</summary>
    public bool Connector { get; init; }

    /// <summary>
    /// A path through a junction core (#818 phase 5, <c>JPTH</c>): a lane-to-lane line the junction's paint draws, driven on its
    /// own line (no lane offset) from its lane at one arm's mouth to its lane at the exit's. Null on a road or a plain connector.
    /// </summary>
    public JunctionPathInfo? Path { get; init; }

    public float Length => Cumulative[^1];

    /// <summary>
    /// The transition this piece is (#818 phase 5, <c>TRNS</c>): its lanes from station to station, which cars follow; null for a
    /// street at its normal section.
    /// </summary>
    public RoadTransition? Transition { get; init; }

    /// <summary>A transition's lane narrower than this is not open (a pocket's taper): no car is placed in it, m.</summary>
    private const float OpenLane = 1.5f;

    /// <summary>An opening lane is a car's own from this width on; narrower, the car is between it and the lane it branches from, m.</summary>
    private const float OwnLane = 2.5f;

    /// <summary>
    /// The id of the transition's car lane, of a car travelling <paramref name="forward"/>, whose centre lies nearest
    /// <paramref name="driverRight"/> (m right of that driver) at the end it travels to (or from); -1 for none.
    /// </summary>
    public int StripAt(bool forward, bool atTravelEnd, float driverRight)
    {
        if (Transition is not { } tr) return -1;
        double s = forward == atTravelEnd ? tr.Stations[^1] : 0;
        float drawing = forward ? driverRight : -driverRight;
        int best = -1;
        float bestD = float.MaxValue;
        for (int k = 0; k < tr.Strips.Count; k++)
        {
            var x = tr.Strips[k];
            if (x.Kind != StripKind.CarLane || x.Travel != (forward ? 1 : -1) || tr.WidthAt(k, s) < OpenLane) continue;
            var (from, to) = tr.SpanAt(k, s);
            float d = Mathf.Abs((from + to) * 0.5f - drawing);
            if (d < bestD) (best, bestD) = (x.Id, d);
        }
        return best;
    }

    /// <summary>
    /// Where lane <paramref name="id"/>'s centre lies <paramref name="arc"/> m along the car's way (m right of its driver). A lane
    /// opening or closing (a pocket's taper, an exit's lane merging) takes the car from the nearest open lane of its direction
    /// (the one it branches from or merges into) over to its own as it widens: no jump where it opens. Null without the lane.
    /// </summary>
    public float? LaneAt(bool forward, float arc, int id)
    {
        if (Transition is not { } tr || Length < 1e-3f) return null;
        double s = tr.Stations[^1] * Mathf.Clamp(forward ? arc : Length - arc, 0f, Length) / Length;
        int at = tr.Strips.FindIndex(x => x.Id == id);
        if (at < 0) return null;
        float Centre(int k) { var (from, to) = tr.SpanAt(k, s); return (from + to) * 0.5f; }
        // (a lane only narrower than at its widest, as a 5 m lane easing to 3.5, is its own all along)
        float w = tr.WidthAt(at, s), full = Mathf.Min(tr.Strips[at].Widths.Max(), OwnLane);
        float centre = Centre(at);
        if (full > 1e-3f && w < full - 0.01f)
        {
            int parent = -1;
            for (int k = 0; k < tr.Strips.Count; k++)
            {
                var x = tr.Strips[k];
                if (k == at || x.Kind != StripKind.CarLane || x.Travel != tr.Strips[at].Travel || tr.WidthAt(k, s) < OpenLane) continue;
                if (parent < 0 || Math.Abs(k - at) < Math.Abs(parent - at)) parent = k;
            }
            if (parent >= 0) centre = Mathf.Lerp(Centre(parent), centre, w / full);
            else if (w < 1e-3f) return null;
        }
        return forward ? centre : -centre;
    }

    /// <summary>The origin moved (#185): the line is somewhere else in world space, the same shape.</summary>
    public void Shift(OriginShift shift)
    {
        for (int i = 0; i < Points.Length; i++) Points[i] = shift.Point(Points[i]);
    }

    /// <summary>Position and unit tangent at arc length <paramref name="s"/> along the drawing order.</summary>
    public (Vector3 Pos, Vector3 Tangent) Sample(float s)
    {
        int n = Points.Length;
        if (s <= 0) return (Points[0], Dir(0));
        if (s >= Length) return (Points[n - 1], Dir(n - 2));
        int lo = 0, hi = n - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (Cumulative[mid] <= s) lo = mid; else hi = mid;
        }
        float span = Cumulative[hi] - Cumulative[lo];
        float t = span > 1e-5f ? (s - Cumulative[lo]) / span : 0f;
        return (Points[lo].Lerp(Points[hi], t), Dir(lo));
    }

    private Vector3 Dir(int i)
    {
        var d = Points[i + 1] - Points[i];
        return d.LengthSquared() > 1e-8f ? d.Normalized() : Vector3.Forward;
    }
}

/// <summary>
/// What traffic needs of a junction path (#818 phase 5): its move, where its lane lies at each end (metres right of a driver
/// arriving on that arm, from the road's centreline), and the lights and group it goes on (null and -1 without lights).
/// </summary>
public sealed record JunctionPathInfo(SignalMoves Move, float FromOffset, float ToOffset, SignalSite? Site, int Group);

/// <summary>
/// A signalised junction as traffic sees it (#353): its plan (#349, shared with the lamps, a
/// function of the server clock) and, per arm, the way out of the junction along it (world, flat).
/// </summary>
public sealed class SignalSite
{
    public required SignalPlan Plan { get; init; }
    public required Vector3[] Out { get; init; }
}

/// <summary>
/// One approach to a junction as traffic drives it (#353): the lanes the tile's <c>LANE</c> record
/// gives it (<see cref="RoadApproach"/>; one lane with every movement for a signalised approach
/// of an older tile), its traffic lights and plan arm if any, the turns it may not take, and what
/// <c>--trafficcheck</c> counts there.
/// </summary>
public sealed class LaneApproach
{
    public required ApproachLane[] Lanes { get; init; }
    public SignalSite? Site { get; init; }
    public int Arm { get; init; }
    public SignalMoves Banned { get; init; }
    /// <summary>The original lane's centre, metres right of the road's centre line; the lanes' offsets are from it.</summary>
    public float LaneCentre { get; init; }
    /// <summary>Out of the junction along the arm (world, flat, unit).</summary>
    public Vector3 Out { get; init; }
    /// <summary>Where the stop line is (world), for the probe.</summary>
    public Vector3 Stop { get; set; }

    /// <summary>
    /// Per lane, the lane its traffic rides until it opens (-1: none, it moves off the original lane):
    /// the next car lane beside it that starts further back. A left pocket opening beside the
    /// through lane, a right pocket branching off it.
    /// </summary>
    public int[] Parent { get; private init; } = [];
    /// <summary>More than one car lane: cars choose one and move to it.</summary>
    public bool MultiLane { get; private init; }
    /// <summary>The furthest a car lane starts before the stop line.</summary>
    public float Reach { get; private init; }

    // ---- --trafficcheck (#353) ----
    public int RedStops, RedRuns, AmberClears, GreenCrossings, PocketLefts, PermissiveWaits, RoomWaits;
    /// <summary>The furthest a car crossed the line from where its lane is, m.</summary>
    public float LaneError;
    /// <summary>Per lane: cars that crossed the line in it, and the sum of how far right of their usual line they were then, m.</summary>
    public int[] Crossings { get; private init; } = [];
    public float[] OffsetSum { get; private init; } = [];

    public static LaneApproach Make(ApproachLane[] lanes, SignalSite? site, int arm, SignalMoves banned, float centre, Vector3 outDir)
    {
        var parent = new int[lanes.Length];
        int cars = 0;
        float reach = 0f;
        for (int i = 0; i < lanes.Length; i++)
        {
            parent[i] = -1;
            if (lanes[i].Kind != ApproachLaneKind.Car) continue;
            cars++;
            reach = Mathf.Max(reach, lanes[i].TaperFrom);
            float best = lanes[i].TaperFrom + 0.5f;
            foreach (int step in (ReadOnlySpan<int>)[-1, 1])
                for (int k = i + step; k >= 0 && k < lanes.Length; k += step)
                {
                    if (lanes[k].Kind != ApproachLaneKind.Car) continue;
                    if (lanes[k].TaperFrom > best) { best = lanes[k].TaperFrom; parent[i] = k; }
                    break;   // only the next car lane on that side
                }
        }
        return new LaneApproach
        {
            Lanes = lanes, Site = site, Arm = arm, Banned = banned, LaneCentre = centre, Out = outDir,
            Parent = parent, MultiLane = cars > 1, Reach = reach, Crossings = new int[lanes.Length], OffsetSum = new float[lanes.Length],
        };
    }

    /// <summary>A lane change into a pocket that appears at once beside its lane takes this long, m (from a few metres before it opens).</summary>
    private const float Change = 12f;

    /// <summary>
    /// Where a car bound for lane <paramref name="lane"/> drives <paramref name="d"/> m before the
    /// stop line, metres right of the original lane's centre: the lane it branches from until it
    /// opens, then over to its own over the taper (or a short lane change).
    /// </summary>
    public float Lateral(int lane, float d)
    {
        var l = Lanes[lane];
        float from = l.TaperFrom, full = l.FullFrom;
        int p = Parent[lane];
        if (p < 0) return l.Offset * Ramp(d, from, full);
        if (from - full < Change * 0.5f)
        {
            from = full + Change * 0.3f;
            full = Mathf.Max(0f, full - Change * 0.7f);
        }
        return Mathf.Lerp(Lateral(p, d), l.Offset, Ramp(d, from, full));
    }

    /// <summary>0 at <paramref name="from"/> m before the line, 1 from <paramref name="full"/> on.</summary>
    private static float Ramp(float d, float from, float full) =>
        from <= full + 0.01f ? (d <= full ? 1f : 0f) : Mathf.Clamp((from - d) / (from - full), 0f, 1f);

    /// <summary>The car lane for a turn: the one whose arrows show it and fewest others; else the one carrying the original lane on.</summary>
    public int LaneFor(SignalMoves turn, uint habit = 0)
    {
        int best = -1, bestCount = int.MaxValue, root = -1, ties = 0;
        for (int i = 0; i < Lanes.Length; i++)
        {
            var l = Lanes[i];
            if (l.Kind != ApproachLaneKind.Car) continue;
            if (Parent[i] < 0 && (root < 0 || l.TaperFrom > Lanes[root].TaperFrom)) root = i;
            if ((l.Moves & turn) == 0) continue;
            int count = System.Numerics.BitOperations.PopCount((uint)l.Moves);
            if (count < bestCount) { best = i; bestCount = count; ties = 1; }
            // several lanes show it equally (#700: two through lanes): the car's habit picks, so both carry cars
            else if (count == bestCount && habit % (uint)++ties == 0) best = i;
        }
        return best >= 0 ? best : root;
    }

    /// <summary>
    /// The car lane whose centre at the stop line lies nearest <paramref name="offset"/> (metres right of the road's centreline):
    /// the lane a junction path starts from (#818 phase 5); -1 without a car lane.
    /// </summary>
    public int LaneNearest(float offset)
    {
        int best = -1;
        float bestGap = float.MaxValue;
        for (int i = 0; i < Lanes.Length; i++)
        {
            if (Lanes[i].Kind != ApproachLaneKind.Car) continue;
            float gap = Mathf.Abs(LaneCentre + Lanes[i].Offset - offset);
            if (gap < bestGap) { best = i; bestGap = gap; }
        }
        return best;
    }
}

/// <summary>
/// The drivable roads (or the railway) around the player, stitched into a graph traffic can
/// follow. Built from the same <c>.road</c> tiles the renderer draws, straight into world
/// coordinates, with endpoints snapped onto a half-metre lattice so a line cut at a kilometre
/// seam carries on into the next tile instead of ending there.
///
/// <para>
/// Immutable once built, except that it follows the origin (<see cref="Shift"/>). The traffic
/// manager rebuilds it as the player moves and simply swaps the reference; a vehicle keeps the
/// edge it is on and finds its next one by the endpoint key, which means the same junction in any
/// rebuild: keys are cells of the LV95 grid, so they do not change when the origin moves (#185).
/// </para>
/// </summary>
public sealed class LaneGraph
{
    public List<LaneEdge> Edges { get; } = new();

    /// <summary>The origin frame the edges' points are in.</summary>
    public OriginFrame Frame { get; private set; }
    private readonly Dictionary<long, List<(LaneEdge Edge, bool AtStart)>> _incident = new();

    private const float Snap = 0.5f;

    /// <summary>RoadMeshBuilder's raised rail height (0.18) less the paint lift (0.02): RoadGen's RailRoadOverlap.RailTop.</summary>
    private const float EmbeddedRailSink = 0.16f;

    private LaneGraph(OriginFrame frame) => Frame = frame;

    /// <summary>The traffic lights of the tiles it was built from (#353).</summary>
    public List<SignalSite> Signals { get; } = new();

    /// <summary>An approach's stop line is matched to an edge end this close to it.</summary>
    private const float StopReach = 15f;

    /// <summary>The snap cell of an LV95 point, laid out like world X/Z (east, then south).</summary>
    private static long Key(double e, double n) =>
        ((long)(int)Math.Round(e / Snap) << 32) ^ (uint)(int)Math.Round(-n / Snap);

    /// <summary>
    /// Builds in one origin frame, taken once by the caller (a worker must not read a live origin
    /// that may move under it). Shift the result if the origin has moved since.
    /// </summary>
    public static LaneGraph Build(IEnumerable<RoadTile> tiles, OriginFrame origin, Func<RoadSegment, bool> keep)
    {
        var g = new LaneGraph(origin);
        foreach (var tile in tiles)
        {
            // the transitions' pieces (#818 phase 5): their lanes, which cars follow
            var transitionOf = tile.Transitions.Count == 0 ? null
                : tile.Transitions.ToDictionary(t => t.Segment, t => t, ReferenceEqualityComparer.Instance);
            foreach (var seg in tile.Segments)
            {
                if (seg.PointCount < 2 || !keep(seg)) continue;
                var pts = new Vector3[seg.PointCount];
                var cum = new float[seg.PointCount];
                // a rail embedded in a road (#124) lies at the road's height and its rail head is
                // the groove paint, not the raised rail a train's lift is measured from
                float sink = seg.Attributes.Has(RoadAttrFlags.Embedded) ? EmbeddedRailSink : 0f;
                long keyStart = 0, keyEnd = 0;
                for (int i = 0; i < pts.Length; i++)
                {
                    var (e, n) = seg.Lv95(tile.Id, i);
                    pts[i] = origin.ToWorld(e, n, seg.Points[i * 3 + 1] - sink);
                    if (i > 0) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
                    if (i == 0) keyStart = Key(e, n);
                    if (i == pts.Length - 1) keyEnd = Key(e, n);
                }
                if (cum[^1] < 1f) continue;

                // an undivided road: each direction keeps to its rightmost lane, clear of a painted bike lane
                // (#700); under 4.5 m the two share the road and keep 0.3 m over, as before
                var at = seg.Attributes;
                int back = Math.Max(1, (int)at.LanesBackward), fwd = Math.Max(1, (int)at.LanesForward);
                bool shared = seg.Width < 4.5f;
                float leftBike = at.Left.HasLane ? at.Left.BikeDm / 10f : 0f, rightBike = at.Right.HasLane ? at.Right.BikeDm / 10f : 0f;
                var edge = new LaneEdge
                {
                    RightLaneFwd = shared ? 0.3f : RoadCrossSection.TwoWayLaneOffset(seg.Width, rightBike, leftBike, fwd, back),
                    RightLaneBwd = shared ? 0.3f : RoadCrossSection.TwoWayLaneOffset(seg.Width, leftBike, rightBike, back, fwd),
                    LanesFwd = shared ? 1 : fwd, LanesBwd = shared ? 1 : back,
                    OneWayLanes = Math.Max(1, (int)Math.Max(at.LanesForward, at.LanesBackward)),
                    LaneWidth = at.OneWay != 0 ? RoadCrossSection.LaneWidth(seg.Class)
                        : shared ? 0f : RoadCrossSection.TwoWayLaneWidth(seg.Width, leftBike, rightBike, back, fwd),
                    Points = pts, Cumulative = cum, Class = seg.Class, Flags = seg.Flags,
                    Width = seg.Width, KeyStart = keyStart, KeyEnd = keyEnd,
                    OneWay = seg.Attributes.OneWay,
                    Yield = seg.Attributes.Flags & (RoadAttrFlags.YieldAtStart | RoadAttrFlags.YieldAtEnd),
                    RightLane = RoadCrossSection.RightLaneOffset(seg.Class, seg.Width,
                        Math.Max(seg.Attributes.LanesForward, seg.Attributes.LanesBackward)),
                    Transition = transitionOf?.GetValueOrDefault(seg),
                };
                g.Edges.Add(edge);
                if (edge.Transition is not null) g.Transitions++;
                g.Link(edge.KeyStart, edge, true);
                g.Link(edge.KeyEnd, edge, false);
            }
        }
        g.OrientDivided();
        var matched = new Dictionary<(LaneEdge, bool), float>();
        foreach (var tile in tiles)
        {
            var sites = new SignalSite[tile.Signals.Count];
            for (int k = 0; k < sites.Length; k++)
            {
                var plan = tile.Signals[k].Plan;
                var outs = new Vector3[plan.Arms.Count];
                for (int a = 0; a < outs.Length; a++) outs[a] = OutOf(plan.Arms[a].Heading);
                g.Signals.Add(sites[k] = new SignalSite { Plan = plan, Out = outs });
            }
            // the junction paths (#818 phase 5): each a one-way edge from the road end at its arm's mouth to the exit's; their ends meet
            // those roads, so JoinTrimmedEnds leaves the junction to them. Cars only (riders are not traffic)
            foreach (var jpath in tile.JunctionPaths)
            {
                if (jpath.Kind != JunctionPathKind.Car || jpath.PointCount < 2) continue;
                int n = jpath.PointCount;
                var pts = new Vector3[n];
                var cum = new float[n];
                for (int i = 0; i < n; i++)
                {
                    pts[i] = origin.ToWorld(tile.Id.MinE + jpath.Points[i * 3], tile.Id.MaxN - jpath.Points[i * 3 + 2], jpath.Points[i * 3 + 1]);
                    if (i > 0) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
                }
                if (cum[^1] < 0.5f) continue;
                var site = jpath.Signal >= 0 && jpath.Signal < sites.Length ? sites[jpath.Signal] : null;
                var edge = new LaneEdge
                {
                    Points = pts, Cumulative = cum, Class = jpath.Junction.Class, Width = Math.Max(jpath.Width, jpath.ToWidth), OneWay = 1, Connector = true,
                    KeyStart = Key(tile.Id.MinE + jpath.From.X, tile.Id.MaxN - jpath.From.Z), KeyEnd = Key(tile.Id.MinE + jpath.To.X, tile.Id.MaxN - jpath.To.Z),
                    Path = new JunctionPathInfo(jpath.Move, jpath.From.Offset, jpath.To.Offset, site, site is null ? -1 : jpath.Group),
                };
                g.Edges.Add(edge);
                g.Link(edge.KeyStart, edge, true);
                g.Link(edge.KeyEnd, edge, false);
                g.Paths++;
            }
            // the lanes of every approach the tile records (#353), then any signalised approach it does not (an older tile)
            var recorded = new HashSet<(int, int)>();
            foreach (var r in tile.Approaches)
            {
                var site = r.Signal >= 0 && r.Signal < sites.Length && r.SignalArm < sites[r.Signal].Out.Length ? sites[r.Signal] : null;
                if (site is not null) recorded.Add((r.Signal, r.SignalArm));
                var approach = LaneApproach.Make(r.Lanes.ToArray(), site, r.SignalArm, r.Banned, r.LaneCentre, OutOf(r.Heading));
                g.Attach(approach, origin.ToWorld(tile.Id.MinE + r.X, tile.Id.MaxN - r.Z, r.Y), matched);
            }
            for (int k = 0; k < sites.Length; k++)
            {
                var signal = tile.Signals[k];
                for (int a = 0; a < sites[k].Out.Length; a++)
                {
                    if (!signal.Plan.Arms[a].In || float.IsNaN(signal.Stops[a * 3]) || recorded.Contains((k, a))) continue;
                    var moves = SignalMoves.None;
                    for (int to = 0; to < signal.Plan.Arms.Count; to++)
                        if (to != a && signal.Plan.Arms[to].Out) moves |= SignalPlan.Turn(signal.Plan.Arms, a, to);
                    var approach = LaneApproach.Make([new ApproachLane(0f, 0f, 0f, moves, ApproachLaneKind.Car)], sites[k], a, SignalMoves.None, 0f, sites[k].Out[a]);
                    g.Attach(approach, origin.ToWorld(tile.Id.MinE + signal.Stops[a * 3], tile.Id.MaxN - signal.Stops[a * 3 + 2], signal.Stops[a * 3 + 1]), matched);
                }
            }
        }
        return g;
    }

    /// <summary>The junction paths it holds (#818 phase 5).</summary>
    public int Paths { get; private set; }

    /// <summary>Its edges that are a transition's pieces, whose lanes cars follow (#818 phase 5).</summary>
    public int Transitions { get; private set; }

    /// <summary>Every approach tied to an edge (#353).</summary>
    public List<LaneApproach> Approaches { get; } = new();

    /// <summary>World direction (flat, unit) of a heading in plan view (LV95: east 0, north π/2).</summary>
    private static Vector3 OutOf(double heading) => new((float)Math.Cos(heading), 0f, -(float)Math.Sin(heading));

    /// <summary>
    /// Ties an approach (#353) to the edge that ends at it: the end nearest its stop point, of an
    /// edge driven toward the junction along the arm (the nearest approach wins an end). Where the
    /// stop line lies before that end is what a car stops at.
    /// </summary>
    private void Attach(LaneApproach approach, Vector3 stop, Dictionary<(LaneEdge, bool), float> matched)
    {
        LaneEdge? best = null;
        bool bestAtEnd = false;
        float bestDist = StopReach, bestBack = 0f;
        foreach (var e in Edges)
            foreach (bool atEnd in (ReadOnlySpan<bool>)[true, false])
            {
                if (atEnd ? e.OneWay < 0 : e.OneWay > 0) continue;   // driven toward that end
                var end = atEnd ? e.Points[^1] : e.Points[0];
                float d = new Vector2(end.X - stop.X, end.Z - stop.Z).Length();
                if (d >= bestDist) continue;
                var dir = atEnd ? e.Sample(e.Length).Tangent : -e.Sample(0f).Tangent;
                var flat = new Vector3(dir.X, 0f, dir.Z).Normalized();
                if (flat.Dot(-approach.Out) < 0.7f) continue;   // driving into the junction along this arm
                best = e; bestAtEnd = atEnd; bestDist = d;
                bestBack = (end - stop).Dot(flat);
            }
        if (best is null || matched.TryGetValue((best, bestAtEnd), out float taken) && taken <= bestDist) return;
        matched[(best, bestAtEnd)] = bestDist;
        if ((bestAtEnd ? best.ApproachAtEnd : best.ApproachAtStart) is { } displaced) Approaches.Remove(displaced.Approach);
        approach.Stop = stop;
        Approaches.Add(approach);
        if (bestAtEnd) best.ApproachAtEnd = (approach, bestBack);
        else best.ApproachAtStart = (approach, bestBack);
    }

    private void Link(long key, LaneEdge edge, bool atStart)
    {
        if (!_incident.TryGetValue(key, out var list)) _incident[key] = list = new();
        list.Add((edge, atStart));
    }

    /// <summary>How many edge ends meet at a point: 2 is a road carrying on (a tile seam, a split line), 3 or more a junction.</summary>
    public int Degree(long key) => Incident(key).Count();

    /// <summary>
    /// Whether another road meeting at <paramref name="key"/> is more important than <paramref name="edge"/> (a lower
    /// class): the car on <paramref name="edge"/> gives way. Also across a short connector (<see cref="JoinTrimmedEnds"/>):
    /// a side road trimmed back from the main road meets only its connectors, of its own class.
    /// </summary>
    public bool GivesWay(long key, LaneEdge edge) => Incident(key).Any(o => o.Edge != edge
        && (o.Edge.Class < edge.Class
            || (o.Edge.Length < 20f && Incident(o.AtStart ? o.Edge.KeyEnd : o.Edge.KeyStart).Any(f => f.Edge != o.Edge && f.Edge != edge && f.Edge.Class < edge.Class))));

    /// <summary>
    /// Joins road ends that stop short of each other with a straight connector edge: the road
    /// generator trims every road back from its junction polygon, so the roads meeting at a junction
    /// no longer share an endpoint (<see cref="UnitSport.Player.RaceRoute"/> looks 18 m around for the
    /// same reason). Without it every such junction was a dead end to the traffic: cars turned round on
    /// the spot in the middle of the junction, in front of the racers (#85). Only ends nothing else
    /// meets, only within <paramref name="reach"/> m, and only where both roads point at the gap (no
    /// connector turns a car back the way it came).
    /// </summary>
    public void JoinTrimmedEnds(float reach = 18f)
    {
        var ends = new List<(LaneEdge Edge, bool AtStart, Vector3 At, Vector3 Out)>();
        foreach (var e in Edges)
        {
            if (Degree(e.KeyStart) == 1) ends.Add((e, true, e.Points[0], -e.Sample(0f).Tangent));
            if (Degree(e.KeyEnd) == 1) ends.Add((e, false, e.Points[^1], e.Sample(e.Length).Tangent));
        }
        for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count; j++)
            {
                var (a, b) = (ends[i], ends[j]);
                if (a.Edge == b.Edge) continue;
                var gap = b.At - a.At;
                float d = new Vector2(gap.X, gap.Z).Length();
                if (d < 1f || d > reach || Mathf.Abs(gap.Y) > 3f) continue;
                var dir = new Vector3(gap.X, 0, gap.Z) / d;
                if (new Vector3(a.Out.X, 0, a.Out.Z).Normalized().Dot(dir) < 0.3f
                    || new Vector3(b.Out.X, 0, b.Out.Z).Normalized().Dot(-dir) < 0.3f) continue;
                var link = new LaneEdge
                {
                    Points = new[] { a.At, b.At }, Cumulative = new[] { 0f, gap.Length() },
                    Class = (RoadClass)Mathf.Max((int)a.Edge.Class, (int)b.Edge.Class),
                    Width = Mathf.Min(a.Edge.Width, b.Edge.Width),
                    KeyStart = a.AtStart ? a.Edge.KeyStart : a.Edge.KeyEnd,
                    KeyEnd = b.AtStart ? b.Edge.KeyStart : b.Edge.KeyEnd,
                    Connector = true,
                };
                Edges.Add(link);
                Link(link.KeyStart, link, true);
                Link(link.KeyEnd, link, false);
            }
    }

    /// <summary>
    /// The origin moved (#185): every edge follows, and the graph is in the frame <paramref name="now"/>.
    /// <paramref name="done"/> holds edges already moved, since a vehicle may still drive on an edge
    /// of an older graph, and an edge must move exactly once.
    /// </summary>
    public void Shift(OriginShift shift, OriginFrame now, HashSet<LaneEdge> done)
    {
        foreach (var e in Edges)
            if (done.Add(e)) e.Shift(shift);
        foreach (var a in Approaches) a.Stop = shift.Point(a.Stop);
        Frame = now;
    }

    /// <summary>Road ends with nothing joining them: cul-de-sacs, and the edge of what is loaded.</summary>
    public int DeadEnds => Edges.Sum(e => (Degree(e.KeyStart) == 1 ? 1 : 0) + (Degree(e.KeyEnd) == 1 ? 1 : 0));

    /// <summary>
    /// The edge ends at a point: its snap cell and the eight around it. Two ends 0.1 m apart can round
    /// into neighbouring 0.5 m cells, and a road then "ended" there — measured on the Mollendruz pass,
    /// traffic turned round on the spot in mid-road in front of the racers (#85).
    /// </summary>
    private IEnumerable<(LaneEdge Edge, bool AtStart)> Incident(long key)
    {
        int x = (int)(key >> 32), z = (int)(uint)key;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (_incident.TryGetValue(((long)(x + dx) << 32) ^ (uint)(z + dz), out var list))
                    foreach (var end in list) yield return end;
    }

    /// <summary>Everything leaving a junction that may be driven away from it.</summary>
    public IEnumerable<(LaneEdge Edge, bool Forward)> Leaving(long key)
    {
        foreach (var (edge, atStart) in Incident(key))
        {
            bool forward = atStart;   // leaving from its start means driving in drawing order
            if (edge.OneWay == 1 && !forward) continue;
            if (edge.OneWay == -1 && forward) continue;
            yield return (edge, forward);
        }
    }

    /// <summary>Everything that may be driven into a junction (for <c>--trafficcheck --feed</c>, #353).</summary>
    public IEnumerable<(LaneEdge Edge, bool Forward)> Entering(long key)
    {
        foreach (var (edge, atStart) in Incident(key))
        {
            bool forward = !atStart;   // arriving at its end means driving in drawing order
            if (edge.OneWay == 1 && !forward) continue;
            if (edge.OneWay == -1 && forward) continue;
            yield return (edge, forward);
        }
    }

    /// <summary>
    /// swissTLM3D draws a divided road as two centrelines and records nothing about which way
    /// each carries traffic. Switzerland drives on the right, so each carriageway runs with the
    /// other one on its LEFT: find the partner beside the middle of each line and read the
    /// direction off which side it is on. Guessing instead sends half of all motorway traffic
    /// the wrong way down its carriageway.
    /// </summary>
    private void OrientDivided()
    {
        const float Cell = 30f;
        var grid = new Dictionary<long, List<(LaneEdge Edge, Vector3 P)>>();
        long CellKey(Vector3 p) => ((long)Mathf.FloorToInt(p.X / Cell) << 32) ^ (uint)Mathf.FloorToInt(p.Z / Cell);

        var divided = Edges.Where(e => (e.Flags & RoadFlags.Divided) != 0).ToList();
        foreach (var e in divided)
            foreach (var p in e.Points)
            {
                long k = CellKey(p);
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new();
                l.Add((e, p));
            }

        foreach (var e in divided)
        {
            if (e.OneWay != 0) continue;   // stored in the tile (v3): the build already decided
            var (mid, t) = e.Sample(e.Length * 0.5f);
            var right = new Vector3(-t.Z, 0, t.X).Normalized();
            float votes = 0;
            int cx = Mathf.FloorToInt(mid.X / Cell), cz = Mathf.FloorToInt(mid.Z / Cell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long k = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                    if (!grid.TryGetValue(k, out var l)) continue;
                    foreach (var (other, p) in l)
                    {
                        if (other == e) continue;
                        var d = p - mid;
                        float along = Mathf.Abs(d.Dot(t)), side = d.Dot(right);
                        // beside it, a carriageway's width away, not ahead on the same line
                        if (along > 25f || Mathf.Abs(side) < 3f || Mathf.Abs(side) > 30f) continue;
                        votes += side > 0 ? -1 : 1;   // partner on the right: we run against the drawing
                    }
                }
            e.OneWay = votes > 0 ? 1 : votes < 0 ? -1 : 0;
        }
    }

    /// <summary>A random edge with a point inside the given distance band from <paramref name="around"/>.</summary>
    public (LaneEdge Edge, float Arc)? RandomSpot(Random rng, Vector3 around, float minDist, float maxDist,
        Func<LaneEdge, float> weight)
    {
        if (Edges.Count == 0) return null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var e = Edges[rng.Next(Edges.Count)];
            if (rng.NextDouble() > weight(e)) continue;
            float s = (float)rng.NextDouble() * e.Length;
            var (p, _) = e.Sample(s);
            float d = new Vector2(p.X - around.X, p.Z - around.Z).Length();
            if (d >= minDist && d <= maxDist) return (e, s);
        }
        return null;
    }
}
