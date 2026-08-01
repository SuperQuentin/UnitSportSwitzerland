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

    public static int Run(string gwrPath, string outDir, IReadOnlySet<TileId> available,
        string? tlmPath = null)
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

        int summits = tlmPath is not null ? AddSummits(tlmPath, index, available) : 0;

        index.Places.Sort((a, b) => b.Rank.CompareTo(a.Rank));
        File.WriteAllText(Path.Combine(outDir, PlaceIndex.FileName), index.ToJson());

        Console.WriteLine($"Places: {index.Places.Count} with terrain ({summits} summits and " +
                          $"passes), {skipped} outside the imported tiles, " +
                          $"in {sw.Elapsed.TotalSeconds:F1}s");
        if (index.Places.Count > 0)
            Console.WriteLine("  top: " + string.Join(", ", index.Places.Take(6)
                .Select(p => p.Kind == PlaceKind.Town
                    ? $"{p.Name} ({p.Buildings} bldg)"
                    : $"{p.Name} ({p.Elevation} m)")));
        return 0;
    }

    /// <summary>
    /// Adds named summits and passes from <c>tlm_namen_name_pkt</c>.
    ///
    /// <para>
    /// The index was municipalities only, so the teleport search could not find a single
    /// mountain in a country largely made of them. These points carry both a name and a
    /// surveyed <c>hoehe</c>, which is what lets a search result put you on the summit rather
    /// than somewhere below it.
    /// </para>
    ///
    /// <para>
    /// Names are multilingual and pipe-separated in the source — "Nordend | Punta Nordend" —
    /// so the first form is taken and the rest dropped; the search normalises accents anyway.
    /// </para>
    /// </summary>
    private static int AddSummits(string tlmPath, PlaceIndex index, IReadOnlySet<TileId> available)
    {
        if (!File.Exists(tlmPath)) return 0;

        var kinds = new Dictionary<string, PlaceKind>(StringComparer.Ordinal)
        {
            ["Hauptgipfel"] = PlaceKind.Summit,
            ["Gipfel"] = PlaceKind.Summit,
            ["Alpiner Gipfel"] = PlaceKind.Summit,
            ["Felskopf"] = PlaceKind.Summit,
            ["Haupthuegel"] = PlaceKind.Summit,
            ["Huegel"] = PlaceKind.Summit,
            ["Pass"] = PlaceKind.Pass,
            ["Strassenpass"] = PlaceKind.Pass,
        };

        int added = 0;
        using var conn = GeoPackageReader.Open(tlmPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select objektart, name, hoehe, geom from tlm_namen_name_pkt
            where name is not null and geom is not null
            """;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!kinds.TryGetValue(r.GetString(0), out var kind)) continue;

            string name = r.GetString(1).Split('|')[0].Trim();
            if (name.Length == 0) continue;

            int elevation = r.IsDBNull(2) ? 0 : (int)Math.Round(r.GetDouble(2));

            // POINT geometry, so the point parser — the linestring one silently returns nothing
            foreach (var (e, n, _) in GeoPackageReader.ParsePoints((byte[])r.GetValue(3)))
            {
                if (!available.Contains(TileId.FromLv95(e, n))) break;

                index.Places.Add(new Place
                {
                    Name = name,
                    Canton = "",
                    E = e,
                    N = n,
                    Buildings = 0,
                    Kind = kind,
                    Elevation = elevation,
                });
                added++;
                break;
            }
        }

        return added;
    }
}
