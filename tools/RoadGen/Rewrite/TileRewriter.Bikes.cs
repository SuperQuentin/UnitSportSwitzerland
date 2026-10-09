namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Bike infrastructure in the network stage (#120, <see cref="BikePlanner"/>): the street layout
/// of separated paths, their paint, and the crossings at junctions: a bike lane or path carried
/// across a side road's mouth as a red surface between yellow dashed lines.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>The red stops this short of the yellow lines and the kerb (Stadt Bern C 2.10.10: 5 cm).</summary>
    private const float RedInset = 0.05f;

    /// <summary>The stroke key of a link that may carry a separated path: its TLM line, else its first point.</summary>
    private static string? BikeStrokeKey(RoadLink link)
    {
        if (link.Tag is not Source s) return null;
        var a = s.Segment.Attributes;
        if (!s.Line.BikeWanted && !a.Left.HasTrack && !a.Right.HasTrack) return null;
        return s.Key is { } k
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{k.Uuid}:{k.Part}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"~{s.Line.Plan[0].X:F1},{s.Line.Plan[0].Y:F1}");
    }

    /// <summary>A path this short of a junction's mouth is carried on to it (<see cref="PathsToMouth"/>).</summary>
    private const double CrossingLookIn = 8.0;

    /// <summary>
    /// A street's separated path (and the sidewalk beside it) that stops within <see cref="CrossingLookIn"/> m of a
    /// junction's mouth goes on to the mouth (#700, the user's rule: the path reaches the road it crosses before its
    /// crossing starts). A joining road's corner zone stops it a few metres short where the junction's setback is small;
    /// the pieces between take the side as it is where it starts. <paramref name="atStart"/>/<paramref name="atEnd"/>:
    /// which ends of the street are a junction's mouth.
    /// </summary>
    private static List<RoadSegment> PathsToMouth(List<RoadSegment> pieces, bool atStart, bool atEnd)
    {
        if (pieces.Count < 2) return pieces;
        var result = new List<RoadSegment>(pieces);
        foreach (bool fromStart in (ReadOnlySpan<bool>)[true, false])
        {
            if (fromStart ? !atStart : !atEnd) continue;
            foreach (bool right in (ReadOnlySpan<bool>)[false, true])
            {
                RoadSide SideOf(RoadSegment s) => right ? s.Attributes.Right : s.Attributes.Left;
                double gap = 0;
                int p = 0;
                for (; p < result.Count; p++)
                {
                    var piece = fromStart ? result[p] : result[^(p + 1)];
                    if (SideOf(piece).HasTrack) break;
                    gap += RoadPaintGeometry.Length(piece.Points);
                    if (gap > CrossingLookIn) break;
                }
                if (p == 0 || p >= result.Count || gap > CrossingLookIn) continue;
                var found = SideOf(fromStart ? result[p] : result[^(p + 1)]);
                ushort shift = fromStart ? found.ShiftStartCm : found.ShiftEndCm;
                var carried = found with { ShiftStartCm = shift, ShiftEndCm = shift };
                for (int q = 0; q < p; q++)
                {
                    int index = fromStart ? q : result.Count - 1 - q;
                    var s = result[index];
                    result[index] = new RoadSegment
                    {
                        Class = s.Class, Surface = s.Surface, Flags = s.Flags, Width = s.Width, Points = s.Points,
                        Attributes = right ? s.Attributes with { Right = carried } : s.Attributes with { Left = carried },
                    };
                }
            }
        }
        return result;
    }

    /// <summary>A link end that is a junction's mouth or a dead end (not a road simply carrying on).</summary>
    private static bool EndsAtJunction(RoadNetwork net, int node) =>
        node < 0 || node >= net.Nodes.Count || net.Nodes[node].Degree != 2;

    /// <summary>
    /// At traffic lights (#406): the line of a painted bike lane on the approach side of
    /// <paramref name="piece"/> (right: the junction is at its end, else at its start), among the
    /// paint added from index <paramref name="from"/> on, stops at the stop line
    /// (<paramref name="stop"/>, metres along the piece: the crossing through the junction starts
    /// there) and is solid between it and <paramref name="solid"/>, as the car lanes' lines are over
    /// the last <see cref="TurnSolid"/> metres.
    /// </summary>
    private static void BikeLaneToStop(List<RoadPaint> paint, int from, RoadSegment piece, bool right, double stop, double solid)
    {
        double lo = Math.Min(stop, solid), hi = Math.Max(stop, solid);
        double pieceLength = RoadPaintGeometry.Length(piece.Points);
        bool barred = false;
        for (int k = paint.Count - 1; k >= from; k--)
        {
            var p = paint[k];
            if (p.Type != PaintType.YellowDashed || !ReferenceEquals(p.Segment, piece) || (p.Offset > 0) != right) continue;
            // #700: the riders going straight on stop at their own yellow line, level with the cars'
            // (which ends at the lane's line): across the lane, on the cars' side of the stop
            if (!barred && stop >= 0 && stop <= pieceLength)
            {
                barred = true;
                var side = right ? piece.Attributes.Right : piece.Attributes.Left;
                float edge = piece.Width * 0.5f + side.ShiftStartCm / 100f, line = Math.Abs(p.Offset);
                if (edge - line > 0.5f)
                {
                    float sign = right ? 1f : -1f;
                    double near = right ? stop - BikeStopLine : stop, far = right ? stop : stop + BikeStopLine;
                    paint.Add(RoadPaint.AlongSegment(piece, PaintType.StopLine, PaintEmitter.Yellow, edge - line - 0.1f, 0, 0,
                        sign * (line + edge) * 0.5f, Math.Max(0, near), Math.Min(pieceLength, far), p.Variant));
                }
            }
            double a = p.From, b = float.IsPositiveInfinity(p.To)
                ? RoadPaintGeometry.Length(RoadPaintGeometry.Offset(piece, p.Offset)) : p.To;
            if (b <= lo && right || a >= hi && !right) continue;   // all of it before the solid stretch
            paint.RemoveAt(k);
            void Part(PaintType type, float dash, double d0, double d1)
            {
                if (d1 - d0 > 0.3)
                    paint.Insert(k, RoadPaint.AlongSegment(piece, type, p.Rgba, p.Width, dash, dash == 0 ? 0 : p.Gap, p.Offset, d0, d1, p.Variant));
            }
            // the dashed part keeps its phase where it starts; past the solid stretch it starts again
            if (right) { Part(PaintType.YellowSolid, 0, Math.Max(a, lo), Math.Min(b, hi)); Part(PaintType.YellowDashed, p.Dash, a, Math.Min(b, lo)); }
            else { Part(PaintType.YellowDashed, p.Dash, Math.Max(a, hi), b); Part(PaintType.YellowSolid, 0, Math.Max(a, lo), Math.Min(b, hi)); }
        }
    }

    /// <summary>
    /// The paint of a street's separated paths, piece by piece: the yellow dashed line between a
    /// path at the sidewalk's height and the sidewalk (layouts 1 and 2), and a Velo symbol where
    /// the path starts or ends (a junction, or a stretch without it).
    /// </summary>
    private static void EmitTrackPaint(List<RoadSegment> pieces, bool startsAtJunction, bool endsAtJunction,
        List<RoadPaint> into)
    {
        static bool Track(RoadSegment s, bool right) => (right ? s.Attributes.Right : s.Attributes.Left).HasTrack;
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            foreach (bool right in (ReadOnlySpan<bool>)[false, true])
            {
                var side = right ? p.Attributes.Right : p.Attributes.Left;
                if (!side.HasTrack) continue;
                // beside a turn lane's widening the side is shifted out (#123): along a steady shift the
                // paint moves with it, along a taper (a varying shift) there is none
                bool dashed = side.Bike == BikeKind.Track && side.BufferDm == 0 && side.SidewalkDm > 0;
                if (side.ShiftStartCm != side.ShiftEndCm) { if (dashed) PaintEmitter.TaperTrackLine(p, right, into); continue; }
                float sign = right ? 1f : -1f, half = p.Width * 0.5f + side.ShiftStartCm / 100f;
                if (dashed)
                    PaintEmitter.AddDashed(p, sign * (half + (side.VergeDm + side.BikeDm) / 10f), 0, into,
                        PaintType.YellowDashed, PaintEmitter.Yellow, BikePlanner.LineWidth, BikePlanner.Dash, BikePlanner.Gap);
                bool atStart = i == 0 ? startsAtJunction : !Track(pieces[i - 1], right);
                bool atEnd = i == pieces.Count - 1 ? endsAtJunction : !Track(pieces[i + 1], right);
                PaintEmitter.Symbols(p, sign * (half + RoadStreetSection.TrackCentre(side)), right, into, atStart, atEnd);
            }
        }
    }

    /// <summary>
    /// The crossings of every junction of the block, along each road carried straight through it:
    /// at a main-road junction (#121's <see cref="PriorityPlanner.Kind.Main"/>) its main road, at any
    /// other each pair of car arms turning less than 60 degrees. On each side of such a road that
    /// carries a bike lane on both arms, the lane goes on across the junction. Where a road joins on
    /// that side, as a red surface (RAL 3020, the full lane width, 5 cm clear of the lines) between
    /// yellow 1 m / 1 m lines on both edges, replacing the white guide line there (SSV Art. 74a al. 1
    /// marks a Radstreifen across a junction only where the entering traffic gives way: the game
    /// marks it at every junction, the user's choice for #120); where none joins, as its yellow line
    /// carried through. A separated path on both arms crosses a joining road's mouth the same way,
    /// outside the carriageway, and that road's Wartelinie and 3.02 move back behind it; where none
    /// joins, the path runs on through the junction (<see cref="BridgePath"/>) instead of stopping at
    /// a sidewalk corner. At traffic lights, where the road it crosses is widened by its pockets or
    /// the crossing comes from a widened arm (#351), it runs square across that road's widened
    /// mouth, between its stop line and the junction (<see cref="SquareCrossing"/>).
    /// </summary>
    private static void EmitBikeCrossings(PriorityResult priority, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf, Dictionary<RoadSegment, List<RoadSegment>> finalPieces,
        Dictionary<(int Node, int Arm), ArmLanes> lanes, HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, List<RoadPaint>> paint,
        Dictionary<TileId, List<RoadPointProp>> signs, Dictionary<TileId, List<(RoadAreaProp Band, List<Vec2> Ring)>> bridges,
        BikePlanner.Stats stats, Dictionary<(int Link, LinkEnd End), double> stopsAt, Dictionary<int, (SignalPlan Plan, int[] PlanArm)> signalPlans,
        Dictionary<(int Node, int Arm), CornerArc> townArcs, Dictionary<TileId, List<RoadAreaProp>> islands, Dictionary<TileId, List<CornerPlanner.PathEnd>> pathEnds,
        List<SideCut> cutBacks)
    {
        var net = result.Network;
        bool IsCar(int linkId) => net.Links[linkId].Tag is Source s && PriorityPlanner.IsCarRoad(s.Segment.Class)
            && (s.Segment.Flags & RoadFlags.Stairs) == 0;
        foreach (var (junction, plan) in priority.Plans)
        {
            if (plan.Arms.Count != junction.Arms.Count) continue;
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;
            var anchors = Anchors(junction, net);
            if (anchors.Count == 0) continue;

            // the roads carried straight through: the main road, else every straight pair of car arms
            var car = Enumerable.Range(0, junction.Arms.Count).Where(i => IsCar(junction.Arms[i].LinkId)).ToList();
            var pairs = new List<(int A, int B, bool Main)>();
            if (plan.Kind == PriorityPlanner.Kind.Main)
            {
                var main = Enumerable.Range(0, plan.Arms.Count).Where(i => plan.Arms[i].Role == PriorityPlanner.Role.Main).ToArray();
                if (main.Length == 2) pairs.Add((main[0], main[1], true));
            }
            else if (plan.Kind is not (PriorityPlanner.Kind.Roundabout or PriorityPlanner.Kind.HighSpeed))
            {
                var used = new HashSet<int>();
                foreach (var (x, y) in car.SelectMany(x => car.Where(y => y > x).Select(y => (x, y)))
                             .OrderByDescending(p => -Math.Cos(junction.Arms[p.x].OutwardHeading - junction.Arms[p.y].OutwardHeading)))
                    if (!used.Contains(x) && !used.Contains(y)) { used.Add(x); used.Add(y); pairs.Add((x, y, false)); }
            }

            foreach (var (ia, ib, isMain) in pairs)
            {
                var a = junction.Arms[ia];
                var b = junction.Arms[ib];
                if (-Math.Cos(a.OutwardHeading - b.OutwardHeading) < 0.5) continue;   // turns more than 60 degrees
                Vec2 from = (a.Left + a.Right) * 0.5, to = (b.Left + b.Right) * 0.5;
                var ua = Vec2.FromHeading(a.OutwardHeading);
                var ub = Vec2.FromHeading(b.OutwardHeading);
                var mainDir = (Vec2.FromHeading(b.OutwardHeading) - ua).Normalized();

                // the bike side of an arm's end piece on its left or right looking outward
                // the side's shift off a turn lane's widening where the arm ends here (#123)
                var endShift = new Dictionary<(int, bool), double>();
                // a painted bike lane at the mouth (#351): looking outward the approach side is the
                // left, where a layout (b) bike lane lies a pocket further in than the kerb
                double LaneShift(int armIndex, bool armLeft, double shift) =>
                    armLeft && lanes.GetValueOrDefault((junction.NodeId, armIndex))?.Approach is { BikeBetween: true } l
                        ? l.BikeLane()!.Value.To - l.Half : shift;
                // how far in from the mouth the side's path or lane starts (#700: with a short setback a joining road's corner
                // can stop it a few metres in, and the crossing runs on to where it starts)
                var endInset = new Dictionary<(int, bool), double>();
                RoadSide SideOf(int armIndex, bool armLeft)
                {
                    var arm = junction.Arms[armIndex];
                    var end = plan.Arms[armIndex].End;
                    bool segRight = (end == LinkEnd.End) == armLeft;   // looking outward, an arm ending here has the drawing's right on its left
                    if (segmentOf.TryGetValue(arm.LinkId, out var so))
                    {
                        var pieces = finalPieces.TryGetValue(so.Segment, out var list) && list.Count > 0 ? list : [so.Segment];
                        double inset = 0;
                        for (int p = 0; p < pieces.Count && inset <= CrossingLookIn; p++)
                        {
                            var piece = end == LinkEnd.Start ? pieces[p] : pieces[^(p + 1)];
                            var side = segRight ? piece.Attributes.Right : piece.Attributes.Left;
                            if (side.HasTrack || side.HasLane)
                            {
                                endShift[(armIndex, armLeft)] = side.ShiftAt(end == LinkEnd.Start ? 0 : 1);
                                endInset[(armIndex, armLeft)] = inset;
                                return side;
                            }
                            inset += RoadPaintGeometry.Length(piece.Points);
                        }
                        var seg = end == LinkEnd.Start ? pieces[0] : pieces[^1];
                        var found = segRight ? seg.Attributes.Right : seg.Attributes.Left;
                        endShift[(armIndex, armLeft)] = found.ShiftAt(end == LinkEnd.Start ? 0 : 1);
                        return found;
                    }
                    // an arm in the halo: its lanes as the line decided them (paths are not known there)
                    if (net.Links[arm.LinkId].Tag is not Source src) return default;
                    var attrs = CrossSectionPlanner.Attributes(src.Line);
                    var s = segRight ? attrs.Right : attrs.Left;
                    return s.HasLane ? s : default;
                }

                for (int k = 0; k < 2; k++)
                {
                    var (ca, cb) = k == 0 ? (a.Left, b.Right) : (a.Right, b.Left);
                    var sa = SideOf(ia, armLeft: k == 0);
                    var sb = SideOf(ib, armLeft: k != 0);
                    double xa = endShift.GetValueOrDefault((ia, k == 0)), xb = endShift.GetValueOrDefault((ib, k != 0));
                    double sideSign = Math.Sign(ua.Cross(ca - from));
                    var joined = car.Where(i => i != ia && i != ib
                            && Math.Sign(ua.Cross(Vec2.FromHeading(junction.Arms[i].OutwardHeading))) == sideSign)
                        .ToList();
                    Vec2 da = (ca - from).Normalized(), db = (cb - to).Normalized();
                    // the crossing's ends where the paths or lanes start (#700)
                    ca += ua * endInset.GetValueOrDefault((ia, k == 0));
                    cb += ub * endInset.GetValueOrDefault((ib, k != 0));
                    // a curve through the junction, offset from the carriageway edge (+ outward, - inward) at each arm
                    List<Vec2> Bezier(double oa, double ob, bool simplify = true)
                    {
                        var pa = ca + da * oa;
                        var pb = cb + db * ob;
                        var control = junction.Centre + ((pa - from) + (pb - to)) * 0.5;
                        var line = new List<Vec2>();
                        for (int s = 0; s <= 8; s++)
                        {
                            double t = s / 8.0, mt = 1 - t;
                            line.Add(pa * (mt * mt) + control * (2 * mt * t) + pb * (t * t));
                        }
                        return simplify ? Polyline.Simplify(line, 0.02) : line;
                    }
                    // at traffic lights, across a widened arm or from one: square across the arm it crosses (#351)
                    bool round = townArcs.Keys.Any(k => k.Node == junction.NodeId);   // kerb arcs with paths round them (#682)
                    var rules = JunctionRules.Of(plan.Kind);
                    SquareCrossing? square = rules.Has(JunctionRule.BikeCrossingByPhase) && joined.Count == 1
                        ? SquareCrossing.For(junction, joined[0], lanes.GetValueOrDefault((junction.NodeId, joined[0])), from, xa, xb, force: round)
                        : null;
                    if (square is not null && round) square.Straight = true;
                    List<Vec2> Curve(double oa, double ob, bool simplify = true) =>
                        square is null ? Bezier(oa, ob, simplify)
                            : square.Line(ca + da * oa, ua, cb + db * ob, Vec2.FromHeading(b.OutwardHeading), oa - xa, ob - xb);
                    void Add(List<Vec2> line, PaintType type, uint rgba, float width, float dash) =>
                        Get(paint, home).Add(new RoadPaint
                        {
                            Shape = PaintShape.Polyline, Type = type, Rgba = rgba, Width = width, Dash = dash, Gap = dash,
                            Vertices = Local(home, line, p => HeightAt(anchors, p), 0f),
                        });
                    float lw = BikePlanner.LineWidth;

                    if (sa.HasLane && sb.HasLane)
                    {
                        double la = sa.BikeDm / 10.0, lb = sb.BikeDm / 10.0;
                        // the bike lane where it reaches the mouth (#351: in layout (b) between the
                        // through lane and a right pocket, not shifted out with the kerb)
                        xa = LaneShift(ia, k == 0, xa);
                        xb = LaneShift(ib, k != 0, xb);
                        if (signalPlans.TryGetValue(junction.NodeId, out var lights))
                        {
                            // traffic lights (#406): straight from where the riders' lane ends at their
                            // stop line to the exit's lane on the far side; red only where a car
                            // movement crosses it while the riders have green, else its dashed edges
                            int riders = k == 0 ? ia : ib;
                            double stop = stopsAt.GetValueOrDefault((junction.Arms[riders].LinkId, plan.Arms[riders].End));
                            Vec2 backA = k == 0 ? ua * stop : Vec2.Zero, backB = k == 0 ? Vec2.Zero : ub * stop;
                            // a widened arm's lane runs on straight along its kerb until the corner's radius starts (#700), then across
                            double maxIn = (from - junction.Centre).Length + (to - junction.Centre).Length;
                            double ta = StraightIn(junction, ca + da * xa, -ua, maxIn), tb = StraightIn(junction, cb + db * xb, -ub, maxIn);
                            // ... only where the line straight across would cut over a kerb (the user's rule): the least of it that clears
                            (ta, tb) = ClearOfKerbs(junction, ca + da * (xa - lw), ca + da * xa, da, ua, from, cb + db * (xb - lw), cb + db * xb, db, ub, to, ta, tb);
                            Vec2 LeadA(double oa) => ca + da * oa + backA;
                            Vec2 BendA(double oa) => ca + da * oa - ua * ta;
                            Vec2 BendB(double ob) => cb + db * ob - ub * tb;
                            Vec2 LeadB(double ob) => cb + db * ob + backB;
                            List<Vec2> Straight(double oa, double ob) => Densify(2.0, LeadA(oa), BendA(oa), BendB(ob), LeadB(ob));
                            double ra = (lw + la - lw * 0.5) * 0.5, rb = (lw + lb - lw * 0.5) * 0.5;
                            float band = (float)(Math.Min(la, lb) - lw * 1.5 - 2 * RedInset);
                            // red only on the half (or halves) where a car movement crosses it while the riders have green (#682);
                            // a run from either end of the crossing carries on over the straight lead there
                            Vec2 s0 = BendA(-ra + xa), s1 = BendB(-rb + xb);
                            var runs = RedRuns(ConflictsOf(lights, riders, joined, lanes.GetValueOrDefault((junction.NodeId, riders))?.Approach, junction, s0, s1));
                            bool crossed = runs.Count > 0;
                            if (band > 0.3f)
                                foreach (var (from0, to0) in runs)
                                {
                                    var corners = new List<Vec2>();
                                    if (from0 <= 1e-6) corners.Add(LeadA(-ra + xa));
                                    corners.Add(s0 + (s1 - s0) * from0);
                                    corners.Add(s0 + (s1 - s0) * to0);
                                    if (to0 >= 1 - 1e-6) corners.Add(LeadB(-rb + xb));
                                    Add(Densify(2.0, [.. corners]), PaintType.BikeCrossing, PaintEmitter.Red, band, 0);
                                }
                            Add(Straight(-lw * 0.5 + xa, -lw * 0.5 + xb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                            Add(Straight(-la + xa, -lb + xb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                            if (crossed) stats.SignalLanesRed++; else stats.SignalLanesDashed++;
                            continue;
                        }
                        square?.Place(Bezier, -(la + lw * 0.5) * 0.5, -(lb + lw * 0.5) * 0.5, xa, xb, (Math.Max(la, lb) + lw * 0.5) * 0.5);
                        if (joined.Count == 0)
                        {
                            Add(Curve(-la + xa, -lb + xb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.Dash);
                            stats.LanesThrough++;
                            continue;
                        }
                        // between the outer line (on the edge) and the lane's own line, 5 cm clear of both
                        double redA = (lw + la - lw * 0.5) * 0.5, redB = (lw + lb - lw * 0.5) * 0.5;
                        float red = (float)(Math.Min(la, lb) - lw * 1.5 - 2 * RedInset);
                        // a lane shifted off a turn lane's widening (#123) crosses from its shifted place
                        if (red > 0.3f) Add(Curve(-redA + xa, -redB + xb), PaintType.BikeCrossing, PaintEmitter.Red, red, 0);
                        Add(Curve(-lw * 0.5 + xa, -lw * 0.5 + xb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                        Add(Curve(-la + xa, -lb + xb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                        if (isMain && priority.GuideAt.Remove((junction.NodeId, k), out var guide)) paint[guide.Tile].Remove(guide.Paint);
                        stats.Crossings++;
                        stats.CrossingsLane++;
                    }
                    else if (sa.HasTrack && sb.HasTrack && joined.Count > 0)
                    {
                        double ta = RoadStreetSection.TrackCentre(sa) + xa, tb = RoadStreetSection.TrackCentre(sb) + xb;
                        double ha = sa.BikeDm / 20.0 - lw * 0.5, hb = sb.BikeDm / 20.0 - lw * 0.5;
                        // (#711, the user's rule) the path runs on to the kerb of the road it crosses, and only that road's
                        // carriageway is crossed: each band of the side straight on from the mouth to where it meets the kerb. At
                        // every junction (the lights too, the user's review: before, their bands went round the kerb arc)
                        if (PathsToKerb(home, junction, islands.GetValueOrDefault(home), joined, sa, sb, ca, da, ua, xa, cb, db, ub, xb, ca.DistanceTo(cb) + 5,
                                p => HeightAt(anchors, p)) is { } toKerb)
                        {
                            Get(bridges, home).AddRange(toKerb.Bands);
                            Get(pathEnds, home).AddRange(toKerb.Ends);
                            // the joining road's side under the carried one starts behind it: no two surfaces in one place
                            foreach (var (pathEnd, c, d, u, w) in new[] { (toKerb.Ends[0], ca, da, ua, sa.OuterDm / 10.0 + xa), (toKerb.Ends[1], cb, db, ub, sb.OuterDm / 10.0 + xb) })
                                if (SideUnder(junction, plan, joined, new Vec2(pathEnd.At.X + home.MinE, pathEnd.At.Y + home.MaxN), c + d * w, u, segmentOf, finalPieces) is { } under)
                                    cutBacks.Add(under);
                            var (kerbA, kerbB) = (toKerb.KerbA, toKerb.KerbB);
                            List<Vec2> Across(double oa, double ob) => Densify(kerbA(oa), kerbB(ob), 2.0);
                            float across = (float)(Math.Min(sa.BikeDm, sb.BikeDm) / 10.0 - 2 * lw - 2 * RedInset);
                            if (across > 0.3f && rules.Has(JunctionRule.BikeCrossingByPhase) && signalPlans.TryGetValue(junction.NodeId, out var kerbLights))
                            {
                                // at the lights red only on the half or halves where a car crosses while the riders go (#682)
                                Vec2 s0 = kerbA(ta), s1 = kerbB(tb);
                                foreach (var (from0, to0) in RedRuns(ConflictsOf(kerbLights, k == 0 ? ia : ib, joined, lanes.GetValueOrDefault((junction.NodeId, k == 0 ? ia : ib))?.Approach, junction, s0, s1)))
                                    Add(Densify(s0 + (s1 - s0) * from0, s0 + (s1 - s0) * to0, 2.0), PaintType.BikeCrossing, PaintEmitter.Red, across, 0);
                            }
                            else if (across > 0.3f) Add(Across(ta, tb), PaintType.BikeCrossing, PaintEmitter.Red, across, 0);
                            Add(Across(ta - ha, tb - hb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                            Add(Across(ta + ha, tb + hb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                            stats.Crossings++;
                            stats.CrossingsTrack++;
                            stats.PathsToKerb++;
                            double behind = Math.Max(sa.VergeDm + sa.BikeDm, sb.VergeDm + sb.BikeDm) / 10.0 + 0.1;
                            foreach (int i in joined) MoveYieldBack(priority, net, junction.Arms[i], mainDir, behind, paint, signs, stats);
                            continue;
                        }
                        square?.Place(Bezier, ta - xa, tb - xb, xa, xb, Math.Max(ha, hb) + lw * 0.5);
                        // kerb arcs round the corners (#682): Swiss crossings are not set back, the band runs straight from the path in to the path out
                        bool straight = round && rules.Has(JunctionRule.BikeCrossingByPhase);
                        List<Vec2> Run(double oa, double ob) => straight ? Densify(ca + da * oa, cb + db * ob, 2.0) : Curve(oa, ob);
                        float red = (float)(Math.Min(sa.BikeDm, sb.BikeDm) / 10.0 - 2 * lw - 2 * RedInset);
                        if (red > 0.3f && straight && signalPlans.TryGetValue(junction.NodeId, out var pathLights))
                        {
                            // at the lights red only on the half or halves where a car crosses while the riders go (#682)
                            int pathRiders = k == 0 ? ia : ib;
                            Vec2 s0 = ca + da * ta, s1 = cb + db * tb;
                            foreach (var (from0, to0) in RedRuns(ConflictsOf(pathLights, pathRiders, joined, lanes.GetValueOrDefault((junction.NodeId, pathRiders))?.Approach, junction, s0, s1)))
                                Add(Densify(s0 + (s1 - s0) * from0, s0 + (s1 - s0) * to0, 2.0), PaintType.BikeCrossing, PaintEmitter.Red, red, 0);
                        }
                        else if (red > 0.3f) Add(Run(ta, tb), PaintType.BikeCrossing, PaintEmitter.Red, red, 0);
                        Add(Run(ta - ha, tb - hb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                        Add(Run(ta + ha, tb + hb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                        stats.Crossings++;
                        stats.CrossingsTrack++;
                        // the joining road gives way before the path, not on it
                        double beyond = Math.Max(sa.VergeDm + sa.BikeDm, sb.VergeDm + sb.BikeDm) / 10.0 + 0.1;
                        foreach (int i in joined) MoveYieldBack(priority, net, junction.Arms[i], mainDir, beyond, paint, signs, stats);
                    }
                    else if (sa.HasTrack && sb.HasTrack)
                    {
                        if (BridgePath(home, sa, sb, ua, ub, (oa, ob) => Bezier(oa + xa, ob + xb, simplify: false), p => HeightAt(anchors, p)) is { } bands)
                        {
                            Get(bridges, home).AddRange(bands);
                            stats.PathsThrough++;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Points every <paramref name="step"/> metres or less from <paramref name="a"/> to <paramref name="b"/>, both ends included (paint heights follow the junction).</summary>
    private static List<Vec2> Densify(Vec2 a, Vec2 b, double step)
    {
        int n = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / step));
        var line = new List<Vec2>(n + 1);
        for (int i = 0; i <= n; i++) line.Add(a + (b - a) * ((double)i / n));
        return line;
    }

    /// <summary><see cref="Densify"/> along a polyline of corners, each shared corner once.</summary>
    private static List<Vec2> Densify(double step, params Vec2[] corners)
    {
        var line = new List<Vec2> { corners[0] };
        for (int i = 1; i < corners.Length; i++)
        {
            if (corners[i].DistanceTo(corners[i - 1]) < 0.05) continue;
            var piece = Densify(corners[i - 1], corners[i], step);
            line.AddRange(piece.Skip(1));
        }
        return line;
    }

    /// <summary>
    /// How far the straight edge of a widened arm runs on into the junction (#700, the user's rule: lanes, bike lanes and
    /// their markings follow the straight edge until the corner's radius starts): from <paramref name="start"/>, the arm's
    /// kerb at its mouth, along <paramref name="inward"/> to where it meets the junction's ring. 0 where the kerb is the
    /// mouth's own corner (an arm not widened: its radius starts there).
    /// </summary>
    private static double StraightIn(Junction junction, Vec2 start, Vec2 inward, double max)
    {
        var ring = junction.Boundary;
        if (ring.Count < 3) return 0;
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            Vec2 p = ring[i], q = ring[(i + 1) % ring.Count], e = q - p;
            double den = inward.Cross(e);
            if (Math.Abs(den) < 1e-9) continue;
            var w = p - start;
            double t = w.Cross(e) / den, s = w.Cross(inward) / den;
            if (s >= 0 && s <= 1 && t >= 0 && t < best) best = t;
        }
        if (best >= 0.3) return best > max ? 0 : best;
        // the kerb is the mouth's corner: the ring runs on along it where a tight corner keeps its edge straight (#700)
        double run = 0;
        for (double t = 0.25; t <= max; t += 0.25)
        {
            var at = start + inward * t;
            double near = double.MaxValue;
            for (int i = 0; i < ring.Count; i++) near = Math.Min(near, DistanceToSegment(at, ring[i], ring[(i + 1) % ring.Count]));
            if (near > 0.15) break;
            run = t;
        }
        return run < 0.5 ? 0 : run;
    }

    /// <summary>
    /// Of a crossing's straight runs in along each arm's kerb (<paramref name="ta"/>, <paramref name="tb"/>), the least that keeps
    /// the line across on the junction (#700): none, one side's, the other's, both. A line point counts once it is past both
    /// arms' mouths (<paramref name="midA"/>, <paramref name="midB"/>); beside an arm's straight kerb (up to its run in) it
    /// must stay inside that kerb's line, past both runs inside the junction's ring.
    /// </summary>
    private static (double, double) ClearOfKerbs(Junction junction, Vec2 lineA, Vec2 kerbA, Vec2 da, Vec2 ua, Vec2 midA,
        Vec2 lineB, Vec2 kerbB, Vec2 db, Vec2 ub, Vec2 midB, double ta, double tb)
    {
        bool Clear(double a, double b)
        {
            Vec2 p0 = lineA - ua * a, p1 = lineB - ub * b;
            for (int s = 1; s < 40; s++)
            {
                var p = p0 + (p1 - p0) * (s / 40.0);
                double inA = -(p - midA).Dot(ua), inB = -(p - midB).Dot(ub);
                if (inA < 0.3 || inB < 0.3) continue;   // on an arm, not yet in the junction
                if (inA <= ta) { if ((p - kerbA).Dot(da) > 0.05) return false; }
                else if (inB <= tb) { if ((p - kerbB).Dot(db) > 0.05) return false; }
                else if (!Inside(junction.Boundary, p)) return false;
            }
            return true;
        }
        foreach (var (a, b) in (ReadOnlySpan<(double, double)>)[(0, 0), (ta, 0), (0, tb), (ta, tb)])
            if (Clear(a, b)) return (a, b);
        return (ta, tb);
    }

    private static bool Inside(List<Vec2> ring, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y)
                && p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }

    private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var e = b - a;
        double l2 = e.Dot(e), t = l2 < 1e-12 ? 0 : Math.Clamp((p - a).Dot(e) / l2, 0, 1);
        return p.DistanceTo(a + e * t);
    }

    /// <summary>
    /// Whether a car movement crosses a bike lane through a signalised junction while its riders
    /// have green (#406). The riders come from arm <paramref name="from"/> (junction arm indices)
    /// along the side where the <paramref name="joined"/> arms join; a movement crosses their way
    /// when exactly one of its ends lies on that side: in from or out to a joined arm, or in from
    /// the riders' own approach out of a right pocket kerbside of their lane (layout (b),
    /// <paramref name="layout"/>). The riders go with their bike group, else their approach's
    /// through group; two groups run together when their greens overlap in the built plan (a
    /// protected arrow held red then does not count). An approach missing from the plan: red.
    /// </summary>
    private static List<double> ConflictsOf((SignalPlan Plan, int[] PlanArm) lights, int from, List<int> joined, ApproachLayout? layout,
        Junction junction, Vec2 lineA, Vec2 lineB)
    {
        var found = new List<double>();
        var (plan, armInPlan) = lights;
        int pa = armInPlan[from];
        if (pa < 0) return [0.25, 0.75];
        var junctionArm = new int[plan.Arms.Count];
        for (int j = 0; j < armInPlan.Length; j++) if (armInPlan[j] >= 0) junctionArm[armInPlan[j]] = j;
        int riders = -1;
        for (int g = 0; g < plan.Groups.Count && riders < 0; g++)
            if (plan.Groups[g].Kind == SignalGroupKind.Bike && plan.Groups[g].Arm == pa) riders = g;
        for (int g = 0; g < plan.Groups.Count && riders < 0; g++)
            if (plan.Groups[g].Kind == SignalGroupKind.Car && plan.Groups[g].Arm == pa && (plan.Groups[g].Moves & SignalMoves.Through) != 0) riders = g;
        for (int g = 0; g < plan.Groups.Count && riders < 0; g++)
            if (plan.Groups[g].Kind == SignalGroupKind.Car && plan.Groups[g].Arm == pa) riders = g;
        if (riders < 0) return [0.25, 0.75];
        bool kerbsidePocket = layout is { BikeBetween: true, Right: true };
        foreach (var m in plan.Movements())
        {
            if (plan.Groups[m.Group].Kind == SignalGroupKind.Bike) continue;
            int jf = junctionArm[m.From], jt = junctionArm[m.To];
            bool inside = joined.Contains(jf) || (jf == from && m.Turn == SignalMoves.Right && kerbsidePocket);
            if (inside == joined.Contains(jt)) continue;
            if (!GreenTogether(plan, riders, m.Group)) continue;
            found.Add(CrossesAt(junction, jf, jt, lineA, lineB));
        }
        return found;
    }

    /// <summary>
    /// Where a car movement from arm <paramref name="from"/> to arm <paramref name="to"/> crosses the line from
    /// <paramref name="a"/> to <paramref name="b"/>, as a fraction of it (the movement as a curve from its entry lane at the
    /// mouth through the junction's centre to its exit lane); the middle where it does not.
    /// </summary>
    private static double CrossesAt(Junction junction, int from, int to, Vec2 a, Vec2 b)
    {
        Vec2 Lane(int arm, bool entering)
        {
            var arm_ = junction.Arms[arm];
            var u = Vec2.FromHeading(arm_.OutwardHeading);
            var mid = (arm_.Left + arm_.Right) * 0.5;
            return mid + u.Perp * (arm_.HalfWidth * 0.5 * (entering ? 1 : -1));
        }
        Vec2 p0 = Lane(from, true), p2 = Lane(to, false), c = junction.Centre;
        Vec2 prev = p0;
        for (int k = 1; k <= 16; k++)
        {
            double t = k / 16.0, mt = 1 - t;
            var q = p0 * (mt * mt) + c * (2 * mt * t) + p2 * (t * t);
            if (SegmentsCross(prev, q, a, b) is { } hit)
            {
                double len = a.DistanceTo(b);
                return len < 1e-6 ? 0.5 : Math.Clamp(a.DistanceTo(hit) / len, 0, 1);
            }
            prev = q;
        }
        return 0.5;
    }

    /// <summary>The red stretches of a crossing from its conflicts: the half or halves of it where cars cross (#682).</summary>
    private static List<(double From, double To)> RedRuns(List<double> conflicts)
    {
        bool first = conflicts.Any(t => t < 0.5), second = conflicts.Any(t => t >= 0.5);
        if (first && second) return [(0, 1)];
        if (first) return [(0, 0.5)];
        return second ? [(0.5, 1)] : [];
    }
    /// <summary>Whether two groups of a plan are ever green at the same time.</summary>
    private static bool GreenTogether(SignalPlan plan, int a, int b)
    {
        foreach (var x in plan.Groups[a].Intervals)
        {
            if (x.Aspect != SignalAspect.Green) continue;
            foreach (var y in plan.Groups[b].Intervals)
                if (y.Aspect == SignalAspect.Green && Math.Min(x.To, y.To) - Math.Max(x.From, y.From) > 0.05) return true;
        }
        return false;
    }

    /// <summary>
    /// A bike crossing at traffic lights across a joining arm that is widened by its pockets, or that
    /// comes from a widened arm (#351). The #120 curve joins the two arms' lane or path ends through
    /// the junction; drawn for the narrow mouth, from a widened arm's shifted end it ran skewed across
    /// the joining arm's approach and over its stop line (seen at LV95 2499132,1116455). Here it runs
    /// square across that arm, edge to widened edge, between its stop line and the junction (and
    /// behind an advanced bike stop line there), the two ends joined to it straight.
    /// </summary>
    private sealed class SquareCrossing
    {
        private Vec2 _mid, _u, _n;
        /// <summary>The arm's extent on each side, from its centre line: toward the approach's right (+n), and the departing side.</summary>
        private double _in, _out;
        /// <summary>Where the arm's stop line starts, and an advanced bike line, metres from the mouth along it.</summary>
        private double _stop, _advanced = double.NaN;
        /// <summary>Whether the first arm (a) lies on the approach's side of it.</summary>
        private bool _aOnApproach;
        /// <summary>The band's centre: its distance along the arm, and the offsets it was placed for.</summary>
        private double _at, _centre;
        /// <summary>The paths run round kerb arcs to the crossing (#682): only the crossing itself is drawn, not the jogs to the arms' path ends.</summary>
        public bool Straight;

        public static SquareCrossing? For(Junction junction, int armIndex, ArmLanes? lanes, Vec2 fromA, double xa, double xb, bool force = false)
        {
            double widenIn = lanes?.Approach is { } l ? l.EdgeOut : 0, widenOut = lanes?.ExitWidening ?? 0;
            if (!force && widenIn < 0.05 && widenOut < 0.05 && xa < 0.05 && xb < 0.05) return null;
            var arm = junction.Arms[armIndex];
            var u = Vec2.FromHeading(arm.OutwardHeading);
            var mid = (arm.Left + arm.Right) * 0.5;
            var c = new SquareCrossing
            {
                _mid = mid, _u = u, _n = u.Perp,
                _in = arm.HalfWidth + widenIn, _out = arm.HalfWidth + widenOut,
                _stop = MouthSkew(junction, arm) + SignalStopSetback,
                _aOnApproach = u.Perp.Dot(fromA - mid) > 0,
            };
            if (lanes is { AdvancedBikeLine: true }) c._advanced = Math.Max(0.2, c._stop - AdvancedBikeLine);
            return c;
        }

        /// <summary>
        /// Places the band: where the #120 curve of its centre (<paramref name="ca"/>,
        /// <paramref name="cb"/> from the edges, shifted by <paramref name="xa"/>, <paramref name="xb"/>)
        /// crosses the arm's centre line, kept <paramref name="half"/> clear of the mouth, its stop
        /// line and an advanced bike line (behind it when the band cannot fit in front).
        /// </summary>
        public void Place(Func<double, double, bool, List<Vec2>> bezier, double ca, double cb, double xa, double xb, double half)
        {
            _centre = (ca + cb) * 0.5;
            var curve = bezier(ca + xa, cb + xb, false);
            double at = 0;
            for (int i = 1; i < curve.Count; i++)
            {
                double s0 = _n.Dot(curve[i - 1] - _mid), s1 = _n.Dot(curve[i] - _mid);
                if (Math.Sign(s0) == Math.Sign(s1)) continue;
                var p = curve[i - 1] + (curve[i] - curve[i - 1]) * (s0 / (s0 - s1));
                at = _u.Dot(p - _mid);
                break;
            }
            double lo = half + 0.1, hi = _stop - 0.2 - half;
            if (!double.IsNaN(_advanced) && _advanced - BikeStopLine * 0.5 - 0.2 - half < lo)
                lo = Math.Max(lo, _advanced + BikeStopLine * 0.5 + 0.2 + half);
            _at = Math.Min(Math.Max(at, lo), hi);
        }

        /// <summary>
        /// A line of the band: from <paramref name="pa"/> at the first arm, square across, to
        /// <paramref name="pb"/> (outward along their arms <paramref name="ua"/>, <paramref name="ub"/>);
        /// <paramref name="oa"/>, <paramref name="ob"/> its offsets from the edges.
        /// </summary>
        public List<Vec2> Line(Vec2 pa, Vec2 ua, Vec2 pb, Vec2 ub, double oa, double ob)
        {
            double at = _at + (oa + ob) * 0.5 - _centre;
            var onApproach = _mid + _u * at + _n * _in;
            var onExit = _mid + _u * at - _n * _out;
            if (Straight) return _aOnApproach ? [onApproach, onExit] : [onExit, onApproach];
            pa = Clear(pa, ua, _aOnApproach ? 1 : -1, at);
            pb = Clear(pb, ub, _aOnApproach ? -1 : 1, at);
            return _aOnApproach ? [pa, onApproach, onExit, pb] : [pa, onExit, onApproach, pb];
        }

        /// <summary>
        /// An end that lies within the widened arm's span (a widening reaching into the next arm's
        /// corner): moved out along its own arm's lane or path until it clears the widened edge by as
        /// much as it stands off the crossing along the arm, so the band jogs out to it instead of
        /// turning back.
        /// </summary>
        private Vec2 Clear(Vec2 p, Vec2 outward, int side, double at)
        {
            double edge = side > 0 ? _in : _out, lateral = side * _n.Dot(p - _mid), away = side * _n.Dot(outward);
            double need = edge + Math.Max(1.0, Math.Abs(_u.Dot(p - _mid) - at));
            return lateral >= need || away < 0.3 ? p : p + outward * ((need - lateral) / away);
        }
    }

    /// <summary>Douglas-Peucker on a polyline, returning the indices kept (both ends always).</summary>
    private static List<int> KeepIndices(List<Vec2> line, double tolerance)
    {
        var keep = new bool[line.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, line.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            int worst = -1;
            double far = tolerance;
            for (int i = a + 1; i < b; i++)
            {
                double d = PriorityPlanner.DistanceToSegment(line[i], line[a], line[b]);
                if (d > far) { far = d; worst = i; }
            }
            if (worst < 0) continue;
            keep[worst] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
        return Enumerable.Range(0, line.Count).Where(i => keep[i]).ToList();
    }

    /// <summary>The sidewalk corners of a tile, less those a path carried through the junction (#120) now covers.</summary>
    private static IEnumerable<RoadAreaProp> Unbridged(TileId id, List<RoadAreaProp> corners,
        List<(RoadAreaProp Band, List<Vec2> Ring)> bridges, BikePlanner.Stats stats)
    {
        foreach (var corner in corners)
        {
            var v = corner.Vertices;
            int n = v.Length / 3;
            double x = 0, z = 0;
            for (int i = 0; i < n; i++) { x += v[i * 3]; z += v[i * 3 + 2]; }
            var centre = new Vec2(id.MinE + x / Math.Max(n, 1), id.MaxN - z / Math.Max(n, 1));
            if (n > 0 && bridges.Any(b => PriorityPlanner.Inside(b.Ring, centre))) { stats.CornersReplaced++; continue; }
            yield return corner;
        }
    }

    /// <summary>Each end of a path carried through a junction reaches this far back over its piece: the piece's open end is chamfered down.</summary>
    private const double BridgeOverlap = 0.15;

    /// <summary>
    /// A separated path carried on through a junction where no road joins on its side (#120): each
    /// band of its profile (grass, path, sidewalk) as a solid area prop at its level, each sloped
    /// kerb as a strip whose vertices carry its slope, between the two arms' ends along the junction's edge curve.
    /// Null when the two arms' profiles differ (their bands would not meet): the sidewalk corner
    /// stays. <paramref name="curve"/> gives the curve at an offset outward from each arm's edge.
    /// </summary>
    private static List<(RoadAreaProp Band, List<Vec2> Ring)>? BridgePath(TileId home, RoadSide sa, RoadSide sb, Vec2 ua, Vec2 ub,
        Func<double, double, List<Vec2>> curve, Func<Vec2, float> height, double across = 0)
    {
        if (RoadStreetSection.For(sa) is not { } pa || RoadStreetSection.For(sb) is not { } pb) return null;
        if (!pa.Surface.SequenceEqual(pb.Surface) || !pa.H.SequenceEqual(pb.H)) return null;
        // the samples the outer edge needs to stay within 2 cm, shared by every band (a straight mouth keeps its ends)
        var keep = KeepIndices(curve(pa.Width, pb.Width), 0.02);
        List<Vec2> At(double oa, double ob)
        {
            var c = curve(oa, ob);
            var line = keep.Select(i => c[i]).ToList();
            line.Insert(0, line[0] + ua * BridgeOverlap);
            line.Add(line[^1] + ub * BridgeOverlap);
            return line;
        }
        var result = new List<(RoadAreaProp, List<Vec2>)>();
        for (int k = 0; k + 1 < pa.Count; k++)
        {
            var surface = pa.Surface[k];
            bool kerb = surface == StreetSurface.Kerb;
            // a vertical kerb has no width: the faces down the bands' open edges are its face
            if (kerb && pa.D[k + 1] - pa.D[k] < 1e-4f) continue;
            // the band's lines from its inner edge to its outer: more than two where it is cut into strips no wider than
            // `across` (#711: the band's end then follows a curved kerb strip by strip)
            int strips = across > 0 ? Math.Max(1, (int)Math.Ceiling(Math.Max(pa.D[k + 1] - pa.D[k], pb.D[k + 1] - pb.D[k]) / across)) : 1;
            var lines = Enumerable.Range(0, strips + 1).Select(s => At(pa.D[k] + (pa.D[k + 1] - pa.D[k]) * s / (double)strips,
                pb.D[k] + (pb.D[k + 1] - pb.D[k]) * s / (double)strips)).ToList();
            var inner = lines[0];
            var outer = lines[^1];
            int n = inner.Count;
            var plan = lines.SelectMany(l => l).ToList();
            var vertices = Local(home, plan, height, 0f);
            if (kerb)   // a sloped kerb strip carries its slope in its vertices: inner edge at its foot, outer at its top
                for (int i = 0; i < plan.Count; i++) vertices[i * 3 + 1] += pa.H[k] + (pa.H[k + 1] - pa.H[k]) * (i / n) / strips;
            var indices = new List<ushort>();
            for (int s = 0; s < strips; s++)
                for (int i = 0; i + 1 < n; i++)
                {
                    ushort a = (ushort)(s * n + i), b = (ushort)(s * n + i + 1), c = (ushort)((s + 1) * n + i + 1), d = (ushort)((s + 1) * n + i);
                    indices.AddRange([a, b, c, a, c, d]);
                }
            var type = surface switch
            {
                StreetSurface.Track => AreaPropType.BikePath,
                StreetSurface.Kerb => AreaPropType.Kerb,
                StreetSurface.Verge or StreetSurface.Buffer => AreaPropType.Grass,
                _ => AreaPropType.Sidewalk,
            };
            var ring = new List<Vec2>(inner);
            ring.AddRange(Enumerable.Reverse(outer));
            result.Add((new RoadAreaProp
            {
                Type = type, Flags = pa.H[k + 1] > 0 ? PropFlags.Solid : PropFlags.None, Height = kerb ? 0f : pa.H[k + 1],
                Vertices = vertices, Indices = indices.ToArray(),
            }, ring));
        }
        return result;
    }

    /// <summary>
    /// A path crossing a joining road at a junction without lights (#711, the user's rule: the path goes up to the kerb, where
    /// it turns into the red crossing). Each arm's side (grass, path, sidewalk: <see cref="BridgePath"/>'s bands) runs on
    /// straight from its mouth until each band's line meets the carriageway (<see cref="Carriageway"/>), so the band ends
    /// follow the kerb round the corner. <c>KerbA(o)</c> / <c>KerbB(o)</c>: where the line at offset <c>o</c> from an arm's edge
    /// (shift included) meets the kerb, the crossing's ends; <c>Ends</c>: where each side's outer edge meets it, tile-local,
    /// for the sidewalk corner beside (<see cref="CornerPlanner.PathEnd"/>). Null where a side's outer edge never meets it.
    /// </summary>
    private static (List<(RoadAreaProp Band, List<Vec2> Ring)> Bands, Func<double, Vec2> KerbA, Func<double, Vec2> KerbB, List<CornerPlanner.PathEnd> Ends)?
        PathsToKerb(TileId home, Junction junction, List<RoadAreaProp>? pavement, List<int> joined, RoadSide sa, RoadSide sb,
            Vec2 ca, Vec2 da, Vec2 ua, double xa, Vec2 cb, Vec2 db, Vec2 ub, double xb, double reach, Func<Vec2, float> height)
    {
        var road = Carriageway(junction, home, pavement, joined);
        // from the edge's point at offset o, inward along the arm, the first step on the carriageway (the line along the edge
        // itself is the outline's: 5 cm out)
        double? Run(Vec2 c, Vec2 d, Vec2 u, double o)
        {
            var p0 = c + d * Math.Max(o, 0.05);
            for (double t = 0; t <= reach; t += 0.05)
                if (road(p0 - u * t)) return t;
            return null;
        }
        double wa = sa.OuterDm / 10.0 + xa, wb = sb.OuterDm / 10.0 + xb;
        if (Run(ca, da, ua, wa) is not { } outA || Run(cb, db, ub, wb) is not { } outB) return null;
        Func<double, Vec2> Kerb(Vec2 c, Vec2 d, Vec2 u) => o => c + d * o - u * (Run(c, d, u, o) ?? 0);
        var bands = new List<(RoadAreaProp Band, List<Vec2> Ring)>();
        foreach (var (side, c, d, u, x) in new[] { (sa, ca, da, ua, xa), (sb, cb, db, ub, xb) })
        {
            // a fixed count of points per line, so every band's lines pair up (BridgePath keeps the outer edge's samples)
            List<Vec2> Line(double o, double _)
            {
                double length = Run(c, d, u, o + x) ?? 0;
                var p0 = c + d * (o + x);
                return [.. Enumerable.Range(0, 6).Select(i => p0 - u * (length * i / 5))];
            }
            if (BridgePath(home, side, side, u, Vec2.Zero, Line, height, across: 0.2) is not { } part) return null;
            bands.AddRange(part);
        }
        Vec2 Local(Vec2 p) => new(p.X - home.MinE, p.Y - home.MaxN);
        var ends = new List<CornerPlanner.PathEnd>
        {
            new(Local(ca + da * wa - ua * outA), ua),
            new(Local(cb + db * wb - ub * outB), ub),
        };
        return (bands, Kerb(ca, da, ua), Kerb(cb, db, ub), ends);
    }

    /// <summary>
    /// The joining road's side that a side carried on to the kerb (#711, <see cref="PathsToKerb"/>) lies over: the joined arm
    /// whose kerb the carried side's outer edge meets (<paramref name="kerbPoint"/>, LV95, past that arm's mouth), and how far
    /// out from the mouth its side must start, the carried outer edge (through <paramref name="edgeAt"/> along
    /// <paramref name="u"/>) crossing both that side's kerb and its outer edge. Null where it meets no joined arm's kerb.
    /// </summary>
    private static SideCut? SideUnder(Junction junction, PriorityPlanner.Plan plan, List<int> joined, Vec2 kerbPoint, Vec2 edgeAt, Vec2 u,
        Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf, Dictionary<RoadSegment, List<RoadSegment>> finalPieces)
    {
        (int Arm, bool Left, double Off)? best = null;
        foreach (int j in joined)
        {
            var arm = junction.Arms[j];
            var uj = Vec2.FromHeading(arm.OutwardHeading);
            foreach (bool left in (ReadOnlySpan<bool>)[true, false])
            {
                var e = left ? arm.Left : arm.Right;
                double s = (kerbPoint - e).Dot(uj), off = Math.Abs((kerbPoint - e).Cross(uj));
                if (s > 0.2 && off < 0.5 && (best is null || off < best.Value.Off)) best = (j, left, off);
            }
        }
        if (best is not { } b) return null;
        var joinedArm = junction.Arms[b.Arm];
        var end = plan.Arms[b.Arm].End;
        bool segRight = (end == LinkEnd.End) == b.Left;
        if (!segmentOf.TryGetValue(joinedArm.LinkId, out var so)) return null;
        var pieces = finalPieces.TryGetValue(so.Segment, out var list) && list.Count > 0 ? list : [so.Segment];
        var piece = end == LinkEnd.Start ? pieces[0] : pieces[^1];
        var side = segRight ? piece.Attributes.Right : piece.Attributes.Left;
        var edge = b.Left ? joinedArm.Left : joinedArm.Right;
        var uJ = Vec2.FromHeading(joinedArm.OutwardHeading);
        var outward = (edge - (joinedArm.Left + joinedArm.Right) * 0.5).Normalized();
        // how far out along the joined arm the carried outer edge crosses a line along it through q
        double? Cross(Vec2 q)
        {
            double den = uJ.Cross(u);
            return Math.Abs(den) < 0.2 ? null : (edgeAt - q).Cross(u) / den;
        }
        if (Cross(edge) is not { } atKerb || Cross(edge + outward * (side.OuterDm / 10.0 + side.ShiftAt(end == LinkEnd.Start ? 0 : 1))) is not { } atOuter) return null;
        double length = Math.Max(atKerb, atOuter) + 0.05;
        if (length < 0.3 || length > 20) return null;
        return new SideCut(so.Segment, so.Tile, end == LinkEnd.End, segRight, length);
    }

    /// <summary>
    /// Whether an LV95 point is carriageway at a junction (#711): inside its outline, on a turn lane's widening or a corner's
    /// kerb patch in its tile (tile-local pavement props), or on a joining arm's lanes out from its mouth.
    /// </summary>
    private static Func<Vec2, bool> Carriageway(Junction junction, TileId home, List<RoadAreaProp>? pavement, List<int> joined)
    {
        double cx = junction.Centre.X - home.MinE, cz = home.MaxN - junction.Centre.Y;
        var near = (pavement ?? []).Where(a => a.Type == AreaPropType.Pavement && a.Vertices.Length >= 9 && Enumerable.Range(0, a.Vertices.Length / 3)
            .Any(i => Math.Abs(a.Vertices[i * 3] - cx) < 60 && Math.Abs(a.Vertices[i * 3 + 2] - cz) < 60)).ToList();
        var arms = joined.Select(i => junction.Arms[i])
            .Select(a => (Mid: (a.Left + a.Right) * 0.5, U: Vec2.FromHeading(a.OutwardHeading), Half: a.Left.DistanceTo(a.Right) * 0.5)).ToList();
        return p =>
        {
            if (Inside(junction.Boundary, p)) return true;
            foreach (var (mid, u, half) in arms)
            {
                var q = p - mid;
                if (q.Dot(u) is var along && along > -0.3 && along < 40 && Math.Abs(q.Cross(u)) < half) return true;
            }
            double x = p.X - home.MinE, z = home.MaxN - p.Y;
            foreach (var a in near)
            {
                var v = a.Vertices;
                for (int t = 0; t + 2 < a.Indices.Length; t += 3)
                {
                    int i0 = a.Indices[t] * 3, i1 = a.Indices[t + 1] * 3, i2 = a.Indices[t + 2] * 3;
                    double d1 = (x - v[i1]) * (v[i0 + 2] - v[i1 + 2]) - (v[i0] - v[i1]) * (z - v[i1 + 2]);
                    double d2 = (x - v[i2]) * (v[i1 + 2] - v[i2 + 2]) - (v[i1] - v[i2]) * (z - v[i2 + 2]);
                    double d3 = (x - v[i0]) * (v[i2 + 2] - v[i0 + 2]) - (v[i2] - v[i0]) * (z - v[i0 + 2]);
                    bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                    if (!(neg && pos)) return true;
                }
            }
            return false;
        };
    }

    /// <summary>A yielding arm's Wartelinie rows and 3.02 sign moved out along it by <paramref name="beyond"/> (measured across the main road).</summary>
    private static void MoveYieldBack(PriorityResult priority, RoadNetwork net, JunctionArm arm, Vec2 mainDir, double beyond,
        Dictionary<TileId, List<RoadPaint>> paint, Dictionary<TileId, List<RoadPointProp>> signs, BikePlanner.Stats stats)
    {
        var u = Vec2.FromHeading(arm.OutwardHeading);
        double shift = beyond / Math.Max(Math.Abs(u.Cross(mainDir)), 0.5);
        if (priority.TeethOf.TryGetValue(arm.LinkId, out var rows) && net.Links[arm.LinkId].Tag is Source source)
            for (int r = 0; r < rows.Count; r++)
            {
                var (tile, row) = rows[r];
                if (!paint.TryGetValue(tile, out var list) || list.IndexOf(row) is var at && at < 0) continue;
                var v = (float[])row.Vertices.Clone();
                for (int i = 0; i + 2 < v.Length; i += 3)
                {
                    var p = new Vec2(tile.MinE + v[i] + u.X * shift, tile.MaxN - v[i + 2] + u.Y * shift);
                    v[i] = (float)(p.X - tile.MinE);
                    v[i + 2] = (float)(tile.MaxN - p.Y);
                    v[i + 1] = source.SampleHeight(p) + (row.Vertices[i + 1] - source.SampleHeight(new Vec2(tile.MinE + row.Vertices[i], tile.MaxN - row.Vertices[i + 2])));
                }
                var moved = new RoadPaint
                {
                    Shape = row.Shape, Type = row.Type, Variant = row.Variant, Rgba = row.Rgba,
                    Width = row.Width, Dash = row.Dash, Gap = row.Gap, Vertices = v,
                };
                list[at] = moved;
                rows[r] = (tile, moved);
                stats.TeethMoved++;
            }
        if (priority.SignsOf.TryGetValue(arm.LinkId, out var spots))
            foreach (var (tile, index) in spots)
            {
                if (!signs.TryGetValue(tile, out var list) || index >= list.Count) continue;
                var s = list[index];
                if (s.Type != PointPropType.YieldSign) continue;
                list[index] = s with { X = (float)(s.X + u.X * shift), Z = (float)(s.Z - u.Y * shift) };
                stats.SignsMoved++;
            }
    }

    /// <summary>
    /// A sign that would stand on a separated path (a 3.03 placed beside the kerb before the street
    /// got its path) moves out across it, onto the buffer or the sidewalk behind, at that height.
    /// </summary>
    private static void MoveSignsOffPaths(List<RoadSegment> segments, List<RoadPointProp> props, BikePlanner.Stats stats)
    {
        for (int k = 0; k < props.Count; k++)
        {
            var prop = props[k];
            foreach (var seg in segments)
            {
                if (!seg.Attributes.Left.HasTrack && !seg.Attributes.Right.HasTrack) continue;
                if (Nearest(seg, prop.X, prop.Z) is not { } near) continue;
                var (fx, fz, cx, cy, cz) = near;
                // right of travel (x east, z south): (-fz, fx)
                double lateral = (prop.X - cx) * -fz + (prop.Z - cz) * fx;
                bool right = lateral > 0;
                var side = right ? seg.Attributes.Right : seg.Attributes.Left;
                if (!side.HasTrack) continue;
                double edge = seg.Width * 0.5 + (side.ShiftStartCm + side.ShiftEndCm) / 200.0, from = edge + side.VergeDm / 10.0 - 0.3, to = edge + (side.VergeDm + side.BikeDm) / 10.0 + 0.3;
                if (Math.Abs(lateral) < from || Math.Abs(lateral) > to) continue;
                double outward = (side.VergeDm + side.BikeDm) / 10.0 + (side.BufferDm > 0 ? side.BufferDm / 20.0 : 0.4);
                double sign = right ? 1 : -1;
                float x = (float)(cx + -fz * sign * (edge + outward)), z = (float)(cz + fx * sign * (edge + outward));
                float y = (float)(cy + RoadStreetSection.HeightAt(side, (float)outward));
                props[k] = prop with { X = x, Y = y, Z = z };
                stats.SignsMoved++;
                break;
            }
        }
    }

    /// <summary>The point of a segment's centreline nearest (x, z) in plan, inside its span: direction and position.</summary>
    private static (double Fx, double Fz, double X, double Y, double Z)? Nearest(RoadSegment seg, double x, double z)
    {
        var p = seg.Points;
        double best = 25 * 25;
        (double, double, double, double, double)? result = null;
        for (int i = 0; i + 1 < seg.PointCount; i++)
        {
            double ax = p[i * 3], az = p[i * 3 + 2], bx = p[i * 3 + 3], bz = p[i * 3 + 5];
            double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            if (l2 < 1e-8) continue;
            double t = ((x - ax) * dx + (z - az) * dz) / l2;
            if ((i == 0 && t < -0.01) || (i + 2 == seg.PointCount && t > 1.01)) continue;   // past the segment's ends
            t = Math.Clamp(t, 0, 1);
            double px = ax + dx * t, pz = az + dz * t, d2 = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d2 >= best) continue;
            best = d2;
            double l = Math.Sqrt(l2);
            result = (dx / l, dz / l, px, p[i * 3 + 1] + (p[i * 3 + 4] - p[i * 3 + 1]) * t, pz);
        }
        return result;
    }

    /// <summary>Path layout (1..5) a side's record describes, 0 without a path.</summary>
    public static int LayoutOf(RoadSide s) =>
        !s.HasTrack ? 0
        : s.Bike == BikeKind.Track ? (s.VergeDm > 0 ? 2 : 1)
        : s.VergeDm == 0 ? 3 : s.BufferDm == 0 ? 4 : 5;
}
