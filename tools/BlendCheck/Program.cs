using System.Collections.Concurrent;
using System.Diagnostics;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using static System.Console;

// Checks the invariants that make generated tiles meet real ones without a seam
// (src/Terrain/ProceduralWorld.Blend.cs), on synthetic "real" terrain placed next to the generator:
//   A: a 3x3 block far ABOVE the generated Rhone floor east of Riddes (the river runs into it)
//   B: a 5x5 block with a one-tile hole, far BELOW the generated mountains north of the valley
// Non-zero exit on any failed check.
//
//   dotnet run --project tools/BlendCheck -c Release

if (args.Contains("--roads")) return RoadBlendCheck.Run(args);
if (args.Contains("--generated-roads")) return await GeneratedRoadsCheck.Run(args);

const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N
var world = new ProceduralWorld(AnchorE, AnchorN);
var c0 = TileId.FromLv95(AnchorE, AnchorN);
int fails = 0;
void Check(string what, long bad)
{
    WriteLine($"  {(bad == 0 ? "ok  " : "FAIL")} {what}: {bad}");
    if (bad != 0) fails++;
}

var high = new HashSet<TileId>();
for (int de = 3; de <= 5; de++) for (int dn = -1; dn <= 1; dn++) high.Add(new TileId(c0.E + de, c0.N + dn));
var low = new HashSet<TileId>();
for (int de = -8; de <= -4; de++) for (int dn = 5; dn <= 9; dn++)
    if (!(de == -6 && dn == 7)) low.Add(new TileId(c0.E + de, c0.N + dn));
var hole = new TileId(c0.E - 6, c0.N + 7);
var real = new HashSet<TileId>(high.Concat(low));

static double RealH(double e, double n, bool isHigh) =>
    (isHigh ? 3000 : 700) + 250 * Math.Sin(e / 710.0) * Math.Cos(n / 930.0)
    + 60 * Math.Sin(e / 170.0 + n / 230.0) + 4 * Math.Sin(e / 9.3) * Math.Cos(n / 7.1);

// real tiles: a pure function of position quantised at the vertices, so their own seams are exact
var realFull = new ConcurrentDictionary<TileId, ChunkGrid>();
Parallel.ForEach(real, k =>
{
    bool h = high.Contains(k);
    var hs = new ushort[ChunkFormat.GridSize * ChunkFormat.GridSize];
    ushort mn = ushort.MaxValue, mx = 0;
    for (int r = 0; r < ChunkFormat.GridSize; r++)
        for (int c = 0; c < ChunkFormat.GridSize; c++)
        {
            ushort q = ChunkFormat.Quantize(RealH(k.MinE + c, k.MaxN - r, h));
            hs[r * ChunkFormat.GridSize + c] = q;
            mn = Math.Min(mn, q);
            mx = Math.Max(mx, q);
        }
    realFull[k] = new ChunkGrid(k, hs, (float)ChunkFormat.Dequantize(mn), (float)ChunkFormat.Dequantize(mx));
});
var realCoarse = realFull.ToDictionary(kv => kv.Key, kv => kv.Value.Decimate(ChunkFormat.CoarseStride));
var knots = realFull.ToDictionary(kv => kv.Key, kv => HorizonFormat.Extract(kv.Value));

// generated tiles: everything within 4 tiles of either block
var gen = new List<TileId>();
for (int n = real.Min(t => t.N) - 4; n <= real.Max(t => t.N) + 4; n++)
    for (int e = real.Min(t => t.E) - 4; e <= real.Max(t => t.E) + 4; e++)
    {
        var t = new TileId(e, n);
        if (!real.Contains(t) && real.Any(k => Math.Abs(k.E - e) <= 4 && Math.Abs(k.N - n) <= 4)) gen.Add(t);
    }
var genSet = gen.ToHashSet();
WriteLine($"{real.Count} real tiles, {gen.Count} generated tiles around them");

// what FallbackChunkSource hands the generator: 0 knots only, 1 for a full grid, 2 for a coarse one
ProceduralWorld.Blend? MakeBlend(TileId t, int kind)
{
    var reals = ProceduralWorld.BlendWindow(t).Where(real.Contains).Select(k =>
    {
        int s = kind == 0 ? 0 : ProceduralWorld.BlendGridStride(t, k, kind == 1);
        ChunkGrid? g = s == 0 ? null : s == 1 ? realFull[k] : realCoarse[k];
        return new ProceduralWorld.RealTile(k, knots[k], g);
    });
    return world.CreateBlend(t, reals, 1);
}

var full = new ConcurrentDictionary<TileId, ChunkGrid>();
var coarse = new ConcurrentDictionary<TileId, ChunkGrid>();
var horizon = new ConcurrentDictionary<TileId, ushort[]>();
var plain = new ConcurrentDictionary<TileId, ChunkGrid>();
var tFull = new ConcurrentBag<double>();
var tCoarse = new ConcurrentBag<double>();
var tPlain = new ConcurrentBag<double>();
int blended = 0;
var wall = Stopwatch.StartNew();
Parallel.ForEach(gen, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
{
    var fb = MakeBlend(t, 1);
    if (fb != null) Interlocked.Increment(ref blended);
    var sw = Stopwatch.StartNew();
    full[t] = world.BuildGrid(t, 1, fb);
    tFull.Add(sw.Elapsed.TotalMilliseconds);
    sw.Restart();
    coarse[t] = world.BuildGrid(t, ChunkFormat.CoarseStride, MakeBlend(t, 2));
    tCoarse.Add(sw.Elapsed.TotalMilliseconds);
    horizon[t] = world.HorizonSamples(t, MakeBlend(t, 0));
    sw.Restart();
    plain[t] = world.BuildGrid(t, 1);
    tPlain.Add(sw.Elapsed.TotalMilliseconds);
});
WriteLine($"built in {wall.Elapsed.TotalSeconds:F1} s; {blended} of {gen.Count} tiles have a blend");

ChunkGrid G(TileId t, bool isFull) =>
    real.Contains(t) ? (isFull ? realFull[t] : realCoarse[t]) : (isFull ? full[t] : coarse[t]);

// every full-resolution vertex two tiles sharing an edge or a corner both hold
long SharedMismatch(TileId a, TileId b, bool isFull, ref long checkedCount)
{
    var ga = G(a, isFull);
    var gb = G(b, isFull);
    long bad = 0;
    double e0 = Math.Max(a.MinE, b.MinE), e1 = Math.Min(a.MinE + 1000, b.MinE + 1000);
    double n0 = Math.Max(a.MinN, b.MinN), n1 = Math.Min(a.MaxN, b.MaxN);
    int step = isFull ? 1 : ChunkFormat.CoarseStride;
    for (double e = e0; e <= e1; e += step)
        for (double n = n0; n <= n1; n += step)
        {
            checkedCount++;
            if (ga.HeightAt((int)(e - a.MinE), (int)(a.MaxN - n)) != gb.HeightAt((int)(e - b.MinE), (int)(b.MaxN - n)))
                bad++;
        }
    return bad;
}
var around = new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, 1), (-1, 1), (1, -1) };

WriteLine("seams:");
long grBad = 0, grCoarseBad = 0, grChecked = 0;
foreach (var t in gen)
    foreach (var (de, dn) in around)
    {
        var k = new TileId(t.E + de, t.N + dn);
        if (!real.Contains(k)) continue;
        grBad += SharedMismatch(t, k, true, ref grChecked);
        grCoarseBad += SharedMismatch(t, k, false, ref grChecked);
    }
Check($"generated|real vertices differing, full ({grChecked} checked, both resolutions)", grBad);
Check("generated|real vertices differing, coarse", grCoarseBad);

long ggBad = 0, ggCoarseBad = 0, ggChecked = 0;
foreach (var t in gen)
    foreach (var (de, dn) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
    {
        var u = new TileId(t.E + de, t.N + dn);
        if (!genSet.Contains(u)) continue;
        ggBad += SharedMismatch(t, u, true, ref ggChecked);
        ggCoarseBad += SharedMismatch(t, u, false, ref ggChecked);
    }
Check($"generated|generated vertices differing, full ({ggChecked} checked, both resolutions)", ggBad);
Check("generated|generated vertices differing, coarse", ggCoarseBad);

WriteLine("resolutions:");
long cBad = 0, hBad = 0;
foreach (var t in gen)
{
    var d = full[t].Decimate(ChunkFormat.CoarseStride).Heights;
    var c = coarse[t].Heights;
    for (int i = 0; i < c.Length; i++) if (c[i] != d[i]) cBad++;
    var x = HorizonFormat.Extract(full[t]);
    var h = horizon[t];
    for (int i = 0; i < h.Length; i++) if (h[i] != x[i]) hBad++;
}
Check("coarse vertices differing from the decimated full grid", cBad);
Check("horizon samples differing from the full grid at 100 m", hBad);

// the point path (trees, roads, footings) against the grid path, on tiles with detail neighbours
long pBad = 0, pChecked = 0;
foreach (var t in gen.Where(t => real.Any(k => Math.Abs(k.E - t.E) <= 1 && Math.Abs(k.N - t.N) <= 1)).Take(12))
{
    var fb = MakeBlend(t, 1)!;   // a fresh blend with no lattice, so Correction takes its own path
    var g = full[t];
    for (int r = 1; r < 1000; r += 3)
        for (int c = 1; c < 1000; c += 3)
        {
            double e = t.MinE + c, n = t.MaxN - r;
            pChecked++;
            double gh = world.Height(e, n);
            if (ChunkFormat.Quantize(gh + fb.Correction(e, n, gh)) != g.HeightAt(c, r)) pBad++;
        }
}
Check($"vertices where the point path differs from the grid ({pChecked} checked)", pBad);

WriteLine("shape:");
static double MaxStep(IEnumerable<ChunkGrid> grids)
{
    double max = 0;
    foreach (var g in grids)
        for (int r = 0; r < g.Size; r++)
            for (int c = 0; c < g.Size; c++)
            {
                double h = g.HeightMetersAt(c, r);
                if (c + 1 < g.Size) max = Math.Max(max, Math.Abs(g.HeightMetersAt(c + 1, r) - h));
                if (r + 1 < g.Size) max = Math.Max(max, Math.Abs(g.HeightMetersAt(c, r + 1) - h));
            }
    return max;
}
double sReal = MaxStep(realFull.Values), sPlain = MaxStep(plain.Values);
double sBlend = MaxStep(full.Values), sHole = MaxStep(new[] { full[hole] });
double mismatch = 0;
foreach (var k in real)
    for (int i = 0; i < HorizonFormat.SamplesPerTile; i++)
    {
        const int side = HorizonFormat.SamplesPerSide;
        double e = k.MinE + i % side * HorizonFormat.SpacingM, n = k.MaxN - i / side * HorizonFormat.SpacingM;
        mismatch = Math.Max(mismatch, Math.Abs(ChunkFormat.Dequantize(knots[k][i]) - world.Height(e, n)));
    }
// the band may steepen ground by at most the smoothstep's peak slope over the largest mismatch
double bound = sPlain + sReal + 1.5 * mismatch / ProceduralWorld.Band;
WriteLine($"  largest step per metre: real {sReal:F2}, generated {sPlain:F2}, blended {sBlend:F2} " +
          $"(bound {bound:F2} for a {mismatch:F0} m mismatch), the hole {sHole:F2}");
Check("blended grids steeper than the bound (a cliff)", sBlend > bound ? 1 : 0);
Check("the hole steeper than generated ground (a jump on the medial axis)", sHole > sPlain ? 1 : 0);

double preMax = 0;
foreach (var t in gen)
{
    var fb = MakeBlend(t, 1);
    if (fb == null) continue;
    foreach (var (de, dn) in around.Take(4))
    {
        var k = new TileId(t.E + de, t.N + dn);
        if (!real.Contains(k)) continue;
        for (int i = 0; i <= 1000; i++)
        {
            double e = de == -1 ? t.MinE : de == 1 ? t.MinE + 1000 : t.MinE + i;
            double n = dn == 1 ? t.MaxN : dn == -1 ? t.MinN : t.MaxN - i;
            double gh = world.Height(e, n);
            preMax = Math.Max(preMax, Math.Abs(gh + fb.Correction(e, n, gh) - realFull[k].SampleMeshHeight(e, n)));
        }
    }
}
WriteLine($"  the blend at real edges before the vertex copy: within {preMax * 100:F1} cm");

// Blended ground lies between the generated and the real: never past both. The first, additive
// blend kept the generator's relief at full size and dug a trench 150 m below both where a steep
// generated flank met a real valley floor. Measured against the lowest and highest real knot the
// tile's blend can see, plus the real residual D carries (the synthetic ground's own roughness).
double residual = 0;
foreach (var k in real)
    for (int r = 0; r < ChunkFormat.GridSize; r += 7)
        for (int c = 0; c < ChunkFormat.GridSize; c += 7)
        {
            double e = k.MinE + c, n = k.MaxN - r;
            double u = c / (double)HorizonFormat.Stride, v = r / (double)HorizonFormat.Stride;
            int kc = Math.Min((int)u, HorizonFormat.SamplesPerSide - 2), kr = Math.Min((int)v, HorizonFormat.SamplesPerSide - 2);
            double K(int cc, int rr) => ChunkFormat.Dequantize(knots[k][rr * HorizonFormat.SamplesPerSide + cc]);
            double fu = u - kc, fv = v - kr;
            double rs = (K(kc, kr) * (1 - fu) + K(kc + 1, kr) * fu) * (1 - fv) + (K(kc, kr + 1) * (1 - fu) + K(kc + 1, kr + 1) * fu) * fv;
            residual = Math.Max(residual, Math.Abs(realFull[k].HeightMetersAt(c, r) - rs));
        }
double excursion = 0;
foreach (var t in gen)
{
    var near = ProceduralWorld.BlendWindow(t).Where(real.Contains).ToList();
    if (near.Count == 0) continue;
    double rmin = near.Min(k => knots[k].Min(q => ChunkFormat.Dequantize(q)));
    double rmax = near.Max(k => knots[k].Max(q => ChunkFormat.Dequantize(q)));
    var g = full[t];
    for (int r = 0; r < g.Size; r += 3)
        for (int c = 0; c < g.Size; c += 3)
        {
            double gh = world.Height(t.MinE + c, t.MaxN - r), h = g.HeightMetersAt(c, r);
            double lo = Math.Min(gh, rmin), hi = Math.Max(gh, rmax);
            excursion = Math.Max(excursion, Math.Max(lo - h, h - hi));
        }
}
WriteLine($"  furthest blended ground strays past both surfaces: {excursion:F1} m (real residual up to {residual:F1} m)");
Check("blended ground past both the generated and the real (a trench or a ridge)", excursion > residual + 1 ? 1 : 0);

WriteLine("content:");
double treeErr = 0, roadErr = 0;
int trees = 0, buildings = 0, water = 0, plainWater = 0;
object gate = new();
Parallel.ForEach(gen.Where(t => MakeBlend(t, 1) != null), new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
{
    var fb = MakeBlend(t, 1);
    int w = world.BuildCover(t, fb).Count(b => b == (byte)CoverClass.Water);
    int pw = world.BuildCover(t).Count(b => b == (byte)CoverClass.Water);
    var tr = world.BuildTrees(t, fb);
    double te = 0, re = 0;
    foreach (var x in tr) te = Math.Max(te, Math.Abs(x.Y - full[t].SampleMeshHeight(t.MinE + x.X, t.MaxN - x.Z)));
    if (world.BuildRoads(t, fb) is { } roads)
        foreach (var s in roads.Segments.Where(s => (s.Flags & RoadFlags.Bridge) == 0))   // a deck stands over the ground by design (#559)
            for (int i = 0; i < s.Points.Length; i += 3)
                re = Math.Max(re, Math.Abs(s.Points[i + 1] - full[t].SampleMeshHeight(t.MinE + s.Points[i], t.MaxN - s.Points[i + 2])));
    int b = world.BuildBuildings(t, fb)?.Buildings.Count ?? 0;
    lock (gate)
    {
        water += w;
        plainWater += pw;
        trees += tr.Count;
        buildings += b;
        treeErr = Math.Max(treeErr, te);
        roadErr = Math.Max(roadErr, re);
    }
});
WriteLine($"  {trees} trees, {buildings} buildings, {water} water cells ({plainWater} unblended, rivers end where the band tilts them)");
// a tree or road vertex off the mesh by more than a 1 m cell's worth of steep ground is on the wrong surface
Check("trees more than 1 m off the ground mesh", treeErr > 1 ? 1 : 0);
Check("road vertices more than 1 m off the ground mesh", roadErr > 1 ? 1 : 0);

// ---- quality: what the renders showed and no earlier number measured ---------------------------
WriteLine("quality:");

// the height of the world vertex at whole metres (e, n), from whichever full grid holds it
double VertexAt(double e, double n)
{
    var id = TileId.FromLv95(e, n);
    var g = real.Contains(id) ? realFull[id] : full[id];
    return g.HeightMetersAt((int)(e - id.MinE), (int)(id.MaxN - n));
}

// Seam kink: the change of slope across a generated|real edge, against the same measure one row in
// on either side. A seam that is C0 but not C1 (a ridge or a crease) stands out here.
var kSeam = new List<double>();
var kInside = new List<double>();
foreach (var t in gen)
    foreach (var (de, dn) in around.Take(4))
    {
        var k = new TileId(t.E + de, t.N + dn);
        if (!real.Contains(k)) continue;
        // walk the shared edge; (ue, un) steps from the real side into the generated one
        double ue = -de, un = -dn;
        for (int i = 1; i < 1000; i++)
        {
            double e0 = de == -1 ? t.MinE : de == 1 ? t.MinE + 1000 : t.MinE + i;
            double n0 = dn == 1 ? t.MaxN : dn == -1 ? t.MinN : t.MaxN - i;
            double H(int s) => VertexAt(e0 + s * ue, n0 + s * un);
            double hm2 = H(-2), hm1 = H(-1), h0 = H(0), h1 = H(1), h2 = H(2);
            kSeam.Add(Math.Abs(h1 - 2 * h0 + hm1));
            kInside.Add(Math.Abs(h0 - 2 * hm1 + hm2));   // one row into the real tile
            kInside.Add(Math.Abs(h2 - 2 * h1 + h0));     // one row into the generated one
        }
    }
double Mean(List<double> v) => v.Count == 0 ? 0 : v.Average();
double P99(List<double> v) => v.Count == 0 ? 0 : v.OrderBy(x => x).ElementAt((int)(v.Count * 0.99));
WriteLine($"  seam kink: mean {Mean(kSeam):F3} m, p99 {P99(kSeam):F3} m; one row in: mean {Mean(kInside):F3} m, p99 {P99(kInside):F3} m");
Check("seams creased or ridged (kink over 1.5x the ground's own, mean or p99)",
    Mean(kSeam) > 1.5 * Mean(kInside) || P99(kSeam) > 1.5 * P99(kInside) ? 1 : 0);

// Streaks: along lines parallel to a real edge, the part of the ground the blend brought (W R + D,
// i.e. h - (1 - W) G) high-passed over 600 m. Real detail extruded straight out shows up here;
// read coarser the further it is carried, it should fade past a kilometre.
// no lattice is prepared on these blends, so each point is computed afresh and the level switch applies
var streakBlends = new Dictionary<TileId, ProceduralWorld.Blend?>();
double StreakRms(IEnumerable<(double E, double N)> line)
{
    var q = new List<double>();
    foreach (var (e, n) in line)
    {
        var id = TileId.FromLv95(e, n);
        if (!streakBlends.TryGetValue(id, out var fb)) streakBlends[id] = fb = MakeBlend(id, 1);
        if (fb == null) { q.Add(0); continue; }
        double g = world.Height(e, n);
        q.Add(fb.Correction(e, n, g) + fb.RealWeight(e, n) * g);
    }
    // a local quadratic fit over 150 m either side (Savitzky-Golay), not a moving average: the
    // synthetic ground's own km-scale curvature leaks through an average and reads as streaks
    const int half = 15;
    double norm = (2.0 * half - 1) * (2 * half + 1) * (2 * half + 3);
    double sum = 0;
    int count = 0;
    for (int i = half; i < q.Count - half; i++)
    {
        double fit = 0;
        for (int j = -half; j <= half; j++) fit += (3.0 * (3 * half * half + 3 * half - 1) - 15.0 * j * j) / norm * q[i + j];
        sum += (q[i] - fit) * (q[i] - fit);
        count++;
    }
    return Math.Sqrt(sum / Math.Max(1, count));
}
// east of the high block, running north-south; south of the low block, running east-west
double highEdge = (c0.E + 6) * 1000.0, lowEdge = (c0.N + 5) * 1000.0 - 1000;
IEnumerable<(double, double)> EastLine(double d) =>
    Enumerable.Range(0, 301).Select(i => (highEdge + d, (c0.N - 1) * 1000.0 - 1000 + i * 10.0));
IEnumerable<(double, double)> SouthLine(double d) =>
    Enumerable.Range(0, 501).Select(i => ((c0.E - 8) * 1000.0 + i * 10.0, lowEdge - d));
// the scale to judge them by: the generated ground's own relief on the same lines, same filter
double OwnRms(IEnumerable<(double E, double N)> line)
{
    var pts = line.ToList();
    var q = pts.Select(p => world.Height(p.E, p.N)).ToList();
    const int half = 15;
    double norm = (2.0 * half - 1) * (2 * half + 1) * (2 * half + 3), sum = 0;
    int count = 0;
    for (int i = half; i < q.Count - half; i++)
    {
        double fit = 0;
        for (int j = -half; j <= half; j++) fit += (3.0 * (3 * half * half + 3 * half - 1) - 15.0 * j * j) / norm * q[i + j];
        sum += (q[i] - fit) * (q[i] - fit);
        count++;
    }
    return Math.Sqrt(sum / count);
}
long streakBad = 0;
foreach (double d in new[] { 250.0, 500, 1000, 2000, 2900 })
{
    ProceduralWorld.SingleLevelForChecks = true;
    double before = (StreakRms(EastLine(d)) + StreakRms(SouthLine(d))) / 2;
    ProceduralWorld.SingleLevelForChecks = false;
    double after = (StreakRms(EastLine(d)) + StreakRms(SouthLine(d))) / 2;
    double own = (OwnRms(EastLine(d)) + OwnRms(SouthLine(d))) / 2;
    WriteLine($"  streaks {d,5:F0} m out: {after,5:F2} m RMS (a single level: {before,5:F2} m; the generated ground's own relief: {own,5:F2} m)");
    // past a kilometre what the blend brings must be lost in the ground's own texture (or under
    // 10 cm, where that texture is a flat valley floor), and the pyramid must never make it worse
    // than reading every distance at the knots
    if ((d >= 1000 && after > Math.Max(0.05 * own, 0.1)) || after > before + 0.005) streakBad++;
}
Check("distances where carried real detail shows as streaks", streakBad);

if (args.Contains("--render")) Renders.Write(world, real, realFull, full, c0, hole);

// ---- the source: FallbackChunkSource under CachingChunkSource, as the game chains them ----------
WriteLine("source:");
var inner = new SyntheticSource(real, realFull, realCoarse, knots);
var fallback = new FallbackChunkSource(inner, world, AnchorE, AnchorN);
var cache = new CachingChunkSource(fallback);
fallback.Neighbours = cache;
var logs = new ConcurrentBag<string>();
fallback.Log = logs.Add;

// a sample of generated tiles: every one with a detail neighbour, and some further out
var sample = gen.Where(t => real.Any(k => Math.Abs(k.E - t.E) <= 1 && Math.Abs(k.N - t.N) <= 1)).Take(10)
    .Concat(gen.Where(t => !real.Any(k => Math.Abs(k.E - t.E) <= 1 && Math.Abs(k.N - t.N) <= 1)).Take(6))
    .ToList();
static long Differ(ushort[] a, ushort[] b) =>
    a.Length != b.Length ? long.MaxValue : a.Zip(b).LongCount(p => p.First != p.Second);

// no real tiles yet: every tile is generated, unblended, the real ones' ids included
long srcBad = 0;
foreach (var t in sample.Concat(real.Take(3)))
    srcBad += Differ((await cache.LoadChunkAsync(t))!.Heights, world.BuildGrid(t, 1).Heights);
Check("tiles differing from the plain generator before any real tile is known", srcBad);

// the real set arrives: until invalidated, the cache still serves the old tiles
var snap = fallback.SetReal(real);
int stale = 0;
foreach (var t in sample.Take(4))
    if (Differ((await cache.LoadChunkAsync(t))!.Heights, full[t].Heights) != 0) stale++;
Check("blended tiles NOT served stale before Invalidate (the test would prove nothing)", 4 - stale);
// what ChunkManager.MergeAvailableTiles invalidates: the new ids and generated tiles within 4 of them
bool Affected(TileId id) => real.Contains(id) || real.Any(k => Math.Abs(k.E - id.E) <= 4 && Math.Abs(k.N - id.N) <= 4);
cache.Invalidate(Affected);

// knots from the coarse grids (no horizon.bin loaded yet)
long fullBad = 0, coarseBad = 0, realBad = 0, coverBad = 0;
var clockSrc = Stopwatch.StartNew();
await Parallel.ForEachAsync(sample, async (t, _) =>
{
    var f = await cache.LoadChunkAsync(t);
    var c = await cache.LoadCoarseChunkAsync(t);
    Interlocked.Add(ref fullBad, Differ(f!.Heights, full[t].Heights));
    Interlocked.Add(ref coarseBad, Differ(c!.Heights, coarse[t].Heights));
    // cover comes from the coarse blend and must equal cover from the full one
    var cov = await cache.LoadCoverAsync(t);
    Interlocked.Add(ref coverBad, cov!.Zip(world.BuildCover(t, MakeBlend(t, 1))).LongCount(p => p.First != p.Second));
});
double coldMs = clockSrc.Elapsed.TotalMilliseconds;
foreach (var k in real.Take(5))
    realBad += Differ((await cache.LoadChunkAsync(k))!.Heights, realFull[k].Heights);
Check($"full vertices differing from the harness's blend ({sample.Count} tiles through the source)", fullBad);
Check("coarse vertices differing from the harness's blend", coarseBad);
Check("cover cells differing between the coarse blend and the full one", coverBad);
Check("real tiles served other than the inner source's", realBad);
Check("real ids the source claims to generate", real.Count(fallback.Covers));
var farther = new TileId(real.Max(t => t.E) + FallbackChunkSource.FillRadiusTiles + 1, c0.N);
Check("a tile past the fill domain the source claims to generate", fallback.Covers(farther) || snap.InDomain(farther) ? 1 : 0);
Check("tiles outside every blend window that still get a blend",
    gen.Count(t => !Affected(t) && fallback.BlendFor(t, true).Result != null));

// the merged horizon: real knots where real, the harness's knots-only samples everywhere generated
var hIndex = (await fallback.LoadHorizonAsync())!;
long hzBad = 0, hzMissing = 0;
foreach (var t in gen)
    if (hIndex.TryGet(t, out var s)) hzBad += Differ(s, horizon[t]); else hzMissing++;
foreach (var k in real)
    if (hIndex.TryGet(k, out var s)) hzBad += Differ(s, knots[k]); else hzMissing++;
Check($"horizon samples differing from the grids ({hIndex.Count} tiles in the merged index)", hzBad);
Check("tiles missing from the merged horizon", hzMissing);

// the same blends again, now from horizon.bin's knots rather than the tiles: the same bits
fallback.SetReal(real);
cache.Invalidate(Affected);
long knotBad = 0;
foreach (var t in sample.Take(4))
    knotBad += Differ((await cache.LoadChunkAsync(t))!.Heights, full[t].Heights);
Check("vertices differing when the knots come from horizon.bin instead of the tiles", knotBad);

// switched off: nothing generated, the real tiles untouched
fallback.SetReal(real, enabled: false);
cache.Invalidate(null);
Check("tiles still generated with the fill off",
    (await cache.LoadChunkAsync(sample[0]) == null ? 0 : 1) + (fallback.Covers(sample[0]) ? 1 : 0));
Check("real tiles lost with the fill off", await cache.LoadChunkAsync(real.First()) == null ? 1 : 0);
foreach (var l in logs) WriteLine($"  log: {l}");
WriteLine($"  {sample.Count} tiles cold through the source, blend loads included: {coldMs:F0} ms");

// ---- water (#298): generated lakes and rivers have a bed below a still level -------------------
WriteLine("water:");
{
    // Léman's middle, then north across its shore; the Rhône round the anchor
    var lakeTiles = Enumerable.Range(0, 16).Select(i => new TileId(2530, 1140 + i)).ToList();
    var riverTiles = Enumerable.Range(-3, 7).SelectMany(de => Enumerable.Range(-2, 5).Select(dn => new TileId(c0.E + de, c0.N + dn))).ToList();
    long above = 0, notFlush = 0, edgeBad = 0, wetSamples = 0;
    double lakeMax = 0, riverMax = 0;
    int shoreSamples = 0;
    var tiles = new Dictionary<TileId, WaterTile?>();
    foreach (var t in lakeTiles.Concat(riverTiles).Distinct())
    {
        var w = world.BuildWater(t);
        tiles[t] = w;
        if (w == null) continue;
        var g = world.BuildGrid(t, 1);
        int n = WaterTile.Size;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                float l = w.Level[r * n + c];
                if (float.IsNaN(l)) continue;
                wetSamples++;
                double depth = l - g.HeightMetersAt(c * WaterTile.Stride, r * WaterTile.Stride);
                if (depth < -ChunkFormat.HeightScale && above++ < 5)
                    WriteLine($"    above: {t} ({c},{r}) level {l:F2} ground {g.HeightMetersAt(c * WaterTile.Stride, r * WaterTile.Stride):F2}");
                if (lakeTiles.Contains(t)) lakeMax = Math.Max(lakeMax, depth); else riverMax = Math.Max(riverMax, depth);
                // next to a dry sample the bed meets the bank: a lake's shelf there is a metre or so
                // (the cover's 10 m fields put the water's edge up to ~15 m into the shelf)
                bool shore = (c > 0 && float.IsNaN(w.Level[r * n + c - 1])) || (c < n - 1 && float.IsNaN(w.Level[r * n + c + 1]))
                    || (r > 0 && float.IsNaN(w.Level[(r - 1) * n + c])) || (r < n - 1 && float.IsNaN(w.Level[(r + 1) * n + c]));
                if (shore && lakeTiles.Contains(t)) { shoreSamples++; if (depth > 1.5) notFlush++; }
            }
    }
    foreach (var (t, w) in tiles)
    {
        if (w == null || !tiles.TryGetValue(new TileId(t.E + 1, t.N), out var east) || east == null) continue;
        int n = WaterTile.Size;
        for (int r = 0; r < n; r++)
        {
            float a = w.Level[r * n + n - 1], b = east.Level[r * n];
            if (float.IsNaN(a) != float.IsNaN(b) || (!float.IsNaN(a) && a != b)) edgeBad++;
        }
    }
    WriteLine($"  {wetSamples} wet samples; deepest lake bed {lakeMax:F1} m, river {riverMax:F1} m; {shoreSamples} lake shore samples");
    Check("wet samples whose bed is above the still level", above);
    Check("lake shore samples more than 1.5 m deep (the bed must meet the bank)", notFlush);
    Check("water level samples differing across a tile edge", edgeBad);
    Check("a generated lake with no depth (Léman's middle under 20 m)", lakeMax < 20 ? 1 : 0);
    Check("a generated river deeper than a channel gets (6 m)", riverMax > WaterBed.ChannelDepth(1e9) + 0.5 ? 1 : 0);
}

static string Ms(IEnumerable<double> v)
{
    var a = v.OrderBy(x => x).ToArray();
    return $"median {a[a.Length / 2]:F1}, max {a[^1]:F1} ms";
}
WriteLine("timings per tile (8 in parallel):");
WriteLine($"  full grid {Ms(tFull)} (unblended {Ms(tPlain)}), coarse grid {Ms(tCoarse)}");

WriteLine(fails == 0 ? "ALL OK" : $"{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

/// <summary>The "real" region as an ordinary chunk source: grids, coarse grids and a horizon.bin.</summary>
sealed class SyntheticSource(IReadOnlySet<TileId> real, IReadOnlyDictionary<TileId, ChunkGrid> fullGrids,
    IReadOnlyDictionary<TileId, ChunkGrid> coarseGrids, IReadOnlyDictionary<TileId, ushort[]> knots) : IChunkSource
{
    public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) => Task.FromResult(new TerrainManifest());
    public Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(real.Contains(id) ? fullGrids[id] : null);
    public Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(real.Contains(id) ? coarseGrids[id] : null);
    public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) => Task.FromResult<RoadTile?>(null);
    public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) => Task.FromResult<HashSet<int>?>(null);
    public Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default) => Task.FromResult<BuildingTile?>(null);
    public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult<List<TreeInstance>?>(null);
    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) =>
        Task.FromResult<HorizonIndex?>(new HorizonIndex(knots.ToDictionary(kv => kv.Key, kv => kv.Value)));
}

/// <summary>
/// "--render": shaded-relief before/after pictures (left the generator alone, right the blended
/// world) of the overview, the one-tile hole and a block corner, to test_output/blend. The numbers
/// above are what gates a change; these are how a change is judged by eye.
/// </summary>
static class Renders
{
    public static void Write(ProceduralWorld world, HashSet<TileId> real, IReadOnlyDictionary<TileId, ChunkGrid> realFull,
        IReadOnlyDictionary<TileId, ChunkGrid> blended, TileId c0, TileId hole)
    {
        string dir = Path.Combine(FindRepo(), "test_output", "blend");
        Directory.CreateDirectory(dir);
        double After(double e, double n)
        {
            var id = TileId.FromLv95(e, n);
            var g = real.Contains(id) ? realFull[id] : blended.TryGetValue(id, out var b) ? b : null;
            return g == null ? world.Height(e, n) : g.SampleMeshHeight(e, n);
        }
        void One(string name, double minE, double maxN, double sizeE, double sizeN, double metresPerPixel)
        {
            int w = (int)(sizeE / metresPerPixel), h = (int)(sizeN / metresPerPixel);
            var pixels = new byte[h * w * 2];
            Shade(pixels, w, h, 0, (x, y) => world.Height(minE + x * metresPerPixel, maxN - y * metresPerPixel), metresPerPixel);
            Shade(pixels, w, h, w, (x, y) => After(minE + x * metresPerPixel, maxN - y * metresPerPixel), metresPerPixel);
            string path = Path.Combine(dir, $"{name}_before_after.png");
            Png.WriteGray(path, pixels, 2 * w, h);
            Console.WriteLine($"  render: {path}");
        }
        One("overview", (c0.E - 12) * 1000.0, (c0.N + 13) * 1000.0, 22000, 18000, 20);
        One("hole", (hole.E - 1) * 1000.0, (hole.N + 2) * 1000.0, 3000, 3000, 2);
        One("corner", (c0.E + 5) * 1000.0, (c0.N + 3) * 1000.0, 2000, 2000, 2);
    }

    /// <summary>Hillshade, light from the north-west at 45 degrees, into one half of a double-width image.</summary>
    private static void Shade(byte[] pixels, int w, int h, int x0, Func<int, int, double> height, double step)
    {
        var z = new double[(w + 2) * (h + 2)];
        Parallel.For(0, h + 2, y => { for (int x = 0; x < w + 2; x++) z[y * (w + 2) + x] = height(x - 1, y - 1); });
        double lx = -1 / Math.Sqrt(3), ly = -1 / Math.Sqrt(3), lz = 1 / Math.Sqrt(3);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y + 1) * (w + 2) + x + 1;
                double gx = (z[i + 1] - z[i - 1]) / (2 * step), gy = (z[i + w + 2] - z[i - w - 2]) / (2 * step);
                // image y runs south; normal of the surface h(x, y) with y pointing north
                double nx = -gx, ny = gy, nz = 1, len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                double shade = Math.Max(0, (nx * lx + ny * -ly + nz * lz) / len);
                pixels[y * 2 * w + x0 + x] = (byte)Math.Clamp(40 + 215 * shade, 0, 255);
            }
    }

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "project.godot"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }
}

/// <summary>An 8-bit greyscale PNG, no library: signature, IHDR, one zlib IDAT, IEND.</summary>
static class Png
{
    public static void WriteGray(string path, byte[] pixels, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Optimal, true))
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);   // no filter
                z.Write(pixels, y * width, width);
            }
        using var f = File.Create(path);
        f.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)width);
        BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // bit depth; colour type 0 (grey), default compression, filter, no interlace
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        var crc = new byte[4];
        BigEndian(crc, 0, Crc(head.AsSpan(4, 4), data));
        s.Write(crc);
    }

    private static uint Crc(ReadOnlySpan<byte> type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        void Feed(ReadOnlySpan<byte> bytes)
        {
            foreach (byte b in bytes)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        }
        Feed(type);
        Feed(data);
        return c ^ 0xFFFFFFFF;
    }

    private static void BigEndian(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }
}
