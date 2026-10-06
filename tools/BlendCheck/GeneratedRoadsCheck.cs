using System.Collections.Concurrent;
using System.Diagnostics;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;
using static System.Console;

/// <summary>
/// "--generated-roads [--villages N] [--size N]" (#559): generated roads served through the game's
/// chain (FallbackChunkSource under CachingChunkSource, parallel loads as the streamer makes them,
/// one shared walls/density pair) must equal the road network stage run over the whole block at
/// once with nothing shared, byte for byte. Blocks round the biggest generated villages near the
/// anchor; prints what the stage added and what a tile cost. Non-zero exit on any difference.
/// </summary>
static class GeneratedRoadsCheck
{
    const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N

    public static async Task<int> Run(string[] args)
    {
        int villages = int.Parse(Arg(args, "--villages") ?? "3"), size = int.Parse(Arg(args, "--size") ?? "6");
        var world = new ProceduralWorld(AnchorE, AnchorN);
        var picks = world.VillageCentres()
            .Where(v => Math.Abs(v.E - AnchorE) < 20000 && Math.Abs(v.N - AnchorN) < 20000)
            .OrderByDescending(v => v.Buildings).Take(villages).ToList();

        int tiles = 0, same = 0;
        double servedMs = 0;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in picks)
        {
            var c = TileId.FromLv95(v.E, v.N);
            var block = new HashSet<TileId>();
            for (int de = 0; de < size; de++)
                for (int dn = 0; dn < size; dn++)
                    block.Add(new TileId(c.E - size / 2 + de, c.N - size / 2 + dn));

            // the reference: the whole block at once, every input straight from the generator
            var reference = TileRewriter.Rewrite(block, new TileRewriter.Options(BlockSize: 1, Halo: 1), new TileRewriter.Inputs
            {
                Roads = t =>
                {
                    var (roads, keys) = world.BuildRoadsKeyed(t);
                    return (roads, roads is null ? null : keys.Select(k => (RawRoads.Key?)new RawRoads.Key(k.Line, 0,
                        Math.Round(k.FromM, 2, MidpointRounding.AwayFromZero))).ToArray());
                },
                Grid = t => world.BuildGrid(t, 5),
                Buildings = t => world.BuildBuildings(t),
            }).ToDictionary(t => t.Id);

            // served: the game's chain on an empty region, the block's tiles asked for all at once
            var fallback = new FallbackChunkSource(new EmptySource(), world, AnchorE, AnchorN);
            var cache = new CachingChunkSource(fallback);
            fallback.Neighbours = cache;
            var logs = new ConcurrentBag<string>();
            fallback.Log = logs.Add;
            var clock = Stopwatch.StartNew();
            var served = await Task.WhenAll(block.Select(async t => (Id: t, Tile: await cache.LoadRoadsAsync(t))));
            double ms = clock.Elapsed.TotalMilliseconds;
            servedMs += ms;

            int withRoads = served.Count(s => s.Tile != null);
            WriteLine($"{v.Name} ({v.Buildings} buildings): {block.Count} tiles, {withRoads} with roads, served in {ms:F0} ms");
            foreach (var log in logs) WriteLine($"  log: {log}");
            foreach (var (id, tile) in served)
            {
                if (tile is null && !reference.ContainsKey(id)) continue;
                tiles++;
                if (tile is null || !reference.TryGetValue(id, out var want))
                {
                    WriteLine($"  FAIL {id.E},{id.N}: served {(tile is null ? "nothing" : "a tile")}, reference {(reference.ContainsKey(id) ? "a tile" : "nothing")}");
                    continue;
                }
                Tally(counts, want);
                if (args.Contains("--list"))
                    foreach (var pp in want.PointProps.Where(pp => pp.Type == PointPropType.YieldSign))
                        WriteLine($"  yield sign at {id.MinE + pp.X:F1},{id.MaxN - pp.Z:F1} y {pp.Y:F1}");
                if (args.Contains("--list"))
                    foreach (var sg in want.Signals)
                        WriteLine($"  traffic lights at {id.MinE + sg.X:F1},{id.MaxN - sg.Z:F1} y {sg.Y:F1}");
                if (Encode(tile).AsSpan().SequenceEqual(Encode(want))) same++;
                else
                {
                    var round = Encode(RoadCodec.Decode(new MemoryStream(Encode(want))));
                    WriteLine($"  FAIL {id.E},{id.N}: served tile differs from the block's"
                        + $" (reference survives the codec: {round.AsSpan().SequenceEqual(Encode(want))}, served = reference through the codec: {round.AsSpan().SequenceEqual(Encode(tile))})");
                }
            }
        }

        WriteLine($"\n[generated-roads] {same}/{tiles} tiles identical, {servedMs / Math.Max(1, tiles):F0} ms a road tile (wall clock, parallel)");
        foreach (var (k, n) in counts) WriteLine($"  {k,-26} {n}");
        bool ok = same == tiles && tiles > 0;
        WriteLine($"[generated-roads] RESULT: {(ok ? "ok" : "FAILED")}");
        return ok ? 0 : 1;
    }

    static byte[] Encode(RoadTile t)
    {
        using var ms = new MemoryStream();
        RoadCodec.Encode(t, ms);
        return ms.ToArray();
    }

    static void Tally(SortedDictionary<string, int> c, RoadTile t)
    {
        void Add(string k, int n = 1) => c[k] = c.GetValueOrDefault(k) + n;
        Add("segments", t.Segments.Count);
        Add("junction polygons", t.Junctions.Count);
        foreach (var p in t.PointProps) Add($"point {p.Type}");
        Add("paint", t.Paint.Count);
        Add("signals", t.Signals.Count);
        Add("approaches", t.Approaches.Count);
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>A region with no terrain at all: a fresh clone.</summary>
    sealed class EmptySource : IChunkSource
    {
        public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) => Task.FromResult(new TerrainManifest());
        public Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default) => Task.FromResult<ChunkGrid?>(null);
        public Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default) => Task.FromResult<ChunkGrid?>(null);
        public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) => Task.FromResult<RoadTile?>(null);
        public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) => Task.FromResult<HashSet<int>?>(null);
        public Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default) => Task.FromResult<BuildingTile?>(null);
        public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) => Task.FromResult<List<TreeInstance>?>(null);
        public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) => Task.FromResult<HorizonIndex?>(null);
    }
}
