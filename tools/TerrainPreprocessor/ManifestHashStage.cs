using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <c>--manifest-hash</c> (#924): every manifest tile's content hash (<see cref="TileFingerprint"/>)
/// and the region-wide files' into <c>manifest.json</c>, nothing else touched: the tiles are only
/// read. Also run after every preprocessor pass that writes tiles, so a fresh build carries them.
///
/// <para>
/// Incremental: each file's SHA-256 is kept in <c>&lt;temp&gt;/tile_hashes.tsv</c> with its size and
/// modification time, and read again only when either changed. The first pass over a whole map reads
/// every byte (10 GB, 2,698 tiles: about a minute from an external SSD); later ones only stat the files.
/// </para>
/// </summary>
public static class ManifestHashStage
{
    public const string CacheFile = "tile_hashes.tsv";

    /// <summary>Hashes the tiles of <paramref name="outDir"/>'s manifest; 0 on success, 2 without a manifest.</summary>
    public static int Run(string outDir, string tempDir, int ioJobs, CancellationToken ct = default)
    {
        string manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"[hash] no manifest.json in {outDir}");
            return 2;
        }
        var clock = Stopwatch.StartNew();
        var manifest = TerrainManifest.FromJson(File.ReadAllText(manifestPath));
        var wanted = manifest.Tiles.Select(t => t.Id).ToHashSet();

        // every file of a manifest tile, and the region-wide ones
        var files = new List<(string Name, TileId? Tile)>();
        foreach (var path in Directory.EnumerateFiles(outDir))
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            if (TileFingerprint.TryTileOf(name, out var id)) { if (wanted.Contains(id)) files.Add((name, id)); }
            else if (TileFingerprint.SharedFiles.Contains(name)) files.Add((name, null));
        }

        Directory.CreateDirectory(tempDir);
        string cachePath = Path.Combine(tempDir, CacheFile);
        var cache = ReadCache(cachePath);
        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, long Ticks, string Hash)>();
        long readBytes = 0;
        int read = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ioJobs), CancellationToken = ct }, f =>
        {
            var info = new FileInfo(Path.Combine(outDir, f.Name));
            long ticks = info.LastWriteTimeUtc.Ticks;
            if (cache.TryGetValue(f.Name, out var had) && had.Size == info.Length && had.Ticks == ticks)
            {
                hashes[f.Name] = had;
                return;
            }
            hashes[f.Name] = (info.Length, ticks, TileFingerprint.OfFile(info.FullName));
            Interlocked.Add(ref readBytes, info.Length);
            int n = Interlocked.Increment(ref read);
            if (n % 2000 == 0) Console.WriteLine($"  [hash] {n} files read");
        });

        var byTile = files.Where(f => f.Tile != null).GroupBy(f => f.Tile!.Value)
            .ToDictionary(g => g.Key, g => TileFingerprint.OfTile(g.Select(f => (f.Name, hashes[f.Name].Hash))));
        int changed = 0;
        foreach (var t in manifest.Tiles)
        {
            string? h = byTile.TryGetValue(t.Id, out var v) ? v : null;
            if (h != t.Hash) changed++;
            t.Hash = h;
        }
        var shared = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files.Where(f => f.Tile == null)) shared[f.Name] = hashes[f.Name].Hash[..TileFingerprint.Digits];
        manifest.Shared = shared.Count > 0 ? shared : null;
        manifest.HashAlgorithm = TileFingerprint.Algorithm;
        manifest.ManifestVersion = TerrainManifest.HashedVersion;

        AtomicFile.Write(manifestPath, s => { using var w = new StreamWriter(s); w.Write(manifest.ToJson()); });
        WriteCache(cachePath, hashes);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[hash] {byTile.Count} of {manifest.Tiles.Count} tiles fingerprinted ({changed} changed), {files.Count} files, read {read} "
            + $"({readBytes / 1048576.0:F0} MB) in {clock.Elapsed.TotalSeconds:F1}s"));
        return 0;
    }

    private static Dictionary<string, (long Size, long Ticks, string Hash)> ReadCache(string path)
    {
        var cache = new Dictionary<string, (long, long, string)>(StringComparer.Ordinal);
        if (!File.Exists(path)) return cache;
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split('\t');
            if (p.Length == 4 && long.TryParse(p[1], CultureInfo.InvariantCulture, out long size)
                && long.TryParse(p[2], CultureInfo.InvariantCulture, out long ticks))
                cache[p[0]] = (size, ticks, p[3]);
        }
        return cache;
    }

    private static void WriteCache(string path, IDictionary<string, (long Size, long Ticks, string Hash)> hashes)
    {
        AtomicFile.Write(path, s =>
        {
            using var w = new StreamWriter(s);
            foreach (var (name, h) in hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)))
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}\t{h.Size}\t{h.Ticks}\t{h.Hash}"));
        });
    }
}
