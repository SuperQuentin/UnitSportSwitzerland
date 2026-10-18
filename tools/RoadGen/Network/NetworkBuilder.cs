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

    /// <summary>
    /// A dead end that stops short of another road's flank, inside that road's half width, is
    /// carried on onto it and becomes a T junction (#121): TLM sometimes ends a side road at the
    /// main road's edge instead of its centreline. Null = off. The predicate says which pairs may
    /// join (the network stage keeps divided carriageways and motorways out: their partner lies
    /// inside their half width by construction).
    /// </summary>
    public Func<RoadLink, RoadLink, bool>? JoinNearEnds { get; init; }

    /// <summary>Dead ends carried onto a nearby road by <see cref="JoinNearEnds"/> in the last build.</summary>
    public int NearEndsJoined { get; private set; }

    /// <summary>
    /// Footpaths and car roads (#711, the user's rule: a footpath joining a junction broke it; it simply connects to the
    /// sidewalk and is ignored): a footpath never splits a car road, never joins its flank and never shares its node, and an
    /// end left on a carriageway is trimmed back to its edge (under the sidewalk). Null = every link nodes with every other.
    /// </summary>
    public Func<RoadLink, bool>? IsFootpath { get; init; }

    /// <summary>The car roads <see cref="IsFootpath"/> keeps footpaths apart from.</summary>
    public Func<RoadLink, bool>? IsCarRoad { get; init; }

    /// <summary>Footpath ends trimmed back off a carriageway in the last build.</summary>
    public int FootpathEndsTrimmed { get; private set; }

    /// <summary>
    /// Footpath ends that meet another footpath's end (a path crossing a street through one of its points, where OSM maps
    /// the crossing): these keep their node with the street, so the crossing stays (#711).
    /// </summary>
    private HashSet<(int LinkId, bool AtStart)> _crossingEnds = [];

    /// <summary>Whether two links never node: a footpath and a car road (<see cref="IsFootpath"/>).</summary>
    private bool Apart(RoadLink a, RoadLink b) => IsFootpath is not null && IsCarRoad is not null
        && (IsFootpath(a) && IsCarRoad(b) || IsFootpath(b) && IsCarRoad(a));

    /// <summary>
    /// Footpath ends near a junction of car roads (#711, the user's review: a footpath joining the junction broke it): only these
    /// are kept apart; a footpath meeting a street elsewhere keeps its node (OSM's crossing there stays).
    /// </summary>
    private HashSet<(int LinkId, bool AtStart)> _nearJunction = [];

    /// <summary>A footpath end within this (m) of a junction of car roads is kept apart from them (#854: as far as its mouths reach).</summary>
    private const double FootpathJunctionReach = 30;

    /// <summary><see cref="Apart"/> for one footpath end: near a car junction, and not a crossing's (<see cref="_crossingEnds"/>).</summary>
    private bool Apart(RoadLink a, bool aAtStart, RoadLink b, bool bAtStart) => Apart(a, b)
        && (_nearJunction.Contains((a.Id, aAtStart)) || _nearJunction.Contains((b.Id, bAtStart)))
        && !_crossingEnds.Contains((a.Id, aAtStart)) && !_crossingEnds.Contains((b.Id, bAtStart));

    /// <summary>
    /// Finds the footpath ends near a junction of car roads: where three car ends meet, or a car end lands on another car
    /// road's flank (the network is not noded yet).
    /// </summary>
    private void FindNearJunction(RoadNetwork net)
    {
        _nearJunction = [];
        if (IsFootpath is null || IsCarRoad is null) return;
        var cars = net.Links.Where(l => l.Centreline.Count >= 2 && IsCarRoad(l)).ToList();
        var index = new SegmentIndex(cars, 16);
        var carEnds = cars.SelectMany(l => new[] { (l.Id, P: l.First, l.Layer), (l.Id, P: l.Last, l.Layer) }).ToList();
        var grid = new Dictionary<(long, long), List<int>>();
        for (int k = 0; k < carEnds.Count; k++)
        {
            var key = ((long)Math.Floor(carEnds[k].P.X / 2), (long)Math.Floor(carEnds[k].P.Y / 2));
            if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = [];
            cell.Add(k);
        }
        var junctions = new List<(Vec2 P, int Layer)>();
        foreach (var (id, p, layer) in carEnds)
        {
            int meeting = 0;
            long cx = (long)Math.Floor(p.X / 2), cy = (long)Math.Floor(p.Y / 2);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((cx + dx, cy + dy), out var cell))
                        foreach (int k in cell) if (carEnds[k].Layer == layer && carEnds[k].P.DistanceTo(p) <= SnapTolerance) meeting++;
            bool onFlank = index.Near(p, SnapTolerance).Any(o => o != id && net.Links[o].Layer == layer
                && ClosestOnPolyline(net.Links[o].Centreline, p, out double d, out double st) && d <= SnapTolerance
                && st > SnapTolerance && st < Polyline.Length(net.Links[o].Centreline) - SnapTolerance);
            if (meeting >= 3 || onFlank) junctions.Add((p, layer));
        }
        foreach (var l in net.Links)
        {
            if (l.Centreline.Count < 2 || !IsFootpath(l)) continue;
            foreach (bool atStart in new[] { true, false })
            {
                var p = atStart ? l.First : l.Last;
                if (junctions.Any(j => j.Layer == l.Layer && j.P.DistanceTo(p) <= FootpathJunctionReach)) _nearJunction.Add((l.Id, atStart));
            }
        }
    }

    /// <summary>Finds the footpath ends that meet another footpath end within the snap tolerance.</summary>
    private void FindCrossingEnds(RoadNetwork net)
    {
        _crossingEnds = [];
        if (IsFootpath is null) return;
        var ends = new List<(int Id, bool AtStart, Vec2 P, int Layer)>();
        foreach (var l in net.Links)
            if (l.Centreline.Count >= 2 && IsFootpath(l)) { ends.Add((l.Id, true, l.First, l.Layer)); ends.Add((l.Id, false, l.Last, l.Layer)); }
        var cells = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < ends.Count; i++)
        {
            var key = ((long)Math.Floor(ends[i].P.X / 2), (long)Math.Floor(ends[i].P.Y / 2));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(i);
        }
        for (int i = 0; i < ends.Count; i++)
        {
            long cx = (long)Math.Floor(ends[i].P.X / 2), cy = (long)Math.Floor(ends[i].P.Y / 2);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (cells.TryGetValue((cx + dx, cy + dy), out var list))
                        foreach (int j in list)
                            if (ends[j].Id != ends[i].Id && ends[j].Layer == ends[i].Layer && ends[j].P.DistanceTo(ends[i].P) <= SnapTolerance)
                                _crossingEnds.Add((ends[i].Id, ends[i].AtStart));
        }
    }

    public sealed record Stats(int Nodes, int Junctions, int Links, int SplitsApplied,
        int CrossingsFound, int EndpointsMerged);

    public Stats Build(RoadNetwork net)
    {
        // Crossings first: they create new endpoints, which the T-junction pass then gets to
        // treat like any other endpoint landing on a flank.
        int crossings = SplitAtCrossings ? SplitCrossings(net) : 0;
        FindCrossingEnds(net);
        FindNearJunction(net);
        int splits = SplitAtTJunctions ? SplitLinks(net) : 0;
        int merged = BuildNodes(net);
        TrimFootpathEnds(net);
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
            foreach (bool atStart in new[] { true, false })
            {
                var end = atStart ? link.First : link.Last;
                if (JoinNearEnds is not null && TryJoinNearEnd(net, index, link, atStart, cuts)) continue;
                foreach (int otherId in index.Near(end, SnapTolerance))
                {
                    if (otherId == link.Id) continue;
                    var other = net.Links[otherId];

                    // A road crossing over another is not a junction with it. Merging by
                    // position alone is exactly how a motorway gets welded to the lane
                    // running underneath its bridge.
                    if (other.Layer != link.Layer) continue;
                    if (Apart(link, atStart, other, true) && Apart(link, atStart, other, false)) continue;   // a footpath ends beside the street, it does not join it (#711)

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

    /// <summary>
    /// <see cref="JoinNearEnds"/> for one end: only a real dead end (no other end, no flank within
    /// the snap tolerance), only onto a flank between the tolerance and the other road's half width,
    /// and only when the end points at it. The end is extended onto the flank and the flank cut there.
    /// </summary>
    private bool TryJoinNearEnd(RoadNetwork net, SegmentIndex index, RoadLink link, bool atStart,
        Dictionary<int, List<double>> cuts)
    {
        var pts = link.Centreline;
        if (pts.Count < 2) return false;
        var end = atStart ? pts[0] : pts[^1];
        var outward = (end - (atStart ? pts[1] : pts[^2])).Normalized();
        const double MaxReach = 6.0;

        int best = -1;
        double bestDistance = double.MaxValue, bestStation = 0;
        foreach (int otherId in index.Near(end, MaxReach))
        {
            if (otherId == link.Id) continue;
            var other = net.Links[otherId];
            if (other.Layer != link.Layer || other.Centreline.Count < 2 || Apart(link, atStart, other, true) && Apart(link, atStart, other, false)) continue;
            // a dead end only: another end or a flank inside the tolerance is the normal path's
            if (other.First.DistanceTo(end) <= SnapTolerance || other.Last.DistanceTo(end) <= SnapTolerance) return false;
            if (!ClosestOnPolyline(other.Centreline, end, out double distance, out double station)) continue;
            if (distance <= SnapTolerance) return false;
            if (distance > Math.Min(other.Profile.HalfWidth, MaxReach) || distance >= bestDistance) continue;
            double total = Polyline.Length(other.Centreline);
            if (station < SnapTolerance || station > total - SnapTolerance) continue;
            if (!JoinNearEnds!(link, other)) continue;
            (best, bestDistance, bestStation) = (otherId, distance, station);
        }
        if (best < 0) return false;

        var line = net.Links[best].Centreline;
        var target = Polyline.PointAt(line, Polyline.ArcLengths(line), bestStation);
        if ((target - end).Normalized().Dot(outward) < 0.5) return false;   // it lies beside or behind the end
        if (atStart) pts.Insert(0, target); else pts.Add(target);
        AddCut(cuts, best, bestStation, Polyline.Length(line));
        NearEndsJoined++;
        return true;
    }

    /// <summary>
    /// A footpath end lying on a car road's carriageway (OSM ends it on the road's centreline, or the path runs on across the
    /// road through its node) is trimmed back to where it leaves the carriageway (#711): it meets the sidewalk there, which
    /// stands over it, or the kerb where there is none. At most 45 % of the path, so a stub is kept.
    /// </summary>
    private void TrimFootpathEnds(RoadNetwork net)
    {
        FootpathEndsTrimmed = 0;
        if (IsFootpath is null || IsCarRoad is null) return;
        var cars = net.Links.Where(l => l.Centreline.Count >= 2 && IsCarRoad(l)).ToList();
        if (cars.Count == 0) return;
        var index = new SegmentIndex(cars, 16);
        bool OnCarriageway(Vec2 p, int layer)
        {
            foreach (int id in index.Near(p, 12))
            {
                var car = net.Links[id];
                if (car.Layer != layer) continue;
                if (ClosestOnPolyline(car.Centreline, p, out double d, out _) && d < car.Profile.HalfWidth) return true;
            }
            return false;
        }
        const double Step = 0.25;
        foreach (var path in net.Links)
        {
            if (path.Centreline.Count < 2 || !IsFootpath(path)) continue;
            var line = path.Centreline;
            var lengths = Polyline.ArcLengths(line);
            double total = lengths[^1];
            foreach (bool atStart in new[] { true, false })
            {
                double trim = 0;
                for (double s = 0; s <= total * 0.45; s += Step)
                {
                    var p = Polyline.PointAt(line, lengths, atStart ? s : total - s);
                    if (!OnCarriageway(p, path.Layer)) break;
                    trim = s + Step;
                }
                if (trim <= 0) continue;
                trim = Math.Min(trim, total * 0.45);
                if (atStart) path.TrimStart = Math.Max(path.TrimStart, trim); else path.TrimEnd = Math.Max(path.TrimEnd, trim);
                FootpathEndsTrimmed++;
            }
        }
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
                    if (Apart(net.Links[endpoints[i].LinkId], endpoints[i].End == LinkEnd.Start, net.Links[endpoints[j].LinkId], endpoints[j].End == LinkEnd.Start)) continue;   // #711
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
