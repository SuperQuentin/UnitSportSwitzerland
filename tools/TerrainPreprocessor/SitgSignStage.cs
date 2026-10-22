using System.Globalization;
using System.Text;
using System.Text.Json;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--sitg-signs</c> (#1110, <c>docs/notes/tools/direction-signs.md</c>): canton Geneva's direction signs, the gantries and
/// signal masts they hang on and the overhead signal heads, from the SITG's open data (opendata.swiss, "terms_open": free use,
/// the source named), into <c>&lt;chunks&gt;_temp/sitg_signs.tsv</c>, where RoadGen's rewrite reads them (beside
/// <c>osm_overlay.tsv</c>): the real gantries and their sign text, and the benchmark of the inference rule used elsewhere. Only
/// the region's box, only where it meets the canton. Cached: an existing file is kept unless <c>--force</c>. A minute.
/// </summary>
public static class SitgSignStage
{
    public const string FileName = "sitg_signs.tsv";

    /// <summary>The canton's box (LV95, with a margin): a region outside it has nothing to fetch.</summary>
    private const double CantonMinE = 2_484_000, CantonMaxE = 2_514_000, CantonMinN = 1_108_000, CantonMaxN = 1_137_000;
    private const string Service = "https://vector.sitg.ge.ch/arcgis/rest/services/";
    private const int Page = 1000;

    /// <summary>One layer: what is asked of it and what goes into the file.</summary>
    private sealed record Layer(string Kind, string Path, string Where, string[] Fields);

    private static readonly Layer[] Layers =
    [
        // direction signs (Jalonnement): SSV number, the way the traffic reading it drives (deg), text, plate size, how it is fixed
        new("sign", "OTC_SV_SIGNAL/FeatureServer/0", "CATEGORIE='Jalonnement'",
            ["ID_SIGNAL", "ID_SUPPORT", "NO_OSR", "ORIENTATION", "FIXATION", "FORMAT", "NOM_VOIE", "CONTENU"]),
        // gantries and signal masts: the support is where a sign really stands (the sign's own point is moved off it for display)
        new("support", "OTC_SV_SUPPORT/FeatureServer/0", "TYPE_SUPPORT IN ('Portique','Mât feux')", ["ID_SUPPORT", "TYPE_SUPPORT"]),
        // 300 mm heads, '3x300-portique' over the lanes (positions schematic)
        new("head", "OTC_SL_EQUIPEMENT_BOITEFEUX/FeatureServer/0", "TYPE_FEUX LIKE '3x300%'", ["TYPE_FEUX"]),
    ];

    public static int Run(string outDir, string? tempDir, bool force)
    {
        var manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath)) { Console.Error.WriteLine($"--sitg-signs needs a built region: no {manifestPath}"); return 2; }
        string temp = tempDir ?? outDir.TrimEnd('/', '\\') + "_temp";
        string file = Path.Combine(temp, FileName);
        if (File.Exists(file) && !force) { Console.WriteLine($"[sitg-signs] kept {file} (--force to fetch again)"); return 0; }
        var tiles = TerrainManifest.FromJson(File.ReadAllText(manifestPath)).Tiles.Select(t => t.Id).ToList();
        if (tiles.Count == 0) return 0;
        double minE = Math.Max(tiles.Min(t => t.MinE), CantonMinE), maxE = Math.Min(tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM, CantonMaxE);
        double minN = Math.Max(tiles.Min(t => t.MaxN) - ChunkFormat.TileSizeM, CantonMinN), maxN = Math.Min(tiles.Max(t => t.MaxN), CantonMaxN);
        if (minE >= maxE || minN >= maxN) { Console.WriteLine("[sitg-signs] the region is outside canton Geneva: nothing to fetch"); return 0; }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(c, $"# sitg_signs v1 fetched={DateTime.UtcNow:yyyy-MM-dd} bbox={minE:F0},{minN:F0},{maxE:F0},{maxN:F0} source=SITG (Etat de Genève) via opendata.swiss, terms_open\n");
        sb.Append("kind\te\tn\tid\tsupport\tosr\torientation\tfixation\tformat\ttype\tname\ttext\n");
        var counts = new Dictionary<string, int>();
        foreach (var layer in Layers)
        {
            int n = 0;
            foreach (var (attrs, e, nn) in Fetch(http, layer, minE, minN, maxE, maxN))
            {
                string A(string k) => attrs.TryGetValue(k, out var v) ? Clean(v) : "";
                // kind e n id support osr orientation fixation format type name text
                string[] row = layer.Kind switch
                {
                    "sign" => [A("ID_SIGNAL"), A("ID_SUPPORT"), A("NO_OSR"), A("ORIENTATION"), A("FIXATION"), A("FORMAT"), "", A("NOM_VOIE"), A("CONTENU")],
                    "support" => ["", A("ID_SUPPORT"), "", "", "", "", A("TYPE_SUPPORT"), "", ""],
                    _ => ["", "", "", "", "", "", A("TYPE_FEUX"), "", ""],
                };
                sb.Append(c, $"{layer.Kind}\t{e:F2}\t{nn:F2}\t{string.Join('\t', row)}\n");
                n++;
            }
            counts[layer.Kind] = n;
        }
        Directory.CreateDirectory(temp);
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine($"[sitg-signs] {counts["sign"]} direction signs, {counts["support"]} gantries and signal masts, {counts["head"]} 300 mm heads -> {file}");
        return 0;
    }

    /// <summary>A layer's features in the box, page by page: attributes as text, the point in LV95.</summary>
    private static IEnumerable<(Dictionary<string, string> Attrs, double E, double N)> Fetch(HttpClient http, Layer layer,
        double minE, double minN, double maxE, double maxN)
    {
        var c = CultureInfo.InvariantCulture;
        for (int offset = 0; ; offset += Page)
        {
            string url = Service + layer.Path + "/query?where=" + Uri.EscapeDataString(layer.Where)
                + "&outFields=" + string.Join(',', layer.Fields)
                + string.Create(c, $"&geometry={minE:F0},{minN:F0},{maxE:F0},{maxN:F0}&geometryType=esriGeometryEnvelope&inSR=2056&outSR=2056")
                + string.Create(c, $"&orderByFields=OBJECTID&resultOffset={offset}&resultRecordCount={Page}&f=json");
            // UTF-8 whatever the response says (the service names no charset: "Genève" came back as "Gen?ve")
            using var doc = JsonDocument.Parse(http.GetByteArrayAsync(url).GetAwaiter().GetResult());
            if (doc.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException($"SITG {layer.Path}: {error}");
            var features = doc.RootElement.GetProperty("features");
            int count = 0;
            foreach (var f in features.EnumerateArray())
            {
                count++;
                if (!f.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object) continue;
                var attrs = new Dictionary<string, string>();
                foreach (var p in f.GetProperty("attributes").EnumerateObject())
                    attrs[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString()!,
                        JsonValueKind.Number => p.Value.GetRawText(),
                        _ => "",
                    };
                yield return (attrs, g.GetProperty("x").GetDouble(), g.GetProperty("y").GetDouble());
            }
            bool more = doc.RootElement.TryGetProperty("exceededTransferLimit", out var x) && x.ValueKind == JsonValueKind.True;
            if (count < Page && !more) yield break;
        }
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
}
