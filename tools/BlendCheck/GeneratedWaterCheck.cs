using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using static System.Console;

/// <summary>
/// "--generated-water [--radius N]" (#572): the generated rivers' water layer over the tiles round
/// the anchor. Rivers must be drawn where their channel is (coverage of the samples well inside a
/// wet, dug channel) and never stand above the ground (floating samples outside lakes).
/// </summary>
static class GeneratedWaterCheck
{
    const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N

    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--radius");
        int radius = i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : 6;
        var world = new ProceduralWorld(AnchorE, AnchorN);
        var c0 = TileId.FromLv95(AnchorE, AnchorN);
        var tiles = new List<TileId>();
        for (int de = -radius; de <= radius; de++)
            for (int dn = -radius; dn <= radius; dn++)
                tiles.Add(new TileId(c0.E + de, c0.N + dn));

        var results = new (TileId Id, int Expected, int Drawn, int Undug, int Floating, (double E, double N, double Above) Worst)[tiles.Count];
        Parallel.For(0, tiles.Count, t =>
        {
            var a = world.WaterAudit(tiles[t]);
            results[t] = (tiles[t], a.Dug, a.Drawn, a.Undug, a.Floating, a.Worst);
        });

        long expected = results.Sum(r => (long)r.Expected), drawn = results.Sum(r => (long)r.Drawn);
        long floating = results.Sum(r => (long)r.Floating), undug = results.Sum(r => (long)r.Undug);
        double coverage = expected == 0 ? 1 : (double)drawn / expected;
        WriteLine($"[generated-water] {tiles.Count} tiles: dug river samples drawn {drawn}/{expected} ({coverage:P1}); undug {undug}, of which drawn floating {floating}");
        foreach (var r in results.Where(r => r.Floating > 0).OrderByDescending(r => r.Worst.Above).Take(8))
            WriteLine($"  floating: {r.Floating} samples on {r.Id.E},{r.Id.N}, worst {r.Worst.Above:F1} m above the ground at {r.Worst.E:F0},{r.Worst.N:F0}");
        foreach (var r in results.Where(r => r.Expected > 200).OrderBy(r => (double)r.Drawn / r.Expected).Take(5))
            WriteLine($"  patchy: {r.Id.E},{r.Id.N} drawn {r.Drawn}/{r.Expected}");
        // what the cover costs a river tile (the exact channel test runs only in a band along it)
        var riverTiles = results.Where(r => r.Expected > 0).Select(r => r.Id).Take(40).ToList();
        var ms = new List<double>();
        foreach (var t in riverTiles)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            world.BuildCover(t);
            ms.Add(clock.Elapsed.TotalMilliseconds);
        }
        ms.Sort();
        if (ms.Count > 0) WriteLine($"  cover of a river tile: median {ms[ms.Count / 2]:F1} ms, max {ms[^1]:F1} ms ({ms.Count} tiles, one thread)");
        bool ok = floating == 0 && coverage >= 0.995;
        WriteLine($"[generated-water] RESULT: {(ok ? "ok" : "FAILED")}");
        return ok ? 0 : 1;
    }
}
