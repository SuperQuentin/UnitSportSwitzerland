using Godot;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World;

/// <summary>
/// A cube region's files for the globe (#1430 H4): a folder on disk (the local install) or the levels the game ships,
/// <c>res://assets/globe</c>, which in an export live inside the pack, where .NET's file API cannot reach: <c>res://</c>
/// goes through Godot's <see cref="Godot.FileAccess"/>. The bundles are named from the manifest (each level's bundle
/// levels), so no folder is ever listed. Read as a layer of the pyramid's chain (<see cref="PyramidSource"/>, #1656).
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

    /// <summary>The folder as a layer of the pyramid's chain; <paramref name="local"/>: this copy's own data (served on).</summary>
    public PyramidSource.Layer Layer(string name, bool local) => PyramidSource.FromFiles(name, Manifest(),
        f => _res ? Godot.FileAccess.FileExists(PathOf(f)) : File.Exists(PathOf(f)),
        f => _res ? new MemoryStream(Godot.FileAccess.GetFileAsBytes(PathOf(f))) : File.OpenRead(PathOf(f)),
        (f, offset, length) => ReadBytes((PathOf(f), offset, length)), local);

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

/// <summary>
/// The Earth's pyramid for this process (#1656): one <see cref="PyramidSource"/> the globe, the fill, the server's sight
/// lines and the probes all read. Its layers, finest copy first: the local install (<see cref="Core.TerrainPaths.FindGlobeDir"/>),
/// then the server's (<see cref="PyramidSource.UseRemote"/>, once its index arrives), then the base the game ships.
/// Null with <c>--globe off</c>; empty (until a server's comes) where this copy has nothing.
/// </summary>
public static class GlobeData
{
    private static PyramidSource? _earth;
    private static bool _opened;

    public static PyramidSource? Earth
    {
        get
        {
            if (_opened) return _earth;
            _opened = true;
            if (Core.TerrainPaths.GlobeOff) return null;
            var local = new List<PyramidSource.Layer>();
            if (Core.TerrainPaths.FindGlobeDir() is { } dir) local.Add(new GlobeFiles(dir).Layer(dir, local: true));
            var shipped = new List<PyramidSource.Layer>();
            if (GlobeFiles.HasManifest(GlobeFiles.Shipped)) shipped.Add(new GlobeFiles(GlobeFiles.Shipped).Layer("the shipped base", local: false));
            _earth = new PyramidSource(local, shipped);
            GD.Print($"[globe] the pyramid: {(_earth.Count > 0 ? _earth.Describe() : "nothing")}");
            return _earth;
        }
    }

    /// <summary>Whether this copy has the pyramid on its own disk (the fill may stand on it before any server's comes).</summary>
    public static bool HasLocal => Earth is { } e && e.Keys.Any(e.IsLocal);
}
