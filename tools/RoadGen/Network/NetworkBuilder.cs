namespace UnitSport.Tools.RoadGen.Network;

using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// Turns a bag of independent polylines into a connected graph: shared endpoints become
/// nodes, and a line that ends against the flank of another splits it.
///
/// <para>
/// This is the step the current pipeline has no equivalent of, and its absence is the whole
/// junction problem. Without a graph there is nothing that <i>knows</i> four ribbons meet at
/// a point, so all four are drawn to full length and simply overlap — which is why the
/// existing renderer needs a per-class depth bias to stop them z-fighting. That bias hides
/// the flicker; it does not stop four carriageways being painted on top of each other.
/// </para>
/// </summary>
public sealed class NetworkBuilder
{
    /// <summary>
    /// How close two endpoints must be to be the same place. swissTLM3D is a surveyed
    /// dataset and its junctions are usually exact, but clipping to tile boundaries and the
    /// 2 m height lattice both introduce small offsets.
    /// </summary>
    public double SnapTolerance { get; init; } = 0.75;

    /// <summary>Split a link when another link's endpoint lands on its flank (a T junction).</summary>
    public bool SplitAtTJunctions { get; init; } = true;

    /// <summary>Split two links that cross mid-span, where neither one ends on the other.</summary>
    public bool SplitAtCrossings { get; init; } = true;

    public sealed record Stats(int Nodes, int Junctions, int Links, int SplitsApplied,
        int CrossingsFound, int EndpointsMerged);

    public Stats Build(RoadNetwork net)
    {
        // Crossings first: they create new endpoints, which the T-junction pass then gets to
        // treat like any other endpoint landing on a flank.
        int crossings = SplitAtCrossings ? SplitCrossings(net) : 0;
        int splits = SplitAtTJunctions ? SplitLinks(net) : 0;
        int merged = BuildNodes(net);
        return new Stats(net.Nodes.Count, net.Nodes.Count(n => n.IsJunction), net.Links.Count,
            splits, crossings, merged);
    }

    // ---------------------------------------------------------------- crossings

    /// <summary>
    /// Nodes true X crossings: two links whose interiors intersect without either ending there.
    ///
    /// <para>
    /// A surveyed dataset like swissTLM3D is already noded, so this rarely fires on real data —
    /// but a synthesised network is full of them, and one undetected crossing is two full-width
    /// carriageways painted over each other for the length of the overlap. It is the same defect
    /// as an untrimmed junction, just harder to spot because there is no node to look at.
    /// </para>
    /// </summary>
    private int SplitCrossings(RoadNetwork net)
    {
        double cell = Math.Max(SnapTolerance * 8, 25);
        var buckets = new Dictionary<(long, long), List<(int Link, int Segment)>>();
        var arcs = net.Links.Select(l => Polyline.ArcLengths(l.Centreline)).ToArray();

        for (int li = 0; li < net.Links.Count; li++)
        {
            var pts = net.Links[li].Centreline;
            for (int si = 1; si < pts.Count; si++)
            {
                double minX = Math.Min(pts[si - 1].X, pts[si].X), maxX = Math.Max(pts[si - 1].X, pts[si].X);
                double minY = Math.Min(pts[si - 1].Y, pts[si].Y), maxY = Math.Max(pts[si - 1].Y, pts[si].Y);
                for (long cx = (long)Math.Floor(minX / cell); cx <= (long)Math.Floor(maxX / cell); cx++)
                for (long cy = (long)Math.Floor(minY / cell); cy <= (long)Math.Floor(maxY / cell); cy++)
                {
                    if (!buckets.TryGetValue((cx, cy), out var list)) buckets[(cx, cy)] = list = new();
                    list.Add((li, si));
                }
            }
        }

        var cuts = new Dictionary<int, List<double>>();
        var tested = new HashSet<(int Link, int Segment, int OtherLink, int OtherSegment)>();
        int found = 0;

        foreach (var list in buckets.Values)
        {
            for (int a = 0; a < list.Count; a++)
            for (int b = a + 1; b < list.Count; b++)
            {
                var (la, sa) = list[a];
                var (lb, sb) = list[b];
                if (la == lb) continue;
                if (net.Links[la].Layer != net.Links[lb].Layer) continue;

                // the same segment pair lands in every cell both overlap, so dedupe on an
                // orientation-independent key
                var key = la < lb ? (la, sa, lb, sb) : (lb, sb, la, sa);
                if (!tested.Add(key)) continue;

                var pa = net.Links[la].Centreline;
                var pb = net.Links[lb].Centreline;
                if (!SegmentsCross(pa[sa - 1], pa[sa], pb[sb - 1], pb[sb], out double ta, out double tb)) continue;

                double stationA = arcs[la][sa - 1] + ta * (arcs[la][sa] - arcs[la][sa - 1]);
                double stationB = arcs[lb][sb - 1] + tb * (arcs[lb][sb] - arcs[lb][sb - 1]);

                // an intersection at a link's own end is a T, not a crossing; the endpoint
                // pass handles that one and cutting here would make a zero-length stub
                bool cutA = AddCut(cuts, la, stationA, arcs[la][^1]);
                bool cutB = AddCut(cuts, lb, stationB, arcs[lb][^1]);
                if (cutA || cutB) found++;
            }
        }

        ApplyCuts(net, cuts);
        return found;
    }

    private bool AddCut(Dictionary<int, List<double>> cuts, int linkId, double station, double total)
    {
        if (station < SnapTolerance || station > total - SnapTolerance) return false;
        if (!cuts.TryGetValue(linkId, out var list)) cuts[linkId] = list = new List<double>();
        list.Add(station);
        return true;
    }

    /// <summary>Proper (non-touching) intersection of two segments, with both parameters.</summary>
    private static bool SegmentsCross(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1, out double ta, out double tb)
    {
        ta = tb = 0;
        var da = a1 - a0;
        var db = b1 - b0;
        double denom = da.Cross(db);
        if (Math.Abs(denom) < 1e-12) return false;      // parallel or degenerate

        ta = (b0 - a0).Cross(db) / denom;
        tb = (b0 - a0).Cross(da) / denom;
        return ta > 1e-9 && ta < 1 - 1e-9 && tb > 1e-9 && tb < 1 - 1e-9;
    }

    /// <summary>
    /// Cuts each link at its recorded stations. Always from the back, because a cut invalidates
    /// every station past it but leaves the ones before it exactly where they were.
    /// </summary>
    private int ApplyCuts(RoadNetwork net, Dictionary<int, List<double>> cuts)
    {
        int applied = 0;
        foreach (var (linkId, stations) in cuts)
        {
            var link = net.Links[linkId];
            stations.Sort();

            var distinct = new List<double>();
            foreach (double s in stations)
                if (distinct.Count == 0 || s - distinct[^1] > SnapTolerance) distinct.Add(s);

            for (int i = distinct.Count - 1; i >= 0; i--)
            {
                var arc = Polyline.ArcLengths(link.Centreline);
                double total = arc[^1];
                double station = distinct[i];
                if (station < SnapTolerance || station > total - SnapTolerance) continue;

                var head = Polyline.Trim(link.Centreline, 0, total - station);
                var tail = Polyline.Trim(link.Centreline, station, 0);
                if (head.Count < 2 || tail.Count < 2) continue;

                net.AddSplit(link, tail);
                link.Centreline = head;
                applied++;
            }
        }
        return applied;
    }

    // ---------------------------------------------------------------- splitting

    private int SplitLinks(RoadNetwork net)
    {
        var index = new SegmentIndex(net.Links, Math.Max(SnapTolerance * 4, 8));
        var cuts = new Dictionary<int, List<double>>();

        foreach (var link in net.Links)
        {
            foreach (var end in new[] { link.First, link.Last })
            {
                foreach (int otherId in index.Near(end, SnapTolerance))
                {
                    if (otherId == link.Id) continue;
                    var other = net.Links[otherId];

                    // A road crossing over another is not a junction with it. Merging by
                    // position alone is exactly how a motorway gets welded to the lane
                    // running underneath its bridge.
                    if (other.Layer != link.Layer) continue;

                    if (!ClosestOnPolyline(other.Centreline, end, out double distance, out double station))
                        continue;
                    if (distance > SnapTolerance) continue;

                    double total = Polyline.Length(other.Centreline);
                    if (station < SnapTolerance || station > total - SnapTolerance) continue;  // already an endpoint

                    AddCut(cuts, otherId, station, total);
                }
            }
        }

        return ApplyCuts(net, cuts);
    }

    private static bool ClosestOnPolyline(IReadOnlyList<Vec2> pts, Vec2 query,
        out double distance, out double station)
    {
        distance = double.MaxValue;
        station = 0;
        if (pts.Count < 2) return false;

        double travelled = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var ab = b - a;
            double lenSq = ab.LengthSquared;
            double t = lenSq < 1e-18 ? 0 : Math.Clamp((query - a).Dot(ab) / lenSq, 0, 1);
            var projected = a + ab * t;
            double d = query.DistanceTo(projected);
            if (d < distance)
            {
                distance = d;
                station = travelled + t * Math.Sqrt(lenSq);
            }
            travelled += Math.Sqrt(lenSq);
        }
        return true;
    }

    // ---------------------------------------------------------------- nodes

    private int BuildNodes(RoadNetwork net)
    {
        net.Nodes.Clear();

        var endpoints = new List<(int LinkId, LinkEnd End, Vec2 Position, int Layer)>();
        foreach (var link in net.Links)
        {
            link.StartNode = link.EndNode = -1;
            if (link.Centreline.Count < 2) continue;
            endpoints.Add((link.Id, LinkEnd.Start, link.First, link.Layer));
            endpoints.Add((link.Id, LinkEnd.End, link.Last, link.Layer));
        }

        // union-find over endpoints that share a place and a layer
        var parent = new int[endpoints.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        double cell = Math.Max(SnapTolerance * 2, 1.0);
        var buckets = new Dictionary<(long, long, int), List<int>>();
        for (int i = 0; i < endpoints.Count; i++)
        {
            var key = ((long)Math.Floor(endpoints[i].Position.X / cell),
                       (long)Math.Floor(endpoints[i].Position.Y / cell),
                       endpoints[i].Layer);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
            list.Add(i);
        }

        int merges = 0;
        for (int i = 0; i < endpoints.Count; i++)
        {
            var (_, _, position, layer) = endpoints[i];
            long cx = (long)Math.Floor(position.X / cell);
            long cy = (long)Math.Floor(position.Y / cell);

            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            {
                if (!buckets.TryGetValue((cx + dx, cy + dy, layer), out var list)) continue;
                foreach (int j in list)
                {
                    if (j <= i) continue;
                    if (endpoints[i].Position.DistanceTo(endpoints[j].Position) > SnapTolerance) continue;
                    if (Find(i) != Find(j)) { Union(i, j); merges++; }
                }
            }
        }

        var nodeOf = new Dictionary<int, RoadNode>();
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < endpoints.Count; i++)
        {
            int root = Find(i);
            if (!members.TryGetValue(root, out var list)) members[root] = list = new List<int>();
            list.Add(i);
        }

        foreach (var (root, list) in members)
        {
            // the node sits at the centroid of what met there, and every link end is then
            // moved onto it — a junction polygon built from ends that are 20 cm apart has a
            // 20 cm crack down the middle of it
            var centroid = Vec2.Zero;
            foreach (int i in list) centroid += endpoints[i].Position;
            centroid /= list.Count;

            var node = new RoadNode { Id = net.Nodes.Count, Position = centroid, Layer = endpoints[list[0]].Layer };
            net.Nodes.Add(node);
            nodeOf[root] = node;

            foreach (int i in list)
            {
                var (linkId, end, _, _) = endpoints[i];
                var link = net.Links[linkId];
                if (end == LinkEnd.Start) { link.StartNode = node.Id; link.Centreline[0] = centroid; }
                else { link.EndNode = node.Id; link.Centreline[^1] = centroid; }
                node.Approaches.Add(new Approach(linkId, end, 0));
            }
        }

        return merges;
    }

    /// <summary>
    /// Fills in each approach's outward heading from the smoothed alignment. Has to run after
    /// smoothing, because the heading a ribbon actually leaves on is the alignment's, not the
    /// raw polyline's — they differ by exactly the amount the corner was rounded.
    /// </summary>
    public static void RefreshHeadings(RoadNetwork net)
    {
        foreach (var node in net.Nodes)
        {
            for (int i = 0; i < node.Approaches.Count; i++)
            {
                var approach = node.Approaches[i];
                var link = net.Links[approach.LinkId];
                double heading;

                if (link.Alignment is { IsEmpty: false } alignment)
                {
                    heading = approach.End == LinkEnd.Start
                        ? alignment.HeadingAt(0)
                        : alignment.HeadingAt(alignment.Length) + Math.PI;
                }
                else
                {
                    var pts = link.Centreline;
                    heading = approach.End == LinkEnd.Start
                        ? (pts[1] - pts[0]).Heading
                        : (pts[^2] - pts[^1]).Heading;
                }

                node.Approaches[i] = approach with { OutwardHeading = Angles.Normalize(heading) };
            }
        }
    }
}

/// <summary>Uniform grid over link segments, so endpoint-to-flank tests are not O(n²).</summary>
internal sealed class SegmentIndex
{
    private readonly Dictionary<(long, long), List<int>> _cells = new();
    private readonly double _cell;

    public SegmentIndex(IEnumerable<RoadLink> links, double cellSize)
    {
        _cell = cellSize;
        foreach (var link in links)
        {
            var pts = link.Centreline;
            for (int i = 1; i < pts.Count; i++)
            {
                double minX = Math.Min(pts[i - 1].X, pts[i].X), maxX = Math.Max(pts[i - 1].X, pts[i].X);
                double minY = Math.Min(pts[i - 1].Y, pts[i].Y), maxY = Math.Max(pts[i - 1].Y, pts[i].Y);
                for (long cx = (long)Math.Floor(minX / _cell); cx <= (long)Math.Floor(maxX / _cell); cx++)
                for (long cy = (long)Math.Floor(minY / _cell); cy <= (long)Math.Floor(maxY / _cell); cy++)
                {
                    if (!_cells.TryGetValue((cx, cy), out var list)) _cells[(cx, cy)] = list = new List<int>();
                    if (list.Count == 0 || list[^1] != link.Id) list.Add(link.Id);
                }
            }
        }
    }

    public IEnumerable<int> Near(Vec2 point, double radius)
    {
        long lo = (long)Math.Floor((point.X - radius) / _cell), hi = (long)Math.Floor((point.X + radius) / _cell);
        long lo2 = (long)Math.Floor((point.Y - radius) / _cell), hi2 = (long)Math.Floor((point.Y + radius) / _cell);
        var seen = new HashSet<int>();
        for (long cx = lo; cx <= hi; cx++)
        for (long cy = lo2; cy <= hi2; cy++)
            if (_cells.TryGetValue((cx, cy), out var list))
                foreach (int id in list) if (seen.Add(id)) yield return id;
    }
}
