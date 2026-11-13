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

    /// <summary>
    /// The models that take a second or more, with their seconds (#1717): <c>--zfightshard</c> deals
    /// them out first, each to the least loaded shard, so no shard draws two of them by chance.
    /// Rewritten by <c>--zfightcheck update</c>; a model missing from it counts <see cref="DefaultSeconds"/>.
    /// </summary>
    private const string SecondsPath = "res://tests/zfight-seconds.txt";

    /// <summary>What a model not in <see cref="SecondsPath"/> is assumed to take: the mean of the light ones.</summary>
    private const double DefaultSeconds = 0.1;

    /// <summary>A model at least this slow, s, goes in <see cref="SecondsPath"/>.</summary>
    private const double ListedSeconds = 1.0;

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
        // --zfightshard K/N (#1702): the Kth of N parts of the catalog, so tools/test.sh runs the N parts side by
        // side (one run took 629 s and held a quick tier up alone); dealt by measured seconds (#1717, Shards)
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
        var entries = ModelViewer.Discover().ToList();
        var mine = shards > 1 ? Shards(entries.Select(e => $"{e.Category} / {e.Name}").ToList(), LoadSeconds(), shards, shard) : null;
        var seconds = new SortedDictionary<string, double>(StringComparer.Ordinal);
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
        foreach (var entry in entries)
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
            if (mine != null && !mine[++index]) continue;
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
            if (took >= ListedSeconds) seconds[key] = Math.Round(took, 1);
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
            if (only == null) SaveSeconds(seconds);
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

    /// <summary>
    /// Which models of the catalog (<paramref name="keys"/>, in its order) shard <paramref name="shard"/>
    /// (0-based) of <paramref name="shards"/> scans: the slowest first, each to the shard with the fewest
    /// seconds so far (the lowest on a tie), so every shard computes the same deal and the parts end
    /// together. The light models, all <see cref="DefaultSeconds"/>, then go round in catalog order.
    /// </summary>
    internal static bool[] Shards(IReadOnlyList<string> keys, IReadOnlyDictionary<string, double> seconds, int shards, int shard)
    {
        var cost = keys.Select(k => seconds.TryGetValue(k, out double s) ? s : DefaultSeconds).ToArray();
        var order = Enumerable.Range(0, keys.Count).OrderByDescending(i => cost[i]).ThenBy(i => i);
        var load = new double[shards];
        var mine = new bool[keys.Count];
        foreach (int i in order)
        {
            int to = 0;
            for (int k = 1; k < shards; k++)
                if (load[k] < load[to]) to = k;
            load[to] += cost[i];
            mine[i] = to == shard;
        }
        return mine;
    }

    /// <summary>Lines of "&lt;seconds&gt;&lt;tab&gt;&lt;category&gt; / &lt;name&gt;"; # comments.</summary>
    private static Dictionary<string, double> LoadSeconds()
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        string path = ProjectSettings.GlobalizePath(SecondsPath);
        if (!File.Exists(path)) return result;
        foreach (string line in File.ReadLines(path))
        {
            int tab = line.IndexOf('\t');
            if (line.StartsWith('#') || tab < 0) continue;
            if (double.TryParse(line[..tab], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                result[line[(tab + 1)..]] = s;
        }
        return result;
    }

    private static void SaveSeconds(SortedDictionary<string, double> seconds)
    {
        var lines = new List<string>
        {
            "# --zfightcheck seconds (#1717): the models that took a second or more, so --zfightshard deals them out evenly.",
            "# Rewritten by --zfightcheck update; a model not listed counts 0.1 s. Hand edits are fine.",
        };
        lines.AddRange(seconds.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Value:0.0}\t{p.Key}")));
        File.WriteAllText(ProjectSettings.GlobalizePath(SecondsPath), string.Join('\n', lines) + '\n');   // LF on every OS
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
