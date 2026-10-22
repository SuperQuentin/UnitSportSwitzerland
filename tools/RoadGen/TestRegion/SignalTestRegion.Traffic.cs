namespace UnitSport.Tools.RoadGen.TestRegion;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// The test region's traffic (#1134, the user): generators and destinations on every lit junction's arms, written beside the tiles
/// (<see cref="TrafficEndpointsFile"/>) so the world comes with them. Cars and riders on each arm, their rates and their shares to
/// the other arms from what each movement's lights and lanes can pass (<see cref="SignalCapacity"/>); walkers at each corner from
/// the crossings' greens; trams at the ends of the tram tracks. One endpoint per arm and direction: a stretch between two lights
/// has its endpoints at its middle (each junction's own, each taking the other's traffic), two lights closer than
/// <see cref="ClusterReach"/> are one junction for the traffic (no endpoint between them, the cars carry on through both).
/// Docs: docs/notes/world/traffic-generators.md.
/// </summary>
public static partial class SignalTestRegion
{
    /// <summary>An arm's endpoint stands at most this far out (m): cars from it reach the lights in ~20 s.</summary>
    public const double ArmReach = 250;
    /// <summary>Lights closer than this along a road are one junction for the traffic (J5a/J5b, the staggered J9).</summary>
    public const double ClusterReach = 150;
    /// <summary>A corner's walkers start and end this far out along the arm (m), on its sidewalk.</summary>
    public const double CornerOut = 40;
    /// <summary>A tram endpoint stands this far in from its track's end (m): room behind it for the whole tram.</summary>
    public const double TramIn = 60;
    /// <summary>A tram every this many minutes each way along each tram line (#1134, a believable headway).</summary>
    public const float TramHeadwayMinutes = 6f;

    /// <summary>A lit junction as built: its centre (LV95), plan, approaches by arm, and name (the design's, else its place).</summary>
    public sealed record Lights(string Name, double E, double N, SignalPlan Plan, Dictionary<int, RoadApproach> Approaches);

    /// <summary>Where an arm's road leads: its endpoint (if any), or the lights it joins (a cluster).</summary>
    private sealed record ArmEnd(Lights Lights, int Arm, (double E, double N) At, float HeadingIn, (Lights Lights, int Arm)? Joins, double Width);

    /// <summary>Every lit junction in the chunk directory's road tiles.</summary>
    public static List<Lights> ReadLights(string chunkDir, IReadOnlyList<Junction> junctions)
    {
        var list = new List<Lights>();
        foreach (string path in Directory.EnumerateFiles(chunkDir, "roads_*.road").Order(StringComparer.Ordinal))
        {
            RoadTile tile;
            using (var fs = File.OpenRead(path)) tile = RoadCodec.Decode(fs);
            for (int k = 0; k < tile.Signals.Count; k++)
            {
                var s = tile.Signals[k];
                double e = tile.Id.MinE + s.X, n = tile.Id.MaxN - s.Z;
                var named = junctions.Where(j => Math.Abs(j.E - e) < 20 && Math.Abs(j.N - n) < 20).MinBy(j => Math.Abs(j.E - e) + Math.Abs(j.N - n));
                var approaches = new Dictionary<int, RoadApproach>();
                foreach (var a in tile.Approaches.Where(a => a.Signal == k)) approaches[a.SignalArm] = a;
                list.Add(new Lights(named?.Name.Split('-')[0] ?? string.Create(CultureInfo.InvariantCulture, $"L{e:F0},{n:F0}"), e, n, s.Plan, approaches));
            }
        }
        return list;
    }

    // ---- the roads as drawn: a graph of the design's lines ------------------------------------------

    private static long NodeKey((double E, double N) p) => ((long)Math.Round(p.E * 2) << 32) ^ (uint)Math.Round(p.N * 2);

    /// <summary>The design's lines as a graph: each node's segments out (to the next point, which line).</summary>
    private sealed class LineGraph
    {
        public readonly Dictionary<long, (double E, double N)> At = new();
        public readonly Dictionary<long, List<long>> Next = new();

        public LineGraph(IEnumerable<(double E, double N)[]> lines)
        {
            foreach (var pts in lines)
                for (int i = 0; i + 1 < pts.Length; i++)
                {
                    long a = NodeKey(pts[i]), b = NodeKey(pts[i + 1]);
                    if (a == b) continue;
                    At[a] = pts[i];
                    At[b] = pts[i + 1];
                    Add(a, b);
                    Add(b, a);
                }
        }

        private void Add(long a, long b)
        {
            if (!Next.TryGetValue(a, out var l)) Next[a] = l = new List<long>();
            if (!l.Contains(b)) l.Add(b);
        }

        public long? Nearest((double E, double N) p, double within)
        {
            long? best = null;
            double bd = within;
            foreach (var (k, q) in At)
            {
                double d = Dist(p, q);
                if (d < bd) (bd, best) = (d, k);
            }
            return best;
        }

        public int Degree(long k) => Next.TryGetValue(k, out var l) ? l.Count : 0;

        /// <summary>
        /// From <paramref name="from"/> along the segment leaving at <paramref name="heading"/> (within 35°), on through nodes with two
        /// segments, until <paramref name="stop"/> says so at a node, a node of another degree, or <paramref name="reach"/> m: the
        /// polyline walked and the node it stopped at (null at the reach).
        /// </summary>
        public (List<(double E, double N)> Path, long? StopAt) Walk(long from, double heading, double reach, Func<long, bool> stop)
        {
            var path = new List<(double E, double N)> { At[from] };
            if (!Next.TryGetValue(from, out var outs)) return (path, from);
            long? next = null;
            double bestTurn = 35 * Math.PI / 180;
            foreach (long o in outs)
            {
                double h = Math.Atan2(At[o].N - At[from].N, At[o].E - At[from].E);
                double turn = Math.Abs(Math.IEEERemainder(h - heading, 2 * Math.PI));
                if (turn < bestTurn) (bestTurn, next) = (turn, o);
            }
            if (next is null) return (path, from);
            long prev = from, at = next.Value;
            double length = Dist(At[from], At[at]);
            path.Add(At[at]);
            for (int guard = 0; guard < 10000; guard++)
            {
                if (length >= reach) return (path, null);
                if (stop(at) || Degree(at) != 2) return (path, at);
                long on = Next[at][0] == prev ? Next[at][1] : Next[at][0];
                length += Dist(At[at], At[on]);
                (prev, at) = (at, on);
                path.Add(At[at]);
            }
            return (path, at);
        }
    }

    /// <summary>The point <paramref name="along"/> m along a polyline, and the heading there (radians).</summary>
    private static ((double E, double N) At, double Heading) Along(List<(double E, double N)> path, double along)
    {
        for (int i = 0; i + 1 < path.Count; i++)
        {
            double len = Dist(path[i], path[i + 1]);
            double h = Math.Atan2(path[i + 1].N - path[i].N, path[i + 1].E - path[i].E);
            if (along <= len || i + 2 == path.Count)
            {
                double t = len > 0 ? Math.Clamp(along / len, 0, 1) : 0;
                return ((path[i].E + (path[i + 1].E - path[i].E) * t, path[i].N + (path[i + 1].N - path[i].N) * t), h);
            }
            along -= len;
        }
        return (path[^1], 0);
    }

    private static double PathLength(List<(double E, double N)> path)
    {
        double s = 0;
        for (int i = 0; i + 1 < path.Count; i++) s += Dist(path[i], path[i + 1]);
        return s;
    }

    // ---- the endpoints ------------------------------------------------------------------------------

    /// <summary>
    /// The region's endpoints (#1134) for the lit junctions <paramref name="lights"/> on the design's roads: per arm a car and a bike
    /// endpoint (generator where cars and riders come in, destination where they go out), per corner a walkers' one, per tram track
    /// end a tram's. Rates and shares from <see cref="SignalCapacity"/>.
    /// </summary>
    public static List<RegionEndpoint> Endpoints(IReadOnlyList<Lights> lights, Action<string>? log = null) => Endpoints(lights, Build().Lines, log);

    private static List<RegionEndpoint> Endpoints(IReadOnlyList<Lights> lights, List<Line> lines, Action<string>? log)
    {
        var roads = new LineGraph(lines.Where(l => l.Rail is null).Select(l => l.Points));
        var centres = new Dictionary<long, Lights>();
        foreach (var l in lights)
            if (roads.Nearest((l.E, l.N), 6) is { } k) centres[k] = l;
            else log?.Invoke($"[testregion] traffic: {l.Name} is on no designed road node, no endpoints");

        // where each arm leads
        var ends = new Dictionary<(Lights, int), ArmEnd>();
        foreach (var (node, l) in centres)
            for (int arm = 0; arm < l.Plan.Arms.Count; arm++)
            {
                var a = l.Plan.Arms[arm];
                if (!a.In && !a.Out) continue;
                var (path, stopAt) = roads.Walk(node, a.Heading, ArmReach, k => centres.ContainsKey(k));
                double length = PathLength(path);
                if (path.Count < 2) { log?.Invoke($"[testregion] traffic: {l.Name} arm {Compass(a.Heading)} follows no designed road"); continue; }
                double width = l.Approaches.TryGetValue(arm, out var ap) ? 3.5 * ap.Lanes.Count(x => x.Kind == ApproachLaneKind.Car) + 4 : 8;
                (Lights, int)? joins = null;
                double at;
                if (stopAt is { } s && centres.TryGetValue(s, out var other))
                {
                    if (length < ClusterReach)
                    {
                        // the arm of the other lights pointing back along this road
                        var (_, back) = Along(path, length - 0.1);
                        double headingBack = back + Math.PI;
                        int best = -1;
                        double bd = 40 * Math.PI / 180;
                        for (int k = 0; k < other.Plan.Arms.Count; k++)
                        {
                            double d = Math.Abs(Math.IEEERemainder(other.Plan.Arms[k].Heading - headingBack, 2 * Math.PI));
                            if (d < bd) (bd, best) = (d, k);
                        }
                        joins = best >= 0 ? (other, best) : null;
                        at = length / 2;
                    }
                    else at = Math.Min(ArmReach, length / 2 - 10);
                }
                else at = stopAt is null ? ArmReach : Math.Max(length / 2, length - 15);
                var (p, h) = Along(path, at);
                ends[(l, arm)] = new ArmEnd(l, arm, p, (float)Math.IEEERemainder(h + Math.PI, 2 * Math.PI), joins, width);
            }

        var list = new List<RegionEndpoint>();
        int nextId = 1;
        var carIds = new Dictionary<(Lights, int), int>();
        var bikeIds = new Dictionary<(Lights, int), int>();
        var carFlows = lights.ToDictionary(l => l, l => SignalCapacity.Cars(l.Plan, k => l.Approaches.TryGetValue(k, out var a) ? a.Lanes : null));
        var bikeFlows = lights.ToDictionary(l => l, l => SignalCapacity.Bikes(l.Plan, k => l.Approaches.TryGetValue(k, out var a) ? a.Lanes : null));
        var ordered = ends.Values.Where(x => x.Joins is null).OrderBy(x => x.Lights.N).ThenBy(x => x.Lights.E).ThenBy(x => x.Arm).ToList();
        foreach (var x in ordered) carIds[(x.Lights, x.Arm)] = nextId++;
        foreach (var x in ordered) bikeIds[(x.Lights, x.Arm)] = nextId++;

        // a movement's flow to the destinations it reaches: an arm with its own endpoint, else (a cluster) through the next lights by
        // their shares from the arm it arrives on
        void Spread(Dictionary<Lights, List<SignalCapacity.Flow>> flows, Dictionary<(Lights, int), int> ids, Lights l, int to, float weight,
            Dictionary<int, float> into, int depth)
        {
            if (ids.TryGetValue((l, to), out int id)) { into[id] = into.GetValueOrDefault(id) + weight; return; }
            if (depth > 3 || !ends.TryGetValue((l, to), out var end) || end.Joins is not var (next, arm)) return;
            var onward = flows[next].Where(f => f.From == arm).ToList();
            float sum = onward.Sum(f => f.PerMinute);
            if (sum <= 0) return;
            foreach (var f in onward) Spread(flows, ids, next, f.To, weight * f.PerMinute / sum, into, depth + 1);
        }

        foreach (var (mode, flows, ids) in new[] { (EndpointMode.Car, carFlows, carIds), (EndpointMode.Bike, bikeFlows, bikeIds) })
            foreach (var x in ordered)
            {
                var arm = x.Lights.Plan.Arms[x.Arm];
                var into = new Dictionary<int, float>();
                float rate = 0;
                foreach (var f in flows[x.Lights].Where(f => f.From == x.Arm))
                {
                    rate += f.PerMinute;
                    Spread(flows, ids, x.Lights, f.To, f.PerMinute, into, 0);
                }
                var shares = into.Where(s => s.Value > 1e-4f).OrderBy(s => s.Key).Select(s => (s.Key, s.Value)).ToArray();
                bool generates = rate > 1e-3f && shares.Length > 0;
                if (!generates && !arm.Out) continue;
                list.Add(new RegionEndpoint(ids[(x.Lights, x.Arm)], mode, x.At.E, x.At.N, x.HeadingIn, generates, arm.Out,
                    generates ? rate : 0f, generates ? shares : [], x.Lights.Name, Compass(arm.Heading)));
            }
        // ids of endpoints not written (an arm with nothing in or out) are simply unused

        nextId = Corners(lights, ends, list, nextId);
        Trams(lines, list, nextId);
        return list;
    }

    /// <summary>
    /// The walkers' endpoints (#1134): one at each corner of a junction with crossings, on the sidewalk of the corner's arm
    /// (counter-clockwise of it) <see cref="CornerOut"/> m out, its walkers sent over the two crossings either side of the corner
    /// (half of each crossing's walkers, <see cref="SignalCapacity.Walkers"/>) to the corners beyond.
    /// </summary>
    private static int Corners(IReadOnlyList<Lights> lights, Dictionary<(Lights, int), ArmEnd> ends, List<RegionEndpoint> list, int nextId)
    {
        foreach (var l in lights)
        {
            var crossings = SignalCapacity.Walkers(l.Plan);
            if (crossings.Count == 0) continue;
            // the arms counter-clockwise from east; corner k lies between arm k and the next one counter-clockwise
            var arms = Enumerable.Range(0, l.Plan.Arms.Count).Where(k => ends.ContainsKey((l, k)))
                .OrderBy(k => (l.Plan.Arms[k].Heading % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI)).ToList();
            if (arms.Count < 2) continue;
            var ids = new int[arms.Count];
            var spots = new ((double E, double N) At, float Heading)?[arms.Count];
            for (int c = 0; c < arms.Count; c++)
            {
                var end = ends[(l, arms[c])];
                double h = l.Plan.Arms[arms[c]].Heading;
                double length = Dist((l.E, l.N), end.At) * (end.Joins is null ? 2 : 1);
                double out_ = Math.Min(CornerOut, length / 2 - 4);
                if (out_ < 8) continue;
                // counter-clockwise of the arm's way out: its left
                double side = end.Width;
                var at = (l.E + Math.Cos(h) * out_ - Math.Sin(h) * side, l.N + Math.Sin(h) * out_ + Math.Cos(h) * side);
                spots[c] = (at, (float)Math.IEEERemainder(h + Math.PI, 2 * Math.PI));
                ids[c] = nextId++;
            }
            for (int c = 0; c < arms.Count; c++)
            {
                if (spots[c] is not { } spot) continue;
                int prev = (c + arms.Count - 1) % arms.Count, next = (c + 1) % arms.Count;
                var into = new Dictionary<int, float>();
                // over this corner's arm (arm c) to the corner before; over the next arm to the corner after
                if (crossings.TryGetValue(arms[c], out float fa) && spots[prev] is not null && prev != c) into[ids[prev]] = into.GetValueOrDefault(ids[prev]) + fa / 2;
                if (crossings.TryGetValue(arms[next], out float fb) && spots[next] is not null && next != c) into[ids[next]] = into.GetValueOrDefault(ids[next]) + fb / 2;
                float rate = into.Values.Sum();
                var shares = into.Where(s => s.Value > 0).OrderBy(s => s.Key).Select(s => (s.Key, s.Value)).ToArray();
                list.Add(new RegionEndpoint(ids[c], EndpointMode.Walker, spot.At.E, spot.At.N, spot.Heading, rate > 0, true, rate, shares,
                    l.Name, Compass(l.Plan.Arms[arms[c]].Heading) + Compass(l.Plan.Arms[arms[next]].Heading)));
            }
        }
        return nextId;
    }

    /// <summary>
    /// The trams' endpoints (#1134): at each end of a tram line, <see cref="TramIn"/> m in. A double-track line's both ways; a single
    /// track's where its trams keep right (the parallel track on their left, as the region draws its pairs): a generator heading in
    /// there, a destination at the other end. A tram every <see cref="TramHeadwayMinutes"/> min each way.
    /// </summary>
    private static int Trams(List<Line> lines, List<RegionEndpoint> list, int nextId)
    {
        var trams = lines.Where(l => l.Rail is { } f && (f & RoadFlags.Tramway) != 0).ToList();
        var graph = new LineGraph(trams.Select(l => l.Points));
        foreach (var (k, at) in graph.At.OrderBy(x => x.Value.N).ThenBy(x => x.Value.E))
        {
            if (graph.Degree(k) != 1) continue;
            var line = trams.First(l => NodeKey(l.Points[0]) == k || NodeKey(l.Points[^1]) == k);
            double inward = Math.Atan2(graph.At[graph.Next[k][0]].N - at.N, graph.At[graph.Next[k][0]].E - at.E);
            var (path, _) = graph.Walk(k, inward, TramIn + 1, _ => false);
            var (spot, h) = Along(path, Math.Min(TramIn, PathLength(path) / 2));
            bool generates = (line.Rail!.Value & RoadFlags.DoubleTrack) != 0 || KeepsRight(trams, spot, h);
            list.Add(new RegionEndpoint(nextId++, EndpointMode.Tram, spot.E, spot.N, (float)h, generates, true,
                generates ? 1f / TramHeadwayMinutes : 0f, [], line.Id, Compass(h + Math.PI)));
        }
        return nextId;
    }

    /// <summary>Whether a single tram track's trams run <paramref name="heading"/> at <paramref name="at"/>: the nearest other tram track within 6 m lies on their left.</summary>
    private static bool KeepsRight(List<Line> trams, (double E, double N) at, double heading)
    {
        double best = 6, side = 0;
        foreach (var l in trams)
            for (int i = 0; i + 1 < l.Points.Length; i++)
            {
                var (a, b) = (l.Points[i], l.Points[i + 1]);
                double dx = b.E - a.E, dn = b.N - a.N, len2 = dx * dx + dn * dn;
                if (len2 < 1e-6) continue;
                double t = Math.Clamp(((at.E - a.E) * dx + (at.N - a.N) * dn) / len2, 0, 1);
                double px = a.E + dx * t - at.E, pn = a.N + dn * t - at.N, d = Math.Sqrt(px * px + pn * pn);
                if (d < 0.5 || d >= best) continue;
                best = d;
                side = Math.Cos(heading) * pn - Math.Sin(heading) * px;   // > 0: left of the heading
            }
        return side > 0;
    }

    /// <summary>Writes the region's endpoints beside its tiles and logs a line per junction (cars and riders a minute by arm).</summary>
    private static List<RegionEndpoint> WriteTraffic(string chunkDir, IReadOnlyList<Junction> junctions, List<Line> lines, Action<string> log)
    {
        var lights = ReadLights(chunkDir, junctions);
        var endpoints = Endpoints(lights, lines, log);
        TrafficEndpointsFile.Write(Path.Combine(chunkDir, TrafficEndpointsFile.FileName), endpoints);
        var inv = CultureInfo.InvariantCulture;
        foreach (var j in endpoints.GroupBy(x => x.Junction))
        {
            string Rates(EndpointMode m) => string.Join(" ", j.Where(x => x.Mode == m && x.Generates).Select(x => string.Create(inv, $"{x.Arm} {x.PerMinute:0.0}")));
            log($"traffic {j.Key,-18} cars/min {Rates(EndpointMode.Car)}; bikes/min {Rates(EndpointMode.Bike)}; walkers/min {Rates(EndpointMode.Walker)}"
                + (j.Any(x => x.Mode == EndpointMode.Tram) ? $"; trams/min {Rates(EndpointMode.Tram)}" : ""));
        }
        log($"[testregion] traffic (#1134): {endpoints.Count} endpoints ({string.Join(", ", endpoints.GroupBy(x => x.Mode).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"))}) "
            + $"at {lights.Count} lit junctions, {TrafficEndpointsFile.FileName}");
        return endpoints;
    }
}
