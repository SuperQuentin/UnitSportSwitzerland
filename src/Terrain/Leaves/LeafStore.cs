using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A region's built leaves on disk (#1510, G2 C1): every <c>.cubl</c> bundle's index read once, a leaf's entry
/// read and decoded on first ask (<see cref="CubeLeafContent.Decode"/>), the last <see cref="Kept"/> held, the
/// oldest dropped first. What the earth ground, the globe's leaves and the collision patches read. Thread-safe;
/// a leaf asked from two threads at once is decoded once.
/// </summary>
public sealed class LeafStore
{
    /// <summary>Decoded leaves held (a leaf with roads and buildings is a few MB).</summary>
    public const int DefaultKept = 96;

    private readonly Dictionary<CubeKey, (string Path, long Offset, int Length)> _index;
    private readonly ConcurrentDictionary<CubeKey, Lazy<CubeLeafContent?>> _cache = new();
    private readonly ConcurrentQueue<CubeKey> _order = new();

    public int Kept { get; }

    /// <summary>The folder the bundles were read from.</summary>
    public string Dir { get; }

    private LeafStore(string dir, Dictionary<CubeKey, (string, long, int)> index, int kept)
    {
        Dir = dir;
        _index = index;
        Kept = Math.Max(1, kept);
    }

    /// <summary>Every <c>.cubl</c> bundle in <paramref name="dir"/>, indexed. Empty if it holds none.</summary>
    public static LeafStore Open(string dir, int kept = DefaultKept)
    {
        var index = new Dictionary<CubeKey, (string, long, int)>();
        if (Directory.Exists(dir))
            foreach (var path in Directory.EnumerateFiles(dir, "*.cubl"))
            {
                using var fs = File.OpenRead(path);
                foreach (var (key, at) in CubeBundle.ReadIndex(fs, CubeBundle.LeafMagic)) index[key] = (path, at.Offset, at.Length);
            }
        return new LeafStore(dir, index, kept);
    }

    /// <summary>The leaves the region holds: the real ones.</summary>
    public IReadOnlyCollection<CubeKey> Keys => _index.Keys;

    public int Count => _index.Count;

    public bool Has(CubeKey key) => _index.ContainsKey(key);

    /// <summary>A leaf decoded, null where the region holds none. Reads the disk on the first ask.</summary>
    public CubeLeafContent? Get(CubeKey key)
    {
        if (!_index.ContainsKey(key)) return null;
        bool added = false;
        var lazy = _cache.GetOrAdd(key, k => { added = true; return new Lazy<CubeLeafContent?>(() => Read(k)); });
        if (added)
        {
            _order.Enqueue(key);
            while (_cache.Count > Kept && _order.TryDequeue(out var old)) _cache.TryRemove(old, out _);
        }
        return lazy.Value;
    }

    /// <summary>A leaf only if it is already decoded: never touches the disk (for a per-frame query).</summary>
    public CubeLeafContent? Cached(CubeKey key) =>
        _cache.TryGetValue(key, out var lazy) && lazy.IsValueCreated ? lazy.Value : null;

    private CubeLeafContent? Read(CubeKey key)
    {
        var at = _index[key];
        using var fs = File.OpenRead(at.Path);
        fs.Position = at.Offset;
        var bytes = new byte[at.Length];
        fs.ReadExactly(bytes);
        return CubeLeafContent.Decode(bytes);
    }
}
