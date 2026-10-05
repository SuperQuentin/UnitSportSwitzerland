using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace UnitSport.Map;

/// <summary>
/// Just enough of swisstopo's STAC API (v0.9, the one tools/swiss_data.py talks to) for the
/// bake: list a collection over a bbox and ask a file's size. Downloading itself stays in
/// swiss_data.py.
/// </summary>
public static class Stac
{
    public const string Base = "https://data.geo.admin.ch/api/stac/v0.9";

    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // opendata.swiss 403s requests without one (see swiss_data.py)
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UnitSportSwitzerland-MapSetup");
        return client;
    }

    public sealed record Asset(string Key, string Href);
    /// <param name="Ring">The footprint polygon's outer ring as (lon, lat) pairs, when the item has one.</param>
    public sealed record Item(string Id, double[] Bbox, string Datetime, List<Asset> Assets, List<(double Lon, double Lat)> Ring);

    /// <summary>
    /// Every item of a collection inside a WGS84 bbox. A single query pages 100 items at a time
    /// behind a cursor, so a nationwide one is hundreds of sequential round trips; the bbox is
    /// split into cells listed concurrently and duplicates (items on a cell edge) dropped by id.
    /// </summary>
    public static async Task<List<Item>> ListAsync(string collection, (double W, double S, double E, double N)? bbox,
        double cellDeg = 0.2, int parallel = 8, Action<int>? progress = null)
    {
        var cells = new List<(double, double, double, double)?>();
        if (bbox is { } b)
        {
            int nx = Math.Max(1, (int)Math.Round((b.E - b.W) / cellDeg));
            int ny = Math.Max(1, (int)Math.Round((b.N - b.S) / cellDeg));
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    cells.Add((b.W + (b.E - b.W) * i / nx, b.S + (b.N - b.S) * j / ny,
                               b.W + (b.E - b.W) * (i + 1) / nx, b.S + (b.N - b.S) * (j + 1) / ny));
        }
        else cells.Add(null);

        var items = new ConcurrentDictionary<string, Item>();
        await Parallel.ForEachAsync(cells, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (cell, ct) =>
        {
            string? url = $"{Base}/collections/{collection}/items?limit=100";
            if (cell is { } c)
                url += "&bbox=" + string.Join(",", new[] { c.Item1, c.Item2, c.Item3, c.Item4 }
                    .Select(v => v.ToString("F6", CultureInfo.InvariantCulture)));
            while (url != null)
            {
                using var doc = JsonDocument.Parse(await GetStringAsync(url, ct));
                foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
                {
                    var item = ParseItem(f);
                    if (items.TryAdd(item.Id, item)) progress?.Invoke(items.Count);
                }
                url = null;
                foreach (var link in doc.RootElement.GetProperty("links").EnumerateArray())
                    if (link.GetProperty("rel").GetString() == "next")
                        url = link.GetProperty("href").GetString();
            }
        });
        return items.Values.ToList();
    }

    private static Item ParseItem(JsonElement f)
    {
        double[] bbox = f.TryGetProperty("bbox", out var b) ? b.EnumerateArray().Select(v => v.GetDouble()).ToArray() : [0, 0, 0, 0];
        string dt = f.TryGetProperty("properties", out var p) && p.TryGetProperty("datetime", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()! : "";
        var assets = new List<Asset>();
        if (f.TryGetProperty("assets", out var a))
            foreach (var prop in a.EnumerateObject())
                assets.Add(new Asset(prop.Name, prop.Value.GetProperty("href").GetString()!));
        var ring = new List<(double, double)>();
        if (f.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object
            && g.TryGetProperty("coordinates", out var coords) && coords.GetArrayLength() > 0
            && g.GetProperty("type").GetString() == "Polygon")
            foreach (var pt in coords[0].EnumerateArray())
                ring.Add((pt[0].GetDouble(), pt[1].GetDouble()));
        return new Item(f.GetProperty("id").GetString()!, bbox, dt, assets, ring);
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await Http.GetStringAsync(url, ct); }
            catch (Exception) when (attempt < 4)
            {
                await Task.Delay(500 * attempt * attempt, ct);
            }
        }
    }

    /// <summary>Content-Length of a URL, by HEAD; null when the server will not say.</summary>
    public static async Task<long?> SizeAsync(string url, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return null;
                return resp.Content.Headers.ContentLength;
            }
            catch (Exception) when (attempt < 4)
            {
                await Task.Delay(500 * attempt * attempt, ct);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Sizes of many URLs, <paramref name="parallel"/> HEADs in flight (the same courtesy cap as swiss_data.py).</summary>
    public static async Task<Dictionary<string, long>> SizesAsync(IEnumerable<string> urls, int parallel = 32,
        Action<int>? progress = null)
    {
        var result = new ConcurrentDictionary<string, long>();
        int done = 0;
        await Parallel.ForEachAsync(urls, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (url, ct) =>
        {
            if (await SizeAsync(url, ct) is { } size) result[url] = size;
            progress?.Invoke(Interlocked.Increment(ref done));
        });
        return new Dictionary<string, long>(result);
    }

    public static async Task DownloadAsync(string url, string dest, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var tmp = dest + ".part";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs, ct);
        File.Move(tmp, dest, overwrite: true);
    }
}
