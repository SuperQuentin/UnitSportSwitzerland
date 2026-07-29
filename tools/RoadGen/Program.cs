using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen;
using UnitSport.Tools.RoadGen.Diagnostics;
using UnitSport.Tools.RoadGen.Export;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Network;
using UnitSport.Tools.RoadGen.Rewrite;
using UnitSport.Tools.RoadGen.Synthesis;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        RoadGen — fluid procedural road geometry with real junctions.

          --demo [scene]         hand-built test scenes: crossroads, exit, hairpin, village
                                 (omit the name to run all of them)
          --synth               synthesise a network with a tensor field
              --seed N           default 1
              --size M           square side in metres, default 1600
              --flat             drop the terrain basis field
          --tiles E,N[;E,N...]  load real .road tiles and rebuild them
                                 ranges allowed: "2578-2588,1108-1118"
              --chunks DIR       where the .road files are, default terrain_chunks
              --divided-scale F  width multiplier for direction-separated lines (default 1.0)

          --rewrite             trim + junction existing .road tiles IN PLACE (format v2)
              --chunks DIR       default terrain_chunks;  --tiles limits which
              --no-smooth        junctions only, leave centrelines alone
              --dry-run          measure without writing
              --force            rewrite even if the tiles already carry junctions
              --measure          also compute the overlap comparison (slow)
              --no-audit         skip the height audit against the terrain

          --out DIR             output directory, default roadgen_out
          --no-baseline         skip the untrimmed/unsmoothed comparison pass
          --obj                 also write OBJ meshes
          --simplify M          Douglas-Peucker tolerance in metres, default 0.6
          --chord M             tessellation chord tolerance in metres, default 0.05

        Writes an SVG plan view per case, plus a quality report on stdout. Nothing here
        needs Godot, terrain, or GDAL.
        """);
    return 0;
}

int failures = 0;
string outDir = ArgValue("--out") ?? "roadgen_out";
Directory.CreateDirectory(outDir);
bool baseline = !args.Contains("--no-baseline");
bool writeObj = args.Contains("--obj");

var options = new PipelineOptions(
    SimplifyTolerance: double.Parse(ArgValue("--simplify") ?? "0.6", CultureInfo.InvariantCulture),
    ChordTolerance: double.Parse(ArgValue("--chord") ?? "0.05", CultureInfo.InvariantCulture));

if (args.Contains("--demo"))
{
    string? only = ArgValue("--demo");
    var scenes = only is null ? DemoScenes.All : new[] { only };
    foreach (string scene in scenes)
        RunCase(scene, () => DemoScenes.Build(scene), null);
}
else if (args.Contains("--synth"))
{
    int seed = int.Parse(ArgValue("--seed") ?? "1", CultureInfo.InvariantCulture);
    double size = double.Parse(ArgValue("--size") ?? "1600", CultureInfo.InvariantCulture);
    bool flat = args.Contains("--flat");

    var bounds = new Bounds(0, 0, size, size);
    var terrain = flat ? null : TownGenerator.DemoTerrain(bounds);

    RunCase($"synth_seed{seed}{(flat ? "_flat" : "")}",
        () => TownGenerator.Generate(bounds, new TownGenerator.TownOptions(Seed: seed, Height: terrain)).Network,
        terrain);
}
else if (args.Contains("--rewrite"))
{
    string chunks = ArgValue("--chunks") ?? "terrain_chunks";
    double dividedScale = double.Parse(ArgValue("--divided-scale") ?? "1.0", CultureInfo.InvariantCulture);
    bool dryRun = args.Contains("--dry-run");

    var ids = ArgValue("--tiles") is { } spec ? ParseTiles(spec) : DiscoverTiles(chunks);
    if (ids.Count == 0)
    {
        Console.Error.WriteLine($"no .road files found in '{chunks}'");
        return 1;
    }

    Console.WriteLine($"rewriting {ids.Count} tiles in {chunks}{(dryRun ? " (dry run)" : "")}");
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    TileRewriter.Stats stats;
    try
    {
        stats = TileRewriter.Run(chunks, ids, new TileRewriter.Options(
        Smooth: !args.Contains("--no-smooth"),
        SimplifyTolerance: options.SimplifyTolerance,
        ChordTolerance: options.ChordTolerance,
        DividedScale: dividedScale,
        DryRun: dryRun,
        Force: args.Contains("--force"),
        Measure: args.Contains("--measure"),
        AuditHeights: !args.Contains("--no-audit")), Console.WriteLine);
    }
    catch (TileRewriter.AlreadyRewrittenException e)
    {
        Console.Error.WriteLine(e.Message);
        return 3;
    }

    Console.WriteLine();
    Console.WriteLine($"{stats.TilesWritten} tiles, {stats.SegmentsWritten:N0} segments, "
                      + $"{stats.Junctions:N0} junctions, carriageway {stats.CarriagewayArea:N0} m²");

    if (args.Contains("--measure"))
    {
        double removed = stats.OverlapBefore < 1e-6
            ? 0 : 100.0 * (1 - stats.OverlapAfter / stats.OverlapBefore);
        Console.WriteLine($"overlap {stats.OverlapBefore:N0} m² -> {stats.OverlapAfter:N0} m² "
                          + $"({removed:F1}% removed)");
    }

    Console.WriteLine(stats.Heights.Format());
    if (stats.VerticesReverted > 0)
        Console.WriteLine($"    {stats.VerticesReverted:N0} vertices put back on the surveyed line (cliff guard)");
    Console.WriteLine($"{stopwatch.Elapsed.TotalSeconds:F1} s");
}
else if (ArgValue("--tiles") is { } tileList)
{
    string chunks = ArgValue("--chunks") ?? "terrain_chunks";
    double dividedScale = double.Parse(ArgValue("--divided-scale") ?? "1.0", CultureInfo.InvariantCulture);
    var ids = ParseTiles(tileList);

    RunCase($"tiles_{ids.Count}", () =>
    {
        var (net, stats) = RoadTileImporter.Load(chunks, ids, dividedScale);
        Console.WriteLine($"  loaded {stats.Tiles} tiles, {stats.Segments} segments"
                          + (stats.Skipped > 0 ? $", skipped {stats.Skipped} degenerate" : ""));
        if (stats.Tiles == 0)
            Console.WriteLine($"  (nothing found in '{chunks}' — is the region built?)");
        return net;
    }, null);
}
else
{
    Console.Error.WriteLine("nothing to do; try --demo, --synth, --tiles or --rewrite. --help for details.");
    return 1;
}

// non-zero exit on a failed invariant, so this can sit in a build script
if (failures > 0) Console.Error.WriteLine($"\n{failures} case(s) failed verification");
return failures > 0 ? 2 : 0;

void RunCase(string name, Func<RoadNetwork> build, Func<Vec2, double>? height)
{
    Console.WriteLine();
    Console.WriteLine($"=== {name} ===");

    var generated = Pipeline.Run(build(), options);
    Console.WriteLine(generated.Report.Format("generated"));

    var checks = Verifier.Run(generated.Network, generated.Ribbons, generated.Junctions, options.ChordTolerance);
    Console.WriteLine(checks.Format());
    if (!checks.Passed) failures++;

    string svg = Path.Combine(outDir, $"{name}.svg");
    SvgWriter.Write(svg, generated.Network, generated.Ribbons, generated.Junctions,
        generated.Markings, $"{name} — RoadGen");
    Console.WriteLine($"  -> {svg}");

    if (writeObj)
    {
        string obj = Path.Combine(outDir, $"{name}.obj");
        ObjWriter.Write(obj, generated.Ribbons, generated.Junctions, generated.Markings,
            height is null ? null : p => height(p));
        Console.WriteLine($"  -> {obj}");
    }

    if (!baseline) return;

    // The comparison has to rebuild the network from scratch: Pipeline.Run mutates the graph
    // it is given (nodes, alignments, trims), so handing it the same instance twice would
    // measure the second pass against an already-trimmed network and report no difference.
    var before = Pipeline.Run(build(), options with { Smooth = false, BuildJunctions = false });
    Console.WriteLine(before.Report.Format("baseline (current renderer: raw polylines, no junctions)"));

    string baselineSvg = Path.Combine(outDir, $"{name}_baseline.svg");
    SvgWriter.Write(baselineSvg, before.Network, before.Ribbons, before.Junctions,
        before.Markings, $"{name} — baseline", showCentrelines: false);
    Console.WriteLine($"  -> {baselineSvg}");

    double was = before.Report.OverlapArea;
    double now = generated.Report.OverlapArea;
    string verdict = was < 1e-6
        ? "no overlap either way"
        : now < 1e-6
            ? $"overlap {was:N1} m² -> 0"
            : $"overlap {was:N1} m² -> {now:N1} m² ({100 * (1 - now / was):F1}% removed)";
    Console.WriteLine($"  VERDICT: {verdict}; "
                      + $"worst turn {before.Report.WorstTurn:F1}° -> {generated.Report.WorstTurn:F1}°");
}

string? ArgValue(string flag)
{
    int i = Array.IndexOf(args, flag);
    if (i < 0 || i + 1 >= args.Length) return null;
    return args[i + 1].StartsWith("--") ? null : args[i + 1];
}

static List<TileId> DiscoverTiles(string chunkDir)
{
    var ids = new List<TileId>();
    if (!Directory.Exists(chunkDir)) return ids;

    foreach (string path in Directory.EnumerateFiles(chunkDir, "roads_*.road"))
    {
        var bits = Path.GetFileNameWithoutExtension(path).Split('_');
        if (bits.Length == 3 && int.TryParse(bits[1], out int e) && int.TryParse(bits[2], out int n))
            ids.Add(new TileId(e, n));
    }
    return ids;
}

// "2583,1113" for one tile, "2578-2588,1108-1118" for a block, ";" to join several
static List<TileId> ParseTiles(string spec)
{
    var ids = new List<TileId>();
    foreach (string part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var bits = part.Split(',');
        if (bits.Length != 2) continue;
        if (!ParseRange(bits[0], out int e0, out int e1)) continue;
        if (!ParseRange(bits[1], out int n0, out int n1)) continue;

        for (int e = e0; e <= e1; e++)
        for (int n = n0; n <= n1; n++)
            ids.Add(new TileId(e, n));
    }
    return ids;
}

static bool ParseRange(string text, out int lo, out int hi)
{
    lo = hi = 0;
    int dash = text.IndexOf('-');
    if (dash <= 0)
        return int.TryParse(text, out lo) && (hi = lo) == lo;

    return int.TryParse(text[..dash], out lo) && int.TryParse(text[(dash + 1)..], out hi);
}
