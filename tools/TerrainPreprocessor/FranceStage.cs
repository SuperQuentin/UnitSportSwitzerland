using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Adds French IGN BD TOPO® features to tiles that already exist.
///
/// <para>
/// <b>This stage merges; it never replaces.</b> The tiles along the border hold data from both
/// countries — 2506/1125 is more than a megabyte of Swiss buildings before a single French one is
/// added — so a stage that wrote a fresh <c>.bldg</c> would delete the Swiss half of every border
/// village. Existing files are decoded, appended to, and written back.
/// </para>
///
/// <para>
/// It is idempotent by construction only if run once: running it twice appends the same French
/// buildings again. <see cref="Run"/> therefore strips anything it previously added, identified by
/// <see cref="FrenchMarker"/>, before appending — so a re-run refreshes rather than duplicates.
/// </para>
/// </summary>
public static class FranceStage
{
    /// <summary>
    /// How a French building is recognised on a re-run.
    ///
    /// <para>
    /// The <c>.bldg</c> format has no source field and adding one would change the format for
    /// every tile in the country. <c>Egid</c> is the Swiss cadastre number and is 0 for anything
    /// unmatched, so it cannot be used either. <c>YearBuilt</c> can: BD TOPO carries no
    /// construction year, so nothing French ever has a real one, and no Swiss building is
    /// recorded as built in this year.
    /// </para>
    /// </summary>
    public const ushort FrenchMarker = 1;

    public static async Task<int> RunAsync(string outDir, double minLon, double minLat,
        double maxLon, double maxLat)
    {
        string manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"No manifest in {outDir}; build the terrain first.");
            return 2;
        }

        var manifest = TerrainManifest.FromJson(File.ReadAllText(manifestPath));
        var available = manifest.Tiles.Select(t => t.Id).ToHashSet();

        // Which tiles the box touches. Corners are not enough — the box is a lat/lon rectangle
        // and LV95 is rotated relative to it, so the extremes can fall on an edge.
        var wanted = new HashSet<TileId>();
        for (int i = 0; i <= 24; i++)
            for (int j = 0; j <= 24; j++)
            {
                double lat = minLat + (maxLat - minLat) * i / 24.0;
                double lon = minLon + (maxLon - minLon) * j / 24.0;
                var (e, n) = SwissProjection.ToLv95(lat, lon);
                var tile = TileId.FromLv95(e, n);
                if (available.Contains(tile)) wanted.Add(tile);
            }

        if (wanted.Count == 0)
        {
            Console.Error.WriteLine("That box does not overlap any built tile.");
            return 2;
        }

        Console.WriteLine($"France: {wanted.Count} tiles overlap the box "
            + $"({string.Join(", ", wanted.OrderBy(t => t.E).ThenBy(t => t.N).Take(8))}"
            + (wanted.Count > 8 ? ", …)" : ")"));

        // heights come from the tiles themselves, so French features drape on the same surface
        var grids = new Dictionary<TileId, ChunkGrid>();
        foreach (var tile in wanted)
        {
            using var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(tile)));
            grids[tile] = ChunkCodec.Decode(fs);
        }
        var heightAt = TerrainSampler.For(grids);

        var sw = Stopwatch.StartNew();
        using var client = new BdTopoClient();

        // ---- buildings -------------------------------------------------------------------
        var footprints = await client.FetchAsync("BDTOPO_V3:batiment", minLat, minLon, maxLat, maxLon)
            .ConfigureAwait(false);
        Console.WriteLine($"France: fetched {footprints.Count} building footprints");

        var buildingStats = new FranceBuildings.Stats();
        var buildingsByTile = FranceBuildings.Build(footprints, wanted, heightAt, buildingStats);

        foreach (var (tile, built) in buildingsByTile) MergeBuildings(outDir, tile, built);

        Console.WriteLine($"France: {buildingStats.Built} buildings "
            + $"({buildingStats.Pitched} pitched roofs, {buildingStats.Flat} flat), "
            + $"{buildingStats.NoHeight} with no usable height, {buildingStats.Skipped} skipped");

        // ---- roads -----------------------------------------------------------------------
        var lines = await client.FetchAsync("BDTOPO_V3:troncon_de_route", minLat, minLon, maxLat, maxLon)
            .ConfigureAwait(false);
        Console.WriteLine($"France: fetched {lines.Count} road sections");

        var roadStats = new FranceRoads.Stats();
        var roadsByTile = FranceRoads.Build(lines, wanted, heightAt, roadStats);

        foreach (var (tile, segments) in roadsByTile) MergeRoads(outDir, tile, segments);

        Console.WriteLine($"France: {roadStats.Segments} road segments "
            + $"({roadStats.Bridges} bridge, {roadStats.Tunnels} tunnel, {roadStats.Cycle} cycle), "
            + $"{roadStats.Skipped} skipped");
        Console.WriteLine("France: by class  "
            + string.Join("  ", roadStats.ByClass.Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"France: {client.Requests} requests, {sw.Elapsed.TotalSeconds:F1}s");

        return 0;
    }

    private static void MergeBuildings(string outDir, TileId tile, List<Building> french)
    {
        string path = Path.Combine(outDir, BuildingFormat.FileName(tile));
        var keep = new List<Building>();

        if (File.Exists(path))
        {
            using var fs = File.OpenRead(path);
            foreach (var b in BuildingCodec.Decode(fs).Buildings)
                if (b.YearBuilt != FrenchMarker) keep.Add(b);   // drop a previous French pass
        }

        int swiss = keep.Count;
        foreach (var b in french)
            keep.Add(new Building
            {
                Kind = b.Kind, Egid = b.Egid, YearBuilt = FrenchMarker, Floors = b.Floors,
                MinY = b.MinY, MaxY = b.MaxY, Triangles = b.Triangles,
            });

        using var output = File.Create(path);
        BuildingCodec.Encode(new BuildingTile { Id = tile, Buildings = keep }, output);
        Console.WriteLine($"  {tile}: buildings {swiss} existing + {french.Count} French");
    }

    /// <summary>
    /// Appends French road segments, dropping any added by an earlier run.
    ///
    /// <para>
    /// <c>.road</c> has no source field either, and no spare one to borrow. So a previous pass is
    /// identified geometrically: a segment whose every vertex lies inside the box being rebuilt
    /// and which is not present in the Swiss data cannot be told apart cheaply — instead the
    /// whole file is rebuilt from a kept copy of the original, saved beside it the first time.
    /// </para>
    /// </summary>
    private static void MergeRoads(string outDir, TileId tile, List<RoadSegment> french)
    {
        string path = Path.Combine(outDir, RoadFormat.FileName(tile));
        string original = path + ".swiss";

        // The first run stashes the untouched Swiss file; every later run starts from that,
        // which is what makes re-importing safe rather than cumulative.
        if (File.Exists(path) && !File.Exists(original)) File.Copy(path, original);

        var tileData = new RoadTile { Id = tile, Segments = new List<RoadSegment>() };
        if (File.Exists(original))
        {
            // the whole tile, v3 layers and header flags included
            using var fs = File.OpenRead(original);
            tileData = RoadCodec.Decode(fs);
        }

        int swiss = tileData.Segments.Count;
        tileData.Segments.AddRange(french);

        using var output = File.Create(path);
        RoadCodec.Encode(tileData, output);
        Console.WriteLine($"  {tile}: roads {swiss} existing + {french.Count} French");
    }

    /// <summary>Parses "--france minLon,minLat,maxLon,maxLat".</summary>
    public static (double MinLon, double MinLat, double MaxLon, double MaxLat)? ParseBox(string arg)
    {
        var parts = arg.Split(',');
        if (parts.Length != 4) return null;

        var v = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                return null;

        return (Math.Min(v[0], v[2]), Math.Min(v[1], v[3]),
                Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }
}
