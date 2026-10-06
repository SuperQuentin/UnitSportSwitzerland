// MapSetup: pick a zone of Switzerland on a terminal map, see what it costs, then download and
// build it into a region the game can load. Every stage is an existing tool (swiss_data.py, the
// GDAL exporters, TerrainPreprocessor, RoadGen); this is the order, the estimate and the
// bookkeeping around them.
//
//   dotnet run --project tools/MapSetup                      interactive
//   dotnet run --project tools/MapSetup -- --resume          last selection, straight to the estimate
//   dotnet run --project tools/MapSetup -- --town Zermatt --radius 4 --layers terrain,roads --yes
//   dotnet run --project tools/MapSetup -- --bake            rebuild tools/MapSetup/switzerland.bin
//   dotnet run --project tools/MapSetup -- --pick-location   store the data on another drive

using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using UnitSport.Terrain.Format;
using UnitSport.Map;
using UnitSport.Tools.MapSetup;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
bool Flag(string name) => args.Contains(name);

var paths = Paths.Find(Arg("--data"), Arg("--chunks"));

if (Flag("--help") || Flag("-h"))
{
    PrintHelp();
    return 0;
}

if (Flag("--bake"))
    return await Bake.RunAsync(paths);

if (Flag("--save-location"))
{
    paths.SaveLocation();
    AnsiConsole.MarkupLine($"Saved the location: {Markup.Escape(Where(paths))}");
}
if (Flag("--pick-location") && PickLocation(paths) is { } picked)
{
    paths = picked;
    paths.SaveLocation();
    AnsiConsole.MarkupLine($"Saved the location: {Markup.Escape(Where(paths))}");
}

// the embedded country map, unless --bake has just written a fresh one beside the binary
var country = CountryData.LoadPreferringFile(paths.CountryFile);
var state = SetupState.Load(paths);
var stats = Stats.Load(paths);
// --fresh: behave as on a clone with no data yet (for previews and screenshots)
var local = Flag("--fresh") ? new LocalState()
    : AnsiConsole.Status().Start("Looking at what is already on this machine...", _ => LocalState.Scan(paths));
var selection = new Selection(country);
Directory.CreateDirectory(paths.Temp);

// ---- pick only: the map as a tile selector for another tool (tools/rebuild-map.sh) ---------------
// Starts empty and saves nothing, so the last selection stays what --resume continues. Only built
// tiles are written: the file is a list of tiles to build again. --keys replays keys instead of
// reading the terminal, for checks.
if (Arg("--pick-tiles") is { } pickFile)
{
    var view = new MapView(country, local, selection, PickSummary, "Quit without picking anything? (y/n)");
    if (Arg("--keys") is { } pickKeys) view.Snapshot(140, 45, Snapshot.ParseKeys(pickKeys));
    else if (!view.Run()) return 1;
    var builtTiles = selection.Tiles.Where(local.Built.Contains).OrderBy(t => t.E).ThenBy(t => t.N).ToList();
    if (builtTiles.Count == 0)
    {
        AnsiConsole.MarkupLine($"[yellow]None of the {selection.Count:N0} selected tiles is built in {Markup.Escape(paths.Chunks)}.[/]");
        return 1;
    }
    File.WriteAllLines(pickFile, builtTiles.Select(SetupState.Key));
    AnsiConsole.MarkupLine($"{builtTiles.Count:N0} built tiles of {selection.Count:N0} selected -> {Markup.Escape(pickFile)}");
    return 0;
}

// ---- 1. the selection ----------------------------------------------------------------------------
bool scripted = false;
if (Arg("--tiles-file") is { } tilesFile)
{
    selection.AddAll(TileId.ReadList(tilesFile));
    scripted = true;
}
if (Arg("--bbox") is { } bboxText)
{
    // kilometres, as the tiles are named: --bbox 2580,1110,2585,1115 (both ends included)
    var v = bboxText.Split(',').Select(int.Parse).ToArray();
    selection.AddRect(new TileId(v[0], v[1]), new TileId(v[2], v[3]));
    scripted = true;
}
if (Arg("--town") is { } townName)
{
    var town = country.Search(townName, 1).FirstOrDefault();
    if (town == null)
    {
        AnsiConsole.MarkupLine($"[red]No place called {Markup.Escape(townName)}.[/]");
        return 1;
    }
    double radius = double.Parse(Arg("--radius") ?? "5", CultureInfo.InvariantCulture);
    selection.AddCircle(town.E, town.N, radius);
    AnsiConsole.MarkupLine($"{Markup.Escape(town.Name)} ({town.Canton}), {radius:0.#} km: {selection.Count} tiles");
    scripted = true;
}
if (Arg("--canton") is { } cantonCode)
{
    if (country.CantonByCode(cantonCode) is not { } canton)
    {
        AnsiConsole.MarkupLine($"[red]Unknown canton {Markup.Escape(cantonCode)}.[/]");
        return 1;
    }
    selection.AddCanton(canton);
    scripted = true;
}
if (Flag("--resume") || (scripted == false && state.Tiles.Count > 0))
    foreach (var key in state.Tiles)
    {
        var parts = key.Split('-');
        selection.Add(new TileId(int.Parse(parts[0]), int.Parse(parts[1])));
    }

Layers layers = state.Layers;
if (Arg("--layers") is { } layerText) layers = ParseLayers(layerText);

List<Step> Plan() => Planner.Build(new SetupContext
{
    Paths = paths, Country = country, Local = local, Selection = selection, Layers = layers,
    Stats = stats, State = state,
});

if (Arg("--snapshot") is { } snapshotPath)
{
    var size = (Arg("--size") ?? "140x45").Split('x').Select(int.Parse).ToArray();
    var view = new MapView(country, local, selection, sel => MapSummary(sel));
    var frame = view.Snapshot(size[0], size[1], Snapshot.ParseKeys(Arg("--keys") ?? ""));
    await File.WriteAllTextAsync(snapshotPath, Snapshot.ToHtml(frame, "MapSetup"));
    AnsiConsole.MarkupLine($"wrote {Markup.Escape(snapshotPath)} ({selection.Count} tiles selected)");
    return 0;
}

bool interactive = !scripted && !Flag("--resume") && !Flag("--yes");
bool skipMap = false;
while (true)
{
    if (interactive && !skipMap)
    {
        var view = new MapView(country, local, selection, sel => MapSummary(sel));
        if (!view.Run()) return 0;
        state.Tiles = selection.Tiles.Select(SetupState.Key).ToList();
        state.Save(paths);
        layers = AskLayers(layers);
        state.Layers = layers;
        state.Save(paths);
    }
    skipMap = false;
    if (selection.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]Nothing selected.[/]");
        return 1;
    }

    // remembered even if the run never starts, so --resume and the next map open where this left off
    state.Tiles = selection.Tiles.Select(SetupState.Key).ToList();
    state.Layers = layers;
    state.Save(paths);

    // ---- 2. estimate ----------------------------------------------------------------------------
    if (stats.DownloadProbedAt is not { } probed || DateTime.UtcNow - probed > TimeSpan.FromHours(12))
        await AnsiConsole.Status().StartAsync("Measuring your connection (6 s)...",
            async _ => await stats.ProbeDownloadAsync(country, TimeSpan.FromSeconds(6)));
    var steps = Plan();
    ShowPlan(steps);
    if (Flag("--plan-only")) return 0;

    if (Flag("--yes")) break;
    var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
        .Title("Go?")
        .AddChoices("Start", "Back to the map", "Storage location...", "Quit"));
    if (choice == "Start") break;
    if (choice == "Quit") return 0;
    interactive = true;
    if (choice == "Storage location...")
    {
        if (PickLocation(paths) is { } next) SwitchTo(next);
        skipMap = true;
    }
}

state.Tiles = selection.Tiles.Select(SetupState.Key).ToList();
state.Layers = layers;
state.LastRun = DateTime.UtcNow;
state.Save(paths);
stats.Save(paths);

// ---- 3. run ---------------------------------------------------------------------------------------
var plan = Plan();
var logDir = Path.Combine(paths.LogsDir, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
Directory.CreateDirectory(logDir);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var total = Stopwatch.StartNew();
string? failed = null;
var timings = new List<(string Title, TimeSpan Took, double Estimate)>();
await AnsiConsole.Progress()
    .AutoClear(false)
    .HideCompleted(false)
    .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(), new PercentageColumn(),
        new ElapsedTimeColumn(), new SpinnerColumn())
    .StartAsync(async ctx =>
    {
        var tasks = plan.Where(s => s.Skip == null).Select(s => (Step: s, Task: ctx.AddTask(Markup.Escape(s.Title), autoStart: false))).ToList();
        int index = 0;
        foreach (var (step, task) in tasks)
        {
            index++;
            task.StartTask();
            var logPath = Path.Combine(logDir, $"{index:00}_{Slug(step.Title)}.log");
            await using var log = new StreamWriter(logPath) { AutoFlush = true };
            var run = new StepRun(new SetupContext
            {
                Paths = paths, Country = country, Local = local, Selection = selection, Layers = layers,
                Stats = stats, State = state,
            }, new SpectreProgress(task, step.Title), step.Title, log, cts.Token);
            var clock = Stopwatch.StartNew();
            bool ok;
            try
            {
                ok = await step.Run!(run);
            }
            catch (OperationCanceledException)
            {
                failed = $"{step.Title}: interrupted";
                task.Description = $"{Markup.Escape(step.Title)} [yellow]interrupted[/]";
                return;
            }
            catch (Exception e)
            {
                run.Fail(e.Message);
                ok = false;
            }
            timings.Add((step.Title, clock.Elapsed, step.Seconds));
            if (!ok)
            {
                failed = $"{step.Title}: {run.Error ?? "failed"} — log: {logPath}";
                task.Description = $"{Markup.Escape(step.Title)} [red]failed[/]";
                task.StopTask();
                return;
            }
            task.Value = 100;
            task.Description = $"{Markup.Escape(step.Title)} [green]done[/] [grey]{clock.Elapsed.TotalSeconds:N0} s[/]";
            task.StopTask();
            stats.Save(paths);
        }
    });

stats.Save(paths);
AnsiConsole.WriteLine();
if (failed != null)
{
    AnsiConsole.MarkupLine($"[red]Stopped[/] — {Markup.Escape(failed)}");
    AnsiConsole.MarkupLine("Everything finished so far is kept. Run [bold]dotnet run --project tools/MapSetup -- --resume[/] to continue.");
    return 1;
}

var timeTable = new Table().Border(TableBorder.Rounded).AddColumns("Step", "Took", "Estimated");
foreach (var (title, took, est) in timings)
    timeTable.AddRow(Markup.Escape(title), Duration(took.TotalSeconds), Duration(est));
AnsiConsole.Write(timeTable);

var centre = selection.Centre()!.Value;
AnsiConsole.MarkupLine($"[green]Done in {Duration(total.Elapsed.TotalSeconds)}.[/] Logs: {Markup.Escape(logDir)}");
// the game finds the tiles on its own when they are where terrain_location.json (or its default) says
var gameChunks = DataLocation.Load(paths.LocationFile).Chunks ?? paths.DefaultChunks;
var chunksArg = Paths.SamePath(gameChunks, paths.Chunks) ? "" : $" --chunks \"{paths.Chunks}\"";
AnsiConsole.MarkupLine("Play it:");
AnsiConsole.MarkupLine($"  [bold]{Markup.Escape($"<godot> --path . -- --at {centre.E * 1000 + 500},{centre.N * 1000 + 500}{chunksArg}")}[/]");
return 0;

// ---------------------------------------------------------------------------------------------------

IReadOnlyList<(string, string)> MapSummary(Selection sel)
{
    int n = sel.Count;
    if (n == 0) return [("Tiles", "none yet"), ("", ""), ("R, Space, B, F or C", "to select")];
    int built = sel.Tiles.Count(local.Built.Contains);
    int have = sel.Tiles.Count(t => local.Downloaded.Contains(t) && !local.Built.Contains(t));
    var fetch = sel.Tiles.Where(t => !local.Downloaded.Contains(t) && !local.Built.Contains(t)).ToList();
    long bytes = fetch.Sum(country.SizeBytes);
    int toBuild = n - built;
    double seconds = bytes / stats.EffectiveDownload
                     + toBuild * (stats.TerrainCoreSecPerTile + stats.FeaturesCoreSecPerTile) / Environment.ProcessorCount;
    var cantons = sel.Tiles.Select(t => country.CantonAt(t)?.Code).OfType<string>().Distinct().OrderBy(c => c).ToList();
    return
    [
        ("Tiles", $"{n:N0} km²"),
        ("Already built", $"{built:N0}"),
        ("Downloaded, not built", $"{have:N0}"),
        ("To download", Bytes(bytes)),
        ("Built size (≈)", Bytes(toBuild * 2_200_000L)),
        ("Free disk", Bytes(FreeBytes(paths.Data))),
        ("Time, terrain + roads", "≈ " + Duration(seconds)),
        ("Cantons", cantons.Count > 6 ? $"{cantons.Count}" : string.Join(" ", cantons)),
    ];
}

// the panel of --pick-tiles: what of the selection is built, since only that is written
IReadOnlyList<(string, string)> PickSummary(Selection sel)
{
    int n = sel.Count;
    if (n == 0) return [("Tiles", "none yet"), ("", ""), ("R, Space, B, F or C", "to select"), ("", ""), ("Built on this machine", $"{local.Built.Count:N0}")];
    int built = sel.Tiles.Count(local.Built.Contains);
    var cantons = sel.Tiles.Select(t => country.CantonAt(t)?.Code).OfType<string>().Distinct().OrderBy(c => c).ToList();
    return
    [
        ("Tiles", $"{n:N0} km²"),
        ("Built, picked", $"{built:N0}"),
        ("Not built, ignored", $"{n - built:N0}"),
        ("Cantons", cantons.Count > 6 ? $"{cantons.Count}" : string.Join(" ", cantons)),
    ];
}

void ShowPlan(List<Step> steps)
{
    AnsiConsole.MarkupLine($"[grey]Storage: {Markup.Escape(Where(paths))}[/]");
    var table = new Table().Border(TableBorder.Rounded).Title("[bold]What will happen[/]")
        .AddColumns("Step", "Details", "Download", "Disk", "Time");
    foreach (var s in steps)
    {
        if (s.Skip != null)
            table.AddRow($"[grey]{Markup.Escape(s.Title)}[/]", $"[grey]skip: {Markup.Escape(s.Skip)}[/]", "", "", "");
        else
            table.AddRow(Markup.Escape(s.Title), Markup.Escape(s.Detail),
                s.DownloadBytes > 0 ? Bytes(s.DownloadBytes) : "", s.DiskBytes > 0 ? Bytes(s.DiskBytes) : "",
                Duration(s.Seconds));
    }
    var running = steps.Where(s => s.Skip == null).ToList();
    table.AddEmptyRow();
    table.AddRow("[bold]Total[/]", $"{selection.Count:N0} tiles", $"[bold]{Bytes(running.Sum(s => s.DownloadBytes))}[/]",
        $"[bold]{Bytes(running.Sum(s => s.DiskBytes))}[/]", $"[bold]≈ {Duration(running.Sum(s => s.Seconds))}[/]");
    AnsiConsole.Write(table);

    string rate = stats.DownloadProbedAt != null ? $"measured {stats.DownloadBytesPerSec / 1e6:N0} MB/s" : "assumed 40 MB/s (not measured)";
    AnsiConsole.MarkupLine($"[grey]Download at {rate}; processing on {Environment.ProcessorCount} cores"
                           + (stats.TerrainCoreSecPerTile != new Stats().TerrainCoreSecPerTile ? ", rates calibrated by earlier runs" : "") + ".[/]");

    // not enough room is the one failure worth catching before anything starts
    foreach (var group in running.Where(s => s.DiskBytes > 0 && s.DiskPath != null).GroupBy(s => Path.GetPathRoot(Path.GetFullPath(s.DiskPath!))))
    {
        long need = group.Sum(s => s.DiskBytes);
        long free = FreeBytes(group.First().DiskPath!);
        if (need * 1.1 > free)
            AnsiConsole.MarkupLine($"[red]Drive {Markup.Escape(group.Key ?? "?")} needs {Bytes(need)} but has {Bytes(free)} free.[/]");
    }
}

// Where the downloads and the built tiles go: the repo's folders, a folder on any ready drive, or
// one typed in. Returns null to keep the current place.
Paths? PickLocation(Paths current)
{
    var options = new List<(string Label, string? Base)>
    {
        ($"Keep  [grey]{Markup.Escape(Where(current))}[/]", null),
        ($"The repository's folders  [grey]{Markup.Escape(current.RepoRoot)} · {Bytes(FreeBytes(current.RepoRoot))} free[/]", ""),
    };
    var repoDrive = Path.GetPathRoot(current.RepoRoot);
    foreach (var d in Drives())
    {
        string name = d.RootDirectory.FullName;
        string label = "";
        try { label = d.VolumeLabel; } catch (Exception) { /* not every platform names volumes */ }
        string tags = string.Join(" · ", new[]
        {
            label, $"{Bytes(d.AvailableFreeSpace)} free of {Bytes(d.TotalSize)}",
            string.Equals(name, repoDrive, StringComparison.OrdinalIgnoreCase) ? "the repo's drive" : "",
        }.Where(t => t.Length > 0));
        var baseDir = Path.Combine(name, "UnitSportSwitzerland");
        options.Add(($"{Markup.Escape(baseDir)}  [grey]{Markup.Escape(tags)}[/]", baseDir));
    }
    options.Add(("Another folder...", "?"));

    var pick = AnsiConsole.Prompt(new SelectionPrompt<(string Label, string? Base)>()
        .Title("Where should the map data live? [grey](source downloads ≈ 155 GB and built tiles ≈ 55 GB for all of CH)[/]")
        .PageSize(15)
        .UseConverter(o => o.Label)
        .AddChoices(options));
    if (pick.Base == null) return null;

    string data, chunks;
    if (pick.Base == "")
        (data, chunks) = (current.DefaultData, current.DefaultChunks);
    else
    {
        var baseDir = pick.Base == "?"
            ? AnsiConsole.Prompt(new TextPrompt<string>("Folder [grey](data/ and terrain_chunks/ go inside)[/]:"))
            : pick.Base;
        (data, chunks) = (Path.Combine(baseDir, "data"), Path.Combine(baseDir, "terrain_chunks"));
    }

    const string both = "Source data and built tiles", dataOnly = "Source data only (the downloads)",
        tilesOnly = "Built tiles only (what the game and the server load)";
    var what = AnsiConsole.Prompt(new SelectionPrompt<string>()
        .Title($"Move what to [bold]{Markup.Escape(Path.GetDirectoryName(data) ?? data)}[/]?")
        .AddChoices(both, dataOnly, tilesOnly));
    var next = current.With(what == tilesOnly ? current.Data : data, what == dataOnly ? current.Chunks : chunks);
    return Paths.SamePath(next.Data, current.Data) && Paths.SamePath(next.Chunks, current.Chunks) ? null : next;
}

// Carries the selection and layers over to another location and saves it for the next run and the game.
// Nothing is moved: what is already downloaded or built there counts, what is left behind does not.
void SwitchTo(Paths next)
{
    var old = (Paths: paths, Local: local);
    paths = next;
    paths.SaveLocation();
    local = AnsiConsole.Status().Start("Looking at the new location...", _ => LocalState.Scan(paths));
    var moved = SetupState.Load(paths);
    moved.Tiles = selection.Tiles.Select(SetupState.Key).ToList();
    moved.Layers = layers;
    state = moved;
    state.Save(paths);
    stats.Save(paths); // download and core rates belong to the machine, not the folder
    AnsiConsole.MarkupLine($"Saved the location: {Markup.Escape(Where(paths))}");

    if (!Paths.SamePath(old.Paths.Chunks, paths.Chunks) && old.Local.Built.Count > 0 && local.Built.Count == 0)
        AnsiConsole.MarkupLine($"[yellow]The {old.Local.Built.Count:N0} tiles built in {Markup.Escape(old.Paths.Chunks)} stay there.[/] "
                               + $"Move that folder to {Markup.Escape(paths.Chunks)} (and its _temp next to it) to keep them, or they are built again.");
    if (!Paths.SamePath(old.Paths.Data, paths.Data) && old.Local.Downloaded.Count > 0 && local.Downloaded.Count == 0)
        AnsiConsole.MarkupLine($"[yellow]The downloads in {Markup.Escape(old.Paths.Data)} stay there.[/] "
                               + $"Move what is inside to {Markup.Escape(paths.Data)} to keep them, or they are downloaded again.");
}

static string Where(Paths p) =>
    p.DataOverride == null && p.ChunksOverride == null ? $"the repository's folders ({p.RepoRoot})"
    : $"source data {p.Data}, built tiles {p.Chunks}";

// Ready drives with room on them; skips pseudo and read-only filesystems (Linux lists dozens).
static List<DriveInfo> Drives()
{
    var list = new List<DriveInfo>();
    foreach (var d in DriveInfo.GetDrives())
    {
        try
        {
            if (d.IsReady && d.DriveType is (DriveType.Fixed or DriveType.Removable or DriveType.Network)
                && d.TotalSize >= 8_000_000_000 && d.AvailableFreeSpace >= 1_000_000_000)
                list.Add(d);
        }
        catch (Exception) { /* a drive that cannot be asked is not a candidate */ }
    }
    return list;
}

Layers AskLayers(Layers current)
{
    var choices = new List<(Layers Layer, string Label)>
    {
        (Layers.Roads, $"Roads, rail, rivers, land cover, trees  [grey](swissTLM3D, {Bytes(country.Extras.GetValueOrDefault("swisstlm3d", 4_800_000_000))} once)[/]"),
        (Layers.Buildings, "Buildings  [grey](swissBUILDINGS3D, the sheets you touch)[/]"),
        (Layers.Cadastre, "Building use, age and storeys  [grey](GWR register)[/]"),
        (Layers.Routes, "Cycle and MTB route flags  [grey](ASTRA, ~90 MB)[/]"),
        (Layers.Places, "Place index for the in-game search  [grey](needs the GWR register)[/]"),
        (Layers.Fields, "Real farm fields  [grey](LWB land use per canton, geodienste.ch, ~1.1 GB once; OSM fills the gated cantons if downloaded)[/]"),
        (Layers.Osm, "OpenStreetMap road attributes: one-way, lanes, sidewalks  [grey](Geofabrik, ~550 MB once; ODbL, needs roads)[/]"),
    };
    var prompt = new MultiSelectionPrompt<string>()
        .Title("Terrain is always included. [bold]What else?[/] [grey](space toggles, enter confirms)[/]")
        .NotRequired()
        .PageSize(10);
    foreach (var (layer, label) in choices)
    {
        prompt.AddChoice(label);
        if (current.HasFlag(layer)) prompt.Select(label);
    }
    var picked = AnsiConsole.Prompt(prompt);
    var result = Layers.Terrain;
    foreach (var (layer, label) in choices)
        if (picked.Contains(label)) result |= layer;
    if (result.HasFlag(Layers.Places)) result |= Layers.Cadastre;
    return result;
}

static Layers ParseLayers(string text)
{
    var result = Layers.Terrain;
    foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        result |= part.ToLowerInvariant() switch
        {
            "terrain" => Layers.Terrain,
            "roads" or "tlm" => Layers.Roads,
            "buildings" => Layers.Buildings,
            "cadastre" or "gwr" => Layers.Cadastre,
            "routes" => Layers.Routes,
            "places" => Layers.Places | Layers.Cadastre,
            "fields" => Layers.Fields,
            // never part of "all": tiles built with it fall under the ODbL (docs/notes/tools/osm-odbl-licence.md)
            "osm" => Layers.Osm | Layers.Roads,
            "all" => Layers.Roads | Layers.Buildings | Layers.Cadastre | Layers.Routes | Layers.Places | Layers.Fields,
            _ => throw new ArgumentException($"unknown layer '{part}' (terrain, roads, buildings, cadastre, routes, places, fields, osm, all)"),
        };
    return result;
}

static int Run(string exe, params string[] arguments)
{
    try
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return -1;
    }
}

static long FreeBytes(string path)
{
    try
    {
        var full = Path.GetFullPath(path);
        while (!Directory.Exists(full) && Path.GetDirectoryName(full) is { } parent) full = parent;
        return new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;
    }
    catch (Exception)
    {
        return 0;
    }
}

static string Bytes(long b) => b switch
{
    >= 1_000_000_000_000 => $"{b / 1e12:N1} TB",
    >= 1_000_000_000 => $"{b / 1e9:N1} GB",
    >= 1_000_000 => $"{b / 1e6:N0} MB",
    > 0 => $"{b / 1e3:N0} KB",
    _ => "0",
};

static string Duration(double seconds) => seconds switch
{
    < 60 => $"{Math.Max(1, seconds):N0} s",
    < 3600 => $"{seconds / 60:N0} min",
    < 86400 => $"{Math.Floor(seconds / 3600):N0} h {seconds % 3600 / 60:00} min",
    _ => $"{seconds / 86400:N1} days",
};

static string Slug(string title) =>
    new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

static void PrintHelp()
{
    Console.WriteLine("""
        MapSetup — pick a zone of Switzerland, see its size and time, then download and build it.

          dotnet run --project tools/MapSetup [-- options]

        Selection (skips the map when given):
          --town NAME [--radius KM]    around a town, summit or pass (default 5 km)
          --canton CODE                a whole canton (VS, GE, ...)
          --bbox E0,N0,E1,N1           kilometre tiles, corners included (2580,1110,2585,1115)
          --tiles-file FILE            one "E-N" per line
          --resume                     the last selection and layers
        Layers:
          --layers terrain,roads,buildings,cadastre,routes,places,fields,osm|all
                                       (osm is optional and never implied by all)
        Folders:
          --data DIR                   source data instead of ressources/data (dataset subfolders inside)
          --chunks DIR                 built tiles instead of terrain_chunks (state goes to DIR_temp)
          --save-location              keep --data/--chunks for later runs, the game and the server
          --pick-location              choose a drive or folder first (also under "Go?")
                                       The choice is saved in terrain_location.json (repo root,
                                       gitignored); --data/--chunks override it for one run.
        Pick only:
          --pick-tiles FILE            the map as a selector: writes the built tiles picked, one "E-N"
                                       per line, and builds nothing (tools/rebuild-map.sh uses it)
        Run:
          --plan-only                  show the estimate and stop
          --yes                        do not ask before starting
        Maintenance:
          --bake                       rebuild tools/MapSetup/switzerland.bin from swisstopo (slow, once)
        """);
}
