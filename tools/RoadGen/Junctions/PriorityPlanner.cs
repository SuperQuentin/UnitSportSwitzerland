namespace UnitSport.Tools.RoadGen.Junctions;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Junction priority (#121): per junction, which road carries straight through and which arms
/// give way, and the Swiss yield treatment of those arms: the Wartelinie (6.13, a row of white
/// triangles) across their approach lanes at the main road's edge, and the "Kein Vortritt"
/// signal (3.02) at their right edge, plus "Hauptstrasse" (3.03) on the main road. Plan view
/// only: the network stage turns the result into paint, props and ATTR yield bits.
///
/// <para>
/// Rules (SSV SR 741.21, SVG Art. 36; values and sources in docs/notes/tools/junction-priority.md):
/// arms rank by TLM <c>verkehrsbedeutung</c> (or OSM highway), then owner (canton/federal), then
/// width class; the pair of arms ranking highest, the straighter pair on a tie, is the main road,
/// and only when every other car arm ranks strictly below it. The main road must be a priority
/// road (importance, a cantonal road, or 6 m class and up); otherwise the junction is
/// right-before-left and gets nothing. Field tracks, paths and squares never have priority
/// (VRV Art. 15 al. 3) and get no treatment either. A roundabout ring is the main road of every
/// node on it. Motorway merges and diverges are not junctions in this sense (acceleration lanes,
/// no Wartelinie, SSV Art. 75 al. 4).
/// </para>
/// </summary>
public static class PriorityPlanner
{
    /// <summary>What the planner needs to know about a link; null for links that are not roads (rails).</summary>
    public readonly record struct LinkInfo(RoadClass Class, RoadSurface Surface, RoadFlags Flags, RoadAttributes Attributes, float Width);

    /// <summary><see cref="Signal"/>: traffic lights (#348); the main road and the yielding arms are still chosen, for the signs that rule when the lights are off.</summary>
    public enum Kind : byte { NotRoad, HighSpeed, Minor, RightBeforeLeft, Unresolved, Main, Roundabout, Signal }

    public enum Role : byte { None, Main, Yield }

    public readonly record struct ArmPlan(int LinkId, LinkEnd End, Role Role, bool Approach);

    /// <summary>Wartelinie: teeth along From→To, apexes on the right of that direction (toward the approaching driver).</summary>
    public readonly record struct TeethRow(int LinkId, Vec2 From, Vec2 To);

    /// <summary>A sign candidate: its pole foot, facing the traffic it is for, and where on the carriageway its height comes from.</summary>
    public readonly record struct SignSpot(int LinkId, PointPropType Type, byte Variant, Vec2 At, Vec2 Facing, Vec2 RoadPoint, bool OnSidewalk);

    public sealed class Plan
    {
        public Kind Kind;
        public readonly List<ArmPlan> Arms = new();
        public readonly List<TeethRow> Teeth = new();
        public readonly List<SignSpot> Signs = new();
        /// <summary>The main road's centre line carried across the junction (dashed), urban dash or not.</summary>
        public List<Vec2>? CentreLine;
        public bool CentreUrban;
        /// <summary>
        /// The main road's edges carried through the junction: dashed guide lines (Führungslinien,
        /// SSV 6.16) on a side where a road joins, solid on a side where none does.
        /// </summary>
        public readonly List<(List<Vec2> Line, bool Dashed)> Guides = new();
    }

    /// <summary>The Wartelinie lies this far outside the main carriageway edge (BE handbook p. 41: 25 cm from the longitudinal line).</summary>
    private const double TeethSetback = 0.25;

    /// <summary>Teeth stop this short of the arm's edge and of its centre line.</summary>
    private const double TeethMargin = 0.15;

    /// <summary>The 3.02 pole stands this far behind the Wartelinie's row base ("kurz vor", SSV Art. 36 al. 4; #121: ~2 m).</summary>
    private const double SignBehindTeeth = 2.0;

    /// <summary>3.03 "kurz vor" (inside localities) / "kurz nach" (outside) the junction (SSV Art. 37 al. 2): metres past the trim.</summary>
    private const double MainSignPastTrim = 3.0;

    public static bool IsCarRoad(RoadClass c) => c is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp
        or RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane;

    /// <summary>
    /// Rank of an arm: importance, owner, width class. A ramp ranks under every road: its end on
    /// an ordinary road gives way (or is a roundabout entry, #122).
    /// </summary>
    public static int Rank(LinkInfo info)
    {
        if (info.Class == RoadClass.Ramp) return 0;
        var a = info.Attributes;
        int importance = a.Priority >> 4;
        int owner = a.Has(RoadAttrFlags.OwnerFederal) ? 2 : a.Has(RoadAttrFlags.OwnerCanton) ? 1 : 0;
        int cls = 12 - (int)info.Class;
        return 1 + importance * 64 + owner * 16 + cls;
    }

    /// <summary>A road that may carry 3.03: of some importance, cantonal, or 6 m class and up.</summary>
    public static bool IsPriorityRoad(LinkInfo info) =>
        (info.Attributes.Priority >> 4) > 0
        || info.Attributes.Has(RoadAttrFlags.OwnerCanton) || info.Attributes.Has(RoadAttrFlags.OwnerFederal)
        || info.Class is RoadClass.Major or RoadClass.Road;

    public static LinkEnd EndAt(RoadNetwork net, Junction j, JunctionArm arm)
    {
        var link = net.Links[arm.LinkId];
        bool start = link.StartNode == j.NodeId, end = link.EndNode == j.NodeId;
        if (start != end) return start ? LinkEnd.Start : LinkEnd.End;
        // a loop back to its own node: the end whose alignment lies nearer this arm's mouth
        var mouth = (arm.Left + arm.Right) * 0.5;
        if (link.Alignment is not { IsEmpty: false } al) return LinkEnd.Start;
        return al.PointAt(0).DistanceTo(mouth) <= al.PointAt(al.Length).DistanceTo(mouth) ? LinkEnd.Start : LinkEnd.End;
    }

    /// <summary>
    /// UrbanField density from which a crossing of two main roads gets traffic lights when no
    /// data says where they are (#348): a dense core, stricter than a town's 0.4 (tuned on Sion
    /// and Geneva, docs/notes/tools/traffic-signals.md).
    /// </summary>
    public const double SignalDensity = 0.6;

    /// <summary>
    /// Whether a junction gets traffic lights by inference (#348): at least four car arms, at
    /// grade, paved, no roundabout or motorway class, and two roads crossing that are both
    /// priority roads (each a pair of about opposite arms that may carry 3.03), in a dense core.
    /// </summary>
    public static bool InferSignal(Junction j, RoadNetwork net, Func<RoadLink, LinkInfo?> infoOf, double density)
    {
        if (density < SignalDensity) return false;
        var car = new List<(JunctionArm Arm, LinkInfo Info)>();
        foreach (var arm in j.Arms)
        {
            if (infoOf(net.Links[arm.LinkId]) is not { } info || !IsCarRoad(info.Class) || (info.Flags & RoadFlags.Stairs) != 0) continue;
            if (info.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp || info.Attributes.Has(RoadAttrFlags.Roundabout)
                || info.Surface != RoadSurface.Paved || (info.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) return false;
            car.Add((arm, info));
        }
        if (car.Count < 4) return false;
        int roads = 0;
        var used = new bool[car.Count];
        for (int a = 0; a < car.Count; a++)
        {
            if (used[a] || !IsPriorityRoad(car[a].Info)) continue;
            for (int b = a + 1; b < car.Count; b++)
            {
                if (used[b] || !IsPriorityRoad(car[b].Info)) continue;
                if (-Math.Cos(car[a].Arm.OutwardHeading - car[b].Arm.OutwardHeading) < Math.Cos(40 * Math.PI / 180)) continue;
                used[a] = used[b] = true;
                roads++;
                break;
            }
        }
        return roads >= 2;
    }

    public static Plan Decide(Junction j, RoadNetwork net, Func<RoadLink, LinkInfo?> infoOf, bool signal = false)
    {
        var plan = new Plan();
        int n = j.Arms.Count;
        var infos = new LinkInfo?[n];
        var ends = new LinkEnd[n];
        var approach = new bool[n];
        var car = new List<int>();
        for (int i = 0; i < n; i++)
        {
            infos[i] = infoOf(net.Links[j.Arms[i].LinkId]);
            ends[i] = EndAt(net, j, j.Arms[i]);
            if (infos[i] is not { } info) continue;
            int oneWay = info.Attributes.OneWay;
            approach[i] = ends[i] == LinkEnd.End ? oneWay >= 0 : oneWay <= 0;
            if (IsCarRoad(info.Class) && (info.Flags & RoadFlags.Stairs) == 0) car.Add(i);
        }

        var roles = new Role[n];
        void Finish()
        {
            for (int i = 0; i < n; i++) plan.Arms.Add(new ArmPlan(j.Arms[i].LinkId, ends[i], roles[i], approach[i]));
        }

        if (car.Count == 0) { plan.Kind = Kind.NotRoad; Finish(); return plan; }
        if (car.Any(i => infos[i]!.Value.Class is RoadClass.Motorway or RoadClass.Expressway)
            || car.All(i => infos[i]!.Value.Class == RoadClass.Ramp))
        { plan.Kind = Kind.HighSpeed; Finish(); return plan; }
        if (car.Count < 2) { plan.Kind = Kind.Minor; Finish(); return plan; }

        var ring = car.Where(i => infos[i]!.Value.Attributes.Has(RoadAttrFlags.Roundabout)).ToList();
        if (ring.Count >= 2)
        {
            plan.Kind = Kind.Roundabout;
            foreach (int i in car) roles[i] = ring.Contains(i) ? Role.Main : Role.Yield;
        }
        else
        {
            // the best pair: highest lower rank, then highest upper rank, then straightest, then widest
            int bestA = -1, bestB = -1;
            (int, int, double, double) best = (-1, -1, 0, 0);
            for (int x = 0; x < car.Count; x++)
            for (int y = x + 1; y < car.Count; y++)
            {
                int a = car[x], b = car[y];
                int ra = Rank(infos[a]!.Value), rb = Rank(infos[b]!.Value);
                double straight = -Math.Cos(j.Arms[a].OutwardHeading - j.Arms[b].OutwardHeading);   // 1 = opposite
                var key = (Math.Min(ra, rb), Math.Max(ra, rb), Math.Round(straight, 6),
                    (double)(infos[a]!.Value.Width + infos[b]!.Value.Width));
                if (bestA < 0 || key.CompareTo(best) > 0) { best = key; bestA = a; bestB = b; }
            }

            int lower = best.Item1;
            bool strict = car.All(i => i == bestA || i == bestB || Rank(infos[i]!.Value) < lower);
            bool priority = IsPriorityRoad(infos[bestA]!.Value) && IsPriorityRoad(infos[bestB]!.Value);
            // with lights the best pair is the main road for the signs even on equal ranks (#348)
            if (!signal && !priority) { plan.Kind = Kind.RightBeforeLeft; Finish(); return plan; }
            if (!signal && !strict) { plan.Kind = Kind.Unresolved; Finish(); return plan; }

            plan.Kind = signal ? Kind.Signal : Kind.Main;
            foreach (int i in car) roles[i] = i == bestA || i == bestB ? Role.Main : Role.Yield;
        }
        Finish();

        int[] main = Enumerable.Range(0, n).Where(i => roles[i] == Role.Main).ToArray();
        // the main carriageway as seen from a side arm: its edge is this far from the node, along the arm
        double mainHalf = main.Max(i => j.Arms[i].HalfWidth);
        Vec2 mainDir = main.Length >= 2
            ? (Vec2.FromHeading(j.Arms[main[1]].OutwardHeading) - Vec2.FromHeading(j.Arms[main[0]].OutwardHeading)).Normalized()
            : Vec2.FromHeading(j.Arms[main[0]].OutwardHeading).Perp;

        bool smallSideRoadsOnly = true;
        for (int i = 0; i < n; i++)
        {
            if (roles[i] != Role.Yield || !approach[i]) continue;
            var info = infos[i]!.Value;
            var arm = j.Arms[i];
            if (info.Class <= RoadClass.Minor) smallSideRoadsOnly = false;
            var u = Vec2.FromHeading(arm.OutwardHeading);
            var right = u.Perp;   // the approaching driver's right (they drive along -u)
            double sin = Math.Abs(u.Cross(mainDir));
            double edge = sin > 0.3 ? mainHalf / sin : arm.Trim;
            // never inside the main carriageway, never further out than just past the arm's mouth
            double baseAt = Math.Min(Math.Max(edge, mainHalf) + TeethSetback, arm.Trim + 1.0);
            var centre = j.Centre + u * baseAt;
            double h = arm.HalfWidth;

            // SSV Art. 75 al. 4: not on roads without a hard surface; a signalised arm has a stop line instead (#348)
            if (info.Surface == RoadSurface.Paved && plan.Kind != Kind.Signal)
            {
                bool all = info.Attributes.OneWay != 0;   // a one-way approach: every lane approaches
                double from = all ? -h + TeethMargin : TeethMargin, to = h - TeethMargin;
                // teeth along +right (= u.Perp) put their apexes on the right of the row: +u, outward
                if (to - from >= RoadSigns.ToothBase)
                    plan.Teeth.Add(Centred(arm.LinkId, centre + right * from, centre + right * to));
            }

            bool urban = info.Attributes.Has(RoadAttrFlags.Urban);
            byte variant = urban && info.Class >= RoadClass.Minor ? RoadSigns.Small : RoadSigns.Normal;
            double side = RoadSigns.Side(PointPropType.YieldSign, variant);
            double lateral = h + RoadSigns.Clearance(urban) + side * 0.5;
            var road = centre + u * (SignBehindTeeth + RoadSigns.ToothHeight);
            var sidewalk = ends[i] == LinkEnd.End ? info.Attributes.Right : info.Attributes.Left;   // +Perp(u) in drawing terms
            plan.Signs.Add(new SignSpot(arm.LinkId, PointPropType.YieldSign, variant, road + right * lateral, u,
                road + right * h, sidewalk.OuterDm / 10.0 > lateral - h));
        }

        // 3.03 on the main road: inside localities just before the junction, outside just after
        // (SSV Art. 37 al. 2); left out where only 3 m lanes join ("kann weggelassen werden")
        if (plan.Kind is Kind.Main or Kind.Signal && !smallSideRoadsOnly)
        {
            foreach (int i in main)
            {
                var info = infos[i]!.Value;
                var arm = j.Arms[i];
                bool urban = info.Attributes.Has(RoadAttrFlags.Urban);
                var u = Vec2.FromHeading(arm.OutwardHeading);
                // before: for traffic approaching along this arm, on its right (+Perp);
                // after: for traffic leaving along it, on its right (-Perp)
                if (urban ? !approach[i] : !Leaves(info, ends[i])) continue;
                var right = urban ? u.Perp : -u.Perp;
                byte variant = RoadSigns.Normal;
                double lateral = arm.HalfWidth + RoadSigns.Clearance(urban) + RoadSigns.Side(PointPropType.MainRoadSign, variant) * 0.5;
                var road = j.Centre + u * (arm.Trim + MainSignPastTrim);
                var sidewalk = (ends[i] == LinkEnd.End) == urban ? info.Attributes.Right : info.Attributes.Left;
                plan.Signs.Add(new SignSpot(arm.LinkId, PointPropType.MainRoadSign, variant, road + right * lateral,
                    urban ? u : -u, road + right * arm.HalfWidth, sidewalk.OuterDm / 10.0 > lateral - arm.HalfWidth));
            }
        }

        if (plan.Kind == Kind.Main) CentreLine(plan, j, main, infos);
        return plan;
    }

    /// <summary>Whether traffic may leave the junction along an arm (not a one-way road in).</summary>
    public static bool Leaves(LinkInfo info, LinkEnd end) =>
        end == LinkEnd.Start ? info.Attributes.OneWay >= 0 : info.Attributes.OneWay <= 0;

    /// <summary>The whole teeth that fit between two points, centred: the row as stored starts on a tooth.</summary>
    private static TeethRow Centred(int link, Vec2 from, Vec2 to)
    {
        double length = from.DistanceTo(to), period = RoadSigns.ToothBase + RoadSigns.ToothGap;
        int count = (int)Math.Floor((length + RoadSigns.ToothGap) / period);
        double used = count * period - RoadSigns.ToothGap;
        var dir = (to - from) / length;
        var start = from + dir * ((length - used) * 0.5);
        return new TeethRow(link, start, start + dir * used);
    }

    /// <summary>
    /// The main road's Leitlinie carried through the junction between its two arms' ends, when
    /// both are two-way, paved and wide enough to have one (the paint rules of PaintEmitter), and
    /// the road does not turn sharply there.
    /// </summary>
    private static void CentreLine(Plan plan, Junction j, int[] main, LinkInfo?[] infos)
    {
        if (main.Length != 2) return;
        foreach (int i in main)
        {
            var info = infos[i]!.Value;
            bool urban = info.Attributes.Has(RoadAttrFlags.Urban);
            if (info.Attributes.OneWay != 0 || (info.Flags & RoadFlags.Divided) != 0 || info.Surface != RoadSurface.Paved
                || info.Class > RoadClass.Minor || info.Class < RoadClass.Major
                || info.Width < Meshing.PaintEmitter.MinCentreLineWidth(urban)) return;
        }
        var a = j.Arms[main[0]];
        var b = j.Arms[main[1]];
        if (-Math.Cos(a.OutwardHeading - b.OutwardHeading) < 0.5) return;   // turns more than 60 degrees
        Vec2 from = (a.Left + a.Right) * 0.5, to = (b.Left + b.Right) * 0.5;
        var line = new List<Vec2>();
        const int Samples = 8;
        for (int s = 0; s <= Samples; s++)
        {
            double t = (double)s / Samples, mt = 1 - t;
            line.Add(from * (mt * mt) + j.Centre * (2 * mt * t) + to * (t * t));
        }
        plan.CentreLine = Polyline.Simplify(line, 0.02);   // mostly straight: 2 points instead of 9
        plan.CentreUrban = infos[main[0]]!.Value.Attributes.Has(RoadAttrFlags.Urban);

        // the edges: a's corner on one side meets b's corner on the same side of the road, which
        // looking outward along b is its other hand; inset like an edge line (Randlinie)
        var ua = Vec2.FromHeading(a.OutwardHeading);
        foreach (var (ca, cb) in (ReadOnlySpan<(Vec2, Vec2)>)[(a.Left, b.Right), (a.Right, b.Left)])
        {
            var pa = ca + (from - ca).Normalized() * Meshing.PaintEmitter.EdgeLineInset;
            var pb = cb + (to - cb).Normalized() * Meshing.PaintEmitter.EdgeLineInset;
            var control = j.Centre + ((pa - from) + (pb - to)) * 0.5;
            var edge = new List<Vec2>();
            for (int k = 0; k <= Samples; k++)
            {
                double t = (double)k / Samples, mt = 1 - t;
                edge.Add(pa * (mt * mt) + control * (2 * mt * t) + pb * (t * t));
            }
            // a road joining on this side: which side of the main road its arm leaves on
            double side = Cross(ua, ca - from);
            bool joined = false;
            for (int k = 0; k < j.Arms.Count; k++)
                if (k != main[0] && k != main[1] && Math.Sign(Cross(ua, Vec2.FromHeading(j.Arms[k].OutwardHeading))) == Math.Sign(side))
                    joined = true;
            plan.Guides.Add((Polyline.Simplify(edge, 0.02), joined));
        }
    }

    private static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    // ------------------------------------------------------------------ sign sites

    /// <summary>
    /// Where a sign may not stand: on any carriageway, path or track of the block, or inside a
    /// junction. A coarse grid over the ribbons' centre stations and the junction rings.
    /// </summary>
    public sealed class Clearance
    {
        private const double Cell = 20;
        private readonly Dictionary<(long, long), List<(Vec2 A, Vec2 B, double Half)>> _lines = new();
        private readonly Dictionary<(long, long), List<List<Vec2>>> _rings = new();

        public Clearance(IEnumerable<Meshing.Ribbon> ribbons, IEnumerable<Junction> junctions)
        {
            foreach (var r in ribbons)
                for (int i = 1; i < r.Stations.Count; i++)
                {
                    var a = r.Stations[i - 1].Position;
                    var b = r.Stations[i].Position;
                    foreach (var key in Cells(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), r.Profile.HalfWidth))
                        Get(_lines, key).Add((a, b, r.Profile.HalfWidth));
                }
            foreach (var j in junctions)
            {
                if (j.Boundary.Count < 3) continue;
                foreach (var key in Cells(j.Boundary.Min(p => p.X), j.Boundary.Min(p => p.Y),
                             j.Boundary.Max(p => p.X), j.Boundary.Max(p => p.Y), 0))
                    Get(_rings, key).Add(j.Boundary);
            }
        }

        /// <summary>True when a point (with <paramref name="margin"/> round it) is clear of every surface.</summary>
        public bool IsClear(Vec2 p, double margin)
        {
            var key = ((long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell));
            if (_lines.TryGetValue(key, out var lines))
                foreach (var (a, b, half) in lines)
                    if (DistanceToSegment(p, a, b) < half + margin) return false;
            if (_rings.TryGetValue(key, out var rings))
                foreach (var ring in rings)
                    if (Inside(ring, p)) return false;
            return true;
        }

        private static IEnumerable<(long, long)> Cells(double minX, double minY, double maxX, double maxY, double pad)
        {
            for (long x = (long)Math.Floor((minX - pad - 2) / Cell); x <= (long)Math.Floor((maxX + pad + 2) / Cell); x++)
            for (long y = (long)Math.Floor((minY - pad - 2) / Cell); y <= (long)Math.Floor((maxY + pad + 2) / Cell); y++)
                yield return (x, y);
        }

        private static List<T> Get<T>(Dictionary<(long, long), List<T>> d, (long, long) k)
        {
            if (!d.TryGetValue(k, out var l)) d[k] = l = new List<T>();
            return l;
        }
    }

    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len = ab.LengthSquared;
        double t = len < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / len, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    public static bool Inside(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, k = ring.Count - 1; i < ring.Count; k = i++)
        {
            var a = ring[i];
            var b = ring[k];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    // ------------------------------------------------------------------ self-check

    /// <summary><c>RoadGen --priority-check</c>: synthetic junctions through the real pipeline.</summary>
    public static bool SelfCheck(Action<string> log)
    {
        bool ok = true;
        void Check(bool condition, string what)
        {
            log($"  {(condition ? "ok  " : "FAIL")} {what}");
            ok &= condition;
        }

        static LinkInfo Info(RoadClass c, sbyte oneWay = 0, RoadSurface surface = RoadSurface.Paved) =>
            new(c, surface, 0, new RoadAttributes(OneWay: oneWay, Priority: RoadFormat.PriorityFor(c, null)), (float)Profile(c).Width);
        static RoadProfile Profile(RoadClass c) => c switch
        {
            RoadClass.Motorway => RoadProfile.Motorway, RoadClass.Ramp => RoadProfile.Ramp, RoadClass.Major => RoadProfile.Major,
            RoadClass.Road => RoadProfile.Road, RoadClass.Minor => RoadProfile.Minor, RoadClass.Track => RoadProfile.Track,
            _ => RoadProfile.Lane,
        };
        static (RoadGenResult Result, List<Plan> Plans) Run(params (Vec2 A, Vec2 B, LinkInfo Info)[] lines)
        {
            var net = new RoadNetwork();
            foreach (var (a, b, info) in lines) net.AddLink([a, b], Profile(info.Class), tag: info);
            var result = Pipeline.Run(net, new PipelineOptions(Analyze: false, JoinNearEnds: (_, _) => true));
            var plans = result.Junctions.Select(j => Decide(j, result.Network, l => l.Tag is LinkInfo i ? i : null)).ToList();
            return (result, plans);
        }
        Vec2 V(double x, double y) => new(x, y);

        // T: a 6 m road through, a 4 m road joining from the south
        var (t, tPlans) = Run((V(-100, 0), V(0, 0), Info(RoadClass.Road)), (V(0, 0), V(100, 0), Info(RoadClass.Road)),
            (V(0, -80), V(0, 0), Info(RoadClass.Minor)));
        Check(tPlans.Count == 1 && tPlans[0].Kind == Kind.Main, "T: one junction with a main road");
        if (tPlans.Count == 1)
        {
            var p = tPlans[0];
            var j = t.Junctions[0];
            var side = j.Arms.Single(a => t.Network.Links[a.LinkId].Profile == RoadProfile.Minor);
            var u = Vec2.FromHeading(side.OutwardHeading);
            Check(p.Arms.Count(a => a.Role == Role.Yield) == 1 && p.Arms.Single(a => a.Role == Role.Yield).LinkId == side.LinkId, "T: the side road yields");
            Check(p.Teeth.Count == 1, "T: one Wartelinie");
            if (p.Teeth.Count == 1)
            {
                var row = p.Teeth[0];
                var dir = (row.To - row.From).Normalized();
                Check((-dir.Perp).Dot(u) > 0.99, "T: teeth apexes point at the approaching driver");
                Check(((row.From + row.To) * 0.5 - j.Centre).Dot(u.Perp) > 0, "T: teeth on the approach half (driver's right)");
                double at = ((row.From + row.To) * 0.5 - j.Centre).Dot(u);
                Check(at >= RoadProfile.Road.HalfWidth && at < side.Trim + 1.01, $"T: teeth at the main road's edge ({at:F2} m out)");
                int count = (int)Math.Round((row.From.DistanceTo(row.To) + RoadSigns.ToothGap) / (RoadSigns.ToothBase + RoadSigns.ToothGap));
                Check(count == 2, $"T: 2 teeth across a 2 m approach lane ({count})");
                // tile-local (x east, z south): the runtime's apex must land on the same side
                var paint = new RoadPaint
                {
                    Shape = PaintShape.Polyline, Type = PaintType.SharkTooth, Width = RoadSigns.ToothHeight,
                    Dash = RoadSigns.ToothBase, Gap = RoadSigns.ToothGap,
                    Vertices = [(float)row.From.X, 0, (float)-row.From.Y, (float)row.To.X, 0, (float)-row.To.Y],
                };
                var teeth = RoadPaintGeometry.Teeth(paint);
                var apexOut = teeth.Count > 0 ? (teeth[0][6] - (teeth[0][0] + teeth[0][3]) / 2) * u.X - (teeth[0][8] - (teeth[0][2] + teeth[0][5]) / 2) * u.Y : 0;
                Check(teeth.Count == count && apexOut > 0.59, "T: runtime teeth point outward in tile-local space");
            }
            var yieldSign = p.Signs.Where(s => s.Type == PointPropType.YieldSign).ToList();
            Check(yieldSign.Count == 1, "T: one 3.02 sign");
            if (yieldSign.Count == 1)
            {
                var s = yieldSign[0];
                double lateral = (s.At - j.Centre).Dot(u.Perp);
                Check(lateral > RoadProfile.Minor.HalfWidth + 0.5, $"T: 3.02 off the carriageway on the right ({lateral:F2} m)");
                Check(s.Facing.Dot(u) > 0.99, "T: 3.02 faces the approaching driver");
            }
            Check(p.Signs.Count(s => s.Type == PointPropType.MainRoadSign) == 2, "T: 3.03 on the main road, once per direction");
            // straight through, the line simplifies to its two ends
            Check(p.CentreLine is { Count: >= 2 } cl && cl[0].DistanceTo(cl[^1]) > RoadProfile.Minor.Width,
                "T: the main road's centre line carries on across the mouth");
            var clear = new Clearance(t.Ribbons, t.Junctions);
            Check(p.Signs.All(s => clear.IsClear(s.At, 0.25)), "T: signs stand clear of every carriageway");
            Check(!clear.IsClear(j.Centre, 0) && !clear.IsClear(V(50, 0.5), 0), "T: clearance sees the junction and the road");
        }

        // crossroads of equal 4 m roads: right before left, nothing
        var (_, eq) = Run((V(-80, 0), V(0, 0), Info(RoadClass.Minor)), (V(0, 0), V(80, 0), Info(RoadClass.Minor)),
            (V(0, -80), V(0, 0), Info(RoadClass.Minor)), (V(0, 0), V(0, 80), Info(RoadClass.Minor)));
        Check(eq.Count == 1 && eq[0].Kind == Kind.RightBeforeLeft && eq[0].Teeth.Count == 0 && eq[0].Signs.Count == 0,
            "crossroads of equal minor roads: right before left, unmarked");

        // a field track onto a road: no priority from it, no treatment (VRV Art. 15)
        var (_, track) = Run((V(-80, 0), V(0, 0), Info(RoadClass.Road)), (V(0, 0), V(80, 0), Info(RoadClass.Road)),
            (V(0, -80), V(0, 0), Info(RoadClass.Track, surface: RoadSurface.Natural)));
        Check(track.Count == 1 && track[0].Teeth.Count == 0 && track[0].Signs.Count == 0, "track onto a road: unmarked");

        // an unpaved car road: the sign, but no Wartelinie (SSV Art. 75 al. 4)
        var (_, gravel) = Run((V(-80, 0), V(0, 0), Info(RoadClass.Road)), (V(0, 0), V(80, 0), Info(RoadClass.Road)),
            (V(0, -80), V(0, 0), Info(RoadClass.Lane, surface: RoadSurface.Natural)));
        Check(gravel.Count == 1 && gravel[0].Teeth.Count == 0 && gravel[0].Signs.Count(s => s.Type == PointPropType.YieldSign) == 1,
            "unpaved side road: 3.02 without teeth");

        // a one-way side road leaving the junction: nobody approaches on it
        var (_, away) = Run((V(-80, 0), V(0, 0), Info(RoadClass.Road)), (V(0, 0), V(80, 0), Info(RoadClass.Road)),
            (V(0, 0), V(0, -80), Info(RoadClass.Minor, oneWay: 1)));
        Check(away.Count == 1 && away[0].Teeth.Count == 0 && away[0].Signs.All(s => s.Type != PointPropType.YieldSign),
            "one-way side road leaving: no yield treatment");

        // motorway diverge: not a junction for priority
        var (_, mw) = Run((V(-200, 0), V(0, 0), Info(RoadClass.Motorway, 1)), (V(0, 0), V(200, 0), Info(RoadClass.Motorway, 1)),
            (V(0, 0), V(150, -30), Info(RoadClass.Ramp, 1)));
        Check(mw.All(p => p.Kind == Kind.HighSpeed && p.Teeth.Count == 0), "motorway diverge: no yield treatment");

        // a side road whose TLM line stops 2 m short of the main road's centreline
        var (near, nearPlans) = Run((V(-100, 0), V(0.3, 0), Info(RoadClass.Road)), (V(0.3, 0), V(100, 0), Info(RoadClass.Road)),
            (V(20, -80), V(20, -2), Info(RoadClass.Minor)));
        Check(near.NearEndsJoined == 1 && nearPlans.Any(p => p.Kind == Kind.Main && p.Teeth.Count == 1),
            "a side road ending inside the main road's half width is joined and yields");

        log(ok ? "priority check passed" : "priority check FAILED");
        return ok;
    }

    // ------------------------------------------------------------------ stats

    public sealed class Stats
    {
        public readonly int[] Kinds = new int[Enum.GetValues<Kind>().Length];
        public int MainWithSideRoad, YieldingArms, YieldNoApproach, TeethRows, Teeth, NoTeethUnpaved, CentreLines, Guides, NearEndsJoined;
        public readonly SortedDictionary<string, int> Placed = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Rejected = new(StringComparer.Ordinal);

        public void Reject(PointPropType type, string why) =>
            Rejected[$"{type} {why}"] = Rejected.GetValueOrDefault($"{type} {why}") + 1;

        public void Place(PointPropType type) => Placed[type.ToString()] = Placed.GetValueOrDefault(type.ToString()) + 1;

        public string Format()
        {
            var c = CultureInfo.InvariantCulture;
            int all = Kinds.Sum();
            string Pct(int v) => all == 0 ? "-" : (100.0 * v / all).ToString("F1", c) + "%";
            var sb = new System.Text.StringBuilder();
            sb.Append(c, $"    junctions (#121) {all:N0}: main road {Kinds[(int)Kind.Main]:N0} ({Pct(Kinds[(int)Kind.Main])}), ");
            sb.Append(c, $"with a yielding road {MainWithSideRoad:N0}, traffic lights {Kinds[(int)Kind.Signal]:N0}, roundabout {Kinds[(int)Kind.Roundabout]:N0}, right-before-left {Kinds[(int)Kind.RightBeforeLeft]:N0}, ");
            sb.Append(c, $"equal ranks {Kinds[(int)Kind.Unresolved]:N0}, motorway/ramp {Kinds[(int)Kind.HighSpeed]:N0}, ");
            sb.Append(c, $"one car road {Kinds[(int)Kind.Minor]:N0}, no road {Kinds[(int)Kind.NotRoad]:N0}\n");
            sb.Append(c, $"      yielding arms {YieldingArms:N0} (+{YieldNoApproach:N0} one-way away), Wartelinien {TeethRows:N0} ({Teeth:N0} teeth), unpaved no teeth {NoTeethUnpaved:N0}, main centre lines {CentreLines:N0} and {Guides:N0} edge guide lines, near ends joined {NearEndsJoined:N0}\n");
            sb.Append("      signs placed ");
            sb.Append(string.Join(", ", Placed.Select(kv => $"{kv.Key} {kv.Value:N0}")));
            sb.Append("; rejected ");
            sb.Append(Rejected.Count == 0 ? "none" : string.Join(", ", Rejected.Select(kv => $"{kv.Key} {kv.Value:N0}")));
            return sb.ToString();
        }
    }
}
