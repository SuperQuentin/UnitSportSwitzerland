using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Lake and river beds (#298). swissALTI3D models water as its flat surface, so before this pass
/// the terrain at a Water cover vertex IS the water level. This pass moves that level into the
/// tile's <c>.water</c> layer (<see cref="WaterFormat"/>) and writes the bed into the <c>.terr</c>
/// heights instead (then the <c>.terrc</c> companion, the manifest's min/max and horizon.bin), so
/// collision, the cliffs and the far horizon all see the bed.
///
/// <para>
/// <b>Bed</b>: the swissBATHY3D survey where there is one (<see cref="BathySource"/>), else
/// <see cref="WaterBed"/>'s synthetic profile from the distance to the shore, the local width and
/// the body's maximum depth (from its area). Survey gaps inside a surveyed lake (swissBATHY3D
/// covers Léman's French part only below ~5 m) are filled from the shore out to the survey's edge,
/// <c>depth = Dedge · ds / (ds + dv)</c>, fading to the synthetic profile past
/// <see cref="FillReachM"/> from the survey. Every depth is at most the distance to the shore
/// (1:1), so the bed meets the bank flush.
/// </para>
///
/// <para>
/// <b>Seams</b>: every value is a function of the region's data around the vertex, never of the
/// tile it is computed in. Each tile is computed in a window of itself plus <see cref="Halo"/>
/// metres of its neighbours, distances are exact Euclidean transforms capped at the halo, and
/// water bodies (for the maximum depth) are labelled across the whole region first; so a vertex
/// two tiles share gets bit-identical heights in both (checked by <see cref="TerrainBuild.VerifySeams"/>).
/// </para>
///
/// <para>
/// <b>Re-runnable</b>: the surface is read back from an existing <c>.water</c> where there is
/// one, so running the pass again (or after a cover change) recomputes the bed from the original
/// level instead of digging the old bed deeper; a vertex that was wet and no longer is gets its
/// level back as terrain. The <c>.water</c> is written before the <c>.terr</c>, so an interrupted
/// run is safe to repeat.
/// </para>
/// </summary>
public static class WaterStage
{
    /// <summary>
    /// Metres of neighbouring tiles each tile is computed with. Everything a core vertex reads must
    /// be exact inside it: the gap fill reads the smoothed edge depth up to 2·<see cref="EdgeSmoothM"/>
    /// out, whose nearest survey vertex may be <see cref="FillReachM"/> + 2·EdgeSmoothM further
    /// (482 m), so 600; the distance to the shore saturates at <see cref="DistanceCapM"/>.
    /// </summary>
    public const int Halo = 600;

    /// <summary>
    /// Distances to the shore saturate here (deep enough for every body's maximum but the biggest
    /// lakes', which are surveyed), well short of the halo: a window's outermost
    /// <see cref="BankProbe"/> cells are refined from a truncated neighbourhood, so a shore site
    /// there must never decide a core vertex's distance.
    /// </summary>
    private const int DistanceCapM = 490;

    /// <summary>
    /// A Water cover vertex whose swissALTI3D surface stands more than <see cref="BankRiseQ"/>
    /// quanta (0.5 m) above the lowest wet surface within <see cref="BankProbe"/> vertices is on
    /// the bank, not in the water: the TLM polygon's edge lies on the dyke or beach slope (the
    /// Rhône at Martigny has 468.2 m on its bank vertex over 466.8 m water), and taking its height
    /// as the level drew the surface climbing the bank. It stays dry ground.
    /// </summary>
    private const int BankProbe = 4, BankRiseQ = 7;

    private static void RefineWet(byte[] state, ushort[] surfaceQ, int w)
    {
        // separable min of the wet surface over a (2·BankProbe+1)² square, in quanta (exact)
        var rowMin = new ushort[w * w];
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                ushort m = ushort.MaxValue;
                for (int dx = Math.Max(0, x - BankProbe); dx <= Math.Min(w - 1, x + BankProbe); dx++)
                {
                    int i = y * w + dx;
                    if (state[i] == 1 && surfaceQ[i] < m) m = surfaceQ[i];
                }
                rowMin[y * w + x] = m;
            }
        var dry = new List<int>();
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (state[i] != 1) continue;
                ushort m = ushort.MaxValue;
                for (int dy = Math.Max(0, y - BankProbe); dy <= Math.Min(w - 1, y + BankProbe); dy++)
                    m = Math.Min(m, rowMin[dy * w + x]);
                if (surfaceQ[i] > m + BankRiseQ) dry.Add(i);
            }
        foreach (int i in dry) state[i] = 0;
    }

    /// <summary>How far from the survey a gap is still filled toward the survey's edge depth.</summary>
    public const double FillReachM = 400;

    /// <summary>Stop climbing toward the middle of the water past this distance: open water.</summary>
    private const double WidthProbeM = WaterBed.OpenWidthM / 2 + 10;

    /// <summary>
    /// The survey only counts in a body this large, at vertices within
    /// <see cref="SurveyLevelToleranceM"/> of the body's mean level: swissBATHY3D's grid reaches up
    /// the streams around Nyon, 2-10 m above the lake, where it would dig a 3 m pit in a brook.
    /// </summary>
    private const double MinSurveyAreaM2 = 200_000, SurveyLevelToleranceM = 1.0;

    /// <summary>Max depth per metre from the shore (1:1): a quay in the survey becomes a 45° bank.</summary>
    private const double MaxBankSlope = 1.0;

    private const int S = ChunkFormat.GridSize;   // 1001
    private const int Q = S - 1;                  // 1000 vertices a tile owns per side

    public sealed class Options
    {
        public int Jobs = Environment.ProcessorCount;
        public string? BathyDir;
        public string? PngDir;
        /// <summary>Where the per-tile input is cached between the passes; null = <c>&lt;out&gt;_temp</c>.</summary>
        public string? TempDir;
        /// <summary>Extra 1 m/px crops for the PNG check: LV95 centre E,N and side in metres.</summary>
        public List<(double E, double N, int Size)> Crops = new();
    }

    /// <summary>
    /// What phase B needs of a tile once its arrays are gone: its label count and the labels on its
    /// four edges, where water bodies are joined to the neighbours'.
    /// </summary>
    private sealed record Edges(int Count, int[] Top, int[] Bottom, int[] Left, int[] Right);

    /// <summary>One tile's input: which vertices are water, and the surface (still level) there.</summary>
    private sealed class TileData
    {
        public required TileId Id;
        public required byte[] Wet;          // 1 = Water cover
        public required ushort[] Surface;    // the flat surface: old .water level, else the terrain
        public required bool HadWater;       // an old .water exists
        public int[] Labels = Array.Empty<int>();   // local body label per vertex, 0 = dry
        public int LabelBase;
    }

    private sealed class Body
    {
        public long Count;
        public double LevelSum;
        public int MinE = int.MaxValue, MaxE = int.MinValue, MinN = int.MaxValue, MaxN = int.MinValue;
        public double MaxDepth, AreaM2;
        // results
        public double OutMaxDepth, OutDepthSum;
        public long OutCount, Surveyed, Filled;
        public readonly Dictionary<string, long> Lakes = new();
    }

    public static int Run(string outDir, Options o, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        string manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"No manifest.json in {outDir}");
            return 2;
        }
        var manifest = TerrainManifest.FromJson(File.ReadAllText(manifestPath));
        var region = manifest.Tiles.Select(t => t.Id).ToHashSet();

        BathySource? bathy = null;
        if (o.BathyDir != null)
        {
            if (!Directory.Exists(o.BathyDir))
            {
                Console.Error.WriteLine($"--bathy: no directory {o.BathyDir}");
                return 2;
            }
            bathy = new BathySource(o.BathyDir);
            Console.WriteLine($"swissBATHY3D: {bathy.TileCount} survey km in {o.BathyDir} ({string.Join(", ", bathy.Lakes)})");
        }
        else Console.WriteLine("No --bathy: every bed is synthetic");

        // ---- A: which tiles hold water, and their surface ---------------------------------------
        // Holding every water tile's arrays at once is 7 MB a tile: 17,180 tiles nationwide, over
        // 120 GB (#747). So each tile is read and labelled once, its input cached to disk, and only
        // its label count and edge labels kept; phases B and C read the cache back as they go.
        string cacheDir = Path.Combine(o.TempDir ?? outDir.TrimEnd('/', '\\') + "_temp", "water_cache");
        Directory.CreateDirectory(cacheDir);
        var edges = new ConcurrentDictionary<TileId, Edges>();
        Parallel.ForEach(region, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs, CancellationToken = ct }, id =>
        {
            var td = LoadTile(outDir, id);
            if (td == null) return;
            int n = LabelTile(td);
            WriteCache(cacheDir, td);
            edges[id] = EdgesOf(td, n);
        });
        Console.WriteLine($"Water pass: {edges.Count} of {region.Count} tiles hold water (or did) "
            + $"[{clock.Elapsed.TotalSeconds:F1}s]");
        if (edges.IsEmpty) return 0;

        // ---- B: water bodies across the region (union of per-tile flood fills along the seams) ---
        var ordered = edges.Keys.OrderBy(t => t.E).ThenBy(t => t.N).ToList();
        var labelBase = new Dictionary<TileId, int>(ordered.Count);
        int total = 0;
        foreach (var id in ordered)
        {
            labelBase[id] = total;
            total += edges[id].Count;
        }
        var parent = new int[total + 1];
        for (int i = 0; i <= total; i++) parent[i] = i;
        int GlobalAt(TileId id, int label) => label == 0 ? 0 : labelBase[id] + label;
        foreach (var id in ordered)
        {
            var te = edges[id];
            var eastId = new TileId(id.E + 1, id.N);
            if (edges.TryGetValue(eastId, out var east))
                for (int r = 0; r < S; r++)
                    Union(parent, GlobalAt(id, te.Right[r]), GlobalAt(eastId, east.Left[r]));
            var southId = new TileId(id.E, id.N - 1);
            if (edges.TryGetValue(southId, out var south))
                for (int c = 0; c < S; c++)
                    Union(parent, GlobalAt(id, te.Bottom[c]), GlobalAt(southId, south.Top[c]));
        }
        // in the same tile, row and column order as ever, so the level sums add up bit for bit;
        // a batch is read in parallel, then summed in order
        var bodies = new Dictionary<int, Body>();
        int batch = Math.Max(1, 4 * o.Jobs);
        for (int from = 0; from < ordered.Count; from += batch)
        {
            var ids = ordered.GetRange(from, Math.Min(batch, ordered.Count - from));
            var loaded = new TileData[ids.Count];
            Parallel.For(0, ids.Count, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs, CancellationToken = ct },
                i => loaded[i] = ReadCache(cacheDir, ids[i], labelBase[ids[i]]));
            foreach (var td in loaded)
            {
                var id = td.Id;
                for (int r = 1; r < S; r++)          // a tile owns rows 1..1000 and columns 0..999
                    for (int c = 0; c < Q; c++)
                    {
                        int l = td.Labels[r * S + c];
                        if (l == 0) continue;
                        int root = Find(parent, td.LabelBase + l);
                        if (!bodies.TryGetValue(root, out var b)) bodies[root] = b = new Body();
                        b.Count++;
                        b.LevelSum += ChunkFormat.Dequantize(td.Surface[r * S + c]);
                        int e = id.E * 1000 + c, nn = (id.N + 1) * 1000 - r;
                        if (e < b.MinE) b.MinE = e;
                        if (e > b.MaxE) b.MaxE = e;
                        if (nn < b.MinN) b.MinN = nn;
                        if (nn > b.MaxN) b.MaxN = nn;
                    }
            }
        }
        foreach (var b in bodies.Values)
        {
            b.AreaM2 = b.Count * ChunkFormat.SpacingM * ChunkFormat.SpacingM;
            b.MaxDepth = WaterBed.MaxDepthForArea(b.AreaM2);
        }
        // per global label, read-only from here on (Find compresses paths, so not in parallel)
        var maxDepthOf = new double[total + 1];
        var sqrtAreaOf = new double[total + 1];
        var rootOf = new int[total + 1];
        var surveyLevelOf = new double[total + 1];   // NaN: the survey is not used for this body
        for (int i = 1; i <= total; i++)
        {
            rootOf[i] = Find(parent, i);
            surveyLevelOf[i] = double.NaN;
            if (bodies.TryGetValue(rootOf[i], out var b))
            {
                maxDepthOf[i] = b.MaxDepth;
                sqrtAreaOf[i] = Math.Sqrt(b.AreaM2);
                if (b.AreaM2 >= MinSurveyAreaM2) surveyLevelOf[i] = b.LevelSum / b.Count;
            }
            else maxDepthOf[i] = WaterBed.MinMaxDepthM;   // a body that only touches seams it does not own
        }
        Console.WriteLine($"  {bodies.Count} water bodies, {bodies.Values.Sum(b => b.AreaM2) / 1e6:F1} km² "
            + $"[{clock.Elapsed.TotalSeconds:F1}s]");

        // ---- C: bed and level per tile ----------------------------------------------------------
        // Column by column, west to east: a tile's window reads the columns either side of it, so
        // only three columns of cached input are held at a time. Every read comes from the cache,
        // never from the files this phase is rewriting.
        var changed = new ConcurrentBag<ManifestTile>();
        var joinSteps = new ConcurrentBag<float>();
        var worstJoin = (Step: 0f, E: 0.0, N: 0.0);
        var bodyLock = new object();
        int done = 0;
        long surveyedAll = 0, filledAll = 0, wetAll = 0;
        var held = new ConcurrentDictionary<TileId, Lazy<TileData>>();
        TileData? Input(TileId id) => labelBase.TryGetValue(id, out int lb)
            ? held.GetOrAdd(id, k => new Lazy<TileData>(() => ReadCache(cacheDir, k, lb))).Value
            : null;
        foreach (var column in ordered.GroupBy(t => t.E))
        {
            foreach (var key in held.Keys)
                if (key.E < column.Key - 1) held.TryRemove(key, out _);
            Parallel.ForEach(column, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs, CancellationToken = ct }, id =>
            {
                var r = ComputeTile(outDir, id, Input, region, bathy, rootOf, maxDepthOf, sqrtAreaOf, surveyLevelOf);

                // the level first: a run interrupted between the two files repeats safely
                string waterPath = Path.Combine(outDir, WaterFormat.FileName(id));
                if (r.Layer != null) AtomicFile.Write(waterPath, s => WaterFormat.Encode(r.Layer, s));
                else if (File.Exists(waterPath)) File.Delete(waterPath);

                AtomicFile.Write(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)), s => ChunkCodec.Encode(r.Grid, s));
                AtomicFile.Write(Path.Combine(outDir, ChunkFormat.CoarseFileName(id)),
                    s => ChunkCodec.Encode(r.Grid.Decimate(ChunkFormat.CoarseStride), s));
                changed.Add(new ManifestTile { E = id.E, N = id.N, Min = r.Grid.MinHeight, Max = r.Grid.MaxHeight });
                foreach (var step in r.JoinSteps) joinSteps.Add(step);
                lock (bodyLock) if (r.WorstJoin.Step > worstJoin.Step) worstJoin = r.WorstJoin;

                lock (bodyLock)
                {
                    foreach (var (root, st) in r.Stats)
                    {
                        if (!bodies.TryGetValue(root, out var b)) continue;
                        b.OutCount += st.Count;
                        b.OutDepthSum += st.DepthSum;
                        b.OutMaxDepth = Math.Max(b.OutMaxDepth, st.MaxDepth);
                        b.Surveyed += st.Surveyed;
                        b.Filled += st.Filled;
                        if (st.Lake != null) b.Lakes[st.Lake] = b.Lakes.GetValueOrDefault(st.Lake) + st.Surveyed;
                        surveyedAll += st.Surveyed;
                        filledAll += st.Filled;
                        wetAll += st.Count;
                    }
                }
                int k = Interlocked.Increment(ref done);
                if (k % 25 == 0) Console.WriteLine($"  [{k}/{ordered.Count}] {clock.Elapsed.TotalSeconds:F0}s");
            });
        }
        held.Clear();
        Directory.Delete(cacheDir, recursive: true);

        // ---- D: manifest, horizon, seams ---------------------------------------------------------
        var byId = changed.ToDictionary(t => t.Id);
        foreach (var t in manifest.Tiles)
            if (byId.TryGetValue(t.Id, out var c)) { t.Min = c.Min; t.Max = c.Max; }
        AtomicFile.Write(manifestPath, s => { using var w = new StreamWriter(s); w.Write(manifest.ToJson()); });
        if (HorizonStage.Run(outDir, o.Jobs, ct) is var hrc && hrc != 0) return hrc;

        var seamTiles = new HashSet<TileId>();
        foreach (var id in byId.Keys)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (region.Contains(new TileId(id.E + dx, id.N + dy))) seamTiles.Add(new TileId(id.E + dx, id.N + dy));
        int seamErrors = TerrainBuild.VerifySeams(outDir, seamTiles, o.Jobs, ct);

        // ---- report ------------------------------------------------------------------------------
        Console.WriteLine($"Water bodies (area >= 0.02 km²), bed depth below the still level:");
        Console.WriteLine($"  {"body",-28} {"centre E,N",-16} {"km²",7} {"level",8} {"max",7} {"mean",6} {"survey",7} {"fill",6}  published max");
        foreach (var b in bodies.Values.Where(b => b.AreaM2 >= 20_000).OrderByDescending(b => b.AreaM2))
        {
            string lake = b.Lakes.Count > 0 ? b.Lakes.MaxBy(kv => kv.Value).Key : "";
            string name = lake.Length > 0 ? $"{lake} (swissBATHY3D)" : SyntheticName(b);
            string published = PublishedMaxDepth(lake);
            Console.WriteLine($"  {name,-28} {(b.MinE + b.MaxE) / 2000.0:F1},{(b.MinN + b.MaxN) / 2000.0:F1}".PadRight(48)
                + $" {b.AreaM2 / 1e6,7:F2} {b.LevelSum / b.Count,8:F2} {b.OutMaxDepth,6:F1}m {(b.OutCount > 0 ? b.OutDepthSum / b.OutCount : 0),5:F1}m"
                + $" {100.0 * b.Surveyed / Math.Max(1, b.OutCount),6:F1}% {100.0 * b.Filled / Math.Max(1, b.OutCount),5:F1}%  {published}");
        }
        int small = bodies.Values.Count(b => b.AreaM2 < 20_000);
        Console.WriteLine($"  + {small} smaller bodies (synthetic, max depth {WaterBed.MinMaxDepthM}..{WaterBed.MaxDepthForArea(20_000):F1} m)");
        Console.WriteLine($"  {wetAll:N0} wet vertices: {100.0 * surveyedAll / Math.Max(1, wetAll):F1}% surveyed, "
            + $"{100.0 * filledAll / Math.Max(1, wetAll):F1}% gap fill toward the survey, the rest synthetic");
        if (!joinSteps.IsEmpty)
        {
            var steps = joinSteps.OrderBy(s => s).ToArray();
            Console.WriteLine($"  survey/synthetic join, bed step between neighbouring vertices: median {steps[steps.Length / 2]:F2} m, "
                + $"p99 {steps[(int)(steps.Length * 0.99)]:F2} m, max {steps[^1]:F2} m at {worstJoin.E:F0},{worstJoin.N:F0} over {steps.Length:N0} pairs");
        }

        if (o.PngDir != null) WritePngs(outDir, o, byId.Keys.ToHashSet(), region);

        Console.WriteLine($"Water pass done: {byId.Count} tiles in {clock.Elapsed.TotalSeconds:F1}s"
            + (seamErrors > 0 ? $", {seamErrors} SEAM ERRORS" : ""));
        return seamErrors > 0 ? 1 : 0;
    }

    // ---- per tile ----------------------------------------------------------------------------------

    private static TileData? LoadTile(string outDir, TileId id)
    {
        string coverPath = Path.Combine(outDir, CoverFormat.FileName(id));
        string waterPath = Path.Combine(outDir, WaterFormat.FileName(id));
        byte[]? cover = null;
        if (File.Exists(coverPath))
            using (var fs = File.OpenRead(coverPath)) cover = CoverFormat.Decode(fs);
        WaterGrid? old = null;
        if (File.Exists(waterPath))
            using (var fs = File.OpenRead(waterPath)) old = WaterFormat.Decode(fs);

        var wet = new byte[S * S];
        bool any = false;
        if (cover != null)
            for (int i = 0; i < wet.Length; i++)
                if (cover[i] == (byte)CoverClass.Water) { wet[i] = 1; any = true; }
        if (!any && old == null) return null;

        ChunkGrid grid;
        using (var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)))) grid = ChunkCodec.Decode(fs);
        var surface = (ushort[])grid.Heights.Clone();
        if (old != null)
            for (int i = 0; i < surface.Length; i++)
                if (old.Levels[i] != 0) surface[i] = old.Levels[i];
        return new TileData { Id = id, Wet = wet, Surface = surface, HadWater = old != null };
    }

    /// <summary>4-connected flood fill of the wet vertices; returns the label count.</summary>
    private static int LabelTile(TileData td)
    {
        var labels = new int[S * S];
        var stack = new Stack<int>();
        int next = 0;
        for (int start = 0; start < labels.Length; start++)
        {
            if (td.Wet[start] == 0 || labels[start] != 0) continue;
            labels[start] = ++next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int r = i / S, c = i % S;
                if (c > 0) Visit(i - 1);
                if (c < Q) Visit(i + 1);
                if (r > 0) Visit(i - S);
                if (r < Q) Visit(i + S);
            }
        }
        td.Labels = labels;
        return next;

        void Visit(int j)
        {
            if (td.Wet[j] == 0 || labels[j] != 0) return;
            labels[j] = next;
            stack.Push(j);
        }
    }

    private static Edges EdgesOf(TileData td, int count)
    {
        var top = new int[S]; var bottom = new int[S]; var left = new int[S]; var right = new int[S];
        for (int i = 0; i < S; i++)
        {
            top[i] = td.Labels[i];
            bottom[i] = td.Labels[Q * S + i];
            left[i] = td.Labels[i * S];
            right[i] = td.Labels[i * S + Q];
        }
        return new Edges(count, top, bottom, left, right);
    }

    private static string CachePath(string cacheDir, TileId id) => Path.Combine(cacheDir, $"water_{id.E}_{id.N}.bin");

    /// <summary>A tile's input as phase A read it: the wet flags and surface, deflated.</summary>
    private static void WriteCache(string cacheDir, TileData td)
    {
        using var file = File.Create(CachePath(cacheDir, td.Id));
        using var z = new DeflateStream(file, CompressionLevel.Fastest);
        z.WriteByte(td.HadWater ? (byte)1 : (byte)0);
        z.Write(td.Wet);
        z.Write(MemoryMarshal.AsBytes(td.Surface.AsSpan()));
    }

    /// <summary>Reads a cached tile back and labels it again: the flood fill is a function of the wet flags alone.</summary>
    private static TileData ReadCache(string cacheDir, TileId id, int labelBase)
    {
        var wet = new byte[S * S];
        var surface = new ushort[S * S];
        bool hadWater;
        using (var file = File.OpenRead(CachePath(cacheDir, id)))
        using (var z = new DeflateStream(file, CompressionMode.Decompress))
        {
            int flag = z.ReadByte();
            if (flag < 0) throw new EndOfStreamException(CachePath(cacheDir, id));
            hadWater = flag != 0;
            z.ReadExactly(wet);
            z.ReadExactly(MemoryMarshal.AsBytes(surface.AsSpan()));
        }
        var td = new TileData { Id = id, Wet = wet, Surface = surface, HadWater = hadWater, LabelBase = labelBase };
        LabelTile(td);
        return td;
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

    private sealed class LabelStats
    {
        public long Count, Surveyed, Filled;
        public double DepthSum, MaxDepth;
        public string? Lake;
    }

    private sealed record TileResult(ChunkGrid Grid, WaterGrid? Layer, Dictionary<int, LabelStats> Stats,
        List<float> JoinSteps, (float Step, double E, double N) WorstJoin);

    private static TileResult ComputeTile(string outDir, TileId id, Func<TileId, TileData?> data,
        HashSet<TileId> region, BathySource? bathy, int[] rootOf, double[] maxDepthOf, double[] sqrtAreaOf,
        double[] surveyLevelOf)
    {
        var td = data(id)!;
        ChunkGrid grid;
        using (var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)))) grid = ChunkCodec.Decode(fs);

        // window: the tile plus Halo metres around it; 0 dry, 1 water, 2 no tile there (unknown).
        // Everything at a vertex comes from here, never from td, so two tiles that share a vertex
        // read the same wet flag, level and body for it even if their own files disagreed.
        const int W = S + 2 * Halo;
        var state = new byte[W * W];
        var surfaceQ = new ushort[W * W];
        var label = new int[W * W];   // global label (not root) of a wet vertex
        Array.Fill(state, (byte)2);
        double wMinE = id.MinE - Halo, wMaxN = id.MaxN + Halo;   // window cell (x, y) is at E = wMinE + x, N = wMaxN - y

        // ascending (E, N): a vertex two tiles share is written by the same tile in every window
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var nb = new TileId(id.E + dx, id.N + dy);
                if (!region.Contains(nb)) continue;
                var nd = data(nb);
                int ox = Halo + dx * Q, oy = Halo - dy * Q;
                for (int r = 0; r < S; r++)
                {
                    int y = oy + r;
                    if ((uint)y >= W) continue;
                    for (int c = 0; c < S; c++)
                    {
                        int x = ox + c;
                        if ((uint)x >= W) continue;
                        int wi = y * W + x, ti = r * S + c;
                        if (nd != null && nd.Wet[ti] != 0)
                        {
                            state[wi] = 1;
                            surfaceQ[wi] = nd.Surface[ti];
                            label[wi] = nd.LabelBase + nd.Labels[ti];
                        }
                        else state[wi] = 0;
                    }
                }
            }

        RefineWet(state, surfaceQ, W);

        // distance to the shore (any known dry vertex), capped short of the halo
        var land = new bool[W * W];
        for (int i = 0; i < land.Length; i++) land[i] = state[i] == 0;
        var ds = new float[W * W];
        DistanceTransform.Run(land, W, W, ds);

        // the survey, as depth below the still level, at every wet vertex of the window
        float[]? survey = null;
        float[]? dv = null;
        int[]? site = null;
        float[]? edgeDepth = null;
        string? lake = null;
        if (bathy != null)
        {
            bool near = false;
            for (int ey = id.N - 1; ey <= id.N + 1 && !near; ey++)
                for (int ex = id.E - 1; ex <= id.E + 1 && !near; ex++)
                    near = bathy.Covers(new TileId(ex, ey));
            if (near)
            {
                lake = bathy.LakeAt(id);
                survey = new float[W * W];
                var valid = new bool[W * W];
                bool anyValid = false;
                for (int y = 0; y < W; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        survey[i] = float.NaN;
                        if (state[i] != 1) continue;
                        double lakeLevel = surveyLevelOf[label[i]];
                        double surf = ChunkFormat.Dequantize(surfaceQ[i]);
                        if (!(Math.Abs(surf - lakeLevel) <= SurveyLevelToleranceM)) continue;
                        double bed = bathy.SampleBed(wMinE + x, wMaxN - y);
                        if (double.IsNaN(bed)) continue;
                        survey[i] = (float)Math.Max(0, surf - bed);
                        valid[i] = anyValid = true;
                    }
                if (anyValid)
                {
                    dv = new float[W * W];
                    site = new int[W * W];
                    DistanceTransform.Run(valid, W, W, dv, site);
                    edgeDepth = SmoothEdgeDepth(survey, state, dv, site, W);
                }
                else survey = null;
            }
        }

        var heights = (ushort[])grid.Heights.Clone();
        var levels = new ushort[S * S];
        var fetch = new byte[S * S];
        var stats = new Dictionary<int, LabelStats>();
        var depth = new float[S * S];
        var kind = new byte[S * S];   // 0 synthetic, 1 survey, 2 gap fill
        bool anyWet = false;

        for (int r = 0; r < S; r++)
            for (int c = 0; c < S; c++)
            {
                int ti = r * S + c;
                int x = Halo + c, y = Halo + r, wi = y * W + x;
                if (state[wi] != 1)
                {
                    // was water in an earlier run, is not any more: give the vertex its surface back
                    if (td.HadWater) heights[ti] = td.Surface[ti];
                    continue;
                }
                anyWet = true;
                double dShore = Math.Min(ds[wi], (float)DistanceCapM);
                int gl = label[wi];
                double maxDepth = maxDepthOf[gl];
                double half = HalfWidth(ds, W, x, y);
                double synthetic = WaterBed.Depth(dShore, half, maxDepth);

                double d;
                byte k = 0;
                if (survey != null && !float.IsNaN(survey[wi]))
                {
                    d = survey[wi];
                    k = 1;
                }
                else if (dv != null && dv[wi] < FillReachM)
                {
                    double dSurvey = dv[wi];
                    double edge = edgeDepth![wi];
                    double fill = edge * dShore / (dShore + dSurvey);
                    double w = 1 - SmoothStep(FillReachM / 2, FillReachM, dSurvey);
                    d = synthetic + (fill - synthetic) * w;
                    k = 2;
                }
                else d = synthetic;
                d = Math.Clamp(d, 0, dShore * MaxBankSlope);

                double level = ChunkFormat.Dequantize(surfaceQ[wi]);
                heights[ti] = ChunkFormat.Quantize(level - d);
                levels[ti] = surfaceQ[wi];
                double fetchM = double.IsPositiveInfinity(half) ? sqrtAreaOf[gl] : Math.Min(sqrtAreaOf[gl], 2 * half);
                fetch[ti] = WaterFormat.QuantizeFetch(fetchM);
                depth[ti] = (float)d;
                kind[ti] = k;

                // statistics on the vertices this tile owns (rows 1..1000, columns 0..999)
                if (r == 0 || c == Q) continue;
                if (!stats.TryGetValue(rootOf[gl], out var st)) stats[rootOf[gl]] = st = new LabelStats { Lake = lake };
                st.Count++;
                st.DepthSum += d;
                if (d > st.MaxDepth) st.MaxDepth = d;
                if (k == 1) st.Surveyed++;
                if (k == 2) st.Filled++;
            }

        // bed steps across the survey's edge (this tile's own pairs: east and south neighbours)
        var join = new List<float>();
        var worst = (Step: 0f, E: 0.0, N: 0.0);
        if (survey != null)
            for (int r = 1; r < S; r++)
                for (int c = 0; c < Q; c++)
                {
                    int ti = r * S + c;
                    if (levels[ti] == 0) continue;
                    foreach (int tj in new[] { ti + 1, ti - S })
                        if (levels[tj] != 0 && (kind[ti] == 1) != (kind[tj] == 1))
                        {
                            float step = MathF.Abs(depth[ti] - depth[tj]);
                            join.Add(step);
                            if (step > worst.Step) worst = (step, id.MinE + c, id.MaxN - r);
                        }
                }

        ushort qMin = ushort.MaxValue, qMax = 0;
        foreach (ushort q in heights) { if (q < qMin) qMin = q; if (q > qMax) qMax = q; }
        var outGrid = new ChunkGrid(id, heights, (float)ChunkFormat.Dequantize(qMin), (float)ChunkFormat.Dequantize(qMax));
        return new TileResult(outGrid, anyWet ? new WaterGrid(id, levels, fetch) : null, stats, join, worst);
    }

    /// <summary>
    /// Half the local width of the water at window cell (x, y): climb the distance-to-shore field to
    /// the ridge running down the middle and read the distance there. Infinity once the climb passes
    /// <see cref="WidthProbeM"/>: open water, where the width no longer matters.
    /// </summary>
    private static double HalfWidth(float[] ds, int w, int x, int y)
    {
        float here = ds[y * w + x];
        while (here < WidthProbeM)
        {
            int bx = x, by = y;
            float best = here;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)w) continue;
                    float v = ds[ny * w + nx];
                    if (v > best) { best = v; bx = nx; by = ny; }
                }
            if (bx == x && by == y) return here;
            x = bx;
            y = by;
            here = best;
        }
        return double.PositiveInfinity;
    }

    /// <summary>Box radius of the edge-depth smoothing, in metres (two passes, so a tent twice as wide).</summary>
    private const int EdgeSmoothM = 40;

    /// <summary>
    /// For every gap vertex the fill may reach: the survey's depth at the nearest surveyed vertex,
    /// smoothed. The nearest vertex alone jumps wherever two parts of the survey's edge are equally
    /// near (a Voronoi edge), which drew metre-high steps across the fill; two box passes of
    /// <see cref="EdgeSmoothM"/> over the gap turn those into gentle ramps. Box sums over the window
    /// only read cells within the halo, so the result stays a function of the region's data.
    /// </summary>
    private static float[] SmoothEdgeDepth(float[] survey, byte[] state, float[] dv, int[] site, int w)
    {
        var raw = new float[w * w];
        float reach = (float)(FillReachM + 2 * EdgeSmoothM + 2);
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = float.NaN;
            if (state[i] != 1 || !float.IsNaN(survey[i]) || dv[i] > reach || site[i] < 0) continue;
            int x0 = site[i] % w, y0 = site[i] / w;
            double sum = 0;
            int n = 0;
            for (int y = Math.Max(0, y0 - 2); y <= Math.Min(w - 1, y0 + 2); y++)
                for (int x = Math.Max(0, x0 - 2); x <= Math.Min(w - 1, x0 + 2); x++)
                {
                    float v = survey[y * w + x];
                    if (float.IsNaN(v)) continue;
                    sum += v;
                    n++;
                }
            raw[i] = n > 0 ? (float)(sum / n) : float.NaN;
        }
        return BoxMean(BoxMean(raw, w, EdgeSmoothM), w, EdgeSmoothM);
    }

    /// <summary>
    /// Mean of the defined (non-NaN) cells in a (2r+1)² box, NaN where the cell itself is NaN. The
    /// sums are exact integers (tenths of a millimetre): a floating-point summed-area table rounds
    /// differently from each window's origin, and a shared vertex would then differ by a quantum
    /// between the two tiles that hold it.
    /// </summary>
    private static float[] BoxMean(float[] f, int w, int r)
    {
        const double Unit = 1e-4;
        int sw = w + 1;
        var sum = new long[sw * sw];
        var cnt = new int[sw * sw];
        for (int y = 0; y < w; y++)
        {
            long rowSum = 0;
            int rowCnt = 0;
            for (int x = 0; x < w; x++)
            {
                float v = f[y * w + x];
                if (!float.IsNaN(v)) { rowSum += (long)Math.Round(v / Unit); rowCnt++; }
                sum[(y + 1) * sw + x + 1] = sum[y * sw + x + 1] + rowSum;
                cnt[(y + 1) * sw + x + 1] = cnt[y * sw + x + 1] + rowCnt;
            }
        }
        var o = new float[w * w];
        for (int y = 0; y < w; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(w, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                if (float.IsNaN(f[y * w + x])) { o[y * w + x] = float.NaN; continue; }
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                long s = sum[y1 * sw + x1] - sum[y0 * sw + x1] - sum[y1 * sw + x0] + sum[y0 * sw + x0];
                int n = cnt[y1 * sw + x1] - cnt[y0 * sw + x1] - cnt[y1 * sw + x0] + cnt[y0 * sw + x0];
                o[y * w + x] = (float)(s * Unit / n);
            }
        }
        return o;
    }

    private static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static string SyntheticName(Body b)
    {
        double elong = (double)(b.MaxE - b.MinE + 1) * (b.MaxN - b.MinN + 1) / Math.Max(1, b.Count);
        return elong > 6 ? "river/channel (synthetic)" : "lake (synthetic)";
    }

    /// <summary>
    /// Published maximum depths of the whole lake (swisstopo / cantonal figures), for the report.
    /// The Petit Lac, the part of Léman in the western region, is ~76 m deep at most.
    /// </summary>
    private static string PublishedMaxDepth(string lake) => lake switch
    {
        "lacleman" => "309.7 m whole lake; Petit Lac ~76 m",
        "lacneuchatel" => "152 m",
        "bodensee" => "251 m",
        "lagomaggiore" => "372 m",
        "vierwaldstaettersee" => "214 m",
        "zuerichsee" => "136 m",
        "thunersee" => "217 m",
        "brienzersee" => "259 m",
        "bielersee" => "74 m",
        "zugersee" => "198 m",
        "walensee" => "151 m",
        "murtensee" => "45 m",
        "lacdejoux" => "32 m",
        "sempachersee" => "87 m",
        "hallwilersee" => "47 m",
        "baldeggersee" => "66 m",
        "aegerisee" => "83 m",
        "sarnersee" => "52 m",
        "lungernsee" => "68 m",
        "silsersee" => "71 m",
        "silvaplanersee" => "77 m",
        "rotsee" => "16 m",
        _ => "",
    };

    // ---- PNG check ------------------------------------------------------------------------------

    /// <summary>
    /// Per cluster of touched tiles: <c>water_shade_*.png</c>, a hillshade of the new terrain (bed
    /// included, vertical x3) at 2 m/px where a seam at a tile edge or along the survey's edge
    /// shows as a line; <c>water_depth_*.png</c>, depth below the level (black = dry, white = 80 m+).
    /// Crops at 1 m/px for <see cref="Options.Crops"/>.
    /// </summary>
    private static void WritePngs(string outDir, Options o, HashSet<TileId> touched, HashSet<TileId> region)
    {
        Directory.CreateDirectory(o.PngDir!);
        var clusters = new List<List<TileId>>();
        var seen = new HashSet<TileId>();
        foreach (var start in touched.OrderBy(t => t.E).ThenBy(t => t.N))
        {
            if (!seen.Add(start)) continue;
            var cl = new List<TileId>();
            var q = new Queue<TileId>();
            q.Enqueue(start);
            while (q.Count > 0)
            {
                var t = q.Dequeue();
                cl.Add(t);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var nb = new TileId(t.E + dx, t.N + dy);
                        if (touched.Contains(nb) && seen.Add(nb)) q.Enqueue(nb);
                    }
            }
            clusters.Add(cl);
        }

        foreach (var cl in clusters)
        {
            int minE = cl.Min(t => t.E), maxE = cl.Max(t => t.E), minN = cl.Min(t => t.N), maxN = cl.Max(t => t.N);
            string name = $"{minE}_{minN}";
            WriteArea(outDir, Path.Combine(o.PngDir!, $"water_{{0}}_{name}.png"), region,
                minE * 1000.0, (maxN + 1) * 1000.0, (maxE - minE + 1) * 1000, (maxN - minN + 1) * 1000, 2);
        }
        foreach (var (e, n, size) in o.Crops)
            WriteArea(outDir, Path.Combine(o.PngDir!, $"water_{{0}}_crop_{e:F0}_{n:F0}.png"), region,
                Math.Round(e - size / 2.0), Math.Round(n + size / 2.0), size, size, 1);
        Console.WriteLine($"Water PNGs written to {o.PngDir}");
    }

    private static void WriteArea(string outDir, string pattern, HashSet<TileId> region,
        double west, double north, int widthM, int heightM, int step)
    {
        int w = widthM / step + 1, h = heightM / step + 1;
        var elev = new float[w * h];
        var level = new float[w * h];
        Array.Fill(elev, float.NaN);
        var grids = new Dictionary<TileId, (ChunkGrid G, WaterGrid? L)>();
        (ChunkGrid G, WaterGrid? L)? Tile(TileId id)
        {
            if (!region.Contains(id)) return null;
            if (grids.TryGetValue(id, out var t)) return t;
            ChunkGrid g;
            using (var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)))) g = ChunkCodec.Decode(fs);
            WaterGrid? l = null;
            string wp = Path.Combine(outDir, WaterFormat.FileName(id));
            if (File.Exists(wp)) using (var fs = File.OpenRead(wp)) l = WaterFormat.Decode(fs);
            return grids[id] = (g, l);
        }
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                double e = west + px * step, n = north - py * step;
                // the vertex's owning tile: on a km line, the tile to the east / to the south
                var id = new TileId((int)Math.Floor(e / 1000), (int)Math.Ceiling(n / 1000) - 1);
                if (Tile(id) is not { } t) continue;
                int c = (int)(e - id.MinE), r = (int)(id.MaxN - n);
                if ((uint)c >= S || (uint)r >= S) continue;
                elev[py * w + px] = (float)t.G.HeightMetersAt(c, r);
                level[py * w + px] = t.L != null && t.L.IsWet(c, r) ? (float)t.L.LevelMetersAt(c, r) : float.NaN;
            }

        var shade = new byte[w * h];
        var depthPix = new byte[w * h];
        (double lx, double ly, double lz) = (-0.5, 0.7, -0.5);
        double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        (lx, ly, lz) = (lx / ll, ly / ll, lz / ll);
        const double Exaggeration = 3;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (float.IsNaN(elev[i])) continue;
                int xl = Math.Max(x - 1, 0), xr = Math.Min(x + 1, w - 1), yu = Math.Max(y - 1, 0), yd = Math.Min(y + 1, h - 1);
                float el = elev[y * w + xl], er = elev[y * w + xr], eu = elev[yu * w + x], ed = elev[yd * w + x];
                if (float.IsNaN(el)) el = elev[i];
                if (float.IsNaN(er)) er = elev[i];
                if (float.IsNaN(eu)) eu = elev[i];
                if (float.IsNaN(ed)) ed = elev[i];
                double dzdx = Exaggeration * (er - el) / ((xr - xl) * step);
                double dzdy = Exaggeration * (ed - eu) / ((yd - yu) * step);
                double nl = Math.Sqrt(dzdx * dzdx + 1 + dzdy * dzdy);
                double dot = (-dzdx * lx + ly + -dzdy * lz) / nl;
                shade[i] = (byte)Math.Clamp(dot * 255.0, 0, 255);
                if (!float.IsNaN(level[i]))
                    depthPix[i] = (byte)Math.Clamp(40 + (level[i] - elev[i]) / 80.0 * 215, 40, 255);
            }
        PngWriter.WriteGray8(string.Format(pattern, "shade"), shade, w, h);
        PngWriter.WriteGray8(string.Format(pattern, "depth"), depthPix, w, h);
    }
}
