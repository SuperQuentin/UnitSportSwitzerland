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

        int tiles = 0, same = 0, seamEnds = 0, seamOpen = 0, seamApart = 0;
        double servedMs = 0;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in picks)
        {
            var c = TileId.FromLv95(v.E, v.N);
            var block = new HashSet<TileId>();
            for (int de = 0; de < size; de++)
                for (int dn = 0; dn < size; dn++)
                    block.Add(new TileId(c.E - size / 2 + de, c.N - size / 2 + dn));

            // the reference: the whole block at once, every input straight from the generator. Since #1660 the stage plans
            // the set as ONE block over whole lines, so a tile served alone differs where a line's plan reads past its halo:
            // --alone and the seam count below say how (#1541)
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

            // --alone: each tile rewritten by itself from the reference's own inputs, to tell the stage from the source
            if (args.Contains("--alone"))
            {
                int alike = 0, alone = 0;
                foreach (var t in block)
                {
                    if (!reference.TryGetValue(t, out var want)) continue;
                    var one = TileRewriter.Rewrite([t], new TileRewriter.Options(BlockSize: 1, Halo: 1), new TileRewriter.Inputs
                    {
                        Roads = u =>
                        {
                            var (roads, keys) = world.BuildRoadsKeyed(u);
                            return (roads, roads is null ? null : keys.Select(k => (RawRoads.Key?)new RawRoads.Key(k.Line, 0,
                                Math.Round(k.FromM, 2, MidpointRounding.AwayFromZero))).ToArray());
                        },
                        Grid = u => world.BuildGrid(u, 5),
                        Buildings = u => world.BuildBuildings(u),
                    });
                    alone++;
                    if (one.Count == 1 && Encode(one[0]).AsSpan().SequenceEqual(Encode(want))) alike++;
                    else WriteLine($"  alone {t.E},{t.N}: " + (one.Count == 1 ? Why(want, one[0]) : $"{one.Count} tiles"));
                }
                WriteLine($"  alone: {alike}/{alone} tiles as the block's");
            }

            // served: the game's chain on an empty region, the block's tiles asked for all at once
            var logs = new ConcurrentBag<string>();
            var cache = new FallbackChunkSource(EmptyChunkSource.Instance, world, AnchorE, AnchorN) { Log = logs.Add }.Cached();
            var clock = Stopwatch.StartNew();
            var served = await Task.WhenAll(block.Select(async t => (Id: t, Tile: await cache.LoadRoadsAsync(t))));
            double ms = clock.Elapsed.TotalMilliseconds;
            servedMs += ms;

            int withRoads = served.Count(s => s.Tile != null);
            WriteLine($"{v.Name} ({v.Buildings} buildings): {block.Count} tiles, {withRoads} with roads, served in {ms:F0} ms");
            foreach (var log in logs) WriteLine($"  log: {log}");
            // the seams between served tiles: every centreline end on a shared tile edge meets its neighbour's
            var (ends, open, apart) = Seams(served.Where(s => s.Tile != null).ToDictionary(s => s.Id, s => s.Tile!));
            seamEnds += ends;
            seamOpen += open;
            seamApart += apart;
            var (refEnds, refOpen, _) = Seams(reference);
            WriteLine($"  seams: {ends - open}/{ends} centreline ends on a shared edge meet the neighbour's, {apart} apart across (the block's own: {refEnds - refOpen}/{refEnds})");
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
                    if (args.Contains("--why")) WriteLine("    " + Why(RoadCodec.Decode(new MemoryStream(Encode(want))), tile));
                }
            }
        }

        WriteLine($"\n[generated-roads] {same}/{tiles} tiles identical, {servedMs / Math.Max(1, tiles):F0} ms a road tile (wall clock, parallel)");
        WriteLine($"[generated-roads] seams: {seamEnds - seamOpen}/{seamEnds} centreline ends on a shared tile edge meet their neighbour's (1 cm, 2 cm up), {seamApart} apart across");
        if (Gaps.Count > 0)
        {
            var g = Gaps.Order().ToList();
            WriteLine($"[generated-roads] the ends apart: median {g[g.Count / 2]:F2} m, worst {g[^1]:F2} m over {g.Count} ends");
        }
        foreach (var (k, n) in counts) WriteLine($"  {k,-26} {n}");
        bool ok = same == tiles && tiles > 0;
        WriteLine($"[generated-roads] RESULT: {(ok ? "ok" : "FAILED")}");
        return ok ? 0 : 1;
    }

    /// <summary>--why: how a served tile differs from the block's: the counts first, then the segments' largest shift by axis.</summary>
    static string Why(RoadTile want, RoadTile got)
    {
        var diffs = new List<string>();
        void Count(string what, int w, int g) { if (w != g) diffs.Add($"{what} {w} vs {g}"); }
        Count("segments", want.Segments.Count, got.Segments.Count);
        Count("junctions", want.Junctions.Count, got.Junctions.Count);
        Count("paint", want.Paint.Count, got.Paint.Count);
        Count("point props", want.PointProps.Count, got.PointProps.Count);
        Count("linear props", want.LinearProps.Count, got.LinearProps.Count);
        Count("area props", want.AreaProps.Count, got.AreaProps.Count);
        Count("signals", want.Signals.Count, got.Signals.Count);
        Count("approaches", want.Approaches.Count, got.Approaches.Count);
        Count("transitions", want.Transitions.Count, got.Transitions.Count);
        Count("parking", want.Parking.Count, got.Parking.Count);
        Count("junction paths", want.JunctionPaths.Count, got.JunctionPaths.Count);
        if (want.Flags != got.Flags) diffs.Add($"flags {want.Flags} vs {got.Flags}");
        if (want.Segments.Count == got.Segments.Count)
        {
            float dx = 0, dy = 0, dz = 0;
            int pts = 0, attrs = 0, widths = 0;
            for (int i = 0; i < want.Segments.Count; i++)
            {
                var (a, b) = (want.Segments[i], got.Segments[i]);
                if (a.Width != b.Width || a.Class != b.Class || a.Flags != b.Flags) widths++;
                if (!a.Attributes.Equals(b.Attributes)) attrs++;
                if (a.Points.Length != b.Points.Length) { pts++; continue; }
                for (int k = 0; k < a.Points.Length; k += 3)
                {
                    dx = Math.Max(dx, Math.Abs(a.Points[k] - b.Points[k]));
                    dy = Math.Max(dy, Math.Abs(a.Points[k + 1] - b.Points[k + 1]));
                    dz = Math.Max(dz, Math.Abs(a.Points[k + 2] - b.Points[k + 2]));
                }
            }
            diffs.Add($"segment points: {pts} differ in count, largest shift x {dx:F3} y {dy:F3} z {dz:F3}; {widths} width/class/flags, {attrs} attributes");
        }
        return string.Join("; ", diffs);
    }

    /// <summary>
    /// Centreline ends lying on an edge two served tiles share, how many have no end of the neighbour's within 1 cm
    /// across and 2 cm up (a road that breaks or steps where two tiles meet), and of those how many none within 1 cm across.
    /// </summary>
    /// <summary>For each end apart across, the distance to the neighbour's nearest end on the edge.</summary>
    static readonly List<double> Gaps = new();

    static (int Ends, int Open, int Apart) Seams(Dictionary<TileId, RoadTile> tiles)
    {
        var at = new Dictionary<TileId, List<(double E, double N, float Y)>>();
        foreach (var (id, t) in tiles)
        {
            var list = at[id] = new();
            foreach (var sg in t.Segments)
                foreach (int i in new[] { 0, sg.PointCount - 1 })
                {
                    float x = sg.Points[i * 3], z = sg.Points[i * 3 + 2];
                    if (x < 0.02f || x > 999.98f || z < 0.02f || z > 999.98f) list.Add((id.MinE + x, id.MaxN - z, sg.Points[i * 3 + 1]));
                }
        }
        int ends = 0, open = 0, apart = 0;
        foreach (var (id, list) in at)
            foreach (var p in list)
            {
                // the tile across the edge the end lies on
                int de = p.E - id.MinE < 0.02 ? -1 : id.MinE + 1000 - p.E < 0.02 ? 1 : 0;
                int dn = id.MaxN - p.N < 0.02 ? 1 : p.N - (id.MaxN - 1000) < 0.02 ? -1 : 0;
                var other = new TileId(id.E + de, id.N + dn);
                if (!at.TryGetValue(other, out var there)) continue;
                ends++;
                bool across = there.Any(q => Math.Abs(q.E - p.E) < 0.01 && Math.Abs(q.N - p.N) < 0.01);
                if (!across || !there.Any(q => Math.Abs(q.E - p.E) < 0.01 && Math.Abs(q.N - p.N) < 0.01 && Math.Abs(q.Y - p.Y) < 0.02)) open++;
                if (!across)
                {
                    apart++;
                    double gap = there.Count == 0 ? double.MaxValue : there.Min(q => Math.Sqrt((q.E - p.E) * (q.E - p.E) + (q.N - p.N) * (q.N - p.N)));
                    Gaps.Add(gap);
                }
            }
        return (ends, open, apart);
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
}
