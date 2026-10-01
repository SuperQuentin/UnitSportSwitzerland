using System.Diagnostics;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Runs road extraction for the tiles whose terrain chunks already exist. The output is the
/// network stage's raw input (<see cref="RawRoads"/>, in the temp dir); the chunk dir's
/// <c>.road</c> tiles are written by <see cref="RunNetwork"/> once every batch is extracted.
/// </summary>
public static class RoadStage
{
    public static int Run(string tlmGpkg, string? routeKeys, string outDir, string tempDir,
        Dictionary<TileId, ChunkGrid> grids)
    {
        if (!File.Exists(tlmGpkg))
        {
            Console.Error.WriteLine($"swissTLM3D GeoPackage not found: {tlmGpkg}");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var extractor = new RoadExtractor(tlmGpkg, routeKeys);
        Console.WriteLine($"Roads: {extractor.CycleKeyCount} cycle / {extractor.MtbKeyCount} MTB route keys loaded");

        var tiles = extractor.Extract(grids.Keys.ToList(), TerrainSampler.For(grids));

        int totalSegments = 0;
        var byClass = new SortedDictionary<RoadClass, int>();
        var flagCounts = new SortedDictionary<string, int>();
        int paved = 0, natural = 0;

        string rawDir = RawRoads.DirFor(tempDir);
        foreach (var (id, tile) in tiles)
        {
            RawRoads.Write(rawDir, tile, extractor.SourceKeys.GetValueOrDefault(id));

            totalSegments += tile.Segments.Count;
            foreach (var s in tile.Segments)
            {
                byClass[s.Class] = byClass.GetValueOrDefault(s.Class) + 1;
                if (s.Surface == RoadSurface.Paved) paved++;
                else if (s.Surface == RoadSurface.Natural) natural++;
                foreach (RoadFlags f in Enum.GetValues<RoadFlags>())
                    if (f != RoadFlags.None && (s.Flags & f) != 0)
                        flagCounts[f.ToString()] = flagCounts.GetValueOrDefault(f.ToString()) + 1;
            }
        }

        // tunnel portal holes — only written for tiles that actually have one
        foreach (var id in grids.Keys)
        {
            string holePath = Path.Combine(outDir, HoleFormat.FileName(id));
            if (extractor.Holes.TryGetValue(id, out var cells) && cells.Count > 0)
            {
                using var fs = File.Create(holePath);
                HoleFormat.Encode(id, cells, fs);
            }
            else if (File.Exists(holePath))
            {
                File.Delete(holePath); // stale from an earlier run
            }
        }
        int holeTiles = extractor.Holes.Count(kv => kv.Value.Count > 0);
        int holeCells = extractor.Holes.Sum(kv => kv.Value.Count);

        Console.WriteLine($"Raw roads written for {tiles.Count} tiles: {totalSegments} segments in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  tunnel portals: {holeCells} carved quads across {holeTiles} tiles");
        Console.WriteLine("  by class:   " + string.Join(", ", byClass.Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"  by surface: Paved={paved}, Natural={natural}");
        Console.WriteLine("  flags:      " + string.Join(", ", flagCounts.Select(kv => $"{kv.Key}={kv.Value}")));
        return 0;
    }

    /// <summary>
    /// The road network stage over <paramref name="tiles"/>: raw input -> junctions, attributes,
    /// OSM overlay (when <c>&lt;temp&gt;/osm_overlay.tsv</c> exists) -> v3 tiles in the chunk dir.
    /// Always from the raw input, so a second build writes the same bytes.
    /// </summary>
    public static int RunNetwork(string outDir, string tempDir, IReadOnlyList<TileId> tiles)
    {
        var sw = Stopwatch.StartNew();
        string overlay = Path.Combine(tempDir, "osm_overlay.tsv");
        Console.WriteLine($"Road network stage: {tiles.Count} tiles"
            + (File.Exists(overlay) ? " with the OSM overlay (ODbL)" : ""));
        try
        {
            var stats = TileRewriter.Run(outDir, tiles, new TileRewriter.Options(
                RawDir: RawRoads.DirFor(tempDir), OsmOverlay: overlay), Console.WriteLine);
            Console.WriteLine($"  {stats.TilesWritten} tiles, {stats.SegmentsWritten:N0} segments, "
                + $"{stats.Junctions:N0} junctions in {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine(stats.Network.Format(stats.TilesWritten));
            Console.WriteLine(stats.Heights.Format());
            return 0;
        }
        catch (TileRewriter.AlreadyRewrittenException e)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }
    }
}
