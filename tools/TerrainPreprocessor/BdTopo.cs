using System.Globalization;
using System.Text.Json;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>One BD TOPO feature: its attributes, and its geometry already in LV95.</summary>
public sealed class BdFeature
{
    public required Dictionary<string, JsonElement> Properties { get; init; }

    /// <summary>Rings for a polygon feature, or a single list for a line. LV95 metres.</summary>
    public required List<List<(double E, double N)>> Rings { get; init; }

    /// <summary>The same rings as they came (WGS84 / ETRS89 degrees): the cube tiler (#1443, G1) converts these rigorously.</summary>
    public required List<List<(double Lat, double Lon)>> LatLonRings { get; init; }

    /// <summary>
    /// <see cref="Rings"/> again from <see cref="LatLonRings"/>, each point on the ground of the built tiles (#1507): they
    /// come at the Plateau's height, which puts a point ~3 cm off a kilometre above or below it.
    /// </summary>
    public void OntoGround(GeoInput geo)
    {
        for (int r = 0; r < Rings.Count; r++)
            for (int k = 0; k < Rings[r].Count; k++)
                Rings[r][k] = geo.ToLv95(LatLonRings[r][k].Lat, LatLonRings[r][k].Lon);
    }

    public string? Text(string key) =>
        Properties.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>
    /// A numeric attribute, accepting the string form too.
    ///
    /// <para>
    /// BD TOPO's GeoJSON is not consistent about this: <c>hauteur</c> and the altitudes come
    /// back as JSON numbers, while <c>position_par_rapport_au_sol</c> — an integer level, 0 for
    /// ground and 1 for a bridge — comes back as the string <c>"1"</c>. Reading only numbers
    /// silently found no bridges at all, which looks exactly like a region that happens to have
    /// none.
    /// </para>
    /// </summary>
    public double? Number(string key)
    {
        if (!Properties.TryGetValue(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null,
            _ => null,
        };
    }

    public bool Flag(string key) =>
        Properties.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Reads BD TOPO® from the IGN Géoplateforme WFS — the French national mapping agency's own
/// service, published under Licence Ouverte 2.0.
///
/// <para>
/// A web service rather than the département download because the areas wanted here are a few
/// square kilometres and the département file is gigabytes. It also means no GDAL, matching the
/// rest of the pipeline: what comes back is GeoJSON, which <c>System.Text.Json</c> reads.
/// </para>
///
/// <para>
/// Coordinates are requested in WGS84 and projected to LV95 on arrival, so French data lands on
/// exactly the same kilometre lattice as the Swiss and the two can share a tile. That is the
/// whole trick to making the border invisible: there is no French coordinate system anywhere
/// downstream, only one lattice with data from two countries on it.
/// </para>
/// </summary>
public sealed class BdTopoClient : IDisposable
{
    /// <summary>The Géoplateforme WFS endpoint.</summary>
    private const string Endpoint = "https://data.geopf.fr/wfs/ows";

    /// <summary>
    /// Features per request. The service caps a single response, and asking for everything at
    /// once silently truncates rather than failing — which would look like a sparse village.
    /// </summary>
    private const int PageSize = 1000;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public int Requests { get; private set; }

    /// <summary>
    /// Fetches every feature of one layer inside a WGS84 bounding box, following pages.
    /// </summary>
    public async Task<List<BdFeature>> FetchAsync(string typeName,
        double minLat, double minLon, double maxLat, double maxLon)
    {
        var all = new List<BdFeature>();

        for (int start = 0; ; start += PageSize)
        {
            string url = Endpoint
                + "?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature"
                + $"&TYPENAMES={typeName}"
                + "&SRSNAME=EPSG:4326"
                + $"&BBOX={F(minLat)},{F(minLon)},{F(maxLat)},{F(maxLon)},urn:ogc:def:crs:EPSG::4326"
                + $"&COUNT={PageSize}&STARTINDEX={start}"
                + "&OUTPUTFORMAT=application/json";

            Requests++;
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("features", out var features)) break;

            int page = 0;
            foreach (var feature in features.EnumerateArray())
            {
                page++;
                var parsed = Parse(feature);
                if (parsed != null) all.Add(parsed);
            }

            if (page < PageSize) break;
        }

        return all;
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static BdFeature? Parse(JsonElement feature)
    {
        if (!feature.TryGetProperty("geometry", out var geometry)
            || geometry.ValueKind != JsonValueKind.Object
            || !geometry.TryGetProperty("type", out var typeElement)
            || !geometry.TryGetProperty("coordinates", out var coordinates)) return null;

        var rings = new List<List<(double, double)>>();
        var latLon = new List<List<(double, double)>>();
        switch (typeElement.GetString())
        {
            case "Polygon":
                ReadPolygon(coordinates, rings, latLon);
                break;
            case "MultiPolygon":
                foreach (var polygon in coordinates.EnumerateArray()) ReadPolygon(polygon, rings, latLon);
                break;
            case "LineString":
                ReadRing(coordinates, rings, latLon);
                break;
            case "MultiLineString":
                foreach (var line in coordinates.EnumerateArray()) ReadRing(line, rings, latLon);
                break;
            default:
                return null;
        }

        if (rings.Count == 0) return null;

        var properties = new Dictionary<string, JsonElement>();
        if (feature.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject()) properties[p.Name] = p.Value.Clone();

        return new BdFeature { Properties = properties, Rings = rings, LatLonRings = latLon };
    }

    private static void ReadPolygon(JsonElement polygon, List<List<(double, double)>> into, List<List<(double, double)>> latLon)
    {
        foreach (var ring in polygon.EnumerateArray()) ReadRing(ring, into, latLon);
    }

    private static void ReadRing(JsonElement ring, List<List<(double, double)>> into, List<List<(double, double)>> latLon)
    {
        var points = new List<(double, double)>();
        var raw = new List<(double, double)>();
        foreach (var position in ring.EnumerateArray())
        {
            // GeoJSON is x,y = lon,lat — the opposite order to the WFS bbox parameter, which
            // is a genuine trap: swap them and everything lands in the Indian Ocean.
            double lon = position[0].GetDouble();
            double lat = position[1].GetDouble();
            // at the Plateau's height: FranceStage puts them on its tiles' ground (BdFeature.OntoGround)
            points.Add(SwissGeodesy.ToLv95(lat, lon));
            raw.Add((lat, lon));
        }
        into.Add(points);
        latLon.Add(raw);
    }

    public void Dispose() => _http.Dispose();
}
