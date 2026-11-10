using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The globe's finer levels where leaves exist (#1430 H4; docs/plans/ellipsoid-handover.md §6a,
/// tools/cube-tiles note): levels 13 down to 5 averaged from a <c>--cube</c> build's level-14 leaves
/// (swissALTI3D, RGE ALTI), merged into a <c>--cube-global</c> region beside its levels 0-4, so one
/// pyramid runs from the leaves to the whole Earth (agreed with #1443's coordinator).
///
/// <code>
/// --cube-swiss --cube-leaves DIR --etopo DIR --cube-out GLOBAL_DIR [--cube-levels 5-13] [--jobs N] [--cube-check]
/// </code>
///
/// <para>
/// <b>Each height is a function of its lattice point</b>, as G1 asked (#1455): a level's vertex is the
/// tent-weighted mean (¼ ½ ¼ each way) of the next finer level's nine lattice points round it, the
/// same points from either neighbour, so edges stay the same bits. A vertex whose nine are not all
/// there (past the leaves' edge) takes ETOPO's footprint mean instead, as levels 0-4 do; so does every
/// vertex of a tile with no leaves under it. Built for every tile over the leaves and its three
/// siblings, so the renderer can always split a parent into all four.
/// </para>
/// </summary>
public static class CubeSwiss
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-swiss");

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? leavesDir = null, etopo = null, outDir = null;
        int minLevel = 5, maxLevel = 13, jobs = Environment.ProcessorCount;
        bool check = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-swiss": break;
                case "--cube-leaves": leavesDir = args[++i]; break;
                case "--etopo": etopo = args[++i]; break;
                case "--cube-out": outDir = args[++i]; break;
                case "--cube-levels":
                    var r = args[++i].Split('-');
                    minLevel = int.Parse(r[0], CultureInfo.InvariantCulture);
                    maxLevel = int.Parse(r[^1], CultureInfo.InvariantCulture);
                    break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-check": check = true; break;
                default: Console.Error.WriteLine($"--cube-swiss: unknown option {args[i]}"); return 2;
            }
        }
        if (leavesDir == null || etopo == null || outDir == null)
        {
            Console.Error.WriteLine("--cube-swiss needs --cube-leaves DIR, --etopo DIR and --cube-out DIR (a --cube-global region)");
            return 2;
        }
        string manifestPath = Path.Combine(outDir, CubeManifest.FileName);
        var manifest = CubeManifest.FromJson(File.ReadAllText(manifestPath));

        var sw = Stopwatch.StartNew();
        // several regions (comma separated): the first that holds a leaf gives it (#1654: B4's, then G1's where B4 has none)
        var leafDirs = leavesDir.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var leafManifest = CubeManifest.FromJson(File.ReadAllText(Path.Combine(leafDirs[0], CubeManifest.FileName)));
        int leafLevel = leafManifest.LeafLevel;
        if (maxLevel >= leafLevel) { Console.Error.WriteLine($"--cube-levels must stay above the leaves' level {leafLevel}"); return 2; }
        // the finer level's heights, level by level: the leaves first, from their .cubl bundles (#1507; G1's .cubb read too),
        // each at its surface: a lake's still water over its bed (#1654)
        var (finer, wet, lakes) = ReadLeafSurfaces(leafDirs[0], jobs);
        foreach (var more in leafDirs.Skip(1))
        {
            var (extra, extraWet, _) = ReadLeafSurfaces(more, jobs);
            int added = 0;
            foreach (var (key, tile) in extra)
                if (finer.TryAdd(key, tile)) added++;
            wet += extraWet;
            Console.WriteLine($"cube-swiss: {added} more leaves from {more} (of {extra.Count})");
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"cube-swiss: {finer.Count} leaves at level {leafLevel} read in {sw.Elapsed.TotalSeconds:F0} s, {wet} with water at its surface; lakes at {string.Join(", ", lakes.Select(l => l.ToString("F1", CultureInfo.InvariantCulture)))} m"));
        var grid = EtopoGrid.Load(etopo, jobs);

        // the tiles of each level: every ancestor of a leaf, and its siblings
        var leafKeys = finer.Keys.ToList();
        manifest.Tiles.RemoveAll(t => CubeKey.Parse(t.Key).Level is var l && l >= minLevel && l <= maxLevel);
        // each level's colour before the rebuild: it depends on the footprint alone, so it carries over (#1659)
        var colourBefore = manifest.Levels.Where(l => l.Level >= minLevel && l.Level <= maxLevel).ToDictionary(l => l.Level);
        manifest.Levels.RemoveAll(l => l.Level >= minLevel && l.Level <= maxLevel);
        int uncoloured = 0;
        long bytes = 0;
        var built = new ConcurrentDictionary<CubeKey, CubeTile>();
        for (int level = maxLevel; level >= minLevel; level--)
        {
            ct.ThrowIfCancellationRequested();
            var keys = new HashSet<CubeKey>();
            foreach (var leaf in leafKeys)
                foreach (var sibling in leaf.AncestorAt(level).Parent.Children())
                    keys.Add(sibling);
            int bits = CubeKey.LatticeBits(level), finerBits = CubeKey.LatticeBits(level + 1);
            var source = finer;
            var made = new ConcurrentDictionary<CubeKey, CubeTile>();
            long fromLeaves = 0, fromEtopo = 0;
            Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, key =>
            {
                var (h, own, etopoCount) = Averaged(source, grid, key, bits, finerBits, lakes, blend: level + 1 == leafLevel);
                Interlocked.Add(ref fromLeaves, own);
                Interlocked.Add(ref fromEtopo, etopoCount);
                var tile = new CubeTile { Key = key, Bits = bits, Quantum = CubeKey.QuantumFor(level), Heights = h };
                // what the file holds feeds the next level and the check, so all agree on the stored bits
                made[key] = CubeTileCodec.Decode(CubeTileCodec.Encode(tile));
            });
            foreach (var bundle in made.Keys.GroupBy(k => k.AncestorAt(CubeKey.PyramidBundleLevel(k.Level))))
            {
                var list = bundle.OrderBy(k => k.Y).ThenBy(k => k.X).Select(k => (k, CubeTileCodec.Encode(made[k]))).ToList();
                CubeBundle.Write(Path.Combine(outDir, CubeBundle.FileName(bundle.Key)), bundle.Key, list);
                foreach (var (key, bb) in list)
                {
                    var (_, _, min, max) = CubeTileCodec.ReadHeader(bb);
                    manifest.Tiles.Add(new CubeManifestTile { Key = key.ToString(), Min = min, Max = max, Bytes = bb.Length });
                    bytes += bb.Length;
                }
            }
            var rebuilt = new CubeManifestLevel { Level = level, Bits = bits, BundleLevel = CubeKey.PyramidBundleLevel(level) };
            var noColour = CubeColourCarry.Carry(outDir, colourBefore.GetValueOrDefault(level), rebuilt, made.Keys);
            if (rebuilt.Albedo > 0 && noColour.Count > 0)
                Console.WriteLine($"cube-swiss: level {level}: {noColour.Count} new tiles have no colour yet (first {noColour[0]})");
            uncoloured += rebuilt.Albedo > 0 ? noColour.Count : 0;
            manifest.Levels.Add(rebuilt);
            if (check) foreach (var (k, t) in made) built[k] = t;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"cube-swiss: level {level}: {made.Count} tiles, {100.0 * fromLeaves / Math.Max(1, fromLeaves + fromEtopo):F1} % of vertices from the finer level (the rest ETOPO), {sw.Elapsed.TotalSeconds:F0} s"));
            // the next level averages this one
            finer = made;
        }
        manifest.Levels.Sort((a, b) => a.Level.CompareTo(b.Level));
        manifest.Lakes = lakes.Length > 0 ? lakes.ToList() : null;
        manifest.LeafLevel = manifest.Levels[^1].Level;
        manifest.Bits = manifest.Levels[^1].Bits;
        manifest.BundleLevel = manifest.Levels[^1].BundleLevel;
        string leafSources = string.Join("; ", leafManifest.Sources);
        manifest.Sources.RemoveAll(s => s.StartsWith("levels ", StringComparison.Ordinal));
        manifest.Sources.Add($"levels {minLevel}-{maxLevel}: averaged from {leafManifest.Tiles.Count} level-{leafLevel} leaves ({leafSources}), ETOPO past them");
        File.WriteAllText(manifestPath, manifest.ToJson());
        int colourless = manifest.Levels.Count(l => l.Level >= minLevel && l.Level <= maxLevel && l.Albedo == 0);
        if (uncoloured > 0 || colourless > 0)
            Console.WriteLine($"cube-swiss: {uncoloured} tiles and {colourless} levels without colour: run the colour bake (--globealbedo {outDir}), or the globe draws them white");
        Console.WriteLine($"cube-swiss: {bytes / 1e6:F1} MB in levels {minLevel}-{maxLevel}, {sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return check ? CubeGlobal.CheckEdges(built) : 0;
    }

    /// <summary>A region's leaves at their surface (<see cref="CubeSurface"/>): heights, the water over them; G1's <c>.cubb</c> as they are.</summary>
    private static (ConcurrentDictionary<CubeKey, CubeTile> Leaves, int Wet, float[] Lakes) ReadLeafSurfaces(string dir, int jobs)
    {
        var leaves = Directory.GetFiles(dir, "*.cubl");
        if (leaves.Length == 0) return (CubeCheck.ReadLeafHeights(dir, jobs), 0, []);
        var tiles = new ConcurrentDictionary<CubeKey, CubeTile>();
        // still levels to the decimetre and how many samples stand at each: a big lake is one level over many leaves
        var levels = new ConcurrentDictionary<int, long>();
        int wet = 0;
        Parallel.ForEach(leaves, new ParallelOptions { MaxDegreeOfParallelism = jobs }, path =>
        {
            foreach (var (key, bytes) in CubeBundle.ReadAllEntries(path))
            {
                if (CubeLeafCodec.ReadSection(bytes, LeafKind.Heights) is not { } s) continue;
                var heights = LeafHeights.Decode(s);
                var water = CubeLeafCodec.ReadSection(bytes, LeafKind.Water) is { } w ? LeafWater.Decode(w) : null;
                if (water != null)
                    foreach (var g in water.Level.Where(l => !float.IsNaN(l)).GroupBy(l => (int)MathF.Round(l * 10)))
                        levels.AddOrUpdate(g.Key, g.Count(), (_, c) => c + g.Count());
                var surface = CubeSurface.Of(heights, water);
                if (!ReferenceEquals(surface, heights)) Interlocked.Increment(ref wet);
                tiles[key] = surface;
            }
        });
        var lakes = CubeSurface.LakeLevels(levels, LakeSamples);
        return (tiles, wet, lakes);
    }

    /// <summary>Samples at one level that make a lake whose surface ETOPO's is snapped to (Lake Geneva: millions).</summary>
    private const long LakeSamples = 200_000;

    /// <summary>How far ETOPO's surface may stand from the leaves' lake (Lake Geneva: 418.3 against 422.3 m, #1654).</summary>
    private const float LakeSnapM = 6f;

    /// <summary>How far round a vertex ETOPO must stay within <see cref="LakeSnapM"/> of the lake too, degrees (~400 m): a basin, not a slope.</summary>
    private const double LakeFlatDeg = 0.004;

    /// <summary>How close a finer level's tent mean must stand to a lake's level, all nine points, to be that lake, metres.</summary>
    private const float LakeOnM = 0.3f;

    /// <summary>
    /// The leaves' lake level for an ETOPO height at a lattice point, where ETOPO holds a lake there: near one of the
    /// leaves' lakes, and so for ~400 m each way (ETOPO draws a lake a few metres off the survey, and its 1.8 km cells
    /// rise towards a shore, so no flatness test: one left strips 4 m low and green bands across Léman, #1669); else NaN.
    /// A function of the point, so tiles sharing it agree.
    /// </summary>
    private static float LakeAt(EtopoGrid grid, float[] lakes, double lat, double lon, float etopo)
    {
        float lake = Nearest(lakes, etopo, LakeSnapM);
        if (float.IsNaN(lake)) return lake;
        foreach (var (dlat, dlon) in new[] { (LakeFlatDeg, 0.0), (-LakeFlatDeg, 0.0), (0.0, LakeFlatDeg), (0.0, -LakeFlatDeg) })
            if (Math.Abs(grid.Footprint(lat + dlat, lon + dlon, 1) - lake) > LakeSnapM) return float.NaN;
        return lake;
    }

    /// <summary>The lake level nearest a height, within <paramref name="within"/> metres; else NaN.</summary>
    private static float Nearest(float[] lakes, float h, float within)
    {
        float best = float.NaN, d = within;
        foreach (float l in lakes)
            if (Math.Abs(h - l) <= d) { best = l; d = Math.Abs(h - l); }
        return best;
    }

    /// <summary>
    /// A tile's heights from the finer level: each vertex the tent mean of the nine finer lattice
    /// points round it (when all nine exist), else ETOPO's footprint mean. Also how many came from each.
    /// </summary>
    private static (float[] Heights, long Own, long Etopo) Averaged(ConcurrentDictionary<CubeKey, CubeTile> finer, EtopoGrid grid,
        CubeKey key, int bits, int finerBits, float[] lakes, bool blend)
    {
        int n = (1 << bits) + 1;
        var h = new float[n * n];
        long own = 0, etopo = 0;
        // a vertex at (level + bits) is the finer level's point 2i at (level + 1 + finerBits) when the lattices
        // are one level apart: level + bits + 1 == level + 1 + finerBits (bits capped at 9 on both)
        int scale = (key.Level + 1 + finerBits) - (key.Level + bits);
        long gi0 = (long)key.X << bits, gj0 = (long)key.Y << bits;
        long finerSide = 1L << (key.Level + 1 + finerBits);
        float[]? etopoHeights = null;
        Span<float> w = stackalloc float[] { 0.25f, 0.5f, 0.25f };
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                long fi = (gi0 + i) << scale, fj = (gj0 + j) << scale;
                double sum = 0;
                bool all = true;
                for (int b = -1; b <= 1 && all; b++)
                    for (int a = -1; a <= 1; a++)
                    {
                        if (FinerAt(finer, key.Face, key.Level + 1, finerBits, finerSide, fi + a, fj + b) is not { } v) { all = false; break; }
                        sum += w[a + 1] * w[b + 1] * v;
                    }
                if (all)
                {
                    // the leaves' lake to one level: their own water stands a few centimetres apart from leaf to leaf,
                    // and ETOPO's lake beside them snaps to the level exactly (the globe draws water where it is level)
                    float mean = (float)sum;
                    if (lakes.Length > 0 && Nearest(lakes, mean, LakeOnM) is var on && !float.IsNaN(on) && OnLake(finer, key.Face, key.Level + 1, finerBits, finerSide, fi, fj, on))
                        mean = on;
                    h[j * n + i] = mean;
                    own++;
                }
                else
                {
                    etopoHeights ??= CubeGlobal.Heights(grid, key, bits);
                    float e = etopoHeights[j * n + i];
                    bool snapped = false;
                    if (lakes.Length > 0)
                    {
                        // a lake beside the leaves stands at their level, not ETOPO's few metres off (#1654)
                        var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(key.Level + bits, gi0 + i), CubeSphere.Lattice(key.Level + bits, gj0 + j));
                        double lat = Math.Atan2(normal.Z, Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y)) * 180 / Math.PI;
                        double lon = Math.Atan2(normal.Y, normal.X) * 180 / Math.PI;
                        if (LakeAt(grid, lakes, lat, lon, e) is var lake && !float.IsNaN(lake)) { e = lake; snapped = true; }
                    }
                    // past the leaves' edge, ETOPO corrected by how far it misses the nearest leaves, the correction fading
                    // over BlendM: ETOPO's 1.8 km cells blur a steep shore, and a hard switch stood a wall up to 120 m along
                    // the region's edge (#1654); a correction, not the leaves' height, so a mountain beside them keeps its shape
                    // from the leaves alone: a coarser level's finer one already holds ETOPO past them, and blending into its
                    // edge again at each level carried the leaves' foothills over the Salève (889 m down to 485)
                    // a lake snapped to the leaves' level is theirs already: ETOPO's miss there (+4 m) stood humps on it
                    if (blend && !snapped && NearestFiner(finer, grid, key.Face, key.Level + 1, finerBits, finerSide, fi, fj) is var (miss, t) && t < 1)
                    {
                        float s = t * t * (3 - 2 * t);
                        e += miss * (1 - s);
                    }
                    h[j * n + i] = e;
                    etopo++;
                }
            }
        return (h, own, etopo);
    }

    /// <summary>How far past the leaves their heights blend into ETOPO's, metres.</summary>
    private const double BlendM = 1500;

    /// <summary>Rings searched out to <see cref="BlendM"/>, and directions on each.</summary>
    private const int BlendRings = 16, BlendDirections = 12;

    private static readonly (double X, double Y)[] Directions = Enumerable.Range(0, BlendDirections)
        .Select(d => (Math.Cos(2 * Math.PI * d / BlendDirections), Math.Sin(2 * Math.PI * d / BlendDirections))).ToArray();

    /// <summary>
    /// How far ETOPO misses the finer level's nearest heights round its point (gi, gj) (their mean less ETOPO's there),
    /// searched ring by ring out to <see cref="BlendM"/>, and how far, 0..1 of it (1: none). Rings and directions are
    /// fixed, so the answer is a function of the point.
    /// </summary>
    private static (float Miss, float T) NearestFiner(ConcurrentDictionary<CubeKey, CubeTile> finer, EtopoGrid grid, int face, int level, int bits,
        long side, long gi, long gj)
    {
        // a lattice step at this level, metres (a face is ~10,000 km across)
        double stepM = 6_371_000 * Math.PI / 2 / side;
        if (BlendM / stepM < 1) return (0, 1);
        for (int ring = 1; ring <= BlendRings; ring++)
        {
            // rings close together near the leaves (the first a lattice step out), wider further: a vertex beside them
            // takes the height beside it
            double f = ring / (double)BlendRings, rM = Math.Max(stepM, BlendM * f * f), r = rM / stepM;
            double sum = 0;
            int found = 0;
            foreach (var (dx, dy) in Directions)
            {
                long pi = gi + (long)Math.Round(dx * r), pj = gj + (long)Math.Round(dy * r);
                if (FinerAt(finer, face, level, bits, side, pi, pj) is not { } v) continue;
                sum += v - EtopoAt(grid, face, level + bits, pi, pj, stepM);
                found++;
            }
            if (found > 0) return ((float)(sum / found), (float)(rM / BlendM));
        }
        return (0, 1);
    }

    /// <summary>Whether the nine finer points round (fi, fj) all stand within <see cref="LakeOnM"/> of a lake's level.</summary>
    private static bool OnLake(ConcurrentDictionary<CubeKey, CubeTile> finer, int face, int level, int bits, long side, long fi, long fj, float lake)
    {
        for (int b = -1; b <= 1; b++)
            for (int a = -1; a <= 1; a++)
                if (FinerAt(finer, face, level, bits, side, fi + a, fj + b) is not { } v || Math.Abs(v - lake) > LakeOnM) return false;
        return true;
    }

    /// <summary>ETOPO's footprint mean at a face lattice point (as <see cref="CubeGlobal.Heights"/> takes it).</summary>
    private static double EtopoAt(EtopoGrid grid, int face, int lv, long gi, long gj, double stepM)
    {
        var normal = CubeSphere.Normal(face, CubeSphere.Lattice(lv, gi), CubeSphere.Lattice(lv, gj));
        double lat = Math.Atan2(normal.Z, Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y)) * 180 / Math.PI;
        double lon = Math.Atan2(normal.Y, normal.X) * 180 / Math.PI;
        return grid.Footprint(lat, lon, stepM / 2);
    }

    /// <summary>The finer level's height at its face lattice point (gi, gj), or null when no finer tile holds it (or it has no data there).</summary>
    private static float? FinerAt(ConcurrentDictionary<CubeKey, CubeTile> finer, int face, int level, int bits, long side, long gi, long gj)
    {
        if (gi < 0 || gj < 0 || gi > side || gj > side) return null;
        // a point on a tile edge is in either tile, the same bits: take the one it starts
        long tx = Math.Min(gi >> bits, (1L << level) - 1), ty = Math.Min(gj >> bits, (1L << level) - 1);
        if (!finer.TryGetValue(new CubeKey(face, level, (int)tx, (int)ty), out var t)) return null;
        float v = t.HeightAt((int)(gi - (tx << bits)), (int)(gj - (ty << bits)));
        return float.IsNaN(v) ? null : v;
    }
}
