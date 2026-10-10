namespace UnitSport.Tools.RoadGen.Network;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;

/// <summary>
/// Sidewalk corners (#119): where two streets meet, the sidewalks of the two sides facing the
/// same corner are joined by a patch (<see cref="AreaPropType.Sidewalk"/> in the v3 APRP layer)
/// whose kerb follows the junction cap's curve and whose outer edge squares the corner, so the
/// sidewalk runs round the block instead of stopping at each arm's trim.
///
/// <para>
/// Works on a tile's final segments and caps, tile-local. A node is a junction cap with the
/// segment ends on its ring, or a bare bend (two street ends sharing a point, turning more than
/// <see cref="MinBendDeg"/>). Arms are taken counter-clockwise (in plan, north up); between two
/// neighbours lie A's left and B's right, looking out of the node. Each side's sidewalk is looked
/// for along the arm, piece by piece, up to <see cref="ChainReach"/> (the street planner stops a
/// sidewalk short of another street's, so it often starts a few metres out). The patch is the
/// kerb line from A's sidewalk start in to the node, round the cap, out to B's, then B's outer
/// corner, the outer edges' meeting point (or the chord if they meet nowhere sensible), A's outer
/// corner. It is dropped if it would stand on a wall, a carriageway or the cap.
/// </para>
/// </summary>
public static class CornerPlanner
{
    public const double ChainReach = 30.0;
    public const double MinBendDeg = 30.0;
    public const double MaxArea = 400.0;
    private const double Sample = 0.5;

    public sealed class Stats
    {
        public int Corners, Built, OneSided, Squared, Banded, Arcs, Gaps, Fillets, Covered, Facade, Road, Shape;
        public double Area;

        public string Format() => string.Create(CultureInfo.InvariantCulture, $"""
                corners: {Corners:N0} with a sidewalk on either side, {Built:N0} built ({Squared:N0} squared, {Banded:N0} along the kerb, {Area:N0} m2; {Arcs:N0} round a widening's kerb arc, #711), {Covered:N0} covered by a side carried on to the kerb ({Gaps:N0} wedges laid before its bands, #711), {OneSided:N0} one-sided, {Fillets:N0} outer corners rounded (#711); rejected: a wall {Facade:N0}, a carriageway {Road:N0}, shape {Shape:N0}
            """);
    }

    private readonly record struct End(int Seg, bool AtStart);

    /// <summary><c>--debug-street E,N</c>: corners at nodes within 15 m of this LV95 point are traced.</summary>
    public static (double E, double N)? Debug;

    private static bool Traced(TileId id, Vec2 p) =>
        Debug is { } d && Math.Abs(id.MinE + p.X - d.E) < 15 && Math.Abs(id.MaxN + p.Y - d.N) < 15;

    /// <summary>One arm's sidewalk on the side facing a corner: kerb line from the node out to where the sidewalk starts.</summary>
    private sealed record Chain(List<(Vec2 P, float Y)> Kerb, Vec2 Out, double Width, float Kerb_, Vec2 Inward, RoadSide Side = default);

    /// <summary>
    /// A kerb arc round a widened corner (#682, #711), tile-local plan (x east, y = -z): the kerb line from arm A's
    /// mouth round to arm B's, and each arm's direction out of the junction along its widened edge.
    /// </summary>
    public sealed record KerbArc(List<(Vec2 P, float Y)> Line, Vec2 OutA, Vec2 OutB);

    /// <summary>
    /// Where an arm's side, carried on to the kerb of the road its path crosses (#711), meets that kerb with its outer edge,
    /// tile-local plan; <c>Out</c> the arm's direction out of the junction. The sidewalk corner beside it starts there.
    /// <c>Start</c>: where its outer edge starts, at the arm's mouth; the carried bands start on the line through it across the arm.
    /// </summary>
    public sealed record PathEnd(Vec2 At, Vec2 Out, Vec2 Start);

    /// <summary>The arms' directions match an arc's within this (cosine of 30 degrees).</summary>
    private const double ArcArmCos = 0.866;

    public static List<RoadAreaProp> Plan(TileId id, IReadOnlyList<RoadSegment> segments, IReadOnlyList<RoadJunction> caps,
        Facades facades, Stats stats, IReadOnlyList<RoadAreaProp>? pavement = null, IReadOnlyList<KerbArc>? arcs = null,
        IReadOnlyList<PathEnd>? pathEnds = null, IEnumerable<RoadAreaProp>? bands = null)
    {
        _pathEnds = pathEnds;
        _extra = [];
        _bandTris = [];
        foreach (var band in bands ?? [])
        {
            var v = band.Vertices;
            for (int t = 0; t + 2 < band.Indices.Length; t += 3)
            {
                Vec2 P(int k) => new(v[band.Indices[t + k] * 3], -v[band.Indices[t + k] * 3 + 2]);
                _bandTris.Add((P(0), P(1), P(2)));
            }
        }
        // a turn lane's widening (#123) is carriageway too: a corner never stands on it (#120:
        // the sidewalk beside a pocket now carries on, and its corner reached across the lane)
        _pavement = pavement?.Where(a => a.Type == AreaPropType.Pavement && a.Vertices.Length >= 9).ToList() ?? [];
        var props = new List<RoadAreaProp>();
        var ends = new Dictionary<(long, long), List<End>>();
        for (int s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            if (!IsRoad(seg)) continue;
            foreach (bool atStart in (ReadOnlySpan<bool>)[true, false])
            {
                var key = Key(Point(seg, atStart ? 0 : seg.PointCount - 1));
                if (!ends.TryGetValue(key, out var list)) ends[key] = list = new List<End>();
                list.Add(new End(s, atStart));
            }
        }

        var claimed = new HashSet<End>();
        foreach (var cap in caps)
        {
            if (cap.Layer != 0 || cap.VertexCount < 4) continue;
            var ring = new List<(Vec2 P, float Y)>();
            for (int i = 1; i < cap.VertexCount; i++)
                ring.Add((new Vec2(cap.Vertices[i * 3], -cap.Vertices[i * 3 + 2]), cap.Vertices[i * 3 + 1]));
            var arms = new List<End>();
            foreach (var list in ends.Values)
                foreach (var e in list)
                    if (DistanceToRing(Plan(segments[e.Seg].Points, e.AtStart ? 0 : segments[e.Seg].PointCount - 1), ring) < 0.3)
                        arms.Add(e);
            foreach (var e in arms) claimed.Add(e);
            // the cap's kerb arcs: both ends at its ring (an arc is clamped to the mouths, which lie on it)
            var capArcs = arcs?.Where(k => DistanceToRing(k.Line[0].P, ring) < ArcReach && DistanceToRing(k.Line[^1].P, ring) < ArcReach).ToList();
            Corners(id, segments, ends, arms, ring, cap, facades, stats, props, capArcs);
        }

        // bare bends: two street ends sharing a point, no cap
        foreach (var list in ends.Values)
        {
            if (list.Count != 2 || claimed.Contains(list[0]) || claimed.Contains(list[1])) continue;
            var a = Outward(segments[list[0].Seg], list[0].AtStart);
            var b = Outward(segments[list[1].Seg], list[1].AtStart);
            double turn = 180 - Math.Acos(Math.Clamp(a.Dot(b), -1, 1)) * 180 / Math.PI;
            if (turn < MinBendDeg) continue;
            Corners(id, segments, ends, list, null, null, facades, stats, props, null);
        }
        return props;
    }

    private static void Corners(TileId id, IReadOnlyList<RoadSegment> segments, Dictionary<(long, long), List<End>> ends,
        List<End> arms, List<(Vec2 P, float Y)>? ring, RoadJunction? cap, Facades facades, Stats stats, List<RoadAreaProp> props,
        List<KerbArc>? arcs)
    {
        if (arms.Count < 2) return;
        // (#711) a footpath or track joining a junction leads into the sidewalk corner, it does not split it: the corner runs
        // between the streets either side of it (else both halves had a sidewalk on one side only, and none was laid)
        if (ring != null)
        {
            var streets = arms.Where(e => IsStreet(segments[e.Seg]) || segments[e.Seg].Attributes.Left.OuterDm > 0 || segments[e.Seg].Attributes.Right.OuterDm > 0).ToList();
            if (streets.Count >= 2 && streets.Count < arms.Count) arms = streets;
        }
        var ordered = arms
            .Select(e => (End: e, Out: Outward(segments[e.Seg], e.AtStart)))
            .OrderBy(a => Math.Atan2(a.Out.Y, a.Out.X))
            .ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            var b = ordered[(i + 1) % ordered.Count];
            if (ordered.Count == 2 && i == 1 && ring == null) break;   // a bend has one inside and one outside: both from i = 0 below
            var ca = Walk(segments, ends, a.End, left: true);
            var cb = Walk(segments, ends, b.End, left: false);
            var node = Plan(segments[a.End.Seg].Points, a.End.AtStart ? 0 : segments[a.End.Seg].PointCount - 1);
            if (Traced(id, node))
                Console.WriteLine($"[corner] node ({id.MinE + node.X:F1},{id.MaxN + node.Y:F1}) {(ring != null ? "cap" : "bend")}: "
                    + $"{segments[a.End.Seg].Class} left -> {(ca == null ? "no sidewalk" : $"{ca.Width:F1} m after {ca.Kerb.Count} kerb points")}, "
                    + $"{segments[b.End.Seg].Class} right -> {(cb == null ? "no sidewalk" : $"{cb.Width:F1} m after {cb.Kerb.Count} kerb points")}");
            if (ca == null && cb == null) continue;
            stats.Corners++;
            if (ca == null || cb == null || (ca.Kerb_ > 0) != (cb.Kerb_ > 0)) { stats.OneSided++; continue; }
            // beside a side carried on to the kerb (#711) the corner starts where that side's outer edge meets the kerb
            var nodeB = Plan(segments[b.End.Seg].Points, b.End.AtStart ? 0 : segments[b.End.Seg].PointCount - 1);
            var carried = ring == null ? (null, null) : (EndOf(node, a.Out, left: true), EndOf(nodeB, b.Out, left: false));
            if (Traced(id, node) && (carried.Item1 ?? carried.Item2) != null) Console.WriteLine($"[corner]   beside a path carried to the kerb ({(carried.Item1 != null ? "A" : "")}{(carried.Item2 != null ? "B" : "")})");
            // round the kerb arc beside a widening (#711), else (or where that corner cannot stand) round the cap
            RoadAreaProp? prop = null;
            bool covered = false;
            var (squaredBefore, gapsBefore) = (stats.Squared, stats.Gaps);
            if (ArcOf(arcs, a.Out, b.Out) is { } arc)
            {
                var (facade, road, shape) = (stats.Facade, stats.Road, stats.Shape);
                prop = Build(id, segments, ca, cb, ring, cap, facades, stats, arc, carried, out covered);
                if (prop == null) (stats.Facade, stats.Road, stats.Shape) = (facade, road, shape);
                else stats.Arcs++;
                if (Traced(id, node)) Console.WriteLine($"[corner]   round a kerb arc of {arc.Line.Count} points: {(prop == null ? covered ? "covered by the carried side" : "failed" : "built")}");
            }
            if (prop == null && !covered)
            {
                var (facade, road, shape) = (stats.Facade, stats.Road, stats.Shape);
                prop = Build(id, segments, ca, cb, ring, cap, facades, stats, null, carried, out covered);
                if (Traced(id, node)) Console.WriteLine($"[corner]   round the cap: {(prop != null ? "built" : covered ? "covered by the carried side" : $"failed (facade {stats.Facade - facade}, road {stats.Road - road}, shape {stats.Shape - shape})")}");
            }
            if (covered) stats.Covered++;
            if (prop != null) props.Add(prop);
            if (_extra is { Count: > 0 } extra) { props.AddRange(extra); extra.Clear(); }   // a wedge laid in its sides' bands (#711)
            bool pieceAtCorner = stats.Squared > squaredBefore || stats.Gaps > gapsBefore;   // a corner piece reaching the block's corner
            if (Fillet(id, segments, cap, facades, stats, ca, cb, carried, pieceAtCorner, out var filletWhy) is { } fillet) props.Add(fillet);
            if (Traced(id, node)) Console.WriteLine($"[corner]   outer corner: {filletWhy ?? "rounded"}");
            if (ordered.Count == 2 && ring == null)
            {
                // the bend's other corner: B's left and A's right
                var cb2 = Walk(segments, ends, b.End, left: true);
                var ca2 = Walk(segments, ends, a.End, left: false);
                if (ca2 != null && cb2 != null && (ca2.Kerb_ > 0) == (cb2.Kerb_ > 0)
                    && Build(id, segments, cb2, ca2, null, null, facades, stats, null, (null, null), out _) is { } p2) props.Add(p2);
            }
        }
    }

    /// <summary>
    /// Along the arm from the node, on its left (or right) looking out: the kerb line up to the
    /// first piece carrying a sidewalk on that side, and that sidewalk. Null if none starts within
    /// <see cref="ChainReach"/> or the street branches first.
    /// </summary>
    private static Chain? Walk(IReadOnlyList<RoadSegment> segments, Dictionary<(long, long), List<End>> ends, End arm, bool left)
    {
        var kerb = new List<(Vec2 P, float Y)>();
        double travelled = 0;
        var cur = arm;
        for (int guard = 0; guard < 16; guard++)
        {
            var seg = segments[cur.Seg];
            // looking out of the node along the drawing direction (an arm starting there) its left is the segment's left
            bool right = cur.AtStart ? !left : left;
            var side = right ? seg.Attributes.Right : seg.Attributes.Left;
            var edge = Edge(seg, right);
            if (!cur.AtStart) edge.Reverse();
            if (side.OuterDm > 0)   // the whole side: a bike path (#120) and its sidewalk
            {
                if (kerb.Count == 0) kerb.Add(edge[0]);   // else the last piece ended there
                var dir = edge.Count > 1 ? (edge[1].P - edge[0].P) : Outward(seg, cur.AtStart);
                dir = dir / Math.Max(dir.Length, 1e-9);
                // away from the carriageway: left of the outward direction for a left side
                var across = left ? new Vec2(-dir.Y, dir.X) : new Vec2(dir.Y, -dir.X);
                double w = side.OuterDm / 10.0;
                return new Chain(kerb, edge[0].P + across * w, w, side.KerbCm / 100f, dir * -1, side);
            }
            if (!IsStreet(seg)) return null;
            kerb.AddRange(kerb.Count == 0 ? edge : edge.Skip(1));
            for (int k = 1; k < edge.Count; k++) travelled += edge[k].P.DistanceTo(edge[k - 1].P);
            if (travelled > ChainReach) return null;
            // on to the next piece, if exactly one carries the street on
            var far = Key(Point(seg, cur.AtStart ? seg.PointCount - 1 : 0));
            if (!ends.TryGetValue(far, out var there) || there.Count != 2) return null;
            var next = there[0].Seg == cur.Seg ? there[1] : there[0];
            if (next.Seg == cur.Seg) return null;
            cur = next;
        }
        return null;
    }

    private static RoadAreaProp? Build(TileId id, IReadOnlyList<RoadSegment> segments, Chain a, Chain b,
        List<(Vec2 P, float Y)>? ring, RoadJunction? cap, Facades facades, Stats stats, KerbArc? arc,
        (PathEnd? A, PathEnd? B) ends, out bool covered)
    {
        covered = false;
        // kerb: A's sidewalk start in to the node, round the cap, out to B's sidewalk start; beside a widening
        // (#711) in to A's mouth, round the kerb arc, out from B's mouth: the cap's own corner lies under the widening
        var poly = new List<(Vec2 P, float Y)>();
        if (arc != null)
        {
            for (int k = a.Kerb.Count - 1; k >= 0; k--)
                if ((arc.Line[0].P - a.Kerb[k].P).Dot(a.Inward) > 0.05) poly.Add(a.Kerb[k]);
            poly.AddRange(arc.Line);
            foreach (var p in b.Kerb)
                if ((arc.Line[^1].P - p.P).Dot(b.Inward) > 0.05) poly.Add(p);
        }
        else
        {
            for (int k = a.Kerb.Count - 1; k >= 0; k--) poly.Add(a.Kerb[k]);
            if (ring != null) poly.AddRange(RingBetween(ring, a.Kerb[0].P, b.Kerb[0].P));
            poly.AddRange(b.Kerb);
        }
        // the corner as it would be with no side carried on: what is left of it before the carried bands' starts (#711)
        var whole = new List<(Vec2 P, float Y)>(poly);
        int wholeKerb = poly.Count;   // its kerb line first
        if (b.Width > 0) whole.Add((b.Out, b.Kerb[^1].Y));
        if (Meet(b.Out, b.Inward, a.Out, a.Inward) is { } wc && wc.DistanceTo(a.Out) < 8 && wc.DistanceTo(b.Out) < 8)
            whole.Add((wc, (a.Kerb[^1].Y + b.Kerb[^1].Y) * 0.5f));
        if (a.Width > 0 && whole[^1].P.DistanceTo(a.Out) > 0.1) whole.Add((a.Out, a.Kerb[^1].Y));
        var (chainA, chainB) = (a, b);
        // (#711) beside a side carried on to the kerb the corner is laid in its sides' bands, round the carried ones and off any
        // road or footpath (the path runs on round the corner); else as before, from where the carried side meets the kerb
        if ((ends.A ?? ends.B) != null)
        {
            int gapsBefore = stats.Gaps;
            Gap(out covered);
            if (stats.Gaps > gapsBefore) return null;
            covered = false;
        }
        // a side carried on to the kerb (#711) covers the kerb up to where its outer edge meets it: the corner starts there,
        // narrowed to nothing at that end, and its outer edge is the carried side's (out along the arm from there)
        if (ends.A is { } ea)
        {
            var (s, q) = Project(poly, ea.At);
            poly = [q, .. poly.Skip(s + 1)];
            a = a with { Kerb = [q], Out = q.P, Width = 0, Inward = ea.Out };
        }
        if (ends.B is { } eb)
        {
            var (s, q) = Project(poly, eb.At);
            poly = [.. poly.Take(s + 1), q];
            b = b with { Kerb = [q], Out = q.P, Width = 0, Inward = eb.Out };
        }
        poly = Thin(Dedupe(poly));
        if ((ends.A ?? ends.B) != null && (poly.Count < 2 || PolylineLength(poly) < 0.02)) return Gap(out covered);
        int kerbCount = poly.Count;
        if (kerbCount < 2) { stats.Shape++; return null; }

        float ya = a.Kerb[^1].Y, yb = b.Kerb[^1].Y;
        // the outer edges, carried on toward the node, meet at the block's corner
        var corner = Meet(b.Out, b.Inward, a.Out, a.Inward);
        bool squared = corner is { } c && c.DistanceTo(a.Out) < 15 && c.DistanceTo(b.Out) < 15;
        // (#711: an end narrowed to nothing has its outer point on the kerb already)
        var withCorner = new List<(Vec2 P, float Y)>(poly);
        if (b.Width > 0) withCorner.Add((b.Out, yb));
        if (squared) withCorner.Add((corner!.Value, (ya + yb) * 0.5f));
        if (a.Width > 0) withCorner.Add((a.Out, ya));
        var chord = new List<(Vec2 P, float Y)>(poly);
        if (b.Width > 0) chord.Add((b.Out, yb));
        if (a.Width > 0) chord.Add((a.Out, ya));
        var bandOuter = Band(poly, a, b, p => facades.Occupied(id.MinE + p.X, id.MaxN + p.Y));
        // squared only where the corner is compact (near a right angle): at an acute junction the
        // outer edges meet deep inside the block, and the band along the kerb is the sidewalk
        bool compact = squared && corner!.Value.DistanceTo(a.Out) < 8 && corner.Value.DistanceTo(b.Out) < 8;

        string? why = null;
        if (compact && Polygon(id, segments, withCorner, kerbCount, cap, facades, a, out why) is { } square)
        {
            stats.Squared++;
            return square;
        }
        if (Traced(id, poly[0].P)) Console.WriteLine($"[corner]   squared: {(corner is null ? "outer edges do not meet ahead" : !compact ? "not compact" : why)}");
        if (Strip(id, segments, poly, bandOuter, facades, a, stats, out var bandWhy) is { } strip) return strip;
        if (Traced(id, a.Kerb[0].P)) Console.WriteLine($"[corner]   band: {bandWhy}");
        if (Polygon(id, segments, chord, kerbCount, cap, facades, a, out why) is { } cut) return cut;
        // (#711) beside a side carried on to the kerb, what is left may be a sliver: nothing to lay, the sides meet
        if ((ends.A ?? ends.B) != null && Math.Abs(SignedArea(withCorner.Select(p => p.P).ToList())) < 0.2) return Gap(out covered);
        if (why != null && why.StartsWith("wall")) stats.Facade++; else if (why == "road") stats.Road++; else stats.Shape++;
        if (Traced(id, a.Kerb[0].P)) Console.WriteLine($"[corner]   rejected: {why}");
        return null;

        // (#711) the carried bands start square across their arm at its mouth; where the sidewalk starts further out on a slanted
        // line (a skewed mouth, a junction of several nodes) the wedge between is the corner's: the corner as it would be with no
        // side carried on, less the carried bands themselves (their triangles), else covered
        RoadAreaProp? Gap(out bool covered)
        {
            covered = true;
            // only a compact corner: both sidewalks start near it (else the corner is a long sliver along an arm)
            if (PolylineLength(chainA.Kerb) > GapReach || PolylineLength(chainB.Kerb) > GapReach) return null;
            // in triangles first: clipping a convex piece never leaves a degenerate one
            var wholeTris = EarClip.Triangulate(whole.Select(p => p.P).ToList());
            if (wholeTris.Count != 3 * (whole.Count - 2)) return null;
            var wholePieces = Enumerable.Range(0, wholeTris.Count / 3)
                .Select(t => new List<(Vec2 P, float Y)> { whole[wholeTris[t * 3]], whole[wholeTris[t * 3 + 1]], whole[wholeTris[t * 3 + 2]] }).ToList();
            var pieces = wholePieces;
            foreach (var tri in (_bandTris ?? []).Concat(RoadTriangles(segments, whole)))
                pieces = [.. pieces.SelectMany(pc => Subtract(pc, tri)).Select(Dedupe)
                    .Where(pc => pc.Count >= 3 && Math.Abs(SignedArea(pc.Select(p => p.P).ToList())) >= 0.005)];
            string? gapWhy = null;
            double area = pieces.Sum(pc => Math.Abs(SignedArea(pc.Select(p => p.P).ToList())));
            var laid = new List<List<(Vec2 P, float Y)>>();
            if (area >= 0.2)
                foreach (var pc in pieces)
                {
                    if (Check(id, segments, pc, 0, cap, facades, minArea: 0.005, footpaths: false) is { } why) gapWhy = why;   // a footpath ends in it
                    else laid.Add(pc);
                }
            if (Traced(id, chainA.Out)) Console.WriteLine($"[corner]   the wedge before the carried bands: {area:F2} m2 in {laid.Count} of {pieces.Count} part(s){(gapWhy != null ? ", " + gapWhy : "")}");
            if (laid.Count == 0) return null;
            // in its sides' bands round the kerb (the user: the path runs on round the corner to the crossing): each inner band at its
            // offsets from the corner's kerb line, the outer one (the sidewalk) the rest; the side with a path gives the profile
            // the corner continues both sides' profiles round the curve, blended from A's at its end to B's at its end (the user's
            // playtest: one continuous surface, the only steps the kerb to the road; the path rises smoothly from one side's level
            // to the other's). Where only one side has a path, the corner is the other side's sidewalk at both ends
            var sideA = chainA.Side;
            var sideB = chainB.Side;
            if (sideA.HasTrack != sideB.HasTrack) sideA = sideB = sideA.HasTrack ? sideB : sideA;
            var slotsA = Slots(RoadStreetSection.For(sideA), chainA);
            var slotsB = Slots(RoadStreetSection.For(sideB), chainB);
            if (Traced(id, chainA.Out)) Console.WriteLine($"[corner]   wedge bands: A {string.Join(" ", slotsA.Select(s => $"{s.W:F2}@{s.H0:F2}-{s.H1:F2}"))}; B {string.Join(" ", slotsB.Select(s => $"{s.W:F2}@{s.H0:F2}-{s.H1:F2}"))}");
            var kerbLine = whole.Take(wholeKerb).ToList();
            // one welded mesh per band (type, base height): vertices at one point are one vertex, so an edge two pieces share has
            // no skirt; only the band's outline gets one. A band whose height varies carries the difference in its vertices
            var bands = new Dictionary<(AreaPropType Type, float Height, bool Kerb), (List<float> V, List<ushort> I, Dictionary<(long, long, long), ushort> At)>();
            void Lay(AreaPropType type, float height, List<(Vec2 P, float Y)> part)
            {
                bool kerb = type == AreaPropType.Kerb;
                if (!bands.TryGetValue((type, height, kerb), out var mesh)) bands[(type, height, kerb)] = mesh = ([], [], new());
                if (mesh.V.Count / 3 + part.Count > ushort.MaxValue) return;
                ushort Index((Vec2 P, float Y) q)
                {
                    var key = ((long)Math.Round(q.P.X * 1000), (long)Math.Round(q.P.Y * 1000), (long)Math.Round(q.Y * 1000));
                    if (mesh.At.TryGetValue(key, out var i)) return i;
                    i = (ushort)(mesh.V.Count / 3);
                    mesh.V.AddRange([(float)q.P.X, q.Y, (float)-q.P.Y]);
                    mesh.At[key] = i;
                    return i;
                }
                var idx = part.Select(Index).ToList();
                for (int t = 1; t + 1 < idx.Count; t++)
                    if (idx[0] != idx[t] && idx[t] != idx[t + 1] && idx[0] != idx[t + 1]) mesh.I.AddRange([idx[0], idx[t], idx[t + 1]]);   // convex: a fan
            }
            // the bands by distance from the kerb line (offset lines fold over where the kerb turns tighter than the corner is wide):
            // the corner cut into small triangles, each split where its distance crosses a band's edge, every point's height the
            // blended profile's at its distance, so the surface is continuous and only a real kerb (a jump in height) shows a face
            if (kerbLine.Count < 2) return null;
            // how far into the corner a point lies from each side's end (the line across that street where its side ends): the
            // blend runs from one end to the other, so a whole end line has its street's profile exactly (the user's playtest: a
            // blend by the nearest kerb point drifted from the street's heights toward the corner's outside)
            var inA = chainA.Inward / Math.Max(chainA.Inward.Length, 1e-9);
            var inB = chainB.Inward / Math.Max(chainB.Inward.Length, 1e-9);
            (double D, double F) Field(Vec2 p)
            {
                double best = double.MaxValue;
                for (int j = 0; j + 1 < kerbLine.Count; j++)
                {
                    Vec2 a = kerbLine[j].P, ab = kerbLine[j + 1].P - a;
                    double len2 = ab.Dot(ab), t = len2 < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
                    best = Math.Min(best, p.DistanceTo(a + ab * t));
                }
                double dA = Math.Max(0, (p - kerbLine[0].P).Dot(inA)), dB = Math.Max(0, (p - kerbLine[^1].P).Dot(inB));
                // near a side's end the distance is the street's own: straight across it from its kerb line, so the bands meet
                // the street's at the joint, easing into the distance from the curved kerb over JointEase
                double wA = Math.Clamp(1 - dA / JointEase, 0, 1), wB = Math.Clamp(1 - dB / JointEase, 0, 1);
                double across = best;
                if (wA > 0) across += (Math.Abs((p - kerbLine[0].P).Cross(inA)) - across) * wA;
                if (wB > 0) across += (Math.Abs((p - kerbLine[^1].P).Cross(inB)) - across) * wB;
                return (across, dA + dB < 1e-9 ? 0.5 : dA / (dA + dB));
            }
            int count = slotsA.Count;
            double Width(int s, double f) => slotsA[s].W + (slotsB[s].W - slotsA[s].W) * f;
            double Start(int s, double f) { double c = 0; for (int k = 0; k < s; k++) c += Width(k, f); return c; }
            float HeightAt(int s, double d, double f)
            {
                float h0 = (float)(slotsA[s].H0 + (slotsB[s].H0 - slotsA[s].H0) * f), h1 = (float)(slotsA[s].H1 + (slotsB[s].H1 - slotsA[s].H1) * f);
                double w = Width(s, f);
                return s == count - 1 || w < 1e-6 ? h1 : h0 + (h1 - h0) * (float)Math.Clamp((d - Start(s, f)) / w, 0, 1);
            }
            // a point of the corner: where it is, the ground under it, and its place in the field
            static (Vec2 P, float Y, double D, double F) Mid((Vec2 P, float Y, double D, double F) a, (Vec2 P, float Y, double D, double F) b, double t)
            {
                // the same point from either side of a shared edge: interpolate from the lower endpoint
                if (a.P.X > b.P.X || a.P.X == b.P.X && a.P.Y > b.P.Y) { (a, b) = (b, a); t = 1 - t; }
                return (a.P + (b.P - a.P) * t, a.Y + (b.Y - a.Y) * (float)t, a.D + (b.D - a.D) * t, a.F + (b.F - a.F) * t);
            }
            var tris = new List<(Vec2 P, float Y, double D, double F)[]>();
            void Refine((Vec2 P, float Y, double D, double F)[] t, int depth)
            {
                int longest = 0;
                double lmax = 0;
                for (int e = 0; e < 3; e++)
                {
                    double l = t[e].P.DistanceTo(t[(e + 1) % 3].P);
                    if (l > lmax) { lmax = l; longest = e; }
                }
                if (lmax <= CornerCell || depth > 12) { tris.Add(t); return; }
                var (a, b, c) = (t[longest], t[(longest + 1) % 3], t[(longest + 2) % 3]);
                var m = Mid(a, b, 0.5);
                m = (m.P, m.Y, Field(m.P).D, Field(m.P).F);
                Refine([a, m, c], depth + 1);
                Refine([m, b, c], depth + 1);
            }
            foreach (var pc in laid)
            {
                var pts = pc.Select(q => { var (d, f) = Field(q.P); return (q.P, q.Y, d, f); }).ToList();
                for (int t = 1; t + 1 < pts.Count; t++) Refine([pts[0], pts[t], pts[t + 1]], 0);
            }
            // each triangle cut to each band: where the distance lies between the band's start and end (both moving with f)
            List<(Vec2 P, float Y, double D, double F)> ClipBy(List<(Vec2 P, float Y, double D, double F)> poly, Func<(Vec2 P, float Y, double D, double F), double> g)
            {
                var r = new List<(Vec2 P, float Y, double D, double F)>();
                for (int i = 0; i < poly.Count; i++)
                {
                    var p = poly[i];
                    var q = poly[(i + 1) % poly.Count];
                    double gp = g(p), gq = g(q);
                    if (gp >= 0) r.Add(p);
                    if ((gp >= 0) != (gq >= 0)) r.Add(Mid(p, q, gp / (gp - gq)));
                }
                return r;
            }
            foreach (var tri in tris)
                for (int s = 0; s < count; s++)
                {
                    var part = tri.ToList();
                    if (s > 0) part = ClipBy(part, v => v.D - Start(s, v.F));
                    if (s + 1 < count && part.Count >= 3) part = ClipBy(part, v => Start(s + 1, v.F) - v.D - 1e-9);
                    if (part.Count < 3 || Math.Abs(SignedArea(part.Select(v => v.P).ToList())) < 1e-5) continue;
                    var type = slotsA[s].Type;
                    // a raised band's own height is its lowest; what it rises above that is in its vertices
                    float baseH = type == AreaPropType.Kerb ? 0f : MathF.Min(MathF.Min(slotsA[s].H0, slotsB[s].H0), MathF.Min(slotsA[s].H1, slotsB[s].H1));
                    Lay(type, baseH, part.Select(v => (v.P, v.Y + HeightAt(s, v.D, v.F) - baseH)).ToList());
                }
            if (bands.Count == 0) return null;
            stats.Gaps++;
            stats.Built++;
            stats.Area += area;
            if (Environment.GetEnvironmentVariable("CORNERGAPS") == "1") Console.WriteLine($"[gap] {id.MinE + chainA.Out.X:F0},{id.MaxN + chainA.Out.Y:F0} {area:F1} m2");
            foreach (var ((type, height, isKerb), mesh) in bands)
            {
                var band = new RoadAreaProp
                {
                    Type = type, Flags = height > 0 || isKerb ? PropFlags.Solid : PropFlags.None, Height = height,
                    Vertices = [.. mesh.V], Indices = [.. mesh.I],
                };
                ClearOfBands.AddOrUpdate(band, true);
                _extra!.Add(band);
            }
            return null;   // laid as its bands (covered stays true: no single corner piece)
        }

        RoadAreaProp? Polygon(TileId id, IReadOnlyList<RoadSegment> segments, List<(Vec2 P, float Y)> candidate, int kerbCount,
            RoadJunction? cap, Facades facades, Chain a, out string? why, double minArea = 0.2)
        {
            why = Check(id, segments, candidate, kerbCount, cap, facades, minArea);
            if (why != null) return null;
            var plan = candidate.Select(p => p.P).ToList();
            var tris = EarClip.Triangulate(plan);
            if (tris.Count != 3 * (plan.Count - 2)) { why = "shape"; return null; }
            stats.Built++;
            stats.Area += Math.Abs(SignedArea(plan));
            var verts = new float[candidate.Count * 3];
            for (int k = 0; k < candidate.Count; k++)
            {
                verts[k * 3] = (float)candidate[k].P.X;
                verts[k * 3 + 1] = candidate[k].Y;
                verts[k * 3 + 2] = (float)-candidate[k].P.Y;
            }
            return new RoadAreaProp
            {
                Type = AreaPropType.Sidewalk,
                Flags = a.Kerb_ > 0 ? PropFlags.Solid : PropFlags.None,
                Height = a.Kerb_,
                Vertices = verts,
                Indices = tris.Select(t => (ushort)t).ToArray(),
            };
        }
    }

    /// <summary>
    /// The band along the kerb as a strip of quads (kerb point k, k+1, their outer points), each
    /// checked on its own: no simple outline needed, so the kink where an arm's edge turns into the
    /// cap's curve (where an offset outline crosses itself) costs nothing; neighbouring quads may
    /// overlap there at one height. A quad standing on a wall or a carriageway is left out.
    /// </summary>
    private static RoadAreaProp? Strip(TileId id, IReadOnlyList<RoadSegment> segments, List<(Vec2 P, float Y)> kerb,
        List<(Vec2 P, float Y)> outer, Facades facades, Chain a, Stats stats, out string? why)
    {
        int n = kerb.Count;
        var indices = new List<ushort>();
        double area = 0;
        int dropped = 0;
        why = null;
        double bx0 = Math.Min(kerb.Min(p => p.P.X), outer.Min(p => p.P.X)), bx1 = Math.Max(kerb.Max(p => p.P.X), outer.Max(p => p.P.X));
        double by0 = Math.Min(kerb.Min(p => p.P.Y), outer.Min(p => p.P.Y)), by1 = Math.Max(kerb.Max(p => p.P.Y), outer.Max(p => p.P.Y));
        var near = Near(segments, bx0, bx1, by0, by1);
        for (int k = 0; k + 1 < n; k++)
        {
            var quad = new List<Vec2> { kerb[k].P, kerb[k + 1].P, outer[k + 1].P, outer[k].P };
            double qa = Math.Abs(SignedArea(quad));
            if (qa < 1e-3) continue;
            if (QuadBlocked(id, near, quad, facades) is { } blocked) { why = blocked; dropped++; continue; }
            area += qa;
            indices.AddRange([(ushort)k, (ushort)(k + 1), (ushort)(n + k + 1), (ushort)k, (ushort)(n + k + 1), (ushort)(n + k)]);
        }
        if (indices.Count == 0 || area < 0.2 || dropped * 2 > n) { why ??= "shape"; return null; }
        if (n * 2 > ushort.MaxValue) { why = "shape"; return null; }
        var verts = new float[n * 2 * 3];
        for (int k = 0; k < n; k++)
        {
            (verts[k * 3], verts[k * 3 + 1], verts[k * 3 + 2]) = ((float)kerb[k].P.X, kerb[k].Y, (float)-kerb[k].P.Y);
            int o = (n + k) * 3;
            (verts[o], verts[o + 1], verts[o + 2]) = ((float)outer[k].P.X, outer[k].Y, (float)-outer[k].P.Y);
        }
        stats.Built++;
        stats.Banded++;
        stats.Area += area;
        return new RoadAreaProp
        {
            Type = AreaPropType.Sidewalk,
            Flags = a.Kerb_ > 0 ? PropFlags.Solid : PropFlags.None,
            Height = a.Kerb_,
            Vertices = verts,
            Indices = indices.ToArray(),
        };
    }

    /// <summary>The tile's turn-lane widenings while a tile is planned (<see cref="Plan"/>).</summary>
    [ThreadStatic] private static List<RoadAreaProp>? _pavement;

    /// <summary>Whether a plan point (x east, y = -z) lies on one of the tile's turn-lane widenings.</summary>
    private static bool OnPavement(Vec2 q)
    {
        if (_pavement is null || _pavement.Count == 0) return false;
        double x = q.X, z = -q.Y;
        foreach (var a in _pavement)
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
    }

    /// <summary>A wall or a carriageway inside a quad (sampled every 0.5 m), or null.</summary>
    private static string? QuadBlocked(TileId id, List<RoadSegment> segments, List<Vec2> quad, Facades facades)
    {
        double minX = quad.Min(p => p.X), maxX = quad.Max(p => p.X), minY = quad.Min(p => p.Y), maxY = quad.Max(p => p.Y);
        for (double x = minX + Sample / 2; x < maxX; x += Sample)
            for (double y = minY + Sample / 2; y < maxY; y += Sample)
            {
                var q = new Vec2(x, y);
                if (!Inside(quad, q)) continue;
                if (facades.Occupied(id.MinE + x, id.MaxN + y)) return "wall";
                foreach (var s in segments)
                    if (DistanceToLine(s, q) < s.Width * 0.5 - 0.3) return "road";
                if (OnPavement(q)) return "road";
            }
        return null;
    }

    /// <summary>The at-grade roads with a vertex within 20 m (plus their width) of the box.</summary>
    private static List<RoadSegment> Near(IReadOnlyList<RoadSegment> segments, double minX, double maxX, double minY, double maxY)
    {
        var near = new List<RoadSegment>();
        foreach (var s in segments)
        {
            if (!IsRoad(s) || (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) continue;
            double pad = s.Width + 20;
            for (int i = 0; i < s.PointCount; i++)
            {
                double x = s.Points[i * 3], y = -s.Points[i * 3 + 2];
                if (x > minX - pad && x < maxX + pad && y > minY - pad && y < maxY + pad) { near.Add(s); break; }
            }
        }
        return near;
    }

    /// <summary>
    /// The kerb line from A's sidewalk start round the corner to B's, and the same line offset away
    /// from the road by the sidewalk's width (A's at one end, B's at the other, blended along it):
    /// a sidewalk that follows the kerb round the corner at its own width, as a real one does. It
    /// narrows to stop 0.2 m short of a wall standing in its way (a building on the corner).
    /// </summary>
    private static List<(Vec2 P, float Y)> Band(List<(Vec2 P, float Y)> kerb, Chain a, Chain b, Func<Vec2, bool> wall)
    // returns the outer line, point for point with the kerb line
    {
        int n = kerb.Count;
        var along = new double[n];
        for (int k = 1; k < n; k++) along[k] = along[k - 1] + kerb[k].P.DistanceTo(kerb[k - 1].P);
        double total = Math.Max(along[^1], 1e-6);
        // which side of the line is away from the road: the side A's own sidewalk lies on
        var d0 = kerb[1].P - kerb[0].P;
        double sign = Math.Sign(new Vec2(-d0.Y, d0.X).Dot(a.Out - kerb[0].P));
        if (a.Width <= 0)   // (#711) A's end narrowed to nothing beside a side carried on to the kerb: B's tells
        {
            var d1 = kerb[^1].P - kerb[^2].P;
            sign = Math.Sign(new Vec2(-d1.Y, d1.X).Dot(b.Out - kerb[^1].P));
        }
        if (sign == 0) sign = 1;
        var outer = new List<(Vec2 P, float Y)>(n);
        for (int k = 0; k < n; k++)
        {
            var t = kerb[Math.Min(n - 1, k + 1)].P - kerb[Math.Max(0, k - 1)].P;
            double l = t.Length;
            if (l < 1e-9) { outer.Add(kerb[k]); continue; }
            var normal = new Vec2(-t.Y / l, t.X / l) * sign;
            double w = a.Width + (b.Width - a.Width) * along[k] / total;
            for (double d = 0.25; d <= w + 0.2; d += 0.25)
                if (wall(kerb[k].P + normal * d)) { w = Math.Max(0.3, Math.Min(w, d - 0.2)); break; }
            outer.Add((kerb[k].P + normal * w, kerb[k].Y));
        }
        outer[0] = (a.Out, kerb[0].Y);
        outer[^1] = (b.Out, kerb[^1].Y);
        Unfold(outer, kerb);
        return outer;
    }

    /// <summary>
    /// An outer line offset further than the kerb's radius round a tight corner runs backwards or
    /// crosses itself, and the band folds over at two heights (#120: a side with a bike path is up
    /// to 5 m wide). An outer point stepping back against the kerb's direction holds the previous
    /// one; every remaining loop is cut at its crossing, the points between collapsing onto it at
    /// their mean height. Their quads become triangles fanning to that point.
    /// </summary>
    private static void Unfold(List<(Vec2 P, float Y)> outer, List<(Vec2 P, float Y)> kerb)
    {
        for (int k = 1; k + 1 < outer.Count; k++)
            if ((outer[k].P - outer[k - 1].P).Dot(kerb[k].P - kerb[k - 1].P) <= 0) outer[k] = outer[k - 1];
        // the last step into B's end may run back too: hold from that end
        for (int k = outer.Count - 2; k > 0; k--)
            if ((outer[k + 1].P - outer[k].P).Dot(kerb[k + 1].P - kerb[k].P) <= 0) outer[k] = outer[k + 1];
        for (int pass = 0; pass < 32; pass++)
        {
            bool cut = false;
            for (int i = 0; i + 2 < outer.Count && !cut; i++)
                for (int j = outer.Count - 2; j > i + 1 && !cut; j--)
                {
                    if (Crossing(outer[i].P, outer[i + 1].P, outer[j].P, outer[j + 1].P) is not { } x) continue;
                    float y = 0;
                    for (int k = i + 1; k <= j; k++) y += outer[k].Y;
                    y /= j - i;
                    for (int k = i + 1; k <= j; k++) outer[k] = (x, y);
                    cut = true;
                }
            if (!cut) return;
        }
    }

    private static Vec2? Crossing(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        var r = b - a;
        var s = d - c;
        double den = r.Cross(s);
        if (Math.Abs(den) < 1e-12) return null;
        double t = (c - a).Cross(s) / den, u = (c - a).Cross(r) / den;
        return t > 1e-6 && t < 1 - 1e-6 && u > 1e-6 && u < 1 - 1e-6 ? a + r * t : null;
    }

    /// <summary>
    /// The kerb line with the points it can do without: the arms' edges come at 5 cm chords and
    /// copying every one into a corner patch cost more bytes than the rest of the street. A point
    /// goes if the line from the last kept point to the next passes within 2 cm of it in plan and
    /// 1 cm in height.
    /// </summary>
    private static List<(Vec2 P, float Y)> Thin(List<(Vec2 P, float Y)> line)
    {
        if (line.Count < 3) return line;
        var keep = new List<(Vec2 P, float Y)> { line[0] };
        int from = 0;
        for (int to = 2; to < line.Count; to++)
        {
            bool fits = true;
            for (int k = from + 1; k < to && fits; k++)
            {
                var ab = line[to].P - line[from].P;
                double l2 = ab.LengthSquared;
                double t = l2 < 1e-12 ? 0 : Math.Clamp((line[k].P - line[from].P).Dot(ab) / l2, 0, 1);
                fits = line[k].P.DistanceTo(line[from].P + ab * t) <= 0.02
                    && Math.Abs(line[from].Y + (line[to].Y - line[from].Y) * t - line[k].Y) <= 0.01;
            }
            if (fits) continue;
            keep.Add(line[to - 1]);
            from = to - 1;
        }
        keep.Add(line[^1]);
        return keep;
    }

    /// <summary>Consecutive points under a centimetre apart are one (the chains meet at shared points).</summary>
    private static List<(Vec2 P, float Y)> Dedupe(List<(Vec2 P, float Y)> poly)
    {
        var result = new List<(Vec2 P, float Y)>(poly.Count);
        foreach (var p in poly)
            if (result.Count == 0 || result[^1].P.DistanceTo(p.P) > 0.01) result.Add(p);
        while (result.Count > 2 && result[0].P.DistanceTo(result[^1].P) <= 0.01) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>Null if the patch may stand there, else what is in its way.</summary>
    private static string? Check(TileId id, IReadOnlyList<RoadSegment> segments, List<(Vec2 P, float Y)> poly,
        int kerbCount, RoadJunction? cap, Facades facades, double minArea = 0.2, bool footpaths = true)
    {
        var plan = poly.Select(p => p.P).ToList();
        double area = Math.Abs(SignedArea(plan));
        if (area < minArea || area > MaxArea || SelfIntersects(plan)) return "shape";
        double minX = plan.Min(p => p.X), maxX = plan.Max(p => p.X), minY = plan.Min(p => p.Y), maxY = plan.Max(p => p.Y);
        var near = new List<RoadSegment>();
        foreach (var s in segments)
        {
            if (!IsRoad(s) || !footpaths && s.Class is RoadClass.Track or RoadClass.Path || (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) continue;
            double pad = s.Width;
            for (int i = 0; i < s.PointCount; i++)
            {
                double x = s.Points[i * 3], y = -s.Points[i * 3 + 2];
                if (x > minX - pad - 20 && x < maxX + pad + 20 && y > minY - pad - 20 && y < maxY + pad + 20) { near.Add(s); break; }
            }
        }
        for (double x = minX + Sample / 2; x < maxX; x += Sample)
            for (double y = minY + Sample / 2; y < maxY; y += Sample)
            {
                var q = new Vec2(x, y);
                if (!Inside(plan, q)) continue;
                if (facades.Occupied(id.MinE + x, id.MaxN + y))
                    return Debug != null ? string.Create(CultureInfo.InvariantCulture, $"wall at ({id.MinE + x:F1},{id.MaxN + y:F1}), patch of {plan.Count} points, {area:F0} m2") : "wall";
                foreach (var s in near)
                    if (DistanceToLine(s, q) < s.Width * 0.5 - 0.3) return "road";
                if (OnPavement(q)) return "road";
            }
        return null;
    }

    /// <summary>
    /// A side's profile in fixed slots, so two sides' blend band by band (#711): the sloped kerb at the road, verge, path, buffer,
    /// the sloped kerb up to the sidewalk, the sidewalk; a slot the side lacks has no width and its neighbour's height.
    /// </summary>
    private static List<(AreaPropType Type, double W, float H0, float H1)> Slots(RoadStreetSection.Profile? profile, Chain chain)
    {
        var slots = new List<(AreaPropType Type, double W, float H0, float H1)>
        {
            (AreaPropType.Kerb, 0, float.NaN, float.NaN), (AreaPropType.Grass, 0, float.NaN, float.NaN), (AreaPropType.BikePath, 0, float.NaN, float.NaN),
            (AreaPropType.Grass, 0, float.NaN, float.NaN), (AreaPropType.Kerb, 0, float.NaN, float.NaN), (AreaPropType.Sidewalk, 0, float.NaN, float.NaN),
        };
        if (profile is null) slots[5] = (AreaPropType.Sidewalk, chain.Width, chain.Kerb_, chain.Kerb_);
        else
        {
            bool pastTrack = false;
            for (int b = 0; b + 1 < profile.Count; b++)
            {
                double w = profile.D[b + 1] - profile.D[b];
                if (w < 1e-4) continue;
                var surface = profile.Surface[b];
                int slot = surface switch
                {
                    StreetSurface.Kerb => pastTrack ? 4 : 0,
                    StreetSurface.Verge => 1,
                    StreetSurface.Track => 2,
                    StreetSurface.Buffer => 3,
                    _ => 5,
                };
                if (surface is StreetSurface.Track or StreetSurface.Buffer) pastTrack = true;
                float h0 = surface == StreetSurface.Kerb ? profile.H[b] : profile.H[b + 1], h1 = profile.H[b + 1];
                var old = slots[slot];
                slots[slot] = (old.Type, old.W + w, float.IsNaN(old.H0) ? h0 : old.H0, h1);
            }
        }
        // empty slots take the height the band before them ends at
        float at = 0;
        for (int s = 0; s < slots.Count; s++)
        {
            if (float.IsNaN(slots[s].H0)) slots[s] = (slots[s].Type, 0, at, at);
            at = slots[s].H1;
        }
        return slots;
    }

    /// <summary>How far a point lies from a polygon's outline.</summary>
    private static double DistanceToPolygon(List<Vec2> poly, Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            Vec2 a = poly[i], b = poly[(i + 1) % poly.Count], ab = b - a;
            double t = ab.Dot(ab) < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / ab.Dot(ab), 0, 1);
            best = Math.Min(best, p.DistanceTo(a + ab * t));
        }
        return best;
    }

    /// <summary>A convex polygon cut to a triangle (#711): clipped by each of its edges, inside.</summary>
    private static List<(Vec2 P, float Y)> Into(List<(Vec2 P, float Y)> poly, List<(Vec2 P, float Y)> tri)
    {
        var (a, b, c) = (tri[0].P, tri[1].P, tri[2].P);
        if ((b - a).Cross(c - a) < 0) (b, c) = (c, b);   // counter-clockwise
        foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
        {
            poly = ClipBefore(poly, p, new Vec2(-(q - p).Y, (q - p).X));   // left of a counter-clockwise edge: inside
            if (poly.Count < 3) break;
        }
        return poly;
    }

    /// <summary>
    /// The part of a polygon on the far side of the line through <paramref name="start"/> across <paramref name="u"/> (the points
    /// p with (p - start)·u &gt; 0), heights interpolated (Sutherland-Hodgman against one half-plane, #711).
    /// </summary>
    private static List<(Vec2 P, float Y)> ClipBefore(List<(Vec2 P, float Y)> poly, Vec2 start, Vec2 u)
    {
        var r = new List<(Vec2 P, float Y)>();
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            double dp = (p.P - start).Dot(u), dq = (q.P - start).Dot(u);
            if (dp > 0) r.Add(p);
            if ((dp > 0) != (dq > 0))
            {
                double t = dp / (dp - dq);
                r.Add((p.P + (q.P - p.P) * t, p.Y + (float)((q.Y - p.Y) * t)));
            }
        }
        return r;
    }

    /// <summary>The outer corner where two sidewalks meet is rounded with this radius (m), less where an edge is short (#711, the user's rule).</summary>
    private const double OuterRound = 1.5;

    /// <summary>
    /// The outside of a corner where two sidewalks meet, rounded a bit (#711, the user's rule): where their outer edges meet at
    /// the block's corner, a sidewalk patch on the block's side fills it up to a
    /// curve tangent to both edges, <see cref="OuterRound"/> in radius. None where the edges meet far out, run on nearly
    /// straight or turn back sharply, or the patch would stand on a wall or a carriageway.
    /// </summary>
    private static RoadAreaProp? Fillet(TileId id, IReadOnlyList<RoadSegment> segments, RoadJunction? cap, Facades facades, Stats stats,
        Chain a, Chain b, (PathEnd? A, PathEnd? B) carried, bool pieceAtCorner, out string? why)
    {
        why = null;
        // each outer edge: where its sidewalk starts and its way toward the node (a side carried on to the kerb: its band's
        // outer edge carries on the same line); the lines meet at the block's corner, ahead of the starts or behind
        var (pa, ia) = (a.Out, a.Inward);
        var (pb, ib) = (b.Out, b.Inward);
        if (ia.Length < 1e-9 || ib.Length < 1e-9) { why = "no edge"; return null; }
        ia /= ia.Length;
        ib /= ib.Length;
        double den = ia.Cross(ib);
        if (Math.Abs(den) < 1e-6) { why = "edges parallel"; return null; }
        var x = pa + ia * ((pb - pa).Cross(ib) / den);
        if (x.DistanceTo(pa) > 8 || x.DistanceTo(pb) > 8) { why = "edges meet far out"; return null; }
        // only where both sidewalks reach that corner: a corner piece up to it, the sidewalk starting there, or a band carried through it
        bool Reaches(Chain c, PathEnd? e) => pieceAtCorner || c.Out.DistanceTo(x) < 0.5
            || e != null && DistanceToSegment(x, e.Start, e.At) < 0.5;
        if (!Reaches(a, carried.A) || !Reaches(b, carried.B)) { why = "the sidewalks do not reach the corner"; return null; }
        // along the block's sides, out from its corner
        Vec2 da = ia * -1, db = ib * -1;
        double angle = Math.Acos(Math.Clamp(da.Dot(db), -1, 1));
        if (angle < 20 * Math.PI / 180 || angle > 160 * Math.PI / 180) { why = $"angle {angle * 180 / Math.PI:F0}"; return null; }
        double t = Math.Min(OuterRound / Math.Tan(angle / 2), 3.0);
        float y = (a.Kerb[^1].Y + b.Kerb[^1].Y) * 0.5f;
        var poly = new List<(Vec2 P, float Y)> { (x, y) };
        Vec2 t1 = x + da * t, t2 = x + db * t;
        const int Steps = 8;
        for (int k = 0; k <= Steps; k++)
        {
            double s = (double)k / Steps, ms = 1 - s;
            poly.Add((t1 * (ms * ms) + x * (2 * ms * s) + t2 * (s * s), y));   // the curve, the corner its control
        }
        poly = Dedupe(poly);
        if (poly.Count < 4 || (why = Check(id, segments, poly, 0, cap, facades, minArea: 0.05)) != null) { why ??= "shape"; return null; }
        var plan = poly.Select(p => p.P).ToList();
        var tris = EarClip.Triangulate(plan);
        if (tris.Count != 3 * (plan.Count - 2)) { why = "shape"; return null; }
        stats.Fillets++;
        var verts = new float[poly.Count * 3];
        for (int k = 0; k < poly.Count; k++)
            (verts[k * 3], verts[k * 3 + 1], verts[k * 3 + 2]) = ((float)poly[k].P.X, poly[k].Y, (float)-poly[k].P.Y);
        return new RoadAreaProp
        {
            Type = AreaPropType.Sidewalk, Flags = a.Kerb_ > 0 ? PropFlags.Solid : PropFlags.None, Height = a.Kerb_,
            Vertices = verts, Indices = tris.Select(k => (ushort)k).ToArray(),
        };
    }

    private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double t = ab.Dot(ab) < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / ab.Dot(ab), 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>A kerb arc ends this close (m) to its cap's ring.</summary>
    private const double ArcReach = 4.0;

    /// <summary>Over this far (m) into a corner from a side's end its bands ease from the street's straight profile into the curve (#711).</summary>
    private const double JointEase = 1.5;

    /// <summary>A corner laid in bands is cut into triangles no longer than this (m) on a side, so its band edges follow the kerb (#711).</summary>
    private const double CornerCell = 0.35;

    /// <summary>A wedge before carried bands is laid only where both sidewalks start within this (m, along the kerb) of the corner (#711).</summary>
    private const double GapReach = 8.0;

    /// <summary>
    /// The car roads' ribbons near a polygon as plan triangles (#711): a corner laid round them never covers one. A footpath or
    /// track joining the junction ends in the corner instead (the corner stands a kerb above it).
    /// </summary>
    private static IEnumerable<(Vec2 A, Vec2 B, Vec2 C)> RoadTriangles(IReadOnlyList<RoadSegment> segments, List<(Vec2 P, float Y)> poly)
    {
        double x0 = poly.Min(p => p.P.X) - 5, x1 = poly.Max(p => p.P.X) + 5, y0 = poly.Min(p => p.P.Y) - 5, y1 = poly.Max(p => p.P.Y) + 5;
        foreach (var s in segments)
        {
            if (!IsRoad(s) || s.Class is RoadClass.Track or RoadClass.Path || (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) continue;
            var l = Edge(s, right: false);
            var r = Edge(s, right: true);
            for (int i = 0; i + 1 < l.Count; i++)
            {
                var (a, b, c, d) = (l[i].P, l[i + 1].P, r[i + 1].P, r[i].P);
                if (Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) < x0 || Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) > x1
                    || Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) < y0 || Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) > y1) continue;
                yield return (a, b, c);
                yield return (a, c, d);
            }
        }
    }

    /// <summary>Props a corner lays besides its own piece (a wedge in its sides' bands, #711), while a tile is planned.</summary>
    [ThreadStatic] private static List<RoadAreaProp>? _extra;

    /// <summary>The wedges laid in their sides' bands (#711): cut around the carried bands already, they are never replaced by them.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoadAreaProp, object> ClearOfBands = new();

    /// <summary>Whether a corner prop was cut around the carried bands already (<c>TileRewriter.Unbridged</c> keeps it).</summary>
    public static bool IsClearOfBands(RoadAreaProp prop) => ClearOfBands.TryGetValue(prop, out _);

    /// <summary>The tile's carried bands (paths through and to the kerb) as tile-local plan triangles, while a tile is planned (#711).</summary>
    [ThreadStatic] private static List<(Vec2 A, Vec2 B, Vec2 C)>? _bandTris;

    /// <summary>
    /// A polygon less a triangle (#711): the parts outside each of its edges in turn and inside the ones before (a convex
    /// shape's complement cut into half-planes), heights kept; the polygon whole where their boxes do not meet.
    /// </summary>
    private static IEnumerable<List<(Vec2 P, float Y)>> Subtract(List<(Vec2 P, float Y)> poly, (Vec2 A, Vec2 B, Vec2 C) tri)
    {
        double x0 = poly.Min(p => p.P.X), x1 = poly.Max(p => p.P.X), y0 = poly.Min(p => p.P.Y), y1 = poly.Max(p => p.P.Y);
        if (Math.Max(tri.A.X, Math.Max(tri.B.X, tri.C.X)) <= x0 || Math.Min(tri.A.X, Math.Min(tri.B.X, tri.C.X)) >= x1
            || Math.Max(tri.A.Y, Math.Max(tri.B.Y, tri.C.Y)) <= y0 || Math.Min(tri.A.Y, Math.Min(tri.B.Y, tri.C.Y)) >= y1)
        {
            yield return poly;
            yield break;
        }
        var (a, b, c) = (tri.A, tri.B, tri.C);
        if ((b - a).Cross(c - a) < 0) (b, c) = (c, b);   // counter-clockwise
        if (Math.Abs((b - a).Cross(c - a)) < 1e-9) { yield return poly; yield break; }
        var rest = poly;
        foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
        {
            var outward = new Vec2((q - p).Y, -(q - p).X);   // right of a counter-clockwise edge: outside
            var outside = ClipBefore(rest, p, outward);
            if (outside.Count >= 3) yield return outside;
            rest = ClipBefore(rest, p, outward * -1);
            if (rest.Count < 3) yield break;
        }
    }

    /// <summary>The tile's sides carried on to the kerb while a tile is planned (<see cref="Plan"/>, #711).</summary>
    [ThreadStatic] private static IReadOnlyList<PathEnd>? _pathEnds;

    /// <summary>
    /// The side carried on to the kerb (#711) on an arm's left (or right) looking out from <paramref name="node"/>: its
    /// direction within <see cref="ArcArmCos"/> of the arm's, on that side of it, the nearest within 25 m; or null.
    /// </summary>
    private static PathEnd? EndOf(Vec2 node, Vec2 armOut, bool left)
    {
        PathEnd? best = null;
        if (_pathEnds is null) return null;
        foreach (var e in _pathEnds)
        {
            if (e.Out.Dot(armOut) < ArcArmCos || e.At.DistanceTo(node) > 25 || (armOut.Cross(e.At - node) > 0) != left) continue;
            if (best == null || e.At.DistanceTo(node) < best.At.DistanceTo(node)) best = e;
        }
        return best;
    }

    /// <summary>The point of a polyline nearest <paramref name="p"/>, with its height, and the index of the piece it lies on.</summary>
    private static (int Piece, (Vec2 P, float Y) Point) Project(List<(Vec2 P, float Y)> line, Vec2 p)
    {
        if (line.Count == 1) return (0, line[0]);
        (int, (Vec2, float)) best = (0, line[0]);
        double nearest = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var ab = line[i + 1].P - line[i].P;
            double l2 = ab.LengthSquared, t = l2 < 1e-12 ? 0 : Math.Clamp((p - line[i].P).Dot(ab) / l2, 0, 1);
            var q = line[i].P + ab * t;
            if (q.DistanceTo(p) >= nearest) continue;
            nearest = q.DistanceTo(p);
            best = (i, (q, line[i].Y + (line[i + 1].Y - line[i].Y) * (float)t));
        }
        return best;
    }

    private static double PolylineLength(List<(Vec2 P, float Y)> line)
    {
        double length = 0;
        for (int i = 1; i < line.Count; i++) length += line[i].P.DistanceTo(line[i - 1].P);
        return length;
    }

    /// <summary>
    /// The kerb arc of the corner between arm A's left and arm B's right (#711), matched by the arms' directions
    /// out of the junction, not by distance: the best match within <see cref="ArcArmCos"/> on both arms, or null.
    /// </summary>
    private static KerbArc? ArcOf(List<KerbArc>? arcs, Vec2 outA, Vec2 outB)
    {
        if (arcs is null) return null;
        KerbArc? best = null;
        double score = double.MinValue;
        foreach (var k in arcs)
        {
            double sa = k.OutA.Dot(outA), sb = k.OutB.Dot(outB);
            if (sa < ArcArmCos || sb < ArcArmCos || sa + sb <= score) continue;
            (best, score) = (k, sa + sb);
        }
        return best;
    }

    /// <summary>The ring's vertices strictly between the two points nearest <paramref name="from"/> and <paramref name="to"/>, the shorter way round.</summary>
    private static IEnumerable<(Vec2 P, float Y)> RingBetween(List<(Vec2 P, float Y)> ring, Vec2 from, Vec2 to)
    {
        int n = ring.Count;
        int i0 = Nearest(ring, from), i1 = Nearest(ring, to);
        double fwd = 0, back = 0;
        for (int i = i0; i != i1; i = (i + 1) % n) fwd += ring[i].P.DistanceTo(ring[(i + 1) % n].P);
        for (int i = i0; i != i1; i = (i - 1 + n) % n) back += ring[i].P.DistanceTo(ring[(i - 1 + n) % n].P);
        int step = fwd <= back ? 1 : n - 1;
        for (int i = (i0 + step) % n; i != i1; i = (i + step) % n) yield return ring[i];
    }

    private static int Nearest(List<(Vec2 P, float Y)> ring, Vec2 p)
    {
        int best = 0;
        for (int i = 1; i < ring.Count; i++) if (ring[i].P.DistanceSquaredTo(p) < ring[best].P.DistanceSquaredTo(p)) best = i;
        return best;
    }

    /// <summary>Where the line through <paramref name="p"/> along <paramref name="u"/> meets the one through <paramref name="q"/> along <paramref name="v"/>, ahead of both.</summary>
    private static Vec2? Meet(Vec2 p, Vec2 u, Vec2 q, Vec2 v)
    {
        double den = u.Cross(v);
        if (Math.Abs(den) < 1e-6) return null;
        var d = q - p;
        double t = d.Cross(v) / den, s = d.Cross(u) / den;
        if (t < -1 || s < -1) return null;
        return p + u * t;
    }

    /// <summary>The ribbon's edge on one side, as <c>RoadMeshBuilder</c> offsets it (per-vertex bisector), in plan (north up) with heights.</summary>
    private static List<(Vec2 P, float Y)> Edge(RoadSegment seg, bool right)
    {
        int n = seg.PointCount;
        var result = new List<(Vec2, float)>(n);
        double half = seg.Width * 0.5, sign = right ? 1 : -1;
        // the kerb runs past a turn lane's widening where the side is shifted out (#120)
        var side = right ? seg.Attributes.Right : seg.Attributes.Left;
        var along = side.ShiftStartCm != 0 || side.ShiftEndCm != 0 ? RoadStreetSection.Fractions(seg) : null;
        for (int i = 0; i < n; i++)
        {
            double off = half + (along is null ? 0 : side.ShiftAt(along[i]));
            int a = i == 0 ? 0 : i - 1, b = i == n - 1 ? n - 1 : i + 1;
            if (i == 0) b = 1; else if (i == n - 1) a = n - 2;
            double fx = seg.Points[b * 3] - seg.Points[a * 3], fz = seg.Points[b * 3 + 2] - seg.Points[a * 3 + 2];
            double len = Math.Sqrt(fx * fx + fz * fz);
            if (len < 1e-9) { fx = 0; fz = -1; len = 1; }
            fx /= len; fz /= len;
            // right of travel, tile-local (X east, Z south): (-fz, fx)
            double x = seg.Points[i * 3] + -fz * off * sign, z = seg.Points[i * 3 + 2] + fx * off * sign;
            result.Add((new Vec2(x, -z), seg.Points[i * 3 + 1]));
        }
        return result;
    }

    /// <summary>Unit direction out of the node into the segment, in plan (north up), from its first 2 m.</summary>
    private static Vec2 Outward(RoadSegment seg, bool atStart)
    {
        int n = seg.PointCount;
        var p0 = Plan(seg.Points, atStart ? 0 : n - 1);
        Vec2 p1 = p0;
        double gone = 0;
        for (int k = 1; k < n && gone < 2; k++)
        {
            p1 = Plan(seg.Points, atStart ? k : n - 1 - k);
            gone = p1.DistanceTo(p0);
        }
        var d = p1 - p0;
        return d / Math.Max(d.Length, 1e-9);
    }

    private static bool IsRoad(RoadSegment s) =>
        s.PointCount >= 2 && s.Class <= RoadClass.Square && s.Class != RoadClass.Railway;

    private static bool IsStreet(RoadSegment s) => StreetPlanner.IsCandidate(s);

    private static Vec2 Plan(float[] p, int i) => new(p[i * 3], -p[i * 3 + 2]);
    private static (float X, float Z) Point(RoadSegment s, int i) => (s.Points[i * 3], s.Points[i * 3 + 2]);
    private static (long, long) Key((float X, float Z) p) => ((long)Math.Round(p.X * 20), (long)Math.Round(p.Z * 20));

    private static double DistanceToRing(Vec2 p, List<(Vec2 P, float Y)> ring)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++) best = Math.Min(best, Distance(ring[i].P, ring[(i + 1) % ring.Count].P, p));
        return best;
    }

    private static double DistanceToLine(RoadSegment s, Vec2 q)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < s.PointCount; i++) best = Math.Min(best, Distance(Plan(s.Points, i), Plan(s.Points, i + 1), q));
        return best;
    }

    private static double Distance(Vec2 a, Vec2 b, Vec2 p)
    {
        var ab = b - a;
        double l2 = ab.LengthSquared;
        double t = l2 < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / l2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    private static bool Inside(List<Vec2> poly, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y)
                && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    private static double SignedArea(List<Vec2> poly)
    {
        double sum = 0;
        for (int i = 0; i < poly.Count; i++) sum += poly[i].Cross(poly[(i + 1) % poly.Count]);
        return sum * 0.5;
    }

    private static bool SelfIntersects(List<Vec2> poly)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                if (Cross(poly[i], poly[(i + 1) % n], poly[j], poly[(j + 1) % n])) return true;
            }
        return false;
    }

    private static bool Cross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        double d1 = (b - a).Cross(c - a), d2 = (b - a).Cross(d - a), d3 = (d - c).Cross(a - c), d4 = (d - c).Cross(b - c);
        return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
    }
}
