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
        public int Corners, Built, OneSided, Squared, Banded, Facade, Road, Shape;
        public double Area;

        public string Format() => string.Create(CultureInfo.InvariantCulture, $"""
                corners: {Corners:N0} with a sidewalk on either side, {Built:N0} built ({Squared:N0} squared, {Banded:N0} along the kerb, {Area:N0} m2), {OneSided:N0} one-sided; rejected: a wall {Facade:N0}, a carriageway {Road:N0}, shape {Shape:N0}
            """);
    }

    private readonly record struct End(int Seg, bool AtStart);

    /// <summary><c>--debug-street E,N</c>: corners at nodes within 15 m of this LV95 point are traced.</summary>
    public static (double E, double N)? Debug;

    private static bool Traced(TileId id, Vec2 p) =>
        Debug is { } d && Math.Abs(id.MinE + p.X - d.E) < 15 && Math.Abs(id.MaxN + p.Y - d.N) < 15;

    /// <summary>One arm's sidewalk on the side facing a corner: kerb line from the node out to where the sidewalk starts.</summary>
    private sealed record Chain(List<(Vec2 P, float Y)> Kerb, Vec2 Out, double Width, float Kerb_, Vec2 Inward);

    public static List<RoadAreaProp> Plan(TileId id, IReadOnlyList<RoadSegment> segments, IReadOnlyList<RoadJunction> caps,
        Facades facades, Stats stats, IReadOnlyList<RoadAreaProp>? pavement = null)
    {
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
            Corners(id, segments, ends, arms, ring, cap, facades, stats, props);
        }

        // bare bends: two street ends sharing a point, no cap
        foreach (var list in ends.Values)
        {
            if (list.Count != 2 || claimed.Contains(list[0]) || claimed.Contains(list[1])) continue;
            var a = Outward(segments[list[0].Seg], list[0].AtStart);
            var b = Outward(segments[list[1].Seg], list[1].AtStart);
            double turn = 180 - Math.Acos(Math.Clamp(a.Dot(b), -1, 1)) * 180 / Math.PI;
            if (turn < MinBendDeg) continue;
            Corners(id, segments, ends, list, null, null, facades, stats, props);
        }
        return props;
    }

    private static void Corners(TileId id, IReadOnlyList<RoadSegment> segments, Dictionary<(long, long), List<End>> ends,
        List<End> arms, List<(Vec2 P, float Y)>? ring, RoadJunction? cap, Facades facades, Stats stats, List<RoadAreaProp> props)
    {
        if (arms.Count < 2) return;
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
            if (Build(id, segments, ca, cb, ring, cap, facades, stats) is { } prop) props.Add(prop);
            if (ordered.Count == 2 && ring == null)
            {
                // the bend's other corner: B's left and A's right
                var cb2 = Walk(segments, ends, b.End, left: true);
                var ca2 = Walk(segments, ends, a.End, left: false);
                if (ca2 != null && cb2 != null && (ca2.Kerb_ > 0) == (cb2.Kerb_ > 0)
                    && Build(id, segments, cb2, ca2, null, null, facades, stats) is { } p2) props.Add(p2);
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
                return new Chain(kerb, edge[0].P + across * w, w, side.KerbCm / 100f, dir * -1);
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
        List<(Vec2 P, float Y)>? ring, RoadJunction? cap, Facades facades, Stats stats)
    {
        // kerb: A's sidewalk start in to the node, round the cap, out to B's sidewalk start
        var poly = new List<(Vec2 P, float Y)>();
        for (int k = a.Kerb.Count - 1; k >= 0; k--) poly.Add(a.Kerb[k]);
        if (ring != null) poly.AddRange(RingBetween(ring, a.Kerb[0].P, b.Kerb[0].P));
        poly.AddRange(b.Kerb);
        poly = Thin(Dedupe(poly));
        int kerbCount = poly.Count;
        if (kerbCount < 2) { stats.Shape++; return null; }

        float ya = a.Kerb[^1].Y, yb = b.Kerb[^1].Y;
        // the outer edges, carried on toward the node, meet at the block's corner
        var corner = Meet(b.Out, b.Inward, a.Out, a.Inward);
        bool squared = corner is { } c && c.DistanceTo(a.Out) < 15 && c.DistanceTo(b.Out) < 15;
        var withCorner = new List<(Vec2 P, float Y)>(poly) { (b.Out, yb) };
        if (squared) withCorner.Add((corner!.Value, (ya + yb) * 0.5f));
        withCorner.Add((a.Out, ya));
        var chord = new List<(Vec2 P, float Y)>(poly) { (b.Out, yb), (a.Out, ya) };
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
        if (Strip(id, segments, poly, bandOuter, facades, a, stats, out var bandWhy) is { } strip) return strip;
        if (Traced(id, a.Kerb[0].P)) Console.WriteLine($"[corner]   band: {bandWhy}");
        if (Polygon(id, segments, chord, kerbCount, cap, facades, a, out why) is { } cut) return cut;
        if (why != null && why.StartsWith("wall")) stats.Facade++; else if (why == "road") stats.Road++; else stats.Shape++;
        if (Traced(id, a.Kerb[0].P)) Console.WriteLine($"[corner]   rejected: {why}");
        return null;

        RoadAreaProp? Polygon(TileId id, IReadOnlyList<RoadSegment> segments, List<(Vec2 P, float Y)> candidate, int kerbCount,
            RoadJunction? cap, Facades facades, Chain a, out string? why)
        {
            why = Check(id, segments, candidate, kerbCount, cap, facades);
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
        int kerbCount, RoadJunction? cap, Facades facades)
    {
        var plan = poly.Select(p => p.P).ToList();
        double area = Math.Abs(SignedArea(plan));
        if (area < 0.2 || area > MaxArea || SelfIntersects(plan)) return "shape";
        double minX = plan.Min(p => p.X), maxX = plan.Max(p => p.X), minY = plan.Min(p => p.Y), maxY = plan.Max(p => p.Y);
        var near = new List<RoadSegment>();
        foreach (var s in segments)
        {
            if (!IsRoad(s) || (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) continue;
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
