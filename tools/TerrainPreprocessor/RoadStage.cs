using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>Runs road extraction for the tiles whose terrain chunks already exist.</summary>
public static class RoadStage
{
    public static int Run(string tlmGpkg, string? routeKeys, string outDir,
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

        foreach (var (id, tile) in tiles)
        {
            using (var fs = File.Create(Path.Combine(outDir, RoadFormat.FileName(id))))
                RoadCodec.Encode(tile, fs);

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

        Console.WriteLine($"Roads written for {tiles.Count} tiles: {totalSegments} segments in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  tunnel portals: {holeCells} carved quads across {holeTiles} tiles");
        Console.WriteLine("  by class:   " + string.Join(", ", byClass.Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"  by surface: Paved={paved}, Natural={natural}");
        Console.WriteLine("  flags:      " + string.Join(", ", flagCounts.Select(kv => $"{kv.Key}={kv.Value}")));
        return 0;
    }
}
