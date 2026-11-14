using System.Diagnostics;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World;

/// <summary>
/// The globe's base the game ships (#1656, <c>assets/globe</c>): a coarse Earth and a coarse Switzerland, the offline
/// fallback under every server's and local pyramid, about <c>horizon.bin</c>'s size. A Godot tool mode, because Godot's
/// <see cref="Image"/> decodes and resizes the colour (the tools have no image library).
/// <code>
/// &lt;godot&gt; --headless --path . -- --globebase OUT_DIR --globe IN_DIR [--bits 6] [--albedo 64] [--months 1,4,7,10]
///     [--levels 0-2] [--country minLat,minLon,maxLat,maxLon,maxLevel] [--albedo-levels 0-4]
/// </code>
/// <list type="bullet">
/// <item>Heights at <c>--bits</c> (65² by default), taken exactly at the full lattice's points (every 8th of 513²): a
/// vertex of the base is a vertex of the streamed tile, to its quantum (a thousandth of the base's cell, at most
/// <see cref="MaxQuantumM"/>), so the finer copy replaces it with no step worth seeing.</item>
/// <item>Every tile of <c>--levels</c>, and the tiles of the <c>--country</c> box down to its level (with their siblings,
/// so a split always finds four children), from the input pyramid (<c>--cube-swiss --cube-country</c> builds those).</item>
/// <item>Colour on <c>--albedo-levels</c> only, <c>--albedo</c>² a month for the <c>--months</c> given (the seasons;
/// <see cref="CubeAlbedo.ForMonth"/> picks the one that holds a month): a deeper tile takes its ancestor's.</item>
/// </list>
/// </summary>
public partial class GlobeBaseBake : Node
{
    public static bool Requested() => CmdArgs.Value("--globebase") != null;

    /// <summary>The coarsest height step the base stores, metres: 4 m at most off from orbit, where a cell is 20 km.</summary>
    public const double MaxQuantumM = 8;

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            string outDir = CmdArgs.Value("--globebase")!;
            string inDir = CmdArgs.Value("--globe") ?? throw new ArgumentException("--globebase needs --globe IN_DIR (a full pyramid)");
            int bits = CmdArgs.Int("--bits") ?? 6, albedo = CmdArgs.Int("--albedo") ?? 64;
            int[] months = (CmdArgs.Value("--months") ?? "1,4,7,10").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var (minLevel, maxLevel) = Range(CmdArgs.Value("--levels") ?? "0-2");
            var (minAlbedo, maxAlbedo) = Range(CmdArgs.Value("--albedo-levels") ?? "0-4");
            double[]? country = CmdArgs.Value("--country")?.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var sw = Stopwatch.StartNew();
            var source = new PyramidSource([PyramidSource.Folder(inDir) ?? throw new ArgumentException($"no pyramid in {inDir}")]);
            var manifestIn = CubeManifest.FromJson(File.ReadAllText(System.IO.Path.Combine(inDir, CubeManifest.FileName)));

            var keys = new HashSet<CubeKey>();
            foreach (var key in source.Keys)
                if (key.Level >= minLevel && key.Level <= maxLevel) keys.Add(key);
            if (country is { Length: 5 })
            {
                for (int level = 1; level <= (int)country[4]; level++)
                {
                    double step = 90.0 / (1 << level) / 4;
                    for (double lat = country[0]; lat <= country[2] + step; lat += step)
                        for (double lon = country[1]; lon <= country[3] + step; lon += step)
                            foreach (var sibling in CubeKey.FromLatLon(Math.Min(lat, country[2]), Math.Min(lon, country[3]), level).Parent.Children())
                                if (source.Has(sibling)) keys.Add(sibling);
                }
            }
            // every ancestor too: a tile is only drawn once its parent split
            foreach (var key in keys.ToList())
                for (var k = key; k.Level > 0;) { k = k.Parent; foreach (var s in k.Level > 0 ? k.Parent.Children() : [k]) if (source.Has(s)) keys.Add(s); }

            var heights = new List<(CubeKey Key, byte[] Bytes)>();
            var colours = new List<(CubeKey Key, byte[] Bytes)>();
            var manifest = new CubeManifest
            {
                Orientation = manifestIn.Orientation,
                Lakes = manifestIn.Lakes,
                Sources = [.. manifestIn.Sources, $"the shipped base (#1656): {bits}-bit lattices, colour {albedo}² for months {string.Join(",", months)} on levels {minAlbedo}-{maxAlbedo}"],
            };
            long colourBytes = 0;
            await Task.Run(() =>
            {
                foreach (var key in keys.OrderBy(k => k.Level).ThenBy(k => k.Face).ThenBy(k => k.Y).ThenBy(k => k.X))
                {
                    var full = source.Heights(key)!;
                    int b = Math.Min(bits, full.Bits), step = 1 << (full.Bits - b), n = (1 << b) + 1;
                    var h = new float[n * n];
                    for (int j = 0; j < n; j++)
                        for (int i = 0; i < n; i++)
                            h[j * n + i] = full.HeightAt(i * step, j * step);
                    // the pyramid's rule at the base's own lattice: a thousandth of its cell (8 m at most, levels 0-2)
                    float quantum = (float)Math.Clamp(1.22 * Math.Pow(2, 23 - key.Level - b) / 1000, full.Quantum, MaxQuantumM);
                    var bytes = CubeTileCodec.Encode(new CubeTile { Key = key, Bits = b, Quantum = quantum, Heights = h });
                    heights.Add((key, bytes));
                    var (_, _, min, max) = CubeTileCodec.ReadHeader(bytes);
                    manifest.Tiles.Add(new CubeManifestTile { Key = key.ToString(), Min = min, Max = max, Bytes = bytes.Length });
                }
            });
            // colour on the main thread: Godot's Image
            foreach (var (key, _) in heights)
            {
                if (key.Level < minAlbedo || key.Level > maxAlbedo || !source.HasColour(key)) continue;
                var images = new byte[months.Length][];
                bool ok = true;
                for (int m = 0; m < months.Length && ok; m++)
                {
                    var image = new Image();
                    if (source.Colour(key, months[m]) is not { } webp || image.LoadWebpFromBuffer(webp) != Error.Ok) { ok = false; break; }
                    image.Resize(albedo, albedo, Image.Interpolation.Lanczos);
                    images[m] = image.SaveWebpToBuffer(true, 0.8f);
                }
                if (!ok) continue;
                var entry = CubeAlbedoCodec.Encode(new CubeAlbedo { Key = key, Size = albedo, Months = images });
                colours.Add((key, entry));
                colourBytes += entry.Length;
            }

            Directory.CreateDirectory(outDir);
            foreach (var old in Directory.GetFiles(outDir, "*.cub?")) File.Delete(old);
            var levels = heights.Select(t => t.Key.Level).Distinct().Order();
            foreach (int level in levels)
                manifest.Levels.Add(new CubeManifestLevel
                {
                    Level = level, Bits = Math.Min(bits, CubeKey.LatticeBits(level)), BundleLevel = CubeKey.PyramidBundleLevel(level),
                    Albedo = level >= minAlbedo && level <= maxAlbedo ? albedo : 0, AlbedoBundleLevel = CubeKey.PyramidBundleLevel(level),
                });
            foreach (var (list, magic, name) in new[] { (heights, CubeBundle.Magic, (Func<CubeKey, string>)CubeBundle.FileName), (colours, CubeBundle.AlbedoMagic, CubeBundle.AlbedoFileName) })
                foreach (var bundle in list.GroupBy(e => e.Key.AncestorAt(CubeKey.PyramidBundleLevel(e.Key.Level))))
                    CubeBundle.Write(System.IO.Path.Combine(outDir, name(bundle.Key)), bundle.Key, bundle.OrderBy(e => e.Key.Level).ThenBy(e => e.Key.Y).ThenBy(e => e.Key.X).ToList(), magic);
            manifest.LeafLevel = manifest.Levels[^1].Level;
            manifest.Bits = manifest.Levels[^1].Bits;
            manifest.BundleLevel = manifest.Levels[^1].BundleLevel;
            File.WriteAllText(System.IO.Path.Combine(outDir, CubeManifest.FileName), manifest.ToJson());
            long heightBytes = heights.Sum(t => (long)t.Bytes.Length);
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[globebase] {heights.Count} tiles (levels {string.Join(",", manifest.Levels.Select(l => $"{l.Level}:{heights.Count(t => t.Key.Level == l.Level)}"))}), heights {heightBytes / 1e6:F2} MB, colour {colours.Count} tiles {colourBytes / 1e6:F2} MB, in {outDir}, {sw.Elapsed.TotalSeconds:F0} s"));
            GD.Print("RESULT globebase ok");
            GetTree().Quit();
        }
        catch (Exception e)
        {
            GD.PushError($"[globebase] {e}");
            GD.Print("RESULT globebase failed");
            GetTree().Quit(1);
        }
    }

    private static (int, int) Range(string s)
    {
        var r = s.Split('-');
        return (int.Parse(r[0], CultureInfo.InvariantCulture), int.Parse(r[^1], CultureInfo.InvariantCulture));
    }
}
