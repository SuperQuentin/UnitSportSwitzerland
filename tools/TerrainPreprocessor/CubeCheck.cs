using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// G1's measurements on a built cube region (#1443; <c>--cube-check</c>, or <c>--cube-check-only</c>
/// on an existing one): every vertex against its rigorous ECEF point (target 1 cm), the control-point
/// resampling against the exact one, shared edges between every pair of built neighbours (exact), a
/// tangent-plane collision field against the mesh (target 2 cm), and the features against the
/// terrain they stand on.
/// </summary>
public static class CubeCheck
{
    public static int Run(CubeStage.Options o, CubeHeightSource src)
    {
        var manifest = CubeManifest.FromJson(File.ReadAllText(Path.Combine(o.Out!, CubeManifest.FileName)));
        int bits = manifest.Bits, n = (1 << bits) + 1;
        var tiles = new Dictionary<CubeKey, CubeTile>();
        foreach (var path in Directory.EnumerateFiles(o.Out!, "*.cubb"))
            foreach (var t in CubeBundle.ReadAll(path)) tiles[t.Key] = t;
        Console.WriteLine($"check: {tiles.Count} tiles decoded");
        bool ok = true;

        // ---- shared edges: every pair of built neighbours, exact --------------------------------
        long pairs = 0, compared = 0, differ = 0;
        int lv = manifest.LeafLevel + bits;
        foreach (var (key, tile) in tiles)
            for (int side = 0; side < 2; side++)
            {
                var other = key.EdgeNeighbour(side);
                if (!tiles.TryGetValue(other, out var ot)) continue;
                pairs++;
                for (int s = 0; s < n; s++)
                {
                    int i = side == 0 ? n - 1 : s, j = side == 0 ? s : n - 1;
                    long gi = ((long)key.X << bits) + i, gj = ((long)key.Y << bits) + j;
                    var at = other.Face == key.Face ? (gi, gj) : CubeSphere.LatticeOn(key.Face, lv, gi, gj, other.Face)!.Value;
                    int oi = (int)(at.Item1 - ((long)other.X << bits)), oj = (int)(at.Item2 - ((long)other.Y << bits));
                    float a = tile.Heights[j * n + i], b = ot.Heights[oj * n + oi];
                    if (float.IsNaN(a) && float.IsNaN(b)) continue;
                    compared++;
                    if (a != b) differ++;
                }
            }
        Console.WriteLine($"check: shared edges: {pairs} neighbour pairs, {compared} edge vertices, {differ} differ (target 0)");
        ok &= differ == 0;

        // ---- sample tiles: spread over the region, the foreign ones included -------------------
        var sample = tiles.Keys.OrderBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X).Where((_, i) => i % Math.Max(1, tiles.Count / 12) == 0).Take(12)
            .Concat(tiles.Values.Where(t => t.Roads.Any(r => r.Source == CubeSource.BdTopo)).Select(t => t.Key).Take(4)).Distinct().ToList();
        var resampler = new CubeResampler(src);
        var interp = new List<double>();
        var total = new List<double>();
        var geom = new List<double>();
        var coll1 = new List<double>();
        var collHalf = new List<double>();
        var coll2 = new List<double>();
        var featTlm = new List<double>();
        var featBd = new List<double>();
        double spacing = 0;
        foreach (var key in sample)
        {
            var tile = tiles[key];
            var cur = new CubeHeightSource.Cursors(src.Dems.Count);
            var fresh = resampler.Heights(key, bits, cur)!;
            var verts = CubeGeometry.Vertices(key, bits, tile.Heights);
            var anchor = key.Anchor;
            var rng = new Random(key.GetHashCode());
            for (int k = 0; k < 2000; k++)
            {
                int i = rng.Next(1, n - 1), j = rng.Next(1, n - 1);
                float stored = tile.Heights[j * n + i];
                if (float.IsNaN(stored)) continue;
                var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(lv, ((long)key.X << bits) + i), CubeSphere.Lattice(lv, ((long)key.Y << bits) + j));
                double exact = src.ExactHeight(normal, cur);
                if (double.IsNaN(exact)) continue;
                interp.Add(Math.Abs(fresh[j * n + i] - exact));
                // the mesh vertex against the rigorous point of the ground
                var (x, y, z) = anchor.ToLocal(CubeSphere.OnEllipsoid(normal, exact));
                int v = (j * n + i) * 3;
                total.Add(Math.Sqrt(Sq(verts[v] - x) + Sq(verts[v + 1] - y) + Sq(verts[v + 2] - z)));
                var (gx, gy, gz) = CubeGeometry.Exact(key, bits, i, j, stored);
                geom.Add(Math.Sqrt(Sq(verts[v] - gx) + Sq(verts[v + 1] - gy) + Sq(verts[v + 2] - gz)));
            }

            // collision: a tangent-plane field over the middle of the tile, at the mesh's spacing and twice it
            double cell = key.Vertex(bits, n / 2, n / 2, 0).DistanceTo(key.Vertex(bits, n / 2 + 1, n / 2, 0));
            spacing = Math.Max(spacing, cell);
            var hint = (n / 2, n / 2);
            double MeshAt(double px, double pz) => CubeGeometry.MeshHeightAt(verts, n, px, pz, ref hint);
            foreach (var (factor, into) in new[] { (0.5, collHalf), (1.0, coll1), (2.0, coll2) })
            {
                double s = cell * factor;
                int side = (int)(cell * (n - 1) * 0.6 / s);
                var field = CubeGeometry.CollisionField(MeshAt, side, s);
                double half = (side - 1) * s / 2;
                for (int k = 0; k < 3000; k++)
                {
                    double px = -half + rng.NextDouble() * (side - 1) * s, pz = -half + rng.NextDouble() * (side - 1) * s;
                    double mesh = MeshAt(px, pz);
                    var (fa, fb) = FieldAt(field, side, s, half, px, pz);
                    if (!double.IsNaN(mesh) && !double.IsNaN(fa) && !double.IsNaN(fb)) into.Add(Math.Max(Math.Abs(mesh - fa), Math.Abs(mesh - fb)));
                }
            }

            // features: each point against the mesh straight below it
            foreach (var (pts, source) in tile.Roads.Select(r => (r.Points, r.Source)).Concat(tile.Buildings.Select(b => (b.Points, b.Source))))
                for (int p = 0; p < pts.Length; p += 3)
                {
                    double mesh = MeshAt(pts[p], pts[p + 2]);
                    if (double.IsNaN(mesh)) continue;
                    (source == CubeSource.BdTopo ? featBd : featTlm).Add(Math.Abs(pts[p + 1] - mesh));
                }
        }

        Console.WriteLine($"check: {sample.Count} sample tiles: {string.Join(", ", sample.Take(6).Select(k => k.Describe()))}{(sample.Count > 6 ? ", ..." : "")}");
        Report("resampling, control points against exact (height)", interp, 0.001);
        Report("vertex geometry, control grid against rigorous", geom, 0.01);
        ok &= Report("mesh vertex against the rigorous ground point (target 1 cm)", total, 0.01);
        ok &= Report($"collision field at the mesh's spacing ({spacing:F2} m) against the mesh (target 2 cm)", coll1, 0.02);
        Report($"collision field at half the spacing ({spacing / 2:F2} m) against the mesh", collHalf, 0.02);
        Report($"collision field at twice the spacing ({2 * spacing:F2} m) against the mesh", coll2, 0.02);
        Report("swissTLM3D feature points against the mesh below", featTlm, double.NaN);
        Report("BD TOPO feature points against the mesh below", featBd, double.NaN);
        Console.WriteLine(ok ? "check: PASS" : "check: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>A height field's value at (x, z) with each cell split along one diagonal and along the other: the worse of the two is what is measured, whichever the physics engine takes.</summary>
    private static (double A, double B) FieldAt(float[] f, int side, double s, double half, double x, double z)
    {
        double gx = (x + half) / s, gz = (z + half) / s;
        int c = Math.Clamp((int)gx, 0, side - 2), r = Math.Clamp((int)gz, 0, side - 2);
        double fx = gx - c, fz = gz - r;
        double h00 = f[r * side + c], h10 = f[r * side + c + 1], h01 = f[(r + 1) * side + c], h11 = f[(r + 1) * side + c + 1];
        // diagonal 00-11
        double a = fx >= fz ? h00 + (h10 - h00) * fx + (h11 - h10) * fz : h00 + (h11 - h01) * fx + (h01 - h00) * fz;
        // diagonal 10-01
        double b = fx + fz <= 1 ? h00 + (h10 - h00) * fx + (h01 - h00) * fz : h11 + (h01 - h11) * (1 - fx) + (h10 - h11) * (1 - fz);
        return (a, b);
    }

    private static bool Report(string what, List<double> errors, double target)
    {
        if (errors.Count == 0) { Console.WriteLine($"  {what}: no samples"); return true; }
        errors.Sort();
        double p50 = errors[errors.Count / 2], p90 = errors[(int)(errors.Count * 0.9)], p99 = errors[(int)(errors.Count * 0.99)], max = errors[^1];
        bool pass = double.IsNaN(target) || max <= target;
        string over = double.IsNaN(target) ? "" : $", {errors.Count(e => e > target) * 100.0 / errors.Count:F1} % over {target * 1000:F0} mm";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {what}: {errors.Count} samples, median {p50 * 1000:F2} mm, p90 {p90 * 1000:F2} mm, p99 {p99 * 1000:F2} mm, max {max * 1000:F2} mm{over}{(double.IsNaN(target) ? "" : pass ? "  ok" : "  OVER")}"));
        return pass;
    }

    private static double Sq(double a) => a * a;
}
