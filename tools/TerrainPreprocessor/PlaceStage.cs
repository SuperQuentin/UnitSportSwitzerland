using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Builds the searchable place index from the GWR building register.
///
/// A municipality's coordinate is taken from its **densest 500 m cell**, not its centroid:
/// Swiss communes often stretch far up a hillside, so the centroid of Riddes lands on the
/// mountain above it rather than in the village.
///
/// Only places whose coordinate falls inside an imported tile are kept, so every search
/// result is somewhere you can actually stand.
/// </summary>
public static class PlaceStage
{
    private const double CellSize = 500.0;

    public static int Run(string gwrPath, string outDir, IReadOnlySet<TileId> available)
    {
        if (!File.Exists(gwrPath))
        {
            Console.Error.WriteLine($"GWR database not found: {gwrPath}");
            return 1;
        }

        var sw = Stopwatch.StartNew();

        // name -> cell -> count, plus the canton for disambiguation
        var cells = new Dictionary<(string Name, string Canton), Dictionary<(int, int), int>>();
        var totals = new Dictionary<(string Name, string Canton), int>();

        using (var conn = GeoPackageReader.Open(gwrPath))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                select GGDENAME, GDEKT, GKODE, GKODN from building
                where GGDENAME is not null and GKODE is not null and GKODN is not null
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
                double e = r.GetDouble(2), n = r.GetDouble(3);

                totals[key] = totals.GetValueOrDefault(key) + 1;
                if (!cells.TryGetValue(key, out var grid))
                    cells[key] = grid = new Dictionary<(int, int), int>();
                var cell = ((int)Math.Floor(e / CellSize), (int)Math.Floor(n / CellSize));
                grid[cell] = grid.GetValueOrDefault(cell) + 1;
            }
        }

        var index = new PlaceIndex();
        int skipped = 0;

        foreach (var (key, grid) in cells)
        {
            var best = grid.MaxBy(kv => kv.Value);
            double e = best.Key.Item1 * CellSize + CellSize / 2;
            double n = best.Key.Item2 * CellSize + CellSize / 2;

            // a place with no terrain would teleport you into empty sky
            if (!available.Contains(TileId.FromLv95(e, n))) { skipped++; continue; }

            index.Places.Add(new Place
            {
                Name = key.Name,
                Canton = key.Canton,
                E = e,
                N = n,
                Buildings = totals[key],
            });
        }

        index.Places.Sort((a, b) => b.Buildings.CompareTo(a.Buildings));
        File.WriteAllText(Path.Combine(outDir, PlaceIndex.FileName), index.ToJson());

        Console.WriteLine($"Places: {index.Places.Count} with terrain, {skipped} outside the " +
                          $"imported tiles, in {sw.Elapsed.TotalSeconds:F1}s");
        if (index.Places.Count > 0)
            Console.WriteLine("  largest: " + string.Join(", ",
                index.Places.Take(6).Select(p => $"{p.Name} ({p.Buildings})")));
        return 0;
    }
}
