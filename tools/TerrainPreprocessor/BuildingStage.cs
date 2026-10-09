using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Turns swissBUILDINGS3D solids into per-tile .bldg files. The solids come either from the
/// FileGDB zips swisstopo publishes (#537, no GDAL and no export step) or from a GeoPackage left
/// by an older run; the extractor is opened once and run per batch of tiles (#570).
/// </summary>
public static class BuildingStage
{
    /// <summary>
    /// The extractor for the FileGDB route, built <b>once</b> and reused for every batch: it holds
    /// the GWR cadastre (3.37 M records, several seconds to load) and the per-sheet extents that
    /// let later batches skip sheets without opening them (#570).
    /// </summary>
    public static BuildingExtractor? OpenGdb(IReadOnlyList<string> gdbZips, string workDir, string? gwrPath)
    {
        var present = gdbZips.Where(File.Exists).ToList();
        foreach (var z in gdbZips.Where(z => !File.Exists(z))) Console.Error.WriteLine($"  missing, skipped: {z}");
        if (present.Count == 0)
        {
            Console.Error.WriteLine("No swissBUILDINGS3D .gdb.zip files to read");
            return null;
        }
        var extractor = new BuildingExtractor(present, workDir, gwrPath);
        Console.WriteLine($"Buildings: {extractor.CadastreCount} cadastre records loaded, {present.Count} sheet(s)");
        return extractor;
    }

    /// <summary>The GeoPackage route's extractor, likewise built once.</summary>
    public static BuildingExtractor? OpenGeoPackage(string gpkgPath, string? gwrPath)
    {
        if (File.Exists(gpkgPath))
        {
            var extractor = new BuildingExtractor(gpkgPath, gwrPath);
            Console.WriteLine($"Buildings: {extractor.CadastreCount} cadastre records loaded");
            return extractor;
        }
        Console.Error.WriteLine($"Buildings GeoPackage not found: {gpkgPath}");
        return null;
    }

    /// <summary>One batch of tiles, against an extractor from <see cref="OpenGdb"/>/<see cref="OpenGeoPackage"/>.</summary>
    public static int Run(BuildingExtractor extractor, string outDir, Dictionary<TileId, ChunkGrid> grids)
    {
        var sw = Stopwatch.StartNew();

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
