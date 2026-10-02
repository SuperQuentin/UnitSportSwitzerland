namespace UnitSport.Tools.RoadGen.Network;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// Urban streets (#119): which sides of which roads are lined by facades, and how wide a sidewalk
/// fits there, decided in the network stage on a tile's final segments and written into the v3
/// ATTR record (<see cref="RoadAttrFlags.Urban"/>, <see cref="RoadSide.SidewalkDm"/>,
/// <see cref="RoadSide.KerbCm"/>). A segment whose sidewalk changes along it is split, so every
/// piece carries one cross-section; points are cut, never moved.
///
/// <para>
/// Every <see cref="Step"/> metres along a candidate (paved at-grade <c>Major</c>..<c>Lane</c>
/// and <c>Square</c>), on each side, a ray runs out from the carriageway edge: the first building
/// wall (<see cref="Facades"/>) and the first other line (road, rail, stream, wall:
/// <see cref="Obstacles"/>) bound what is free. The street is urban where the
/// <see cref="UrbanField"/> says so (the building walls around the place). The sidewalk spans what is free, clamped to
/// [<see cref="MinSidewalk"/>, <see cref="MaxSidewalk"/>] (narrower down to
/// <see cref="NarrowSidewalk"/> where a facade stands close, none below), in
/// <see cref="WidthStep"/> steps, constant over pieces of at least <see cref="MinPiece"/>, and
/// never wider than the narrowest station of its piece or its neighbours (so it covers no wall
/// between two samples).
/// </para>
/// </summary>
public sealed class StreetPlanner(Facades facades, UrbanField field, StreetPlanner.Obstacles obstacles, StreetPlanner.Stats stats,
    Func<double, double, double>? ground = null)
{
    public const double Step = 2.0;
    /// <summary>How far out a ray looks for a facade.</summary>
    public const double UrbanReach = 15.0;
    public const double MinSidewalk = 1.5;
    public const double MaxSidewalk = 5.0;
    /// <summary>Where a facade stands closer than the minimum, a sidewalk this narrow still fits.</summary>
    public const double NarrowSidewalk = 1.0;
    /// <summary>
    /// An urban side whose facade stands back further than <see cref="MaxSidewalk"/> (a front
    /// yard) or not at all (a gap between houses): an ordinary sidewalk this wide. Paving all the
    /// way to a facade 12 m back reads as a square, not a street.
    /// </summary>
    public const double OpenSidewalk = 2.0;
    /// <summary>The sidewalk stops this short of a facade, and of another line's paved edge.</summary>
    public const double FacadeClearance = 0.2, LineClearance = 0.3;
    public const double WidthStep = 0.5;
    /// <summary>
    /// A side facing another carriageway or a tram line that runs alongside (within 30 deg) this
    /// close, with no building between, is a median or an island, not a sidewalk: TLM draws a big
    /// junction's carriageways, turn lanes, bus lanes and tram lines as parallel lines a few metres apart.
    /// </summary>
    public const double MedianReach = 15.0;
    /// <summary>Shortest stretch of one constant cross-section; shorter ones merge.</summary>
    public const double MinPiece = 20.0;
    public const byte KerbCm = 12;

    /// <summary>
    /// <c>--debug-street E,N</c> (with <c>--rewrite</c>): every station of the candidate segments
    /// passing within 3 m of this LV95 point is printed (density, facade, other line, width).
    /// </summary>
    public static (double E, double N)? Debug;
    private const double RayStep = 0.25;

    /// <summary>Region numbers, printed with the stage report.</summary>
    public sealed class Stats
    {
        public int Candidates, Segments, Pieces, Tiles;
        public double CandidateKm, UrbanKm, Ms;
        /// <summary>Sidewalk length per width bucket: &lt;1.5, 1.5-2.5, 2.5-3.5, 3.5-5, metres (one side each).</summary>
        public readonly double[] SidewalkM = new double[4];
        /// <summary>Urban stations by what bounded the sidewalk: a facade, another line, the maximum, nothing.</summary>
        public int ByFacade, ByLine, Yard, Open, TooNarrow, Median;
        /// <summary>Where a sidewalk runs to a facade: the bare ground there minus the sidewalk's top, in metres.</summary>
        public readonly List<double> FacadeStep = new();

        public string Format()
        {
            var c = CultureInfo.InvariantCulture;
            double total = SidewalkM.Sum();
            int st = ByFacade + ByLine + Yard + Open + TooNarrow + Median;
            string Pct(int n) => st == 0 ? "-" : (100.0 * n / st).ToString("F0", c) + "%";
            return string.Create(c, $"""
                  streets (#119): {UrbanKm:F1} of {CandidateKm:F1} km of candidate roads urban; sidewalks {total / 1000:F2} km (one side each)
                    widths <1.5 {SidewalkM[0] / 1000:F2} km, 1.5-2.5 {SidewalkM[1] / 1000:F2}, 2.5-3.5 {SidewalkM[2] / 1000:F2}, 3.5-5 {SidewalkM[3] / 1000:F2}
                    urban stations: sidewalk to a facade {Pct(ByFacade)}, front yard {Pct(Yard)}, open {Pct(Open)}, too narrow for one {Pct(TooNarrow)}, stopped by another line {Pct(ByLine)}, a median {Pct(Median)}
                    {Segments:N0} candidate segments -> {Pieces:N0} pieces; planning {Ms / Math.Max(1, Tiles):F1} ms/tile
                """) + "\n" + Steps();
        }

        /// <summary>How far the ground at a facade the sidewalk reaches lies off its top (both signs).</summary>
        private string Steps()
        {
            if (FacadeStep.Count == 0) return "    ground at the facade: not measured (no terrain)";
            var c = CultureInfo.InvariantCulture;
            int n = FacadeStep.Count;
            string Share(Func<double, bool> f) => (100.0 * FacadeStep.Count(f) / n).ToString("F1", c) + "%";
            var abs = FacadeStep.Select(Math.Abs).OrderBy(x => x).ToList();
            return string.Create(c, $"    ground at the facade vs the sidewalk top ({n:N0} stations): median {abs[n / 2]:F2} m, p90 {abs[(int)(n * 0.9)]:F2} m; above by > 0.5 m {Share(d => d > 0.5)}, > 1 m {Share(d => d > 1)}; below by > 0.5 m {Share(d => d < -0.5)}, > 1 m {Share(d => d < -1)}");
        }
    }

    public static bool IsCandidate(RoadSegment s) =>
        s.Class is RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane or RoadClass.Square
        && s.Surface != RoadSurface.Natural && s.PointCount >= 2
        && RoadEmbankment.IsAtGrade(s) && (s.Flags & (RoadFlags.Ford | RoadFlags.Stairs)) == 0;

    /// <summary>One side's state over a stretch: urban or not, sidewalk width in decimetres (0 none).</summary>
    private readonly record struct SideState(bool Urban, byte WidthDm);

    private readonly record struct Station(double X, double Z, double Along, double Fx, double Fz, double Y = 0)
    {
        /// <summary>Unit vector to one side, X east and Z south (as <c>EmbankmentPlanner</c>).</summary>
        public (double X, double Z) Side(bool right) => right ? (-Fz, Fx) : (Fz, -Fx);
    }

    /// <summary>
    /// The pieces <paramref name="seg"/> (tile-local, final) becomes, with their sidewalks;
    /// itself if it is no candidate or has one cross-section all along. <paramref name="self"/>
    /// is its own line in <see cref="Obstacles"/>. <paramref name="urban"/>: urban for most of its
    /// length, so its paint follows the built-up rules.
    /// </summary>
    public List<RoadSegment> Plan(RoadSegment seg, TileId id, int self, out bool urban)
    {
        urban = false;
        if (!IsCandidate(seg)) return [seg];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stations = Stations(seg);
        double km = stations[^1].Along / 1000;
        stats.Segments++;
        stats.Candidates++;
        stats.CandidateKm += km;

        double half = Math.Max(seg.Width, seg.Attributes.WidthCm / 100.0) * 0.5;
        var l = Measure(id, self, stations, half, right: false);
        var r = Measure(id, self, stations, half, right: true);
        var street = Urban(seg, id, stations);
        var left = Widths(seg, street, l, seg.Attributes.Left);
        var right = Widths(seg, street, r, seg.Attributes.Right);
        if (Debug is { } dbg && stations.Any(st => Math.Abs(id.MinE + st.X - dbg.E) < 3 && Math.Abs(id.MaxN - st.Z - dbg.N) < 3))
        {
            var c = CultureInfo.InvariantCulture;
            Console.WriteLine(string.Create(c, $"[street] {seg.Class} {seg.Flags} width {seg.Width:F1}, {stations.Count} stations, tile {id.E}_{id.N}"));
            for (int i = 0; i < stations.Count; i++)
            {
                var st = stations[i];
                Console.WriteLine(string.Create(c, $"  {i,3} ({id.MinE + st.X:F1},{id.MaxN - st.Z:F1}) density {field.Density(id.MinE + st.X, id.MaxN - st.Z):F2} urban {street[i]} | L facade {l.Facade[i],5:F1} line {l.Line[i],5:F1} -> {left[i].WidthDm / 10.0:F1} | R facade {r.Facade[i],5:F1} line {r.Line[i],5:F1} -> {right[i].WidthDm / 10.0:F1}"));
            }
        }

        int urbanStations = street.Count(u => u);
        urban = urbanStations * 2 > stations.Count;

        var pieces = Cut(seg, stations, left, right);
        stats.Pieces += pieces.Count;
        foreach (var p in pieces)
        {
            double len = Length(p);
            if (p.Attributes.Has(RoadAttrFlags.Urban)) stats.UrbanKm += len / 1000;
            foreach (var s in (ReadOnlySpan<RoadSide>)[p.Attributes.Left, p.Attributes.Right])
                if (s.SidewalkDm > 0)
                    stats.SidewalkM[s.SidewalkDm < 15 ? 0 : s.SidewalkDm < 25 ? 1 : s.SidewalkDm < 35 ? 2 : 3] += len;
        }
        stats.Ms += clock.Elapsed.TotalMilliseconds;
        return pieces;
    }

    /// <summary>What one side's rays found at every station.</summary>
    private sealed record SideRays(double[] Facade, double[] Free, double[] Line, double[] Median, (double E, double N)?[] FacadeAt, double[] Y);

    /// <summary>
    /// Per station, the distance from the carriageway edge to the first wall (+inf if none within
    /// <see cref="UrbanReach"/>), what a sidewalk may span before that wall (less its clearance), and
    /// how far out another line begins (+inf if none within a sidewalk's reach).
    /// </summary>
    private SideRays Measure(TileId id, int self, List<Station> stations, double half, bool right)
    {
        int n = stations.Count;
        var facade = new double[n];
        var free = new double[n];
        var line = new double[n];
        var median = new double[n];
        var facadeAt = new (double E, double N)?[n];
        var y = new double[n];
        double reach = Math.Max(Math.Max(UrbanReach, MedianReach), MaxSidewalk + FacadeClearance);
        for (int i = 0; i < n; i++)
        {
            var st = stations[i];
            var (sx, sz) = st.Side(right);
            facade[i] = double.PositiveInfinity;
            free[i] = double.PositiveInfinity;
            line[i] = double.PositiveInfinity;
            median[i] = double.PositiveInfinity;
            y[i] = st.Y;
            for (double d = 0; d <= reach; d += RayStep)
            {
                double x = st.X + sx * (half + d), z = st.Z + sz * (half + d);
                double e = id.MinE + x, nn = id.MaxN - z;
                if (facades.Occupied(e, nn))
                {
                    facade[i] = d;
                    free[i] = Math.Min(free[i], d - FacadeClearance);
                    // the ground just in front of the wall
                    double back = Math.Max(0, d - 0.5);
                    facadeAt[i] = (id.MinE + st.X + sx * (half + back), id.MaxN - (st.Z + sz * (half + back)));
                    break;
                }
                if (double.IsPositiveInfinity(median[i]) && d <= MedianReach
                    && obstacles.ParallelCarriageway(e, nn, self, st.Fx, -st.Fz))
                    median[i] = d;
                if (double.IsPositiveInfinity(line[i]) && d <= MaxSidewalk + LineClearance
                    && obstacles.Covers(e, nn, self, id.MinE + st.X, id.MaxN - st.Z, half + 1.0, st.Fx, -st.Fz))
                    line[i] = d;
            }
        }
        return new SideRays(facade, free, line, median, facadeAt, y);
    }

    /// <summary>
    /// Whether the street is urban at each station: the <see cref="UrbanField"/> there reaches
    /// <see cref="UrbanField.UrbanAt"/>. The field is the one the network stage lowers roads by, so a
    /// sidewalk only ever stands where the road is fully down and its top lies on the ground; and it
    /// is position-only, so a street cut at a seam or a junction carries on the same. A segment OSM
    /// maps a sidewalk on is urban all along (#118).
    /// </summary>
    private bool[] Urban(RoadSegment seg, TileId id, List<Station> stations)
    {
        var urban = new bool[stations.Count];
        bool osm = seg.Attributes.Left.SidewalkDm > 0 || seg.Attributes.Right.SidewalkDm > 0;
        for (int i = 0; i < stations.Count; i++)
            urban[i] = osm || field.Density(id.MinE + stations[i].X, id.MaxN - stations[i].Z) >= UrbanField.UrbanAt;
        return urban;
    }

    /// <summary>
    /// One side's sidewalk along an urban street: to the facade where it stands within
    /// <see cref="MaxSidewalk"/>, an ordinary <see cref="OpenSidewalk"/> where it stands back or
    /// is missing, what is free at the narrowest of the station and its two neighbours; none where
    /// another line begins within that width (it stops, it does not narrow).
    /// </summary>
    private SideState[] Widths(RoadSegment seg, bool[] urban, SideRays rays, RoadSide osm)
    {
        int n = urban.Length;
        bool square = seg.Class == RoadClass.Square;
        var widthDm = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (!urban[i]) continue;
            // a median or an island: another carriageway alongside, nothing built between
            if (rays.Median[i] < rays.Facade[i] && osm.SidewalkDm == 0) { stats.Median++; continue; }
            double f = rays.Free[i];
            for (int k = Math.Max(0, i - 1); k <= Math.Min(n - 1, i + 1); k++)
                f = Math.Min(f, rays.Free[k]);
            double width;
            if (f <= MaxSidewalk)
            {
                width = f;
                stats.ByFacade++;
                if (rays.FacadeAt[i] is { } at && ground != null && ground(at.E, at.N) is var g && !double.IsNaN(g))
                    stats.FacadeStep.Add(g - (rays.Y[i] + (square ? 0 : KerbCm / 100.0)));
            }
            else if (double.IsPositiveInfinity(rays.Facade[i])) { width = OpenSidewalk; stats.Open++; }
            else { width = OpenSidewalk; stats.Yard++; }
            double least = square ? WidthStep : NarrowSidewalk;
            if (width < least) { stats.TooNarrow++; continue; }
            // another line (a crossing street's sidewalk zone, a path, a stream) within the width:
            // the sidewalk stops there rather than narrowing, so it runs full width up to a junction
            // and the corner patch (CornerPlanner) joins it to the next one
            if (rays.Line[i] - LineClearance < width) { stats.ByLine++; continue; }
            widthDm[i] = (byte)Math.Round(Math.Floor(width / WidthStep + 1e-9) * WidthStep * 10);
        }

        var states = new SideState[n];
        for (int i = 0; i < n; i++) states[i] = new SideState(urban[i], widthDm[i]);
        MergeShort(states, (int)Math.Round(MinPiece / Step));
        return states;
    }

    /// <summary>
    /// A sidewalk piece shorter than <paramref name="shorter"/> stations takes the width of its
    /// widest neighbour that is a sidewalk no wider than itself, so the street does not change
    /// house by house; a sidewalk never grows over a station that could not hold it, and a short
    /// gap (a wall at the kerb) stays. A fragment under half that, between two gaps, is dropped.
    /// </summary>
    private static void MergeShort(SideState[] s, int shorter)
    {
        for (int pass = 0; pass < 64; pass++)
        {
            bool changed = false;
            var runs = Runs(s);
            for (int r = 0; r < runs.Count && !changed; r++)
            {
                var (from, to) = runs[r];
                var me = s[from];
                if (to - from + 1 >= shorter || me.WidthDm == 0) continue;
                SideState? take = null;
                foreach (int nb in (ReadOnlySpan<int>)[r - 1, r + 1])
                {
                    if (nb < 0 || nb >= runs.Count) continue;
                    var other = s[runs[nb].From];
                    if (other.WidthDm == 0 || other.WidthDm > me.WidthDm) continue;
                    if (take is not { } t || other.WidthDm > t.WidthDm) take = other;
                }
                if (take is null && 2 * (to - from + 1) < shorter) take = me with { WidthDm = 0 };
                if (take is not { } apply) continue;
                for (int k = from; k <= to; k++) s[k] = apply;
                changed = true;
            }
            if (!changed) return;
        }
    }

    private static List<(int From, int To)> Runs(SideState[] s)
    {
        var runs = new List<(int, int)>();
        int i = 0;
        while (i < s.Length)
        {
            int j = i;
            while (j + 1 < s.Length && s[j + 1] == s[i]) j++;
            runs.Add((i, j));
            i = j + 1;
        }
        return runs;
    }

    /// <summary>Cuts the segment wherever either side changes, halfway between two stations.</summary>
    private static List<RoadSegment> Cut(RoadSegment seg, List<Station> st, SideState[] left, SideState[] right)
    {
        var cuts = new List<(double Along, int Station)>();
        for (int i = 1; i < st.Count; i++)
            if (left[i] != left[i - 1] || right[i] != right[i - 1])
                cuts.Add(((st[i - 1].Along + st[i].Along) * 0.5, i));

        var pieces = new List<RoadSegment>(cuts.Count + 1);
        double from = 0;
        int first = 0;
        foreach (var (along, station) in cuts.Append((double.PositiveInfinity, st.Count)))
        {
            var points = Slice(seg, from, along);
            if (points.Length >= 6)
            {
                var a = seg.Attributes;
                var l = left[first];
                var r = right[first];
                // the yield bits belong to the segment's ends (#121): only its first piece starts
                // there, only its last ends there
                var flags = a.Flags;
                if (first > 0) flags &= ~RoadAttrFlags.YieldAtStart;
                if (station < st.Count) flags &= ~RoadAttrFlags.YieldAtEnd;
                pieces.Add(new RoadSegment
                {
                    Class = seg.Class, Surface = seg.Surface, Flags = seg.Flags, Width = seg.Width,
                    Points = points,
                    Attributes = a with
                    {
                        Flags = l.Urban || r.Urban ? flags | RoadAttrFlags.Urban : flags,
                        Left = Side(a.Left, l, seg.Class),
                        Right = Side(a.Right, r, seg.Class),
                    },
                });
            }
            from = along;
            first = station;
        }
        return pieces;
    }

    /// <summary>
    /// The side's record: a kerbed sidewalk, or flush paving (no kerb) on a square.
    /// </summary>
    private static RoadSide Side(RoadSide side, SideState s, RoadClass cls) =>
        side with { SidewalkDm = s.WidthDm, KerbCm = s.WidthDm == 0 || cls == RoadClass.Square ? (byte)0 : KerbCm };

    /// <summary>The part of the polyline from <paramref name="a0"/> to <paramref name="a1"/> metres (plan) along it.</summary>
    private static float[] Slice(RoadSegment seg, double a0, double a1)
    {
        var p = seg.Points;
        var result = new List<float>();
        double travelled = 0;
        void Add(float x, float y, float z)
        {
            int k = result.Count;
            if (k >= 3 && Math.Abs(result[k - 3] - x) < 1e-4 && Math.Abs(result[k - 1] - z) < 1e-4) return;
            result.Add(x); result.Add(y); result.Add(z);
        }
        for (int i = 0; i < seg.PointCount - 1; i++)
        {
            float ax = p[i * 3], ay = p[i * 3 + 1], az = p[i * 3 + 2];
            float bx = p[i * 3 + 3], by = p[i * 3 + 4], bz = p[i * 3 + 5];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            double s0 = travelled, s1 = travelled + len;
            travelled = s1;
            if (s1 < a0 || s0 > a1) continue;
            float At(double s, float u, float v) => len < 1e-9 ? u : (float)(u + (v - u) * Math.Clamp((s - s0) / len, 0, 1));
            if (a0 >= s0) Add(At(a0, ax, bx), At(a0, ay, by), At(a0, az, bz));
            else Add(ax, ay, az);
            if (a1 <= s1) { Add(At(a1, ax, bx), At(a1, ay, by), At(a1, az, bz)); break; }
            Add(bx, by, bz);
        }
        return result.ToArray();
    }

    /// <summary>Points every <see cref="Step"/> metres along the line, both ends included.</summary>
    private static List<Station> Stations(RoadSegment seg)
    {
        var result = new List<Station>();
        var p = seg.Points;
        double next = 0, travelled = 0;
        double fx = 0, fz = -1;
        for (int i = 0; i < seg.PointCount - 1; i++)
        {
            double ax = p[i * 3], az = p[i * 3 + 2], bx = p[i * 3 + 3], bz = p[i * 3 + 5];
            double ay = p[i * 3 + 1], by = p[i * 3 + 4];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            if (len < 1e-6) continue;
            fx = (bx - ax) / len;
            fz = (bz - az) / len;
            for (; next <= travelled + len; next += Step)
            {
                double t = (next - travelled) / len;
                result.Add(new Station(ax + (bx - ax) * t, az + (bz - az) * t, next, fx, fz, ay + (by - ay) * t));
            }
            travelled += len;
        }
        if (result.Count == 0 || travelled - result[^1].Along > 0.1)
            result.Add(new Station(p[(seg.PointCount - 1) * 3], p[(seg.PointCount - 1) * 3 + 2], travelled, fx, fz, p[(seg.PointCount - 1) * 3 + 1]));
        return result;
    }

    private static double Length(RoadSegment seg)
    {
        double len = 0;
        var p = seg.Points;
        for (int i = 3; i < p.Length; i += 3)
            len += Math.Sqrt((p[i] - p[i - 3]) * (p[i] - p[i - 3]) + (p[i + 2] - p[i - 1]) * (p[i + 2] - p[i - 1]));
        return len;
    }

    /// <summary>
    /// <c>RoadGen --street-check</c>: one 6 m road through four stretches, synthetic houses
    /// (10 m boxes, walls only): houses 4 m off both edges, then front yards 10 m deep on one side,
    /// then open country; and a country road far from any house. Checks the widths, that no
    /// sidewalk reaches a wall, that the country road stays rural, and that the cuts are few.
    /// </summary>
    public static bool SelfCheck(Action<string> log)
    {
        var tile = new TileId(2600, 1200);
        bool ok = true;
        void Check(bool cond, string what) { log((cond ? "  ok    " : "  FAIL  ") + what); ok &= cond; }

        // tile-local: x east, z south; the road along z = 500, 6 m wide (edges z = 497 / 503)
        var buildings = new List<Building>();
        void House(double x0, double z0, double x1, double z1)
        {
            var t = new List<float>();
            void Wall(double ax, double az, double bx, double bz)
            {
                t.AddRange([(float)ax, 400, (float)az, (float)bx, 400, (float)bz, (float)bx, 408, (float)bz]);
                t.AddRange([(float)ax, 400, (float)az, (float)bx, 408, (float)bz, (float)ax, 408, (float)az]);
            }
            Wall(x0, z0, x1, z0); Wall(x1, z0, x1, z1); Wall(x1, z1, x0, z1); Wall(x0, z1, x0, z0);
            buildings.Add(new Building { Kind = BuildingKind.House, MinY = 400, MaxY = 408, Triangles = t.ToArray() });
        }
        for (double x = 100; x < 300; x += 16) { House(x, 483, x + 10, 493); House(x, 507, x + 10, 517); }  // 4 m off both edges
        for (double x = 300; x < 500; x += 16) House(x, 477, x + 10, 487);                               // 10 m yards, north only
        var facades = new Facades(id => id == tile ? new BuildingTile { Id = tile, Buildings = buildings } : null);

        RoadSegment Road(double z, double x0, double x1)
        {
            int n = (int)((x1 - x0) / 4) + 1;
            var pts = new float[n * 3];
            for (int i = 0; i < n; i++) (pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]) = ((float)(x0 + (x1 - x0) * i / (n - 1)), 400f, (float)z);
            return new RoadSegment { Class = RoadClass.Road, Surface = RoadSurface.Paved, Width = 6, Points = pts };
        }
        var street = Road(500, 100, 700);
        var country = Road(900, 100, 700);
        var obstacles = new Obstacles();
        obstacles.Add(street, tile, 3, 0, street: true);
        obstacles.Add(country, tile, 3, 1, street: true);
        var stats = new Stats();
        var planner = new StreetPlanner(facades, new UrbanField(facades), obstacles, stats);
        var pieces = planner.Plan(street, tile, 0, out bool urban);
        var rural = planner.Plan(country, tile, 1, out bool countryUrban);

        RoadAttributes At(double x) => pieces.First(p => p.Points[0] <= x && p.Points[^3] >= x).Attributes;
        // drawn west -> east: left is north (z smaller)
        var a = At(200);
        Check(a.Left.SidewalkDm == 35 && a.Right.SidewalkDm == 35 && a.Left.KerbCm == KerbCm,
            $"houses 4 m off both edges: 3.5 m kerbed sidewalks (got {a.Left.SidewalkDm / 10.0}/{a.Right.SidewalkDm / 10.0} m, kerb {a.Left.KerbCm} cm)");
        var b = At(400);
        Check(b.Left.SidewalkDm == 20 && b.Right.SidewalkDm == 20 && b.Has(RoadAttrFlags.Urban),
            $"front yards on one side: an urban street, 2 m sidewalks both sides (got {b.Left.SidewalkDm / 10.0}/{b.Right.SidewalkDm / 10.0} m)");
        var c = At(650);
        Check(c.Left.SidewalkDm == 0 && c.Right.SidewalkDm == 0 && !c.Has(RoadAttrFlags.Urban),
            $"open country past the last house: rural, no sidewalk (got {c.Left.SidewalkDm / 10.0}/{c.Right.SidewalkDm / 10.0} m)");
        Check(rural.Count == 1 && !countryUrban && rural[0].Attributes.Left.SidewalkDm == 0, "a country road far from any house stays one rural segment");
        Check(pieces.Count <= 4, $"cut into few pieces (got {pieces.Count})");
        Check(urban, "the street counts as urban for its paint");

        // no sidewalk reaches a wall: walk each sidewalk's outer edge
        bool clear = true;
        foreach (var p in pieces)
            foreach (bool right in (ReadOnlySpan<bool>)[false, true])
            {
                var side = right ? p.Attributes.Right : p.Attributes.Left;
                if (side.SidewalkDm == 0) continue;
                double off = 3 + side.SidewalkDm / 10.0, sign = right ? 1 : -1;
                for (int i = 0; i < p.PointCount; i++)
                    clear &= !facades.Occupied(tile.MinE + p.Points[i * 3], tile.MaxN - (p.Points[i * 3 + 2] + sign * off));
            }
        Check(clear, "no sidewalk edge stands on a wall");
        return ok;
    }

    /// <summary>
    /// The block's ground-level lines in LV95, in 16 m buckets: does another line's paved width
    /// cover a point. A line's own pieces count away from the station asking (the other leg of a
    /// hairpin does, the piece it stands on does not), as in <c>EmbankmentPlanner</c>.
    /// </summary>
    public sealed class Obstacles
    {
        private const double Cell = 16;
        private readonly Dictionary<(int, int), List<int>> _buckets = new();
        private readonly List<(double Ax, double Ay, double Bx, double By, double Half, int Owner, bool Footway, bool Carriageway, double OwnHalf)> _pieces = new();

        /// <summary>
        /// A plan-view line (LV95, X east, Y north) of half width <paramref name="half"/>. A
        /// street that may carry sidewalks itself (<paramref name="street"/>) keeps
        /// <see cref="MinSidewalk"/> clear beyond its edge: two sidewalks meeting at a junction
        /// stop short of each other, and the corner patch (<see cref="CornerPlanner"/>) joins them.
        /// </summary>
        /// <param name="footway">A path or track: it stops a sidewalk only where it crosses the street,
        /// not where it runs alongside (it is then the sidewalk, or a promenade beside it).</param>
        public void Add(IReadOnlyList<Vec2> line, double half, int owner, bool street = false, bool footway = false,
            bool carriageway = false)
        {
            double own = half;
            if (street) half += MinSidewalk;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                var (a, b) = (line[i], line[i + 1]);
                int k = _pieces.Count;
                _pieces.Add((a.X, a.Y, b.X, b.Y, half, owner, footway, carriageway, own));
                double pad = Math.Max(half, own) + 1;
                int c0 = (int)Math.Floor((Math.Min(a.X, b.X) - pad) / Cell), c1 = (int)Math.Floor((Math.Max(a.X, b.X) + pad) / Cell);
                int r0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - pad) / Cell), r1 = (int)Math.Floor((Math.Max(a.Y, b.Y) + pad) / Cell);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        if (!_buckets.TryGetValue((c, r), out var list)) _buckets[(c, r)] = list = new List<int>();
                        list.Add(k);
                    }
            }
        }

        /// <summary>A tile-local segment, as <see cref="Add(IReadOnlyList{Vec2}, double, int)"/>.</summary>
        public void Add(RoadSegment seg, TileId id, double half, int owner, bool street = false)
        {
            var line = new List<Vec2>(seg.PointCount);
            for (int i = 0; i < seg.PointCount; i++)
                line.Add(new Vec2(id.MinE + seg.Points[i * 3], id.MaxN - seg.Points[i * 3 + 2]));
            Add(line, half, owner, street);
        }

        /// <summary>
        /// Whether a carriageway or rail running alongside (within 30 deg of the street heading
        /// (fx, fy)) covers (x, y) with its own paved width.
        /// </summary>
        public bool ParallelCarriageway(double x, double y, int self, double fx, double fy)
        {
            if (!_buckets.TryGetValue(((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell)), out var list)) return false;
            foreach (int k in list)
            {
                var pc = _pieces[k];
                if (!pc.Carriageway || pc.Owner == self) continue;
                if (Distance(pc.Ax, pc.Ay, pc.Bx, pc.By, x, y) > pc.OwnHalf) continue;
                double dx = pc.Bx - pc.Ax, dy = pc.By - pc.Ay, l = Math.Sqrt(dx * dx + dy * dy);
                if (l > 1e-6 && Math.Abs((dx * fx + dy * fy) / l) > ParallelCos) return true;
            }
            return false;
        }

        /// <summary>Within 30 degrees of each other, either way.</summary>
        private const double ParallelCos = 0.866;

        /// <summary>
        /// Whether another line covers (x, y), seen from a station at (sx, sy) of a street heading
        /// (fx, fy) (LV95, unit): a footway running alongside does not count.
        /// </summary>
        public bool Covers(double x, double y, int self, double sx, double sy, double near, double fx, double fy)
        {
            if (!_buckets.TryGetValue(((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell)), out var list)) return false;
            foreach (int k in list)
            {
                var pc = _pieces[k];
                if (pc.Owner == self && Distance(pc.Ax, pc.Ay, pc.Bx, pc.By, sx, sy) < near) continue;
                if (Distance(pc.Ax, pc.Ay, pc.Bx, pc.By, x, y) > pc.Half) continue;
                if (pc.Footway)
                {
                    double dx = pc.Bx - pc.Ax, dy = pc.By - pc.Ay, l = Math.Sqrt(dx * dx + dy * dy);
                    if (l > 1e-6 && Math.Abs((dx * fx + dy * fy) / l) > ParallelCos) continue;
                }
                return true;
            }
            return false;
        }

        private static double Distance(double ax, double ay, double bx, double by, double x, double y)
        {
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1);
            double px = ax + dx * t - x, py = ay + dy * t - y;
            return Math.Sqrt(px * px + py * py);
        }
    }
}
