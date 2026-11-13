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
/// --cube-swiss --cube-leaves DIR --etopo DIR --cube-out GLOBAL_DIR [--cube-levels 5-13] [--jobs N] [--cube-check] [--tlm GPKG]
///   [--relief FILE|none]
/// </code>
///
/// <para>
/// <b>Each height is a function of its lattice point</b>, as G1 asked (#1455): a level's vertex is the
/// tent-weighted mean (¼ ½ ¼ each way) of the next finer level's nine lattice points round it, the
/// same points from either neighbour, so edges stay the same bits. A vertex whose nine are not all
/// there (past the leaves' edge) takes the coarse ground's footprint mean instead: the game's 500 m Swiss relief
/// where it holds (<c>--relief</c>, #1746: ETOPO's 1.8 km cells drew the far Alps as smooth grey sheets), ETOPO
/// beyond it, as levels 0-4 do; so does every vertex of a tile with no leaves under it. Built for every tile over the leaves and its three
/// siblings, so the renderer can always split a parent into all four.
/// </para>
/// </summary>
public static class CubeSwiss
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-swiss");

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? leavesDir = null, etopo = null, outDir = null, tlm = null;
        // past the leaves the game's 500 m Swiss relief, not ETOPO's 1.8 km cells (#1746); "none" for ETOPO alone
        string? relief = File.Exists(DefaultRelief) ? DefaultRelief : null;
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
                case "--tlm": tlm = args[++i]; break;
                case "--relief": relief = args[++i] is var r2 && r2 == "none" ? null : r2; break;
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
        if (relief != null)
        {
            grid.Regional = SwissRelief.Load(relief);
            Console.WriteLine($"cube-swiss: past the leaves the Swiss relief ({relief}, {grid.Regional.Spacing:F0} m), ETOPO beyond it");
        }
        else Console.WriteLine("cube-swiss: past the leaves ETOPO alone (no --relief)");
        // swissTLM3D's big lakes past the leaves (#1678): ETOPO smears a steep shore kilometres into them
        SwissLakeMask? lakeMask = null;
        if (tlm != null)
        {
            double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
            foreach (var leaf in finer.Keys)
            {
                var c = leaf.Centre;
                var (e, n) = Lv95Source.Instance.FromLatLon(c.LatDeg, c.LonDeg);
                minE = Math.Min(minE, e); maxE = Math.Max(maxE, e);
                minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
            }
            lakeMask = SwissLakeMask.Build(tlm, minE - LakeMarginM, minN - LakeMarginM, maxE + LakeMarginM, maxN + LakeMarginM, lakes,
                (lat, lon) => grid.At(lat, lon));
            Console.WriteLine($"cube-swiss: TLM lakes read in {sw.Elapsed.TotalSeconds:F0} s");
        }

        // the tiles of each level: every ancestor of a leaf, and its siblings; at the level that blends past the leaves,
        // every tile the blend reaches too (#1778): a coarser level stood the uncorrected relief where its tiles ended, a
        // wall tens of metres high along a straight tile edge in the fill built on it, and a ledge under the globe
        var leafKeys = finer.Keys.ToList();
        if (maxLevel + 1 == leafLevel) leafKeys = BlendReach(leafKeys, maxLevel);
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
                var (h, own, etopoCount) = Averaged(source, grid, key, bits, finerBits, lakes, blend: level + 1 == leafLevel, lakeMask);
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
                $"cube-swiss: level {level}: {made.Count} tiles, {100.0 * fromLeaves / Math.Max(1, fromLeaves + fromEtopo):F1} % of vertices from the finer level (the rest the coarse ground: the Swiss relief or ETOPO), {sw.Elapsed.TotalSeconds:F0} s"));
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
        manifest.Sources.Add($"levels {minLevel}-{maxLevel}: averaged from {leafManifest.Tiles.Count} level-{leafLevel} leaves ({leafSources}), {(relief != null ? "past them the game's 500 m Swiss relief (swisstopo swissALTIRegio), then ETOPO" : "ETOPO past them")}");
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

    /// <summary>The game's 500 m Swiss relief, from the repository root (#1746).</summary>
    private const string DefaultRelief = "src/Terrain/swiss_relief.gz";

    /// <summary>How far past the leaves' box TLM's lakes are read, metres.</summary>
    private const double LakeMarginM = 15_000;

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
        CubeKey key, int bits, int finerBits, float[] lakes, bool blend, SwissLakeMask? lakeMask = null)
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
        // the blend past the leaves, made on the first vertex that needs it (#1678)
        float[]? blendField = null;
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
                        e = Shaped(grid, lakes, lakeMask, lat, lon, e, out snapped);
                    }
                    // past the leaves' edge, ETOPO corrected by how far it misses the nearest leaves, the correction fading
                    // over BlendM: ETOPO's 1.8 km cells blur a steep shore, and a hard switch stood a wall up to 120 m along
                    // the region's edge (#1654); a correction, not the leaves' height, so a mountain beside them keeps its shape
                    // from the leaves alone: a coarser level's finer one already holds ETOPO past them, and blending into its
                    // edge again at each level carried the leaves' foothills over the Salève (889 m down to 485)
                    // a lake snapped to the leaves' level is theirs already: ETOPO's miss there (+4 m) stood humps on it
                    // (#1678) a continuous field, no longer 16 rings: each ring's own correction stood a step up to 40 m
                    // high along the leaves' edge east of Lavaux, and the fill built on the pyramid drew them as terraces
                    if (blend && !snapped)
                    {
                        blendField ??= BlendField(finer, grid, key, bits, finerBits, lakes, lakeMask);
                        e += blendField[j * n + i];
                    }
                    h[j * n + i] = e;
                    etopo++;
                }
            }
        return (h, own, etopo);
    }

    /// <summary>
    /// ETOPO's height at a point as the levels take it: a leaves' lake where TLM has one (<see cref="SwissLakeMask"/>,
    /// <paramref name="snapped"/>), its shore brought down to it, or where ETOPO itself stands at one (<see cref="LakeAt"/>).
    /// </summary>
    private static float Shaped(EtopoGrid grid, float[] lakes, SwissLakeMask? lakeMask, double lat, double lon, float e, out bool snapped)
    {
        snapped = false;
        if (lakeMask != null)
        {
            e = lakeMask.Apply(lat, lon, e, out snapped, out bool covered);
            // on the mask TLM alone says where the leaves' lakes are: ETOPO's basin test took the Rhône plain at
            // Villeneuve, 0-6 m over Léman, for lake 2.5-3 km past the shore, and the fill drew it as water (#1696)
            if (snapped || covered) return e;
        }
        if (LakeAt(grid, lakes, lat, lon, e) is var lake && !float.IsNaN(lake))
        {
            snapped = true;
            return lake;
        }
        return e;
    }

    /// <summary>The blend field's window spacing, lattice vertices of the level (~9.5 m at level 13).</summary>
    private const int FieldStep = 4;

    /// <summary>The box blur's radius over the field, window cells, applied twice (~110 m across).</summary>
    private const int FieldBlur = 6;

    /// <summary>
    /// The correction past the leaves at each vertex of a level's tile (#1678): on a window round the tile, every
    /// <see cref="FieldStep"/> vertices, the finer level's points (the leaves) and how far ETOPO (as <see cref="Shaped"/>
    /// has it) misses them; past them each point takes the miss of the nearest one (an exact distance transform), fading
    /// out over <see cref="BlendM"/>; the whole smoothed by two box blurs (each value a sum in one order over the same
    /// cells, so tiles sharing a vertex agree to the bit), bilinear at the vertex. The ring search before it stood a step
    /// at every ring.
    /// </summary>
    private static float[] BlendField(ConcurrentDictionary<CubeKey, CubeTile> finer, EtopoGrid grid, CubeKey key, int bits, int finerBits,
        float[] lakes, SwissLakeMask? lakeMask)
    {
        int n = (1 << bits) + 1, lv = key.Level + bits, scale = (key.Level + 1 + finerBits) - lv;
        long gi0 = (long)key.X << bits, gj0 = (long)key.Y << bits, finerSide = 1L << (key.Level + 1 + finerBits);
        double stepM = 6_371_000 * Math.PI / 2 / (1L << lv), cellM = FieldStep * stepM;
        double half = 0.5 * 1.22 * Math.Pow(2, Math.Max(0, 23 - lv));
        int margin = (int)Math.Ceiling(BlendM / cellM) + 4 * FieldBlur + 2;
        long wx0 = gi0 / FieldStep - margin, wy0 = gj0 / FieldStep - margin;
        int side = (n - 1) / FieldStep + 1 + 2 * margin;
        var own = new bool[side * side];
        var miss = new float[side * side];
        bool any = false;
        Parallel.For(0, side, y =>
        {
            for (int x = 0; x < side; x++)
            {
                long pi = (wx0 + x) * FieldStep, pj = (wy0 + y) * FieldStep;
                if (FinerAt(finer, key.Face, key.Level + 1, finerBits, finerSide, pi << scale, pj << scale) is not { } v) continue;
                var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(lv, pi), CubeSphere.Lattice(lv, pj));
                double lat = Math.Atan2(normal.Z, Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y)) * 180 / Math.PI;
                double lon = Math.Atan2(normal.Y, normal.X) * 180 / Math.PI;
                float e = Shaped(grid, lakes, lakeMask, lat, lon, (float)grid.Footprint(lat, lon, half), out _);
                own[y * side + x] = true;
                miss[y * side + x] = v - e;
                any = true;
            }
        });
        var field = new float[n * n];
        if (!any) return field;
        var dist = new float[side * side];
        var site = new int[side * side];
        DistanceTransform.Run(own, side, side, dist, site);
        var c = new float[side * side];
        for (int k = 0; k < c.Length; k++)
        {
            if (own[k]) { c[k] = miss[k]; continue; }
            if (site[k] < 0) continue;
            double t = Math.Min(1, dist[k] * cellM / BlendM);
            c[k] = (float)(miss[site[k]] * (1 - t * t * (3 - 2 * t)));
        }
        for (int pass = 0; pass < 2; pass++)
        {
            c = Blur(c, side, 1, 0);
            c = Blur(c, side, 0, 1);
        }
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double fx = (double)(gi0 + i) / FieldStep - wx0, fy = (double)(gj0 + j) / FieldStep - wy0;
                int x0 = (int)fx, y0 = (int)fy;
                double u = fx - x0, v = fy - y0;
                int k = y0 * side + x0;
                field[j * n + i] = (float)((1 - v) * ((1 - u) * c[k] + u * c[k + 1]) + v * ((1 - u) * c[k + side] + u * c[k + side + 1]));
            }

        // (#1779) at the leaves' edge the blurred field is the miss averaged over ~110 m, not the miss of the vertex beside
        // it: on a cliff the two stood 77 m apart, a step along every leaf cell's edge. Within RampM of the leaves' own
        // vertices the correction starts from the nearest one's exact miss (an exact distance transform on the level's own
        // lattice, so tiles sharing a vertex agree) and hands over to the field.
        // the margin holds the ramp and twice the widest blur's reach (two passes each way): a blurred cell that far out
        // finds its nearest own vertex up to the ramp and that reach further on, inside the window from every tile
        int m = (int)Math.Ceiling(RampM / stepM) + 2 + 4 * RampBlurs[^1], ws = n + 2 * m;
        var ownV = new bool[ws * ws];
        var missV = new float[ws * ws];
        bool anyV = false;
        Parallel.For(0, ws, y =>
        {
            for (int x = 0; x < ws; x++)
            {
                long vi = gi0 - m + x, vj = gj0 - m + y;
                if (TentMean(finer, key.Face, key.Level + 1, finerBits, finerSide, vi << scale, vj << scale) is not { } mean) continue;
                var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(lv, vi), CubeSphere.Lattice(lv, vj));
                double lat = Math.Atan2(normal.Z, Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y)) * 180 / Math.PI;
                double lon = Math.Atan2(normal.Y, normal.X) * 180 / Math.PI;
                float e = Shaped(grid, lakes, lakeMask, lat, lon, (float)grid.Footprint(lat, lon, half), out _);
                ownV[y * ws + x] = true;
                missV[y * ws + x] = mean - e;
                anyV = true;
            }
        });
        if (!anyV) return field;
        var distV = new float[ws * ws];
        var siteV = new int[ws * ws];
        DistanceTransform.Run(ownV, ws, ws, distV, siteV);
        // the nearest own vertex's miss everywhere (its own on an own vertex), and blurred at growing radii: the nearest one
        // changes on the medial axis of a notch, where two edge vertices far apart along the edge meet (a 17 m step beside
        // a 10 m cliff above Riddes); a vertex d away reads a blur of radius ~d/2, so the seam keeps the exact miss and
        // the medial axis is smoothed over as far as it lies from the edge
        var ext = new long[ws * ws];
        for (int k = 0; k < ext.Length; k++)
            ext[k] = siteV[k] < 0 ? 0 : (long)Math.Round(missV[ownV[k] ? k : siteV[k]] * FixedPerM);
        var blurred = new long[RampBlurs.Length][];
        for (int b = 0; b < RampBlurs.Length; b++)
        {
            var f = ext;
            for (int pass = 0; pass < 2; pass++)
            {
                f = BoxSum(f, ws, 1, 0, RampBlurs[b]);
                f = BoxSum(f, ws, 0, 1, RampBlurs[b]);
            }
            blurred[b] = f;
        }
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = (j + m) * ws + i + m;
                if (siteV[k] < 0) continue;
                double d = distV[k] * stepM;
                if (d >= RampM) continue;
                double r = distV[k] / 2;
                double start;
                if (r <= RampBlurs[0]) start = Mix(ext[k], Blurred(blurred, 0, k), r / RampBlurs[0]);
                else if (r >= RampBlurs[^1]) start = Blurred(blurred, RampBlurs.Length - 1, k);
                else
                {
                    int b = 0;
                    while (RampBlurs[b + 1] < r) b++;
                    start = Mix(Blurred(blurred, b, k) * FixedPerM, Blurred(blurred, b + 1, k), (r - RampBlurs[b]) / (RampBlurs[b + 1] - RampBlurs[b]));
                }
                double t = d / RampM, s = t * t * (3 - 2 * t);
                field[j * n + i] = (float)((1 - s) * start + s * field[j * n + i]);
            }
        return field;
    }

    /// <summary>Fixed-point units a metre for the ramp's blurs: sums of integers are exact in any order, so tiles agree.</summary>
    private const double FixedPerM = 1024;

    /// <summary>The ramp's blur radii, vertices of the level (2.4 m at level 13), each applied twice each way.</summary>
    private static readonly int[] RampBlurs = [1, 2, 4, 8, 16];

    /// <summary>Blur <paramref name="b"/> at <paramref name="k"/>, metres.</summary>
    private static double Blurred(long[][] blurred, int b, int k)
    {
        double norm = Math.Pow(2 * RampBlurs[b] + 1, 4);
        return blurred[b][k] / norm / FixedPerM;
    }

    /// <summary>a (fixed point, or metres times <see cref="FixedPerM"/>) to b (metres) by t.</summary>
    private static double Mix(double aFixed, double b, double t) => (1 - t) * (aFixed / FixedPerM) + t * b;

    /// <summary>A box sum of radius <paramref name="r"/> along one axis (edges clamped), exact: integers.</summary>
    private static long[] BoxSum(long[] f, int side, int dx, int dy, int r)
    {
        var o = new long[f.Length];
        Parallel.For(0, side, line =>
        {
            long sum = 0;
            int At(int p) => dx == 1 ? line * side + Math.Clamp(p, 0, side - 1) : Math.Clamp(p, 0, side - 1) * side + line;
            for (int p = -r; p <= r; p++) sum += f[At(p)];
            for (int p = 0; p < side; p++)
            {
                o[At(p)] = sum;
                sum += f[At(p + r + 1)] - f[At(p - r)];
            }
        });
        return o;
    }

    /// <summary>
    /// How far from the leaves' own vertices the correction starts from the nearest one's exact miss before it is the
    /// blurred field, metres (#1779).
    /// </summary>
    private const double RampM = 100;

    /// <summary>The tent mean (¼ ½ ¼ each way) of the finer level's nine points round (fi, fj), null unless all nine exist.</summary>
    private static float? TentMean(ConcurrentDictionary<CubeKey, CubeTile> finer, int face, int level, int bits, long side, long fi, long fj)
    {
        double sum = 0;
        for (int b = -1; b <= 1; b++)
            for (int a = -1; a <= 1; a++)
            {
                if (FinerAt(finer, face, level, bits, side, fi + a, fj + b) is not { } v) return null;
                sum += TentW[a + 1] * TentW[b + 1] * v;
            }
        return (float)sum;
    }

    private static readonly float[] TentW = [0.25f, 0.5f, 0.25f];

    /// <summary>A box blur of radius <see cref="FieldBlur"/> along one axis, each value summed afresh in one order (edges clamped).</summary>
    private static float[] Blur(float[] f, int side, int dx, int dy)
    {
        var o = new float[f.Length];
        Parallel.For(0, side, y =>
        {
            for (int x = 0; x < side; x++)
            {
                double sum = 0;
                for (int r = -FieldBlur; r <= FieldBlur; r++)
                {
                    int xx = Math.Clamp(x + r * dx, 0, side - 1), yy = Math.Clamp(y + r * dy, 0, side - 1);
                    sum += f[yy * side + xx];
                }
                o[y * side + x] = (float)(sum / (2 * FieldBlur + 1));
            }
        });
        return o;
    }

    /// <summary>
    /// The tiles at <paramref name="level"/> (the one that blends past the leaves) whose correction is not zero: every
    /// tile within <see cref="BlendM"/>, the blur's reach and <see cref="RampM"/> of a leaf's tile, in tiles a side (each
    /// a tile's own key; its ancestors and siblings follow as for a leaf). Past them the correction is zero, so a coarser
    /// level's tent mean of this one and its own relief agree.
    /// </summary>
    internal static List<CubeKey> BlendReach(IEnumerable<CubeKey> leaves, int level)
    {
        // a tile's side at a face's middle, metres (Switzerland is on face 0's middle)
        double tileM = 6_371_000 * Math.PI / 2 / (1L << level);
        double cellM = FieldStep * tileM / (1 << CubeKey.LatticeBits(level));
        int grow = (int)Math.Ceiling((BlendM + 4 * FieldBlur * cellM + RampM) / tileM);
        var keys = new HashSet<CubeKey>();
        int side = 1 << level;
        foreach (var t in leaves.Select(l => l.AncestorAt(level)).Distinct())
            for (int y = Math.Max(0, t.Y - grow); y <= Math.Min(side - 1, t.Y + grow); y++)
                for (int x = Math.Max(0, t.X - grow); x <= Math.Min(side - 1, t.X + grow); x++)
                    keys.Add(new CubeKey(t.Face, level, x, y));
        return keys.ToList();
    }

    /// <summary>How far past the leaves their heights blend into ETOPO's, metres.</summary>
    private const double BlendM = 1500;

    /// <summary>Whether the nine finer points round (fi, fj) all stand within <see cref="LakeOnM"/> of a lake's level.</summary>
    private static bool OnLake(ConcurrentDictionary<CubeKey, CubeTile> finer, int face, int level, int bits, long side, long fi, long fj, float lake)
    {
        for (int b = -1; b <= 1; b++)
            for (int a = -1; a <= 1; a++)
                if (FinerAt(finer, face, level, bits, side, fi + a, fj + b) is not { } v || Math.Abs(v - lake) > LakeOnM) return false;
        return true;
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
