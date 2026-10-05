using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--photos</c>: the SWISSIMAGE aerial photo of every tile in the manifest, for the
/// realistic styles' terrain (docs/notes/styles/swissimage.md). One 512x512 JPEG per 1 km tile,
/// about 2 m a pixel and ~120 KB, as <c>photo_E_N.jpg</c> next to the tile's other files.
///
/// <para>
/// From swisstopo's WMS (layer <c>ch.swisstopo.swissimage</c>, EPSG:2056), which renders any box
/// straight to JPEG: no GeoTIFF decoding, no GDAL. Open government data, free use with the
/// attribution "© swisstopo". Tiles outside Switzerland come back blank and are not written.
/// Existing photos are kept unless <c>--force</c>; <c>--io-jobs</c> requests at a time.
/// </para>
/// </summary>
public static class PhotoStage
{
    public const int Size = 512;

    public static string FileName(TileId id) => $"photo_{id}.jpg";

    private const string Wms = "https://wms.geo.admin.ch/?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap"
        + "&LAYERS=ch.swisstopo.swissimage&STYLES=&CRS=EPSG:2056&FORMAT=image/jpeg";

    /// <summary>A blank (out of coverage) 512x512 JPEG is a few KB; a real one is ~100 KB.</summary>
    private const int BlankBelowBytes = 12_000;

    public static async Task<int> Run(string outDir, IEnumerable<TileId> tiles, int ioJobs, bool force,
        CancellationToken ct = default)
    {
        var todo = tiles.Where(id => force || !File.Exists(Path.Combine(outDir, FileName(id)))).ToList();
        Console.WriteLine($"[photos] {todo.Count} tile(s) to fetch into {outDir}");
        if (todo.Count == 0) return 0;

        var clock = Stopwatch.StartNew();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UnitSportSwitzerland-TerrainPreprocessor/1.0");
        int written = 0, blank = 0, failed = 0;
        long bytes = 0;

        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ioJobs), CancellationToken = ct },
            async (id, taskCt) =>
            {
                string url = FormattableString.Invariant(
                    $"{Wms}&BBOX={id.MinE},{id.MinN},{id.MinE + 1000},{id.MaxN}&WIDTH={Size}&HEIGHT={Size}");
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        var data = await http.GetByteArrayAsync(url, taskCt);
                        // a JPEG starts FF D8; an error comes back as XML
                        if (data.Length < 2 || data[0] != 0xFF || data[1] != 0xD8)
                            throw new InvalidDataException($"not a JPEG ({data.Length} bytes)");
                        if (data.Length < BlankBelowBytes)
                        {
                            Interlocked.Increment(ref blank);
                            return;
                        }
                        string path = Path.Combine(outDir, FileName(id));
                        await File.WriteAllBytesAsync(path + ".tmp", data, taskCt);
                        File.Move(path + ".tmp", path, overwrite: true);
                        Interlocked.Increment(ref written);
                        Interlocked.Add(ref bytes, data.Length);
                        return;
                    }
                    catch (Exception e) when (attempt < 3 && e is not OperationCanceledException)
                    {
                        await Task.Delay(1000 * attempt, taskCt);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"[photos] {id}: {e.Message}");
                        Interlocked.Increment(ref failed);
                        return;
                    }
                }
            });

        Console.WriteLine($"[photos] {written} written ({bytes / 1024 / 1024.0:F1} MB), {blank} outside "
            + $"SWISSIMAGE, {failed} failed, in {clock.Elapsed.TotalSeconds:F0} s");
        return failed == 0 ? 0 : 1;
    }
}
