using System.Globalization;
using Godot;
using UnitSport.Core;

namespace UnitSport.Avatar;

/// <summary>
/// <c>--zfightcheck</c> (#788): every model of the model viewer (<see cref="ModelViewer.Discover"/>)
/// built, scanned with <see cref="ZFight"/>, and compared with the committed baseline
/// <c>tests/zfight-baseline.txt</c>. A model that fights over more area than its baseline (or a new
/// model that fights at all) fails; one that got better asks for the baseline to be lowered.
/// <c>--zfightcheck update</c> rewrites the baseline; <c>--zfightcheck &lt;text&gt;</c> narrows the run
/// to the models whose category or name holds it; <c>--zfighthidden</c> keeps faces nothing can see. See docs/notes/general/zfight.md.
/// </summary>
public static class ZFightCheck
{
    private const string Tag = "zfight";
    private const string BaselinePath = "res://tests/zfight-baseline.txt";

    /// <summary>Grows under this many cm² (or 10 %) are noise: a builder's random seed, float order.</summary>
    private const float SlackCm2 = 0.5f;

    public static bool Requested => CmdArgs.Has("--zfightcheck");

    /// <summary>A model drawn with more triangles than this is not scanned: the largest interiors (164k) take a minute and a half.</summary>
    private const int MaxTriangles = 200_000;

    /// <summary>A model wider than this, m, is not scanned: the scan's grid is sized by the mean triangle (the globe is the Earth).</summary>
    private const float MaxExtentM = 5_000f;

    private static float Extent(IReadOnlyList<ZFight.Tri> tris)
    {
        if (tris.Count == 0) return 0f;
        Vector3 lo = tris[0].A, hi = tris[0].A;
        foreach (var t in tris)
        {
            lo = lo.Min(t.A).Min(t.B).Min(t.C);
            hi = hi.Max(t.A).Max(t.B).Max(t.C);
        }
        return (hi - lo).Length();
    }

    public static int Run(Node host)
    {
        var ci = CultureInfo.InvariantCulture;
        // --zfightcheck update | <text>: rewrite the baseline, or only the models whose name holds the text
        // (not --modelsonly: any --models* flag starts the viewer)
        string? arg = CmdArgs.Value("--zfightcheck", notFlag: true);
        bool update = arg == "update";
        string? only = update ? null : arg;
        // --zfightshard K/N (#1702): only every Nth model of the catalog from the Kth, so tools/test.sh runs the
        // N parts side by side (one run took 629 s and held a quick tier up alone)
        int shard = 0, shards = 1;
        if (CmdArgs.Value("--zfightshard") is { } part)
        {
            var kn = part.Split('/');
            if (kn.Length != 2 || !int.TryParse(kn[0], out shard) || !int.TryParse(kn[1], out shards) || shards < 1 || shard < 1 || shard > shards)
            {
                GD.Print($"[{Tag}] RESULT: FAILED (--zfightshard {part}: K/N with 1 <= K <= N)");
                return 1;
            }
            if (update)
            {
                GD.Print($"[{Tag}] RESULT: FAILED (--zfightcheck update rewrites the whole baseline: run it without --zfightshard)");
                return 1;
            }
            shard--;
        }
        int index = -1;
        var baseline = Load();
        var now = new SortedDictionary<string, float>(StringComparer.Ordinal);
        int models = 0, threw = 0, worse = 0, better = 0, fresh = 0, skipped = 0;
        long tris = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // --zfighttrace: each model's name before it is built, to find the one a run hangs on
        bool trace = CmdArgs.Has("--zfighttrace");

        string category = "";
        double categoryFrom = 0;
        int categoryModels = 0;
        void CategoryDone()
        {
            if (category.Length > 0 && clock.Elapsed.TotalSeconds - categoryFrom > 5.0)
                GD.Print(string.Create(ci, $"[{Tag}]   category {category}: {categoryModels} models in {clock.Elapsed.TotalSeconds - categoryFrom:0.0} s"));
        }
        foreach (var entry in ModelViewer.Discover())
        {
            string key = $"{entry.Category} / {entry.Name}";
            if (entry.Category != category)
            {
                CategoryDone();
                category = entry.Category;
                categoryFrom = clock.Elapsed.TotalSeconds;
                categoryModels = 0;
            }
            categoryModels++;
            if (++index % shards != shard) continue;
            if (only != null && !key.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            double started = clock.Elapsed.TotalSeconds;
            if (trace) GD.Print($"[{Tag}]   building {key}");
            Node3D model;
            try
            {
                model = entry.Make();
            }
            catch (Exception)
            {
                threw++;   // --models reports these; nothing to scan
                continue;
            }
            host.AddChild(model);   // some models build their meshes in _Ready
            var scene = ZFightScan.Gather(model, model.GlobalTransform.AffineInverse());
            float extent = Extent(scene.Tris);
            if (scene.Tris.Count > MaxTriangles || extent > MaxExtentM)
            {
                // the whole-Earth globe (#1430 H4): the scan's grid never ended and the check timed out (G2 step 0, #1501)
                GD.Print(string.Create(ci, $"[{Tag}]   skipped: {key}, {scene.Tris.Count} triangles over {extent:0} m (scanned: up to {MaxTriangles} over {MaxExtentM:0} m)"));
                host.RemoveChild(model);
                model.Free();
                skipped++;
                continue;
            }
            var hits = ZFight.Find(scene.Tris, visibleOnly: !CmdArgs.Has("--zfighthidden"));
            host.RemoveChild(model);
            model.Free();
            models++;
            tris += scene.Tris.Count;

            float area = MathF.Round(ZFight.AreaCm2(hits), 1);
            if (area > 0) now[key] = area;
            bool known = baseline.TryGetValue(key, out float was);
            string verdict = "";
            if (area > was * 1.1f + SlackCm2)
            {
                verdict = known ? string.Create(ci, $"  WORSE (baseline {was:0.#} cm²)") : "  NEW";
                if (known) worse++;
                else fresh++;
            }
            else if (area < was * 0.9f - SlackCm2)
            {
                verdict = string.Create(ci, $"  better (baseline {was:0.#} cm²)");
                better++;
            }
            if (hits.Count > 0 || verdict.Length > 0)
                GD.Print($"[{Tag}] {key}: {ZFightScan.Describe(scene, hits, CmdArgs.Int("--zfightplaces") ?? 3)}{verdict}");
            double took = clock.Elapsed.TotalSeconds - started;
            if (took > 2.0) GD.Print(string.Create(ci, $"[{Tag}]   slow: {key} took {took:0.0} s ({scene.Tris.Count} triangles)"));
        }

        CategoryDone();
        GD.Print(string.Create(ci,
            $"[{Tag}] {models} models, {tris} triangles in {clock.Elapsed.TotalSeconds:0.0} s; {now.Count} fight, {now.Values.Sum():0} cm² in all; {threw} threw, {skipped} too big to scan"));
        foreach (var (key, area) in now.OrderByDescending(p => p.Value).Take(15))
            GD.Print(string.Create(ci, $"[{Tag}]   {area,8:0.#} cm²  {key}"));

        if (update)
        {
            Save(now);
            GD.Print($"[{Tag}] RESULT: ok (baseline rewritten: {now.Count} models)");
            return 0;
        }
        if (worse + fresh > 0)
        {
            GD.Print($"[{Tag}] RESULT: FAILED: {worse} models fight more than their baseline, {fresh} new models fight (lift the faces ~1 cm, docs/notes/general/zfight.md)");
            return 1;
        }
        GD.Print(better > 0
            ? $"[{Tag}] RESULT: ok ({better} models better than the baseline: run --zfightcheck update and commit it)"
            : $"[{Tag}] RESULT: ok");
        return 0;
    }

    /// <summary>Lines of "&lt;cm²&gt;&lt;tab&gt;&lt;category&gt; / &lt;name&gt;"; # comments.</summary>
    private static Dictionary<string, float> Load()
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        string path = ProjectSettings.GlobalizePath(BaselinePath);
        if (!File.Exists(path)) return result;
        foreach (string line in File.ReadLines(path))
        {
            int tab = line.IndexOf('\t');
            if (line.StartsWith('#') || tab < 0) continue;
            if (float.TryParse(line[..tab], NumberStyles.Float, CultureInfo.InvariantCulture, out float area))
                result[line[(tab + 1)..]] = area;
        }
        return result;
    }

    private static void Save(SortedDictionary<string, float> areas)
    {
        var lines = new List<string>
        {
            "# --zfightcheck baseline (#788): cm² of fighting faces per model of the model viewer.",
            "# A model over its line (or a new one with any) fails the check. Lower it with --zfightcheck update.",
        };
        lines.AddRange(areas.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Value:0.#}\t{p.Key}")));
        File.WriteAllText(ProjectSettings.GlobalizePath(BaselinePath), string.Join('\n', lines) + '\n');   // LF on every OS
    }
}
