using System.Diagnostics;
using System.Text.Json;
using UnitSport.Terrain.Format;

namespace UnitSport.Map;

/// <summary>
/// The rates every time estimate is built from. The defaults are the figures measured on the
/// development machine (README "Importing a large region", features_ch.log, roadgen_ch.log);
/// every real run then folds what it measured back in, so estimates converge on this
/// machine's disk, cores and connection instead of staying the author's.
/// </summary>
public sealed class Stats
{
    /// <summary>swiss_data.py throughput with its 8 parallel downloads. 0 = never measured.</summary>
    public double DownloadBytesPerSec { get; set; }
    public DateTime? DownloadProbedAt { get; set; }

    /// <summary>Terrain build cost per tile in CPU-core seconds: 40 s per 1000 tiles on 12 cores.</summary>
    public double TerrainCoreSecPerTile { get; set; } = 0.48;
    /// <summary>Roads + cover + trees + buildings per tile, core seconds (≈55 s per 400-tile batch on 12 cores).</summary>
    public double FeaturesCoreSecPerTile { get; set; } = 1.8;
    /// <summary>Junction rewrite: 1,067 s for 43.5k tiles.</summary>
    public double RoadGenSecPerTile { get; set; } = 0.025;
    /// <summary>Unzipping TLM/GWR (deflate, one stream), bytes written per second.</summary>
    public double ExtractBytesPerSec { get; set; } = 180e6;
    /// <summary>GDAL reading swissBUILDINGS3D zips, bytes of zip per second.</summary>
    public double GdalBytesPerSec { get; set; } = 25e6;
    /// <summary>Starting a tool and opening its inputs (route keys, the 10 GB TLM), seconds.</summary>
    public double ToolStartSec { get; set; } = 8;

    public const double DefaultDownloadBytesPerSec = 40e6;
    public double EffectiveDownload => DownloadBytesPerSec > 0 ? DownloadBytesPerSec : DefaultDownloadBytesPerSec;

    public static Stats Load(Paths paths)
    {
        try
        {
            if (File.Exists(paths.StatsFile))
                return JsonSerializer.Deserialize<Stats>(File.ReadAllText(paths.StatsFile)) ?? new Stats();
        }
        catch (Exception) { /* a corrupt stats file just means default rates */ }
        return new Stats();
    }

    public void Save(Paths paths)
    {
        Directory.CreateDirectory(paths.Temp);
        File.WriteAllText(paths.StatsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Half old, half new: one unusual run moves the estimate, it does not replace it.</summary>
    public static double Blend(double old, double measured) =>
        old <= 0 ? measured : double.IsFinite(measured) && measured > 0 ? old * 0.5 + measured * 0.5 : old;

    /// <summary>
    /// Measures the connection with the same shape of traffic swiss_data.py makes: four tiles
    /// fetched at once for a few seconds. One stream alone underestimates it several times over.
    /// </summary>
    public async Task ProbeDownloadAsync(CountryData country, TimeSpan duration)
    {
        var sample = country.AllTiles()
            .Where(t => country.Year(t) > 0)
            .OrderBy(t => Math.Abs(t.E - 2600) + Math.Abs(t.N - 1200))
            .Take(4)
            .ToList();
        if (sample.Count == 0) return;

        long bytes = 0;
        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(duration);
        await Task.WhenAll(sample.Select(async t =>
        {
            int y = country.Year(t);
            string url = $"https://data.geo.admin.ch/ch.swisstopo.swissalti3d/swissalti3d_{y}_{t.E}-{t.N}/"
                         + $"swissalti3d_{y}_{t.E}-{t.N}_0.5_2056_5728.xyz.zip";
            try
            {
                using var resp = await Stac.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await s.ReadAsync(buffer, cts.Token)) > 0) Interlocked.Add(ref bytes, n);
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
        }));
        double seconds = clock.Elapsed.TotalSeconds;
        if (bytes > 1_000_000 && seconds > 0.5)
        {
            DownloadBytesPerSec = bytes / seconds;
            DownloadProbedAt = DateTime.UtcNow;
        }
    }
}
