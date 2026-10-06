using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

public static class BuildingStage
{
    public static int Run(string gpkgPath, string? gwrPath, string outDir,
        Dictionary<TileId, ChunkGrid> grids)
    {
        if (!File.Exists(gpkgPath))
        {
            Console.Error.WriteLine($"Buildings GeoPackage not found: {gpkgPath}");
            Console.Error.WriteLine("  run: python tools/export_buildings.py --bbox <minE minN maxE maxN>");
            return 1;
        }
        return Run(new BuildingExtractor(gpkgPath, gwrPath), outDir, grids);
    }

    /// <summary>
    /// The same stage, reading the swissBUILDINGS3D FileGDB zips directly (#537): no GDAL, and no
    /// intermediate GeoPackage to export first.
    /// </summary>
    public static int RunFromGdb(IReadOnlyList<string> gdbZips, string workDir, string? gwrPath,
        string outDir, Dictionary<TileId, ChunkGrid> grids)
    {
        var missing = gdbZips.Where(z => !File.Exists(z)).ToList();
        if (gdbZips.Count == 0 || missing.Count == gdbZips.Count)
        {
            Console.Error.WriteLine("No swissBUILDINGS3D .gdb.zip files to read");
            return 1;
        }
        foreach (var z in missing) Console.Error.WriteLine($"  missing, skipped: {z}");
        return Run(new BuildingExtractor(gdbZips.Where(File.Exists).ToList(), workDir, gwrPath), outDir, grids);
    }

    private static int Run(BuildingExtractor extractor, string outDir, Dictionary<TileId, ChunkGrid> grids)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Buildings: {extractor.CadastreCount} cadastre records loaded");

        double? HeightOf(double e, double n)
        {
            var id = TileId.FromLv95(e, n);
            return grids.TryGetValue(id, out var g) ? g.SampleHeight(e, n) : null;
        }

        var result = extractor.Extract(grids.Keys.ToList(), HeightOf);

        long totalTris = 0;
        var byKind = new SortedDictionary<BuildingKind, int>();
        int withYear = 0, withFloors = 0, count = 0;

        foreach (var (id, tile) in result)
        {
            string path = Path.Combine(outDir, BuildingFormat.FileName(id));
            if (tile.Buildings.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                continue;
            }
            using (var fs = File.Create(path))
                BuildingCodec.Encode(tile, fs);

            foreach (var b in tile.Buildings)
            {
                count++;
                totalTris += b.TriangleCount;
                byKind[b.Kind] = byKind.GetValueOrDefault(b.Kind) + 1;
                if (b.YearBuilt > 0) withYear++;
                if (b.Floors > 0) withFloors++;
            }
        }

        double pct = extractor.Total == 0 ? 0 : 100.0 * extractor.Matched / extractor.Total;
        Console.WriteLine($"Buildings written: {count} across {result.Count(kv => kv.Value.Buildings.Count > 0)} tiles, " +
                          $"{totalTris:N0} triangles in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  cadastre matched: {extractor.Matched}/{extractor.Total} ({pct:F0}%), " +
                          $"year known {withYear}, floors known {withFloors}");
        Console.WriteLine($"  re-seated on terrain: {extractor.Reseated} buildings, max shift {extractor.MaxLift:F1} m");
        if (extractor.StrayBuildings > 0)
            Console.WriteLine($"  stray faces dropped: {extractor.StrayFaces} from {extractor.StrayBuildings} buildings");
        Console.WriteLine("  by kind: " + string.Join(", ", byKind.Select(kv => $"{kv.Key}={kv.Value}")));
        return 0;
    }
}
