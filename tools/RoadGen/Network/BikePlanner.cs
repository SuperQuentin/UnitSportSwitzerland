namespace UnitSport.Tools.RoadGen.Network;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// Bike infrastructure (#120), decided by the network stage. swissTLM3D records none, so it is
/// inferred (OSM <c>cycleway</c>, when the overlay has it, overrides per side):
///
/// <list type="bullet">
/// <item><b>Which roads</b> (<see cref="PlanLines"/>, on the raw lines before the geometry, so
/// the junction priority sees the lanes): paved at-grade <c>Major</c> and <c>Road</c>, and
/// <c>Minor</c> on a Veloland route; never motorways, ramps, roundabout rings, tunnels, stairs.
/// Outside towns, skipped where a smaller road, track or path runs alongside (<see cref="ParallelReach"/>,
/// <see cref="ParallelCos"/>) for most of the line (<see cref="ParallelShare"/>): cyclists take
/// that one.</item>
/// <item><b>Painted lanes</b> (Radstreifen): yellow dashed, <see cref="LaneWidth"/> wide inside the
/// carriageway, on both sides of a two-way road (on the right of travel of a one-way one), if the
/// car lanes left keep <see cref="MinCarLane"/> with a centre line between them; else, narrower
/// lanes and no centre line (Kernfahrbahn) if the shared core keeps <see cref="MinCore"/>; else
/// none (narrow).</item>
/// <item><b>Separated paths</b> in towns (<c>StreetPlanner</c>): where the corridor from the kerb
/// to the facade holds a path layout and a sidewalk, one of five layouts (<see cref="Layouts"/>),
/// chosen per street (<see cref="StrokeLayouts"/>: the same along a street continued straight
/// through its junctions), narrowed to a smaller one where room runs short. A street that gets a
/// path for most of its length drops its painted lanes.</item>
/// </list>
/// </summary>
public static class BikePlanner
{
    /// <summary>
    /// Radstreifen width, carriageway edge to the line's axis: 1.50 m inside localities, 1.80 m
    /// outside (Kanton Bern AH Anlagen für den Veloverkehr 2021 §4.3; ASTRA Handbuch Veloverkehr in
    /// Kreuzungen 2021 Abb. 301; measured to the middle of the line, Kanton Zürich Standards
    /// Veloverkehr 2023 p. 37).
    /// </summary>
    public static double LaneWidth(bool urban) => urban ? 1.5 : 1.8;
    /// <summary>Narrowest Radstreifen (Graubünden Sachplan Velo Anhang A §2.6: 1.25 m beside a 3 m lane).</summary>
    public const double MinLaneWidth = 1.25;
    /// <summary>Narrowest car lane beside a bike lane on a road that keeps its centre line (ZH table 4.11.1-3, "Schmalfahrbahn").</summary>
    public const double MinCarLane = 2.75;
    /// <summary>
    /// Narrowest shared core of a Kernfahrbahn. The Swiss guides ask 4.5 m and keep it inside
    /// localities (SSV Art. 74a al. 2: lanes on both sides outside need a centre line); the game
    /// goes down to 3.5 m and uses it outside too, so 6 m roads get lanes (user's choice, #120).
    /// </summary>
    public const double MinCore = 3.5;
    /// <summary>A one-way road keeps one car lane at least this wide beside its bike lane.</summary>
    public const double MinOneWayLane = 3.0;
    /// <summary>Radstreifen line: 0.15 m yellow, 3 m dash / 3 m gap, 1 m / 1 m across a junction (Stadt Bern C 2.10.10; ZH p. 37).</summary>
    public const float LineWidth = 0.15f, Dash = 3f, Gap = 3f, JunctionDash = 1f;
    /// <summary>Velo symbol, 1.00 x 1.00 m (Stadt Bern C 2.10.4; ZH p. 39), this far from a lane's end.</summary>
    public const float SymbolSize = 1.0f, SymbolFromEnd = 4.0f;
    /// <summary>A bike lane or path piece shorter than this gets no symbol.</summary>
    public const float SymbolMinLength = 25f;

    /// <summary>A parallel alternative lies within this distance of the road (centreline to centreline).</summary>
    public const double ParallelReach = 40.0;
    /// <summary>... and runs within 20 degrees of it.</summary>
    public const double ParallelCos = 0.94;
    /// <summary>... along at least this share of the road's length.</summary>
    public const double ParallelShare = 0.7;
    private const double ParallelStep = 10.0;

    /// <summary>A separated path, one direction: 2.00 m (ASTRA Handbuch Veloverkehr in Kreuzungen Abb. 301; ZH, GR).</summary>
    public const byte TrackDm = 20;
    /// <summary>
    /// Grass strip between the carriageway and the path (Kanton Bern: normally 1.0 m, at least
    /// 0.8 m), and between the path and the sidewalk.
    /// </summary>
    public const byte VergeDm = 10, BufferDm = 8;

    /// <summary>
    /// The five path layouts (user's spec for #120), outward from the kerb:
    /// 1 path at sidewalk height against the kerb, yellow dashes to the sidewalk;
    /// 2 as 1 behind a grass verge;
    /// 3 path halfway down between road and sidewalk, sloped kerbs both sides;
    /// 4 as 3 behind a grass verge;
    /// 5 as 4 with a grass strip between path and sidewalk too.
    /// </summary>
    public static readonly (BikeKind Kind, byte Verge, byte Buffer)[] Layouts =
    [
        (BikeKind.None, 0, 0),
        (BikeKind.Track, 0, 0),
        (BikeKind.Track, VergeDm, 0),
        (BikeKind.TrackMid, 0, 0),
        (BikeKind.TrackMid, VergeDm, 0),
        (BikeKind.TrackMid, VergeDm, BufferDm),
    ];

    /// <summary>Width of a layout in metres (verge + path + buffer).</summary>
    public static double LayoutWidth(int layout) =>
        layout <= 0 ? 0 : (Layouts[layout].Verge + TrackDm + Layouts[layout].Buffer) / 10.0;

    /// <summary>The next smaller layout of the same level (5 → 4 → 3, 2 → 1), 0 when none.</summary>
    public static int Smaller(int layout) => layout switch { 5 => 4, 4 => 3, 2 => 1, _ => 0 };

    public enum Why : byte { NotCandidate, Lane, Kern, Narrow, Parallel, Osm }

    /// <summary>Region numbers, over written lines (km) and written segments.</summary>
    public sealed class Stats
    {
        public double CandidateKm, LaneKm, KernKm, NarrowKm, ParallelKm, OsmKm;
        public int Candidates, Parallel;
        /// <summary>Urban stations by path layout chosen (0 = no path fitted), and stations narrowed from the street's layout.</summary>
        public readonly int[] TrackStations = new int[6];
        public int TrackNarrowed, TrackStreets, LaneStreets;
        /// <summary>Final pieces (km): lane per side, path per side by layout.</summary>
        public double LaneSideKm, UrbanLaneSideKm;
        public readonly double[] TrackSideKm = new double[6];
        public int Symbols, Crossings, CrossingsLane, CrossingsTrack, LanesThrough, PathsThrough, CornersReplaced, PathsShiftedOffTurnLanes, TeethMoved, SignsMoved;
        /// <summary>Bike lanes across a signalised junction (#406): red where a car crosses them in the same phase, else only their dashed edges.</summary>
        public int SignalLanesRed, SignalLanesDashed;
        /// <summary>Gaps cut in a grass verge where a left-turn pocket opens (#352), and openings too near a segment end to cut.</summary>
        public int VergeCuts, VergeCutsRejected;
        public readonly List<string> VergeCutsAt = new();

        public string Format()
        {
            var c = CultureInfo.InvariantCulture;
            int st = TrackStations.Sum();
            string Pct(int n) => st == 0 ? "-" : (100.0 * n / st).ToString("F0", c) + "%";
            return string.Create(c, $"""
                  bikes (#120): {Candidates:N0} candidate lines {CandidateKm:F1} km: lanes {LaneKm:F1} km, Kernfahrbahn {KernKm:F1} km, too narrow {NarrowKm:F1} km, parallel alternative {ParallelKm:F1} km ({Parallel:N0} lines), OSM {OsmKm:F1} km
                    final: lane sides {LaneSideKm:F1} km ({UrbanLaneSideKm:F1} in town); path sides by layout 1..5 {TrackSideKm[1]:F2}/{TrackSideKm[2]:F2}/{TrackSideKm[3]:F2}/{TrackSideKm[4]:F2}/{TrackSideKm[5]:F2} km
                    urban stations wanting a path: none fits {Pct(TrackStations[0])}, layouts 1..5 {Pct(TrackStations[1])}/{Pct(TrackStations[2])}/{Pct(TrackStations[3])}/{Pct(TrackStations[4])}/{Pct(TrackStations[5])}, narrowed {TrackNarrowed:N0}; streets with a path {TrackStreets:N0}, keeping lanes {LaneStreets:N0}
                    paint: {Symbols:N0} bike symbols, {Crossings:N0} red crossings ({CrossingsLane:N0} lane, {CrossingsTrack:N0} path), {LanesThrough:N0} lanes and {PathsThrough:N0} paths carried through ({CornersReplaced:N0} sidewalk corners replaced); teeth moved behind a path crossing {TeethMoved:N0}, signs moved off a path {SignsMoved:N0}; path pieces moved out past a turn lane's widening {PathsShiftedOffTurnLanes:N0}; at traffic lights, lanes straight from the stop line: red {SignalLanesRed:N0} (a car crosses in the same phase), dashed edges only {SignalLanesDashed:N0} (#406); verge gaps for cyclists into a left-turn pocket {VergeCuts:N0} (#352, {VergeCutsRejected:N0} too near a segment end; first at LV95 {string.Join(" ", VergeCutsAt)})
                """);
        }
    }

    /// <summary>A line that may get bike infrastructure: paved, at grade, an ordinary road.</summary>
    public static bool IsCandidate(RoadSegment s)
    {
        if (s.Surface != RoadSurface.Paved || s.PointCount < 2) return false;
        if ((s.Flags & (RoadFlags.Tunnel | RoadFlags.Stairs | RoadFlags.Ford)) != 0) return false;
        if (s.Attributes.Has(RoadAttrFlags.Roundabout)) return false;
        return s.Class is RoadClass.Major or RoadClass.Road
            || (s.Class == RoadClass.Minor && (s.Flags & RoadFlags.Cycle) != 0);
    }

    /// <summary>A line cyclists would take instead of a road beside it: a smaller road, a track, a path.</summary>
    private static bool IsAlternative(RoadSegment s) =>
        s.Class is RoadClass.Lane or RoadClass.Track or RoadClass.Path
        && (s.Flags & (RoadFlags.Stairs | RoadFlags.Tunnel)) == 0;

    /// <summary>
    /// Decides every candidate line of the block and its halo: <see cref="CrossSectionPlanner.Line.BikeLaneDm"/>
    /// (0 = none) and why. Halo lines are decided too: a junction of the block sees them.
    /// </summary>
    public static void PlanLines(List<CrossSectionPlanner.Line> lines, UrbanField field, Stats stats)
    {
        var alternatives = new Grid();
        foreach (var line in lines)
            if (IsAlternative(line.Segment)) alternatives.Add(line.Plan);

        foreach (var line in lines)
        {
            var s = line.Segment;
            if (!IsCandidate(s)) { line.BikeWhy = Why.NotCandidate; continue; }
            line.BikeWanted = true;
            double km = line.Length / 1000;
            if (line.Write) { stats.Candidates++; stats.CandidateKm += km; }
            if (line.Write && line.Osm is { } row && (IsOsmBike(row.CyclewayLeft) || IsOsmBike(row.CyclewayRight))) stats.OsmKm += km;
            int urbanPoints = line.Plan.Count(p => field.Density(p.X, p.Y) >= UrbanField.UrbanAt);
            bool urban = urbanPoints * 2 > line.Plan.Length;

            // only out of town: in a town a footpath or a lane runs beside every street
            if (!urban && HasAlternative(line.Plan, alternatives))
            {
                line.BikeWanted = false;
                line.BikeWhy = Why.Parallel;
                if (line.Write) { stats.Parallel++; stats.ParallelKm += km; }
                continue;
            }

            bool oneWay = line.OneWay != 0 || (s.Flags & RoadFlags.Divided) != 0;
            (line.BikeLaneDm, line.BikeWhy) = LaneFor(line.Width, oneWay, urban);
            if (!line.Write) continue;
            switch (line.BikeWhy)
            {
                case Why.Lane: stats.LaneKm += km; break;
                case Why.Kern: stats.KernKm += km; break;
                default: stats.NarrowKm += km; break;
            }
        }
    }

    /// <summary>
    /// The bike lane a carriageway of this width holds: full lanes (<see cref="LaneWidth"/>) with
    /// car lanes of at least <see cref="MinCarLane"/> beside them (a centre line stays), else a
    /// Kernfahrbahn whose lanes take what the <see cref="MinCore"/> leaves, down to
    /// <see cref="MinLaneWidth"/>, else none. A one-way road: one lane on the right of travel and
    /// one car lane of <see cref="MinOneWayLane"/>.
    /// </summary>
    public static (byte Dm, Why Why) LaneFor(double width, bool oneWay, bool urban)
    {
        double full = LaneWidth(urban);
        if (oneWay)
        {
            double lane = Math.Min(full, width - MinOneWayLane);
            return lane >= MinLaneWidth ? (Dm(lane), Why.Lane) : ((byte)0, Why.Narrow);
        }
        if (width - 2 * full >= 2 * MinCarLane) return (Dm(full), Why.Lane);
        double kern = Math.Min(full, (width - MinCore) * 0.5);
        return kern >= MinLaneWidth - 1e-9 ? (Dm(kern), Why.Kern) : ((byte)0, Why.Narrow);
    }

    /// <summary>Whole decimetres, half up: a 1.25 m lane is stored 1.3 m, never under the minimum.</summary>
    private static byte Dm(double m) => (byte)Math.Round(m * 10 + 1e-9, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whether a two-way road with these lanes is a Kernfahrbahn: the core between its bike lanes
    /// is too narrow for two car lanes, so it has no centre line.
    /// </summary>
    public static bool IsKernfahrbahn(float width, RoadSide left, RoadSide right) =>
        left.HasLane && right.HasLane && width - (left.BikeDm + right.BikeDm) / 10.0 < 2 * MinCarLane;

    /// <summary>
    /// The line's lanes on its sides, unless a side already has bike infrastructure (OSM). Left
    /// and right are in drawing order: a one-way line has its lane on the right of travel.
    /// </summary>
    public static RoadAttributes Apply(RoadAttributes a, CrossSectionPlanner.Line line)
    {
        if (line.BikeLaneDm == 0) return a;
        bool divided = (line.Segment.Flags & RoadFlags.Divided) != 0;
        int oneWay = a.OneWay;
        bool right = oneWay >= 0, left = oneWay <= 0;
        if (divided && oneWay == 0) left = false;   // a carriageway of unknown direction: its own right only
        RoadSide Lane(RoadSide s) => s.Bike == BikeKind.None ? s with { Bike = BikeKind.Lane, BikeDm = line.BikeLaneDm } : s;
        return a with { Left = left ? Lane(a.Left) : a.Left, Right = right ? Lane(a.Right) : a.Right };
    }

    private static bool IsOsmBike(string value) => value is "lane" or "opposite_lane" or "track" or "opposite_track";

    private static bool HasAlternative(Vec2[] plan, Grid alternatives)
    {
        int stations = 0, covered = 0;
        double next = 0, travelled = 0;
        for (int i = 1; i < plan.Length; i++)
        {
            var a = plan[i - 1];
            var d = plan[i] - a;
            double len = d.Length;
            if (len < 1e-6) continue;
            var u = d / len;
            for (; next < travelled + len; next += ParallelStep)
            {
                stations++;
                if (alternatives.Near(a + u * (next - travelled), u)) covered++;
            }
            travelled += len;
        }
        return stations > 0 && covered >= ParallelShare * stations;
    }

    /// <summary>The alternative lines' pieces in 50 m buckets.</summary>
    private sealed class Grid
    {
        private const double Cell = 50;
        private readonly Dictionary<(int, int), List<(Vec2 A, Vec2 B)>> _cells = new();

        public void Add(Vec2[] line)
        {
            for (int i = 1; i < line.Length; i++)
            {
                var (a, b) = (line[i - 1], line[i]);
                int c0 = (int)Math.Floor(Math.Min(a.X, b.X) / Cell), c1 = (int)Math.Floor(Math.Max(a.X, b.X) / Cell);
                int r0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / Cell), r1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / Cell);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        if (!_cells.TryGetValue((c, r), out var list)) _cells[(c, r)] = list = new();
                        list.Add((a, b));
                    }
            }
        }

        /// <summary>A piece within reach of p running within the angle of u (either way round).</summary>
        public bool Near(Vec2 p, Vec2 u)
        {
            int cx = (int)Math.Floor(p.X / Cell), cy = (int)Math.Floor(p.Y / Cell);
            for (int r = cy - 1; r <= cy + 1; r++)
                for (int c = cx - 1; c <= cx + 1; c++)
                {
                    if (!_cells.TryGetValue((c, r), out var list)) continue;
                    foreach (var (a, b) in list)
                    {
                        var d = b - a;
                        double len = d.Length;
                        if (len < 1e-6 || Math.Abs(d.Dot(u)) / len < ParallelCos) continue;
                        double t = Math.Clamp((p - a).Dot(d) / (len * len), 0, 1);
                        if ((a + d * t).DistanceTo(p) <= ParallelReach) return true;
                    }
                }
            return false;
        }
    }

    // ---- path layouts per street ---------------------------------------------------------

    /// <summary>
    /// The path layout (1..5) of every link that may carry a path, the same along a street:
    /// links are chained through each node with the straightest partner (turning less than 30
    /// degrees, best first), and a chain takes the layout hashed from its smallest key (TLM uuid
    /// and part; the first point where there is none). Deterministic, and two blocks seeing the
    /// same street agree as long as both see that key.
    /// </summary>
    public static Dictionary<int, int> StrokeLayouts(RoadNetwork net, Func<RoadLink, string?> keyOf)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var keys = new Dictionary<int, string>();
        foreach (var link in net.Links)
            if (keyOf(link) is { } key) { parent[link.Id] = link.Id; keys[link.Id] = key; }

        foreach (var node in net.Nodes)
        {
            var arms = node.Approaches.Where(a => parent.ContainsKey(a.LinkId)).ToList();
            if (arms.Count < 2) continue;
            var pairs = new List<(double Straight, int A, int B)>();
            for (int i = 0; i < arms.Count; i++)
                for (int k = i + 1; k < arms.Count; k++)
                {
                    if (arms[i].LinkId == arms[k].LinkId) continue;
                    double straight = -Math.Cos(arms[i].OutwardHeading - arms[k].OutwardHeading);
                    if (straight >= ParallelCosStroke) pairs.Add((straight, i, k));
                }
            var used = new HashSet<int>();
            foreach (var (_, a, b) in pairs.OrderByDescending(p => p.Straight)
                         .ThenBy(p => Math.Min(arms[p.A].LinkId, arms[p.B].LinkId)).ThenBy(p => Math.Max(arms[p.A].LinkId, arms[p.B].LinkId)))
            {
                if (!used.Add(a)) continue;
                if (!used.Add(b)) { used.Remove(a); continue; }
                int ra = Find(arms[a].LinkId), rb = Find(arms[b].LinkId);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        var smallest = new Dictionary<int, string>();
        foreach (var (id, key) in keys)
        {
            int root = Find(id);
            if (!smallest.TryGetValue(root, out var s) || string.CompareOrdinal(key, s) < 0) smallest[root] = key;
        }
        var result = new Dictionary<int, int>();
        foreach (var id in keys.Keys) result[id] = 1 + (int)(Fnv(smallest[Find(id)]) % 5);
        return result;
    }

    /// <summary>A street carries on through a node where it turns less than 30 degrees.</summary>
    private const double ParallelCosStroke = 0.866;

    internal static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char ch in s) { h ^= ch; h *= 16777619; }
        return h;
    }
}
