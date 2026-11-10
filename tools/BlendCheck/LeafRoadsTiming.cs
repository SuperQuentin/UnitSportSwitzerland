using System.Diagnostics;
using System.Security.Cryptography;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using static System.Console;

/// <summary>
/// "--leaf-roads-time [--at E,N] [--side 3] [--digest file] [--parallel N] [--flat]" (#1699): the time a generated fill leaf's roads take
/// (<see cref="GeneratedLeafRoads"/>) on a block of leaves round a point (default the brsquad village), and a digest
/// of every leaf's finished roads (their codec bytes), written to a file or compared with one: the speed-ups keep
/// every leaf's roads byte-identical.
/// </summary>
static class LeafRoadsTiming
{
    public static int Run(string[] args)
    {
        var at = (Arg(args, "--at") ?? "2642075,1129346").Split(',');
        double e0 = double.Parse(at[0]), n0 = double.Parse(at[1]);
        int side = int.Parse(Arg(args, "--side") ?? "3");
        var world = new ProceduralWorld(2583250, 1113250);   // SpawnPoint.DefaultLv95E/N, as the game builds it
        if (args.Contains("--flat")) return Flat(world, e0, n0, side);
        var roads = new GeneratedLeafRoads(world);
        var leaves = new GeneratedLeaves(world);
        var (lat, lon) = Lv95Source.Instance.ToLatLon(e0, n0);
        var centre = LeafFrames.LeafAt(lat, lon);
        var block = new List<CubeKey>();
        for (int dy = -(side / 2); dy <= side / 2; dy++)
            for (int dx = -(side / 2); dx <= side / 2; dx++)
                if (centre.Neighbour(dx, dy) is { } k) block.Add(k);
        var lines = new List<string>();
        var all = Stopwatch.StartNew();
        // --parallel N: N leaves at once, as the earth world's workers make them (the digests must not change)
        if (Arg(args, "--parallel") is { } p && int.Parse(p) > 1)
        {
            var digests = new string[block.Count];
            Parallel.For(0, block.Count, new ParallelOptions { MaxDegreeOfParallelism = int.Parse(p) }, i =>
            {
                leaves.Build(block[i]);
                digests[i] = Digest(roads.Build(block[i]));
            });
            for (int i = 0; i < block.Count; i++) lines.Add($"{block[i]} {digests[i]}");
            WriteLine($"{block.Count} leaves on {p} workers in {all.Elapsed.TotalSeconds:F1} s");
            return Compare(args, lines);
        }
        foreach (var k in block)
        {
            var c = Stopwatch.StartNew();
            leaves.Build(k);
            double content = c.Elapsed.TotalSeconds;
            c.Restart();
            var t = roads.Build(k);
            double r = c.Elapsed.TotalSeconds;
            string digest = Digest(t);
            WriteLine($"  {k}: content {content:F2} s, roads {r:F2} s, {t?.Segments.Count ?? 0} segments, {digest}");
            lines.Add($"{k} {digest}");
        }
        WriteLine($"{block.Count} leaves in {all.Elapsed.TotalSeconds:F1} s");
        return Compare(args, lines);
    }

    static string Digest(RoadTile? t)
    {
        if (t is null) return "none";
        using var ms = new MemoryStream();
        RoadCodec.Encode(t, ms);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16];
    }

    static int Compare(string[] args, List<string> lines)
    {
        if (Arg(args, "--digest") is { } file)
        {
            if (!File.Exists(file)) { File.WriteAllLines(file, lines); WriteLine($"digest written to {file}"); return 0; }
            var old = File.ReadAllLines(file);
            int diff = old.Zip(lines).Count(p => p.First != p.Second) + Math.Abs(old.Length - lines.Count);
            WriteLine($"[leafroadstime] RESULT: {(diff == 0 ? "ok" : $"FAILED {diff} leaves differ from {file}")}");
            return diff == 0 ? 0 : 1;
        }
        return 0;
    }

    /// <summary>
    /// "--flat": the flat world's generated tiles over the same ground (the LV95 tiles under the leaf block), one at a
    /// time through the game's chain (FallbackChunkSource on an empty region, its fill round the point), as the leaves.
    /// </summary>
    static int Flat(ProceduralWorld world, double e0, double n0, int side)
    {
        var (lat, lon) = Lv95Source.Instance.ToLatLon(e0, n0);
        var centre = LeafFrames.LeafAt(lat, lon);
        var tiles = new HashSet<TileId>();
        for (int dy = -(side / 2); dy <= side / 2; dy++)
            for (int dx = -(side / 2); dx <= side / 2; dx++)
                if (centre.Neighbour(dx, dy) is { } k)
                    foreach (var t in GeneratedLeaves.TilesUnder(new SourceOnLeaf(LeafFrames.Swiss, k, Lv95Source.Instance))) tiles.Add(t);
        var source = new FallbackChunkSource(EmptyChunkSource.Instance, world, e0, n0);
        var all = Stopwatch.StartNew();
        foreach (var t in tiles.OrderBy(t => t.N).ThenBy(t => t.E))
        {
            var c = Stopwatch.StartNew();
            source.LoadChunkAsync(t).GetAwaiter().GetResult();
            double content = c.Elapsed.TotalSeconds;
            c.Restart();
            var r = source.LoadRoadsAsync(t).GetAwaiter().GetResult();
            WriteLine($"  {t}: grid {content:F2} s, roads {c.Elapsed.TotalSeconds:F2} s, {r?.Segments.Count ?? 0} segments");
        }
        WriteLine($"{tiles.Count} flat tiles (1 km) in {all.Elapsed.TotalSeconds:F1} s");
        return 0;
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
