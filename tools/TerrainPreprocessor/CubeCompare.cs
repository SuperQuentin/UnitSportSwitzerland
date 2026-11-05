using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The comparer of G2 B3 (#1507, docs/plans/global-tiling-g2.md "B3"): a cube region of leaves against the
/// LV95 build of the same ground, kind by kind, both in true Earth positions.
///
/// <code>
/// --cube-compare --chunks LV95_DIR --cube CUBE_DIR [--kinds heights,roads,...] [--compare-stride N]
///                [--compare-leaves N] [--jobs N]
/// </code>
///
/// <para>
/// LV95 records go to ECEF through <see cref="SwissGeodesy.ToEcef"/>(MinE + x, MaxN − z, y) with their own
/// height (an altitude above LHN95), leaf records through their leaf's anchor. Vectors are compared in each
/// level-9 cell's anchor frame (one cube bundle at a time, ~20 km: the frame's tilt against the local
/// vertical is under 2.5 mrad there, a tenth of a millimetre on what is matched); rasters are compared at
/// every leaf lattice vertex (every <c>--compare-stride</c>-th), the LV95 value taken at that vertex's
/// rigorous LV95 position (bilinear for heights, nearest for categories). A draped feature is compared as
/// its height above its own build's ground (LV95: the <c>.terr</c> bilinear, the build's draping; leaves:
/// the leaf mesh). Matched by id where the source has one (EGID, OSM/LWB id), else by nearest neighbour.
/// </para>
///
/// <para>
/// Prints counts and p50/p95/p99/max per kind and exits 1 when a bar of <see cref="CompareBars"/> breaks
/// (2 on bad arguments). A kind with no side in the cube region is skipped with a line saying so. A record
/// counts only where both builds have ground: an LV95 record in a leaf the cube region holds, a leaf record
/// over an LV95 tile with a <c>.terr</c>.
/// </para>
/// </summary>
public static class CubeCompare
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-compare");

    private static readonly string[] AllKinds = ["heights", "cover", "holes", "water", "roads", "buildings", "trees", "pistes", "fields", "photo"];

    public static int Run(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? chunks = null, cubeDir = null;
        var kinds = AllKinds.ToList();
        int stride = 1, maxLeaves = int.MaxValue, jobs = Math.Max(1, Environment.ProcessorCount - 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-compare": break;
                case "--chunks": chunks = args[++i]; break;
                case "--cube": cubeDir = args[++i]; break;
                case "--kinds": kinds = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--compare-stride": stride = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--compare-leaves": maxLeaves = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"--cube-compare: unknown option {args[i]}"); return 2;
            }
        }
        if (chunks == null || cubeDir == null) { Console.Error.WriteLine("--cube-compare needs --chunks LV95_DIR and --cube CUBE_DIR"); return 2; }
        if (kinds.FirstOrDefault(k => !AllKinds.Contains(k)) is { } bad) { Console.Error.WriteLine($"--kinds: unknown kind {bad} ({string.Join(",", AllKinds)})"); return 2; }

        var clock = Stopwatch.StartNew();
        Console.OutputEncoding = System.Text.Encoding.UTF8;   // the keys' lat/lon print with a degree sign
        var lv = new Lv95Build(chunks);
        var cube = new CubeBuild(cubeDir, maxLeaves);
        Console.WriteLine($"cube-compare: {lv.Tiles.Count} LV95 tiles in {chunks}; {cube.Leaves.Count} leaves at level {CubeBuild.LeafLevel} "
            + $"in {cube.Bundles.Count} bundles in {cubeDir} ({string.Join(", ", cube.KindsPresent.Order())})");
        if (lv.Tiles.Count == 0 || cube.Leaves.Count == 0) { Console.Error.WriteLine("cube-compare: nothing to compare"); return 2; }
        var ctx = new Ctx(lv, cube, stride, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct });

        var reports = new List<KindReport>();
        foreach (var kind in kinds)
        {
            var sw = Stopwatch.StartNew();
            var r = kind switch
            {
                "heights" => Heights(ctx),
                "cover" => Cover(ctx),
                "holes" => Holes(ctx),
                "water" => Water(ctx),
                "roads" => Roads(ctx),
                "buildings" => Buildings(ctx),
                "trees" => Trees(ctx),
                "pistes" => Pistes(ctx),
                "fields" => Fields(ctx),
                _ => new KindReport(kind).Skip("photos are resampled in a Godot tool mode (B3 PR 7) and judged as images, not here"),
            };
            reports.Add(r);
            if (r.Skipped != null) { Console.WriteLine($"{kind}: skipped: {r.Skipped}"); continue; }
            Console.WriteLine($"{kind} ({sw.Elapsed.TotalSeconds:F0} s):");
            foreach (var line in r.Lines) Console.WriteLine("  " + line);
        }
        var breaches = reports.SelectMany(r => r.Breaches).ToList();
        Console.WriteLine($"cube-compare: {reports.Count(r => r.Skipped == null)} kinds compared, {reports.Count(r => r.Skipped != null)} skipped, "
            + $"{breaches.Count} bar(s) broken, {clock.Elapsed.TotalSeconds:F0} s");
        foreach (var b in breaches) Console.WriteLine("  BREACH " + b);
        return breaches.Count > 0 ? 1 : 0;
    }

    private sealed record Ctx(Lv95Build Lv, CubeBuild Cube, int Stride, ParallelOptions Par);

    private static KindReport? Missing(Ctx c, string kind, LeafKind leafKind, string lv95Pattern)
    {
        if (!c.Cube.KindsPresent.Contains(leafKind)) return new KindReport(kind).Skip($"no leaf in the cube region has a {leafKind} section yet");
        if (lv95Pattern.Length > 0 && !c.Lv.Has(lv95Pattern)) return new KindReport(kind).Skip($"the LV95 build has no {lv95Pattern} files");
        return null;
    }

    // ---- rasters: every leaf vertex against the LV95 value at its rigorous position -------------------

    private static KindReport Heights(Ctx c)
    {
        var r = new KindReport("heights");
        if (!c.Cube.KindsPresent.Contains(LeafKind.Heights)) return r.Skip("the cube region has no heights");
        var all = new Tally();
        var wet = new Tally();
        var fit = new ShiftFit();
        long noData = 0, outside = 0;
        var perLeaf = new ConcurrentBag<(CubeKey Key, double Mean, double Max)>();
        Parallel.ForEach(c.Cube.Ordered(), c.Par, () => (T: new Tally(), W: new Tally(), F: new ShiftFit(), N: 0L, O: 0L), (key, _, acc) =>
        {
            var tile = c.Cube.Heights(key);
            if (tile == null) return acc;
            var leaf = new Tally();
            int n = tile.Side;
            for (int j = 0; j < n; j += c.Stride)
                for (int i = 0; i < n; i += c.Stride)
                {
                    double h = tile.HeightAt(i, j);
                    if (double.IsNaN(h)) { acc.N++; continue; }
                    var (e, nn, hl) = SwissGeodesy.FromEcef(key.Vertex(tile.Bits, i, j, h));
                    double g = c.Lv.Ground(e, nn);
                    if (double.IsNaN(g)) { acc.O++; continue; }
                    double dh = hl - g;
                    // under water the LV95 heights are the bed (#298): apart, until leaves carry beds too (B3 PR 6)
                    if (c.Lv.UnderWater(e, nn)) { acc.W.Add(dh); continue; }
                    leaf.Add(dh);
                    double ge = c.Lv.Ground(e + 0.5, nn) - c.Lv.Ground(e - 0.5, nn), gn = c.Lv.Ground(e, nn + 0.5) - c.Lv.Ground(e, nn - 0.5);
                    // the shift is read off ordinary slopes: not flat (no signal), not cliffs (the grids sample them differently)
                    double slope = Math.Sqrt(ge * ge + gn * gn);
                    if (slope is > 0.05 and < 1.5 && Math.Abs(dh) < 0.5) acc.F.Add(dh, ge, gn);
                }
            if (leaf.Count > 0) perLeaf.Add((key, leaf.Spread().Mean, leaf.Spread().Max));
            acc.T.Merge(leaf);
            return acc;
        }, acc => { lock (all) { all.Merge(acc.T); wet.Merge(acc.W); fit.Merge(acc.F); noData += acc.N; outside += acc.O; } });

        r.Note($"{perLeaf.Count} leaves, vertices every {c.Stride}; {noData} without data, {outside} off the LV95 tiles");
        var s = all.Spread();
        r.Check("leaf − LV95 (LHN95 at the vertex), dry", s, true, new Bar(Stat.Mean, CompareBars.HeightBias), new Bar(Stat.P50, CompareBars.HeightMedian));
        if (wet.Count > 0) r.Check("leaf − LV95 where the LV95 sample touches water (its bed), not held to a bar", wet.Spread(), true);
        var (se, sn, bias) = fit.Solve();
        double shift = Math.Sqrt(se * se + sn * sn);
        r.Note(string.Create(CultureInfo.InvariantCulture,
            $"fitted on {fit.Count} slope vertices: the leaves sit {Spread.Signed(se * 100)} cm east, {Spread.Signed(sn * 100)} cm north, {Spread.Signed(bias * 100)} cm up of the LV95 surface"));
        r.AtMost("fitted horizontal shift (m)", shift, CompareBars.HeightShift, "F4");
        foreach (var w in perLeaf.OrderByDescending(p => Math.Abs(p.Mean)).Take(3))
            r.Note(string.Create(CultureInfo.InvariantCulture, $"largest leaf bias: {w.Key.Describe()} mean {Spread.Signed(w.Mean * 100)} cm, max {w.Max:F2} m"));
        return r;
    }

    private static KindReport Cover(Ctx c)
    {
        if (Missing(c, "cover", LeafKind.Cover, "cover_") is { } skip) return skip;
        var r = new KindReport("cover");
        var agree = new Agreement();
        Parallel.ForEach(c.Cube.Ordered(), c.Par, () => new Agreement(), (key, _, acc) =>
        {
            if (c.Cube.Section(key, LeafKind.Cover) is not { } sec || c.Cube.Heights(key) is not { } tile) return acc;
            var cells = LeafCover.Decode(sec);
            int n = tile.Side;
            for (int j = 0; j < n; j += c.Stride)
                for (int i = 0; i < n; i += c.Stride)
                {
                    var (e, nn) = c.Cube.Lv95Of(key, tile, i, j);
                    int lvClass = c.Lv.CoverAt(e, nn);
                    if (lvClass >= 0) acc.Add(lvClass, cells[j * n + i]);
                }
            return acc;
        }, acc => { lock (agree) agree.Merge(acc); });
        CategoryLines(r, agree, k => ((CoverClass)k).ToString());
        return r;
    }

    private static void CategoryLines(KindReport r, Agreement agree, Func<int, string> name)
    {
        r.Note($"{agree.Count} vertices, overall agreement {agree.Overall:P3}");
        foreach (var (k, count, share) in agree.PerClass())
        {
            if (count >= CompareBars.MinClassSamples) r.AtLeast($"{name(k)} ({count} vertices)", share, CompareBars.ClassAgreement);
            else r.Note(string.Create(CultureInfo.InvariantCulture, $"{name(k)} ({count} vertices, too few for the bar): {share:P2}"));
        }
    }

    private static KindReport Water(Ctx c)
    {
        if (Missing(c, "water", LeafKind.Water, "water_") is { } skip) return skip;
        var r = new KindReport("water");
        var wet = new Agreement();
        var level = new Tally();
        Parallel.ForEach(c.Cube.Ordered(), c.Par, () => (A: new Agreement(), T: new Tally()), (key, _, acc) =>
        {
            if (c.Cube.Heights(key) is not { } tile) return acc;
            var w = c.Cube.Section(key, LeafKind.Water) is { } sec ? LeafWater.Decode(sec) : CubeWater.Dry(tile.Bits);
            int side = w.Side;
            for (int rr = 0; rr < side; rr += Math.Max(1, c.Stride / CubeWater.Stride))
                for (int cc = 0; cc < side; cc += Math.Max(1, c.Stride / CubeWater.Stride))
                {
                    int i = cc * CubeWater.Stride, j = rr * CubeWater.Stride;
                    var (e, nn) = c.Cube.Lv95Of(key, tile, i, j);
                    if (c.Lv.WaterAt(e, nn) is not { } lvLevel) continue;   // off the LV95 tiles
                    float cubeLevel = w.Level[rr * side + cc];
                    acc.A.Add(double.IsNaN(lvLevel) ? 0 : 1, float.IsNaN(cubeLevel) ? 0 : 1);
                    if (double.IsNaN(lvLevel) || float.IsNaN(cubeLevel)) continue;
                    var (_, _, hl) = SwissGeodesy.FromEcef(key.Vertex(tile.Bits, i, j, cubeLevel));
                    acc.T.Add(hl - lvLevel);
                }
            return acc;
        }, acc => { lock (wet) { wet.Merge(acc.A); level.Merge(acc.T); } });
        CategoryLines(r, wet, k => k == 0 ? "dry" : "wet");
        r.Check("still level, leaf − LV95 (LHN95)", level.Spread(), true, new Bar(Stat.P99, CompareBars.WaterLevel));
        return r;
    }

    private static KindReport Pistes(Ctx c)
    {
        if (Missing(c, "pistes", LeafKind.Pistes, "pistes_") is { } skip) return skip;
        var r = new KindReport("pistes");
        var agree = new Agreement();
        var lvIds = new ConcurrentDictionary<ulong, byte>();
        var cubeIds = new ConcurrentDictionary<ulong, byte>();
        Parallel.ForEach(c.Cube.Ordered(), c.Par, () => new Agreement(), (key, _, acc) =>
        {
            if (c.Cube.Heights(key) is not { } tile) return acc;
            var p = c.Cube.Section(key, LeafKind.Pistes) is { } sec ? LeafPistes.Decode(sec) : null;
            foreach (var rec in p?.Records ?? []) cubeIds.TryAdd(rec.Id, 0);
            int n = tile.Side;
            for (int j = 0; j < n; j += c.Stride)
                for (int i = 0; i < n; i += c.Stride)
                {
                    var (e, nn) = c.Cube.Lv95Of(key, tile, i, j);
                    if (!c.Lv.Covers(e, nn)) continue;
                    var lvRec = c.Lv.PisteAt(e, nn, lvIds);
                    int slot = p?.Slot[j * n + i] ?? 0;
                    var cubeRec = slot > 0 ? p!.Records[slot - 1] : null;
                    acc.Add(lvRec == null ? 0 : (int)lvRec.Type, cubeRec == null ? 0 : (int)cubeRec.Type);
                }
            return acc;
        }, acc => { lock (agree) agree.Merge(acc); });
        CategoryLines(r, agree, k => k == 0 ? "no slope" : ((PisteType)k).ToString());
        long both = lvIds.Keys.Count(cubeIds.ContainsKey);
        r.Note($"slopes by OSM id: {lvIds.Count} LV95, {cubeIds.Count} leaves, {both} in both");
        r.AtMost("LV95 slopes missing from the leaves", Density.Relative(lvIds.Count, both), 0.0, "P2");
        return r;
    }

    // ---- lattice features: holes as quad centres, each side's nearest on the other ----------------

    private static KindReport Holes(Ctx c)
    {
        if (Missing(c, "holes", LeafKind.Holes, "") is { } skip) return skip;
        var r = new KindReport("holes");
        var toLv = new Tally();
        var toCube = new Tally();
        long lvCount = 0, cubeCount = 0, lvAlone = 0, cubeAlone = 0;
        foreach (var bundle in c.Cube.Bundles)
        {
            var frame = bundle.Anchor;
            var cubeQuads = new ConcurrentBag<Feature>();
            Parallel.ForEach(c.Cube.LeavesIn(bundle), c.Par, key =>
            {
                if (c.Cube.Section(key, LeafKind.Holes) is not { } sec || c.Cube.Heights(key) is not { } tile) return;
                int bits = tile.Bits;
                foreach (int q in LeafHoles.Decode(sec))
                {
                    int i = q & ((1 << bits) - 1), j = q >> bits;
                    double h = (tile.HeightAt(i, j) + tile.HeightAt(i + 1, j) + tile.HeightAt(i, j + 1) + tile.HeightAt(i + 1, j + 1)) / 4;
                    var p = key.Vertex(bits + 1, 2 * i + 1, 2 * j + 1, double.IsNaN(h) ? 0 : h);
                    if (!c.Lv.Covers(p)) continue;
                    var (x, y, z) = frame.ToLocal(p);
                    cubeQuads.Add(new Feature(x, y, z));
                }
            });
            var lvQuads = new ConcurrentBag<(Feature F, bool Owned)>();
            Parallel.ForEach(c.Lv.TilesNear(c.Cube.Lv95Box(bundle)), c.Par, id =>
            {
                foreach (int cell in c.Lv.Holes(id))
                {
                    int col = cell % HoleFormat.QuadsPerSide, row = cell / HoleFormat.QuadsPerSide;
                    double e = id.MinE + col + 0.5, n = id.MaxN - row - 0.5;
                    var p = SwissGeodesy.ToEcef(e, n, c.Lv.Ground(e, n) is var g && double.IsNaN(g) ? 0 : g);
                    var (x, y, z) = frame.ToLocal(p);
                    lvQuads.Add((new Feature(x, y, z), c.Cube.Owns(bundle, p)));
                }
            });
            var lvAll = lvQuads.Select(q => q.F).ToList();
            var cubeAll = cubeQuads.ToList();
            var lvIndex = Index(lvAll);
            var cubeIndex = Index(cubeAll);
            foreach (var f in cubeAll)
            {
                cubeCount++;
                if (NearestDistance(lvIndex, lvAll, f, 5) is { } d) toLv.Add(d); else cubeAlone++;
            }
            foreach (var (f, owned) in lvQuads)
            {
                if (!owned) continue;
                lvCount++;
                if (NearestDistance(cubeIndex, cubeAll, f, 5) is { } d) toCube.Add(d); else lvAlone++;
            }
        }
        r.Note($"hole quads: {lvCount} LV95 (1 m), {cubeCount} leaves; {lvAlone} LV95 and {cubeAlone} leaf quads with none of the other build's within 5 m");
        r.Check("leaf quad to the nearest LV95 one", toLv.Spread(), false, new Bar(Stat.Max, CompareBars.Quad));
        r.Check("LV95 quad to the nearest leaf one", toCube.Spread(), false, new Bar(Stat.Max, CompareBars.Quad));
        r.AtMost("quads alone", lvAlone + cubeAlone, 0, "F0");
        return r;
    }

    private static PlaneHash Index(List<Feature> f)
    {
        var h = new PlaneHash(5);
        for (int i = 0; i < f.Count; i++) h.Add(i, f[i].X, f[i].Z);
        return h;
    }

    private static double? NearestDistance(PlaneHash hash, List<Feature> pts, Feature f, double radius)
    {
        double best = double.PositiveInfinity;
        foreach (int k in hash.Near(f.X, f.Z, radius))
        {
            double dx = pts[k].X - f.X, dz = pts[k].Z - f.Z;
            best = Math.Min(best, Math.Sqrt(dx * dx + dz * dz));
        }
        return best <= radius ? best : null;
    }

    // ---- roads: centrelines vertex to line both ways, lengths, junctions and signals ----------------

    private static KindReport Roads(Ctx c)
    {
        if (Missing(c, "roads", LeafKind.Roads, "roads_") is { } skip) return skip;
        var r = new KindReport("roads");
        var cubeToLv = new Tally();
        var lvToCube = new Tally();
        var aboveCube = new Tally();
        var lenLv = new Dictionary<int, double>();
        var lenCube = new Dictionary<int, double>();
        var junctions = (Lv: new List<Feature>(), Cube: new List<Feature>());
        var signals = (Lv: new List<Feature>(), Cube: new List<Feature>());
        long cubeAlone = 0, lvAlone = 0;
        foreach (var bundle in c.Cube.Bundles)
        {
            var frame = bundle.Anchor;
            var cubeLines = new ConcurrentBag<(Line Line, CubeKey Leaf)>();
            var lvLines = new ConcurrentBag<(Line Line, bool[] Owned, TileId Tile)>();
            var bag = (LvJ: new ConcurrentBag<Feature>(), CJ: new ConcurrentBag<Feature>(), LvS: new ConcurrentBag<Feature>(), CS: new ConcurrentBag<Feature>());
            Parallel.ForEach(c.Cube.LeavesIn(bundle), c.Par, key =>
            {
                if (c.Cube.Section(key, LeafKind.Roads) is not { } sec) return;
                var tile = LeafRoads.Decode(sec);
                var anchor = key.Anchor;
                foreach (var s in tile.Segments)
                {
                    var (pts, above) = ToFrame(s.Points, 3, i => anchor.ToEcef(s.Points[i], s.Points[i + 1], s.Points[i + 2]), frame, c.Cube.AboveGround);
                    cubeLines.Add((new Line(pts, (int)s.Class, above), key));
                }
                foreach (var j in tile.Junctions)
                    if (Centroid(j.Vertices, i => anchor.ToEcef(j.Vertices[i], j.Vertices[i + 1], j.Vertices[i + 2])) is { } p && c.Lv.Covers(p))
                        bag.CJ.Add(At(frame, p, (int)j.Class));
                foreach (var sig in tile.Signals)
                    if (anchor.ToEcef(sig.X, sig.Y, sig.Z) is var p && c.Lv.Covers(p)) bag.CS.Add(At(frame, p, 0));
            });
            Parallel.ForEach(c.Lv.TilesNear(c.Cube.Lv95Box(bundle)), c.Par, id =>
            {
                if (c.Lv.Roads(id) is not { } tile) return;
                foreach (var s in tile.Segments)
                {
                    var (pts, above) = ToFrame(s.Points, 3, i => Lv95Point(id, s.Points[i], s.Points[i + 1], s.Points[i + 2]), frame,
                        p => c.Lv.AboveGround(p));
                    var owned = new bool[s.PointCount];
                    for (int k = 0; k < owned.Length; k++) owned[k] = c.Cube.Owns(bundle, Lv95Point(id, s.Points[k * 3], s.Points[k * 3 + 1], s.Points[k * 3 + 2]));
                    lvLines.Add((new Line(pts, (int)s.Class, above), owned, id));
                }
                foreach (var j in tile.Junctions)
                    if (Centroid(j.Vertices, i => Lv95Point(id, j.Vertices[i], j.Vertices[i + 1], j.Vertices[i + 2])) is { } p && c.Cube.Owns(bundle, p))
                        bag.LvJ.Add(At(frame, p, (int)j.Class));
                foreach (var sig in tile.Signals)
                    if (Lv95Point(id, sig.X, sig.Y, sig.Z) is var p && c.Cube.Owns(bundle, p)) bag.LvS.Add(At(frame, p, 0));
            });
            junctions.Lv.AddRange(bag.LvJ); junctions.Cube.AddRange(bag.CJ);
            signals.Lv.AddRange(bag.LvS); signals.Cube.AddRange(bag.CS);

            var lvIndex = new LineIndex(lvLines.Select(l => l.Line).ToList());
            var cubeIndex = new LineIndex(cubeLines.Select(l => l.Line).ToList());
            foreach (var (line, leaf) in cubeLines)
            {
                var p = line.Points;
                for (int k = 0; k < line.Count; k++)
                {
                    var ecef = frame.ToEcef(p[k * 3], p[k * 3 + 1], p[k * 3 + 2]);
                    if (!c.Lv.Covers(ecef)) continue;
                    if (lvIndex.Nearest(p[k * 3], p[k * 3 + 1], p[k * 3 + 2], line.Above![k], line.Class, 5, out double h, out _, out double da))
                    {
                        cubeToLv.Add(h);
                        if (!double.IsNaN(da)) aboveCube.Add(da);
                    }
                    else cubeAlone++;
                }
                Accumulate(lenCube, line, (x, y, z) => frame.ToEcef(x, y, z) is var m && c.Cube.Owner(m) == leaf && c.Lv.Covers(m));
            }
            foreach (var (line, owned, id) in lvLines)
            {
                var p = line.Points;
                for (int k = 0; k < line.Count; k++)
                {
                    if (!owned[k]) continue;
                    if (cubeIndex.Nearest(p[k * 3], p[k * 3 + 1], p[k * 3 + 2], line.Above![k], line.Class, 5, out double h, out _, out _)) lvToCube.Add(h);
                    else lvAlone++;
                }
                Accumulate(lenLv, line, (x, y, z) =>
                {
                    var m = frame.ToEcef(x, y, z);
                    var (e, n, _) = SwissGeodesy.FromEcef(m);
                    return TileId.FromLv95(e, n) == id && c.Cube.Owns(bundle, m);
                });
            }
        }
        r.Note($"centreline vertices: {cubeToLv.Count + cubeAlone} leaf, {lvToCube.Count + lvAlone} LV95; alone (no line of their class within 5 m): {cubeAlone} leaf, {lvAlone} LV95");
        r.Check("leaf vertex to the LV95 line", cubeToLv.Spread(), true, new Bar(Stat.P99, CompareBars.Vertex));
        r.Check("LV95 vertex to the leaf line", lvToCube.Spread(), true, new Bar(Stat.P99, CompareBars.Vertex));
        r.Check("height above ground, leaf − LV95", aboveCube.Spread(), true, new Bar(Stat.P99, CompareBars.Vertex));
        foreach (int k in lenLv.Keys.Union(lenCube.Keys).Order())
        {
            double a = lenLv.GetValueOrDefault(k), b = lenCube.GetValueOrDefault(k);
            string what = string.Create(CultureInfo.InvariantCulture, $"length {(RoadClass)k} ({a / 1000:F2} km LV95, {b / 1000:F2} km leaves), relative difference");
            // LV95 measures grid metres (80-190 ppm long), the leaves true ones: the bar allows the plan's 2e-4
            if (a > 1000) r.AtMost(what, Density.Relative((long)Math.Round(a), (long)Math.Round(b)), CompareBars.PerDistance + 1e-4, "P3");
            else r.Note(string.Create(CultureInfo.InvariantCulture, $"length {(RoadClass)k}: {a:F0} m LV95, {b:F0} m leaves (too short for the bar)"));
        }
        MatchPoints(r, "junction", junctions.Lv, junctions.Cube, 5, true);
        MatchPoints(r, "signal", signals.Lv, signals.Cube, 5, false);
        return r;
    }

    private static void Accumulate(Dictionary<int, double> len, Line line, Func<double, double, double, bool> keep)
    {
        double l = LineIndex.Length(line, keep);
        if (l > 0) len[line.Class] = len.GetValueOrDefault(line.Class) + l;
    }

    /// <summary>Count and positions of a point kind: matched, alone each side, plan distance and the vertical (above ground where draped).</summary>
    private static void MatchPoints(KindReport r, string what, List<Feature> lv, List<Feature> cube, double radius, bool byClass)
    {
        var m = FeatureMatch.Match(lv, cube, radius, byClass);
        var h = new Tally();
        var v = new Tally();
        foreach (var (a, b, _) in m.Pairs)
        {
            var (dh, dv) = FeatureMatch.Offset(lv[a], cube[b]);
            h.Add(dh);
            v.Add(dv);
        }
        r.Note($"{what}s: {lv.Count} LV95, {cube.Count} leaves, {m.Pairs.Count} matched ({m.Pairs.Count(p => p.ById)} by id), {m.OnlyA.Count} LV95 and {m.OnlyB.Count} leaf alone");
        if (lv.Count + cube.Count == 0) return;
        r.Check($"{what} plan distance", h.Spread(), true, new Bar(Stat.P99, CompareBars.Vertex));
        r.Check($"{what} vertical (above ground where draped)", v.Spread(), true, new Bar(Stat.P99, CompareBars.Vertex));
        r.AtMost($"{what}s alone", (m.OnlyA.Count + m.OnlyB.Count) / (double)Math.Max(1, Math.Max(lv.Count, cube.Count)), 0.005, "P2");
    }

    // ---- buildings: by EGID, else by nearest; their vertices pair by pair -------------------------

    private static KindReport Buildings(Ctx c)
    {
        if (Missing(c, "buildings", LeafKind.Buildings, "buildings_") is { } skip) return skip;
        var r = new KindReport("buildings");
        var h = new Tally();
        var v = new Tally();
        var seat = new Tally();
        var onSeat = new Tally();
        long lvTotal = 0, cubeTotal = 0, byId = 0, byNearest = 0, lvAlone = 0, cubeAlone = 0, lvWithId = 0, lvIdFound = 0;
        foreach (var bundle in c.Cube.Bundles)
        {
            var frame = bundle.Anchor;
            var cube = new ConcurrentBag<(Feature F, double[] Pts)>();
            var lv = new ConcurrentBag<(Feature F, double[] Pts, bool Owned, (double E, double N)[] Seat)>();
            Parallel.ForEach(c.Cube.LeavesIn(bundle), c.Par, key =>
            {
                if (c.Cube.Section(key, LeafKind.Buildings) is not { } sec) return;
                var anchor = key.Anchor;
                foreach (var b in LeafBuildings.Decode(sec).Buildings)
                    if (BuildingIn(b, frame, i => anchor.ToEcef(b.Triangles[i], b.Triangles[i + 1], b.Triangles[i + 2])) is { } x && c.Lv.Covers(frame.ToEcef(x.F.X, x.F.Y, x.F.Z)))
                        cube.Add(x);
            });
            Parallel.ForEach(c.Lv.TilesNear(c.Cube.Lv95Box(bundle)), c.Par, id =>
            {
                if (c.Lv.Buildings(id) is not { } tile) return;
                foreach (var b in tile.Buildings)
                    if (BuildingIn(b, frame, i => Lv95Point(id, b.Triangles[i], b.Triangles[i + 1], b.Triangles[i + 2])) is { } x)
                        lv.Add((x.F, x.Pts, c.Cube.Owns(bundle, frame.ToEcef(x.F.X, x.F.Y, x.F.Z)), SeatSamples(id, b)));
            });
            var lvList = lv.ToList();
            var cubeList = cube.ToList();
            var m = FeatureMatch.Match(lvList.Select(x => x.F).ToList(), cubeList.Select(x => x.F).ToList(), 5, byClass: false);
            var cubeOfLv = m.Pairs.ToDictionary(p => p.A, p => p.B);
            foreach (var (a, b, id) in m.Pairs)
            {
                if (!lvList[a].Owned) continue;
                if (id) byId++; else byNearest++;
                // each build seats a building on its own ground (#1507): the difference of the two seats (the
                // median ground at the 3 x 3 re-seat samples), and the vertices' vertical offset less it
                double dg = SeatDifference(c, lvList[a].Seat);
                if (double.IsNaN(dg)) VertexOffsets(lvList[a].Pts, cubeList[b].Pts, h, v);
                else
                {
                    seat.Add(dg);
                    VertexOffsets(lvList[a].Pts, cubeList[b].Pts, h, v, onSeat, dg);
                }
            }
            for (int a = 0; a < lvList.Count; a++)
            {
                if (!lvList[a].Owned) continue;
                lvTotal++;
                if (lvList[a].F.Id != 0) { lvWithId++; if (cubeOfLv.TryGetValue(a, out int b) && cubeList[b].F.Id == lvList[a].F.Id) lvIdFound++; }
                if (!cubeOfLv.ContainsKey(a)) lvAlone++;
            }
            var lvOwnedMatched = m.Pairs.Where(p => !lvList[p.A].Owned).Select(p => p.B).ToHashSet();
            cubeTotal += cubeList.Count - lvOwnedMatched.Count;
            cubeAlone += m.OnlyB.Count;
        }
        r.Note($"{lvTotal} LV95 buildings, {cubeTotal} leaf ones; {byId} matched by EGID, {byNearest} by nearest (5 m); {lvAlone} LV95 and {cubeAlone} leaf alone");
        r.AtLeast($"LV95 buildings with an EGID found by it ({lvWithId})", lvWithId == 0 ? 1 : lvIdFound / (double)lvWithId, CompareBars.ClassAgreement);
        r.AtMost("buildings alone", (lvAlone + cubeAlone) / (double)Math.Max(1, Math.Max(lvTotal, cubeTotal)), 1 - CompareBars.ClassAgreement, "P2");
        r.Check("vertex plan distance", h.Spread(), true, new Bar(Stat.P99, CompareBars.Vertex));
        // decided with the user (#1507): a building sits on its own build's ground, and the grounds differ by the
        // heights stage's own differences, so the 2 cm holds on each vertex's height above its seat; the absolute
        // height is held to the heights' bars
        r.Check("vertex vertical", v.Spread(), true, new Bar(Stat.Mean, CompareBars.HeightBias), new Bar(Stat.P50, CompareBars.HeightMedian));
        r.Check("the seats (median ground at the re-seat samples, leaf mesh less .terr)", seat.Spread(), true);
        r.Check("vertex vertical above each build's seat", onSeat.Spread(), true, new Bar(Stat.P99, CompareBars.Vertex));
        return r;
    }

    /// <summary>
    /// Where the stage sampled an LV95 building's seat: a Swiss one at the 3 x 3 grid over its footprint box
    /// (<c>BuildingExtractor</c>, the box of its vertices: its ring corners), a French one (<c>YearBuilt</c> 1,
    /// <c>FranceStage.FrenchMarker</c>) at its footprint's corners (its lowest vertices) and their mean
    /// (<c>FranceBuildings</c>).
    /// </summary>
    private static (double E, double N)[] SeatSamples(TileId id, Building b)
    {
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue, low = double.MaxValue;
        for (int i = 0; i + 2 < b.Triangles.Length; i += 3)
        {
            double e = id.MinE + b.Triangles[i], n = id.MaxN - b.Triangles[i + 2];
            minE = Math.Min(minE, e); maxE = Math.Max(maxE, e);
            minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
            low = Math.Min(low, b.Triangles[i + 1]);
        }
        if (b.YearBuilt == 1)
        {
            var ring = new HashSet<(double, double)>();
            for (int i = 0; i + 2 < b.Triangles.Length; i += 3)
                if (b.Triangles[i + 1] == low) ring.Add((id.MinE + b.Triangles[i], id.MaxN - b.Triangles[i + 2]));
            var list = ring.ToList();
            list.Add((list.Average(p => p.Item1), list.Average(p => p.Item2)));
            return list.ToArray();
        }
        var grid = new (double, double)[9];
        for (int gy = 0; gy <= 2; gy++)
            for (int gx = 0; gx <= 2; gx++)
                grid[gy * 3 + gx] = (minE + (maxE - minE) * gx / 2.0, minN + (maxN - minN) * gy / 2.0);
        return grid;
    }

    /// <summary>
    /// The leaf seat less the LV95 one, each the median of its own ground (heights above sea level) at the samples
    /// where it has one, as each build took it; NaN where either has none.
    /// </summary>
    private static double SeatDifference(Ctx c, (double E, double N)[] samples)
    {
        var lv = new List<double>(samples.Length);
        var cube = new List<double>(samples.Length);
        foreach (var (e, n) in samples)
        {
            double g = c.Lv.Ground(e, n);
            if (!double.IsNaN(g)) lv.Add(g);
            // the leaf mesh below the point as a height above sea level: a point H above sea level is H less
            // its height above the mesh above it
            const double H = 1000;
            double above = c.Cube.AboveGround(SwissGeodesy.ToEcef(e, n, H));
            if (!double.IsNaN(above)) cube.Add(H - above);
        }
        if (lv.Count == 0 || cube.Count == 0) return double.NaN;
        lv.Sort();
        cube.Sort();
        return cube[cube.Count / 2] - lv[lv.Count / 2];
    }

    /// <summary>A building in the frame: its centroid as a feature (EGID as the id, kind as the class) and every vertex.</summary>
    private static (Feature F, double[] Pts)? BuildingIn(Building b, EnuFrame frame, Func<int, EcefPos> at)
    {
        if (b.Triangles.Length < 9) return null;
        var pts = new double[b.Triangles.Length];
        double sx = 0, sy = 0, sz = 0;
        for (int i = 0; i + 2 < b.Triangles.Length; i += 3)
        {
            var (x, y, z) = frame.ToLocal(at(i));
            pts[i] = x; pts[i + 1] = y; pts[i + 2] = z;
            sx += x; sy += y; sz += z;
        }
        int n = b.Triangles.Length / 3;
        return (new Feature(sx / n, sy / n, sz / n, b.Egid, (int)b.Kind), pts);
    }

    /// <summary>Vertex k against vertex k when both builds kept the same triangles, else each leaf vertex against the nearest LV95 one.</summary>
    private static void VertexOffsets(double[] a, double[] b, Tally h, Tally v, Tally? less = null, double ground = 0)
    {
        bool same = a.Length == b.Length;
        for (int k = 0; k + 2 < b.Length; k += 3)
        {
            int best = k;
            if (!same)
            {
                double bd = double.PositiveInfinity;
                for (int q = 0; q + 2 < a.Length; q += 3)
                {
                    double dx = a[q] - b[k], dy = a[q + 1] - b[k + 1], dz = a[q + 2] - b[k + 2], d = dx * dx + dy * dy + dz * dz;
                    if (d < bd) { bd = d; best = q; }
                }
            }
            double ex = b[k] - a[best], ez = b[k + 2] - a[best + 2];
            h.Add(Math.Sqrt(ex * ex + ez * ez));
            v.Add(b[k + 1] - a[best + 1]);
            less?.Add(b[k + 1] - a[best + 1] - ground);
        }
    }

    // ---- trees: surveyed ones matched, the scatter and the planted rows counted ---------------------

    private static KindReport Trees(Ctx c)
    {
        if (Missing(c, "trees", LeafKind.Trees, "trees_") is { } skip) return skip;
        var r = new KindReport("trees");
        var lvCount = new long[256];
        var cubeCount = new long[256];
        var lvSurveyed = new List<Feature>();
        var cubeSurveyed = new List<Feature>();
        const byte Surveyed = 3;
        foreach (var bundle in c.Cube.Bundles)
        {
            var frame = bundle.Anchor;
            var lvBag = new ConcurrentBag<Feature>();
            var cubeBag = new ConcurrentBag<Feature>();
            Parallel.ForEach(c.Cube.LeavesIn(bundle), c.Par, key =>
            {
                if (c.Cube.Section(key, LeafKind.Trees) is not { } sec) return;
                var anchor = key.Anchor;
                var counts = new long[256];
                foreach (var t in LeafTrees.Decode(sec))
                {
                    var p = anchor.ToEcef(t.X, t.Y, t.Z);
                    if (!c.Lv.Covers(p)) continue;
                    counts[t.Kind]++;
                    if (t.Kind == Surveyed) cubeBag.Add(At(frame, p, 0, c.Cube.AboveGround(p)));
                }
                lock (cubeCount) for (int k = 0; k < 256; k++) cubeCount[k] += counts[k];
            });
            Parallel.ForEach(c.Lv.TilesNear(c.Cube.Lv95Box(bundle)), c.Par, id =>
            {
                var counts = new long[256];
                foreach (var t in c.Lv.Trees(id))
                {
                    var p = Lv95Point(id, t.X, t.Y, t.Z);
                    if (!c.Cube.Owns(bundle, p)) continue;
                    counts[t.Kind]++;
                    if (t.Kind == Surveyed) lvBag.Add(At(frame, p, 0, c.Lv.AboveGround(p)));
                }
                lock (lvCount) for (int k = 0; k < 256; k++) lvCount[k] += counts[k];
            });
            // matched within the bundle; a surveyed tree on a bundle edge has its twin in the same bundle to the centimetre
            var lvB = lvBag.ToList();
            var cubeB = cubeBag.ToList();
            lvSurveyed.AddRange(lvB);
            cubeSurveyed.AddRange(cubeB);
        }
        for (int k = 0; k < 256; k++)
        {
            if (lvCount[k] + cubeCount[k] == 0 || k == Surveyed) continue;
            string what = $"kind {k} ({lvCount[k]} LV95, {cubeCount[k]} leaves), relative difference";
            if (lvCount[k] >= 1000) r.AtMost(what, Density.Relative(lvCount[k], cubeCount[k]), CompareBars.Density, "P2");
            else r.Note(what + ": too few for the bar");
        }
        MatchPoints(r, "surveyed tree", lvSurveyed, cubeSurveyed, 1, false);
        return r;
    }

    // ---- fields: outlines by id, each leaf ring vertex against the LV95 outline ----------------------

    private static KindReport Fields(Ctx c)
    {
        if (Missing(c, "fields", LeafKind.Fields, "fields_") is { } skip) return skip;
        var r = new KindReport("fields");
        var h = new Tally();
        // a field is written whole into every tile and leaf it touches: each id once a side; rings on each build's ground
        var lv = new ConcurrentDictionary<uint, EcefPos[][]>();
        var cube = new ConcurrentDictionary<uint, EcefPos[][]>();
        Parallel.ForEach(c.Cube.Ordered(), c.Par, key =>
        {
            if (c.Cube.Section(key, LeafKind.Fields) is not { } sec) return;
            var anchor = key.Anchor;
            foreach (var f in LeafFields.Decode(sec))
                if (!cube.ContainsKey(f.Id))
                    cube.TryAdd(f.Id, f.Rings.Select(ring => Ring(ring, (x, z) =>
                    {
                        // the plan point on the leaf's ground: its height above the ellipsoid is the anchor's y there to ~1 cm
                        var p0 = anchor.ToEcef(x, 0, z);
                        double g = -c.Cube.AboveGround(p0);
                        return anchor.ToEcef(x, double.IsNaN(g) ? 0 : g, z);
                    })).ToArray());
        });
        Parallel.ForEach(c.Lv.TilesNear(c.Cube.Lv95BoxAll()), c.Par, id =>
        {
            foreach (var f in c.Lv.Fields(id))
                if (!lv.ContainsKey(f.Id))
                    lv.TryAdd(f.Id, f.Rings.Select(ring => Ring(ring, (x, n) =>
                        SwissGeodesy.ToEcef(id.MinE + x, id.MinN + n, c.Lv.Ground(id.MinE + x, id.MinN + n) is var g && double.IsNaN(g) ? 0 : g))).ToArray());
        });
        long both = 0;
        foreach (var (id, rings) in cube)
        {
            if (!lv.TryGetValue(id, out var lvRings) || rings.Length == 0 || rings[0].Length == 0) continue;
            both++;
            var frame = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(rings[0][0]));
            var index = new LineIndex(lvRings.Select(ring => new Line(InFrame(ring, frame), 0)).ToList());
            foreach (var ring in rings)
                foreach (var p in ring)
                {
                    var (x, y, z) = frame.ToLocal(p);
                    if (index.Nearest(x, y, z, double.NaN, 0, 50, out double d, out _, out _)) h.Add(d);
                }
        }
        r.Note($"fields by id: {lv.Count} LV95 (over the leaves' box), {cube.Count} leaves, {both} in both");
        r.Check("leaf ring vertex to the LV95 outline", h.Spread(), true, new Bar(Stat.P99, CompareBars.Vertex));
        return r;
    }

    /// <summary>A plan ring (interleaved pairs) on the Earth, closed.</summary>
    private static EcefPos[] Ring(float[] ring, Func<double, double, EcefPos> at)
    {
        int n = ring.Length / 2;
        if (n == 0) return [];
        var pts = new EcefPos[n + 1];
        for (int k = 0; k <= n; k++) pts[k] = at(ring[k % n * 2], ring[k % n * 2 + 1]);
        return pts;
    }

    private static double[] InFrame(EcefPos[] pts, EnuFrame frame)
    {
        var a = new double[pts.Length * 3];
        for (int k = 0; k < pts.Length; k++) (a[k * 3], a[k * 3 + 1], a[k * 3 + 2]) = frame.ToLocal(pts[k]);
        return a;
    }

    // ---- shared helpers ---------------------------------------------------------------------------

    /// <summary>An LV95 tile-local point (x east, y altitude, z south) on the Earth.</summary>
    private static EcefPos Lv95Point(TileId id, double x, double y, double z) => SwissGeodesy.ToEcef(id.MinE + x, id.MaxN - z, y);

    private static Feature At(EnuFrame frame, EcefPos p, int cls, double above = double.NaN)
    {
        var (x, y, z) = frame.ToLocal(p);
        return new Feature(x, y, z, 0, cls, above);
    }

    private static EcefPos? Centroid(float[] v, Func<int, EcefPos> at)
    {
        if (v.Length < 3) return null;
        double x = 0, y = 0, z = 0;
        int n = v.Length / 3;
        for (int i = 0; i < n; i++) { var p = at(i * 3); x += p.X; y += p.Y; z += p.Z; }
        return new EcefPos(x / n, y / n, z / n);
    }

    /// <summary>Points (stride <paramref name="dim"/> in <paramref name="src"/>) into the frame, with each one's height above its build's ground.</summary>
    private static (double[] Pts, double[] Above) ToFrame(float[] src, int dim, Func<int, EcefPos> at, EnuFrame frame, Func<EcefPos, double> above)
    {
        int n = src.Length / dim;
        var pts = new double[n * 3];
        var ab = new double[n];
        for (int k = 0; k < n; k++)
        {
            var p = at(k * dim);
            var (x, y, z) = frame.ToLocal(p);
            pts[k * 3] = x; pts[k * 3 + 1] = y; pts[k * 3 + 2] = z;
            ab[k] = above(p);
        }
        return (pts, ab);
    }
}

/// <summary>The LV95 build's tiles, decoded on demand and kept in small caches (a region does not fit in memory).</summary>
internal sealed class Lv95Build
{
    private readonly string _dir;
    private readonly HashSet<string> _files;
    public HashSet<TileId> Tiles { get; }
    private readonly Lru<TileId, ChunkGrid?> _terr;
    private readonly Lru<TileId, byte[]?> _cover;
    private readonly Lru<TileId, ushort[]?> _water;
    private readonly Lru<TileId, PisteTile?> _pistes;

    public Lv95Build(string dir)
    {
        _dir = dir;
        _files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().ToHashSet() : new();
        Tiles = _files.Where(f => f.StartsWith("chunk_") && f.EndsWith(".terr"))
            .Select(f => f["chunk_".Length..^".terr".Length].Split('_'))
            .Select(p => new TileId(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture))).ToHashSet();
        _terr = new(400, id => Read(ChunkFormat.ChunkFileName(id), s => ChunkCodec.Decode(s)));
        _cover = new(200, id => Read(CoverFormat.FileName(id), CoverFormat.Decode));
        _water = new(200, id => Read(WaterFormat.FileName(id), s => WaterFormat.Decode(s).Levels));
        _pistes = new(100, id => Read(PisteFormat.FileName(id), PisteFormat.Decode));
    }

    public bool Has(string prefix) => _files.Any(f => f.StartsWith(prefix, StringComparison.Ordinal));

    private T? Read<T>(string name, Func<Stream, T> decode) where T : class
    {
        if (!_files.Contains(name)) return null;
        using var fs = File.OpenRead(Path.Combine(_dir, name));
        return decode(fs);
    }

    // each thread keeps the last tile of each kind it asked for: a leaf's vertices fall on one to four tiles,
    // and the shared caches' lock taken per vertex cost more than the rest of the comparison
    [ThreadStatic] private static Last<TileId, ChunkGrid?>? _lastTerr;
    [ThreadStatic] private static Last<TileId, byte[]?>? _lastCover;
    [ThreadStatic] private static Last<TileId, ushort[]?>? _lastWater;

    /// <summary>The <c>.terr</c> height at an LV95 point, bilinear (the build's draping), NaN off the tiles.</summary>
    public double Ground(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        return Tiles.Contains(id) && (_lastTerr ??= new()).Get(id, _terr) is { } g ? g.SampleHeight(e, n) : double.NaN;
    }

    public bool Covers(double e, double n) => Tiles.Contains(TileId.FromLv95(e, n));

    public bool Covers(EcefPos p)
    {
        var (e, n, _) = SwissGeodesy.FromEcef(p);
        return Covers(e, n);
    }

    /// <summary>A point's altitude above the <c>.terr</c> below it, NaN off the tiles.</summary>
    public double AboveGround(EcefPos p)
    {
        var (e, n, h) = SwissGeodesy.FromEcef(p);
        return h - Ground(e, n);
    }

    private static (int I, bool Ok) Vertex(TileId id, double e, double n)
    {
        int col = (int)Math.Round(e - id.MinE), row = (int)Math.Round(id.MaxN - n);
        bool ok = (uint)col < ChunkFormat.GridSize && (uint)row < ChunkFormat.GridSize;
        return (row * ChunkFormat.GridSize + col, ok);
    }

    /// <summary>The cover class of the nearest 1 m vertex; −1 off the tiles; a tile with no file is all <see cref="CoverClass"/> 0.</summary>
    public int CoverAt(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        if (!Tiles.Contains(id)) return -1;
        var (i, ok) = Vertex(id, e, n);
        return !ok ? -1 : (_lastCover ??= new()).Get(id, _cover) is { } cells ? cells[i] : 0;
    }

    /// <summary>
    /// Whether a bilinear <see cref="Ground"/> sample there takes a bed vertex (#298: the <c>.terr</c> holds the
    /// bed under water): one of its four corners has a still level or <see cref="CoverClass.Water"/>, which the
    /// level raster misses at some shores.
    /// </summary>
    public bool UnderWater(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        if (!Tiles.Contains(id)) return false;
        var levels = (_lastWater ??= new()).Get(id, _water);
        var cover = (_lastCover ??= new()).Get(id, _cover);
        if (levels == null && cover == null) return false;
        int c0 = Math.Clamp((int)Math.Floor(e - id.MinE), 0, ChunkFormat.GridSize - 2), r0 = Math.Clamp((int)Math.Floor(id.MaxN - n), 0, ChunkFormat.GridSize - 2);
        for (int dr = 0; dr <= 1; dr++)
            for (int dc = 0; dc <= 1; dc++)
            {
                int k = (r0 + dr) * ChunkFormat.GridSize + c0 + dc;
                if ((levels != null && levels[k] != 0) || (cover != null && cover[k] == (byte)CoverClass.Water)) return true;
            }
        return false;
    }

    /// <summary>The still level (LHN95) at the nearest 1 m vertex, NaN dry; null off the tiles.</summary>
    public double? WaterAt(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        if (!Tiles.Contains(id)) return null;
        var (i, ok) = Vertex(id, e, n);
        if (!ok) return null;
        ushort q = (_lastWater ??= new()).Get(id, _water) is { } levels ? levels[i] : (ushort)0;
        return q == 0 ? double.NaN : ChunkFormat.Dequantize(q);
    }

    /// <summary>The slope at the nearest 1 m vertex (null none); every slope seen goes into <paramref name="ids"/>.</summary>
    public PisteRecord? PisteAt(double e, double n, ConcurrentDictionary<ulong, byte> ids)
    {
        var id = TileId.FromLv95(e, n);
        if (_pistes.Get(id) is not { } t) return null;
        foreach (var r in t.Records) ids.TryAdd(r.Id, 0);
        return t.At(e, n);
    }

    public IEnumerable<TileId> TilesNear((double MinE, double MinN, double MaxE, double MaxN) box) =>
        Tiles.Where(t => t.Touches(box.MinE, box.MinN, box.MaxE, box.MaxN)).OrderBy(t => t.N).ThenBy(t => t.E);

    public HashSet<int> Holes(TileId id) => Read(HoleFormat.FileName(id), HoleFormat.Decode) ?? [];

    public RoadTile? Roads(TileId id) => Read(RoadFormat.FileName(id), RoadCodec.Decode);

    public BuildingTile? Buildings(TileId id) => Read(BuildingFormat.FileName(id), BuildingCodec.Decode);

    public List<TreeInstance> Trees(TileId id) => Read(TreeFormat.FileName(id), TreeFormat.Decode) ?? [];

    public List<FieldPolygon> Fields(TileId id) => Read(FieldFormat.FileName(id), FieldFormat.Decode) ?? [];
}

/// <summary>A cube region's leaves: <c>.cubl</c> entries (every kind a section) and G1's <c>.cubb</c> tiles (heights alone).</summary>
internal sealed class CubeBuild
{
    public const int LeafLevel = 14;

    private sealed record Entry(string Path, long Offset, int Length, bool IsLeaf, LeafKind[] Kinds);

    private readonly Dictionary<CubeKey, Entry> _entries = new();
    public IReadOnlyCollection<CubeKey> Leaves => _entries.Keys;
    public HashSet<LeafKind> KindsPresent { get; } = new();
    public List<CubeKey> Bundles { get; }
    private readonly Lru<CubeKey, CubeTile?> _heights;
    private readonly ConcurrentDictionary<CubeKey, (double, double, double, double)> _boxes = new();

    public CubeBuild(string dir, int maxLeaves)
    {
        // .cubl first: a leaf with content wins over G1's heights-only tile of the same key
        foreach (var file in Directory.EnumerateFiles(dir, "*.cubl").Order().Concat(Directory.EnumerateFiles(dir, "*.cubb").Order()))
        {
            bool leaf = file.EndsWith(".cubl", StringComparison.Ordinal);
            using var fs = File.OpenRead(file);
            foreach (var (key, (offset, length)) in CubeBundle.ReadIndex(fs, leaf ? CubeBundle.LeafMagic : CubeBundle.Magic).OrderBy(e => e.Value.Offset))
            {
                if (key.Level != LeafLevel || _entries.ContainsKey(key)) continue;
                LeafKind[] kinds = leaf ? ReadKinds(fs, offset) : [LeafKind.Heights];
                _entries[key] = new Entry(file, offset, length, leaf, kinds);
            }
        }
        if (_entries.Count > maxLeaves)
            foreach (var k in _entries.Keys.OrderBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X).Skip(maxLeaves).ToList()) _entries.Remove(k);
        foreach (var e in _entries.Values) KindsPresent.UnionWith(e.Kinds);
        Bundles = _entries.Keys.Select(k => k.AncestorAt(WorkFrame.Level)).Distinct().OrderBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X).ToList();
        _heights = new(96, k => Read(k) is { } bytes ? HeightsOf(k, bytes) : null);
    }

    /// <summary>The kinds in a leaf entry's section table, read without its payloads.</summary>
    private static LeafKind[] ReadKinds(FileStream fs, long offset)
    {
        var head = new byte[CubeLeafCodec.HeadBytes];
        fs.Position = offset;
        fs.ReadExactly(head);
        int count = BitConverter.ToUInt16(head, 6);
        var table = new byte[count * CubeLeafCodec.TableEntryBytes];
        fs.ReadExactly(table);
        return Enumerable.Range(0, count).Select(k => (LeafKind)BitConverter.ToUInt16(table, k * CubeLeafCodec.TableEntryBytes)).ToArray();
    }

    private byte[]? Read(CubeKey key)
    {
        if (!_entries.TryGetValue(key, out var e)) return null;
        using var fs = File.OpenRead(e.Path);
        fs.Position = e.Offset;
        var bytes = new byte[e.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    private CubeTile? HeightsOf(CubeKey key, byte[] bytes)
    {
        if (!_entries[key].IsLeaf) return CubeTileCodec.Decode(bytes);
        return CubeLeafCodec.ReadSection(bytes, LeafKind.Heights) is { } s ? LeafHeights.Decode(s) : null;
    }

    public bool Has(CubeKey key) => _entries.ContainsKey(key);

    [ThreadStatic] private static Last<CubeKey, CubeTile?>? _lastHeights;

    public CubeTile? Heights(CubeKey key) => (_lastHeights ??= new()).Get(key, _heights);

    public LeafSection? Section(CubeKey key, LeafKind kind)
    {
        if (!_entries.TryGetValue(key, out var e) || !e.IsLeaf || !e.Kinds.Contains(kind)) return null;
        return Read(key) is { } bytes ? CubeLeafCodec.ReadSection(bytes, kind) : null;
    }

    /// <summary>Leaves bundle by bundle, rows south to north: neighbours stay close in the caches.</summary>
    public IEnumerable<CubeKey> Ordered() => Bundles.SelectMany(LeavesIn);

    public IEnumerable<CubeKey> LeavesIn(CubeKey bundle) =>
        _entries.Keys.Where(k => k.AncestorAt(bundle.Level) == bundle).OrderBy(k => k.Y).ThenBy(k => k.X);

    /// <summary>The leaf (level 14) under a point.</summary>
    public CubeKey Owner(EcefPos p) => CubeKey.FromEcef(p, LeafLevel);

    /// <summary>A point counts in <paramref name="bundle"/>: its leaf is in the region and in that bundle.</summary>
    public bool Owns(CubeKey bundle, EcefPos p)
    {
        var leaf = Owner(p);
        return leaf.AncestorAt(bundle.Level) == bundle && _entries.ContainsKey(leaf);
    }

    /// <summary>The LV95 position of lattice vertex (i, j) on the leaf's ground.</summary>
    public (double E, double N) Lv95Of(CubeKey key, CubeTile tile, int i, int j)
    {
        double h = tile.HeightAt(i, j);
        var (e, n, _) = SwissGeodesy.FromEcef(key.Vertex(tile.Bits, i, j, double.IsNaN(h) ? 0 : h));
        return (e, n);
    }

    /// <summary>
    /// A point's ellipsoidal height above the leaf mesh below it (NaN off the leaves): the mesh's triangles
    /// split along (i, j)–(i+1, j+1), as <see cref="CubeGeometry"/> builds it, interpolated on the lattice.
    /// </summary>
    public double AboveGround(EcefPos p)
    {
        var (f, u, v, h) = CubeSphere.FromEcef(p);
        var key = CubeKey.FromFace(f, u, v, LeafLevel);
        if (Heights(key) is not { } t) return double.NaN;
        int cells = 1 << t.Bits;
        double fi = Math.Clamp((u - key.U0) / key.Size * cells, 0, cells), fj = Math.Clamp((v - key.V0) / key.Size * cells, 0, cells);
        int i = Math.Min((int)fi, cells - 1), j = Math.Min((int)fj, cells - 1);
        double a = fi - i, b = fj - j;
        double h00 = t.HeightAt(i, j), h10 = t.HeightAt(i + 1, j), h01 = t.HeightAt(i, j + 1), h11 = t.HeightAt(i + 1, j + 1);
        double g = a >= b ? h00 + (h10 - h00) * a + (h11 - h10) * b : h00 + (h11 - h01) * a + (h01 - h00) * b;
        return h - g;
    }

    /// <summary>The LV95 box a bundle's leaves cover, 50 m wider: corners and edge midpoints of every leaf, rigorous.</summary>
    public (double MinE, double MinN, double MaxE, double MaxN) Lv95Box(CubeKey bundle) =>
        _boxes.GetOrAdd(bundle, b =>
        {
            double e0 = double.MaxValue, n0 = double.MaxValue, e1 = double.MinValue, n1 = double.MinValue;
            foreach (var leaf in LeavesIn(b))
                for (int j = 0; j <= 2; j++)
                    for (int i = 0; i <= 2; i++)
                    {
                        var (e, n, _) = SwissGeodesy.FromEcef(leaf.Vertex(1, i, j, 0));
                        e0 = Math.Min(e0, e); n0 = Math.Min(n0, n); e1 = Math.Max(e1, e); n1 = Math.Max(n1, n);
                    }
            return (e0 - 50, n0 - 50, e1 + 50, n1 + 50);
        });

    public (double MinE, double MinN, double MaxE, double MaxN) Lv95BoxAll()
    {
        var boxes = Bundles.Select(Lv95Box).ToList();
        return (boxes.Min(b => b.MinE), boxes.Min(b => b.MinN), boxes.Max(b => b.MaxE), boxes.Max(b => b.MaxN));
    }
}

/// <summary>One thread's last answer from an <see cref="Lru{TKey, TValue}"/>, asked again for the same key without its lock.</summary>
internal sealed class Last<TKey, TValue> where TKey : struct, IEquatable<TKey>
{
    private bool _has;
    private TKey _key;
    private TValue _value = default!;

    public TValue Get(TKey key, Lru<TKey, TValue> from)
    {
        if (_has && _key.Equals(key)) return _value;
        (_has, _key, _value) = (true, key, from.Get(key));
        return _value;
    }
}

/// <summary>A small thread-safe least-recently-used cache; a value is loaded outside the lock (twice at worst).</summary>
internal sealed class Lru<TKey, TValue>(int capacity, Func<TKey, TValue> load) where TKey : notnull
{
    private readonly Dictionary<TKey, (TValue Value, long Stamp)> _map = new();
    private long _clock;

    public TValue Get(TKey key)
    {
        lock (_map)
            if (_map.TryGetValue(key, out var hit)) { _map[key] = (hit.Value, ++_clock); return hit.Value; }
        var value = load(key);
        lock (_map)
        {
            _map[key] = (value, ++_clock);
            if (_map.Count > capacity)
                foreach (var old in _map.OrderBy(e => e.Value.Stamp).Take(capacity / 4).Select(e => e.Key).ToList()) _map.Remove(old);
        }
        return value;
    }
}
