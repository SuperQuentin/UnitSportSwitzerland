namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using System.IO.Compression;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
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
        public readonly SortedDictionary<int, int> Cycles = new();

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    traffic lights (#348): {Junctions:N0} junctions ({Inferred:N0} inferred, {FromData:N0} from data), {Arms:N0} arms, {Approaches:N0} approaches, " +
            $"{LeftPockets:N0} with a left-turn pocket, {RightPockets:N0} with a right-turn pocket, {StopLines:N0} stop lines without a left pocket, {Groups:N0} signal groups, " +
            $"{TwoLensPedestrian:N0} with 2-lens pedestrian heads, cycles s: {string.Join(", ", Cycles.Select(kv => $"{kv.Key} x{kv.Value}"))}, invalid plans {Invalid:N0}\n");
    }

    private static PriorityResult PlanPriority(RoadGenResult result, Func<Junction, bool> signal, SignalStats stats)
    {
        var r = new PriorityResult();
        foreach (var junction in result.Junctions)
        {
            bool lights = signal(junction);
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
                bool approach = plan.Arms[i].Approach, leaves = PriorityPlanner.Leaves(info, plan.Arms[i].End);
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
            if (signalPlan.Validate().Count > 0) { stats.Invalid++; continue; }

            var anchors = Anchors(junction, net);
            var centre = Local(home, [junction.Centre], p => HeightAt(anchors, p), 0f);
            Get(signals, home).Add(new RoadSignal { X = centre[0], Y = centre[1], Z = centre[2], Stops = stops.ToArray(), Plan = signalPlan });
            stats.Junctions++;
            stats.Inferred++;
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
