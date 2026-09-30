// MapSetup: pick a zone of Switzerland on a terminal map, see what it costs, then download and
// build it into a region the game can load. Every stage is an existing tool (swiss_data.py, the
// GDAL exporters, TerrainPreprocessor, RoadGen); this is the order, the estimate and the
// bookkeeping around them.
//
//   dotnet run --project tools/MapSetup                      interactive
//   dotnet run --project tools/MapSetup -- --resume          last selection, straight to the estimate
//   dotnet run --project tools/MapSetup -- --town Zermatt --radius 4 --layers terrain,roads --yes
//   dotnet run --project tools/MapSetup -- --bake            rebuild tools/MapSetup/switzerland.bin

using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using UnitSport.Terrain.Format;
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

if (!File.Exists(paths.CountryFile))
{
    AnsiConsole.MarkupLine($"[red]No country map at {Markup.Escape(paths.CountryFile)}.[/] "
                           + "It is committed to the repository; pull it, or rebuild it with [bold]--bake[/].");
    return 1;
}

var country = CountryData.Load(paths.CountryFile);
var state = SetupState.Load(paths);
var stats = Stats.Load(paths);
// --fresh: behave as on a clone with no data yet (for previews and screenshots)
var local = Flag("--fresh") ? new LocalState()
    : AnsiConsole.Status().Start("Looking at what is already on this machine...", _ => LocalState.Scan(paths));
var selection = new Selection(country);
Directory.CreateDirectory(paths.Temp);

// ---- 1. the selection ----------------------------------------------------------------------------
bool scripted = false;
if (Arg("--tiles-file") is { } tilesFile)
{
    selection.AddAll(Selection.ReadTilesFile(tilesFile));
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

var python = FindPython();
bool gdal = python != null && Run(python, "-c", "import osgeo") == 0;
Layers layers = state.Layers;
if (Arg("--layers") is { } layerText) layers = ParseLayers(layerText);
if (!gdal) layers &= ~(Layers.Buildings | Layers.Routes);

List<Step> Plan() => Planner.Build(new SetupContext
{
    Paths = paths, Country = country, Local = local, Selection = selection, Layers = layers,
    Stats = stats, State = state, Python = python, Gdal = gdal,
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
while (true)
{
    if (interactive)
    {
        var view = new MapView(country, local, selection, sel => MapSummary(sel));
        if (!view.Run()) return 0;
        state.Tiles = selection.Tiles.Select(SetupState.Key).ToList();
        state.Save(paths);
        layers = AskLayers(layers);
        state.Layers = layers;
        state.Save(paths);
    }
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
        .AddChoices("Start", "Back to the map", "Quit"));
    if (choice == "Start") break;
    if (choice == "Quit") return 0;
    interactive = true;
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
                Stats = stats, State = state, Python = python, Gdal = gdal,
            }, task, step.Title, log, cts.Token);
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
AnsiConsole.MarkupLine("Play it:");
AnsiConsole.MarkupLine($"  [bold]<godot> --path . -- --at {centre.E * 1000 + 500},{centre.N * 1000 + 500}[/]");
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

void ShowPlan(List<Step> steps)
{
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
    if (!gdal)
        AnsiConsole.MarkupLine("[yellow]GDAL is not available to Python (python -c \"import osgeo\" fails): buildings and cycle routes are off.[/]");

    // not enough room is the one failure worth catching before anything starts
    foreach (var group in running.Where(s => s.DiskBytes > 0 && s.DiskPath != null).GroupBy(s => Path.GetPathRoot(Path.GetFullPath(s.DiskPath!))))
    {
        long need = group.Sum(s => s.DiskBytes);
        long free = FreeBytes(group.First().DiskPath!);
        if (need * 1.1 > free)
            AnsiConsole.MarkupLine($"[red]Drive {Markup.Escape(group.Key ?? "?")} needs {Bytes(need)} but has {Bytes(free)} free.[/]");
    }
}

Layers AskLayers(Layers current)
{
    var choices = new List<(Layers Layer, string Label)>
    {
        (Layers.Roads, $"Roads, rail, rivers, land cover, trees  [grey](swissTLM3D, {Bytes(country.Extras.GetValueOrDefault("swisstlm3d", 4_800_000_000))} once)[/]"),
        (Layers.Buildings, gdal ? "Buildings  [grey](swissBUILDINGS3D, the sheets you touch; GDAL)[/]" : "Buildings  [red](needs GDAL — unavailable)[/]"),
        (Layers.Cadastre, "Building use, age and storeys  [grey](GWR register)[/]"),
        (Layers.Routes, gdal ? "Cycle and MTB route flags  [grey](ASTRA, ~90 MB; GDAL)[/]" : "Cycle routes  [red](needs GDAL — unavailable)[/]"),
        (Layers.Places, "Place index for the in-game search  [grey](needs the GWR register)[/]"),
    };
    var prompt = new MultiSelectionPrompt<string>()
        .Title("Terrain is always included. [bold]What else?[/] [grey](space toggles, enter confirms)[/]")
        .NotRequired()
        .PageSize(10);
    foreach (var (layer, label) in choices)
    {
        prompt.AddChoice(label);
        if (current.HasFlag(layer) && (gdal || layer is not (Layers.Buildings or Layers.Routes))) prompt.Select(label);
    }
    var picked = AnsiConsole.Prompt(prompt);
    var result = Layers.Terrain;
    foreach (var (layer, label) in choices)
        if (picked.Contains(label) && (gdal || layer is not (Layers.Buildings or Layers.Routes))) result |= layer;
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
            "all" => Layers.Roads | Layers.Buildings | Layers.Cadastre | Layers.Routes | Layers.Places,
            _ => throw new ArgumentException($"unknown layer '{part}' (terrain, roads, buildings, cadastre, routes, places, all)"),
        };
    return result;
}

static string? FindPython()
{
    foreach (var exe in new[] { "python", "python3", "py" })
        if (Run(exe, "--version") == 0) return exe;
    return null;
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
          --layers terrain,roads,buildings,cadastre,routes,places|all
        Folders:
          --data DIR                   source data instead of ressources/data (dataset subfolders inside)
          --chunks DIR                 built tiles instead of terrain_chunks (state goes to DIR_temp)
        Run:
          --plan-only                  show the estimate and stop
          --yes                        do not ask before starting
        Maintenance:
          --bake                       rebuild tools/MapSetup/switzerland.bin from swisstopo (slow, once)
        """);
}
