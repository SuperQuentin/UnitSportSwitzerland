using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// A cube region's files for the globe (#1430 H4): a folder on disk (the full region on D:) or the
/// levels the game ships, <c>res://assets/globe</c>, which in an export live inside the pack, where .NET's
/// file API cannot reach: <c>res://</c> goes through Godot's <see cref="Godot.FileAccess"/>. The bundles
/// are named from the manifest (each level's bundle levels), so no folder is ever listed.
/// </summary>
public sealed class GlobeFiles
{
    public const string Shipped = "res://assets/globe";

    public string Dir { get; }

    private readonly bool _res;

    public GlobeFiles(string dir)
    {
        Dir = dir.TrimEnd('/', '\\');
        _res = Dir.StartsWith("res://", StringComparison.Ordinal);
    }

    public static bool HasManifest(string dir) => dir.StartsWith("res://", StringComparison.Ordinal)
        ? Godot.FileAccess.FileExists(dir.TrimEnd('/') + "/" + CubeManifest.FileName)
        : File.Exists(System.IO.Path.Combine(dir, CubeManifest.FileName));

    private string PathOf(string name) => _res ? Dir + "/" + name : System.IO.Path.Combine(Dir, name);

    public CubeManifest Manifest() => CubeManifest.FromJson(_res
        ? Godot.FileAccess.GetFileAsString(PathOf(CubeManifest.FileName))
        : File.ReadAllText(PathOf(CubeManifest.FileName)));

    /// <summary>Every tile's place (file, offset, length), heights and colour, from the bundles the manifest's levels name.</summary>
    public (Dictionary<CubeKey, (string Path, long Offset, int Length)> Heights, Dictionary<CubeKey, (string Path, long Offset, int Length)> Albedo) Index(CubeManifest manifest)
    {
        var heights = new Dictionary<CubeKey, (string, long, int)>();
        var albedo = new Dictionary<CubeKey, (string, long, int)>();
        var levels = manifest.LevelsOrLeaf.ToDictionary(l => l.Level);
        var heightBundles = new HashSet<CubeKey>();
        var colourBundles = new HashSet<CubeKey>();
        foreach (var t in manifest.Tiles)
        {
            var k = CubeKey.Parse(t.Key);
            var lv = levels.GetValueOrDefault(k.Level);
            heightBundles.Add(k.AncestorAt(lv?.BundleLevel ?? CubeKey.PyramidBundleLevel(k.Level)));
            if (lv is { Albedo: > 0 }) colourBundles.Add(k.AncestorAt(lv.AlbedoBundleLevel));
        }
        foreach (var b in heightBundles) Read(PathOf(CubeBundle.FileName(b)), CubeBundle.Magic, heights);
        foreach (var b in colourBundles) Read(PathOf(CubeBundle.AlbedoFileName(b)), CubeBundle.AlbedoMagic, albedo);
        return (heights, albedo);
    }

    private void Read(string path, uint magic, Dictionary<CubeKey, (string, long, int)> into)
    {
        if (_res ? !Godot.FileAccess.FileExists(path) : !File.Exists(path)) return;
        using Stream s = _res ? new MemoryStream(Godot.FileAccess.GetFileAsBytes(path)) : File.OpenRead(path);
        foreach (var (key, at) in CubeBundle.ReadIndex(s, magic)) into[key] = (path, at.Offset, at.Length);
    }

    /// <summary>One entry's bytes; any thread.</summary>
    public static byte[] ReadBytes((string Path, long Offset, int Length) at)
    {
        if (at.Path.StartsWith("res://", StringComparison.Ordinal))
        {
            using var f = Godot.FileAccess.Open(at.Path, Godot.FileAccess.ModeFlags.Read);
            f.Seek((ulong)at.Offset);
            return f.GetBuffer(at.Length);
        }
        using var handle = File.OpenHandle(at.Path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        var bytes = new byte[at.Length];
        RandomAccess.Read(handle, bytes, at.Offset);
        return bytes;
    }
}
