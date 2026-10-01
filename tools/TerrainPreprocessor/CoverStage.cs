using System.Diagnostics;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Tools.Preprocessor;

public static class CoverStage
{
    /// <summary>Clearance kept either side of a carriageway edge, in metres.</summary>
    private const double RoadClearance = 2.5;

    /// <summary>Marks every lattice cell lying within a road corridor of any of <paramref name="tiles"/>.</summary>
    private static bool[] BuildRoadMask(IEnumerable<RoadTile> tiles)
    {
        var mask = new bool[CoverFormat.Size * CoverFormat.Size];
        double spacing = ChunkFormat.SpacingM;

        foreach (var seg in tiles.SelectMany(t => t.Segments))
        {
            // tunnels and bridges need the widest clearance: their portals and abutments
            // are exactly where a stray tree ruins the shot
            double radius = seg.Width * 0.5 + RoadClearance
                + ((seg.Flags & (RoadFlags.Tunnel | RoadFlags.Bridge)) != 0 ? 4.0 : 0.0);
            int cells = (int)Math.Ceiling(radius / spacing);

            for (int i = 0; i < seg.PointCount - 1; i++)
            {
                var (ax, az) = (seg.Points[i * 3], seg.Points[i * 3 + 2]);
                var (bx, bz) = (seg.Points[(i + 1) * 3], seg.Points[(i + 1) * 3 + 2]);
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                int steps = Math.Max(1, (int)Math.Ceiling(len / spacing));

                for (int st = 0; st <= steps; st++)
                {
                    double t = (double)st / steps;
                    double x = ax + (bx - ax) * t;
                    double z = az + (bz - az) * t;
                    int c0 = (int)(x / spacing), r0 = (int)(z / spacing);

                    for (int dr = -cells; dr <= cells; dr++)
                        for (int dc = -cells; dc <= cells; dc++)
                        {
                            int c = c0 + dc, r = r0 + dr;
                            if ((uint)c >= CoverFormat.Size || (uint)r >= CoverFormat.Size) continue;
                            if (dc * dc + dr * dr > cells * cells) continue;
                            mask[r * CoverFormat.Size + c] = true;
                        }
                }
            }
        }
        return mask;
    }

    public static int Run(string tlmGpkg, string outDir, Dictionary<TileId, ChunkGrid> grids, string? overridesPath = null,
        string? rawDir = null)
    {
        if (!File.Exists(tlmGpkg))
        {
            Console.Error.WriteLine($"swissTLM3D GeoPackage not found: {tlmGpkg}");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var extractor = new CoverExtractor(tlmGpkg) { OverridesPath = overridesPath };

        var heightOf = TerrainSampler.For(grids);

        // roads are written before this stage, so their corridors can be masked out: the raw
        // extractor lines (they run through the junction areas the network stage trims away) and
        // the network stage's tile, whose motorway carriageways #117 moved outward and widened.
        // The full build runs the network stage before this one.
        foreach (var id in grids.Keys)
        {
            var tiles = new List<RoadTile>();
            foreach (var path in new[] { rawDir != null ? RawRoads.RoadPath(rawDir, id) : null,
                         Path.Combine(outDir, RoadFormat.FileName(id)) })
            {
                if (path is null || !File.Exists(path)) continue;
                using var fs = File.OpenRead(path);
                tiles.Add(RoadCodec.Decode(fs));
            }
            if (tiles.Count > 0) extractor.RoadMask[id] = BuildRoadMask(tiles);
        }

        extractor.Extract(grids.Keys.ToList(), heightOf);

        var histogram = new SortedDictionary<CoverClass, long>();
        long coveredCells = 0, totalCells = 0;
        foreach (var (id, cells) in extractor.Cover)
        {
            using (var fs = File.Create(Path.Combine(outDir, CoverFormat.FileName(id))))
                CoverFormat.Encode(id, cells, fs);

            foreach (byte b in cells)
            {
                totalCells++;
                if (b == 0) continue;
                coveredCells++;
                histogram[(CoverClass)b] = histogram.GetValueOrDefault((CoverClass)b) + 1;
            }
        }

        long treeTotal = 0;
        foreach (var (id, list) in extractor.Trees)
        {
            var trees = list.Select(t => new UnitSport.Terrain.Format.TreeInstance(t.X, t.Y, t.Z, t.Height, t.Kind)).ToList();
            using var fs = File.Create(Path.Combine(outDir, TreeFormat.FileName(id)));
            TreeFormat.Encode(id, trees, fs);
            treeTotal += trees.Count;
        }

        long ringTotal = extractor.LayerRings.Values.Sum();
        Console.WriteLine($"Cover: {ringTotal} rings from {extractor.LayerRings.Count} layers " +
                          $"over {extractor.Cover.Count} tiles in {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var (layer, rings) in extractor.LayerRings.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {layer}: {rings} rings");
        Console.WriteLine($"  road corridors masked on {extractor.RoadMask.Count} tiles");
        Console.WriteLine($"  classified {100.0 * coveredCells / totalCells:F1}% of vertices; " +
                          string.Join(", ", histogram.Select(kv => $"{kv.Key}={100.0 * kv.Value / totalCells:F1}%")));
        Console.WriteLine($"  trees: {treeTotal:N0} across {extractor.Trees.Count} tiles " +
                          $"({extractor.ScatteredTrees:N0} scattered, {extractor.PlantedTrees:N0} planted, " +
                          $"{extractor.SurveyedTrees:N0} surveyed singles)");
        return 0;
    }
}
