using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--tram-stops</c> (#1057): the region's tram stops into <c>&lt;chunks&gt;_temp/tram_stops.tsv</c>, where RoadGen's rewrite
/// reads them (beside <c>osm_overlay.tsv</c>) and stands a stop sign by each tram track there (a <see cref="PointPropType.TramStop"/>
/// prop the game's trams stop at). swissTLM3D has no tram stop of its own: a public transport stop (<c>tlm_oev_haltestelle</c>, any
/// kind: Geneva's tram stops are mapped as bus or rail stops) within <see cref="Reach"/> m of a tram track
/// (<c>tlm_oev_eisenbahn</c> <c>verkehrsmittel</c> Tram or Metro) is one. Re-runnable, seconds.
/// </summary>
public static class TramStopStage
{
    /// <summary>A stop this close to a tram track serves it (m): a platform beside the track, or a stop in the street it runs in.</summary>
    public const double Reach = 25;

    public const string FileName = "tram_stops.tsv";
    /// <summary>OSM's tram stops (<c>railway=tram_stop</c>, tram stop positions), written by <c>--osm-overlay</c> beside it.</summary>
    public const string OsmFileName = "osm_tram_stops.tsv";

    public static int Run(string outDir, string tlmPath)
    {
        if (!File.Exists(tlmPath)) { Console.Error.WriteLine($"swissTLM3D GeoPackage not found: {tlmPath}"); return 1; }
        var manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath)) { Console.Error.WriteLine($"--tram-stops needs a built region: no {manifestPath}"); return 2; }
        var tiles = TerrainManifest.FromJson(File.ReadAllText(manifestPath)).Tiles.Select(t => t.Id).ToHashSet();
        if (tiles.Count == 0) return 0;
        double minE = tiles.Min(t => t.MinE), maxE = tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        double minN = tiles.Min(t => t.MaxN) - ChunkFormat.TileSizeM, maxN = tiles.Max(t => t.MaxN);
        using var conn = GeoPackageReader.Open(tlmPath);

        // the tram tracks, as segments
        var segments = new List<(double E0, double N0, double E1, double N1)>();
        using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_oev_eisenbahn", new[] { "verkehrsmittel", "ausser_betrieb" }, minE - Reach, minN - Reach, maxE + Reach, maxN + Reach))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.GetString(0) is not ("Tram" or "Metro")) continue;
                if (!r.IsDBNull(1) && r.GetString(1) is "Wahr" or "true" or "1") continue;
                foreach (var line in GeoPackageReader.ParseLines((byte[])r[2]))
                    for (int i = 0; i + 1 < line.Count; i++) segments.Add((line.E[i], line.N[i], line.E[i + 1], line.N[i + 1]));
            }
        // the stops beside them
        var stops = new List<(string Name, double E, double N)>();
        using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_oev_haltestelle", new[] { "objektart", "name" }, minE, minN, maxE, maxN))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                string name = r.IsDBNull(1) ? "Stop" : r.GetString(1);
                foreach (var (e, n, _) in GeoPackageReader.ParsePoints((byte[])r[2]))
                    if (tiles.Contains(TileId.FromLv95(e, n)) && segments.Any(s => Distance(e, n, s) < Reach)) stops.Add((name, e, n));
            }
        stops.Sort((a, b) => a.E != b.E ? a.E.CompareTo(b.E) : a.N.CompareTo(b.N));
        var sb = new StringBuilder("name\te\tn\n");
        foreach (var (name, e, n) in stops)
            sb.Append(CultureInfo.InvariantCulture, $"{name.Replace('\t', ' ')}\t{e:F2}\t{n:F2}\n");
        string temp = outDir.TrimEnd('/', '\\') + "_temp";
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, FileName), sb.ToString());
        Console.WriteLine($"[tram-stops] {stops.Count} stops by {segments.Count:N0} tram track segments -> {Path.Combine(temp, FileName)}");
        return 0;
    }

    private static double Distance(double e, double n, (double E0, double N0, double E1, double N1) s)
    {
        double dx = s.E1 - s.E0, dy = s.N1 - s.N0, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((e - s.E0) * dx + (n - s.N0) * dy) / l2, 0, 1);
        double ex = s.E0 + dx * t - e, ey = s.N0 + dy * t - n;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
