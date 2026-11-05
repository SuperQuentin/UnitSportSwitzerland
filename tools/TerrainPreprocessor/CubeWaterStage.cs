using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Lake and river beds on cube leaves (#1507, G2 B3 PR 6; docs/notes/tools/cube-water.md): <see cref="WaterStage"/>'s
/// pass over a region's leaves, its window maths shared (<see cref="WaterWindow"/>), its windows on a face's leaf
/// lattice instead of LV95's 1 m grid.
/// <list type="number">
/// <item><b>A</b>: every leaf's wet vertices (its <see cref="LeafKind.Cover"/> is Water) and its still surface, from the
/// heights as the <c>--cube</c> stage wrote them (kept in <c>&lt;temp&gt;/water_surface/</c> the first time, so a rerun
/// starts from them again, as the LV95 pass starts from its old <c>.water</c>), in centimetres above sea level;
/// a 4-connected flood fill per leaf.</item>
/// <item><b>B</b>: one union-find over the region, along every shared leaf edge on a face: bodies, their area (the
/// leaf's own vertices, each the leaf's area over its cells), their most common level, their maximum depth.</item>
/// <item><b>C</b>: frame by frame in the driver's order, each leaf with water (or with water before) in a window of
/// itself and <see cref="WaterWindow.Halo"/> of its face's lattice round it (the 3 × 3 leaves at level 14; a leaf on
/// another face or with no heights is unknown, as a missing LV95 tile), cells of the leaf's own spacing: the bank refine,
/// the distance to shore, the half width, the survey (swissBATHY3D at the cell's LV95 position) and its gap fill, the
/// depth. The bed goes into the leaf's <see cref="LeafKind.Heights"/> (the surface less the depth, ellipsoidal), the
/// level and fetch into its <see cref="LeafKind.Water"/>, through the <see cref="LeafWriter"/>'s heights and water seams.</item>
/// </list>
///
/// <code>
/// --cube-water --cube-out REGION [--bathy DIR] [--cube-temp DIR] [--jobs N]
/// </code>
/// </summary>
public static class CubeWaterStage
{
    /// <summary>The bank rise (<see cref="WaterWindow.BankProbeM"/>), in centimetres: the LV95 pass's 7 quanta of 7.2 cm.</summary>
    private const int BankRiseCm = 50;

    private const double Cm = 0.01;

    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-water");

    /// <summary>One leaf's input: its wet vertices, its surface in cm above sea level, its flood-fill labels.</summary>
    private sealed class LeafData
    {
        public required CubeKey Key;
        public required int Cells;
        public required byte[] Wet;
        public required int[] SurfaceCm;
        public int[] Labels = Array.Empty<int>();
        public int Count;
        public int LabelBase;
        public double VertexAreaM2;
        public double CellM;
        public bool HadWater;
    }

    private sealed class Body
    {
        public long Count;
        public double AreaM2;
        public readonly Dictionary<int, long> LevelCounts = new();
        public double SurveyLevel, MaxDepth;
        public double OutMaxDepth, OutDepthSum;
        public long OutCount, Surveyed, Filled;
        public CubeKey First;

        public double ModeLevel()
        {
            int best = 0; long n = -1;
            foreach (var (q, k) in LevelCounts)
                if (k > n || (k == n && q < best)) { best = q; n = k; }
            return best * Cm;
        }
    }

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? region = null, temp = null, bathyDir = null;
        int jobs = Math.Max(1, Environment.ProcessorCount / 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-water": break;
                case "--cube-out": region = args[++i]; break;
                case "--cube-temp": temp = args[++i]; break;
                case "--bathy": bathyDir = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"--cube-water: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null) { Console.Error.WriteLine("--cube-water needs --cube-out REGION (with its heights and cover)"); return 2; }
        temp ??= region.TrimEnd('/', '\\') + "_temp";
        var clock = Stopwatch.StartNew();
        var heights = new RegionHeights(region);
        var leaves = heights.Leaves.ToHashSet();
        var surfaceStore = new LeafStore(Path.Combine(temp, "water_surface"), heights.FrameLevel);
        BathySource? bathy = bathyDir != null ? new BathySource(bathyDir) : null;
        Console.WriteLine(bathy != null ? $"swissBATHY3D: {bathy.TileCount} survey km ({string.Join(", ", bathy.Lakes)})" : "No --bathy: every bed is synthetic");
        var par = new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct };

        // ---- A: wet vertices, surfaces, labels ------------------------------------------------------
        var data = new ConcurrentDictionary<CubeKey, LeafData>();
        Parallel.ForEach(leaves, par, key =>
        {
            if (Load(region, heights, surfaceStore, key) is { } d) data[key] = d;
        });
        Console.WriteLine($"cube water: {data.Count} of {leaves.Count} leaves hold water (or did) [{clock.Elapsed.TotalSeconds:F1}s]");
        if (data.IsEmpty) return 0;

        // ---- B: bodies over the region ----------------------------------------------------------------
        var ordered = data.Keys.OrderBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X).ToList();
        int total = 0;
        foreach (var k in ordered) { data[k].LabelBase = total; total += data[k].Count; }
        var parent = new int[total + 1];
        for (int i = 0; i <= total; i++) parent[i] = i;
        int Global(LeafData d, int idx) => d.Labels[idx] == 0 ? 0 : d.LabelBase + d.Labels[idx];
        foreach (var k in ordered)
        {
            var d = data[k];
            int n = d.Cells, side = n + 1;
            if (k.Neighbour(1, 0) is { } east && east.Face == k.Face && data.TryGetValue(east, out var de))
                for (int j = 0; j <= n; j++) Union(parent, Global(d, j * side + n), Global(de, j * side));
            if (k.Neighbour(0, 1) is { } north && north.Face == k.Face && data.TryGetValue(north, out var dn))
                for (int i = 0; i <= n; i++) Union(parent, Global(d, n * side + i), Global(dn, i));
        }
        var bodies = new Dictionary<int, Body>();
        foreach (var k in ordered)
        {
            var d = data[k];
            int n = d.Cells, side = n + 1;
            for (int j = 0; j < n; j++)          // a leaf owns its vertices i, j < cells
                for (int i = 0; i < n; i++)
                {
                    int l = d.Labels[j * side + i];
                    if (l == 0) continue;
                    int root = Find(parent, d.LabelBase + l);
                    if (!bodies.TryGetValue(root, out var b)) bodies[root] = b = new Body { First = k };
                    b.Count++;
                    b.AreaM2 += d.VertexAreaM2;
                    int q = d.SurfaceCm[j * side + i];
                    b.LevelCounts[q] = b.LevelCounts.GetValueOrDefault(q) + 1;
                }
        }
        foreach (var b in bodies.Values)
        {
            b.MaxDepth = WaterBed.MaxDepthForArea(b.AreaM2);
            b.SurveyLevel = b.ModeLevel();
        }
        var rootOf = new int[total + 1];
        var maxDepthOf = new double[total + 1];
        var sqrtAreaOf = new double[total + 1];
        var surveyLevelOf = new double[total + 1];
        for (int i = 1; i <= total; i++)
        {
            rootOf[i] = Find(parent, i);
            surveyLevelOf[i] = double.NaN;
            if (bodies.TryGetValue(rootOf[i], out var b))
            {
                maxDepthOf[i] = b.MaxDepth;
                sqrtAreaOf[i] = Math.Sqrt(b.AreaM2);
                if (b.AreaM2 >= WaterWindow.MinSurveyAreaM2) surveyLevelOf[i] = b.SurveyLevel;
            }
            else maxDepthOf[i] = WaterBed.MinMaxDepthM;
        }
        Console.WriteLine($"  {bodies.Count} water bodies, {bodies.Values.Sum(b => b.AreaM2) / 1e6:F1} km² [{clock.Elapsed.TotalSeconds:F1}s]");

        // ---- C: beds and levels, frame by frame -----------------------------------------------------------
        var driver = new FrameDriver(leaves, 0, SwissSeaLevel.Instance, heights.FrameLevel);
        var writer = new LeafWriter(region, driver.FrameLevel, [LeafKind.Heights, LeafKind.Water], [LatticeKind.Heights, LatticeKind.Water]);
        var bodyLock = new object();
        long seamSamples = 0, seamCopied = 0, wetAll = 0, surveyedAll = 0, filledAll = 0;
        int written = 0;
        foreach (var plan in driver.Frames)
        {
            ct.ThrowIfCancellationRequested();
            var todo = plan.Core.Where(data.ContainsKey).ToList();
            if (todo.Count == 0) continue;
            var fs = Stopwatch.StartNew();
            var results = new ConcurrentDictionary<CubeKey, List<LeafSection>>();
            Parallel.ForEach(todo, par, key =>
            {
                var r = Solve(key, data, heights, surfaceStore, bathy, rootOf, maxDepthOf, sqrtAreaOf, surveyLevelOf);
                results[key] = r.Sections;
                lock (bodyLock)
                    foreach (var (root, st) in r.Stats)
                    {
                        if (!bodies.TryGetValue(root, out var b)) continue;
                        b.OutCount += st.Count; b.OutDepthSum += st.DepthSum; b.OutMaxDepth = Math.Max(b.OutMaxDepth, st.MaxDepth);
                        b.Surveyed += st.Surveyed; b.Filled += st.Filled;
                        wetAll += st.Count; surveyedAll += st.Surveyed; filledAll += st.Filled;
                    }
            });
            // the surfaces the beds are cut from, kept the first time (a rerun cuts from them again)
            foreach (var k in todo.Where(k => data[k].HadWater && !surfaceStore.Has(k))) Console.WriteLine($"  warning: {k} has water but no kept surface: its bed is cut from its current heights");
            surfaceStore.Keep(plan.Cell, todo.Where(k => !surfaceStore.Has(k)).ToDictionary(k => k, k => heights.ReadSection(k)!));
            // every other leaf of the bundle keeps its heights as they are (a stage's kinds are replaced in every entry)
            var sections = heights.AllHeightSections(plan.Cell);
            foreach (var (k, list) in results) sections[k] = list;
            var seams = writer.WriteFrame(plan.Cell, sections);
            heights.Reload(plan.Cell);
            foreach (var st in seams) { seamSamples += st.Samples; seamCopied += st.Copied; }
            written += todo.Count;
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {todo.Count} leaves with water, {fs.Elapsed.TotalSeconds:F1} s; "
                + string.Join(", ", seams.Select(st => $"{st.Kind} seam {st.Copied} of {st.Samples} copied ({st.CopiedAcrossFrames} across frames)"))
                + $"; {clock.Elapsed.TotalSeconds:F0} s");
        }
        var manifestPath = Path.Combine(region, CubeManifest.FileName);
        var sources = File.Exists(manifestPath) ? CubeManifest.FromJson(File.ReadAllText(manifestPath)).Sources : new List<string>();
        string source = bathy != null ? "swissBATHY3D lake beds, synthetic beds elsewhere (WaterBed)" : "synthetic lake and river beds (WaterBed)";
        if (!sources.Contains(source)) sources.Add(source);
        LeafWriter.WriteManifest(region, driver.LeafLevel, driver.FrameLevel, sources);

        Console.WriteLine("Water bodies (area >= 0.02 km²), bed depth below the still level:");
        Console.WriteLine($"  {"first leaf",-18} {"km²",8} {"level",8} {"max",7} {"mean",6} {"survey",7} {"fill",6}");
        foreach (var b in bodies.Values.Where(b => b.AreaM2 >= 20_000).OrderByDescending(b => b.AreaM2))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {b.First,-18} {b.AreaM2 / 1e6,8:F2} {b.SurveyLevel,8:F2} {b.OutMaxDepth,6:F1}m {(b.OutCount > 0 ? b.OutDepthSum / b.OutCount : 0),5:F1}m {100.0 * b.Surveyed / Math.Max(1, b.OutCount),6:F1}% {100.0 * b.Filled / Math.Max(1, b.OutCount),5:F1}%"));
        Console.WriteLine($"  + {bodies.Values.Count(b => b.AreaM2 < 20_000)} smaller bodies; {wetAll:N0} wet vertices: {100.0 * surveyedAll / Math.Max(1, wetAll):F1}% surveyed, "
            + $"{100.0 * filledAll / Math.Max(1, wetAll):F1}% gap fill, the rest synthetic");
        Console.WriteLine($"cube water: {written} leaves written, seams {seamCopied:N0} of {seamSamples:N0} shared samples copied from their owner; "
            + $"{clock.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return 0;
    }

    // ---- A ---------------------------------------------------------------------------------------------

    private static LeafData? Load(string region, RegionHeights heights, LeafStore store, CubeKey key)
    {
        var leaf = heights.ReadLeaf(key);
        if (leaf?.Find(LeafKind.Heights) is not { } hs) return null;
        var cover = leaf.Find(LeafKind.Cover) is { } cs ? LeafCover.Decode(cs) : null;
        bool hadWater = leaf.Find(LeafKind.Water) != null;
        bool any = false;
        var tile = store.Read(key) is { } kept ? LeafHeights.Decode(kept) : LeafHeights.Decode(hs);
        int side = tile.Side, n = side - 1;
        var wet = new byte[side * side];
        if (cover != null)
            for (int i = 0; i < wet.Length; i++)
                if (cover[i] == (byte)CoverClass.Water) { wet[i] = 1; any = true; }
        if (!any && !hadWater) return null;
        var t = new LeafTransform(new WorkFrame(key.AncestorAt(heights.FrameLevel), SwissSeaLevel.Instance), key, tile.Bits);
        var surface = new int[side * side];
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float h = tile.Heights[j * side + i];
                surface[j * side + i] = float.IsNaN(h) ? int.MinValue : (int)Math.Round((h - t.UndulationAt(i, j)) / Cm);
                if (float.IsNaN(h)) wet[j * side + i] = 0;
            }
        // the leaf's spacing and a vertex's area: its corners on the ellipsoid
        var p00 = key.Vertex(tile.Bits, 0, 0, 0); var p10 = key.Vertex(tile.Bits, n, 0, 0); var p01 = key.Vertex(tile.Bits, 0, n, 0);
        double su = p00.DistanceTo(p10) / n, sv = p00.DistanceTo(p01) / n;
        var d = new LeafData { Key = key, Cells = n, Wet = wet, SurfaceCm = surface, HadWater = hadWater, VertexAreaM2 = su * sv, CellM = 0.5 * (su + sv) };
        Label(d);
        return d;
    }

    private static void Label(LeafData d)
    {
        int side = d.Cells + 1;
        var labels = new int[side * side];
        var stack = new Stack<int>();
        int next = 0;
        for (int start = 0; start < labels.Length; start++)
        {
            if (d.Wet[start] == 0 || labels[start] != 0) continue;
            labels[start] = ++next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int r = i / side, c = i % side;
                if (c > 0) Visit(i - 1);
                if (c < d.Cells) Visit(i + 1);
                if (r > 0) Visit(i - side);
                if (r < d.Cells) Visit(i + side);
            }
        }
        d.Labels = labels;
        d.Count = next;

        void Visit(int j)
        {
            if (d.Wet[j] == 0 || labels[j] != 0) return;
            labels[j] = next;
            stack.Push(j);
        }
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        if (a == 0 || b == 0) return;
        a = Find(parent, a);
        b = Find(parent, b);
        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
    }

    // ---- C ---------------------------------------------------------------------------------------------

    private sealed class Stats
    {
        public long Count, Surveyed, Filled;
        public double DepthSum, MaxDepth;
    }

    private sealed record Result(List<LeafSection> Sections, Dictionary<int, Stats> Stats);

    private static Result Solve(CubeKey key, IReadOnlyDictionary<CubeKey, LeafData> data, RegionHeights heights, LeafStore store,
        BathySource? bathy, int[] rootOf, double[] maxDepthOf, double[] sqrtAreaOf, double[] surveyLevelOf)
    {
        var me = data[key];
        int n = me.Cells, side = n + 1;
        double cellM = me.CellM;
        int halo = WaterWindow.Cells(WaterWindow.Halo, cellM);
        int ring = (halo + n - 1) / n;
        int w = side + 2 * halo;
        var state = new byte[w * w];
        var surfaceQ = new int[w * w];
        var label = new int[w * w];
        Array.Fill(state, (byte)2);
        long gx0 = (long)key.X * n - halo, gy0 = (long)key.Y * n - halo;   // window cell (x, y) is face lattice vertex (gx0 + x, gy0 + y)

        // (y, x) order: a vertex two leaves share is written by the same leaf in every window
        for (int dy = -ring; dy <= ring; dy++)
            for (int dx = -ring; dx <= ring; dx++)
            {
                var nb = new CubeKey(key.Face, key.Level, key.X + dx, key.Y + dy);
                if (!nb.IsValid || !heights.Has(nb)) continue;
                data.TryGetValue(nb, out var nd);
                long ox = (long)nb.X * n - gx0, oy = (long)nb.Y * n - gy0;
                for (int j = 0; j <= n; j++)
                {
                    long y = oy + j;
                    if ((ulong)y >= (ulong)w) continue;
                    for (int i = 0; i <= n; i++)
                    {
                        long x = ox + i;
                        if ((ulong)x >= (ulong)w) continue;
                        int wi = (int)(y * w + x), li = j * side + i;
                        if (nd != null && nd.Wet[li] != 0)
                        {
                            state[wi] = 1;
                            surfaceQ[wi] = nd.SurfaceCm[li];
                            label[wi] = nd.LabelBase + nd.Labels[li];
                        }
                        else state[wi] = 0;
                    }
                }
            }

        WaterWindow.RefineWet(state, surfaceQ, w, WaterWindow.Cells(WaterWindow.BankProbeM, cellM), BankRiseCm);
        var land = new bool[w * w];
        for (int i = 0; i < land.Length; i++) land[i] = state[i] == 0;
        var ds = WaterWindow.Distances(land, w, cellM);

        float[]? survey = null, dv = null, edgeDepth = null;
        if (bathy != null && NearSurvey(key, bathy))
        {
            survey = new float[w * w];
            var valid = new bool[w * w];
            bool anyValid = false;
            var lv95 = new Lv95Lattice(key.Face, key.Level + CubeKey.LatticeBits(key.Level), gx0, gy0, w);
            for (int y = 0; y < w; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    survey[i] = float.NaN;
                    if (state[i] != 1) continue;
                    double lakeLevel = surveyLevelOf[label[i]];
                    double surf = surfaceQ[i] * Cm;
                    if (!(Math.Abs(surf - lakeLevel) <= WaterWindow.SurveyLevelToleranceM)) continue;
                    var (e, nn) = lv95.At(x, y);
                    double bed = bathy.SampleBed(e, nn);
                    if (double.IsNaN(bed)) continue;
                    survey[i] = (float)Math.Max(0, surf - bed);
                    valid[i] = anyValid = true;
                }
            if (anyValid)
            {
                var site = new int[w * w];
                dv = WaterWindow.Distances(valid, w, cellM, site);
                edgeDepth = WaterWindow.SmoothEdgeDepth(survey, state, dv, site, w, cellM);
            }
            else survey = null;
        }

        // cut from the surface the leaf was built with (kept from the first run)
        var leaf = heights.ReadLeaf(key)!;
        var now = LeafHeights.Decode(leaf.Find(LeafKind.Heights)!);
        var surfaceTile = store.Read(key) is { } kept ? LeafHeights.Decode(kept) : now;
        var bedH = (float[])surfaceTile.Heights.Clone();
        var water = CubeWater.Dry(surfaceTile.Bits);
        int ws = water.Side;
        var stats = new Dictionary<int, Stats>();
        bool anyWet = false;
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                int x = halo + i, y = halo + j, wi = y * w + x, li = j * side + i;
                if (state[wi] != 1) continue;
                anyWet = true;
                double dShore = Math.Min(ds[wi], (float)WaterWindow.DistanceCapM);
                int gl = label[wi];
                double half = WaterWindow.HalfWidth(ds, w, x, y);
                var (d, kind) = WaterWindow.Depth(dShore, half, maxDepthOf[gl], survey, dv, edgeDepth, wi);
                float surfEll = surfaceTile.Heights[li];
                bedH[li] = (float)(surfEll - d);
                if (i % CubeWater.Stride == 0 && j % CubeWater.Stride == 0)
                {
                    int si = (j / CubeWater.Stride) * ws + i / CubeWater.Stride;
                    water.Level[si] = surfEll;
                    double fetchM = double.IsPositiveInfinity(half) ? sqrtAreaOf[gl] : Math.Min(sqrtAreaOf[gl], 2 * half);
                    water.Fetch[si] = WaterFormat.QuantizeFetch(fetchM);
                }
                if (i == n || j == n) continue;   // statistics on the vertices this leaf owns
                if (!stats.TryGetValue(rootOf[gl], out var st)) stats[rootOf[gl]] = st = new Stats();
                st.Count++;
                st.DepthSum += d;
                st.MaxDepth = Math.Max(st.MaxDepth, d);
                if (kind == 1) st.Surveyed++;
                if (kind == 2) st.Filled++;
            }
        var tile = new CubeTile { Key = key, Bits = now.Bits, Heights = bedH, Quantum = now.Quantum, Roads = now.Roads, Buildings = now.Buildings };
        var sections = new List<LeafSection> { LeafHeights.Encode(tile) };
        if (anyWet) sections.Add(LeafWater.Encode(water));
        return new Result(sections, stats);
    }

    /// <summary>Whether a survey kilometre lies under the leaf or within a kilometre of it.</summary>
    private static bool NearSurvey(CubeKey key, BathySource bathy)
    {
        var c = key.Centre;
        var (e, n) = SwissGeodesy.ToLv95(c.LatDeg, c.LonDeg);
        var t = TileId.FromLv95(e, n);
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (bathy.Covers(new TileId(t.E + dx, t.N + dy))) return true;
        return false;
    }

    /// <summary>
    /// LV95 positions of a window's face lattice vertices, rigorous every <see cref="Step"/> cells and bilinear between
    /// (the projection is smooth: under a millimetre over 8 cells), at the Plateau's height.
    /// </summary>
    private sealed class Lv95Lattice
    {
        private const int Step = 8;
        private readonly double[] _e, _n;
        private readonly int _side;

        public Lv95Lattice(int face, int latticeLevel, long gx0, long gy0, int w)
        {
            _side = (w + Step - 1) / Step + 1;
            _e = new double[_side * _side];
            _n = new double[_side * _side];
            for (int b = 0; b < _side; b++)
                for (int a = 0; a < _side; a++)
                {
                    var p = CubeSphere.LatticeEcef(face, latticeLevel, gx0 + a * Step, gy0 + b * Step, SwissGeodesy.GroundH);
                    (_e[b * _side + a], _n[b * _side + a], _) = SwissGeodesy.FromEcef(p);
                }
        }

        public (double E, double N) At(int x, int y)
        {
            int a = x / Step, b = y / Step;
            double fa = (x - a * Step) / (double)Step, fb = (y - b * Step) / (double)Step;
            int k = b * _side + a;
            double Lerp(double[] v) => (v[k] * (1 - fa) + v[k + 1] * fa) * (1 - fb) + (v[k + _side] * (1 - fa) + v[k + _side + 1] * fa) * fb;
            return (Lerp(_e), Lerp(_n));
        }
    }
}

/// <summary>A temporary <c>.cubl</c> region of one kind of section a stage keeps per leaf (the water stage's surfaces).</summary>
public sealed class LeafStore(string dir, int frameLevel)
{
    private readonly object _lock = new();

    private string PathOf(CubeKey frame) => Path.Combine(dir, CubeBundle.LeafFileName(frame));

    public bool Has(CubeKey leaf) => Read(leaf) != null;

    public LeafSection? Read(CubeKey leaf)
    {
        string path = PathOf(leaf.AncestorAt(frameLevel));
        lock (_lock)
            return File.Exists(path) && CubeBundle.ReadLeaf(path, leaf) is { } l ? l.Find(LeafKind.Heights) : null;
    }

    /// <summary>Adds leaves' sections to a frame's bundle, keeping the ones already there.</summary>
    public void Keep(CubeKey frame, Dictionary<CubeKey, LeafSection> add)
    {
        if (add.Count == 0) return;
        lock (_lock)
        {
            Directory.CreateDirectory(dir);
            string path = PathOf(frame);
            var all = new Dictionary<CubeKey, List<LeafSection>>();
            if (File.Exists(path))
                foreach (var (k, bytes) in CubeBundle.ReadAllEntries(path))
                    if (CubeLeafCodec.ReadSection(bytes, LeafKind.Heights) is { } s) all[k] = [s];
            foreach (var (k, s) in add) all.TryAdd(k, [s]);
            CubeBundle.UpdateLeaves(path, frame, all, [LeafKind.Heights]);
        }
    }
}
