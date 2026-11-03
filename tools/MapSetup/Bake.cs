using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using UnitSport.Map;

namespace UnitSport.Tools.MapSetup;

/// <summary>
/// Builds <see cref="CountryData"/> from the live sources, once, on a developer machine: the
/// result is committed so everyone else gets the map offline. Slow parts (tens of thousands of
/// HEAD requests for zip sizes) are cached under terrain_chunks_temp/mapsetup_bake, so an
/// interrupted bake picks up where it stopped.
/// </summary>
public static partial class Bake
{
    // swisstopo's own footprint for every national dataset
    private static readonly (double W, double S, double E, double N) Switzerland = (5.9, 45.8, 10.55, 47.85);

    /// <summary>BFS canton numbers are fixed; swissBOUNDARIES3D carries the number, not the code.</summary>
    private static readonly string[] CantonCodes = CantonRings.Codes;

    [GeneratedRegex(@"^swissalti3d_(\d{4})_(\d{4})-(\d{4})$")]
    private static partial Regex AltiId();

    [GeneratedRegex(@"_0\.5_2056_5728\.xyz\.zip$")]
    private static partial Regex AltiAsset();

    [GeneratedRegex(@"^swissbuildings3d_3_0_(\d{4})_(\S+)$")]
    private static partial Regex SheetId();

    public static async Task<int> RunAsync(Paths paths)
    {
        var cacheDir = Path.Combine(paths.Temp, "mapsetup_bake");
        Directory.CreateDirectory(cacheDir);
        var sizeCachePath = Path.Combine(cacheDir, "sizes.json");
        var sizes = File.Exists(sizeCachePath)
            ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(sizeCachePath)) ?? new()
            : new Dictionary<string, long>();
        SeedSizesFromManifests(paths, sizes);

        var country = new CountryData { BakedAt = DateTime.UtcNow };

        // ---- 1. swissALTI3D: which tiles exist, newest survey, zip size -------------------
        AnsiConsole.MarkupLine("[bold]1/5[/] listing every swissALTI3D tile (STAC)...");
        var items = await Progress("items listed", p => Stac.ListAsync("ch.swisstopo.swissalti3d", Switzerland, progress: p));
        var newest = new Dictionary<TileId, (int Year, string Url)>();
        foreach (var item in items)
        {
            if (AltiId().Match(item.Id) is not { Success: true } m) continue;
            var asset = item.Assets.FirstOrDefault(a => AltiAsset().IsMatch(a.Key));
            if (asset == null) continue;
            int year = int.Parse(m.Groups[1].Value);
            var tile = new TileId(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            if (!newest.TryGetValue(tile, out var had) || year > had.Year) newest[tile] = (year, asset.Href);
        }
        AnsiConsole.MarkupLine($"  {items.Count:N0} items -> [green]{newest.Count:N0}[/] tiles (newest survey each)");

        AnsiConsole.MarkupLine("[bold]2/5[/] zip sizes (HEAD, cached between runs)...");
        await FillSizes(newest.Values.Select(v => v.Url), sizes, sizeCachePath);
        long missing = 0;
        foreach (var (tile, (year, url)) in newest)
        {
            if (!sizes.TryGetValue(url, out long size)) { missing++; size = 19_000_000; }
            country.SetTile(tile, size, year);
        }
        if (missing > 0) AnsiConsole.MarkupLine($"  [yellow]{missing} tiles without a size, assumed 19 MB[/]");

        // highest point per tile, from terrain already built on this machine (hillshade only)
        var manifestPath = Path.Combine(paths.Chunks, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var manifest = TerrainManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
            foreach (var t in manifest.Tiles) country.SetMaxElevation(t.Id, t.Max);
            AnsiConsole.MarkupLine($"  elevations from {manifest.Tiles.Count:N0} built tiles");
        }
        else AnsiConsole.MarkupLine("  [yellow]no terrain_chunks/manifest.json: the map will be flat-coloured[/]");

        // ---- 3. cantons ---------------------------------------------------------------------
        AnsiConsole.MarkupLine("[bold]3/5[/] canton boundaries (swissBOUNDARIES3D)...");
        await BakeCantons(country, cacheDir);

        // ---- 4. buildings sheets + nationwide files -------------------------------------------
        AnsiConsole.MarkupLine("[bold]4/5[/] swissBUILDINGS3D sheets and nationwide files...");
        var sheetItems = await Stac.ListAsync("ch.swisstopo.swissbuildings3d_3_0", Switzerland);
        var sheets = new Dictionary<string, (int Year, Stac.Item Item, string Url)>();
        foreach (var item in sheetItems)
        {
            // the collection mixes per-sheet items with nationwide ones; a sheet is a few km wide
            if (Math.Max(item.Bbox[2] - item.Bbox[0], item.Bbox[3] - item.Bbox[1]) >= 0.5) continue;
            if (SheetId().Match(item.Id) is not { Success: true } m) continue;
            var gdb = item.Assets.FirstOrDefault(a => a.Key.EndsWith(".gdb.zip", StringComparison.Ordinal));
            if (gdb == null) continue;
            int year = int.Parse(m.Groups[1].Value);
            string key = m.Groups[2].Value;
            if (!sheets.TryGetValue(key, out var had) || year > had.Year) sheets[key] = (year, item, gdb.Href);
        }

        var extraUrls = new Dictionary<string, string>();
        if (await LatestAsset("ch.swisstopo.swisstlm3d", ".gpkg.zip") is { } tlm) extraUrls["swisstlm3d"] = tlm;
        if (await LatestAsset("ch.astra.veloland", "_2056.gdb.zip") is { } velo) extraUrls["veloland"] = velo;
        if (await LatestAsset("ch.astra.mountainbikeland", "_2056.gdb.zip") is { } mtb) extraUrls["mountainbikeland"] = mtb;
        foreach (var code in CantonCodes.Append("CH"))
            extraUrls["gwr_" + code.ToLowerInvariant()] = $"https://public.madd.bfs.admin.ch/{code.ToLowerInvariant()}.zip";

        await FillSizes(sheets.Values.Select(s => s.Url).Concat(extraUrls.Values), sizes, sizeCachePath);
        foreach (var (key, (_, item, url)) in sheets.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            // from the footprint polygon: a lon/lat bbox around a sheet cut on the LV95 grid is
            // tens of metres too big and would make every neighbouring sheet "touch"
            var pts = item.Ring.Count >= 3
                ? item.Ring.Select(v => SwissProjection.ToLv95(v.Lat, v.Lon)).ToList()
                : [SwissProjection.ToLv95(item.Bbox[1], item.Bbox[0]), SwissProjection.ToLv95(item.Bbox[3], item.Bbox[2])];
            country.Sheets.Add(new BuildingSheet(key, pts.Min(v => v.E), pts.Min(v => v.N), pts.Max(v => v.E),
                pts.Max(v => v.N), sizes.GetValueOrDefault(url, 20_000_000)));
        }
        foreach (var (key, url) in extraUrls)
            if (sizes.TryGetValue(url, out long s)) country.Extras[key] = s;
        AnsiConsole.MarkupLine($"  {country.Sheets.Count:N0} building sheets, {country.Extras.Count} nationwide files");

        // ---- 5. places ------------------------------------------------------------------------
        AnsiConsole.MarkupLine("[bold]5/5[/] place names...");
        var placesPath = Path.Combine(paths.Chunks, PlaceIndex.FileName);
        if (File.Exists(placesPath))
        {
            var places = PlaceIndex.FromJson(await File.ReadAllTextAsync(placesPath));
            foreach (var p in places.Places)
                country.Towns.Add(new Town(p.Name, p.Canton, p.E, p.N, p.Kind, p.Rank));
            AnsiConsole.MarkupLine($"  {country.Towns.Count:N0} towns, summits and passes");
        }
        else AnsiConsole.MarkupLine("  [yellow]no terrain_chunks/places.json: place search will be empty[/]");

        country.Recount();
        country.Save(paths.CountrySourceFile);
        long total = country.AllTiles().Sum(country.SizeBytes);
        AnsiConsole.MarkupLine($"[green]wrote[/] {paths.CountrySourceFile} ({new FileInfo(paths.CountrySourceFile).Length / 1024:N0} KB): "
                               + $"{country.TileCount:N0} tiles, {total / 1e9:N0} GB of zips for the whole country");
        return 0;
    }

    /// <summary>Sizes swiss_data.py already recorded for files on disk: those need no HEAD at all.</summary>
    private static void SeedSizesFromManifests(Paths paths, Dictionary<string, long> sizes)
    {
        foreach (var dir in new[] { paths.AltiDir, paths.BuildingsDir, paths.TlmDir, paths.GwrDir, paths.RoutesDir })
        {
            var path = Path.Combine(dir, ".swiss_data_manifest.json");
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in doc.RootElement.EnumerateObject())
                if (entry.Value.TryGetProperty("url", out var u) && entry.Value.TryGetProperty("size", out var s)
                    && u.GetString() is { } url && s.ValueKind == JsonValueKind.Number)
                    sizes.TryAdd(url, s.GetInt64());
        }
    }

    private static async Task FillSizes(IEnumerable<string> urls, Dictionary<string, long> sizes, string cachePath)
    {
        var todo = urls.Where(u => !sizes.ContainsKey(u)).Distinct().ToList();
        if (todo.Count == 0)
        {
            AnsiConsole.MarkupLine("  all sizes already known");
            return;
        }
        // in slices, saved after each, so Ctrl+C loses at most one slice of work
        const int Slice = 4000;
        await AnsiConsole.Progress().StartAsync(async ctx =>
        {
            var task = ctx.AddTask($"HEAD {todo.Count:N0} files", maxValue: todo.Count);
            for (int i = 0; i < todo.Count; i += Slice)
            {
                var slice = todo.Skip(i).Take(Slice).ToList();
                int before = (int)task.Value;
                var got = await Stac.SizesAsync(slice, progress: n => task.Value = before + n);
                foreach (var (u, s) in got) sizes[u] = s;
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(sizes));
            }
        });
    }

    private static async Task<string?> LatestAsset(string collection, string suffix)
    {
        var items = await Stac.ListAsync(collection, null);
        return items.OrderByDescending(i => i.Datetime, StringComparer.Ordinal).ThenByDescending(i => i.Id, StringComparer.Ordinal)
            .SelectMany(i => i.Assets)
            .FirstOrDefault(a => a.Key.EndsWith(suffix, StringComparison.Ordinal))?.Href;
    }

    private static async Task<T> Progress<T>(string what, Func<Action<int>, Task<T>> work)
    {
        T result = default!;
        await AnsiConsole.Status().StartAsync(what, async ctx =>
        {
            result = await work(n => { if (n % 500 == 0) ctx.Status($"{n:N0} {what}"); });
        });
        return result;
    }

    /// <summary>
    /// Stamps each tile with the canton its centre falls in. The rings come from the canton
    /// territories of swissBOUNDARIES3D; where rings overlap (an enclave drawn inside its
    /// neighbour, since interior rings are not read) the smaller ring wins.
    /// </summary>
    private static async Task BakeCantons(CountryData country, string cacheDir)
    {
        var items = await Stac.ListAsync("ch.swisstopo.swissboundaries3d", null);
        var href = items.OrderByDescending(i => i.Id, StringComparer.Ordinal)
            .SelectMany(i => i.Assets).FirstOrDefault(a => a.Key.EndsWith(".gpkg.zip", StringComparison.Ordinal))?.Href;
        if (href == null)
        {
            AnsiConsole.MarkupLine("  [yellow]swissBOUNDARIES3D not found on STAC: no cantons[/]");
            return;
        }

        var gpkg = Path.Combine(cacheDir, Path.GetFileNameWithoutExtension(href.Split('/')[^1]));
        if (!File.Exists(gpkg))
        {
            var zip = gpkg + ".zip";
            AnsiConsole.MarkupLine($"  downloading {href.Split('/')[^1]}...");
            await Stac.DownloadAsync(href, zip);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.First(e => e.Name.EndsWith(".gpkg", StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(gpkg, overwrite: true);
            }
            File.Delete(zip);
        }

        var rings = CantonRings.Read(gpkg);

        country.Cantons.Clear();
        for (int i = 0; i < CantonCodes.Length; i++)
            country.Cantons.Add(new Canton((byte)(i + 1), CantonCodes[i], rings.Names.GetValueOrDefault(i + 1, CantonCodes[i])));

        int stamped = 0;
        var tiles = country.AllTiles().ToList();
        Parallel.ForEach(tiles, t =>
        {
            double e = t.MinE + 500, n = t.MinN + 500;
            byte best = rings.At(e, n);
            if (best != 0)
            {
                country.SetCanton(t, best);
                Interlocked.Increment(ref stamped);
            }
        });
        AnsiConsole.MarkupLine($"  {rings.RingCount:N0} canton rings, {stamped:N0} of {tiles.Count:N0} tiles inside a canton");
    }
}
