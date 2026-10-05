using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>One swissALTI3D 0.5 m source tile found on disk.</summary>
public sealed record SourceTile(TileId Id, string Path, int Year, long Length, long Ticks);

/// <summary>
/// swissALTI3D sources -> <c>.terr</c> + <c>.terrc</c> for every tile, in one parallel pass.
///
/// <para>
/// Each worker reads a source (throttled separately from CPU work, since a spinning disk or a
/// network share wants few concurrent readers), inflates and parses it, and reduces it straight to
/// the tile's interior vertices plus 16 KB of perimeter partial sums (<see cref="TileReducer"/>).
/// A tile is written the moment every tile around it has published its partials; workers walk the
/// region north-to-south so that happens a row or two behind the parse front, and only that band
/// of interiors (2 MB each) is ever held in memory. No full-resolution cell grid outlives its
/// worker, so the 8 MB/tile pass-1 cache the old pipeline needed is gone.
/// </para>
///
/// <para>
/// Incremental: a tile whose <c>.edge</c> and <c>.terr</c> already exist and whose source is
/// unchanged (length + mtime) is not parsed again. Only its seam is refreshed, from its existing
/// <c>.terr</c>, when a neighbour was (re)parsed. That makes it cheap to import Switzerland one
/// download batch — or one drive — at a time.
/// </para>
/// </summary>
public static class TerrainBuild
{
    public sealed class Options
    {
        public int Jobs = Environment.ProcessorCount;
        public int IoJobs = 4;
        /// <summary>Re-parse every source, ignoring the edge cache.</summary>
        public bool Force;
        /// <summary>Only tiles in this run's sources exist; forget tiles built by earlier runs.</summary>
        public bool Fresh;
        /// <summary>Read every written tile back and compare, then check all seams.</summary>
        public bool Verify;
    }

    private static readonly Regex NameRe = new(
        @"^swissalti3d_(\d{4})_(\d{4})-(\d{4})_0\.5_.*\.xyz(\.zip)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds 0.5 m sources under every directory, recursively. When the same tile appears more than
    /// once (two survey years, or two copies on two drives) the newest survey wins.
    /// </summary>
    public static List<SourceTile> Discover(IEnumerable<string> dirs)
    {
        var found = new Dictionary<TileId, SourceTile>();
        int skipped = 0, duplicates = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException($"--in directory not found: {dir}");
            foreach (var path in Directory.EnumerateFiles(dir, "swissalti3d_*", opts))
            {
                var m = NameRe.Match(Path.GetFileName(path));
                if (!m.Success)
                {
                    if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xyz", StringComparison.OrdinalIgnoreCase))
                        skipped++; // e.g. the 2 m product
                    continue;
                }
                var info = new FileInfo(path);
                var tile = new SourceTile(
                    new TileId(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)),
                    path, int.Parse(m.Groups[1].Value), info.Length, info.LastWriteTimeUtc.Ticks);
                if (found.TryGetValue(tile.Id, out var prev))
                {
                    duplicates++;
                    if (prev.Year >= tile.Year) continue;
                }
                found[tile.Id] = tile;
            }
        }
        if (skipped > 0) Console.WriteLine($"  ignored {skipped} swissalti3d files that are not the 0.5 m XYZ product");
        if (duplicates > 0) Console.WriteLine($"  {duplicates} tiles found more than once; kept the newest survey year");
        return found.Values.ToList();
    }

    /// <summary>Builds tiles; returns the manifest of every tile in the dataset, or null on failure.</summary>
    public static TerrainManifest? Run(List<SourceTile> sources, string outDir, string cacheDir, Options o,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(cacheDir);
        var clock = Stopwatch.StartNew();
        int n = ChunkFormat.GridSize;

        // ---- what exists, what must be parsed, what must be (re)written ----------------------
        var bySource = sources.ToDictionary(s => s.Id);
        var dataset = new HashSet<TileId>(bySource.Keys);
        var cachedEdges = new ConcurrentDictionary<TileId, uint[]>();

        // previously built tiles, in this run's sources or not, keep counting as neighbours
        var candidates = new HashSet<TileId>(bySource.Keys);
        if (!o.Fresh)
            foreach (var path in Directory.EnumerateFiles(cacheDir, "*.edge"))
                if (TryParseEdgeName(path, out var id)) candidates.Add(id);

        var toParse = new ConcurrentBag<SourceTile>();
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs, CancellationToken = ct }, id =>
        {
            bySource.TryGetValue(id, out var src);
            bool built = IsValidTerr(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)));
            uint[]? edges = o.Force || !built ? null
                : EdgeStrips.TryRead(EdgeStrips.PathFor(cacheDir, id), src?.Length, src?.Ticks);
            if (edges != null) cachedEdges[id] = edges;
            else if (src != null) toParse.Add(src);
        });
        foreach (var id in cachedEdges.Keys) dataset.Add(id);

        // raster order, north row first: a tile completes once the row south of it has parsed,
        // so the set of interiors waiting on neighbours stays a narrow band
        var parseList = toParse.OrderByDescending(t => t.Id.N).ThenBy(t => t.Id.E).ToList();
        var parseSet = parseList.Select(t => t.Id).ToHashSet();

        var toWrite = new HashSet<TileId>(parseSet);
        foreach (var id in parseSet)
            foreach (var nb in Hood(id))
                if (dataset.Contains(nb)) toWrite.Add(nb);

        Console.WriteLine($"Terrain: {dataset.Count} tiles in dataset, {parseList.Count} to parse, "
            + $"{toWrite.Count - parseList.Count} seams to refresh, {dataset.Count - toWrite.Count} up to date "
            + $"(jobs {o.Jobs}, io {o.IoJobs})");

        // ---- bookkeeping for the streaming finish --------------------------------------------
        // how many tiles still to be written need each tile's edges; at zero they are dropped
        var users = new ConcurrentDictionary<TileId, int>();
        foreach (var id in toWrite)
            foreach (var nb in Hood(id))
                if (dataset.Contains(nb)) users.AddOrUpdate(nb, 1, (_, v) => v + 1);
        foreach (var id in cachedEdges.Keys)
            if (!users.ContainsKey(id)) cachedEdges.TryRemove(id, out _);

        var edgesOf = cachedEdges; // parsed tiles join it as they finish
        var interiors = new ConcurrentDictionary<TileId, ushort[]>();
        var written = new ConcurrentDictionary<TileId, ManifestTile>();
        var claimed = new ConcurrentDictionary<TileId, byte>();
        var ioGate = new SemaphoreSlim(Math.Max(1, o.IoJobs));
        var scratchPool = new ConcurrentBag<Scratch>();
        var failures = new ConcurrentQueue<string>();

        long tRead = 0, tParse = 0, tReduce = 0, tWrite = 0, bytesIn = 0;
        int parsed = 0, peakBand = 0;
        var progress = Stopwatch.StartNew();
        long lastReport = 0;

        void TryFinish(TileId id)
        {
            if (!toWrite.Contains(id)) return;
            foreach (var nb in Hood(id))
                if (dataset.Contains(nb) && !edgesOf.ContainsKey(nb)) return;
            if (!claimed.TryAdd(id, 0)) return;

            long t0 = Stopwatch.GetTimestamp();
            string terrPath = Path.Combine(outDir, ChunkFormat.ChunkFileName(id));
            if (!interiors.TryRemove(id, out var heights))
            {
                // up-to-date tile whose seam changed: its interior is the one already on disk
                using var fs = File.OpenRead(terrPath);
                heights = ChunkCodec.Decode(fs).Heights;
            }

            TileReducer.FinishPerimeter(id, heights, t => dataset.Contains(t) && edgesOf.TryGetValue(t, out var e) ? e : null);

            ushort qMin = ushort.MaxValue, qMax = 0;
            foreach (ushort q in heights) { if (q < qMin) qMin = q; if (q > qMax) qMax = q; }
            var grid = new ChunkGrid(id, heights, (float)ChunkFormat.Dequantize(qMin), (float)ChunkFormat.Dequantize(qMax));

            AtomicFile.Write(terrPath, s => ChunkCodec.Encode(grid, s));
            // the horizon's copy of the same tile, 5 KB instead of 2 MB
            AtomicFile.Write(Path.Combine(outDir, ChunkFormat.CoarseFileName(id)),
                s => ChunkCodec.Encode(grid.Decimate(ChunkFormat.CoarseStride), s));

            if (o.Verify)
            {
                using var fs = File.OpenRead(terrPath);
                var back = ChunkCodec.Decode(fs);
                if (!back.Heights.AsSpan().SequenceEqual(heights))
                    failures.Enqueue($"{id}: .terr did not round-trip");
            }

            written[id] = new ManifestTile { E = id.E, N = id.N, Min = grid.MinHeight, Max = grid.MaxHeight };
            foreach (var nb in Hood(id))
                if (users.TryGetValue(nb, out _) && users.AddOrUpdate(nb, 0, (_, v) => v - 1) == 0)
                    edgesOf.TryRemove(nb, out _);
            Interlocked.Add(ref tWrite, Stopwatch.GetTimestamp() - t0);
        }

        // ---- the pass --------------------------------------------------------------------
        var partitioner = System.Collections.Concurrent.Partitioner.Create(parseList, EnumerablePartitionerOptions.NoBuffering);
        try
        {
            Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs, CancellationToken = ct }, src =>
            {
                if (!scratchPool.TryTake(out var sc)) sc = new Scratch();
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    // a legacy pass-1 cache file is the parsed grid already: 8 MB read, no inflate
                    string legacy = Path.Combine(cacheDir, $"{src.Id.E}_{src.Id.N}.raw");
                    string readPath = !o.Force && new FileInfo(legacy) is { Exists: true, Length: XyzParser.RawFileBytes } li
                        && li.LastWriteTimeUtc.Ticks >= src.Ticks ? legacy : src.Path;
                    byte[] data;
                    ioGate.Wait();
                    try { data = File.ReadAllBytes(readPath); }
                    finally { ioGate.Release(); }
                    long t1 = Stopwatch.GetTimestamp();

                    XyzParser.Parse(data, readPath, src.Id, sc.Cells, sc.Text);
                    long t2 = Stopwatch.GetTimestamp();

                    var heights = new ushort[n * n];
                    var edges = new uint[EdgeStrips.Length];
                    TileReducer.Reduce(sc.Cells, sc.Sums, heights, edges);
                    EdgeStrips.Write(EdgeStrips.PathFor(cacheDir, src.Id), edges, src.Length, src.Ticks);
                    long t3 = Stopwatch.GetTimestamp();

                    Interlocked.Add(ref tRead, t1 - t0);
                    Interlocked.Add(ref tParse, t2 - t1);
                    Interlocked.Add(ref tReduce, t3 - t2);
                    Interlocked.Add(ref bytesIn, data.Length);

                    interiors[src.Id] = heights;
                    edgesOf[src.Id] = edges;
                    int band = interiors.Count;
                    if (band > peakBand) Interlocked.Exchange(ref peakBand, Math.Max(peakBand, band));

                    TryFinish(src.Id);
                    foreach (var nb in Hood(src.Id)) TryFinish(nb);

                    int done = Interlocked.Increment(ref parsed);
                    long now = progress.ElapsedMilliseconds;
                    long last = Interlocked.Read(ref lastReport);
                    if ((now - last > 5000 || done == parseList.Count) && Interlocked.CompareExchange(ref lastReport, now, last) == last)
                    {
                        double rate = done / Math.Max(0.001, progress.Elapsed.TotalSeconds);
                        double eta = (parseList.Count - done) / Math.Max(0.001, rate);
                        Console.WriteLine($"  [{done}/{parseList.Count}] {rate:F1} tiles/s, "
                            + $"{bytesIn / 1048576.0 / progress.Elapsed.TotalSeconds:F0} MB/s in, "
                            + $"written {written.Count}/{toWrite.Count}, eta {TimeSpan.FromSeconds(eta):hh\\:mm\\:ss}");
                    }
                }
                catch (Exception e)
                {
                    failures.Enqueue($"{src.Id} ({src.Path}): {e.Message}");
                    throw;
                }
                finally { scratchPool.Add(sc); }
            });

            // seam-only tiles with no parsed neighbour to trigger them (possible after --force-less reruns)
            Parallel.ForEach(toWrite.Where(id => !claimed.ContainsKey(id)),
                new ParallelOptions { MaxDegreeOfParallelism = o.Jobs, CancellationToken = ct }, TryFinish);
        }
        catch (AggregateException)
        {
            foreach (var f in failures) Console.Error.WriteLine($"  [FAIL] {f}");
            return null;
        }

        if (written.Count != toWrite.Count || !failures.IsEmpty)
        {
            foreach (var f in failures) Console.Error.WriteLine($"  [FAIL] {f}");
            Console.Error.WriteLine($"  [FAIL] wrote {written.Count} of {toWrite.Count} tiles");
            return null;
        }

        double f64 = Stopwatch.Frequency;
        Console.WriteLine($"Terrain pass: parsed {parseList.Count}, wrote {written.Count} tiles in {clock.Elapsed.TotalSeconds:F1}s "
            + $"({bytesIn / 1048576.0:F0} MB read; worker time read {tRead / f64:F0}s, inflate+parse {tParse / f64:F0}s, "
            + $"reduce {tReduce / f64:F0}s, finish+write {tWrite / f64:F0}s; peak {peakBand} interiors in memory)");

        // ---- manifest: fresh entries for what was written, previous ones for the rest --------
        var previous = new Dictionary<TileId, ManifestTile>();
        string manifestPath = Path.Combine(outDir, "manifest.json");
        if (File.Exists(manifestPath))
            foreach (var t in TerrainManifest.FromJson(File.ReadAllText(manifestPath)).Tiles)
                previous[t.Id] = t;

        var manifest = new TerrainManifest();
        foreach (var id in dataset.OrderBy(t => t.N).ThenBy(t => t.E))
        {
            if (written.TryGetValue(id, out var t) || previous.TryGetValue(id, out t))
                manifest.Tiles.Add(t);
            else
                manifest.Tiles.Add(ReadHeaderTile(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)), id));
        }
        manifest.BoundsLv95 = new Lv95Bounds
        {
            MinE = manifest.Tiles.Min(t => t.E) * 1000.0,
            MinN = manifest.Tiles.Min(t => t.N) * 1000.0,
            MaxE = (manifest.Tiles.Max(t => t.E) + 1) * 1000.0,
            MaxN = (manifest.Tiles.Max(t => t.N) + 1) * 1000.0,
        };
        manifest.SuggestedOriginLv95 = new Lv95Point
        {
            E = Math.Round((manifest.BoundsLv95.MinE + manifest.BoundsLv95.MaxE) / 2),
            N = Math.Round((manifest.BoundsLv95.MinN + manifest.BoundsLv95.MaxN) / 2),
        };
        return manifest;
    }

    /// <summary>
    /// Every shared edge of adjacent tiles must be bit-identical. Streams the tiles (keeping only
    /// their four borders), so it runs on a whole country in constant memory per worker.
    /// </summary>
    public static int VerifySeams(string outDir, IEnumerable<TileId> tiles, int jobs, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        int n = ChunkFormat.GridSize;
        var borders = new ConcurrentDictionary<TileId, ushort[]>(); // N, S, W, E rows of n
        Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, id =>
        {
            ChunkGrid g;
            using (var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)))) g = ChunkCodec.Decode(fs);
            var b = new ushort[4 * n];
            for (int i = 0; i < n; i++)
            {
                b[i] = g.HeightAt(i, 0);
                b[n + i] = g.HeightAt(i, n - 1);
                b[2 * n + i] = g.HeightAt(0, i);
                b[3 * n + i] = g.HeightAt(n - 1, i);
            }
            borders[id] = b;
        });

        int errors = 0;
        foreach (var (id, b) in borders)
        {
            if (borders.TryGetValue(new TileId(id.E + 1, id.N), out var east)
                && !b.AsSpan(3 * n, n).SequenceEqual(east.AsSpan(2 * n, n)))
            {
                Console.Error.WriteLine($"  [FAIL] seam {id} <-> {new TileId(id.E + 1, id.N)}");
                errors++;
            }
            if (borders.TryGetValue(new TileId(id.E, id.N + 1), out var north)
                && !b.AsSpan(0, n).SequenceEqual(north.AsSpan(n, n)))
            {
                Console.Error.WriteLine($"  [FAIL] seam {id} <-> {new TileId(id.E, id.N + 1)}");
                errors++;
            }
        }
        Console.WriteLine(errors == 0
            ? $"Verify OK ({borders.Count} chunks, seam checks) in {clock.Elapsed.TotalSeconds:F1}s"
            : $"Verify FAILED with {errors} seam errors");
        return errors;
    }

    private sealed class Scratch
    {
        public readonly ushort[] Cells = new ushort[XyzParser.CellsPerSide * XyzParser.CellsPerSide];
        public readonly uint[] Sums = new uint[ChunkFormat.GridSize * ChunkFormat.GridSize];
        public readonly byte[] Text = new byte[4 << 20];
    }

    private static IEnumerable<TileId> Hood(TileId id)
    {
        for (int dN = -1; dN <= 1; dN++)
            for (int dE = -1; dE <= 1; dE++)
                yield return new TileId(id.E + dE, id.N + dN);
    }

    private static bool TryParseEdgeName(string path, out TileId id)
    {
        id = default;
        var parts = Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int e) || !int.TryParse(parts[1], out int nn)) return false;
        id = new TileId(e, nn);
        return true;
    }

    /// <summary>A full-resolution tile of the current format, judged from its header and length alone.</summary>
    private static bool IsValidTerr(string path)
    {
        var info = new FileInfo(path);
        long expected = ChunkFormat.HeaderSize + (long)ChunkFormat.GridSize * ChunkFormat.GridSize * 2;
        if (!info.Exists || info.Length != expected) return false;
        Span<byte> h = stackalloc byte[ChunkFormat.HeaderSize];
        using var fs = File.OpenRead(path);
        fs.ReadExactly(h);
        var header = ChunkCodec.ReadHeader(h);
        // stride 0 (legacy) or 1 only: a coarse companion is not a built full-resolution tile
        return header.Tile.Magic == ChunkFormat.Magic
            && header.Tile.Version == ChunkFormat.Version
            && header.GridSize == ChunkFormat.GridSize
            && header.RawStride is 0 or 1;
    }

    private static ManifestTile ReadHeaderTile(string path, TileId id)
    {
        Span<byte> h = stackalloc byte[ChunkFormat.HeaderSize];
        using var fs = File.OpenRead(path);
        fs.ReadExactly(h);
        var header = ChunkCodec.ReadHeader(h);
        return new ManifestTile { E = id.E, N = id.N, Min = header.MinHeight, Max = header.MaxHeight };
    }
}
