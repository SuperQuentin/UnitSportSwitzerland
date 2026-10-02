using System.Diagnostics;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using UnitSport.Tools.RoadGen.Rewrite;

// swissALTI3D XYZ zips -> .terr chunk files + manifest.json
// Usage:
//   dotnet run --project tools/TerrainPreprocessor -c Release -- --in <dir> [--in <dir> ...] --out terrain_chunks
//       [--temp <cache dir>] [--jobs N] [--io-jobs N] [--force] [--fresh] [--verify] [--dump-png <dir>]
//   ... --out terrain_chunks --photos [--tiles-file f] [--io-jobs N] [--force]   (SWISSIMAGE, PhotoStage)
// --in is searched recursively and may be repeated (sources can live on any drive); the build is
// incremental — see TerrainBuild.

var inDirs = new List<string>();
string? outDir = null, tempDir = null, pngDir = null;
string? tlmGpkg = null, routeKeys = null, buildingsGpkg = null, gwrPath = null;
bool verify = false;
bool roadsOnly = false, featuresOnly = false, doCover = false, doPlaces = false, placesOnly = false;
// --tiles-file: feature passes only touch these tiles ("E-N" in km per line), so adding one valley
// to a built country does not re-extract every road in it
string? tilesFile = null;
// hand-traced cover TLM lacks (see docs/notes/tools/land-cover.md); --cover-only skips the road
// stage and the network stage after it
string? coverOverrides = File.Exists("docs/data/cover_overrides.json") ? "docs/data/cover_overrides.json" : null;
bool coverOnly = false;
bool coarseOnly = false, horizonOnly = false, photosOnly = false;
bool force = false, fresh = false;
string? franceBox = null;
// optional OpenStreetMap overlay (#118): a region-wide intermediate for the road network stage
string? osmPbf = null;
bool osmCheck = false;
int jobs = Environment.ProcessorCount;
int ioJobs = 4;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--in": inDirs.Add(args[++i]); break;
        case "--out": outDir = args[++i]; break;
        case "--temp": tempDir = args[++i]; break;
        case "--dump-png": pngDir = args[++i]; break;
        case "--tlm": tlmGpkg = args[++i]; break;
        case "--route-keys": routeKeys = args[++i]; break;
        case "--buildings": buildingsGpkg = args[++i]; break;
        case "--gwr": gwrPath = args[++i]; break;
        case "--cover": doCover = true; break;
        case "--cover-only": doCover = coverOnly = featuresOnly = true; break;
        case "--cover-overrides": coverOverrides = args[++i]; break;
        case "--places": doPlaces = true; break;
        // the place index alone: --places with --tlm (for summits) would otherwise re-run roads
        case "--places-only": doPlaces = placesOnly = true; featuresOnly = true; break;
        case "--tiles-file": tilesFile = args[++i]; break;
        case "--roads-only": roadsOnly = true; break;
        case "--features-only": featuresOnly = true; break;
        case "--coarse": coarseOnly = true; break;
        case "--horizon": horizonOnly = true; break;
        case "--photos": photosOnly = true; break;
        case "--verify": verify = true; break;
        case "--france": franceBox = args[++i]; break;
        case "--osm-overlay": osmPbf = args[++i]; break;
        case "--osm-check": osmCheck = true; break;
        case "--jobs": jobs = int.Parse(args[++i]); break;
        case "--io-jobs": ioJobs = int.Parse(args[++i]); break;
        case "--force": force = true; break;
        case "--fresh": fresh = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (osmCheck) return OsmOverlay.SelfCheck();

// ---- OSM overlay: OSM attributes conflated onto TLM road lines, for the built tiles ----------
// Standalone and region-wide (not per batch), so it covers the whole region however the feature
// passes were split. Without --osm-overlay nothing here runs and no build output changes.
if (osmPbf != null)
{
    if (outDir == null || tlmGpkg == null)
    {
        Console.Error.WriteLine("--osm-overlay <pbf> requires --out <chunk dir> and --tlm <gpkg>");
        return 2;
    }
    var manifestPath = Path.Combine(outDir, "manifest.json");
    var region = tilesFile != null ? ReadTilesFile(tilesFile)
        : File.Exists(manifestPath) ? TerrainManifest.FromJson(File.ReadAllText(manifestPath)).Tiles.Select(t => t.Id).ToHashSet()
        : new HashSet<TileId>();
    return OsmOverlay.Run(osmPbf, tlmGpkg, tempDir ?? outDir.TrimEnd('/', '\\') + "_temp", region, jobs);
}

// ---- SWISSIMAGE photos: one aerial JPEG per tile, for the realistic styles' terrain -------------
// Standalone: it needs only the manifest (or --tiles-file), and fetches what is missing.
if (photosOnly)
{
    if (outDir == null)
    {
        Console.Error.WriteLine("--photos requires --out <chunk dir>");
        return 2;
    }
    var manifestPath = Path.Combine(outDir, "manifest.json");
    var region = tilesFile != null ? ReadTilesFile(tilesFile)
        : File.Exists(manifestPath) ? TerrainManifest.FromJson(File.ReadAllText(manifestPath)).Tiles.Select(t => t.Id).ToHashSet()
        : new HashSet<TileId>();
    return await PhotoStage.Run(outDir, region, ioJobs, force);
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

if (outDir == null || (inDirs.Count == 0 && !roadsOnly && !featuresOnly))
{
    Console.Error.WriteLine("Required: --in <source dir> --out <chunk dir>");
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
    return RunFeatures(TerrainManifest.FromJson(File.ReadAllText(Path.Combine(outDir, "manifest.json"))));
}

// ---- terrain: sources -> .terr + .terrc, parsed and finished in one parallel pass -------
var sw = Stopwatch.StartNew();
var sources = TerrainBuild.Discover(inDirs);
if (sources.Count == 0)
{
    Console.Error.WriteLine($"No swissalti3d 0.5 m *.xyz(.zip) files found under {string.Join(", ", inDirs)}");
    return 1;
}
Console.WriteLine($"Found {sources.Count} source tiles: E {sources.Min(t => t.Id.E)}..{sources.Max(t => t.Id.E)}, "
    + $"N {sources.Min(t => t.Id.N)}..{sources.Max(t => t.Id.N)}");

var manifest = TerrainBuild.Run(sources, outDir, tempDir, new TerrainBuild.Options
{
    Jobs = jobs, IoJobs = ioJobs, Force = force, Fresh = fresh, Verify = verify,
});
if (manifest == null) return 1;
File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToJson());
// the far horizon is cut from the same tiles, one file for the whole region
if (HorizonStage.Run(outDir, jobs) is var hrc && hrc != 0) return hrc;
Console.WriteLine($"Terrain done in {sw.Elapsed.TotalSeconds:F1}s -> {manifest.Tiles.Count} chunks, " +
                  $"heights {manifest.Tiles.Min(t => t.Min):F0}..{manifest.Tiles.Max(t => t.Max):F0} m");

if (verify && TerrainBuild.VerifySeams(outDir, manifest.Tiles.Select(t => t.Id), jobs) > 0)
    return 1;

// ---- roads (optional, needs the terrain chunks for draping) ------------------------
if (tlmGpkg != null && RunFeatures(manifest) is var frc && frc != 0) return frc;

ChunkGrid LoadChunk(TileId id)
{
    using var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)));
    return ChunkCodec.Decode(fs);
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

// Batched: loading every chunk grid at once is ~2 MB x tile count (13 GB for the 6,699-tile
// import) before feature data is even extracted. Tiles are ordered by (E, N) so each batch is a
// compact strip and its bbox query stays tight.
int RunFeatures(TerrainManifest existing)
{
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
        if (placesOnly || (tlmGpkg == null && buildingsGpkg == null)) return 0;
    }

    const int BatchSize = 400;
    var ordered = existing.Tiles.OrderBy(t => t.E).ThenBy(t => t.N).ToList();
    if (tilesFile != null)
    {
        var wanted = ReadTilesFile(tilesFile);
        ordered = ordered.Where(t => wanted.Contains(t.Id)).ToList();
        Console.WriteLine($"--tiles-file: {ordered.Count} of {wanted.Count} listed tiles are built");
        if (ordered.Count == 0) return 0;
    }
    int batches = (ordered.Count + BatchSize - 1) / BatchSize;

    Dictionary<TileId, ChunkGrid> LoadBatch(int b, out List<ManifestTile> slice)
    {
        slice = ordered.Skip(b * BatchSize).Take(BatchSize).ToList();
        var grids = new System.Collections.Concurrent.ConcurrentDictionary<TileId, ChunkGrid>();
        Parallel.ForEach(slice, new ParallelOptions { MaxDegreeOfParallelism = jobs }, t =>
        {
            using var fs = File.OpenRead(Path.Combine(outDir!, ChunkFormat.ChunkFileName(t.Id)));
            grids[t.Id] = ChunkCodec.Decode(fs);
        });
        return new Dictionary<TileId, ChunkGrid>(grids);
    }

    // roads first, every batch, then the network stage, which sees every batch at once (a junction
    // on a batch seam needs both sides); cover masks trees off the network stage's final lines
    if (tlmGpkg != null && !coverOnly)
    {
        for (int b = 0; b < batches; b++)
        {
            var batch = LoadBatch(b, out var slice);
            Console.WriteLine($"=== roads, batch {b + 1}/{batches}: {slice.Count} tiles, E {slice[0].E}..{slice[^1].E} ===");
            int rc = RoadStage.Run(tlmGpkg, routeKeys, outDir!, tempDir!, batch);
            if (rc != 0) return rc;
        }
        int nrc = RoadStage.RunNetwork(outDir!, tempDir!, ordered.Select(t => t.Id).ToList());
        if (nrc != 0) return nrc;
    }

    if (!doCover && buildingsGpkg == null) return 0;
    if (doCover && tlmGpkg == null)
    {
        Console.Error.WriteLine("--cover requires --tlm <swisstlm3d .gpkg>");
        return 2;
    }
    for (int b = 0; b < batches; b++)
    {
        var batch = LoadBatch(b, out var slice);
        Console.WriteLine($"=== batch {b + 1}/{batches}: {slice.Count} tiles, E {slice[0].E}..{slice[^1].E} ===");
        if (doCover)
        {
            int rc = CoverStage.Run(tlmGpkg!, outDir!, batch, coverOverrides, RawRoads.DirFor(tempDir!));
            if (rc != 0) return rc;
        }
        if (buildingsGpkg != null)
        {
            int rc = BuildingStage.Run(buildingsGpkg, gwrPath, outDir!, batch);
            if (rc != 0) return rc;
        }
    }
    return 0;
}

static HashSet<TileId> ReadTilesFile(string path)
{
    var tiles = new HashSet<TileId>();
    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.Split('#')[0].Trim();
        if (line.Length == 0) continue;
        var parts = line.Split('-', '_', ',');
        tiles.Add(new TileId(int.Parse(parts[0]), int.Parse(parts[1])));
    }
    return tiles;
}
