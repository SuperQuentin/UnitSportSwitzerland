using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A pyramid's coarsest levels on their own (#1430 H4): the levels the game ships
/// (<c>assets/globe</c>, levels 0-2), taken from a full <c>--cube-global</c> region, heights and colour,
/// rebundled with only those levels in (the full region bundles levels 0-3 together under each face)
/// and a manifest of them alone. The tiles' bytes are copied unchanged.
///
/// <code>
/// --cube-subset --cube-in GLOBAL_DIR --cube-out DIR [--cube-levels 0-2]
/// </code>
/// </summary>
public static class CubeSubset
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-subset");

    public static int Run(IReadOnlyList<string> args)
    {
        string? inDir = null, outDir = null;
        int minLevel = 0, maxLevel = 2;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-subset": break;
                case "--cube-in": inDir = args[++i]; break;
                case "--cube-out": outDir = args[++i]; break;
                case "--cube-levels":
                    var r = args[++i].Split('-');
                    minLevel = int.Parse(r[0], CultureInfo.InvariantCulture);
                    maxLevel = int.Parse(r[^1], CultureInfo.InvariantCulture);
                    break;
                default: Console.Error.WriteLine($"--cube-subset: unknown option {args[i]}"); return 2;
            }
        }
        if (inDir == null || outDir == null) { Console.Error.WriteLine("--cube-subset needs --cube-in DIR and --cube-out DIR"); return 2; }
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.GetFiles(outDir, "*.cub?")) File.Delete(old);

        var manifest = CubeManifest.FromJson(File.ReadAllText(Path.Combine(inDir, CubeManifest.FileName)));
        bool Keep(CubeKey k) => k.Level >= minLevel && k.Level <= maxLevel;
        long bytes = 0;
        foreach (var (pattern, magic, name) in new[] { ("*.cubb", CubeBundle.Magic, (Func<CubeKey, string>)CubeBundle.FileName), ("*.cuba", CubeBundle.AlbedoMagic, CubeBundle.AlbedoFileName) })
        {
            var entries = new List<(CubeKey Key, byte[] Bytes)>();
            foreach (var file in Directory.GetFiles(inDir, pattern))
            {
                using var fs = File.OpenRead(file);
                foreach (var (key, at) in CubeBundle.ReadIndex(fs, magic))
                {
                    if (!Keep(key)) continue;
                    fs.Position = at.Offset;
                    var b = new byte[at.Length];
                    fs.ReadExactly(b);
                    entries.Add((key, b));
                }
            }
            foreach (var bundle in entries.GroupBy(e => e.Key.AncestorAt(CubeKey.PyramidBundleLevel(e.Key.Level))))
            {
                var list = bundle.OrderBy(e => e.Key.Level).ThenBy(e => e.Key.Y).ThenBy(e => e.Key.X).ToList();
                CubeBundle.Write(Path.Combine(outDir, name(bundle.Key)), bundle.Key, list, magic);
                bytes += list.Sum(e => (long)e.Bytes.Length);
            }
            Console.WriteLine($"cube-subset: {entries.Count} {(magic == CubeBundle.Magic ? "height" : "colour")} tiles");
        }
        manifest.Tiles.RemoveAll(t => !Keep(CubeKey.Parse(t.Key)));
        manifest.Levels.RemoveAll(l => l.Level < minLevel || l.Level > maxLevel);
        manifest.LeafLevel = maxLevel;
        manifest.Bits = CubeKey.LatticeBits(maxLevel);
        manifest.BundleLevel = CubeKey.PyramidBundleLevel(maxLevel);
        manifest.Sources.RemoveAll(s => s.StartsWith("levels ", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(outDir, CubeManifest.FileName), manifest.ToJson());
        Console.WriteLine($"cube-subset: levels {minLevel}-{maxLevel}, {manifest.Tiles.Count} tiles, {bytes / 1e6:F1} MB, in {outDir}");
        return 0;
    }
}
