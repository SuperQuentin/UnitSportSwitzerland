namespace UnitSport.Tools.RoadGen.Junctions;

using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;

public sealed record JunctionOptions(
    /// <summary>Extra trim past the geometric corner, so there is room for a kerb curve.</summary>
    double KerbFactor = 0.6,
    double MinKerb = 0.5,
    double MaxKerb = 5.0,
    /// <summary>Points used to draw each rounded inner corner.</summary>
    int FilletSamples = 6,
    /// <summary>
    /// Hard ceiling on how far back a road may be trimmed, as a multiple of its own width.
    /// Two roads diverging at a shallow angle have their edge intersection a very long way
    /// out — a motorway and its exit ramp at 6° put it over a hundred metres away — and
    /// trimming to it would delete the entire ramp. Past this the arms are allowed to stay
    /// merged, which is what a gore area physically is.
    /// </summary>
    double MaxTrimWidths = 5.0,
    double MaxTrimAbsolute = 30.0);

/// <summary>
/// Builds junction polygons and decides how far each road is cut back.
///
/// <para>
/// For every pair of angularly adjacent arms, the left edge of one and the right edge of the
/// next are two straight lines; where they cross is where the two carriageways would start to
/// overlap. Trim both arms to at least that point and the overlap is gone — not hidden, gone.
/// The junction polygon is then the ring joining the trimmed ends, with the inner corners
/// rounded, which is why a crossroads comes out plus-shaped rather than square.
/// </para>
/// </summary>
public sealed class JunctionBuilder
{
    private readonly JunctionOptions _options;

    public JunctionBuilder(JunctionOptions? options = null) => _options = options ?? new JunctionOptions();

    private readonly record struct PendingArm(
        int LinkId, LinkEnd End, double NodeOutwardHeading, double HalfWidth, double Trim, bool Clamped);

    private sealed record PendingJunction(RoadNode Node, List<PendingArm> Arms, Vec2[] Corners, bool[] CornerValid);

    /// <summary>
    /// Three passes, and the order is the whole point. Trims are decided first for every node,
    /// then reconciled against link lengths, and only then are the polygons drawn — because a
    /// short link between two junctions can have both its trims scaled down, and a polygon
    /// drawn before that happens would no longer touch the ribbon it is supposed to join.
    /// </summary>
    public List<Junction> Build(RoadNetwork net)
    {
        var pending = new List<PendingJunction>();
        foreach (var node in net.Nodes)
        {
            if (!node.IsJunction) continue;
            var p = Plan(net, node);
            if (p is not null) pending.Add(p);
        }

        ApplyTrims(net, pending);

        var junctions = new List<Junction>();
        foreach (var p in pending)
        {
            var junction = Draw(net, p);
            if (junction is not null) junctions.Add(junction);
        }
        return junctions;
    }

    // ------------------------------------------------------------------ pass 1: trims

    private PendingJunction? Plan(RoadNetwork net, RoadNode node)
    {
        // counter-clockwise by outward heading: the whole construction is a walk around the node
        var ordered = node.Approaches
            .Select(a => (Approach: a, Half: net.Links[a.LinkId].Profile.HalfWidth))
            .OrderBy(a => Angles.Normalize(a.Approach.OutwardHeading))
            .ToList();

        if (ordered.Count < 3) return null;

        int n = ordered.Count;
        var trims = new double[n];
        var clamped = new bool[n];
        var corners = new Vec2[n];
        var cornerValid = new bool[n];

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var di = Vec2.FromHeading(ordered[i].Approach.OutwardHeading);
            var dj = Vec2.FromHeading(ordered[j].Approach.OutwardHeading);

            // arm i's LEFT edge against arm j's RIGHT edge — the two that face each other
            // across the wedge between them
            var pi = node.Position + di.Perp * ordered[i].Half;
            var pj = node.Position - dj.Perp * ordered[j].Half;

            if (!TryIntersect(pi, di, pj, dj, out var corner)) continue;

            corners[i] = corner;
            cornerValid[i] = true;

            double ti = (corner - node.Position).Dot(di);
            double tj = (corner - node.Position).Dot(dj);
            if (ti > trims[i]) trims[i] = ti;
            if (tj > trims[j]) trims[j] = tj;
        }

        var arms = new List<PendingArm>(n);
        for (int i = 0; i < n; i++)
        {
            double kerb = Math.Clamp(_options.KerbFactor * ordered[i].Half, _options.MinKerb, _options.MaxKerb);
            double limit = Math.Min(_options.MaxTrimWidths * ordered[i].Half * 2, _options.MaxTrimAbsolute);

            double wanted = Math.Max(trims[i], 0) + kerb;
            if (wanted > limit) { wanted = limit; clamped[i] = true; }

            arms.Add(new PendingArm(
                ordered[i].Approach.LinkId, ordered[i].Approach.End,
                ordered[i].Approach.OutwardHeading, ordered[i].Half, wanted, clamped[i]));
        }

        return new PendingJunction(node, arms, corners, cornerValid);
    }

    // ------------------------------------------------------------------ pass 2: reconcile

    /// <summary>
    /// Pushes each arm's trim onto its link, then rescues links too short to carry both ends'
    /// trims by scaling them down together — a 12 m stub between two crossroads would otherwise
    /// be trimmed out of existence and leave a hole.
    /// </summary>
    private static void ApplyTrims(RoadNetwork net, List<PendingJunction> pending)
    {
        foreach (var junction in pending)
        foreach (var arm in junction.Arms)
        {
            var link = net.Links[arm.LinkId];
            if (arm.End == LinkEnd.Start) link.TrimStart = Math.Max(link.TrimStart, arm.Trim);
            else link.TrimEnd = Math.Max(link.TrimEnd, arm.Trim);
        }

        foreach (var link in net.Links)
        {
            if (link.Alignment is not { IsEmpty: false } alignment) continue;

            double total = link.TrimStart + link.TrimEnd;
            double keep = Math.Max(alignment.Length * 0.15, 0.5);   // always leave a visible road
            if (total > alignment.Length - keep && total > 1e-6)
            {
                double scale = Math.Max(0, alignment.Length - keep) / total;
                link.TrimStart *= scale;
                link.TrimEnd *= scale;
            }
        }
    }

    // ------------------------------------------------------------------ pass 3: polygons

    private Junction? Draw(RoadNetwork net, PendingJunction pending)
    {
        var junction = new Junction
        {
            NodeId = pending.Node.Id,
            Centre = pending.Node.Position,
            Layer = pending.Node.Layer,
        };

        foreach (var arm in pending.Arms)
        {
            var link = net.Links[arm.LinkId];

            // Take the arm's end corners from the ALIGNMENT at the final trim station, not
            // from a straight ray out of the node. On a curved approach the two disagree —
            // arc length runs ahead of straight-line distance — and the ribbon is trimmed by
            // arc length, so a ray-based corner leaves a crack between road and junction
            // exactly where the road bends into it.
            Vec2 position;
            double outward;

            if (link.Alignment is { IsEmpty: false } alignment)
            {
                double station = arm.End == LinkEnd.Start
                    ? Math.Min(link.TrimStart, alignment.Length)
                    : Math.Max(alignment.Length - link.TrimEnd, 0);

                position = alignment.PointAt(station);
                double forward = alignment.HeadingAt(station);
                outward = arm.End == LinkEnd.Start ? forward : forward + Math.PI;
            }
            else
            {
                position = pending.Node.Position + Vec2.FromHeading(arm.NodeOutwardHeading) * arm.Trim;
                outward = arm.NodeOutwardHeading;
            }

            var normal = Vec2.FromHeading(outward).Perp;
            junction.Arms.Add(new JunctionArm(
                arm.LinkId, outward, arm.HalfWidth, arm.Trim,
                position + normal * arm.HalfWidth,
                position - normal * arm.HalfWidth,
                arm.Clamped));
        }

        // walk the ring: across each arm end right→left, then round the corner into the next
        int n = junction.Arms.Count;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            junction.Boundary.Add(junction.Arms[i].Right);
            junction.Boundary.Add(junction.Arms[i].Left);

            // A fillet only when its control point is somewhere sane. Two nearly collinear
            // arms — a straight through-road with a side turning — put their edge intersection
            // enormously far out, and a Bézier reaching for it balloons the junction across
            // whatever else is nearby. Measured 3,594 m² of junction-over-carriageway on four
            // real tiles before this guard; past the limit the corner is simply a straight
            // chord, which is what a wide splayed junction mouth looks like anyway.
            double reach = Math.Max(junction.Arms[i].Trim, junction.Arms[j].Trim) * 1.5 + 2.0;
            if (pending.CornerValid[i]
                && (pending.Corners[i] - pending.Node.Position).Length <= reach)
                AppendFillet(junction.Boundary, junction.Arms[i].Left, pending.Corners[i],
                    junction.Arms[j].Right, _options.FilletSamples);
        }

        DedupeRing(junction.Boundary);
        if (junction.Boundary.Count < 3) return null;

        Triangulate(junction);
        return junction;
    }

    /// <summary>
    /// Rounds the inner corner between two arms with a quadratic Bézier whose control point is
    /// the edge intersection. When the trim is exactly the corner distance the two ends and the
    /// control coincide and this collapses to nothing, which is the correct square junction;
    /// as the kerb allowance grows it opens into the curve a real kerb has.
    /// </summary>
    private static void AppendFillet(List<Vec2> ring, Vec2 from, Vec2 control, Vec2 to, int samples)
    {
        if (from.DistanceSquaredTo(to) < 1e-6) return;

        for (int s = 1; s < samples; s++)
        {
            double t = (double)s / samples;
            double mt = 1 - t;
            ring.Add(from * (mt * mt) + control * (2 * mt * t) + to * (t * t));
        }
    }

    private static void DedupeRing(List<Vec2> ring)
    {
        for (int i = ring.Count - 1; i >= 0; i--)
        {
            var next = ring[(i + 1) % ring.Count];
            if (ring[i].DistanceSquaredTo(next) < 1e-8) ring.RemoveAt(i);
        }
    }

    /// <summary>
    /// Fan from the centre. A junction ring is star-shaped about its node by construction —
    /// every boundary point sits on an arm end or on a fillet between two of them — so the fan
    /// is both correct and gives the tidy radial triangulation a junction wants. Ear clipping
    /// catches the pathological rings that a badly tangled node can still produce.
    /// </summary>
    private static void Triangulate(Junction junction)
    {
        junction.Vertices.Clear();
        junction.Triangles.Clear();

        junction.Vertices.Add(junction.Centre);
        junction.Vertices.AddRange(junction.Boundary);

        bool fanIsValid = true;
        int count = junction.Boundary.Count;
        for (int i = 0; i < count; i++)
        {
            var a = junction.Boundary[i];
            var b = junction.Boundary[(i + 1) % count];
            if ((a - junction.Centre).Cross(b - junction.Centre) <= 1e-9) { fanIsValid = false; break; }
        }

        if (fanIsValid)
        {
            for (int i = 0; i < count; i++)
            {
                junction.Triangles.Add(0);
                junction.Triangles.Add(1 + i);
                junction.Triangles.Add(1 + (i + 1) % count);
            }
            return;
        }

        var ears = EarClip.Triangulate(junction.Boundary);
        junction.Vertices.Clear();
        junction.Vertices.AddRange(junction.Boundary);
        junction.Triangles.AddRange(ears);
    }

    private static bool TryIntersect(Vec2 p1, Vec2 d1, Vec2 p2, Vec2 d2, out Vec2 hit)
    {
        double denom = d1.Cross(d2);
        if (Math.Abs(denom) < 1e-9) { hit = Vec2.Zero; return false; }   // parallel: a straight through
        double a = (p2 - p1).Cross(d2) / denom;
        hit = p1 + d1 * a;
        return true;
    }
}
