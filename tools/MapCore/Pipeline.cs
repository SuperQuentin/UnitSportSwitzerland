using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Map;

[Flags]
public enum Layers
{
    None = 0,
    Terrain = 1,
    /// <summary>swissTLM3D: roads, railways, rivers, land cover and trees.</summary>
    Roads = 2,
    /// <summary>swissBUILDINGS3D, via a GDAL export.</summary>
    Buildings = 4,
    /// <summary>GWR register: building use, year and storeys; also what the place index is made from.</summary>
    Cadastre = 8,
    /// <summary>Veloland / Mountainbikeland route flags on roads, via a GDAL export.</summary>
    Routes = 16,
    /// <summary>places.json, the in-game Tab search.</summary>
    Places = 32,
    /// <summary>
    /// OpenStreetMap road attributes (one-way, lanes, sidewalks...) conflated onto TLM lines.
    /// Off unless picked by name: tiles built with it are an ODbL derived database
    /// (docs/notes/tools/osm-odbl-licence.md).
    /// </summary>
    Osm = 64,
}

/// <summary>What MapSetup remembers between runs, in terrain_chunks_temp/mapsetup.json.</summary>
public sealed class SetupState
{
    public List<string> Tiles { get; set; } = new();
    public Layers Layers { get; set; } = Layers.Terrain | Layers.Roads | Layers.Buildings | Layers.Cadastre | Layers.Places;
    /// <summary>Feature layers already extracted per tile ("E-N"), so a re-run does not redo them.</summary>
    public Dictionary<string, Layers> FeaturesDone { get; set; } = new();
    /// <summary>Cantons (lower-case codes, or "ch") the extracted GWR data.sqlite was made from; null = unknown.</summary>
    public List<string>? GwrCantons { get; set; }
    /// <summary>Tiles the road network stage (RoadGen) has already been run over ("E-N").</summary>
    public HashSet<string> JunctionsDone { get; set; } = new();
    public DateTime? LastRun { get; set; }

    public static SetupState Load(Paths paths)
    {
        try
        {
            if (File.Exists(paths.StateFile))
                return JsonSerializer.Deserialize<SetupState>(File.ReadAllText(paths.StateFile)) ?? new();
        }
        catch (Exception) { /* start over rather than refuse to run */ }
        return new();
    }

    public void Save(Paths paths)
    {
        Directory.CreateDirectory(paths.Temp);
        File.WriteAllText(paths.StateFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Key(TileId t) => $"{t.E}-{t.N}";
}

/// <summary>Everything a plan is made from.</summary>
public sealed class SetupContext
{
    public required Paths Paths { get; init; }
    public required CountryData Country { get; init; }
    public required LocalState Local { get; init; }
    public required Selection Selection { get; init; }
    public required Layers Layers { get; init; }
    public required Stats Stats { get; init; }
    public required SetupState State { get; init; }
    public required string? Python { get; init; }
    public int Cores { get; init; } = Environment.ProcessorCount;
}

/// <summary>One stage of the pipeline, with its estimate and, unless skipped, how to run it.</summary>
public sealed class Step
{
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public long DownloadBytes { get; init; }
    /// <summary>Space the step leaves on disk, on the drive of <see cref="DiskPath"/>.</summary>
    public long DiskBytes { get; init; }
    public string? DiskPath { get; init; }
    public double Seconds { get; init; }
    /// <summary>Why the step will not run (already done, layer off, GDAL missing); null when it runs.</summary>
    public string? Skip { get; init; }
    public Func<StepRun, Task<bool>>? Run { get; init; }
}

/// <summary>
/// Turns a selection and a set of layers into the ordered list of steps the README describes,
/// each one skipped when what it would produce is already there. Every step shells out to the
/// existing tools (swiss_data.py, the GDAL exporters, TerrainPreprocessor, RoadGen), so this
/// class holds the order and the bookkeeping and none of the data handling.
/// </summary>
public static partial class Planner
{
    private const long BuiltBytesPerTile = 2_200_000;
    private const double TlmExtractFactor = 2.25;   // 4.8 GB zip -> 10.8 GB GeoPackage

    public static string SelectionTilesFile(Paths p) => Path.Combine(p.Temp, "mapsetup_selection.txt");
    public static string DownloadTilesFile(Paths p) => Path.Combine(p.Temp, "mapsetup_download.txt");
    public static string BuildingsOut(Paths p) => Path.Combine(p.BuildingsDir, "mapsetup_selection.gpkg");
    private static string NationwideBuildingsGpkg(Paths p) => Path.Combine(p.BuildingsDir, "buildings_ch.gpkg");
    private static string NationwideBuildingsZip(Paths p) => Path.Combine(p.BuildingsDir, "swissbuildings3d_3_0_2026_2056_5728.gdb.zip");

    public static List<Step> Build(SetupContext c)
    {
        var p = c.Paths;
        var steps = new List<Step>();
        var sel = c.Selection.Tiles.ToList();
        var stats = c.Stats;
        bool py = c.Python != null;
        // Terrain, swissTLM3D and GWR are downloaded by SwissDownload now (#515 phase 1), so only the
        // datasets whose resolvers are not ported yet still gate on Python.
        string noPython = "Python not found (needed for this dataset)";

        // ---- which tiles need what -------------------------------------------------------
        var toDownload = sel.Where(t => !c.Local.Downloaded.Contains(t) && !c.Local.Built.Contains(t)).ToList();
        var toBuild = sel.Where(t => !c.Local.Built.Contains(t)).ToList();
        bool wantRoads = c.Layers.HasFlag(Layers.Roads);
        // No GDAL gate since #537: the preprocessor reads the FileGDB itself, so the buildings
        // layer only needs its sheets downloaded.
        bool wantBuildings = c.Layers.HasFlag(Layers.Buildings);
        bool haveNationwideGpkg = File.Exists(NationwideBuildingsGpkg(p));
        var featureTiles = sel.Where(t =>
            (wantRoads && !RoadsDone(c, t)) || (wantBuildings && !BuildingsDone(c, t))).ToList();

        long downloadBytes = toDownload.Sum(c.Country.SizeBytes);
        steps.Add(new Step
        {
            Title = "Download terrain tiles",
            Detail = $"{toDownload.Count:N0} swissALTI3D tiles"
                     + (sel.Count > toDownload.Count ? $" ({sel.Count - toDownload.Count:N0} already here)" : ""),
            DownloadBytes = downloadBytes,
            DiskBytes = downloadBytes,
            DiskPath = p.AltiDir,
            Seconds = downloadBytes / stats.EffectiveDownload + (toDownload.Count > 0 ? 5 : 0),
            // No Python needed since #515 phase 1: SwissDownload talks to the STAC API itself, and
            // shares swiss_data.py's manifest, so neither tool re-downloads what the other fetched.
            Skip = toDownload.Count == 0 ? "all tiles already downloaded or built" : null,
            Run = r =>
            {
                // still written: the terminal tool's --resume and swiss_data.py --tiles-file read it
                WriteTiles(DownloadTilesFile(p), toDownload);
                return r.Download(() => SwissDownload.AltiAsync(p.AltiDir, toDownload, r.Progress, r.Cancellation));
            },
        });

        // ---- swissTLM3D --------------------------------------------------------------------
        long tlmZip = c.Country.Extras.GetValueOrDefault("swisstlm3d", 4_800_000_000);
        bool needTlm = wantRoads && !c.Local.TlmExtracted;
        steps.Add(new Step
        {
            Title = "Download swissTLM3D",
            Detail = "roads, rail, rivers, land cover, trees — one nationwide file, fetched once",
            DownloadBytes = needTlm && !c.Local.TlmZip ? tlmZip : 0,
            DiskBytes = needTlm && !c.Local.TlmZip ? tlmZip : 0,
            DiskPath = p.TlmDir,
            Seconds = tlmZip / stats.EffectiveDownload + 5,
            Skip = !wantRoads ? "roads layer off" : !needTlm || c.Local.TlmZip ? "already here" : null,
            Run = r => r.Download(() => SwissDownload.TlmAsync(p.TlmDir, r.Progress, r.Cancellation)),
        });

        // ---- buildings sheets -----------------------------------------------------------------
        var sheets = c.Country.Sheets.Where(s => sel.Any(s.Touches)).ToList();
        bool haveNationwideZip = File.Exists(NationwideBuildingsZip(p));
        var sheetsToGet = sheets.Where(s => !c.Local.BuildingSheets.Contains(s.Key)).ToList();
        bool needSheets = wantBuildings && featureTiles.Count > 0 && !haveNationwideGpkg && !haveNationwideZip;
        long sheetBytes = sheetsToGet.Sum(s => s.Size);
        steps.Add(new Step
        {
            Title = "Download buildings",
            Detail = $"{sheets.Count} swissBUILDINGS3D sheets touch the selection",
            DownloadBytes = needSheets ? sheetBytes : 0,
            DiskBytes = needSheets ? sheetBytes : 0,
            DiskPath = p.BuildingsDir,
            Seconds = sheetBytes / stats.EffectiveDownload + 5,
            Skip = !c.Layers.HasFlag(Layers.Buildings) ? "buildings layer off"
                 : haveNationwideGpkg || haveNationwideZip ? "nationwide buildings already here"
                 : !needSheets || sheetsToGet.Count == 0 ? "already here" : !py ? noPython : null,
            Run = r =>
            {
                WriteTiles(SelectionTilesFile(p), sel);
                return r.SwissData(["--out", p.BuildingsDir, "swissbuildings3d", "--tiles-file", SelectionTilesFile(p)]);
            },
        });

        // ---- GWR cadastre ----------------------------------------------------------------------
        var cantons = sel.Select(t => c.Country.CantonAt(t)?.Code.ToLowerInvariant()).OfType<string>().Distinct().ToList();
        string gwrPick = cantons.Count == 1 ? cantons[0] : "ch";
        bool gwrCovered = c.Local.GwrSqlite && (c.State.GwrCantons is not { } had
                                                || had.Contains("ch") || cantons.All(had.Contains));
        bool wantGwr = c.Layers.HasFlag(Layers.Cadastre) || c.Layers.HasFlag(Layers.Places);
        long gwrZip = c.Country.Extras.GetValueOrDefault("gwr_" + gwrPick, gwrPick == "ch" ? 945_000_000 : 60_000_000);
        long gwrSqlite = gwrPick == "ch" ? 1_710_000_000 : gwrZip * 2;
        steps.Add(new Step
        {
            Title = "Download GWR cadastre",
            Detail = gwrPick == "ch" ? "national register (the selection spans several cantons)" : $"register of {gwrPick.ToUpperInvariant()}",
            DownloadBytes = wantGwr && !gwrCovered && !c.Local.GwrZips.Contains(gwrPick) ? gwrZip : 0,
            DiskBytes = wantGwr && !gwrCovered && !c.Local.GwrZips.Contains(gwrPick) ? gwrZip : 0,
            DiskPath = p.GwrDir,
            Seconds = gwrZip / stats.EffectiveDownload + 3,
            Skip = !wantGwr ? "cadastre and places off" : gwrCovered ? "data.sqlite already covers the selection"
                 : c.Local.GwrZips.Contains(gwrPick) ? "already here" : null,
            Run = r => r.Download(() => SwissDownload.GwrAsync(p.GwrDir, gwrPick, r.Progress, r.Cancellation)),
        });

        // ---- cycle routes -----------------------------------------------------------------------
        bool wantRoutes = c.Layers.HasFlag(Layers.Routes) && wantRoads;
        long routesZip = c.Country.Extras.GetValueOrDefault("veloland", 40_000_000) + c.Country.Extras.GetValueOrDefault("mountainbikeland", 50_000_000);
        steps.Add(new Step
        {
            Title = "Download cycle routes",
            Detail = "Veloland + Mountainbikeland (ASTRA)",
            DownloadBytes = wantRoutes && !c.Local.RouteKeys && !c.Local.RoutesZips ? routesZip : 0,
            DiskBytes = wantRoutes && !c.Local.RouteKeys && !c.Local.RoutesZips ? routesZip : 0,
            DiskPath = p.RoutesDir,
            Seconds = routesZip / stats.EffectiveDownload + 4,
            // Neither Python nor GDAL since #537: the zips come down through SwissDownload and the
            // FileGDB inside them is read in-process.
            Skip = !wantRoutes ? "routes layer off"
                 : c.Local.RouteKeys || c.Local.RoutesZips ? "already here" : null,
            Run = async r =>
            {
                bool RoutesGdb(string key) => key.EndsWith("_2056.gdb.zip", StringComparison.OrdinalIgnoreCase);
                return await r.Download(() => SwissDownload.CollectionAsync(p.RoutesDir, "ch.astra.veloland",
                           null, r.Progress, r.Cancellation, assetFilter: RoutesGdb))
                    && await r.Download(() => SwissDownload.CollectionAsync(p.RoutesDir, "ch.astra.mountainbikeland",
                           null, r.Progress, r.Cancellation, assetFilter: RoutesGdb));
            },
        });

        // ---- OpenStreetMap (optional) -----------------------------------------------------------
        bool wantOsm = c.Layers.HasFlag(Layers.Osm) && wantRoads;
        long osmPbf = c.Country.Extras.GetValueOrDefault("osm", 550_000_000);
        steps.Add(new Step
        {
            Title = "Download OpenStreetMap",
            Detail = "Geofabrik Switzerland extract (ODbL), for one-way, lanes, sidewalks and cycleways",
            DownloadBytes = wantOsm && p.OsmPbf == null ? osmPbf : 0,
            DiskBytes = wantOsm && p.OsmPbf == null ? osmPbf : 0,
            DiskPath = p.OsmDir,
            Seconds = osmPbf / stats.EffectiveDownload + 3,
            Skip = !wantOsm ? "OSM layer off" : p.OsmPbf != null ? "already here" : !py ? noPython : null,
            Run = r => r.SwissData(["--out", p.OsmDir, "osm"]),
        });

        // ---- unpack ------------------------------------------------------------------------------
        steps.Add(new Step
        {
            Title = "Unpack swissTLM3D",
            Detail = "the preprocessor reads the GeoPackage itself",
            DiskBytes = needTlm ? (long)(tlmZip * TlmExtractFactor) : 0,
            DiskPath = p.TlmDir,
            Seconds = tlmZip * TlmExtractFactor / stats.ExtractBytesPerSec,
            Skip = !wantRoads ? "roads layer off" : !needTlm ? "already unpacked" : null,
            Run = r => r.Extract(p.TlmDir, "*.gpkg.zip", e => e.Name.EndsWith(".gpkg", StringComparison.OrdinalIgnoreCase), p.TlmDir),
        });
        steps.Add(new Step
        {
            Title = "Unpack GWR cadastre",
            Detail = $"data.sqlite from gwr_{gwrPick}.zip",
            DiskBytes = wantGwr && !gwrCovered ? gwrSqlite : 0,
            DiskPath = p.GwrDir,
            Seconds = gwrSqlite / stats.ExtractBytesPerSec,
            Skip = !wantGwr ? "cadastre and places off" : gwrCovered ? "data.sqlite already covers the selection" : null,
            Run = async r =>
            {
                bool ok = await r.Extract(p.GwrDir, $"gwr_{gwrPick}.zip", e => e.Name == "data.sqlite", p.GwrDir, overwrite: true);
                if (ok)
                {
                    c.State.GwrCantons = gwrPick == "ch" ? ["ch"] : cantons;
                    c.State.Save(p);
                }
                return ok;
            },
        });

        // ---- GDAL exports --------------------------------------------------------------------------
        steps.Add(new Step
        {
            Title = "Export cycle routes",
            Detail = "route_keys.sqlite: which roads are on a signed route",
            Seconds = 20,
            Skip = !wantRoutes ? "routes layer off" : c.Local.RouteKeys ? "already exported" : null,
            Run = r => r.Tool("TerrainPreprocessor",
                ["--export-route-keys", p.RoutesDir, "--temp", p.Temp], LineProgress.None),
        });

        var bounds = c.Selection.Bounds();
        // No "export buildings" step any more (#537): the preprocessor reads the swissBUILDINGS3D
        // FileGDB zips itself, so there is no GeoPackage to convert to first and no GDAL to need.

        // ---- processing ------------------------------------------------------------------------------
        int builtTotal = c.Local.Built.Count + toBuild.Count;
        steps.Add(new Step
        {
            Title = "Build terrain",
            Detail = $"{toBuild.Count:N0} tiles -> .terr + .terrc, then the far horizon",
            DiskBytes = toBuild.Count * 510_000L,
            DiskPath = p.Chunks,
            Seconds = stats.ToolStartSec + toBuild.Count * stats.TerrainCoreSecPerTile / c.Cores
                      + builtTotal * 0.0006 + c.Local.Downloaded.Count * 0.0005,
            Skip = toBuild.Count == 0 ? "all selected tiles already built" : null,
            Run = async r =>
            {
                var clock = Stopwatch.StartNew();
                bool ok = await r.Tool("TerrainPreprocessor", ["--in", p.AltiDir, "--out", p.Chunks], LineProgress.Counter);
                if (ok && toBuild.Count >= 20)
                    stats.TerrainCoreSecPerTile = Stats.Blend(stats.TerrainCoreSecPerTile,
                        Math.Max(0, clock.Elapsed.TotalSeconds - stats.ToolStartSec) * c.Cores / toBuild.Count);
                return ok;
            },
        });

        string? osmSkip = !wantOsm ? "OSM layer off"
            : toBuild.Count == 0 && featureTiles.Count == 0 && File.Exists(Path.Combine(p.Temp, "osm_overlay.tsv"))
              && File.Exists(Path.Combine(p.Temp, "osm_nodes.tsv")) ? "overlay up to date" : null; // osm_nodes.tsv: #347
        // Before the extraction: the road network stage at its end reads the overlay. The whole
        // built region, not just the selection, so a second selection does not shrink it.
        steps.Add(new Step
        {
            Title = "OpenStreetMap overlay",
            Detail = "OSM road attributes, signals and turn restrictions matched to TLM lines (osm_overlay.tsv, osm_nodes.tsv, temp dir)",
            Seconds = 10 + builtTotal * 0.01,
            Skip = osmSkip,
            Run = r =>
            {
                if (p.TlmGpkg is not { } tlm) { r.Fail("no swissTLM3D GeoPackage in " + p.TlmDir); return Task.FromResult(false); }
                if (p.OsmPbf is not { } pbf) { r.Fail("no switzerland-*.osm.pbf in " + p.OsmDir); return Task.FromResult(false); }
                return r.Tool("TerrainPreprocessor", ["--out", p.Chunks, "--tlm", tlm, "--osm-overlay", pbf], LineProgress.None);
            },
        });

        var tlmGpkgFuture = () => p.TlmGpkg;
        steps.Add(new Step
        {
            Title = "Extract roads, cover and buildings",
            Detail = $"{featureTiles.Count:N0} tiles: "
                     + string.Join(", ", new[] { wantRoads ? "roads, rivers, land cover, trees" : null, wantBuildings ? "buildings" : null }.OfType<string>()),
            DiskBytes = featureTiles.Count * (BuiltBytesPerTile - 510_000L),
            DiskPath = p.Chunks,
            Seconds = stats.ToolStartSec * 2 + featureTiles.Count * stats.FeaturesCoreSecPerTile / c.Cores,
            Skip = featureTiles.Count == 0 ? (wantRoads || wantBuildings ? "already extracted for every tile" : "roads and buildings off") : null,
            Run = async r =>
            {
                WriteTiles(SelectionTilesFile(p), featureTiles);
                var args = new List<string> { "--out", p.Chunks, "--features-only", "--tiles-file", SelectionTilesFile(p) };
                if (wantRoads)
                {
                    if (tlmGpkgFuture() is not { } tlm) { r.Fail("no swissTLM3D GeoPackage in " + p.TlmDir); return false; }
                    args.AddRange(["--tlm", tlm, "--cover"]);
                    if (File.Exists(p.RouteKeys)) args.AddRange(["--route-keys", p.RouteKeys]);
                    // the cover pass ends with the lake and river beds (#298): surveyed where the zips are
                    if (Directory.Exists(p.BathyDir)) args.AddRange(["--bathy", p.BathyDir]);
                }
                if (wantBuildings)
                {
                    // A GeoPackage left by an older run (or the nationwide one) is still read; a
                    // fresh region goes straight from the published sheet zips (#537).
                    string gpkg = File.Exists(NationwideBuildingsGpkg(p)) ? NationwideBuildingsGpkg(p) : BuildingsOut(p);
                    if (File.Exists(gpkg)) args.AddRange(["--buildings", gpkg]);
                    else
                        foreach (var sheet in sheets)
                        {
                            string zip = Directory.Exists(p.BuildingsDir)
                                ? Directory.EnumerateFiles(p.BuildingsDir, $"swissbuildings3d_3_0_*_{sheet.Key}_*.gdb.zip").FirstOrDefault() ?? ""
                                : "";
                            if (zip.Length > 0) args.AddRange(["--buildings-gdb", zip]);
                        }
                    if (File.Exists(p.GwrSqlite)) args.AddRange(["--gwr", p.GwrSqlite]);
                }
                var clock = Stopwatch.StartNew();
                bool ok = await r.Tool("TerrainPreprocessor", args, LineProgress.Batch);
                if (!ok) return false;
                var done = (wantRoads ? Layers.Roads : 0) | (wantBuildings ? Layers.Buildings : 0);
                foreach (var t in featureTiles)
                {
                    c.State.FeaturesDone[SetupState.Key(t)] = c.State.FeaturesDone.GetValueOrDefault(SetupState.Key(t)) | done;
                    // TerrainPreprocessor ends the road extraction with the network stage
                    if (wantRoads) c.State.JunctionsDone.Add(SetupState.Key(t));
                }
                c.State.Save(p);
                if (featureTiles.Count >= 20)
                    stats.FeaturesCoreSecPerTile = Stats.Blend(stats.FeaturesCoreSecPerTile,
                        Math.Max(0, clock.Elapsed.TotalSeconds - 2 * stats.ToolStartSec) * c.Cores / featureTiles.Count);
                return true;
            },
        });

        steps.Add(new Step
        {
            Title = "Road network (RoadGen)",
            Detail = "junctions and v3 road attributes again, from the kept raw roads (new OSM overlay)",
            Seconds = 3 + sel.Count * stats.RoadGenSecPerTile,
            // the extraction step ends with the network stage for the tiles it extracted; this
            // reruns it for the selection when the overlay changed or a tile predates the stage
            Skip = !wantRoads ? "roads layer off"
                 : sel.All(featureTiles.Contains) ? "done by the extraction step"
                 : osmSkip == null ? null
                 : sel.All(t => featureTiles.Contains(t) || c.State.JunctionsDone.Contains(SetupState.Key(t))) ? "all done" : null,
            Run = async r =>
            {
                WriteTiles(SelectionTilesFile(p), sel);
                // --skip-rewritten: a tile rewritten before the raw input was kept is left alone,
                // never trimmed twice; every other tile rebuilds from its raw input
                bool ok = await r.Tool("RoadGen", ["--rewrite", "--chunks", p.Chunks, "--tiles-file", SelectionTilesFile(p), "--skip-rewritten"], LineProgress.None);
                if (ok)
                {
                    foreach (var t in sel) c.State.JunctionsDone.Add(SetupState.Key(t));
                    c.State.Save(p);
                }
                return ok;
            },
        });

        bool wantPlaces = c.Layers.HasFlag(Layers.Places);
        steps.Add(new Step
        {
            Title = "Place index",
            Detail = "places.json for the in-game Tab search (towns, summits, passes)",
            Seconds = 6 + builtTotal * 0.0003,
            Skip = !wantPlaces ? "places layer off"
                 : toBuild.Count == 0 && File.Exists(Path.Combine(p.Chunks, PlaceIndex.FileName)) ? "no new terrain" : null,
            Run = r =>
            {
                if (!File.Exists(p.GwrSqlite)) { r.Fail("places need the GWR data.sqlite"); return Task.FromResult(false); }
                var args = new List<string> { "--out", p.Chunks, "--places-only", "--gwr", p.GwrSqlite };
                if (p.TlmGpkg is { } tlm) args.AddRange(["--tlm", tlm]);
                return r.Tool("TerrainPreprocessor", args, LineProgress.None);
            },
        });

        // the C# tools are built once, just before the first step that needs them
        int firstTool = steps.FindIndex(st => st.Title == "Build terrain");
        bool anyTool = steps.Skip(firstTool).Any(st => st.Skip == null);
        steps.Insert(firstTool, new Step
        {
            Title = "Prepare processing tools",
            Detail = "dotnet build of RoadGen (Release); the preprocessor runs in-process",
            // No repository means the game, which has the processing tools compiled into it and no
            // .NET SDK to build anything with: there is nothing for this step to do there.
            Seconds = p.Root == null ? 0
                : File.Exists(StepRun.ToolDll(p, "TerrainPreprocessor")) && File.Exists(StepRun.ToolDll(p, "RoadGen")) ? 8 : 45,
            Skip = p.Root == null ? "built into the game" : anyTool ? null : "nothing to process",
            Run = r => r.BuildTools(),
        });
        return steps;
    }

    private static bool RoadsDone(SetupContext c, TileId t) =>
        c.State.FeaturesDone.GetValueOrDefault(SetupState.Key(t)).HasFlag(Layers.Roads)
        || File.Exists(Path.Combine(c.Paths.Chunks, CoverFormat.FileName(t)));

    private static bool BuildingsDone(SetupContext c, TileId t) =>
        c.State.FeaturesDone.GetValueOrDefault(SetupState.Key(t)).HasFlag(Layers.Buildings)
        || File.Exists(Path.Combine(c.Paths.Chunks, BuildingFormat.FileName(t)));

    private static void WriteTiles(string path, IEnumerable<TileId> tiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, tiles.OrderBy(t => t.E).ThenBy(t => t.N).Select(SetupState.Key));
    }
}

public enum LineProgress { None, Counter, Batch, Source }

/// <summary>
/// Where a running step reports to. The terminal tool implements this over a Spectre progress
/// bar; the game implements it over <c>DownloadJob</c>, which the map screen polls. Keeping it
/// this small is the point: a step may only move a bar, say what it is doing, and write its log.
/// </summary>
public interface IStepProgress
{
    /// <summary>How far the step has got, 0-100.</summary>
    double Value { set; }

    /// <summary>
    /// The step's latest line, shown beside the bar. Already trimmed and shortened by
    /// <see cref="StepRun"/>; an implementation that renders markup must escape it itself.
    /// </summary>
    void Show(string text);

    /// <summary>Every line the step produced, verbatim, for the step's log file.</summary>
    void Log(string line);
}

/// <summary>
/// Runs one step: starts the tool, logs every line to the step's log file, turns the tool's own
/// progress output into the progress bar, and shows its latest line beside it.
/// </summary>
public sealed partial class StepRun
{
    private readonly SetupContext _c;
    private readonly IStepProgress _progress;
    private readonly string _title;
    private readonly StreamWriter _log;
    private readonly CancellationToken _ct;

    public string? Error { get; private set; }

    public StepRun(SetupContext c, IStepProgress progress, string title, StreamWriter log, CancellationToken ct)
    {
        _c = c;
        _progress = progress;
        _title = title;
        _log = log;
        _ct = ct;
    }

    public void Fail(string message)
    {
        Error = message;
        Log("error: " + message);
    }

    private void Log(string line)
    {
        lock (_log) _log.WriteLine(line);
        _progress.Log(line);
    }

    /// <summary>The step's latest line, shortened to fit beside a progress bar.</summary>
    private void Show(string line)
    {
        var text = line.Trim();
        if (text.Length > 60) text = text[..59] + "…";
        _progress.Show(text);
    }

    /// <summary>
    /// The built tool, under whichever target framework folder it landed in — the preprocessor moved
    /// from net9.0 to net8.0 when the game started hosting it (#515 phase 2), and a machine may
    /// still have the old build lying beside the new one.
    /// </summary>
    public static string ToolDll(Paths p, string name)
    {
        string release = Path.Combine(p.Tools, name, "bin", "Release");
        foreach (string framework in new[] { "net8.0", "net9.0" })
        {
            string dll = Path.Combine(release, framework, name + ".dll");
            if (File.Exists(dll)) return dll;
        }
        return Path.Combine(release, "net8.0", name + ".dll");
    }

    public async Task<bool> BuildTools()
    {
        foreach (var name in new[] { "TerrainPreprocessor", "RoadGen" })
        {
            Show("building " + name);
            if (await Exec("dotnet", ["build", Path.Combine(_c.Paths.Tools, name), "-c", "Release", "-nologo", "-v", "q"],
                    LineProgress.None) != 0)
            {
                Fail($"dotnet build {name} failed (see the log)");
                return false;
            }
        }
        _progress.Value = 100;
        return true;
    }

    /// <summary>
    /// Runs one of the C# tools. The preprocessor runs <b>in-process</b> since #515 phase 2 — no
    /// .NET SDK, no subprocess, and cancellation reaches its stage loops directly — which is what
    /// lets the game build tiles at all. RoadGen is still a subprocess: its entry point is
    /// top-level statements with no library seam, and its one step is skipped in practice (the
    /// extraction step does the network pass).
    /// </summary>
    public async Task<bool> Tool(string name, IReadOnlyList<string> args, LineProgress parse)
    {
        if (name == "TerrainPreprocessor")
        {
            Log($"$ {name} {string.Join(' ', args)}  (in-process)");
            int inProcess = await UnitSport.Tools.Preprocessor.Preprocessor.RunAsync(
                args, new ToolLog(this, parse), _ct);
            if (inProcess != 0) Fail($"{name} returned {inProcess}");
            return inProcess == 0;
        }

        if (_c.Paths.Root == null)
        {
            Fail($"{name} can only run from the repository, and this is not one");
            return false;
        }
        var dll = ToolDll(_c.Paths, name);
        if (!File.Exists(dll)) { Fail($"{dll} missing (the tools step did not run?)"); return false; }
        int code = await Exec("dotnet", [dll, .. args], parse);
        if (code != 0) Fail($"{name} exited with {code}");
        return code == 0;
    }

    /// <summary>
    /// The in-process preprocessor's output, turned into exactly what the subprocess path makes of
    /// the same lines — so a step's bar and its log read the same either way.
    /// </summary>
    private sealed class ToolLog : UnitSport.Tools.Preprocessor.IPreprocessorLog
    {
        private readonly StepRun _run;
        private readonly LineProgress _parse;

        public ToolLog(StepRun run, LineProgress parse)
        {
            _run = run;
            _parse = parse;
        }

        public void Line(string text) => _run.OnToolLine(text, _parse);

        public void Progress(string stage, double fraction)
        {
            if (stage.Length > 0) _run.Show(stage);
            _run._progress.Value = Math.Clamp(fraction * 100.0, 0, 100);
        }
    }

    /// <summary>One line of a tool's output: logged, shown, and scanned for its own counter.</summary>
    private void OnToolLine(string line, LineProgress parse)
    {
        Log(line);
        Show(line);
        var m = parse switch
        {
            LineProgress.Counter => CounterLine().Match(line),
            LineProgress.Batch or LineProgress.Source => BatchLine().Match(line),
            _ => Match.Empty,
        };
        if (m.Success && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double total) && total > 0)
            _progress.Value = 100.0 * double.Parse(m.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture) / total;
    }

    public async Task<bool> Python(string what, IReadOnlyList<string> args, LineProgress parse)
    {
        if (_c.Python == null) { Fail("Python not found"); return false; }
        int code = await Exec(_c.Python, args, parse);
        if (code != 0) Fail($"{what} exited with {code}");
        return code == 0;
    }

    /// <summary>The step's progress sink, for a stage that reports its own progress.</summary>
    public IStepProgress Progress => _progress;

    /// <summary>Cancelled when the player stops the download.</summary>
    public CancellationToken Cancellation => _ct;

    /// <summary>
    /// Runs one of the C# downloads (#515 phase 1) and folds what it measured back into this
    /// machine's rate, the way <see cref="SwissData"/> does for the Python tool it replaced.
    /// </summary>
    public async Task<bool> Download(Func<Task<DownloadResult>> download)
    {
        var result = await download();
        if (result.Error != null)
        {
            Fail(result.Error);
            return false;
        }
        Log($"{result.Files} files, {result.Bytes:N0} bytes in {result.Seconds:F1} s ({result.Skipped} already current)");
        if (result.Bytes > 50_000_000 && result.Seconds > 1)
            _c.Stats.DownloadBytesPerSec = Stats.Blend(_c.Stats.DownloadBytesPerSec, result.Bytes / result.Seconds);
        return true;
    }

    /// <summary>swiss_data.py with machine-readable progress; learns the download rate from it.</summary>
    public async Task<bool> SwissData(IReadOnlyList<string> args)
    {
        _download = (0, 0);
        bool ok = await Python("swiss_data.py", [_c.Paths.SwissData, "--progress-json", .. args], LineProgress.None);
        if (ok && _download.Bytes > 50_000_000 && _download.Seconds > 1)
            _c.Stats.DownloadBytesPerSec = Stats.Blend(_c.Stats.DownloadBytesPerSec, _download.Bytes / _download.Seconds);
        return ok;
    }

    private (long Bytes, double Seconds) _download;

    public Task<bool> Extract(string dir, string zipPattern, Func<ZipArchiveEntry, bool> pick, string outDir, bool overwrite = false)
    {
        return Task.Run(() =>
        {
            var zip = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, zipPattern).OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
                : null;
            if (zip == null) { Fail($"no {zipPattern} in {dir}"); return false; }
            using var archive = ZipFile.OpenRead(zip);
            var entries = archive.Entries.Where(pick).ToList();
            if (entries.Count == 0) { Fail($"nothing to unpack in {Path.GetFileName(zip)}"); return false; }
            long total = entries.Sum(e => e.Length), done = 0;
            var buffer = new byte[1 << 20];
            foreach (var entry in entries)
            {
                var dest = Path.Combine(outDir, entry.Name);
                if (File.Exists(dest) && !overwrite) continue;
                Show($"{entry.Name} ({entry.Length / 1e9:F1} GB)");
                Log($"unpacking {entry.FullName} from {zip} to {dest}");
                var tmp = dest + ".part";
                using (var src = entry.Open())
                using (var dst = File.Create(tmp))
                {
                    int n;
                    while ((n = src.Read(buffer)) > 0)
                    {
                        _ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, n);
                        done += n;
                        _progress.Value = 100.0 * done / Math.Max(1, total);
                    }
                }
                File.Move(tmp, dest, overwrite: true);
            }
            return true;
        }, _ct);
    }

    [GeneratedRegex(@"\[(\d+)/(\d+)\]")]
    private static partial Regex CounterLine();

    [GeneratedRegex(@"(?:batch|source) (\d+)/(\d+)")]
    private static partial Regex BatchLine();

    private async Task<int> Exec(string exe, IReadOnlyList<string> args, LineProgress parse)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _c.Paths.Root,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        Log($"$ {exe} {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        void OnLine(string? line)
        {
            if (line == null) return;
            Log(line);
            if (line.StartsWith("@progress ", StringComparison.Ordinal)) { OnJson(line[10..]); return; }
            Show(line);
            var m = parse switch
            {
                LineProgress.Counter => CounterLine().Match(line),
                LineProgress.Batch or LineProgress.Source => BatchLine().Match(line),
                _ => Match.Empty,
            };
            if (m.Success && double.TryParse(m.Groups[2].Value, out double total) && total > 0)
                _progress.Value = 100.0 * double.Parse(m.Groups[1].Value) / total;
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(_ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return proc.ExitCode;
    }

    private void OnJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            switch (e.GetProperty("event").GetString())
            {
                case "check":
                    Show($"checking {e.GetProperty("done").GetInt32():N0}/{e.GetProperty("total").GetInt32():N0} against the server");
                    break;
                case "plan":
                    Show($"{e.GetProperty("files").GetInt32():N0} files, {e.GetProperty("bytes").GetInt64() / 1e9:F2} GB to fetch");
                    break;
                case "progress":
                    long bytes = e.GetProperty("bytes").GetInt64(), total = e.GetProperty("bytes_total").GetInt64();
                    _progress.Value = total > 0 ? 100.0 * bytes / total : 0;
                    Show($"{e.GetProperty("done").GetInt32():N0}/{e.GetProperty("total").GetInt32():N0} files, "
                         + $"{bytes / 1e9:F2}/{total / 1e9:F2} GB, {e.GetProperty("rate").GetDouble() / 1e6:F0} MB/s");
                    break;
                case "done":
                    _download = (_download.Bytes + e.GetProperty("bytes").GetInt64(), _download.Seconds + e.GetProperty("seconds").GetDouble());
                    break;
            }
        }
        catch (JsonException) { }
    }
}
