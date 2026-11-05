using System.Collections.Concurrent;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// G1's measurements on a built cube region (#1443; <c>--cube-check</c>, or <c>--cube-check-only</c>
/// on an existing one): every vertex against its rigorous ECEF point (target 1 cm), the control-point
/// resampling against the exact one, shared edges between every pair of built neighbours (exact; #1507
/// counts those across frame and face edges), a tangent-plane collision field against the mesh (target
/// 2 cm), and the features against the terrain they stand on. #1507 adds the frame ground
/// (<see cref="FrameGround"/>) against the sources, and with <c>--cube-terr DIR</c> against the LV95
/// build's <c>.terr</c> heights (<see cref="Lv95Ground"/>) at the same places. Once the water stage has
/// run, the leaves hold lake and river beds where the sources hold the surface (#1568): the rows against
/// the sources leave out the points under a leaf's water and say how many.
/// </summary>
public static class CubeCheck
{
    /// <summary>
    /// Every leaf's heights in a region: the <see cref="LeafKind.Heights"/> sections of its <c>.cubl</c>
    /// bundles (#1507), or G1's <c>.cubb</c> tiles when it has no <c>.cubl</c>.
    /// </summary>
    public static ConcurrentDictionary<CubeKey, CubeTile> ReadLeafHeights(string dir, int jobs)
    {
        var tiles = new ConcurrentDictionary<CubeKey, CubeTile>();
        var leaves = Directory.GetFiles(dir, "*.cubl");
        var opts = new ParallelOptions { MaxDegreeOfParallelism = jobs };
        if (leaves.Length > 0)
            Parallel.ForEach(leaves, opts, path =>
            {
                foreach (var (key, bytes) in CubeBundle.ReadAllEntries(path))
                    if (CubeLeafCodec.ReadSection(bytes, LeafKind.Heights) is { } s) tiles[key] = LeafHeights.Decode(s);
            });
        else
            Parallel.ForEach(Directory.GetFiles(dir, "*.cubb"), opts, path =>
            {
                foreach (var t in CubeBundle.ReadAll(path)) tiles[t.Key] = t;
            });
        return tiles;
    }

    /// <summary>The <see cref="LeafKind.Water"/> sections of a region's <c>.cubl</c> bundles: the leaves the water stage wrote beds into.</summary>
    private static Dictionary<CubeKey, CubeWater> ReadLeafWater(string dir, int jobs)
    {
        var water = new ConcurrentDictionary<CubeKey, CubeWater>();
        Parallel.ForEach(Directory.GetFiles(dir, "*.cubl"), new ParallelOptions { MaxDegreeOfParallelism = jobs }, path =>
        {
            foreach (var (key, bytes) in CubeBundle.ReadAllEntries(path))
                if (CubeLeafCodec.ReadSection(bytes, LeafKind.Water) is { } s) water[key] = LeafWater.Decode(s);
        });
        return new(water);
    }

    /// <summary>
    /// Whether lattice point (i, j), fractional or not, is under the leaf's water: a still level on any of
    /// the water samples (every second vertex) round it. The shore's dry vertices next to the water are
    /// left out with it, which costs a few samples and keeps the test free of the bed's own shape.
    /// </summary>
    private static bool UnderWater(CubeWater? w, double i, double j)
    {
        if (w == null) return false;
        int side = w.Side, ci = (int)Math.Floor(i / 2), cj = (int)Math.Floor(j / 2);
        for (int dj = 0; dj <= 1; dj++)
            for (int di = 0; di <= 1; di++)
            {
                int a = Math.Clamp(ci + di, 0, side - 1), b = Math.Clamp(cj + dj, 0, side - 1);
                if (!float.IsNaN(w.Level[b * side + a])) return true;
            }
        return false;
    }

    public static int Run(CubeStage.Options o, CubeHeightSource src)
    {
        var manifest = CubeManifest.FromJson(File.ReadAllText(Path.Combine(o.Out!, CubeManifest.FileName)));
        int bits = manifest.Bits, n = (1 << bits) + 1;
        var tiles = new Dictionary<CubeKey, CubeTile>(ReadLeafHeights(o.Out!, o.Jobs));
        var water = ReadLeafWater(o.Out!, o.Jobs);
        Console.WriteLine($"check: {tiles.Count} tiles decoded, {water.Count} with water");
        bool ok = true;

        // ---- shared edges: every pair of built neighbours, exact --------------------------------
        long pairs = 0, compared = 0, differ = 0, framePairs = 0, facePairs = 0, frameDiffer = 0;
        int lv = manifest.LeafLevel + bits;
        foreach (var (key, tile) in tiles)
            for (int side = 0; side < 2; side++)
            {
                var other = key.EdgeNeighbour(side);
                if (!tiles.TryGetValue(other, out var ot)) continue;
                pairs++;
                bool acrossFrames = other.Face != key.Face || other.AncestorAt(manifest.BundleLevel) != key.AncestorAt(manifest.BundleLevel);
                if (acrossFrames) framePairs++;
                if (other.Face != key.Face) facePairs++;
                for (int s = 0; s < n; s++)
                {
                    int i = side == 0 ? n - 1 : s, j = side == 0 ? s : n - 1;
                    long gi = ((long)key.X << bits) + i, gj = ((long)key.Y << bits) + j;
                    var at = other.Face == key.Face ? (gi, gj) : CubeSphere.LatticeOn(key.Face, lv, gi, gj, other.Face)!.Value;
                    int oi = (int)(at.Item1 - ((long)other.X << bits)), oj = (int)(at.Item2 - ((long)other.Y << bits));
                    float a = tile.Heights[j * n + i], b = ot.Heights[oj * n + oi];
                    if (float.IsNaN(a) && float.IsNaN(b)) continue;
                    compared++;
                    if (a != b)
                    {
                        differ++;
                        if (acrossFrames) frameDiffer++;
                    }
                }
            }
        Console.WriteLine($"check: shared edges: {pairs} neighbour pairs ({framePairs} across frame edges, {facePairs} across face edges), {compared} edge vertices, "
            + $"{differ} differ, {frameDiffer} of them across frames (target 0)");
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
        int wet = 0;
        foreach (var key in sample)
        {
            var tile = tiles[key];
            var w = water.GetValueOrDefault(key);
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
                int v = (j * n + i) * 3;
                var (gx, gy, gz) = CubeGeometry.Exact(key, bits, i, j, stored);
                geom.Add(Math.Sqrt(Sq(verts[v] - gx) + Sq(verts[v + 1] - gy) + Sq(verts[v + 2] - gz)));
                // the mesh vertex against the rigorous point of the ground: a bed against the sources' lake surface says nothing
                if (UnderWater(w, i, j)) { wet++; continue; }
                var (x, y, z) = anchor.ToLocal(CubeSphere.OnEllipsoid(normal, exact));
                total.Add(Math.Sqrt(Sq(verts[v] - x) + Sq(verts[v + 1] - y) + Sq(verts[v + 2] - z)));
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
        ok &= Report($"mesh vertex against the rigorous ground point (target 1 cm; {wet} under water left out)", total, 0.01);
        // informational since G1 (#1452): the height field misses 2 cm against the mesh, so collision is trimesh patches (C2)
        Report($"collision field at the mesh's spacing ({spacing:F2} m) against the mesh (G1: a height field misses, collision is trimesh patches)", coll1, double.NaN);
        Report($"collision field at half the spacing ({spacing / 2:F2} m) against the mesh", collHalf, 0.02);
        Report($"collision field at twice the spacing ({2 * spacing:F2} m) against the mesh", coll2, 0.02);
        Report("swissTLM3D feature points against the mesh below", featTlm, double.NaN);
        Report("BD TOPO feature points against the mesh below", featBd, double.NaN);
        ok &= GroundCheck(o, src, manifest, tiles, water, sample);
        Console.WriteLine(ok ? "check: PASS" : "check: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The frame ground (#1507): on the sample leaves, the leaf mesh read through each leaf's work frame
    /// (<see cref="FrameGround"/>) against the sources' height at the same place, and with
    /// <c>--cube-terr</c> against the LV95 build's <c>.terr</c> (<see cref="Lv95Ground"/>) at that place's
    /// LV95 position; then points on shared edges across frame edges, sampled from both frames (the same
    /// height to a micrometre: the mesh edge is the same bits and the edges' sea level is rigorous).
    /// </summary>
    private static bool GroundCheck(CubeStage.Options o, CubeHeightSource src, CubeManifest manifest, Dictionary<CubeKey, CubeTile> tiles, Dictionary<CubeKey, CubeWater> water, List<CubeKey> sample)
    {
        int bits = manifest.Bits, cells = 1 << bits;
        var grounds = new Dictionary<CubeKey, FrameGround>();
        FrameGround GroundOf(CubeKey leaf)
        {
            var cell = leaf.AncestorAt(manifest.BundleLevel);
            if (!grounds.TryGetValue(cell, out var g))
                grounds[cell] = g = new FrameGround(new WorkFrame(cell, SwissSeaLevel.Instance), k => tiles.GetValueOrDefault(k), manifest.LeafLevel);
            return g;
        }
        var grids = new Dictionary<TileId, ChunkGrid>();
        var lv95 = new Lv95Ground(grids);
        var toSources = new List<double>();
        var toTerr = new List<double>();
        int beds = 0, wet = 0;
        int misses = 0, points = 0;
        foreach (var key in sample)
        {
            var ground = GroundOf(key);
            var t = ground.TransformOf(key)!;
            if (o.Terr != null)
            {
                var (minE, minN, maxE, maxN) = CubeStage.Lv95Box(new[] { key }, bits);
                for (int e = (int)Math.Floor(minE / 1000); e <= (int)Math.Floor(maxE / 1000); e++)
                    for (int nn = (int)Math.Floor(minN / 1000); nn <= (int)Math.Floor(maxN / 1000); nn++)
                    {
                        var id = new TileId(e, nn);
                        string path = Path.Combine(o.Terr, ChunkFormat.ChunkFileName(id));
                        if (!grids.ContainsKey(id) && File.Exists(path))
                            using (var fs = File.OpenRead(path)) grids[id] = ChunkCodec.Decode(fs);
                    }
            }
            var cur = new CubeHeightSource.Cursors(src.Dems.Count);
            var rng = new Random(key.GetHashCode() ^ 1507);
            var w = water.GetValueOrDefault(key);
            for (int k = 0; k < 2000; k++)
            {
                double i = rng.NextDouble() * cells, j = rng.NextDouble() * cells;
                var (x, y) = t.LatticeToPlane(i, j);
                points++;
                if (ground.Sample(x, y) is not { } h)
                {
                    int qi = Math.Min((int)i, cells - 1), qj = Math.Min((int)j, cells - 1);
                    var th = tiles[key];
                    if (!float.IsNaN(th.HeightAt(qi, qj)) && !float.IsNaN(th.HeightAt(qi + 1, qj)) && !float.IsNaN(th.HeightAt(qi, qj + 1)) && !float.IsNaN(th.HeightAt(qi + 1, qj + 1))) misses++;
                    continue;
                }
                double exact = src.ExactHeight(t.NormalAt(i, j), cur);
                bool underWater = UnderWater(w, i, j);
                if (underWater) wet++;
                else if (!double.IsNaN(exact)) toSources.Add(Math.Abs(h - (exact - t.UndulationAt(i, j))));
                if (o.Terr != null)
                {
                    var (e, nn, _) = SwissGeodesy.FromEcef(ground.Frame.ToEcef(x, y, h));
                    if (lv95.Sample(e, nn) is { } ht)
                    {
                        // a lake on a leaf without the water stage: the LV95 build holds its bed (#298), the leaf the surveyed surface
                        if (!underWater && h - ht > 1) beds++;
                        else toTerr.Add(Math.Abs(h - ht));
                    }
                }
            }
        }
        Console.WriteLine($"check: frame ground on {sample.Count} sample leaves: {points} points, {misses} found no ground over data (target 0)");
        Report($"frame ground (leaf mesh, quads split as drawn) against the sources' height there ({wet} points under water left out)", toSources, double.NaN);
        if (o.Terr != null)
        {
            Report($"frame ground against the LV95 build's .terr at the same place ({grids.Count} tiles; {beds} points over lake beds left out)", toTerr, double.NaN);
            if (toTerr.Count > 0) Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  of those within 5 cm {toTerr.Count(d => d <= 0.05) * 100.0 / toTerr.Count:F1} %, within 50 cm {toTerr.Count(d => d <= 0.5) * 100.0 / toTerr.Count:F1} % (.terr: 1 m lattice, 7 cm quanta)"));
        }

        // points on edges shared across frames, from both frames' grounds
        var across = new List<double>();
        foreach (var (key, _) in tiles.OrderBy(e => e.Key.Face).ThenBy(e => e.Key.Y).ThenBy(e => e.Key.X))
        {
            if (across.Count >= 4000) break;
            for (int side = 0; side < 2; side++)
            {
                var other = key.EdgeNeighbour(side);
                if (!tiles.ContainsKey(other) || other.AncestorAt(manifest.BundleLevel) == key.AncestorAt(manifest.BundleLevel)) continue;
                var ga = GroundOf(key);
                var gb = GroundOf(other);
                var ta = ga.TransformOf(key)!;
                for (int s = 0; s < 20; s++)
                {
                    double f = (s + 0.37) * cells / 20.0;
                    var (i, j) = side == 0 ? ((double)cells, f) : (f, (double)cells);
                    var (xa, ya) = ta.LatticeToPlane(i, j);
                    // the same place on frame B's plane: through its latitude and longitude
                    var (lat, lon) = ga.Frame.Plane.FromPlane(xa, ya);
                    var (xb, yb) = gb.Frame.Plane.ToPlane(lat, lon);
                    if (ga.Sample(xa, ya) is { } ha && gb.Sample(xb, yb) is { } hb) across.Add(Math.Abs(ha - hb));
                }
            }
        }
        return Report("frame ground on edges shared across frames, from both frames (target 1 µm)", across, 1e-6) && misses == 0;
    }

    /// <summary>
    /// <c>--cube-dump F0/L13/x_y</c>: one built tile as JSON for a picture (<c>&lt;out&gt;/F0_L13_x_y.json</c>):
    /// its surface every 4th vertex (local x, y, z), and its features; for BD TOPO points also where
    /// the LV95 path would have put them (<see cref="SwissProjection"/>'s LV95 taken as the place).
    /// </summary>
    public static int Dump(CubeStage.Options o, CubeKey key)
    {
        var manifest = CubeManifest.FromJson(File.ReadAllText(Path.Combine(o.Out!, CubeManifest.FileName)));
        string leaves = Path.Combine(o.Out!, CubeBundle.LeafFileName(key.AncestorAt(manifest.BundleLevel)));
        var tile = (File.Exists(leaves)
                ? CubeBundle.ReadLeaf(leaves, key)?.Find(LeafKind.Heights) is { } s ? LeafHeights.Decode(s) : null
                : CubeBundle.Read(Path.Combine(o.Out!, CubeBundle.FileName(key.AncestorAt(manifest.BundleLevel))), key))
            ?? throw new InvalidOperationException($"{key} is not built");
        var anchor = key.Anchor;
        var v = CubeGeometry.Vertices(key, tile.Bits, tile.Heights);
        int n = tile.Side;
        var sb = new System.Text.StringBuilder();
        string F(double x) => x.ToString("F2", CultureInfo.InvariantCulture);
        sb.Append($"{{\"key\":\"{key.Describe()}\",\"n\":{(n - 1) / 4 + 1},\"grid\":[");
        bool first = true;
        for (int j = 0; j < n; j += 4)
            for (int i = 0; i < n; i += 4)
            {
                int k = (j * n + i) * 3;
                sb.Append(first ? "" : ",").Append(float.IsNaN(v[k + 1]) ? "null,null,null" : $"{F(v[k])},{F(v[k + 1])},{F(v[k + 2])}");
                first = false;
            }
        sb.Append("],\"roads\":[");
        first = true;
        foreach (var r in tile.Roads)
        {
            sb.Append(first ? "" : ",").Append($"{{\"src\":{(int)r.Source},\"cls\":\"{r.Class}\",\"pts\":[{string.Join(",", r.Points.Select(p => F(p)))}],\"old\":[");
            if (r.Source == CubeSource.BdTopo)
                for (int p = 0; p < r.Points.Length; p += 3)
                {
                    var e = anchor.ToEcef(r.Points[p], r.Points[p + 1], r.Points[p + 2]);
                    var g = Ellipsoid.Wgs84.ToGeodetic(e);
                    var (eo, no) = SwissProjection.ToLv95(g.LatDeg, g.LonDeg);
                    var (_, _, h) = SwissGeodesy.FromEcef(e);
                    var (x, _, z) = anchor.ToLocal(SwissGeodesy.ToEcef(eo, no, h));
                    sb.Append(p == 0 ? "" : ",").Append($"{F(x)},{F(z)}");
                }
            sb.Append("]}");
            first = false;
        }
        sb.Append("],\"buildings\":[");
        sb.Append(string.Join(",", tile.Buildings.Select(b => $"{{\"src\":{(int)b.Source},\"pts\":[{string.Join(",", b.Points.Select(p => F(p)))}]}}")));
        sb.Append("]}");
        string path = Path.Combine(o.Out!, key.FileStem + ".json");
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"dump: {path}");
        return 0;
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
