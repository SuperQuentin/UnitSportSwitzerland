using System.Globalization;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using static System.Console;

/// <summary>
/// "--fill-seams" (#1778, #1779): the earth world's generated fill at the spots the playtest of Nov 12 found walls,
/// checked for continuity:
/// <list type="bullet">
/// <item>the pyramid the fill stands on (<see cref="PyramidBase"/>): neighbouring lattice points ~2.4 m apart differ by
/// no more than the steepest real slope allows (<see cref="PyramidStepM"/>);</item>
/// <item>the fill leaves the game builds there (<see cref="EarthLeaves"/>, roads off): every shared leaf edge agrees
/// (<see cref="EdgeM"/>);</item>
/// <item>the globe's far ground of a leaf (<see cref="EarthLeaves.HeightsOf"/> before the leaf is made) on the same
/// edge as the made leaf beside it (<see cref="FarEdgeM"/>);</item>
/// </list>
///
///   dotnet run --project tools/BlendCheck -c Release -- --fill-seams --pyramid D:/UnitSportSwitzerland/cube_global
///     [--leaves DIR] [--at E,N[;E,N...]] [--side 5]
/// </summary>
internal static class FillSeamsCheck
{
    /// <summary>
    /// The most two neighbouring pyramid samples (level 13, ~2.4 m apart) may differ by, metres: a 75° rock face over
    /// one sample. The walls of #1778 were tens of metres in one step.
    /// </summary>
    public const double PyramidStepM = 9;

    /// <summary>Two made leaves on their shared edge, metres: the frames' bar (1 cm) and the height quantum.</summary>
    public const double EdgeM = 0.05;

    /// <summary>
    /// The far ground (every 8th vertex, bilinear between) beside a made leaf on their edge, metres: the generator's
    /// short relief between two far samples, softly clamped to 12 m by the fill, and the globe's skirts hang 2 m.
    /// A ledge of #1779 was tens of metres.
    /// </summary>
    public const double FarEdgeM = 6;

    /// <summary>
    /// The mean miss of the globe's coarser ground (the pyramid's levels) along a leaf edge, metres: the fill's detail
    /// (clamped to 12 m, zero-mean over its ~150 m low-pass) and a coarse level's tent mean are no bias; a ledge is.
    /// </summary>
    public const double LedgeM = 3;

    /// <summary>The playtest's spots: notes 31 and 30 (#1778) and 32 (#1779).</summary>
    private static readonly (double E, double N)[] Spots = [(2578980, 1116051), (2579661, 1108305), (2580090, 1116823)];

    public static int Run(string[] args)
    {
        const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N
        string dir = Value(args, "--leaves") ?? "D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube";
        string? pyramidDir = Value(args, "--pyramid") ?? "D:/UnitSportSwitzerland/cube_global";
        int half = (int.TryParse(Value(args, "--side"), out int sd) ? sd : 5) / 2;
        var pyramid = PyramidBase.Open(pyramidDir);
        if (pyramid is null)
        {
            WriteLine($"no pyramid at {pyramidDir}: skipped");
            return 0;
        }
        var store = LeafStore.Open(dir);
        WriteLine($"pyramid to level {pyramid.FinestLevel}; {store.Count} real leaves in {dir}");
        var spots = Value(args, "--at") is { } at
            ? at.Split(';').Select(s => s.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).Select(v => (v[0], v[1])).ToArray()
            : Spots;
        var world = new ProceduralWorld(AnchorE, AnchorN);
        var (alat, alon) = Lv95Source.Instance.ToLatLon(AnchorE, AnchorN);
        int fails = 0;
        void Check(string what, bool ok, string detail)
        {
            WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}: {detail}");
            if (!ok) fails++;
        }

        foreach (var (e, n) in spots)
        {
            var (lat, lon) = Lv95Source.Instance.ToLatLon(e, n);
            var centre = LeafFrames.LeafAt(lat, lon);
            WriteLine($"{e:F0},{n:F0}: leaf {centre}, {2 * half + 1}² leaves round it");

            // the pyramid on the window, every leaf-lattice point (level 23), its steepest step between neighbours
            int bits = CubeKey.LatticeBits(centre.Level), cells = 1 << bits, level = centre.Level + bits;
            long gi0 = ((long)centre.X - half) << bits, gj0 = ((long)centre.Y - half) << bits;
            int side = (2 * half + 1) * cells + 1;
            var p = new double[side * side];
            Parallel.For(0, side, j =>
            {
                for (int i = 0; i < side; i++) p[j * side + i] = pyramid.HeightAt(centre.Face, gi0 + i, gj0 + j, level);
            });
            double worst = 0;
            long wi = 0, wj = 0;
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    double h = p[j * side + i];
                    if (double.IsNaN(h)) continue;
                    // per 2 lattice points: one level-13 sample
                    foreach (var (di, dj) in new[] { (2, 0), (0, 2) })
                    {
                        if (i + di >= side || j + dj >= side) continue;
                        double o = p[(j + dj) * side + i + di];
                        if (double.IsNaN(o)) continue;
                        if (Math.Abs(o - h) > worst) (worst, wi, wj) = (Math.Abs(o - h), gi0 + i, gj0 + j);
                    }
                }
            Check("pyramid steps", worst <= PyramidStepM, $"steepest {worst:F2} m over 2 lattice points at ({wi},{wj}) {Lv95Of(centre.Face, wi, wj, level)}");
            if (worst > PyramidStepM && args.Contains("--why"))
                foreach (var (di, dj) in new[] { (0, 0), (2, 0), (0, 2) })
                {
                    var levels = string.Join("  ", Enumerable.Range(9, pyramid.FinestLevel - 8).Reverse().Select(l =>
                    {
                        double h = pyramid.HeightAt(centre.Face, wi + di, wj + dj, level, out int a, l);
                        return $"L{l}:{h:F1}(L{a})";
                    }));
                    WriteLine($"    +({di},{dj}): {levels}");
                }

            // the leaves the game builds there, each on the first ask; the far ground of each before
            var leaves = EarthLeaves.WithFill(store, world, alat, alon, roads: false, pyramid: pyramid);
            leaves.Log = WriteLine;
            leaves.Reach(lat, lon);
            var far = new Dictionary<CubeKey, float[]>();
            var made = new Dictionary<CubeKey, float[]>();
            for (int dy = -half; dy <= half; dy++)
                for (int dx = -half; dx <= half; dx++)
                {
                    if (centre.Neighbour(dx, dy) is not { } k || !leaves.Has(k)) continue;
                    if (leaves.HeightsOf(k) is { } fh) far[k] = fh.Heights;
                }
            for (int dy = -half; dy <= half; dy++)
                for (int dx = -half; dx <= half; dx++)
                {
                    if (centre.Neighbour(dx, dy) is not { } k || !leaves.Has(k)) continue;
                    if (leaves.Get(k)?.Heights is { } mh) made[k] = mh.Heights;
                }
            int real = made.Keys.Count(store.Has);
            WriteLine($"  {made.Count} leaves made ({real} the region's), {far.Count} far");
            var (edge, edgeAt) = Edges(made, made, cells);
            Check("made leaf edges", edge <= EdgeM, $"worst {edge:F3} m{edgeAt}");
            var (farEdge, farAt) = Edges(far, made, cells);
            Check("far ground beside made leaves", farEdge <= FarEdgeM, $"worst {farEdge:F2} m{farAt}");

            // the globe's coarser ground beside a drawn leaf: the pyramid's levels 13 down to 11 along each leaf's edges,
            // its mean miss there (a ledge is a bias; a coarse level's own smoothing of the relief is not)
            for (int l = pyramid.FinestLevel; l >= pyramid.FinestLevel - 2; l--)
            {
                double worstBias = 0;
                string biasAt = "";
                foreach (var (k, h) in made)
                {
                    long ki = (long)k.X << bits, kj = (long)k.Y << bits;
                    foreach (int edgeSide in new[] { 0, 1, 2, 3 })
                    {
                        double sum = 0;
                        int count = 0;
                        for (int s = 0; s <= cells; s++)
                        {
                            int i = edgeSide switch { 0 => 0, 1 => cells, _ => s }, j = edgeSide switch { 2 => 0, 3 => cells, _ => s };
                            float lh = h[j * (cells + 1) + i];
                            double ph = pyramid.HeightAt(k.Face, ki + i, kj + j, level, out _, l);
                            if (float.IsNaN(lh) || double.IsNaN(ph)) continue;
                            sum += lh - ph;
                            count++;
                        }
                        if (count < cells / 2) continue;
                        double bias = sum / count;
                        if (Math.Abs(bias) > Math.Abs(worstBias)) (worstBias, biasAt) = (bias, $" on {k}'s {"WESN"[edgeSide]} edge");
                    }
                }
                Check($"pyramid level {l} along the leaves' edges", Math.Abs(worstBias) <= LedgeM, $"worst mean miss {worstBias:F2} m{biasAt}");
            }
        }
        WriteLine(fails == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({fails})");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>The worst difference on the shared edges of a leaf of <paramref name="a"/> and its east or north neighbour in <paramref name="b"/>.</summary>
    private static (double Worst, string Where) Edges(Dictionary<CubeKey, float[]> a, Dictionary<CubeKey, float[]> b, int cells)
    {
        int n = cells + 1;
        double worst = 0;
        string where = "";
        foreach (var (k, ha) in a)
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if (k.Neighbour(dx, dy) is not { } o || !b.TryGetValue(o, out var hb)) continue;
                for (int s = 0; s < n; s++)
                {
                    // a's vertex on the edge and the same vertex in b
                    int ia = dx == 1 ? cells : dx == -1 ? 0 : s, ja = dy == 1 ? cells : dy == -1 ? 0 : s;
                    int ib = dx == 1 ? 0 : dx == -1 ? cells : s, jb = dy == 1 ? 0 : dy == -1 ? cells : s;
                    float x = ha[ja * n + ia], y = hb[jb * n + ib];
                    if (float.IsNaN(x) || float.IsNaN(y)) continue;
                    if (Math.Abs(x - y) > worst)
                    {
                        worst = Math.Abs(x - y);
                        where = $" between {k} ({x:F1}) and {o} ({y:F1}) at vertex {s}";
                    }
                }
            }
        return (worst, where);
    }

    private static string Lv95Of(int face, long gi, long gj, int level)
    {
        var g = CubeSphere.ToGeodetic(face, CubeSphere.Lattice(level, gi), CubeSphere.Lattice(level, gj));
        var (e, n) = Lv95Source.Instance.FromLatLon(g.LatDeg, g.LonDeg);
        return $"LV95 {e:F0},{n:F0}";
    }

    private static string? Value(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
