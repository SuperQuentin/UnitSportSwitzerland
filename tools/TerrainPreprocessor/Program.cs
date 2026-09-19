using System.Diagnostics;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;

// swissALTI3D XYZ zips -> .terr chunk files + manifest.json
// Usage:
//   dotnet run --project tools/TerrainPreprocessor -- --in ressources/data/swiss_chunks --out terrain_chunks [--verify] [--dump-png <dir>] [--jobs N]

string? inDir = null, outDir = null, tempDir = null, pngDir = null;
string? tlmGpkg = null, routeKeys = null, buildingsGpkg = null, gwrPath = null;
bool verify = false;
bool roadsOnly = false, featuresOnly = false, doCover = false, doPlaces = false;
bool coarseOnly = false, horizonOnly = false;
string? franceBox = null;
int jobs = Math.Min(4, Environment.ProcessorCount);

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--in": inDir = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--temp": tempDir = args[++i]; break;
        case "--dump-png": pngDir = args[++i]; break;
        case "--tlm": tlmGpkg = args[++i]; break;
        case "--route-keys": routeKeys = args[++i]; break;
        case "--buildings": buildingsGpkg = args[++i]; break;
        case "--gwr": gwrPath = args[++i]; break;
        case "--cover": doCover = true; break;
        case "--places": doPlaces = true; break;
        case "--roads-only": roadsOnly = true; break;
        case "--features-only": featuresOnly = true; break;
        case "--coarse": coarseOnly = true; break;
        case "--horizon": horizonOnly = true; break;
        case "--verify": verify = true; break;
        case "--france": franceBox = args[++i]; break;
        case "--jobs": jobs = int.Parse(args[++i]); break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

// ---- horizon: one region-wide 100 m lattice, from the tiles already built ------------------
if (horizonOnly)
{
    if (outDir == null)
    {
        Console.Error.WriteLine("--horizon requires --out <chunk dir>");
        return 2;
    }
    return HorizonStage.Run(outDir, jobs);
}

// ---- coarse companion tiles: decimate what is already built -----------------------------
// Standalone because it needs nothing but the .terr files themselves. A region built before
// .terrc existed gets its horizon tiles for 5 KB apiece without re-parsing a single XYZ zip.
if (coarseOnly)
{
    if (outDir == null)
    {
        Console.Error.WriteLine("--coarse requires --out <chunk dir>");
        return 2;
    }

    var coarseFiles = Directory.GetFiles(outDir, "chunk_*.terr");
    if (coarseFiles.Length == 0)
    {
        Console.Error.WriteLine($"No .terr files in {outDir}");
        return 2;
    }

    var coarseClock = Stopwatch.StartNew();
    long readBytes = 0, wroteBytes = 0;
    int written = 0;

    Parallel.ForEach(coarseFiles, new ParallelOptions { MaxDegreeOfParallelism = jobs }, path =>
    {
        ChunkGrid grid;
        using (var fs = File.OpenRead(path)) grid = ChunkCodec.Decode(fs);
        if (grid.Stride != 1) return;   // already a companion; nothing to decimate

        var coarse = grid.Decimate(ChunkFormat.CoarseStride);

        // Construct then verify. The whole claim of this pass is that the horizon renders
        // *identically* from the small file, and that claim rests on the mesh builder reading
        // HeightAt(c * stride, r * stride) — so check exactly that, for every vertex the coarse
        // grid holds and at both strides the LOD rings use. 2,601 comparisons a tile is nothing
        // against having quietly reshaped the mountains.
        foreach (int renderStride in new[] { ChunkFormat.CoarseStride, ChunkFormat.CoarseStride * 2 })
        {
            int m = (ChunkFormat.GridSize - 1) / renderStride + 1;
            for (int r = 0; r < m; r++)
                for (int c = 0; c < m; c++)
                {
                    int fc = c * renderStride, fr = r * renderStride;
                    if (coarse.HeightAt(fc, fr) != grid.HeightAt(fc, fr))
                        throw new InvalidDataException(
                            $"{grid.Id}: coarse tile differs at ({fc},{fr}) stride {renderStride}");
                }
        }

        string outPath = Path.Combine(outDir, ChunkFormat.CoarseFileName(grid.Id));
        using (var fs = File.Create(outPath))
            ChunkCodec.Encode(coarse, fs);

        // and that what lands on disk decodes back to what we checked
        using (var fs = File.OpenRead(outPath))
        {
            var reread = ChunkCodec.Decode(fs);
            if (reread.Stride != ChunkFormat.CoarseStride
                || !reread.Heights.AsSpan().SequenceEqual(coarse.Heights))
                throw new InvalidDataException($"{grid.Id}: coarse tile did not round-trip");
        }

        Interlocked.Add(ref readBytes, new FileInfo(path).Length);
        Interlocked.Add(ref wroteBytes, new FileInfo(outPath).Length);
        int n = Interlocked.Increment(ref written);
        if (n % 500 == 0) Console.WriteLine($"  [{n}/{coarseFiles.Length}]");
    });

    Console.WriteLine($"Coarse pass: {written} tiles in {coarseClock.Elapsed.TotalSeconds:F1}s, "
        + $"read {readBytes / 1048576.0:F0} MB -> wrote {wroteBytes / 1048576.0:F1} MB "
        + $"({(double)readBytes / Math.Max(1, wroteBytes):F0}x smaller)");
    // the horizon reads the companions just written, so a region gets both in one go
    return HorizonStage.Run(outDir, jobs);
}

// ---- French import: adds IGN BD TOPO features to tiles that already exist ---------------
if (franceBox != null)
{
    if (outDir == null)
    {
        Console.Error.WriteLine("--france requires --out <chunk dir>");
        return 2;
    }
    if (FranceStage.ParseBox(franceBox) is not { } box)
    {
        Console.Error.WriteLine("--france wants minLon,minLat,maxLon,maxLat in degrees");
        return 2;
    }
    return await FranceStage.RunAsync(outDir, box.MinLon, box.MinLat, box.MaxLon, box.MaxLat);
}

if (outDir == null || (inDir == null && !roadsOnly && !featuresOnly))
{
    Console.Error.WriteLine("Required: --in <zip dir> --out <chunk dir>");
    Console.Error.WriteLine("  (--in is not needed with --roads-only / --features-only)");
    return 2;
}
tempDir ??= outDir.TrimEnd('/', '\\') + "_temp";
Directory.CreateDirectory(outDir);

// ---- feature-only passes: reuse the .terr chunks already in outDir ------------------
if (roadsOnly || featuresOnly)
{
    if (tlmGpkg == null && buildingsGpkg == null && !doPlaces)
    {
        Console.Error.WriteLine("Nothing to do: pass --tlm, --buildings and/or --places");
        return 2;
    }

    var existing = TerrainManifest.FromJson(File.ReadAllText(Path.Combine(outDir, "manifest.json")));

    // the place index only needs tile coverage, so it runs before the heavy batches
    if (doPlaces)
    {
        if (gwrPath == null)
        {
            Console.Error.WriteLine("--places requires --gwr <gwr data.sqlite>");
            return 2;
        }
        int rc = PlaceStage.Run(gwrPath, outDir, existing.Tiles.Select(t => t.Id).ToHashSet(), tlmGpkg);
        if (rc != 0) return rc;
        if (tlmGpkg == null && buildingsGpkg == null) return 0;
    }

    // Batched: loading every chunk grid at once is ~0.5 MB x tile count (3.4 GB for the
    // 6,699-tile import) before feature data is even extracted. Tiles are ordered by
    // (E, N) so each batch is a compact strip and its bbox query stays tight.
    const int BatchSize = 400;
    var ordered = existing.Tiles.OrderBy(t => t.E).ThenBy(t => t.N).ToList();
    int batches = (ordered.Count + BatchSize - 1) / BatchSize;

    for (int b = 0; b < batches; b++)
    {
        var slice = ordered.Skip(b * BatchSize).Take(BatchSize).ToList();
        var grids = new Dictionary<TileId, ChunkGrid>();
        foreach (var t in slice)
        {
            using var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(t.Id)));
            grids[t.Id] = ChunkCodec.Decode(fs);
        }
        if (batches > 1)
            Console.WriteLine($"=== batch {b + 1}/{batches}: {slice.Count} tiles, E {slice[0].E}..{slice[^1].E} ===");

        if (tlmGpkg != null)
        {
            int rc = RoadStage.Run(tlmGpkg, routeKeys, outDir, grids);
            if (rc != 0) return rc;
        }
        if (doCover)
        {
            if (tlmGpkg == null)
            {
                Console.Error.WriteLine("--cover requires --tlm <swisstlm3d .gpkg>");
                return 2;
            }
            int rc = CoverStage.Run(tlmGpkg, outDir, grids);
            if (rc != 0) return rc;
        }
        if (buildingsGpkg != null)
        {
            int rc = BuildingStage.Run(buildingsGpkg, gwrPath, outDir, grids);
            if (rc != 0) return rc;
        }
    }
    return 0;
}

// ---- discover tiles ----------------------------------------------------------------
var nameRe = new Regex(@"swissalti3d_\d{4}_(\d{4})-(\d{4})_.*\.xyz\.zip$", RegexOptions.IgnoreCase);
var tiles = new SortedDictionary<(int N, int E), (TileId Id, string ZipPath)>();
foreach (var path in Directory.EnumerateFiles(inDir!, "*.zip"))
{
    var m = nameRe.Match(Path.GetFileName(path));
    if (!m.Success) continue;
    var id = new TileId(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    tiles[(id.N, id.E)] = (id, path);
}
if (tiles.Count == 0)
{
    Console.Error.WriteLine($"No swissalti3d *.xyz.zip files found in {inDir}");
    return 1;
}
Console.WriteLine($"Found {tiles.Count} tiles: E {tiles.Values.Min(t => t.Id.E)}..{tiles.Values.Max(t => t.Id.E)}, N {tiles.Values.Min(t => t.Id.N)}..{tiles.Values.Max(t => t.Id.N)}");

var store = new TempGridStore(tempDir);
var sw = Stopwatch.StartNew();

// ---- pass 1: parse XYZ zips into quantized temp grids (parallel, resumable) --------
var toParse = tiles.Values.Where(t => !store.HasValidFile(t.Id)).ToList();
Console.WriteLine($"Pass 1: parsing {toParse.Count} tiles ({tiles.Count - toParse.Count} cached in {tempDir})");
int done = 0;
Parallel.ForEach(toParse, new ParallelOptions { MaxDegreeOfParallelism = jobs }, t =>
{
    var grid = XyzParser.Parse(t.ZipPath, t.Id);
    store.Save(t.Id, grid);
    Console.WriteLine($"  [{Interlocked.Increment(ref done)}/{toParse.Count}] {t.Id} parsed ({sw.Elapsed.TotalSeconds:F0}s)");
});
Console.WriteLine($"Pass 1 done in {sw.Elapsed.TotalSeconds:F1}s");

// ---- pass 2: build vertex grids, write .terr + manifest ----------------------------
sw.Restart();
var builder = new ChunkBuilder(store);
var manifest = new TerrainManifest();
foreach (var (id, _) in tiles.Values)
{
    var grid = builder.Build(id);
    using (var fs = File.Create(Path.Combine(outDir, ChunkFormat.ChunkFileName(id))))
        ChunkCodec.Encode(grid, fs);
    // the horizon's copy of the same tile, 5 KB instead of 490
    using (var fs = File.Create(Path.Combine(outDir, ChunkFormat.CoarseFileName(id))))
        ChunkCodec.Encode(grid.Decimate(ChunkFormat.CoarseStride), fs);
    manifest.Tiles.Add(new ManifestTile { E = id.E, N = id.N, Min = grid.MinHeight, Max = grid.MaxHeight });
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
File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToJson());
// the far horizon is cut from the same tiles, one file for the whole region
if (HorizonStage.Run(outDir, jobs) is var hrc && hrc != 0) return hrc;
Console.WriteLine($"Pass 2 done in {sw.Elapsed.TotalSeconds:F1}s -> {manifest.Tiles.Count} chunks, " +
                  $"heights {manifest.Tiles.Min(t => t.Min):F0}..{manifest.Tiles.Max(t => t.Max):F0} m");

// ---- load decoded chunks for verify/png --------------------------------------------
ChunkGrid LoadChunk(TileId id)
{
    using var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)));
    return ChunkCodec.Decode(fs);
}

// ---- roads (optional, needs the terrain chunks for draping) ------------------------
if (tlmGpkg != null)
{
    var roadGrids = manifest.Tiles.ToDictionary(t => t.Id, t => LoadChunk(t.Id));
    int rc = RoadStage.Run(tlmGpkg, routeKeys, outDir, roadGrids);
    if (rc != 0) return rc;
}

if (verify)
{
    sw.Restart();
    int errors = 0;
    var chunks = manifest.Tiles.ToDictionary(t => t.Id, t => LoadChunk(t.Id));

    // 1) encode/decode + builder determinism: rebuild and compare bit-exact
    foreach (var (id, chunk) in chunks)
    {
        var rebuilt = builder.Build(id);
        if (!chunk.Heights.AsSpan().SequenceEqual(rebuilt.Heights))
        {
            Console.Error.WriteLine($"  [FAIL] {id}: decoded chunk differs from rebuild");
            errors++;
        }
    }

    // 2) seams: shared edges of adjacent tiles must be bit-identical
    int n = ChunkFormat.GridSize;
    foreach (var (id, chunk) in chunks)
    {
        if (chunks.TryGetValue(new TileId(id.E + 1, id.N), out var east))
        {
            for (int r = 0; r < n; r++)
                if (chunk.HeightAt(n - 1, r) != east.HeightAt(0, r))
                {
                    Console.Error.WriteLine($"  [FAIL] seam {id} <-> {east.Id} at row {r}");
                    errors++;
                    break;
                }
        }
        if (chunks.TryGetValue(new TileId(id.E, id.N + 1), out var north))
        {
            for (int c = 0; c < n; c++)
                if (chunk.HeightAt(c, 0) != north.HeightAt(c, n - 1))
                {
                    Console.Error.WriteLine($"  [FAIL] seam {id} <-> {north.Id} at col {c}");
                    errors++;
                    break;
                }
        }
    }

    Console.WriteLine(errors == 0
        ? $"Verify OK ({chunks.Count} chunks, rebuild + seam checks) in {sw.Elapsed.TotalSeconds:F1}s"
        : $"Verify FAILED with {errors} errors");
    if (errors > 0) return 1;
}

if (pngDir != null)
{
    sw.Restart();
    Directory.CreateDirectory(pngDir);
    int minE = manifest.Tiles.Min(t => t.E), maxE = manifest.Tiles.Max(t => t.E);
    int minN = manifest.Tiles.Min(t => t.N), maxN = manifest.Tiles.Max(t => t.N);
    int step = ChunkFormat.GridSize - 1; // 500 px per tile, shared edges overlap
    int width = (maxE - minE + 1) * step + 1;
    int height = (maxN - minN + 1) * step + 1;
    var elev = new float[width * height];
    var shadePix = new byte[width * height];
    float globalMin = manifest.Tiles.Min(t => t.Min), globalMax = manifest.Tiles.Max(t => t.Max);

    foreach (var t in manifest.Tiles)
    {
        var chunk = LoadChunk(t.Id);
        int ox = (t.E - minE) * step, oy = (maxN - t.N) * step;
        for (int r = 0; r < ChunkFormat.GridSize; r++)
            for (int c = 0; c < ChunkFormat.GridSize; c++)
                elev[(oy + r) * width + ox + c] = (float)chunk.HeightMetersAt(c, r);
    }

    var heightPix = new byte[width * height];
    for (int i = 0; i < elev.Length; i++)
        heightPix[i] = (byte)Math.Clamp((elev[i] - globalMin) / (globalMax - globalMin) * 255.0, 0, 255);

    // hillshade, light from the northwest — makes any seam step brutally visible
    (double lx, double ly, double lz) = (-0.5, 0.7, -0.5);
    double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
    (lx, ly, lz) = (lx / ll, ly / ll, lz / ll);
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int xl = Math.Max(x - 1, 0), xr = Math.Min(x + 1, width - 1);
            int yu = Math.Max(y - 1, 0), yd = Math.Min(y + 1, height - 1);
            double dzdx = (elev[y * width + xr] - elev[y * width + xl]) / ((xr - xl) * ChunkFormat.SpacingM);
            double dzdy = (elev[yd * width + x] - elev[yu * width + x]) / ((yd - yu) * ChunkFormat.SpacingM);
            double nl = Math.Sqrt(dzdx * dzdx + 1 + dzdy * dzdy);
            double dot = (-dzdx * lx + ly + -dzdy * lz) / nl;
            shadePix[y * width + x] = (byte)Math.Clamp(dot * 255.0, 0, 255);
        }

    PngWriter.WriteGray8(Path.Combine(pngDir, "mosaic_height.png"), heightPix, width, height);
    PngWriter.WriteGray8(Path.Combine(pngDir, "mosaic_shade.png"), shadePix, width, height);
    Console.WriteLine($"PNG mosaics ({width}x{height}) written to {pngDir} in {sw.Elapsed.TotalSeconds:F1}s");
}

return 0;
