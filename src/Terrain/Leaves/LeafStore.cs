using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// Reads a leaf's entry from somewhere other than this copy's disk (#1655): the client's cache, else the server. Called on a
/// worker and may block for a transfer. Null when the leaf did not come (not connected, busy, timed out, or the server no
/// longer has it): the caller asks again later. <paramref name="section"/> null asks for the whole entry, else an entry
/// holding that one section (the globe's heights, the traffic's roads): a few hundred KB instead of a few MB.
/// </summary>
public delegate byte[]? LeafFetch(CubeKey key, LeafIndex.Entry entry, LeafKind? section);

/// <summary>
/// A region's built leaves (#1510, G2 C1): every <c>.cubl</c> bundle's index read once, a leaf's entry read and decoded
/// on first ask (<see cref="CubeLeafContent.Decode"/>), the last <see cref="Kept"/> held, the oldest dropped first. What
/// the earth world's ground, the globe's leaves and the collision patches read. Thread-safe; a leaf asked from two threads
/// at once is decoded once.
/// <para>
/// The source chain (#1655): the leaves on this copy's disk first, then those the server holds (<see cref="UseRemote"/>,
/// its <see cref="LeafIndex"/>), read through a <see cref="LeafFetch"/> (the client's cache, then the stream). A remote leaf
/// that did not come is not remembered as empty: the next ask fetches again.
/// </para>
/// </summary>
public sealed class LeafStore : ILeafSource
{
    /// <summary>Decoded leaves held (a leaf with roads and buildings is a few MB).</summary>
    public const int DefaultKept = 96;

    private readonly Dictionary<CubeKey, (string Path, long Offset, int Length)> _index;
    private readonly Dictionary<string, uint> _stamps;
    private readonly ConcurrentDictionary<CubeKey, Lazy<CubeLeafContent?>> _cache = new();
    private readonly ConcurrentQueue<CubeKey> _order = new();

    /// <summary>The server's leaves that this copy's disk lacks, and how to read them; null offline or before the index came.</summary>
    private volatile Remote? _remote;

    private sealed record Remote(IReadOnlyDictionary<CubeKey, LeafIndex.Entry> Index, LeafFetch Fetch);

    public int Kept { get; }

    /// <summary>The folder the bundles were read from.</summary>
    public string Dir { get; }

    private LeafStore(string dir, Dictionary<CubeKey, (string, long, int)> index, Dictionary<string, uint> stamps, int kept)
    {
        Dir = dir;
        _index = index;
        _stamps = stamps;
        Kept = Math.Max(1, kept);
    }

    /// <summary>No leaves (a fixture course's world).</summary>
    public static LeafStore Empty { get; } = new("", new Dictionary<CubeKey, (string, long, int)>(), new Dictionary<string, uint>(), 1);

    /// <summary>A store with no leaves on disk, to be fed by a server (<see cref="UseRemote"/>).</summary>
    public static LeafStore None(int kept = DefaultKept) => new("", new Dictionary<CubeKey, (string, long, int)>(), new Dictionary<string, uint>(), kept);

    /// <summary>Every <c>.cubl</c> bundle in <paramref name="dir"/>, indexed. Empty if it holds none.</summary>
    public static LeafStore Open(string dir, int kept = DefaultKept)
    {
        var index = new Dictionary<CubeKey, (string, long, int)>();
        var stamps = new Dictionary<string, uint>();
        if (Directory.Exists(dir))
            foreach (var path in Directory.EnumerateFiles(dir, "*.cubl"))
            {
                using var fs = File.OpenRead(path);
                var rows = CubeBundle.ReadIndex(fs, CubeBundle.LeafMagic);
                foreach (var (key, at) in rows) index[key] = (path, at.Offset, at.Length);
                stamps[path] = LeafIndex.StampOf(fs.Length, rows.Select(r => (r.Key, r.Value.Offset, r.Value.Length)));
            }
        return new LeafStore(dir, index, stamps, kept);
    }

    /// <summary>
    /// The server's leaves (#1655), read through <paramref name="fetch"/> where this copy's disk has none. Returns the leaves
    /// whose content may now differ from what was served before (new, gone or rebuilt on the server, and not on disk here):
    /// whoever drew or blended them reads them again. Replaces an earlier index (a second join, a rebuilt server).
    /// </summary>
    public List<CubeKey> UseRemote(LeafIndex index, LeafFetch fetch)
    {
        var rows = new Dictionary<CubeKey, LeafIndex.Entry>();
        foreach (var (k, e) in index.Leaves)
            if (!_index.ContainsKey(k)) rows[k] = e;
        var changed = LeafIndex.Changed(_remote?.Index, rows);
        _remote = new Remote(rows, fetch);
        foreach (var k in changed) _cache.TryRemove(k, out _);
        return changed;
    }

    /// <summary>The leaves the region holds: on this copy's disk, then the server's.</summary>
    public IReadOnlyCollection<CubeKey> Keys => _remote is { } r ? [.. _index.Keys, .. r.Index.Keys] : _index.Keys;

    /// <summary>The leaves on this copy's disk alone (what a server serves).</summary>
    public IReadOnlyCollection<CubeKey> LocalKeys => _index.Keys;

    public int Count => _index.Count + (_remote?.Index.Count ?? 0);

    /// <summary>Leaves the server holds that this copy's disk lacks.</summary>
    public int RemoteCount => _remote?.Index.Count ?? 0;

    public bool Has(CubeKey key) => _index.ContainsKey(key) || _remote?.Index.ContainsKey(key) == true;

    public bool IsReal(CubeKey key) => Has(key);

    /// <summary>Whether the leaf is on this copy's disk (not only on the server).</summary>
    public bool IsLocal(CubeKey key) => _index.ContainsKey(key);

    /// <summary>A leaf's entry size, bytes (0 where the region holds none): the densest leaf has the most.</summary>
    public int Bytes(CubeKey key) => _index.TryGetValue(key, out var at) ? at.Length
        : _remote is { } r && r.Index.TryGetValue(key, out var e) ? e.Length : 0;

    /// <summary>The stamp of the bundle that holds a leaf on disk (<see cref="LeafIndex.StampOf"/>); 0 where none.</summary>
    public uint Stamp(CubeKey key) => _index.TryGetValue(key, out var at) && _stamps.TryGetValue(at.Path, out uint s) ? s : 0;

    /// <summary>A leaf decoded, null where the region holds none or a remote one did not come. Reads on the first ask.</summary>
    public CubeLeafContent? Get(CubeKey key)
    {
        if (!Has(key)) return null;
        bool added = false;
        var lazy = _cache.GetOrAdd(key, k => { added = true; return new Lazy<CubeLeafContent?>(() => Read(k)); });
        if (added)
        {
            _order.Enqueue(key);
            while (_cache.Count > Kept && _order.TryDequeue(out var old)) _cache.TryRemove(old, out _);
        }
        var value = lazy.Value;
        // a remote leaf that did not come is asked again next time, never kept as empty
        if (value == null && !_index.ContainsKey(key)) _cache.TryRemove(new KeyValuePair<CubeKey, Lazy<CubeLeafContent?>>(key, lazy));
        return value;
    }

    /// <summary>A leaf only if it is already decoded: never touches the disk or the network (for a per-frame query).</summary>
    public CubeLeafContent? Cached(CubeKey key) =>
        _cache.TryGetValue(key, out var lazy) && lazy.IsValueCreated ? lazy.Value : null;

    /// <summary>
    /// A leaf's heights alone, decoded from its entry without the other sections and not kept (the blend's summaries
    /// read every real leaf near the fill once); the decoded leaf's own when it is held. Null where the region holds none
    /// (or a remote one did not come). A remote leaf's heights come alone, not its whole entry.
    /// </summary>
    public CubeTile? HeightsOf(CubeKey key)
    {
        if (Cached(key) is { } held) return held.Heights;
        return Entry(key, LeafKind.Heights) is { } bytes && CubeLeafCodec.ReadSection(bytes, LeafKind.Heights) is { } s ? LeafHeights.Decode(s) : null;
    }

    /// <summary>
    /// Some of a leaf's sections alone (the globe's far leaves: heights, water, cover, trees, buildings, #1629), decoded
    /// from its entry without the rest and not kept, so the near leaves' cache is not churned; the decoded leaf itself
    /// when it is held. Null where the region holds none.
    /// </summary>
    public CubeLeafContent? Sections(CubeKey key, params LeafKind[] kinds)
    {
        if (Cached(key) is { } held) return held;
        if (Entry(key) is not { } bytes) return null;
        var c = new CubeLeafContent { Key = key };
        foreach (var kind in kinds)
        {
            if (CubeLeafCodec.ReadSection(bytes, kind) is not { } s) continue;
            switch (kind)
            {
                case LeafKind.Heights: c.Heights = LeafHeights.Decode(s); break;
                case LeafKind.Cover: c.Cover = LeafCover.Decode(s); break;
                case LeafKind.Water: c.Water = LeafWater.Decode(s); break;
                case LeafKind.Buildings: c.Buildings = LeafBuildings.Decode(s); break;
                case LeafKind.Trees: c.Trees = LeafTrees.Decode(s); break;
                default: throw new ArgumentException($"{kind} is not read alone", nameof(kinds));
            }
        }
        return c;
    }

    /// <summary>
    /// A leaf's roads alone (#1636: the traffic's graphs read a few km of leaves), decoded from its entry without the other
    /// sections and not kept; the decoded leaf's own when it is held. Null where the region holds none or the leaf has no roads.
    /// </summary>
    public RoadTile? RoadsOf(CubeKey key)
    {
        if (Cached(key) is { } held) return held.Roads;
        return Entry(key, LeafKind.Roads) is { } bytes && CubeLeafCodec.ReadSection(bytes, LeafKind.Roads) is { } s ? LeafRoads.Decode(s) : null;
    }

    /// <summary>A leaf's entry as the bundle holds it (what a server sends); null where this copy's disk has none.</summary>
    public byte[]? LocalEntry(CubeKey key)
    {
        if (!_index.TryGetValue(key, out var at)) return null;
        using var fs = File.OpenRead(at.Path);
        fs.Position = at.Offset;
        var bytes = new byte[at.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>An entry with every section but <paramref name="kind"/> dropped (what a server sends for one section, #1655).</summary>
    public static byte[] OneSection(byte[] entry, LeafKind kind)
    {
        var others = CubeLeafCodec.ReadTable(entry).Table.Select(t => t.Kind).Where(k => k != kind).ToList();
        return others.Count == 0 ? entry : CubeLeafCodec.Replace(entry, [], others);
    }

    /// <summary>An entry from disk, else from the server (the whole, or one section); null where none came.</summary>
    private byte[]? Entry(CubeKey key, LeafKind? section = null)
    {
        if (_index.ContainsKey(key)) return LocalEntry(key);
        return _remote is { } r && r.Index.TryGetValue(key, out var e) ? r.Fetch(key, e, section) : null;
    }

    private CubeLeafContent? Read(CubeKey key) => Entry(key) is { } bytes ? CubeLeafContent.Decode(bytes) : null;
}
