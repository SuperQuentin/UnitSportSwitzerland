using System.Collections.Concurrent;
using System.Diagnostics;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using static System.Console;

// Checks the invariants that make generated tiles meet real ones without a seam
// (src/Terrain/ProceduralWorld.Blend.cs), on synthetic "real" terrain placed next to the generator:
//   A: a 3x3 block far ABOVE the generated valley floor (on the river, so the valley runs into it)
//   B: a 5x5 block with a one-tile hole, far BELOW the generated massif north of the valley
// Non-zero exit on any failed check.
//
//   dotnet run --project tools/BlendCheck -c Release

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
            if (ChunkFormat.Quantize(world.Height(e, n) + fb.Correction(e, n)) != g.HeightAt(c, r)) pBad++;
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
            preMax = Math.Max(preMax, Math.Abs(world.Height(e, n) + fb.Correction(e, n) - realFull[k].SampleMeshHeight(e, n)));
        }
    }
}
WriteLine($"  S + D at real edges before the vertex copy: within {preMax * 100:F1} cm");

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
        foreach (var s in roads.Segments)
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

static string Ms(IEnumerable<double> v)
{
    var a = v.OrderBy(x => x).ToArray();
    return $"median {a[a.Length / 2]:F1}, max {a[^1]:F1} ms";
}
WriteLine("timings per tile (8 in parallel):");
WriteLine($"  full grid {Ms(tFull)} (unblended {Ms(tPlain)}), coarse grid {Ms(tCoarse)}");

WriteLine(fails == 0 ? "ALL OK" : $"{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
