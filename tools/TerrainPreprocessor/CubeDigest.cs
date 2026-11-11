using System.Globalization;
using System.Security.Cryptography;
using UnitSport.Terrain.Format;
using Mark = UnitSport.Tools.RoadGen.Diagnostics.RecordMarks.Mark;
using RecordMarks = UnitSport.Tools.RoadGen.Diagnostics.RecordMarks;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Two cube regions' leaves held against each other section by section (#1711, G2 D1's bar: byte-identical leaves against the
/// cube build before it wherever the inputs are the same): a SHA-256 of every leaf's every section (its kind, codec version, flags
/// and payload), counted per kind as the same, different or in one region only; the region-wide files (<c>places.json</c>,
/// <c>pois.bin</c>, ...) by their bytes; and where the network's <see cref="LeafKind.Roads"/> differ, its records matched as the
/// tiling check matches them (<see cref="RecordMarks"/>, within 5 m, placed within 5 cm), so a difference has a size.
/// <code>
/// --cube-digest A B [--verbose]
/// </code>
/// Exit 0 when every section and file is the same, 1 otherwise.
/// </summary>
public static class CubeDigest
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-digest");

    public static int Run(IReadOnlyList<string> args)
    {
        string? a = null, b = null;
        bool verbose = false;
        for (int i = 0; i < args.Count; i++)
            switch (args[i])
            {
                case "--cube-digest": a = args[++i]; b = args[++i]; break;
                case "--verbose": verbose = true; break;
                default: Console.Error.WriteLine($"--cube-digest: unknown option {args[i]}"); return 2;
            }
        if (a == null || b == null) { Console.Error.WriteLine("--cube-digest A B [--verbose]"); return 2; }
        return Compare(a, b, verbose, Console.WriteLine);
    }

    /// <summary>Every leaf's sections by key and kind: their digests and their bytes (the roads kept for the record match).</summary>
    private static Dictionary<(CubeKey, LeafKind), (string Hash, LeafSection Section)> Sections(string dir)
    {
        var all = new Dictionary<(CubeKey, LeafKind), (string, LeafSection)>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.cubl").Order(StringComparer.Ordinal))
            foreach (var (key, bytes) in CubeBundle.ReadAllEntries(path))
                foreach (var s in CubeLeafCodec.Decode(bytes).Sections)
                {
                    var head = new byte[4];
                    BitConverter.TryWriteBytes(head.AsSpan(0, 2), s.Version);
                    BitConverter.TryWriteBytes(head.AsSpan(2, 2), s.Flags);
                    using var sha = SHA256.Create();
                    sha.TransformBlock(head, 0, 4, null, 0);
                    sha.TransformFinalBlock(s.Payload, 0, s.Payload.Length);
                    all[(key, s.Kind)] = (Convert.ToHexString(sha.Hash!), s);
                }
        return all;
    }

    public static int Compare(string a, string b, bool verbose, Action<string> log)
    {
        var sa = Sections(a);
        var sb = Sections(b);
        log($"cube digest: {a} ({sa.Count} sections) against {b} ({sb.Count} sections)");
        bool same = true;
        var roadsA = new List<(CubeKey, LeafSection)>();
        var roadsB = new List<(CubeKey, LeafSection)>();
        foreach (var kind in sa.Keys.Concat(sb.Keys).Select(k => k.Item2).Distinct().Order())
        {
            int both = 0, equal = 0, onlyA = 0, onlyB = 0;
            var differ = new List<CubeKey>();
            foreach (var ((key, k), (hash, sec)) in sa.Where(x => x.Key.Item2 == kind))
            {
                if (!sb.TryGetValue((key, k), out var other)) { onlyA++; continue; }
                both++;
                if (other.Hash == hash) equal++;
                else
                {
                    differ.Add(key);
                    if (kind == LeafKind.Roads) { roadsA.Add((key, sec)); roadsB.Add((key, other.Section)); }
                }
            }
            onlyB = sb.Keys.Count(x => x.Item2 == kind && !sa.ContainsKey(x));
            bool ok = differ.Count == 0 && onlyA == 0 && onlyB == 0;
            same &= ok;
            log($"  {(ok ? "same" : "DIFF")} {kind}: {both} leaves in both, {equal} identical, {differ.Count} differ, {onlyA} only in A, {onlyB} only in B");
            if (verbose) foreach (var key in differ.Take(20)) log($"      differs: {key.Describe()}");
        }

        // the region-wide files, by their bytes
        var files = Directory.EnumerateFiles(a).Concat(Directory.EnumerateFiles(b))
            .Select(Path.GetFileName).OfType<string>()
            .Where(f => !f.EndsWith(".cubl", StringComparison.Ordinal) && f is not (CubeManifest.FileName or "built_by.txt"))
            .Distinct().Order(StringComparer.Ordinal);
        foreach (var f in files)
        {
            string pa = Path.Combine(a, f), pb = Path.Combine(b, f);
            string what = !File.Exists(pa) ? "only in B" : !File.Exists(pb) ? "only in A"
                : File.ReadAllBytes(pa).AsSpan().SequenceEqual(File.ReadAllBytes(pb)) ? "same" : "DIFF";
            if (what != "same") same = false;
            log($"  {(what == "same" ? "same" : "DIFF")} file {f}{(what is "same" or "DIFF" ? "" : ": " + what)}");
        }

        if (roadsA.Count > 0) RoadRecords(roadsA, roadsB, verbose, log);
        log(same ? "[cubedigest] RESULT: identical" : "[cubedigest] RESULT: differ");
        return same ? 0 : 1;
    }

    /// <summary>The differing leaves' network records matched between the two (positions in LV95 through each leaf's anchor).</summary>
    private static void RoadRecords(List<(CubeKey, LeafSection)> a, List<(CubeKey, LeafSection)> b, bool verbose, Action<string> log)
    {
        Dictionary<string, List<Mark>> Marks(List<(CubeKey, LeafSection)> list)
        {
            var into = new Dictionary<string, List<Mark>>();
            foreach (var (key, sec) in list)
            {
                var anchor = key.Anchor;
                RecordMarks.Collect(LeafRoads.Decode(sec), into, (x, y, z) => SwissGeodesy.FromEcef(anchor.ToEcef(x, y, z)));
            }
            return into;
        }
        var ma = Marks(a);
        var mb = Marks(b);
        log($"  the network's records in the {a.Count} leaves whose roads differ (matched within 5 m):");
        foreach (string kind in ma.Keys.Union(mb.Keys).Order(StringComparer.Ordinal))
        {
            var la = ma.GetValueOrDefault(kind) ?? [];
            var lb = mb.GetValueOrDefault(kind) ?? [];
            var (partner, dist, used) = RecordMarks.Match(la, lb, 5);
            int alone = 0, look = 0, far = 0;
            var ds = new List<double>();
            var detail = new List<string>();
            for (int i = 0; i < la.Count; i++)
            {
                if (partner[i] < 0) { alone++; detail.Add(string.Create(CultureInfo.InvariantCulture, $"A alone at {la[i].E:F1},{la[i].N:F1} ({la[i].Look})")); continue; }
                var o = lb[partner[i]];
                ds.Add(dist[i]);
                if (o.Look != la[i].Look) { look++; detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {la[i].E:F1},{la[i].N:F1}: {la[i].Look} -> {o.Look}")); }
                else if (dist[i] > 0.05) { far++; detail.Add(string.Create(CultureInfo.InvariantCulture, $"at {la[i].E:F1},{la[i].N:F1}: {dist[i]:F2} m apart")); }
            }
            int aloneB = used.Count(u => !u);
            for (int k = 0; k < lb.Count; k++)
                if (!used[k]) detail.Add(string.Create(CultureInfo.InvariantCulture, $"B alone at {lb[k].E:F1},{lb[k].N:F1} ({lb[k].Look})"));
            ds.Sort();
            double P(double q) => ds.Count == 0 ? 0 : ds[Math.Min(ds.Count - 1, (int)(q * (ds.Count - 1) + 0.5))];
            log(string.Create(CultureInfo.InvariantCulture,
                $"    {kind}: {la.Count} / {lb.Count}, alone {alone} / {aloneB}, other look {look}, apart > 5 cm {far}; distance p50 {P(0.5) * 100:F2} cm, p95 {P(0.95) * 100:F2} cm, max {P(1) * 100:F2} cm"));
            if (verbose) foreach (string d in detail.Take(30)) log("        " + d);
        }
    }
}
