using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain;

/// <summary>
/// <c>--cubeharness &lt;region of .cubl leaves&gt; --cubepatches [--cubeat lat,lon]</c> (#1511, G2 C2): the collision
/// patches (<see cref="LeafCollision"/>) on the steepest leaf with ground everywhere, a middling one, and the leaf at
/// <c>--cubeat</c> (default Geneva's centre, where the roads are). Per leaf: the time to prepare it (vertices, the road
/// blend from its and its neighbours' roads, the road and building faces), every one of its 16 patches built as a Jolt
/// trimesh body (the time with one physics frame, the memory, the triangles), and 3,000 vertical rays against
/// ground-only patches compared with the blended mesh they are cut from (target: 2 cm). Then the provider
/// (<see cref="CollisionPatches"/>) round a body at the leaf's centre: the patches it asks for and the time they take.
/// Writes <c>test_output/cubeharness/&lt;region&gt;-patches.md</c>; RESULT FAILED if more than 1 % of rays miss by 2 cm.
/// </summary>
public partial class CubeHarness
{
    private const double PatchRayBarM = 0.02;

    private async void StartPatches()
    {
        try { await Patches(); }
        catch (Exception e)
        {
            // a measurement that throws must still end the run
            GD.PrintErr($"[cubeharness] {e}");
            GD.Print("[cubeharness] RESULT: FAILED (threw)");
            GetTree().Quit(1);
        }
    }

    private async Task Patches()
    {
        var index = new Dictionary<CubeKey, (string Path, long Offset, int Length)>();
        foreach (var path in Directory.EnumerateFiles(_dir, "*.cubl"))
        {
            using var fs = File.OpenRead(path);
            foreach (var (key, at) in CubeBundle.ReadIndex(fs, CubeBundle.LeafMagic)) index[key] = (path, at.Offset, at.Length);
        }
        var cache = new ConcurrentDictionary<CubeKey, Lazy<CubeLeafContent?>>();
        CubeLeafContent? Leaf(CubeKey k) => cache.GetOrAdd(k, key => new Lazy<CubeLeafContent?>(() =>
        {
            if (!index.TryGetValue(key, out var at)) return null;
            using var fs = File.OpenRead(at.Path);
            fs.Position = at.Offset;
            var bytes = new byte[at.Length];
            fs.ReadExactly(bytes);
            return CubeLeafContent.Decode(bytes);
        })).Value;

        var manifest = CubeManifest.FromJson(File.ReadAllText(Path.Combine(_dir, CubeManifest.FileName)));
        var byRange = manifest.Tiles.Where(t => t.Max > t.Min).OrderByDescending(t => t.Max - t.Min).Select(t => CubeKey.Parse(t.Key)).ToList();
        bool Full(CubeKey k) => Leaf(k)?.Heights is { } h && !h.Heights.Any(float.IsNaN);
        var near = (CmdArgs.Value("--cubeat") ?? "46.2044,6.1432").Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var picks = new[] { byRange.First(Full), byRange.Skip(byRange.Count / 2).First(Full), LeafFrames.LeafAt(near[0], near[1]) }
            .Where(index.ContainsKey).Distinct().ToList();

        _report.AppendLine($"# Collision patches: {Path.GetFileName(_dir.TrimEnd('/', '\\'))}");
        _report.AppendLine();
        _report.AppendLine($"{index.Count} leaves. A patch is a level-{LeafCollision.PatchLevel} cell (128² lattice cells), {LeafCollision.PerSide * LeafCollision.PerSide} a leaf; "
            + "G1's estimate 50-60 ms and 1-3 MB a patch. Rays straight down the leaf's up against ground-only patches, compared with the blended mesh.");
        _report.AppendLine();
        _report.AppendLine("| Leaf | Roads / buildings | Prepare (ms) | Patch build + physics frame ms (mean / max) | Memory MB a patch (mean) | Triangles a patch (ground / features, mean) | Rays | Median (mm) | p99 (mm) | Max (mm) | Over 2 cm |");
        _report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        var source = new LeafCollisionSource(Leaf);
        int fails = 0;
        foreach (var key in picks)
        {
            var content = Leaf(key)!;
            var sw = Stopwatch.StartNew();
            var lc = source.Prepared(key)!;
            double prepareMs = sw.Elapsed.TotalMilliseconds;

            // every patch as a body: the time with a physics frame, the memory, its triangles
            var times = new List<double>();
            var mems = new List<double>();
            long ground = 0, features = 0;
            var bodies = new List<StaticBody3D>();
            foreach (var patch in LeafCollision.PatchesOf(key))
            {
                var faces = lc.Patch(patch);
                int g = lc.Patch(patch, features: false).Length;
                ground += g / 3;
                features += (faces.Length - g) / 3;
                long mem0 = WorkingSet();
                sw.Restart();
                bodies.Add(Body(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces } }, Transform3D.Identity, 1u << 22));
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                times.Add(sw.Elapsed.TotalMilliseconds);
                mems.Add((WorkingSet() - mem0) / 1e6);
            }
            foreach (var b in bodies) b.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

            // rays against the ground-only patches, compared with the blended mesh they are cut from
            bodies.Clear();
            foreach (var patch in LeafCollision.PatchesOf(key))
                bodies.Add(Body(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = lc.Patch(patch, features: false) } }, Transform3D.Identity, 1u << 23));
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var verts = lc.Vertices.ToArray();
            int n = lc.Side;
            var hint = (n / 2, n / 2);
            var space = GetWorld3D().DirectSpaceState;
            var rng = new Random(key.GetHashCode());
            var err = new List<double>();
            double reach = Math.Abs(verts[(n - 1) * 3] - verts[0]) / 2 * 0.9;
            for (int k = 0; k < 3000; k++)
            {
                double x = (rng.NextDouble() * 2 - 1) * reach, z = (rng.NextDouble() * 2 - 1) * reach;
                double y = CubeGeometry.MeshHeightAt(verts, n, x, z, ref hint);
                if (double.IsNaN(y)) continue;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3((float)x, (float)y + 60, (float)z), new Vector3((float)x, (float)y - 60, (float)z), 1u << 23));
                err.Add(hit.Count == 0 ? double.PositiveInfinity : Math.Abs(hit["position"].AsVector3().Y - y));
            }
            foreach (var b in bodies) b.QueueFree();
            err.Sort();
            double over = err.Count == 0 ? 1 : err.Count(e => e > PatchRayBarM) / (double)err.Count;
            if (over > 0.01) fails++;
            var mt = manifest.Tiles.FirstOrDefault(t => t.Key == key.ToString());
            string name = $"{key} ({(mt is null ? 0 : mt.Max - mt.Min):F0} m relief)";
            string feats = $"{content.Roads?.Segments.Count ?? 0} segments / {content.Buildings?.Buildings.Count ?? 0}";
            string row = $"| {name} | {feats} | {prepareMs:F0} | {times.Average():F0} / {times.Max():F0} | {mems.Average():F1} | {ground / 16:N0} / {features / 16:N0} | {err.Count} | "
                + $"{PctOf(err, 0.5) * 1000:F1} | {PctOf(err, 0.99) * 1000:F1} | {PctOf(err, 1) * 1000:F1} | {over * 100:F2} % |";
            _report.AppendLine(row);
            GD.Print($"[cubeharness] patches {row}");
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }

        // the provider round a body at the last pick's centre, standing
        var centre = picks[^1].Anchor.Origin;
        var provider = new CollisionPatches(source.Build, workers: 2);
        var clock = Stopwatch.StartNew();
        provider.Update([new CollisionPatches.Body(centre, centre, false)]);
        while (provider.Busy) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        int ready = 0, empty = 0;
        while (provider.TryTakeReady(out _, out var faces)) { ready++; if (faces is null) empty++; }
        _report.AppendLine();
        _report.AppendLine($"Provider round a standing body at {picks[^1]}'s centre (reach {CollisionReach.RadiusM:F0} m, 2 workers, leaves prepared on demand): "
            + $"{ready} patches ({empty} with no ground loaded) in {clock.Elapsed.TotalSeconds:F1} s.");

        string outDir = ProjectSettings.GlobalizePath("res://test_output/cubeharness");
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, Path.GetFileName(_dir.TrimEnd('/', '\\')) + "-patches.md");
        File.WriteAllText(file, _report.ToString());
        GD.Print(_report.ToString());
        GD.Print(fails == 0 ? $"[cubeharness] RESULT: ok ({file})" : $"[cubeharness] RESULT: FAILED ({fails} leaves over the 2 cm bar, {file})");
        GetTree().Quit(fails == 0 ? 0 : 1);
    }

    private static double PctOf(List<double> sorted, double q) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
}
