using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using UnitSport.Terrain.Format;

namespace UnitSport.Map;

/// <summary>
/// What one download run fetched, so the caller (the Prepare stage of the pipeline) can learn
/// this machine's download rate and report what happened. Port of the counters
/// <c>tools/swiss_data.py</c>'s <c>run()</c> prints and emits as its "done" progress-json event.
/// </summary>
public sealed record DownloadResult(int Files, long Bytes, double Seconds, int Skipped, string? Error);

/// <summary>
/// A C# port of <c>tools/swiss_data.py</c>'s download machinery (HEAD-check skip decision,
/// parallel downloads, &gt;=256 MB parallel byte-range fetch, <c>.part</c> resume, SHA-256
/// verification against STAC's checksum) so the game can fetch swisstopo open data without
/// shelling out to Python. <c>swiss_data.py</c> stays the standalone tool; this does not replace
/// it, it is read as the specification.
///
/// <para>
/// The pure decisions (manifest format, skip logic, STAC id/bbox arithmetic) live in
/// <see cref="DownloadManifest"/> and <see cref="SwissStacUtil"/> instead of here, so the unit
/// tests can link just that file -- everything in <b>this</b> file needs an <c>IStepProgress</c>
/// (from <c>Pipeline.cs</c>) or an <c>HttpClient</c> to even compile, neither of which the tier-0
/// test project has a path to without also linking in things it has no project reference for.
/// </para>
///
/// <para>
/// Left out, deliberately, because the game does not need them: <c>--fill-disk</c> (and the disk
/// budget it needs), <c>--dry-run</c>, <c>--list</c>, and every dataset resolver except
/// swissALTI3D's, swissTLM3D's and GWR's (swissBUILDINGS3D, swissBATHY3D,
/// Veloland/Mountainbikeland, OSM) -- <see cref="CollectionAsync"/> is the generic "every asset
/// of every STAC item in this collection (bbox, and optionally a filename filter)" building block
/// those would be written on top of, but their per-dataset business logic (nationwide vs.
/// per-sheet, which zip extension) is not reproduced here. <see cref="AltiAsync"/>,
/// <see cref="TlmAsync"/> and <see cref="GwrAsync"/> are the three resolvers fully ported: the
/// terrain, roads/place-name and building-register data the game needs with zero external
/// dependencies.
/// </para>
/// </summary>
public static class SwissDownload
{
    // A nationwide HEAD-check pass is tens of thousands of files; this is what makes that
    // tractable (swiss_data.py's HEAD_CHECK_WORKERS).
    private const int HeadCheckWorkers = 32;
    // swiss_data.py's STAC_WORKERS / the ~0.1 deg (8-11 km) cell size / its 40-per-axis cap.
    private const int StacWorkers = 8;
    private const double StacCellDeg = 0.1;
    private const int StacMaxCellsPerAxis = 40;
    // One file this big is split into byte ranges fetched in parallel (swiss_data.py's
    // SEGMENT_MIN_BYTES / SEGMENT_BYTES / MAX_SEGMENTS).
    private const long SegmentMinBytes = 256L << 20;
    private const long SegmentBytes = 64L << 20;
    private const int MaxSegments = 8;
    private const int ChunkBytes = 1 << 20;

    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        // Deliberately no HttpClient.Timeout (it cancels the whole request, body included, not
        // just connect/headers -- a multi-hundred-MB file can legitimately take minutes). A
        // genuinely stuck transfer is instead caught by the attempt/backoff loop in
        // DownloadFileAsync, same role as swiss_data.py's per-socket 60 s timeout but a different
        // mechanism -- .NET has no direct equivalent of "60 s of silence on this socket", so this
        // is the one place fidelity with the Python tool is approximate rather than exact.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UnitSportSwitzerland-MapCore");
        return client;
    }

    /// <summary>
    /// Downloads swissALTI3D tiles by their 1 km ids ("E-N", the <c>--tiles-file</c> path): the
    /// path the game actually uses, since it always knows exactly which tiles it wants. Picks the
    /// newest flight per tile and the asset matching <paramref name="res"/>, same as
    /// <c>resolve_swissalti3d</c>.
    /// </summary>
    public static async Task<DownloadResult> AltiAsync(string outDir, IReadOnlyCollection<TileId> tiles,
        IStepProgress progress, CancellationToken ct, int jobs = 8, string res = "0.5")
    {
        if (tiles.Count == 0) return new DownloadResult(0, 0, 0, 0, null);

        progress.Show("resolving swissalti3d assets...");
        var bbox = SwissStacUtil.TilesBboxWgs84(tiles);
        var items = await ListStacItemsAsync("ch.swisstopo.swissalti3d", bbox, ct);

        var wanted = tiles as HashSet<TileId> ?? new HashSet<TileId>(tiles);
        var latestByTile = new Dictionary<TileId, (int Year, StacItem Item)>();
        foreach (var item in items)
        {
            if (!SwissStacUtil.TryParseAltiItemId(item.Id, out int year, out TileId tile)) continue;
            if (!wanted.Contains(tile)) continue;
            if (!latestByTile.TryGetValue(tile, out var current) || year > current.Year)
                latestByTile[tile] = (year, item);
        }

        var pattern = SwissStacUtil.AltiAssetPattern(res);
        var candidates = new List<(string Url, string Filename, string? Sha256)>();
        foreach (var (_, item) in latestByTile.Values)
            foreach (var asset in item.Assets)
                if (pattern.IsMatch(asset.Key))
                    candidates.Add((asset.Href, asset.Key, asset.Sha256));

        return await RunAsync(outDir, candidates, progress, ct, jobs);
    }

    /// <summary>
    /// Downloads the swissTLM3D GeoPackage (<c>ch.swisstopo.swisstlm3d</c>): one nationwide file
    /// the preprocessor reads for roads, railways, watercourses, land cover and trees, so no
    /// bbox -- the business logic is entirely in <i>which</i> of several releases to take. The
    /// collection keeps every past release as its own STAC item; this picks the one whose
    /// <c>datetime</c> sorts latest (<see cref="SwissStacUtil.IndexOfLatestDatetime"/>) and takes
    /// its <c>.gpkg.zip</c> asset(s), falling back to <c>.gdb.zip</c> if that release published
    /// only the legacy format (<see cref="SwissStacUtil.TlmAssetKeys"/>). Port of
    /// <c>resolve_swisstlm3d</c>.
    /// </summary>
    public static async Task<DownloadResult> TlmAsync(string outDir, IStepProgress progress,
        CancellationToken ct, int jobs = 8)
    {
        progress.Show("resolving swisstlm3d assets...");
        var items = await ListStacItemsAsync("ch.swisstopo.swisstlm3d", null, ct);
        if (items.Count == 0) return new DownloadResult(0, 0, 0, 0, null);

        var latest = items[SwissStacUtil.IndexOfLatestDatetime(items.Select(i => i.Datetime).ToList())];
        var keys = new HashSet<string>(SwissStacUtil.TlmAssetKeys(latest.Assets.Select(a => a.Key)));
        var candidates = latest.Assets.Where(a => keys.Contains(a.Key))
            .Select(a => (a.Href, a.Key, a.Sha256)).ToList();

        return await RunAsync(outDir, candidates, progress, ct, jobs);
    }

    /// <summary>
    /// Downloads the GWR/RegBL building register for one canton (or "ch" for the whole country),
    /// which the preprocessor reads for building use, year and storeys and the place index is
    /// built from. Not a STAC collection at all -- BFS publishes one zip per canton at a fixed
    /// URL (<see cref="SwissStacUtil.GwrAsset"/>), so there is no item listing and, unlike every
    /// other dataset here, no checksum to verify against: the shared engine's HEAD-check skip
    /// decision falls back to size alone, same as the Python tool's. Port of <c>resolve_gwr</c>.
    /// </summary>
    public static Task<DownloadResult> GwrAsync(string outDir, string canton, IStepProgress progress,
        CancellationToken ct, int jobs = 8)
    {
        var (url, filename) = SwissStacUtil.GwrAsset(canton);
        var candidates = new List<(string Url, string Filename, string? Sha256)> { (url, filename, null) };
        return RunAsync(outDir, candidates, progress, ct, jobs);
    }

    /// <summary>
    /// Downloads every asset of every STAC item in <paramref name="collection"/>, inside
    /// <paramref name="bbox"/> (LV95) when one is given, optionally narrowed to assets whose key
    /// matches <paramref name="assetFilter"/>. The generic building block the per-dataset
    /// resolvers (swissTLM3D latest-release, swissBUILDINGS3D nationwide-vs-sheets, ...) are not
    /// reproduced on top of here -- see the class doc comment.
    /// </summary>
    public static async Task<DownloadResult> CollectionAsync(string outDir, string collection,
        (double MinE, double MinN, double MaxE, double MaxN)? bbox, IStepProgress progress,
        CancellationToken ct, int jobs = 8, Func<string, bool>? assetFilter = null)
    {
        progress.Show($"resolving {collection} assets...");
        var wgsBbox = bbox is { } b ? SwissStacUtil.BboxLv95ToWgs84(b.MinE, b.MinN, b.MaxE, b.MaxN) : ((double, double, double, double)?)null;
        var items = await ListStacItemsAsync(collection, wgsBbox, ct);

        var candidates = new List<(string Url, string Filename, string? Sha256)>();
        foreach (var item in items)
            foreach (var asset in item.Assets)
                if (assetFilter == null || assetFilter(asset.Key))
                    candidates.Add((asset.Href, asset.Key, asset.Sha256));

        return await RunAsync(outDir, candidates, progress, ct, jobs);
    }

    // ---------------------------------------------------------------------------
    // The shared engine: manifest skip decision, HEAD-check, parallel download.
    // Port of run()'s body in swiss_data.py, minus --dry-run/--force/--fill-disk.
    // ---------------------------------------------------------------------------

    private static async Task<DownloadResult> RunAsync(string outDir,
        List<(string Url, string Filename, string? Sha256)> candidates, IStepProgress progress,
        CancellationToken ct, int jobs)
    {
        Directory.CreateDirectory(outDir);
        var manifest = DownloadManifest.Load(outDir);
        var manifestLock = new object();

        // Decided locally: the manifest already holds this exact file, by the checksum STAC
        // publishes -- no request needed at all.
        var toCheck = new List<(string Url, string Filename, string? Sha256)>();
        int unchanged = 0;
        foreach (var c in candidates)
        {
            var dest = Path.Combine(outDir, c.Filename);
            if (DownloadManifest.KnownCurrent(dest, manifest.GetValueOrDefault(c.Filename), c.Sha256))
                unchanged++;
            else
                toCheck.Add(c);
        }
        if (toCheck.Count > 0)
            progress.Show($"{unchanged} already current by checksum; checking {toCheck.Count} against the server");

        var plan = new List<(string Url, string Filename, string Dest, HeadInfo Head, string? Sha256)>();
        int unreachable = 0, headChecked = 0;

        await Parallel.ForEachAsync(toCheck, new ParallelOptions { MaxDegreeOfParallelism = HeadCheckWorkers, CancellationToken = ct },
            async (item, ct2) =>
        {
            var head = await HeadAsync(item.Url, ct2);
            var dest = Path.Combine(outDir, item.Filename);
            lock (manifestLock)
            {
                headChecked++;
                progress.Show($"checking {headChecked}/{toCheck.Count} against the server");
                if (head is null) { unreachable++; return; }

                var entry = manifest.GetValueOrDefault(item.Filename);
                if (DownloadManifest.IsUpToDate(dest, entry, head.Value))
                {
                    unchanged++;
                    if (entry is null)
                    {
                        // First time this tool has seen a file already on disk (hand-downloaded,
                        // or fetched by swiss_data.py before any manifest entry existed) --
                        // record it now so later runs decide from the checksum alone.
                        entry = new ManifestEntry
                        {
                            Url = item.Url,
                            Size = head.Value.Size,
                            ETag = head.Value.ETag,
                            LastModified = head.Value.LastModified,
                            DownloadedAt = null,
                        };
                        manifest[item.Filename] = entry;
                    }
                    entry.Sha256 ??= item.Sha256 ?? head.Value.Sha256;
                    return;
                }
                plan.Add((item.Url, item.Filename, dest, head.Value, item.Sha256));
            }
        });

        DownloadManifest.Save(outDir, manifest);
        if (unreachable > 0) progress.Log($"{unreachable} file(s) could not be reached and were skipped");

        if (plan.Count == 0)
            return new DownloadResult(0, 0, 0, unchanged, null);

        long total = plan.Sum(p => p.Head.Size ?? 0);
        progress.Show($"{plan.Count} file(s) to fetch, {total / 1e9:F2} GB total");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long moved = 0;
        int fetched = 0;
        var errors = new List<string>();
        var lastSave = clock.Elapsed;

        await Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct },
            async (item, ct2) =>
        {
            void OnProgress(long n)
            {
                long sofar = Interlocked.Add(ref moved, n);
                progress.Value = total > 0 ? 100.0 * sofar / total : 0;
            }
            try
            {
                long size = await DownloadFileAsync(item.Url, item.Dest, item.Head, item.Sha256, OnProgress, ct2);
                lock (manifestLock)
                {
                    fetched++;
                    manifest[item.Filename] = new ManifestEntry
                    {
                        Url = item.Url,
                        Size = size,
                        ETag = item.Head.ETag,
                        LastModified = item.Head.LastModified,
                        Sha256 = item.Sha256 ?? item.Head.Sha256,
                        DownloadedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    };
                    // Mid-run saves (every ~10s, same cadence as swiss_data.py) so a crash or a
                    // Ctrl+C keeps every file finished so far instead of losing the whole run's
                    // bookkeeping.
                    if ((clock.Elapsed - lastSave).TotalSeconds > 10)
                    {
                        DownloadManifest.Save(outDir, manifest);
                        lastSave = clock.Elapsed;
                    }
                }
                progress.Show($"[{fetched}/{plan.Count}] {item.Filename}");
            }
            catch (Exception e) when (!ct2.IsCancellationRequested)
            {
                lock (manifestLock) errors.Add($"{item.Filename}: {e.Message}");
                progress.Log("error: " + e.Message);
            }
        });

        lock (manifestLock) DownloadManifest.Save(outDir, manifest);

        return new DownloadResult(fetched, moved, clock.Elapsed.TotalSeconds, unchanged,
            errors.Count > 0 ? string.Join("; ", errors) : null);
    }

    // ---------------------------------------------------------------------------
    // HEAD check.
    // ---------------------------------------------------------------------------

    /// <summary>Port of <c>http_head</c>: null on a 404, or when still unreachable after retrying.</summary>
    private static async Task<HeadInfo?> HeadAsync(string url, CancellationToken ct, int attempts = 3)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await DownloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                if (resp.IsSuccessStatusCode)
                {
                    long? size = resp.Content.Headers.ContentLength;
                    if (size is null) return null;
                    string? etag = FirstHeader(resp, "ETag");
                    string? lastModified = FirstHeader(resp, "Last-Modified");
                    string? sha = FirstHeader(resp, "x-amz-meta-sha256")?.ToLowerInvariant();
                    bool ranges = string.Equals(FirstHeader(resp, "Accept-Ranges"), "bytes", StringComparison.OrdinalIgnoreCase);
                    return new HeadInfo(size, etag, lastModified, sha, ranges);
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (attempt < attempts - 1) await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct);
        }
        return null;
    }

    private static string? FirstHeader(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var v1)) return v1.FirstOrDefault();
        if (resp.Content.Headers.TryGetValues(name, out var v2)) return v2.FirstOrDefault();
        return null;
    }

    // ---------------------------------------------------------------------------
    // Download: resume, parallel segments for big files, SHA-256 verification.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="dest"/> through a <c>.part</c> file,
    /// verifying the SHA-256 when one is known. Port of <c>download_file</c>: on a size mismatch
    /// or a checksum failure the <c>.part</c> is discarded (never left in place looking good) and
    /// the whole attempt is retried from scratch, up to <paramref name="attempts"/> times.
    /// </summary>
    private static async Task<long> DownloadFileAsync(string url, string dest, HeadInfo head, string? sha256,
        Action<long> progress, CancellationToken ct, int attempts = 3)
    {
        string tmp = dest + ".part";
        long? size = head.Size;
        string? expected = (sha256 ?? head.Sha256)?.ToLowerInvariant();
        Exception? lastError = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            long movedThisAttempt = 0;
            void Counted(long n) { movedThisAttempt += n; progress(n); }
            try
            {
                string? got = size is { } sz && sz >= SegmentMinBytes && head.Ranges
                    ? await DownloadSegmentedAsync(url, tmp, sz, expected, Counted, ct)
                    : await DownloadSingleAsync(url, tmp, size, expected, Counted, ct);

                long gotSize = new FileInfo(tmp).Length;
                if (size is { } expectedSize && gotSize != expectedSize)
                    throw new IOException($"size mismatch: got {gotSize}, expected {expectedSize}");
                if (expected != null && got != expected)
                {
                    File.Delete(tmp); // corrupt; resuming would only extend the damage
                    throw new IOException("SHA-256 mismatch");
                }
                File.Move(tmp, dest, overwrite: true);
                return gotSize;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                lastError = e;
                // a retry re-counts whatever it resumes from; keep the progress figure honest
                progress(-movedThisAttempt);
                if (attempt < attempts - 1) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
        throw new IOException($"{Path.GetFileName(dest)}: {lastError?.Message}", lastError);
    }

    /// <summary>One stream, resuming from an existing .part when the server allows it. Port of <c>_download_single</c>.</summary>
    private static async Task<string?> DownloadSingleAsync(string url, string tmp, long? size, string? sha256,
        Action<long> progress, CancellationToken ct)
    {
        long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        if (size is { } sz && have >= sz) have = 0;
        using var hasher = sha256 != null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await DownloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        FileMode mode;
        if (resp.StatusCode == HttpStatusCode.PartialContent && have > 0)
        {
            if (hasher != null)
            {
                var buf = new byte[ChunkBytes];
                using var existing = File.OpenRead(tmp);
                int n;
                while ((n = await existing.ReadAsync(buf, ct)) > 0) hasher.AppendData(buf, 0, n);
            }
            progress(have); // already on disk, so never counted against this attempt's new bytes
            mode = FileMode.Append;
        }
        else if (resp.StatusCode == HttpStatusCode.OK)
        {
            mode = FileMode.Create;
        }
        else
        {
            throw new IOException($"HTTP {(int)resp.StatusCode} for {url}");
        }

        await using var fs = new FileStream(tmp, mode, FileAccess.Write, FileShare.None);
        await using var respStream = await resp.Content.ReadAsStreamAsync(ct);
        await WriteStreamAsync(respStream, fs, hasher, progress, ct);

        return hasher?.GetHashAndReset() is { } hash ? Convert.ToHexString(hash).ToLowerInvariant() : null;
    }

    /// <summary>
    /// Parallel byte ranges into one preallocated file, each on its own connection. Port of
    /// <c>_download_segmented</c>; used only for files &gt;= <see cref="SegmentMinBytes"/> that
    /// advertise range support.
    /// </summary>
    private static async Task<string?> DownloadSegmentedAsync(string url, string tmp, long size, string? sha256,
        Action<long> progress, CancellationToken ct)
    {
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            fs.SetLength(size);

        int count = (int)Math.Min(MaxSegments, Math.Max(2, size / SegmentBytes));
        long step = (size + count - 1) / count;
        var starts = new List<long>();
        for (long s = 0; s < size; s += step) starts.Add(s);

        await Parallel.ForEachAsync(starts, new ParallelOptions { MaxDegreeOfParallelism = count, CancellationToken = ct },
            async (start, ct2) =>
        {
            long end = Math.Min(size, start + step) - 1;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(start, end);
            using var resp = await DownloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct2);
            if (resp.StatusCode != HttpStatusCode.PartialContent)
                throw new IOException($"HTTP {(int)resp.StatusCode} for {url}");
            await using var respStream = await resp.Content.ReadAsStreamAsync(ct2);
            await using var fileStream = new FileStream(tmp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fileStream.Seek(start, SeekOrigin.Begin);
            await WriteStreamAsync(respStream, fileStream, null, progress, ct2);
        });

        if (sha256 == null) return null;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var readFs = File.OpenRead(tmp);
        var buf = new byte[ChunkBytes];
        int n;
        while ((n = await readFs.ReadAsync(buf, ct)) > 0) hasher.AppendData(buf, 0, n);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task WriteStreamAsync(Stream source, Stream dest, IncrementalHash? hasher,
        Action<long> progress, CancellationToken ct)
    {
        var buffer = new byte[ChunkBytes];
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, n), ct);
            hasher?.AppendData(buffer, 0, n);
            progress(n);
        }
    }

    // ---------------------------------------------------------------------------
    // STAC item listing. Stac.cs (used for the bake) lists a collection over a bbox too, but its
    // Item/Asset model does not carry checksum:multihash, which the download path needs for the
    // SHA-256 verification (requirement, not optional) -- so this parses STAC pages itself rather
    // than extending a file this port is not allowed to touch. Same shape as Stac.ListAsync
    // otherwise: Stac.Http for the requests, bbox split into cells listed concurrently, dedup by
    // item id.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// swissBUILDINGS3D (<c>ch.swisstopo.swissbuildings3d_3_0</c>): the building solids, published
    /// as one <c>.gdb.zip</c> per map sheet. Three things decide what to fetch, and all three are
    /// in the data rather than in the query. The collection mixes per-sheet items with a single
    /// nationwide asset, told apart by how much of the country the item spans. Sheets are then kept
    /// only if they really touch the wanted tiles, tested on the item's LV95 footprint rather than
    /// its lon/lat bbox. And swisstopo re-flies sheets, keeping every past flight as its own item,
    /// so only the newest year of each sheet is taken. Port of <c>resolve_swissbuildings3d</c>.
    /// </summary>
    public static async Task<DownloadResult> BuildingsAsync(string outDir, IReadOnlyCollection<TileId> tiles,
        IStepProgress progress, CancellationToken ct, int jobs = 8, bool nationwide = false)
    {
        if (tiles.Count == 0 && !nationwide) return new DownloadResult(0, 0, 0, 0, null);

        progress.Show("resolving swissbuildings3d assets...");
        var bbox = tiles.Count > 0 ? SwissStacUtil.TilesBboxWgs84(tiles) : ((double, double, double, double)?)null;
        var items = await ListStacItemsAsync("ch.swisstopo.swissbuildings3d_3_0", bbox, ct);
        if (items.Count == 0) return new DownloadResult(0, 0, 0, 0, null);

        List<StacItem> wanted;
        if (nationwide)
        {
            // the one asset that covers the country: the widest-spanning item there is
            wanted = [items.MaxBy(i => SwissStacUtil.BboxSpan(i.Bbox))!];
        }
        else
        {
            wanted = items.Where(i => SwissStacUtil.BboxSpan(i.Bbox) < NationwideSpanDegrees)
                .Where(i => SwissStacUtil.ItemTouchesTiles(
                    SwissStacUtil.ItemLv95Bounds(i.Footprint, i.Bbox), tiles))
                .ToList();
        }

        var latestBySheet = new Dictionary<string, (string Year, StacItem Item)>(StringComparer.Ordinal);
        foreach (var item in wanted)
        {
            var (year, key) = SwissStacUtil.BuildingsItemKey(item.Id);
            if (!latestBySheet.TryGetValue(key, out var current)
                || string.CompareOrdinal(year, current.Year) > 0)
                latestBySheet[key] = (year, item);
        }

        var candidates = new List<(string Url, string Filename, string? Sha256)>();
        foreach (var (_, item) in latestBySheet.Values)
            foreach (var asset in item.Assets)
                if (asset.Key.EndsWith(".gdb.zip", StringComparison.OrdinalIgnoreCase))
                    candidates.Add((asset.Href, asset.Key, asset.Sha256));

        progress.Show($"{latestBySheet.Count} building sheet(s) to consider");
        return await RunAsync(outDir, candidates, progress, ct, jobs);
    }

    /// <summary>
    /// How wide an item's bbox has to be, in degrees, before it is the nationwide asset rather than
    /// a map sheet. A sheet is a few hundredths of a degree; the country is several.
    /// </summary>
    private const double NationwideSpanDegrees = 0.5;

    /// <summary>
    /// The OpenStreetMap extract for the optional OSM overlay (ODbL,
    /// <c>docs/notes/tools/osm-odbl-licence.md</c>). Not STAC at all: Geofabrik publishes an index
    /// page, and the newest dated <c>switzerland-YYMMDD.osm.pbf</c> on it is taken in preference to
    /// <c>switzerland-latest.osm.pbf</c>, which has been seen answering with a redirect to itself.
    /// Port of <c>resolve_osm</c>.
    /// </summary>
    public static async Task<DownloadResult> OsmAsync(string outDir, IStepProgress progress,
        CancellationToken ct, int jobs = 2)
    {
        const string index = "https://download.geofabrik.de/europe/";
        progress.Show("resolving the OpenStreetMap extract...");

        string name = "switzerland-latest.osm.pbf";
        try
        {
            string html = await Stac.Http.GetStringAsync(index, ct);
            if (SwissStacUtil.NewestOsmExtract(html) is { } dated) name = dated;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // the index is a convenience, not the download: fall back to -latest and say so
            progress.Show($"could not list {index} ({e.Message}); falling back to {name}");
        }

        return await RunAsync(outDir, [(index + name, name, null)], progress, ct, jobs);
    }

    private sealed record StacAsset(string Key, string Href, string? Sha256);
    /// <summary>
    /// One STAC item. <see cref="Bbox"/> (WGS84 west, south, east, north) and <see cref="Footprint"/>
    /// (the geometry ring's lon/lat points) are carried because swissBUILDINGS3D needs them: its
    /// collection mixes per-sheet items with one nationwide asset, and telling them apart and
    /// deciding which sheets a selection touches is done from the item's own extent.
    /// </summary>
    private sealed record StacItem(string Id, string Datetime, List<StacAsset> Assets,
        (double W, double S, double E, double N)? Bbox, List<(double Lon, double Lat)>? Footprint);

    private static async Task<List<StacItem>> ListStacItemsAsync(string collection,
        (double W, double S, double E, double N)? bbox, CancellationToken ct)
    {
        var cells = new List<(double W, double S, double E, double N)?>();
        if (bbox is { } b)
        {
            int nx = Math.Clamp((int)Math.Round((b.E - b.W) / StacCellDeg), 1, StacMaxCellsPerAxis);
            int ny = Math.Clamp((int)Math.Round((b.N - b.S) / StacCellDeg), 1, StacMaxCellsPerAxis);
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    cells.Add((b.W + (b.E - b.W) * i / nx, b.S + (b.N - b.S) * j / ny,
                               b.W + (b.E - b.W) * (i + 1) / nx, b.S + (b.N - b.S) * (j + 1) / ny));
        }
        else cells.Add(null);

        var items = new ConcurrentDictionary<string, StacItem>();
        await Parallel.ForEachAsync(cells, new ParallelOptions { MaxDegreeOfParallelism = StacWorkers, CancellationToken = ct },
            async (cell, ct2) =>
        {
            string? url = $"{Stac.Base}/collections/{collection}/items?limit=100";
            if (cell is { } c)
                url += "&bbox=" + string.Join(",", new[] { c.W, c.S, c.E, c.N }.Select(v => v.ToString("F6", CultureInfo.InvariantCulture)));
            while (url != null)
            {
                using var doc = JsonDocument.Parse(await GetStringWithRetryAsync(url, ct2));
                foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
                    items.TryAdd(f.GetProperty("id").GetString()!, ParseStacItem(f));

                url = null;
                if (doc.RootElement.TryGetProperty("links", out var links))
                    foreach (var link in links.EnumerateArray())
                        if (link.GetProperty("rel").GetString() == "next")
                            url = link.GetProperty("href").GetString();
            }
        });
        return items.Values.ToList();
    }

    private static StacItem ParseStacItem(JsonElement f)
    {
        string id = f.GetProperty("id").GetString()!;
        string dt = f.TryGetProperty("properties", out var p) && p.TryGetProperty("datetime", out var d)
                    && d.ValueKind == JsonValueKind.String
            ? d.GetString()! : "";

        var assets = new List<StacAsset>();
        if (f.TryGetProperty("assets", out var a))
            foreach (var prop in a.EnumerateObject())
            {
                string href = prop.Value.GetProperty("href").GetString()!;
                string? multihash = prop.Value.TryGetProperty("checksum:multihash", out var cm) && cm.ValueKind == JsonValueKind.String
                    ? cm.GetString() : null;
                assets.Add(new StacAsset(prop.Name, href, SwissStacUtil.Sha256OfAsset(multihash)));
            }
        (double, double, double, double)? bbox = null;
        if (f.TryGetProperty("bbox", out var bb) && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() >= 4)
        {
            var v = bb.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            bbox = (v[0], v[1], v[2], v[3]);
        }

        // The footprint ring, when the item has one. Its WGS84 bbox would do as a rough extent, but
        // a lon/lat box drawn round a sheet cut on the LV95 grid is tens of metres too big on every
        // side — enough to count the neighbouring sheets as touching.
        List<(double, double)>? ring = null;
        if (f.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object
            && g.TryGetProperty("coordinates", out var co) && co.ValueKind == JsonValueKind.Array
            && co.GetArrayLength() > 0)
        {
            var outer = co[0];
            if (outer.ValueKind == JsonValueKind.Array && outer.GetArrayLength() >= 3)
            {
                ring = new List<(double, double)>(outer.GetArrayLength());
                foreach (var point in outer.EnumerateArray())
                    if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
                        ring.Add((point[0].GetDouble(), point[1].GetDouble()));
            }
        }
        return new StacItem(id, dt, assets, bbox, ring);
    }

    private static async Task<string> GetStringWithRetryAsync(string url, CancellationToken ct, int attempts = 4)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await DownloadHttp.GetStringAsync(url, ct); }
            catch (Exception) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                await Task.Delay(500 * attempt * attempt, ct);
            }
        }
    }
}
