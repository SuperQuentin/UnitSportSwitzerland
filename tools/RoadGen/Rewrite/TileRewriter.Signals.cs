namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using System.IO.Compression;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Traffic lights (#348): the junctions <see cref="PriorityPlanner"/> marked
/// <see cref="PriorityPlanner.Kind.Signal"/> get a white stop line (SSV 6.10, 0.50 m) across the
/// approach lanes of every arm (a left-turn pocket's own, #123, already runs across both of its
/// lanes) and a <see cref="RoadSignal"/> record in their home tile: per arm whether traffic comes
/// in and goes out, the pocket built there and the stop line's middle, and the fixed-time plan
/// <see cref="SignalPlan.Build"/> makes of it (#349). Rules and numbers: docs/notes/tools/traffic-signals.md.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>The stop line at traffic lights: 0.50 m (SSV 6.10; Kanton Bern Handbuch Markierung).</summary>
    private const float SignalStopLine = 0.5f;

    /// <summary>
    /// The stop line at traffic lights lies this far back from the junction's mouth: behind a red
    /// bike crossing there (#120, about 2 m) with a metre to spare. #292 moves it back behind a
    /// pedestrian crossing.
    /// </summary>
    private const double SignalStopSetback = 3.0;

    /// <summary>
    /// A link this short between two signalised junction nodes is inside one junction (a large
    /// junction is several TLM nodes, e.g. round tram tracks or a divided road's carriageways):
    /// no stop line on it, and it is not an approach (#348).
    /// </summary>
    private const double InternalLinkM = 30.0;

    private static bool Internal(RoadLink link, int node, HashSet<int> signalNodes)
    {
        int other = link.StartNode == node ? link.EndNode : link.StartNode;
        if (other == node || !signalNodes.Contains(other)) return false;
        double length = 0;
        for (int i = 1; i < link.Centreline.Count; i++) length += link.Centreline[i].DistanceTo(link.Centreline[i - 1]);
        return length < InternalLinkM;
    }

    /// <summary>
    /// How much further out than its middle a skewed mouth reaches along the arm: a stop line
    /// square to the road stands that much further back, so none of it lies in the junction (#348).
    /// </summary>
    private static double MouthSkew(Junction j, JunctionArm arm)
    {
        var u = Vec2.FromHeading(arm.OutwardHeading);
        double mid = ((arm.Left + arm.Right) * 0.5 - j.Centre).Dot(u);
        return Math.Max(0, Math.Max((arm.Left - j.Centre).Dot(u), (arm.Right - j.Centre).Dot(u)) - mid);
    }

    public sealed class SignalStats
    {
        public int Junctions, Inferred, FromData, Arms, Approaches, LeftPockets, RightPockets, StopLines, Groups, TwoLensPedestrian, Invalid;
        /// <summary>Where OSM decides, what the inference rule would have said: both, rule only, OSM only (#348 tuning).</summary>
        public int RuleAndOsm, RuleOnly, OsmOnly, InternalArms;
        public readonly List<string> InvalidExamples = new();
        public readonly SortedDictionary<int, int> Cycles = new();

        public string Format()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(c, $"    traffic lights (#348): {Junctions:N0} junctions ({Inferred:N0} inferred, {FromData:N0} from data), {Arms:N0} arms, {Approaches:N0} approaches, ");
            sb.Append(c, $"{LeftPockets:N0} with a left-turn pocket, {RightPockets:N0} with a right-turn pocket, {StopLines:N0} stop lines without a left pocket, {Groups:N0} signal groups, ");
            sb.Append(c, $"{TwoLensPedestrian:N0} with 2-lens pedestrian heads, cycles s: {string.Join(", ", Cycles.Select(kv => $"{kv.Key} x{kv.Value}"))}, invalid plans {Invalid:N0}").AppendLine();
            sb.Append(c, $"      where OSM decides, the inference rule agrees on {RuleAndOsm:N0}, adds {RuleOnly:N0} OSM does not have, misses {OsmOnly:N0}; {InternalArms:N0} arms inside a junction of several nodes").AppendLine();
            foreach (var x in InvalidExamples) sb.Append("      invalid: ").Append(x).AppendLine();
            return sb.ToString();
        }
    }

    /// <summary>Why a junction has traffic lights: none, the inference rule, or OSM data (#347).</summary>
    private enum SignalSource : byte { None, Inferred, Data }

    /// <summary>
    /// Where a junction gets traffic lights (#348). Data first: where most of its car arms were
    /// matched to OSM ways (the overlay covers it) and the overlay's signal file exists, it has
    /// lights only if OSM has <c>highway=traffic_signals</c> at it (#347: on the junction's node, or
    /// on an approach of one of its arms' TLM lines, anchored at that line's end), on any junction
    /// shape that can carry them (a T too). Elsewhere the inference rule
    /// (<see cref="PriorityPlanner.InferSignal"/>).
    /// </summary>
    private static SignalSource Lights(Junction j, RoadNetwork net, UrbanField field, SignalSites? sites, SignalStats? stats = null)
    {
        var car = j.Arms.Select(a => net.Links[a.LinkId].Tag as Source)
            .Where(s => s is not null && PriorityPlanner.IsCarRoad(s.Segment.Class)).ToList();
        bool covered = sites is not null && car.Count > 0 && car.Count(s => s!.Line.Osm is not null) * 2 >= car.Count;
        bool rule = PriorityPlanner.InferSignal(j, net, InfoOf, field.Density(j.Centre.X, j.Centre.Y));
        if (!covered) return rule ? SignalSource.Inferred : SignalSource.None;
        var uuids = car.Select(s => s!.Key?.Uuid).Where(u => u is not null).ToHashSet();
        bool osm = sites!.At(j.Centre, uuids!) && PriorityPlanner.SignalShape(j, net, InfoOf, 3) is not null;
        if (stats is not null)
        {
            if (rule && osm) stats.RuleAndOsm++;
            else if (rule) stats.RuleOnly++;
            else if (osm) stats.OsmOnly++;
        }
        return osm ? SignalSource.Data : SignalSource.None;
    }

    /// <summary>
    /// OSM junction signals (#347, <c>osm_nodes.tsv</c>), by where they anchor: a signal on an
    /// approach at its TLM line's end (the junction it stands before), one on a junction node at
    /// itself. Pedestrian-only signals (<c>crossing=traffic_signals</c>) do not make a junction.
    /// </summary>
    private sealed class SignalSites
    {
        /// <summary>A line end or node this close to a junction's centre is that junction's.</summary>
        private const double Reach = 12.0, Cell = 50.0;
        private readonly Dictionary<(long, long), List<(Vec2 At, string Uuid, bool Node)>> _grid = new();
        public int Count { get; private set; }

        public static SignalSites? From(OsmNodesReader? nodes)
        {
            if (nodes is null) return null;
            var sites = new SignalSites();
            foreach (var e in nodes.All)
            {
                if (e.Kind != OsmNodesReader.NodeKind.Signal || e.PedestrianOnly) continue;
                bool node = e.Junction == OsmNodesReader.JunctionKind.Node;
                if (!node && (e.Junction != OsmNodesReader.JunctionKind.Approach || e.LineEnd == OsmNodesReader.End.None)) continue;
                var at = node ? new Vec2(e.E, e.N) : new Vec2(e.EndE, e.EndN);
                var key = ((long)Math.Floor(at.X / Cell), (long)Math.Floor(at.Y / Cell));
                if (!sites._grid.TryGetValue(key, out var list)) sites._grid[key] = list = new();
                list.Add((at, e.Uuid, node));
                sites.Count++;
            }
            return sites;
        }

        public bool At(Vec2 centre, HashSet<string> armLines)
        {
            long cx = (long)Math.Floor(centre.X / Cell), cy = (long)Math.Floor(centre.Y / Cell);
            for (long x = cx - 1; x <= cx + 1; x++)
            for (long y = cy - 1; y <= cy + 1; y++)
                if (_grid.TryGetValue((x, y), out var list))
                    foreach (var (at, uuid, node) in list)
                        if (at.DistanceTo(centre) <= Reach && (node || armLines.Contains(uuid))) return true;
            return false;
        }
    }

    private static PriorityResult PlanPriority(RoadGenResult result, Func<Junction, SignalSource> signal, SignalStats stats)
    {
        var r = new PriorityResult();
        foreach (var junction in result.Junctions)
        {
            var source = signal(junction);
            bool lights = source != SignalSource.None;
            if (source == SignalSource.Data) r.SignalsFromData.Add(junction.NodeId);
            var plan = PriorityPlanner.Decide(junction, result.Network, InfoOf, lights);
            r.Plans.Add((junction, plan));
            foreach (var arm in plan.Arms)
                if (arm.Role == PriorityPlanner.Role.Yield && arm.Approach)
                    r.Yield[arm.LinkId] = r.FlagsOf(arm.LinkId)
                        | (arm.End == LinkEnd.Start ? RoadAttrFlags.YieldAtStart : RoadAttrFlags.YieldAtEnd);
        }
        return r;
    }

    private static void EmitSignals(PriorityResult priority, RoadGenResult result, Dictionary<(int Node, int Arm), (bool Left, bool Right)> pockets,
        HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, List<RoadPaint>> paint,
        Dictionary<TileId, List<RoadSignal>> signals, Cantons? cantons, UrbanField field, SignalStats stats)
    {
        var net = result.Network;
        var signalNodes = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Signal).Select(p => p.Junction.NodeId).ToHashSet();
        foreach (var (junction, plan) in priority.Plans)
        {
            if (plan.Kind != PriorityPlanner.Kind.Signal) continue;
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;

            var arms = new List<SignalArm>();
            var stops = new List<float>();
            // 50 km/h inside a locality: the yellow lasts 3 s (#349)
            bool urban = field.Density(junction.Centre.X, junction.Centre.Y) >= UrbanField.UrbanAt;
            for (int i = 0; i < junction.Arms.Count && i < plan.Arms.Count; i++)
            {
                var link = net.Links[plan.Arms[i].LinkId];
                if (link.Tag is not Source source || InfoOf(link) is not { } info || !PriorityPlanner.IsCarRoad(info.Class)) continue;
                var arm = junction.Arms[i];
                bool inside = Internal(link, junction.NodeId, signalNodes);
                bool approach = plan.Arms[i].Approach && !inside, leaves = PriorityPlanner.Leaves(info, plan.Arms[i].End);
                if (inside) stats.InternalArms++;
                var (pocket, rightPocket) = pockets.GetValueOrDefault((junction.NodeId, i));
                var u = Vec2.FromHeading(arm.OutwardHeading);
                var right = u.Perp;   // the approaching driver's right (they drive along -u)
                var mid = (arm.Left + arm.Right) * 0.5;
                double half = arm.HalfWidth;
                // the approach lanes: from the centre (a one-way road: its left edge) to the right
                // edge, and on over the through lane a pocket moved out
                double from = info.Attributes.OneWay != 0 ? -half : 0, to = half + (pocket ? TurnLane : 0) + (rightPocket ? TurnLane : 0);
                var bar = mid + u * (MouthSkew(junction, arm) + SignalStopSetback + SignalStopLine * 0.5);
                if (approach && !pocket && block.Contains(source.Tile))
                {
                    Get(paint, source.Tile).Add(new RoadPaint
                    {
                        Shape = PaintShape.Polyline, Type = PaintType.StopLine, Rgba = PaintEmitter.White, Width = SignalStopLine,
                        Vertices = Local(source.Tile, [bar + right * (from + 0.1), bar + right * (half - 0.1)], source.SampleHeight, 0f),
                    });
                    stats.StopLines++;
                }
                var stop = bar + right * ((from + to) * 0.5);
                if (approach) stops.AddRange(Local(home, [stop], source.SampleHeight, 0f));
                else stops.AddRange([float.NaN, float.NaN, float.NaN]);
                arms.Add(new SignalArm(arm.OutwardHeading, approach, leaves, pocket, rightPocket, Pedestrians: true,
                    BikeSignal: false, SpeedKmh: urban ? 50 : 60, CrossingM: (float)(to - from + (info.Attributes.OneWay != 0 ? 0 : half)),
                    Rank: (byte)Math.Clamp(PriorityPlanner.Rank(info) / 4, 1, 255)));
                stats.Arms++;
                if (approach) stats.Approaches++;
                if (pocket) stats.LeftPockets++;
                if (rightPocket) stats.RightPockets++;
            }
            if (arms.Count(a => a.In) < 2) continue;

            bool amber = PedestrianAmber(cantons?.CodeAt(junction.Centre.X, junction.Centre.Y));
            uint seed = (uint)(long)Math.Round(junction.Centre.X) * 73856093u ^ (uint)(long)Math.Round(junction.Centre.Y) * 19349663u;
            var signalPlan = SignalPlan.Build(arms, seed, amber);
            if (signalPlan.Validate() is { Count: > 0 } errors)
            {
                stats.Invalid++;
                if (stats.InvalidExamples.Count < 5)
                    stats.InvalidExamples.Add(string.Create(CultureInfo.InvariantCulture,
                        $"LV95 {junction.Centre.X:F0},{junction.Centre.Y:F0} {arms.Count} arms ({string.Join(" ", arms.Select(a => $"{a.Heading * 180 / Math.PI:F0}{(a.In ? "i" : "")}{(a.Out ? "o" : "")}{(a.LeftPocket ? "L" : "")}{(a.RightPocket ? "R" : "")}r{a.Rank}"))}): {string.Join("; ", errors.Take(2))}"));
                continue;
            }

            var anchors = Anchors(junction, net);
            var centre = Local(home, [junction.Centre], p => HeightAt(anchors, p), 0f);
            Get(signals, home).Add(new RoadSignal { X = centre[0], Y = centre[1], Z = centre[2], Stops = stops.ToArray(), Plan = signalPlan });
            stats.Junctions++;
            if (priority.SignalsFromData.Contains(junction.NodeId)) stats.FromData++; else stats.Inferred++;
            stats.Groups += signalPlan.Groups.Count;
            if (!amber) stats.TwoLensPedestrian++;
            int cycle = (int)MathF.Round(signalPlan.Cycle);
            stats.Cycles[cycle] = stats.Cycles.GetValueOrDefault(cycle) + 1;
        }
    }

    /// <summary>
    /// Pedestrian heads depend on the canton: Geneva's have two lenses (green, then red), Vaud's
    /// three (with a yellow). Cantons not checked yet keep three (#350 records the table).
    /// </summary>
    private static bool PedestrianAmber(string? canton) => canton != "GE";

    /// <summary>
    /// The canton of each kilometre tile, from MapSetup's committed <c>switzerland.bin</c>
    /// (<c>tools/MapSetup/CountryData.cs</c> writes it; this reads only the canton lattice and names).
    /// </summary>
    public sealed class Cantons
    {
        private const int MinE = 2480, MinN = 1070, Width = 360, Height = 230;
        private readonly byte[] _ids;
        private readonly List<string> _codes;

        private Cantons(byte[] ids, List<string> codes) { _ids = ids; _codes = codes; }

        public string? CodeAt(double e, double n)
        {
            int te = (int)Math.Floor(e / 1000), tn = (int)Math.Floor(n / 1000);
            if (te < MinE || tn < MinN || te >= MinE + Width || tn >= MinN + Height) return null;
            int id = _ids[(tn - MinN) * Width + (te - MinE)];
            return id == 0 || id > _codes.Count ? null : _codes[id - 1];
        }

        /// <summary>The repository's country file, looked for upward from the tool and the working directory; null when missing.</summary>
        public static Cantons? Find()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                {
                    string path = Path.Combine(dir.FullName, "tools", "MapSetup", "switzerland.bin");
                    if (File.Exists(path)) return Load(path);
                }
            return null;
        }

        public static Cantons? Load(string path)
        {
            try
            {
                using var file = File.OpenRead(path);
                using var z = new GZipStream(file, CompressionMode.Decompress);
                using var r = new BinaryReader(z, Encoding.UTF8);
                if (r.ReadString() != "CHMAP1") return null;
                r.ReadInt64();                          // baked at
                r.ReadBytes(Width * Height * 4);        // zip sizes
                r.ReadBytes(Width * Height);            // survey years
                var ids = r.ReadBytes(Width * Height);  // cantons
                r.ReadBytes(Width * Height * 2);        // max elevations
                var codes = new List<string>();
                for (int i = r.ReadInt32(); i > 0; i--)
                {
                    r.ReadByte();
                    codes.Add(r.ReadString());
                    r.ReadString();
                }
                return new Cantons(ids, codes);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
            {
                return null;
            }
        }
    }
}
