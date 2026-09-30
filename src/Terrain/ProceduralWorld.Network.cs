using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

public sealed partial class ProceduralWorld
{
    // ---- the network: rivers, and the roads, railways and villages that follow them ---------
    //
    // Traced once from the relief's drainage and shared by every ProceduralWorld: it is in
    // absolute LV95, the same on every peer, and depends on nothing but the embedded heightmap.
    //
    // A river is every node draining at least RiverMinKm2 that is not in a lake. From each
    // outlet (a lake, or the map's edge) the river is walked upstream, at every confluence
    // carrying on up the branch that drains more — the main stem — and starting a tributary
    // up each other one. So rivers come parents first, and a tributary's first point is the
    // confluence on its parent. Each is smoothed (Chaikin) from the 500 m node path, and its bed
    // is the macro height along it forced to fall all the way to the mouth.

    private const double RiverMinKm2 = 30;
    private const double RoadMinKm2 = 45;
    private const double RailMinKm2 = 500;
    private const double VillageSpacing = 2600;

    /// <summary>A road or railway: a polyline at about 60 m, drawn as a Catmull-Rom through it.</summary>
    private sealed class Line
    {
        public required RoadClass Class;
        public required double[] E, N;
        /// <summary>Arc length from the first point.</summary>
        public required double[] S;
        /// <summary>+1 if the side of the river it runs on is left of its direction, else -1.</summary>
        public required int Side;
        public double MinE, MinN, MaxE, MaxN;
        public int Count => E.Length;
    }

    /// <summary>Where a village stands: a stretch of a valley road, centred <see cref="S"/> along it.</summary>
    private sealed record VillageSlot(int Id, int Line, double S, double HalfLength, double E, double N);

    private sealed class Network
    {
        private static readonly Lazy<Network> Shared = new(() => new Network(Relief.Instance),
            LazyThreadSafetyMode.ExecutionAndPublication);
        public static Network Instance => Shared.Value;

        // river points, all rivers end to end; river r owns [Start[r], Start[r + 1])
        public readonly List<int> Start = new();   // one more than there are rivers
        public readonly List<int> Parent = new();
        public float[] PE = [], PN = [], Bed = [], Floor = [], Reach = [], Half = [], Bank = [], Depth = [];
        public bool[] Water = [];
        public int[] RiverOf = [];

        public readonly List<Line> Lines = new();
        public readonly List<VillageSlot> Villages = new();

        /// <summary>River segments (by first point) whose valley reaches into each 1 km bucket.</summary>
        public readonly Buckets Valleys;
        /// <summary>River segments whose channel reaches into each 1 km bucket.</summary>
        public readonly Buckets Channels;
        /// <summary>Line segments (line, first point) within <see cref="LineReach"/> of each bucket.</summary>
        public readonly Buckets LineSegs;
        public readonly Buckets VillageIndex;

        public const double LineReach = 400;

        public Network(Relief relief)
        {
            double originE = relief.MinE - 5000, originN = relief.MinN - 5000;
            int cols = (int)Math.Ceiling((relief.MaxE - relief.MinE + 10000) / 1000);
            int rows = (int)Math.Ceiling((relief.MaxN - relief.MinN + 10000) / 1000);
            Valleys = new Buckets(originE, originN, 1000, cols, rows);
            Channels = new Buckets(originE, originN, 1000, cols, rows);
            LineSegs = new Buckets(originE, originN, 1000, cols, rows);
            VillageIndex = new Buckets(originE, originN, 1000, cols, rows);

            TraceRivers(relief);
            // children before parents: the valley field applies them in bucket order, the larger
            // rivers last, so their floors win where valleys meet
            for (int r = RiverCount - 1; r >= 0; r--)
                for (int i = Start[r]; i < Start[r + 1] - 1; i++)
                {
                    double reach = Math.Max(Reach[i], Reach[i + 1]);
                    Valleys.Add(i, Math.Min(PE[i], PE[i + 1]) - reach, Math.Min(PN[i], PN[i + 1]) - reach,
                        Math.Max(PE[i], PE[i + 1]) + reach, Math.Max(PN[i], PN[i + 1]) + reach);
                    // two lattice cells past the bank: every corner of a cell the channel reaches
                    // must see it, or the carve stops dead inside the cell
                    double bank = Math.Max(Bank[i], Bank[i + 1]) + 2 * FineM;
                    Channels.Add(i, Math.Min(PE[i], PE[i + 1]) - bank, Math.Min(PN[i], PN[i + 1]) - bank,
                        Math.Max(PE[i], PE[i + 1]) + bank, Math.Max(PN[i], PN[i + 1]) + bank);
                }
            Valleys.Freeze();
            Channels.Freeze();

            BuildLines();

            for (int l = 0; l < Lines.Count; l++)
            {
                var line = Lines[l];
                for (int i = 0; i + 1 < line.Count; i++)
                    LineSegs.Add(l << 16 | i, Math.Min(line.E[i], line.E[i + 1]) - LineReach,
                        Math.Min(line.N[i], line.N[i + 1]) - LineReach,
                        Math.Max(line.E[i], line.E[i + 1]) + LineReach,
                        Math.Max(line.N[i], line.N[i + 1]) + LineReach);
            }
            LineSegs.Freeze();

            PlaceVillages(relief);
            foreach (var v in Villages)
            {
                double r = v.HalfLength + 500;
                VillageIndex.Add(v.Id, v.E - r, v.N - r, v.E + r, v.N + r);
            }
            VillageIndex.Freeze();
        }

        public int RiverCount => MouthKm2.Count;
        /// <summary>The land each river drains at its mouth, the most it drains anywhere.</summary>
        public readonly List<double> MouthKm2 = new();

        // ---- rivers --------------------------------------------------------------------------

        private void TraceRivers(Relief relief)
        {
            int count = relief.Cols * relief.Rows;
            int minNodes = (int)Math.Ceiling(RiverMinKm2 / relief.NodeAreaKm2);
            bool IsRiver(int k) => relief.Area[k] >= minNodes && relief.LakeOf[k] < 0;

            // donors of every river node, compressed
            var donorCount = new int[count + 1];
            for (int k = 0; k < count; k++)
                if (IsRiver(k) && relief.Receiver[k] >= 0 && IsRiver(relief.Receiver[k])) donorCount[relief.Receiver[k] + 1]++;
            for (int k = 0; k < count; k++) donorCount[k + 1] += donorCount[k];
            var donors = new int[donorCount[count]];
            var fill = (int[])donorCount.Clone();
            for (int k = 0; k < count; k++)
                if (IsRiver(k) && relief.Receiver[k] >= 0 && IsRiver(relief.Receiver[k]))
                    donors[fill[relief.Receiver[k]]++] = k;

            // outlets: into a lake or off the map; the largest first, so indices follow size
            var roots = new List<int>();
            for (int k = 0; k < count; k++)
                if (IsRiver(k) && (relief.Receiver[k] < 0 || !IsRiver(relief.Receiver[k]))) roots.Add(k);
            roots.Sort((a, b) => relief.Area[b] != relief.Area[a] ? relief.Area[b].CompareTo(relief.Area[a]) : a.CompareTo(b));

            var pe = _pe;
            var pn = _pn;
            var bed = _bed;
            var floor = _floor;
            var reach = new List<float>();
            var half = new List<float>();
            var bank = new List<float>();
            var depth = new List<float>();
            var water = new List<bool>();
            var riverOf = new List<int>();

            // (first node, parent river, confluence node or -1)
            var queue = new Queue<(int Node, int Parent, int Mouth)>();
            foreach (int k in roots) queue.Enqueue((k, -1, relief.Receiver[k]));
            var chain = new List<int>();
            while (queue.Count > 0)
            {
                var (first, parent, mouth) = queue.Dequeue();
                chain.Clear();
                int river = MouthKm2.Count;
                for (int k = first; ;)
                {
                    chain.Add(k);
                    int main = -1;
                    for (int d = donorCount[k]; d < donorCount[k + 1]; d++)
                    {
                        int m = donors[d];
                        if (main < 0 || relief.Area[m] > relief.Area[main]
                            || (relief.Area[m] == relief.Area[main] && m < main)) main = m;
                    }
                    for (int d = donorCount[k]; d < donorCount[k + 1]; d++)
                        if (donors[d] != main) queue.Enqueue((donors[d], river, k));
                    if (main < 0) break;
                    k = main;
                }

                // mouth first: the confluence (or the lake node it drains into) leads the chain
                var pts = new List<(double E, double N, double A)>();
                if (mouth >= 0)
                {
                    var (me, mn) = relief.NodePos(mouth);
                    pts.Add((me, mn, relief.Area[first]));
                }
                foreach (int k in chain)
                {
                    var (e, n) = relief.NodePos(k);
                    pts.Add((e, n, relief.Area[k]));
                }
                // a lone node at the edge of the map: no line to follow, and nothing drains into it
                if (pts.Count < 2) continue;

                // traced on the grid a river runs in 45-degree steps: average them out along
                // the valley, then round the corners
                pts = Chaikin(Chaikin(SmoothPath(pts, 2)));

                // a tributary meets its parent's smoothed line, not the node it was traced to
                double mouthBed;
                int mouthLake = mouth >= 0 ? relief.LakeOf[mouth] : -1;
                if (parent >= 0)
                {
                    var at = NearestOnRiver(parent, pts[0].E, pts[0].N);
                    pts[0] = (at.E, at.N, pts[0].A);
                    mouthBed = at.Bed;
                }
                else if (mouthLake >= 0) mouthBed = relief.LakeLevels[mouthLake];
                else mouthBed = relief.Macro(pts[0].E, pts[0].N);

                // the bed: macro height, never rising downstream, never below the mouth
                int np = pts.Count;
                var b = new double[np];
                double low = double.MaxValue;
                for (int i = np - 1; i >= 0; i--)
                {
                    low = Math.Min(low, relief.Macro(pts[i].E, pts[i].N));
                    b[i] = Math.Max(low, mouthBed);
                }
                // Crossing its parent's flat floor, a tributary runs at the floor's level: traced on
                // 500 m nodes it often comes down beside the floor, on a flank the parent's valley
                // has since cut away, and would hang there tens of metres up. (Lowering a point
                // leaves the profile rising upstream; the pass after puts back any dip.)
                if (parent >= 0)
                {
                    double along = 0;
                    for (int i = 0; i < np && along < 6000; i++)
                    {
                        if (i > 0) along += Math.Sqrt(Sq(pts[i].E - pts[i - 1].E) + Sq(pts[i].N - pts[i - 1].N));
                        var at = NearestOnRiver(parent, pts[i].E, pts[i].N);
                        if (at.D < at.Floor) b[i] = Math.Min(b[i], Math.Max(at.Bed, mouthBed) + 0.3);
                    }
                    for (int i = np - 2; i >= 0; i--) b[i] = Math.Min(b[i], b[i + 1]);
                }
                // averaging a monotone sequence keeps it monotone, and spreads the steps
                for (int pass = 0; pass < 3; pass++) b = Average(b, 3);
                b[0] = mouthBed;

                // valley shape from the land drained and the fall
                var s = new double[np];
                for (int i = 1; i < np; i++)
                    s[i] = s[i - 1] + Math.Sqrt(Sq(pts[i].E - pts[i - 1].E) + Sq(pts[i].N - pts[i - 1].N));
                var wf = new double[np];
                var grad = new double[np];
                for (int i = 0; i < np; i++)
                {
                    int i0 = Math.Max(0, i - 3), i1 = Math.Min(np - 1, i + 3);
                    grad[i] = s[i1] > s[i0] ? (b[i1] - b[i0]) / (s[i1] - s[i0]) : 0;
                    double km2 = pts[i].A * relief.NodeAreaKm2;
                    wf[i] = Math.Clamp(20 + 16 * Math.Sqrt(km2), 20, 1100) * Math.Clamp(1 - grad[i] / 0.12, 0.25, 1);
                }
                wf = Average(wf, 3);

                // The walls: from the floor's edge the ground is handed back to the relief over a
                // span wide enough for the height it has to climb there. A fixed span squeezed a
                // kilometre of rise into a kilometre of smoothstep, 60 degrees in the middle; at
                // rise / 0.55 the steepest part of the hand-over stays near 40.
                var span = new double[np];
                for (int i = 0; i < np; i++)
                {
                    double rise = relief.PeakAt(pts[i].E, pts[i].N) - b[i];
                    span[i] = Math.Clamp(Math.Max(250 + 0.8 * wf[i], rise / 0.55), 250, 3200);
                }
                span = Average(span, 4);

                // The river meanders across its floor, away from the valley's axis by up to a third
                // of the floor, not at all near its ends, where it meets its parent or its source.
                // The roads keep to the axis, so they run straighter than the water.
                var wanderE = new double[np];
                var wanderN = new double[np];
                double total = s[np - 1];
                for (int i = 0; i < np; i++)
                {
                    int i0 = Math.Max(0, i - 1), i1 = Math.Min(np - 1, i + 1);
                    double dx = pts[i1].E - pts[i0].E, dy = pts[i1].N - pts[i0].N;
                    double len = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
                    double taper = SmoothStep(0, 500, s[i]) * SmoothStep(0, 500, total - s[i]);
                    double amp = Math.Clamp(0.3 * wf[i] - 20, 0, 250) * taper;
                    double off = amp * Math.Clamp(2 * Noise.Fbm(s[i] / 1400, river * 3.7, 2, 301), -1, 1);
                    wanderE[i] = pts[i].E - dy / len * off;
                    wanderN[i] = pts[i].N + dx / len * off;
                }

                Start.Add(pe.Count);
                Parent.Add(parent);
                for (int i = 0; i < np; i++)
                {
                    double km2 = pts[i].A * relief.NodeAreaKm2;
                    double h = Math.Clamp(1.5 + 0.3 * Math.Sqrt(km2), 2, 22);
                    pe.Add((float)wanderE[i]);
                    pn.Add((float)wanderN[i]);
                    _ae.Add((float)pts[i].E);
                    _an.Add((float)pts[i].N);
                    _meander.Add((float)Math.Clamp(0.3 * wf[i] - 20, 0, 250));
                    bed.Add((float)b[i]);
                    floor.Add((float)wf[i]);
                    reach.Add((float)(wf[i] + span[i]));
                    half.Add((float)h);
                    bank.Add((float)(h + Math.Clamp(0.8 * h, 3, 10)));
                    depth.Add((float)Math.Clamp(0.6 + 0.15 * h, 0.8, 3));
                    water.Add(grad[i] < 0.05);
                    riverOf.Add(river);
                }
                MouthKm2.Add(pts[0].A * relief.NodeAreaKm2);
            }
            Start.Add(pe.Count);
            PE = pe.ToArray();
            PN = pn.ToArray();
            Bed = bed.ToArray();
            Floor = floor.ToArray();
            Reach = reach.ToArray();
            Half = half.ToArray();
            Bank = bank.ToArray();
            Depth = depth.ToArray();
            Water = water.ToArray();
            RiverOf = riverOf.ToArray();
        }

        // the points while they are traced: a tributary reads its parent's, finished before it
        private readonly List<float> _pe = new(), _pn = new(), _bed = new(), _floor = new();
        // the valley's axis (the river before it meanders) and how far it may wander: for the lines
        private readonly List<float> _ae = new(), _an = new(), _meander = new();

        /// <summary>The nearest point on a finished river, its bed and floor half-width there, and how far.</summary>
        private (double E, double N, double Bed, double Floor, double D) NearestOnRiver(int river, double e, double n)
        {
            double best = double.MaxValue;
            (double, double, double, double, double) at = default;
            int end = river + 1 < Start.Count ? Start[river + 1] : _pe.Count;
            for (int i = Start[river]; i < end - 1; i++)
            {
                // a cheap reject first: rivers run for hundreds of kilometres
                if (Math.Abs(_pe[i] - e) > best + 200 || Math.Abs(_pn[i] - n) > best + 200) continue;
                var (d, t) = SegmentDistance(e, n, _pe[i], _pn[i], _pe[i + 1], _pn[i + 1]);
                if (d >= best) continue;
                best = d;
                at = (_pe[i] + (_pe[i + 1] - _pe[i]) * t, _pn[i] + (_pn[i + 1] - _pn[i]) * t,
                    _bed[i] + (_bed[i + 1] - _bed[i]) * t, _floor[i] + (_floor[i + 1] - _floor[i]) * t, d);
            }
            return at;
        }

        // ---- roads and railways --------------------------------------------------------------

        private void BuildLines()
        {
            var roadOf = new int[RiverCount];
            var railOf = new int[RiverCount];
            Array.Fill(roadOf, -1);
            Array.Fill(railOf, -1);
            for (int r = 0; r < RiverCount; r++)
            {
                if (MouthKm2[r] < RoadMinKm2) continue;
                int side = Noise.Hash01(r, 17, 131) < 0.5 ? 1 : -1;
                var road = OffsetLine(r, side, cap: 250, clear: 8, maxBed: 1900, maxGrad: 0.10);
                if (road != null)
                    roadOf[r] = AddLine(RoadClass.Road, road, side, Parent[r] >= 0 ? roadOf[Parent[r]] : -1);
                if (MouthKm2[r] < RailMinKm2) continue;
                var rail = OffsetLine(r, -side, cap: 380, clear: 14, maxBed: 1400, maxGrad: 0.025);
                if (rail != null)
                    railOf[r] = AddLine(RoadClass.Railway, rail, -side, Parent[r] >= 0 ? railOf[Parent[r]] : -1);
            }
        }

        /// <summary>
        /// A line along a river at an offset toward one side: most of the way across the valley
        /// floor, capped, and pulled in on the inside of bends so it cannot fold over itself. It
        /// starts where the river leaves a lake and ends where the valley gets too high or too
        /// steep. Null if nothing is left.
        /// </summary>
        private List<(double E, double N)>? OffsetLine(int r, int side, double cap, double clear,
            double maxBed, double maxGrad)
        {
            int a = Start[r], z = Start[r + 1];
            int np = z - a;
            if (np < 4) return null;

            // first point clear of a lake, last point before the valley gets too steep or high
            int first = 0;
            while (first < np && Relief.Instance.LakeWeight(PE[a + first], PN[a + first], out _) > 0.25) first++;
            int last = first;
            while (last + 1 < np)
            {
                int i = a + last + 1;
                double run = Math.Sqrt(Sq(PE[i] - PE[i - 1]) + Sq(PN[i] - PN[i - 1]));
                double grad = run > 0 ? (Bed[i] - Bed[i - 1]) / run : 0;
                if (Bed[i] > maxBed || grad > maxGrad) break;
                last++;
            }
            if (last - first < 3) return null;

            int m = last - first + 1;
            var tE = new double[m];
            var tN = new double[m];
            var kappa = new double[m];
            for (int j = 0; j < m; j++)
            {
                int i = a + first + j;
                int i0 = Math.Max(a, i - 1), i1 = Math.Min(z - 1, i + 1);
                double dx = _ae[i1] - _ae[i0], dy = _an[i1] - _an[i0], len = Math.Sqrt(dx * dx + dy * dy);
                tE[j] = dx / len;
                tN[j] = dy / len;
            }
            for (int j = 1; j < m - 1; j++)
            {
                int i = a + first + j;
                double turn = Math.Atan2(tE[j - 1] * tN[j + 1] - tN[j - 1] * tE[j + 1],
                    tE[j - 1] * tE[j + 1] + tN[j - 1] * tN[j + 1]);
                double run = Math.Sqrt(Sq(_ae[i + 1] - _ae[i - 1]) + Sq(_an[i + 1] - _an[i - 1]));
                kappa[j] = run > 0 ? turn / run : 0;
            }
            kappa = Average(kappa, 2);
            var offset = new double[m];
            for (int j = 0; j < m; j++)
            {
                int i = a + first + j;
                // across most of the floor, and always clear of the river however far it wanders
                double clearOfRiver = _meander[i] + Bank[i] + clear;
                double o = Math.Max(Math.Min(0.5 * Floor[i], cap), clearOfRiver);
                // positive curvature turns left: its inside is the left, side +1
                if (Math.Sign(kappa[j]) == side && Math.Abs(kappa[j]) > 1e-9)
                    o = Math.Min(o, Math.Max(clearOfRiver, 0.5 / Math.Abs(kappa[j])));
                offset[j] = o;
            }
            offset = Average(offset, 2);
            var pts = new List<(double E, double N, double A)>(m);
            for (int j = 0; j < m; j++)
            {
                int i = a + first + j;
                // left normal (-tN, tE)
                pts.Add((_ae[i] - tN[j] * side * offset[j], _an[i] + tE[j] * side * offset[j], 0));
            }
            return Chaikin(pts).Select(p => (p.E, p.N)).ToList();
        }

        /// <summary>
        /// Adds a line, joined at its mouth end to the nearest point of its parent's line by a
        /// straight link, so the valleys' roads make one network.
        /// </summary>
        private int AddLine(RoadClass cls, List<(double E, double N)> pts, int side, int parentLine)
        {
            if (parentLine >= 0)
            {
                var p = Lines[parentLine];
                double best = double.MaxValue;
                (double E, double N) join = default;
                for (int i = 0; i + 1 < p.Count; i++)
                {
                    var (d, t) = SegmentDistance(pts[0].E, pts[0].N, p.E[i], p.N[i], p.E[i + 1], p.N[i + 1]);
                    if (d >= best) continue;
                    best = d;
                    join = (p.E[i] + (p.E[i + 1] - p.E[i]) * t, p.N[i] + (p.N[i + 1] - p.N[i]) * t);
                }
                if (best < 3000)
                {
                    // at the line's own spacing, so the drawn curve eases into the link
                    int steps = Math.Max(1, (int)Math.Ceiling(best / 60));
                    var link = new List<(double E, double N)>();
                    for (int s = 0; s < steps; s++)
                        link.Add((join.E + (pts[0].E - join.E) * s / steps, join.N + (pts[0].N - join.N) * s / steps));
                    pts.InsertRange(0, link);
                }
            }
            var line = new Line
            {
                Class = cls,
                E = pts.Select(p => p.E).ToArray(),
                N = pts.Select(p => p.N).ToArray(),
                S = new double[pts.Count],
                Side = side,
            };
            for (int i = 1; i < line.Count; i++)
                line.S[i] = line.S[i - 1] + Math.Sqrt(Sq(line.E[i] - line.E[i - 1]) + Sq(line.N[i] - line.N[i - 1]));
            line.MinE = line.E.Min();
            line.MaxE = line.E.Max();
            line.MinN = line.N.Min();
            line.MaxN = line.N.Max();
            Lines.Add(line);
            return Lines.Count - 1;
        }

        // ---- villages ------------------------------------------------------------------------

        /// <summary>
        /// Village slots every <see cref="VillageSpacing"/> along each valley road, from its mouth:
        /// most are taken, below the high valleys and where the floor is wide enough.
        /// </summary>
        private void PlaceVillages(Relief relief)
        {
            for (int l = 0; l < Lines.Count; l++)
            {
                var line = Lines[l];
                if (line.Class != RoadClass.Road) continue;
                double length = line.S[^1];
                for (int slot = 0; (slot + 0.5) * VillageSpacing < length; slot++)
                {
                    if (Noise.Hash01(l, slot, 211) > 0.72) continue;
                    double s = (slot + 0.5 + (Noise.Hash01(l, slot, 213) - 0.5) * 0.5) * VillageSpacing;
                    double halfLength = 160 + Noise.Hash01(l, slot, 217) * 260;
                    if (s - halfLength < 150 || s + halfLength > length - 50) continue;
                    var (e, n) = PointAt(line, s);
                    if (relief.LakeWeight(e, n, out _) > 0.1) continue;
                    if (relief.Macro(e, n) > 1700) continue;
                    // a valley floor wide enough for houses both sides of the road
                    if (NearestFloor(e, n) < 70) continue;
                    Villages.Add(new VillageSlot(Villages.Count, l, s, halfLength, e, n));
                }
            }
        }

        /// <summary>The valley floor half-width of the river nearest a point.</summary>
        private double NearestFloor(double e, double n)
        {
            double best = double.MaxValue, floor = 0;
            foreach (int i in Valleys.At(e, n))
            {
                var (d, _) = SegmentDistance(e, n, PE[i], PN[i], PE[i + 1], PN[i + 1]);
                if (d < best)
                {
                    best = d;
                    floor = Floor[i];
                }
            }
            return floor;
        }

        public static (double E, double N) PointAt(Line line, double s)
        {
            int i = Array.BinarySearch(line.S, s);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, line.Count - 2);
            double t = (s - line.S[i]) / Math.Max(1e-9, line.S[i + 1] - line.S[i]);
            return (line.E[i] + (line.E[i + 1] - line.E[i]) * t, line.N[i] + (line.N[i + 1] - line.N[i]) * t);
        }
    }

    // ---- geometry helpers ------------------------------------------------------------------------

    /// <summary>
    /// A fixed-size grid of lists over LV95, filled then frozen into one array: the lists hold
    /// items in the order they were added.
    /// </summary>
    private sealed class Buckets
    {
        private readonly double _originE, _originN, _size;
        private readonly int _cols, _rows;
        private List<int>?[]? _building;
        private int[] _start = [];
        private int[] _items = [];

        public Buckets(double originE, double originN, double size, int cols, int rows)
        {
            _originE = originE;
            _originN = originN;
            _size = size;
            _cols = cols;
            _rows = rows;
            _building = new List<int>?[cols * rows];
        }

        public void Add(int item, double minE, double minN, double maxE, double maxN)
        {
            int c0 = Math.Max(0, (int)Math.Floor((minE - _originE) / _size));
            int c1 = Math.Min(_cols - 1, (int)Math.Floor((maxE - _originE) / _size));
            int r0 = Math.Max(0, (int)Math.Floor((minN - _originN) / _size));
            int r1 = Math.Min(_rows - 1, (int)Math.Floor((maxN - _originN) / _size));
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    (_building![r * _cols + c] ??= new List<int>()).Add(item);
        }

        public void Freeze()
        {
            _start = new int[_cols * _rows + 1];
            for (int b = 0; b < _cols * _rows; b++) _start[b + 1] = _start[b] + (_building![b]?.Count ?? 0);
            _items = new int[_start[^1]];
            for (int b = 0; b < _cols * _rows; b++)
                _building![b]?.CopyTo(_items, _start[b]);
            _building = null;
        }

        /// <summary>The items listed for the bucket holding a point.</summary>
        public ReadOnlySpan<int> At(double e, double n)
        {
            int c = (int)Math.Floor((e - _originE) / _size), r = (int)Math.Floor((n - _originN) / _size);
            if (c < 0 || r < 0 || c >= _cols || r >= _rows) return ReadOnlySpan<int>.Empty;
            int b = r * _cols + c;
            return new ReadOnlySpan<int>(_items, _start[b], _start[b + 1] - _start[b]);
        }

        /// <summary>Every item listed in any bucket a box touches, once each, in item order.</summary>
        public List<int> In(double minE, double minN, double maxE, double maxN)
        {
            var set = new SortedSet<int>();
            int c0 = Math.Max(0, (int)Math.Floor((minE - _originE) / _size));
            int c1 = Math.Min(_cols - 1, (int)Math.Floor((maxE - _originE) / _size));
            int r0 = Math.Max(0, (int)Math.Floor((minN - _originN) / _size));
            int r1 = Math.Min(_rows - 1, (int)Math.Floor((maxN - _originN) / _size));
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    int b = r * _cols + c;
                    for (int i = _start[b]; i < _start[b + 1]; i++) set.Add(_items[i]);
                }
            return set.ToList();
        }
    }

    /// <summary>Distance from a point to a segment, and where along it (0..1) the nearest point is.</summary>
    private static (double D, double T) SegmentDistance(double e, double n, double ae, double an, double be, double bn)
    {
        double dx = be - ae, dy = bn - an;
        double len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? Math.Clamp(((e - ae) * dx + (n - an) * dy) / len2, 0, 1) : 0;
        double px = ae + dx * t - e, py = an + dy * t - n;
        return (Math.Sqrt(px * px + py * py), t);
    }

    private static double Sq(double v) => v * v;

    /// <summary>A moving average of positions over ±radius points; the end points stay where they are.</summary>
    private static List<(double E, double N, double A)> SmoothPath(List<(double E, double N, double A)> p, int radius)
    {
        var o = new List<(double E, double N, double A)>(p.Count);
        for (int i = 0; i < p.Count; i++)
        {
            int r = Math.Min(radius, Math.Min(i, p.Count - 1 - i));
            double e = 0, n = 0;
            for (int j = i - r; j <= i + r; j++)
            {
                e += p[j].E;
                n += p[j].N;
            }
            o.Add((e / (2 * r + 1), n / (2 * r + 1), p[i].A));
        }
        return o;
    }

    /// <summary>One round of Chaikin corner cutting; the end points stay where they are.</summary>
    private static List<(double E, double N, double A)> Chaikin(List<(double E, double N, double A)> p)
    {
        if (p.Count < 3) return p;
        var o = new List<(double E, double N, double A)>(p.Count * 2) { p[0] };
        for (int i = 0; i + 1 < p.Count; i++)
        {
            var a = p[i];
            var b = p[i + 1];
            if (i > 0) o.Add((0.75 * a.E + 0.25 * b.E, 0.75 * a.N + 0.25 * b.N, 0.75 * a.A + 0.25 * b.A));
            if (i + 2 < p.Count) o.Add((0.25 * a.E + 0.75 * b.E, 0.25 * a.N + 0.75 * b.N, 0.25 * a.A + 0.75 * b.A));
        }
        o.Add(p[^1]);
        return o;
    }

    /// <summary>A moving average over ±radius, the window shrinking at the ends.</summary>
    private static double[] Average(double[] v, int radius)
    {
        var o = new double[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            int a = Math.Max(0, i - radius), b = Math.Min(v.Length - 1, i + radius);
            double sum = 0;
            for (int j = a; j <= b; j++) sum += v[j];
            o[i] = sum / (b - a + 1);
        }
        return o;
    }
}
