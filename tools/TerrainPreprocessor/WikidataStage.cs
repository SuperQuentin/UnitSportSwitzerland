using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--wikidata</c> (#1224, plan 1.4 layer 2): facts for the Atlas cards from Wikidata (CC0, no
/// attribution needed) into <c>atlas_facts.json</c> next to <c>pois.bin</c>. Optional and separate from
/// <c>--pois</c> because it needs the network: one SPARQL query per Wikidata class (items in
/// Switzerland with a position), cached in <c>&lt;temp&gt;/wikidata/</c>. A POI takes the item of its kind
/// within 500 m whose label matches its name (<see cref="WikidataMatch"/>); a match below the score, or
/// two items scoring alike, is dropped, never guessed.
/// </summary>
public static class WikidataStage
{
    public const string FileName = "atlas_facts.json";
    private const string Endpoint = "https://query.wikidata.org/sparql";

    /// <summary>The Wikidata classes (<c>P31</c>) of each kind; kinds absent here get no facts.</summary>
    public static readonly Dictionary<PoiKind, string[]> Classes = new()
    {
        [PoiKind.Peak] = new[] { "Q8502", "Q207326", "Q54050" },          // mountain, summit, hill
        [PoiKind.Pass] = new[] { "Q133056" },                              // mountain pass
        [PoiKind.Lake] = new[] { "Q23397", "Q131681" },                    // lake, reservoir
        [PoiKind.Castle] = new[] { "Q23413", "Q751876", "Q17715832" },     // castle, château, castle ruin
        [PoiKind.Waterfall] = new[] { "Q34038" },
        [PoiKind.Church] = new[] { "Q16970", "Q2977" },                    // church building, cathedral
        [PoiKind.Chapel] = new[] { "Q108325" },
        [PoiKind.LookoutTower] = new[] { "Q1440300", "Q12518" },           // observation tower, tower
        [PoiKind.Dam] = new[] { "Q12323" },
        [PoiKind.Bridge] = new[] { "Q1825472", "Q12280" },                 // covered bridge, bridge
        [PoiKind.Hut] = new[] { "Q182676" },                               // mountain hut
        [PoiKind.RailStation] = new[] { "Q55488" },                        // railway station
    };

    /// <summary>The facts kept for a POI. Numbers are invariant; a year is the inception's.</summary>
    public sealed class Facts
    {
        [JsonPropertyName("qid")] public string Qid { get; set; } = "";
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("elevation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Elevation { get; set; }
        [JsonPropertyName("year"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Year { get; set; }
        [JsonPropertyName("length"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Length { get; set; }
        [JsonPropertyName("area"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Area { get; set; }
        /// <summary>A Wikimedia Commons file name (<c>P18</c>); its credit is the file page's, fetched with the image.</summary>
        [JsonPropertyName("image"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Image { get; set; }
        [JsonPropertyName("distance")] public double Distance { get; set; }
    }

    public static async Task<int> RunAsync(string outDir, string? poisFile, string tempDir, CancellationToken ct)
    {
        poisFile ??= Path.Combine(outDir, PoiFile.FileName);
        if (!File.Exists(poisFile))
        {
            Console.Error.WriteLine($"--wikidata needs a built {poisFile}: run --pois first");
            return 2;
        }
        var sw = Stopwatch.StartNew();
        var pois = PoiFile.Decode(File.ReadAllBytes(poisFile)).Pois;
        string cache = Path.Combine(tempDir, "wikidata");
        Directory.CreateDirectory(cache);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UnitSportSwitzerland-preprocessor/1.0 (https://github.com/SuperQuentin/UnitSportSwitzerland)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/sparql-results+json");

        var result = new SortedDictionary<string, Facts>(StringComparer.Ordinal);
        foreach (var (kind, classes) in Classes)
        {
            var ofKind = pois.Where(p => p.Kind == kind).ToList();
            if (ofKind.Count == 0) continue;
            var items = new List<WikidataMatch.Item>();
            foreach (var q in classes) items.AddRange(await Query(http, q, cache, ct));
            var matcher = new WikidataMatch(items);
            int matched = 0, ambiguous = 0;
            foreach (var p in ofKind)
                switch (matcher.Best(p.Name, p.E, p.N))
                {
                    case (WikidataMatch.Item item, double distance):
                        result[p.Id] = new Facts
                        {
                            Qid = item.Qid, Label = item.Labels.FirstOrDefault() ?? "", Elevation = item.Elevation, Year = item.Year,
                            Length = item.Length, Area = item.Area, Image = item.Image, Distance = Math.Round(distance),
                        };
                        matched++;
                        break;
                    case null when matcher.LastWasAmbiguous: ambiguous++; break;
                }
            Console.WriteLine($"  wikidata {PoiId.Tag(kind)}: {items.Select(i => i.Qid).Distinct().Count()} items, {matched} of {ofKind.Count} POIs matched, {ambiguous} ambiguous dropped");
        }
        string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(poisFile))!, FileName);
        File.WriteAllText(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wikidata: {result.Count} of {pois.Count} POIs with facts -> {file} in {sw.Elapsed.TotalSeconds:F0} s");
        return 0;
    }

    private static async Task<List<WikidataMatch.Item>> Query(HttpClient http, string qid, string cache, CancellationToken ct)
    {
        string path = Path.Combine(cache, qid + ".json");
        string json;
        if (File.Exists(path)) json = await File.ReadAllTextAsync(path, ct);
        else
        {
            string sparql = $$"""
                SELECT ?item ?label ?coord ?elev ?inception ?length ?area ?image WHERE {
                  ?item wdt:P31 wd:{{qid}}; wdt:P17 wd:Q39; wdt:P625 ?coord.
                  ?item rdfs:label ?label. FILTER(LANG(?label) IN ("de", "fr", "it", "rm", "en", "gsw"))
                  OPTIONAL { ?item wdt:P2044 ?elev. }
                  OPTIONAL { ?item wdt:P571 ?inception. }
                  OPTIONAL { ?item wdt:P2043 ?length. }
                  OPTIONAL { ?item wdt:P2046 ?area. }
                  OPTIONAL { ?item wdt:P18 ?image. }
                }
                """;
            json = "";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                    {
                        Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", sparql) }),
                    };
                    using var resp = await http.SendAsync(req, ct);
                    resp.EnsureSuccessStatusCode();
                    json = await resp.Content.ReadAsStringAsync(ct);
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    if (attempt >= 4)
                    {
                        Console.Error.WriteLine($"  wikidata {qid}: {e.Message}; no facts from it this run");
                        return new List<WikidataMatch.Item>();
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                }
            }
            await File.WriteAllTextAsync(path, json, ct);
            await Task.Delay(1000, ct);   // the query service asks for one request at a time, politely
        }
        return Parse(json);
    }

    /// <summary>A SPARQL JSON result into items: one per QID, every label, the first value of each fact.</summary>
    public static List<WikidataMatch.Item> Parse(string json)
    {
        var byQid = new Dictionary<string, WikidataMatch.Item>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        foreach (var row in doc.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray())
        {
            string? Val(string name) => row.TryGetProperty(name, out var v) ? v.GetProperty("value").GetString() : null;
            double? Num(string name) => double.TryParse(Val(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
            string? item = Val("item"), coord = Val("coord");
            if (item == null || coord == null || !TryPoint(coord, out double lon, out double lat)) continue;
            string qid = item[(item.LastIndexOf('/') + 1)..];
            if (!byQid.TryGetValue(qid, out var it))
            {
                var (e, n) = SwissProjection.ToLv95(lat, lon);
                byQid[qid] = it = new WikidataMatch.Item(qid, e, n);
            }
            if (Val("label") is { Length: > 0 } label && !it.Labels.Contains(label)) it.Labels.Add(label);
            it.Elevation ??= Num("elev");
            it.Length ??= Num("length");
            it.Area ??= Num("area");
            if (it.Year == null && Val("inception") is { Length: >= 5 } inc
                && int.TryParse(inc.TrimStart('+').Split('-')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int year) && year > 0)
                it.Year = year;
            if (it.Image == null && Val("image") is { } img) it.Image = Uri.UnescapeDataString(img[(img.LastIndexOf('/') + 1)..]);
        }
        return byQid.Values.ToList();
    }

    /// <summary>"Point(7.66 45.97)" (WKT, longitude first).</summary>
    private static bool TryPoint(string wkt, out double lon, out double lat)
    {
        lon = lat = 0;
        int a = wkt.IndexOf('('), b = wkt.IndexOf(')');
        if (a < 0 || b < a) return false;
        var parts = wkt[(a + 1)..b].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
    }
}

/// <summary>
/// Matches a POI to a Wikidata item (pure, tier 0): within <see cref="Reach"/> m, a label equal to the name
/// (normalised: case, accents, punctuation) scores 1, a label containing the name or contained in it (at least
/// five letters) 0.6; the score falls with distance to half at the reach. The best above <see cref="MinScore"/>
/// wins, unless another item scores within <see cref="Margin"/> of it (ambiguous: dropped).
/// </summary>
public sealed class WikidataMatch
{
    public const double Reach = 500, MinScore = 0.55, Margin = 0.1;

    public sealed class Item(string qid, double e, double n)
    {
        public string Qid { get; } = qid;
        public double E { get; } = e;
        public double N { get; } = n;
        public List<string> Labels { get; } = new();
        public double? Elevation { get; set; }
        public int? Year { get; set; }
        public double? Length { get; set; }
        public double? Area { get; set; }
        public string? Image { get; set; }
    }

    private readonly Dictionary<(int, int), List<Item>> _cells = new();

    /// <summary>Whether the last <see cref="Best"/> found two items too close in score.</summary>
    public bool LastWasAmbiguous { get; private set; }

    public WikidataMatch(IEnumerable<Item> items)
    {
        foreach (var it in items)
        {
            var key = ((int)Math.Floor(it.E / 1000), (int)Math.Floor(it.N / 1000));
            if (!_cells.TryGetValue(key, out var l)) _cells[key] = l = new List<Item>();
            l.Add(it);
        }
    }

    public static string Key(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in PlaceIndex.Normalize(s))
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static double NameScore(string name, string label)
    {
        string a = Key(name), b = Key(label);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (Math.Min(a.Length, b.Length) >= 5 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))) return 0.6;
        return 0;
    }

    public (Item Item, double Distance)? Best(string name, double e, double n)
    {
        LastWasAmbiguous = false;
        Item? best = null;
        double bestScore = 0, second = 0, bestD = 0;
        int ce = (int)Math.Floor(e / 1000), cn = (int)Math.Floor(n / 1000);
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                if (!_cells.TryGetValue((ce + de, cn + dn), out var items)) continue;
                foreach (var it in items)
                {
                    double d = Math.Sqrt((it.E - e) * (it.E - e) + (it.N - n) * (it.N - n));
                    if (d > Reach) continue;
                    double score = it.Labels.Select(l => NameScore(name, l)).DefaultIfEmpty(0).Max() * (1 - 0.5 * d / Reach);
                    if (score > bestScore) { second = bestScore; bestScore = score; best = it; bestD = d; }
                    else if (score > second) second = score;
                }
            }
        if (best == null || bestScore < MinScore) return null;
        if (bestScore - second < Margin) { LastWasAmbiguous = true; return null; }
        return (best, bestD);
    }
}
