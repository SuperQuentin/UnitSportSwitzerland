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

    /// <summary>A link end that is a junction's mouth or a dead end (not a road simply carrying on).</summary>
    private static bool EndsAtJunction(RoadNetwork net, int node) =>
        node < 0 || node >= net.Nodes.Count || net.Nodes[node].Degree != 2;

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
                if (side.ShiftStartCm != side.ShiftEndCm) continue;
                float sign = right ? 1f : -1f, half = p.Width * 0.5f + side.ShiftStartCm / 100f;
                if (side.Bike == BikeKind.Track && side.BufferDm == 0 && side.SidewalkDm > 0)
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
    /// a sidewalk corner.
    /// </summary>
    private static void EmitBikeCrossings(PriorityResult priority, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile)> segmentOf, Dictionary<RoadSegment, List<RoadSegment>> finalPieces,
        HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, List<RoadPaint>> paint,
        Dictionary<TileId, List<RoadPointProp>> signs, Dictionary<TileId, List<(RoadAreaProp Band, List<Vec2> Ring)>> bridges,
        BikePlanner.Stats stats)
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
                var mainDir = (Vec2.FromHeading(b.OutwardHeading) - ua).Normalized();

                // the bike side of an arm's end piece on its left or right looking outward
                // the side's shift off a turn lane's widening where the arm ends here (#123)
                var endShift = new Dictionary<(int, bool), double>();
                RoadSide SideOf(int armIndex, bool armLeft)
                {
                    var arm = junction.Arms[armIndex];
                    var end = plan.Arms[armIndex].End;
                    bool segRight = (end == LinkEnd.End) == armLeft;   // looking outward, an arm ending here has the drawing's right on its left
                    if (segmentOf.TryGetValue(arm.LinkId, out var so))
                    {
                        var pieces = finalPieces.TryGetValue(so.Segment, out var list) && list.Count > 0 ? list : [so.Segment];
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
                    // a curve through the junction, offset from the carriageway edge (+ outward, - inward) at each arm
                    List<Vec2> Curve(double oa, double ob, bool simplify = true)
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
                        float red = (float)(Math.Min(sa.BikeDm, sb.BikeDm) / 10.0 - 2 * lw - 2 * RedInset);
                        if (red > 0.3f) Add(Curve(ta, tb), PaintType.BikeCrossing, PaintEmitter.Red, red, 0);
                        Add(Curve(ta - ha, tb - hb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                        Add(Curve(ta + ha, tb + hb), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
                        stats.Crossings++;
                        stats.CrossingsTrack++;
                        // the joining road gives way before the path, not on it
                        double beyond = Math.Max(sa.VergeDm + sa.BikeDm, sb.VergeDm + sb.BikeDm) / 10.0 + 0.1;
                        foreach (int i in joined) MoveYieldBack(priority, net, junction.Arms[i], mainDir, beyond, paint, signs, stats);
                    }
                    else if (sa.HasTrack && sb.HasTrack)
                    {
                        var ub = Vec2.FromHeading(b.OutwardHeading);
                        if (BridgePath(home, sa, sb, ua, ub, (oa, ob) => Curve(oa + xa, ob + xb, simplify: false), p => HeightAt(anchors, p)) is { } bands)
                        {
                            Get(bridges, home).AddRange(bands);
                            stats.PathsThrough++;
                        }
                    }
                }
            }
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
        Func<double, double, List<Vec2>> curve, Func<Vec2, float> height)
    {
        if (RoadStreetSection.For(sa) is not { } pa || RoadStreetSection.For(sb) is not { } pb) return null;
        if (!pa.Surface.SequenceEqual(pb.Surface) || !pa.H.SequenceEqual(pb.H)) return null;
        // the samples the outer edge needs to stay within 2 cm, shared by every band (a straight mouth keeps its ends)
        var keep = KeepIndices(curve(pa.Width, pb.Width), 0.02);
        List<Vec2> At(double oa, double ob) { var c = curve(oa, ob); return keep.Select(i => c[i]).ToList(); }
        var result = new List<(RoadAreaProp, List<Vec2>)>();
        for (int k = 0; k + 1 < pa.Count; k++)
        {
            var surface = pa.Surface[k];
            bool kerb = surface == StreetSurface.Kerb;
            // a vertical kerb has no width: the faces down the bands' open edges are its face
            if (kerb && pa.D[k + 1] - pa.D[k] < 1e-4f) continue;
            int from = k;
            var inner = At(pa.D[from], pb.D[from]);
            var outer = At(pa.D[k + 1], pb.D[k + 1]);
            inner.Insert(0, inner[0] + ua * BridgeOverlap);
            outer.Insert(0, outer[0] + ua * BridgeOverlap);
            inner.Add(inner[^1] + ub * BridgeOverlap);
            outer.Add(outer[^1] + ub * BridgeOverlap);
            int n = inner.Count;
            var plan = inner.Concat(outer).ToList();
            var vertices = Local(home, plan, height, 0f);
            if (kerb)   // a sloped kerb strip carries its slope in its vertices: inner edge at its foot, outer at its top
                for (int i = 0; i < 2 * n; i++) vertices[i * 3 + 1] += i < n ? pa.H[k] : pa.H[k + 1];
            var indices = new List<ushort>();
            for (int i = 0; i + 1 < n; i++)
            {
                ushort a = (ushort)i, b = (ushort)(i + 1), c = (ushort)(n + i + 1), d = (ushort)(n + i);
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
