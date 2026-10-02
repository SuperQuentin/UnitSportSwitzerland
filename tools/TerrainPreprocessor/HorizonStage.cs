using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Decimates every built tile to the 100 m horizon lattice and packs the region into one
/// <c>horizon.bin</c>. Reads the 5 KB coarse companion where it exists (its stride 10 divides
/// the horizon's 100, so the samples are the same bits), the full tile otherwise.
/// </summary>
public static class HorizonStage
{
    public static int Run(string outDir, int jobs)
    {
        var terrFiles = Directory.GetFiles(outDir, "chunk_*.terr");
        if (terrFiles.Length == 0)
        {
            Console.Error.WriteLine($"No .terr files in {outDir}");
            return 2;
        }

        var clock = Stopwatch.StartNew();
        var tiles = new System.Collections.Concurrent.ConcurrentDictionary<TileId, ushort[]>();
        var water = new System.Collections.Concurrent.ConcurrentDictionary<TileId, ushort[]>();
        long readBytes = 0;
        int fromCoarse = 0;

        Parallel.ForEach(terrFiles, new ParallelOptions { MaxDegreeOfParallelism = jobs }, path =>
        {
            string coarsePath = Path.ChangeExtension(path, ".terrc");
            string src = File.Exists(coarsePath) ? coarsePath : path;
            ChunkGrid grid;
            using (var fs = File.OpenRead(src)) grid = ChunkCodec.Decode(fs);
            Interlocked.Add(ref readBytes, new FileInfo(src).Length);
            if (src == coarsePath) Interlocked.Increment(ref fromCoarse);

            var samples = HorizonFormat.Extract(grid);

            // Construct then verify: the horizon claims to be the tile's own vertices one in a
            // hundred, so check it against the grid it was cut from, sample by sample.
            for (int r = 0; r < HorizonFormat.SamplesPerSide; r++)
                for (int c = 0; c < HorizonFormat.SamplesPerSide; c++)
                    if (samples[r * HorizonFormat.SamplesPerSide + c]
                        != grid.HeightAt(c * HorizonFormat.Stride, r * HorizonFormat.Stride))
                        throw new InvalidDataException($"{grid.Id}: horizon sample differs at ({c},{r})");

            tiles[grid.Id] = samples;

            // the still water at the same samples (#298): the ground under a lake is its bed
            string waterPath = Path.Combine(outDir, WaterFormat.FileName(grid.Id));
            if (File.Exists(waterPath))
            {
                WaterGrid wg;
                using (var fs = File.OpenRead(waterPath)) wg = WaterFormat.Decode(fs);
                if (HorizonFormat.ExtractWater(wg) is { } levels) water[grid.Id] = levels;
            }
        });

        string outPath = Path.Combine(outDir, HorizonFormat.FileName);
        using (var fs = File.Create(outPath))
            HorizonFormat.Encode(tiles, fs, water);

        // and that what landed on disk is what we checked
        HorizonIndex reread;
        using (var fs = File.OpenRead(outPath)) reread = HorizonFormat.Decode(fs);
        if (reread.Count != tiles.Count)
            throw new InvalidDataException($"horizon.bin holds {reread.Count} tiles, expected {tiles.Count}");
        foreach (var (id, samples) in tiles)
            if (!reread.TryGet(id, out var back) || !back.AsSpan().SequenceEqual(samples))
                throw new InvalidDataException($"{id}: horizon tile did not round-trip");
        foreach (var (id, levels) in water)
            if (!reread.TryGetWater(id, out var back) || !back.AsSpan().SequenceEqual(levels))
                throw new InvalidDataException($"{id}: horizon water did not round-trip");

        long wrote = new FileInfo(outPath).Length;
        Console.WriteLine($"Horizon pass: {tiles.Count} tiles ({fromCoarse} from .terrc) in "
            + $"{clock.Elapsed.TotalSeconds:F1}s, read {readBytes / 1048576.0:F1} MB -> "
            + $"wrote {wrote / 1024.0:F0} KB ({HorizonFormat.SpacingM} m lattice, {water.Count} tiles with water)");
        return 0;
    }
}
