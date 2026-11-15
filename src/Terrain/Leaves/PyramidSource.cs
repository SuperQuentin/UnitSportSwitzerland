using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// One tile of a remote pyramid read by its key (#1656): section 0 its heights (a <c>CUBT</c> entry), section 1-12 that
/// month's colour (a <c>CUBC</c> entry holding that one image). Null when it did not come (the caller asks again later).
/// Called on a worker; blocks for the transfer.
/// </summary>
public delegate byte[]? PyramidFetch(CubeKey key, PyramidIndex.Entry entry, int section);

/// <summary>
/// The pyramid a body's globe and fill read (#1656): the coarse cube levels from every place the game has them, in one
/// chain. Each layer is a manifest and the places of its entries: a folder on this copy's disk (the local install), the
/// levels the game ships, the server's (its <see cref="PyramidIndex"/> and a <see cref="PyramidFetch"/>). Per tile the
/// <b>finest copy wins</b> (most lattice bits), ties to the layer listed first, so the shipped 65² base never hides a
/// streamed 513² tile and a local copy never waits on the network. Body-agnostic: a planet's pyramid is another chain
/// (H7). Godot-free and thread-safe: the layers are swapped whole (<see cref="UseRemote"/>), readers take one snapshot.
/// </summary>
public sealed class PyramidSource
{
    /// <summary>A tile's place in a layer: its lattice bits, and how to read its heights and its colour (null: none).</summary>
    public sealed record Tile(int Bits, Func<byte[]?> Heights, Func<int, byte[]?>? Colour, bool Local, PyramidIndex.Entry? Remote = null,
        Func<byte[]?>? Features = null);

    /// <summary>The section a tile's far features are asked by (#1657): after the twelve months' colour.</summary>
    public const int FeaturesSection = 13;

    /// <summary>A layer of the chain: its name (for the log), its lakes, the tiles it holds.</summary>
    public sealed record Layer(string Name, IReadOnlyList<float>? Lakes, IReadOnlyDictionary<CubeKey, Tile> Tiles);

    private sealed record Snapshot(Dictionary<CubeKey, Tile> Best, float[] Lakes, int MaxLevel, int FinestRealLevel);

    private readonly List<Layer> _before = new(), _after = new();
    private Layer? _remote;
    private volatile Snapshot _now = new(new(), [], -1, -1);

    /// <param name="local">Layers ahead of the server's (this copy's disk), first wins a tie.</param>
    /// <param name="fallback">Layers behind it (the shipped base).</param>
    public PyramidSource(IEnumerable<Layer> local, IEnumerable<Layer>? fallback = null)
    {
        _before.AddRange(local);
        if (fallback != null) _after.AddRange(fallback);
        Rebuild();
    }

    /// <summary>The layers' names in order, for the log line.</summary>
    public string Describe() => string.Join(", ", Layers().Select(l => $"{l.Name} ({l.Tiles.Count})"));

    private IEnumerable<Layer> Layers()
    {
        foreach (var l in _before) yield return l;
        if (_remote != null) yield return _remote;
        foreach (var l in _after) yield return l;
    }

    private void Rebuild()
    {
        var best = new Dictionary<CubeKey, Tile>();
        float[]? lakes = null;
        int max = -1, finestReal = -1;
        foreach (var layer in Layers())
        {
            if (lakes == null && layer.Lakes is { Count: > 0 } l) lakes = l.ToArray();
            foreach (var (k, t) in layer.Tiles)
            {
                if (best.TryGetValue(k, out var held) && held.Bits >= t.Bits) continue;
                best[k] = t;
                max = Math.Max(max, k.Level);
            }
        }
        // the finest level held at the full lattice (the shipped base's resampled levels know less than their level says)
        foreach (var (k, t) in best)
            if (t.Bits >= CubeKey.LatticeBits(k.Level)) finestReal = Math.Max(finestReal, k.Level);
        _now = new Snapshot(best, lakes ?? [], max, finestReal);
    }

    /// <summary>
    /// The server's pyramid (#1656): its index and the fetch that reads a tile of it (the client's cache, then the stream).
    /// Returns the tiles whose best copy changed, which a reader drops and reads again. The same index again changes
    /// nothing. Main thread (readers on workers keep their snapshot).
    /// </summary>
    public List<CubeKey> UseRemote(PyramidIndex index, PyramidFetch fetch)
    {
        var before = _now.Best;
        var tiles = new Dictionary<CubeKey, Tile>(index.Tiles.Count);
        foreach (var (k, e) in index.Tiles)
        {
            var key = k;
            var entry = e;
            tiles[key] = new Tile(entry.Bits, () => fetch(key, entry, 0),
                entry.HasColour ? month => fetch(key, entry, Math.Clamp(month, 1, 12)) : null, Local: false, Remote: entry,
                Features: entry.HasFeatures ? () => fetch(key, entry, FeaturesSection) : null);
        }
        _remote = new Layer("the server's", index.Lakes, tiles);
        Rebuild();
        var after = _now.Best;
        var changed = new List<CubeKey>();
        foreach (var (k, t) in after)
            if (!before.TryGetValue(k, out var old) || old.Bits != t.Bits || old.Local != t.Local || old.Remote != t.Remote) changed.Add(k);
        foreach (var k in before.Keys)
            if (!after.ContainsKey(k)) changed.Add(k);
        return changed;
    }

    /// <summary>The server's layer dropped (a world left: the next server, or none, brings its own). Main thread.</summary>
    public void DropRemote()
    {
        if (_remote == null) return;
        _remote = null;
        Rebuild();
    }

    public bool Has(CubeKey key) => _now.Best.ContainsKey(key);

    /// <summary>Whether a tile's best copy is on this copy's disk (the server serves those).</summary>
    public bool IsLocal(CubeKey key) => _now.Best.TryGetValue(key, out var t) && t.Local;

    /// <summary>Whether a tile's best copy comes from the server (reading it may block on the network).</summary>
    public bool IsStreamed(CubeKey key) => _now.Best.TryGetValue(key, out var t) && t.Remote != null;

    public bool HasColour(CubeKey key) => _now.Best.TryGetValue(key, out var t) && t.Colour != null;

    /// <summary>Whether a tile's best copy carries far features (#1657).</summary>
    public bool HasFeatures(CubeKey key) => _now.Best.TryGetValue(key, out var t) && t.Features != null;

    /// <summary>A tile's far features (#1657), decoded; null where none, or streamed and not come. Any thread.</summary>
    public CubeFarFeatures? Features(CubeKey key) =>
        _now.Best.TryGetValue(key, out var t) && t.Features?.Invoke() is { } bytes ? CubeFarFeaturesCodec.Decode(bytes) : null;

    /// <summary>A tile's far features entry as stored (the server sends it on); null where none.</summary>
    public byte[]? FeaturesEntry(CubeKey key) => _now.Best.TryGetValue(key, out var t) ? t.Features?.Invoke() : null;

    /// <summary>A tile's lattice bits in its best copy, 0 where none.</summary>
    public int BitsOf(CubeKey key) => _now.Best.TryGetValue(key, out var t) ? t.Bits : 0;

    public int Count => _now.Best.Count;

    /// <summary>Every tile the chain holds (a snapshot).</summary>
    public IReadOnlyCollection<CubeKey> Keys => _now.Best.Keys;

    /// <summary>The deepest level any layer holds; -1 with none.</summary>
    public int MaxLevel => _now.MaxLevel;

    /// <summary>The deepest level held at its full lattice (<see cref="CubeKey.LatticeBits"/>): what the fill may stand on.</summary>
    public int FinestRealLevel => _now.FinestRealLevel;

    /// <summary>The lake levels of the first layer that names any (#1669).</summary>
    public float[] Lakes => _now.Lakes;

    /// <summary>A tile's heights decoded, from its best copy; null where none, or a streamed one that did not come. Any thread.</summary>
    public CubeTile? Heights(CubeKey key) =>
        _now.Best.TryGetValue(key, out var t) && t.Heights() is { } bytes ? CubeTileCodec.Decode(bytes) : null;

    /// <summary>A tile's heights entry as it is stored (the server sends it on); null where none.</summary>
    public byte[]? HeightsBytes(CubeKey key) => _now.Best.TryGetValue(key, out var t) ? t.Heights() : null;

    /// <summary>A month's colour image (WebP or JPEG bytes as stored); null where none or not come.</summary>
    public byte[]? Colour(CubeKey key, int month)
    {
        if (!_now.Best.TryGetValue(key, out var t) || t.Colour?.Invoke(month) is not { } bytes) return null;
        return CubeAlbedoCodec.Decode(bytes).ForMonth(month);
    }

    /// <summary>A month's colour as a one-image <c>CUBC</c> entry (what the server sends for section <paramref name="month"/>).</summary>
    public byte[]? ColourEntry(CubeKey key, int month)
    {
        if (!_now.Best.TryGetValue(key, out var t) || t.Colour?.Invoke(month) is not { } bytes) return null;
        var all = CubeAlbedoCodec.Decode(bytes);
        if (all.Months.Length == 1) return bytes;
        return CubeAlbedoCodec.Encode(new CubeAlbedo { Key = all.Key, Size = all.Size, Format = all.Format, Months = [all.ForMonth(month)] });
    }

    /// <summary>
    /// The fill's ground (<see cref="PyramidBase"/>, #1678) as this copy stands it: the tiles on its own disk at their full
    /// lattice, nothing streamed and nothing resampled (the shipped base), so a server and an offline copy with the same
    /// pyramid build the same fill. Null where they reach no deeper than <see cref="PyramidBase.MinFinestLevel"/>.
    /// </summary>
    public PyramidBase? Base()
    {
        var tiles = new Dictionary<CubeKey, Tile>();
        float[]? lakes = null;
        foreach (var layer in _before)
        {
            if (lakes == null && layer.Lakes is { Count: > 0 } l) lakes = l.ToArray();
            foreach (var (k, t) in layer.Tiles)
                if (t.Bits >= CubeKey.LatticeBits(k.Level)) tiles.TryAdd(k, t);
        }
        return BaseOf(tiles, lakes, (_, t) => t.Heights());
    }

    /// <summary>
    /// The fill's ground as the server stands it (#1656): the server's tiles alone (its index), each read from this copy's
    /// disk where its stamp is the server's, else streamed. Null with no server pyramid, or one no deeper than
    /// <see cref="PyramidBase.MinFinestLevel"/> (the server's fill is the generator's then, and so is this one).
    /// ponytail: a tile that does not come stays unread for as long as <see cref="PyramidBase"/> keeps it (its
    /// <see cref="PyramidBase.Kept"/>), and the leaves made meanwhile stand on a coarser level there.
    /// </summary>
    public PyramidBase? ServerBase()
    {
        if (_remote is not { } remote) return null;
        var locals = _before.ToList();
        var tiles = remote.Tiles.Where(kv => kv.Value.Bits >= CubeKey.LatticeBits(kv.Key.Level)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return BaseOf(tiles, remote.Lakes?.ToArray(), (key, t) =>
        {
            foreach (var layer in locals)
                if (layer.Tiles.TryGetValue(key, out var own) && own.Bits == t.Bits && own.Heights() is { } bytes
                    && PyramidIndex.StampOf(key, bytes) == t.Remote!.Value.Stamp) return bytes;
            return t.Heights();
        });
    }

    private static PyramidBase? BaseOf(Dictionary<CubeKey, Tile> tiles, float[]? lakes, Func<CubeKey, Tile, byte[]?> read)
    {
        int finest = tiles.Count == 0 ? -1 : tiles.Keys.Max(k => k.Level);
        if (finest < PyramidBase.MinFinestLevel) return null;
        return new PyramidBase(k => tiles.TryGetValue(k, out var t) && read(k, t) is { } b ? CubeTileCodec.Decode(b) : null, lakes, finest)
        {
            SeaSurface = Egm2008.Available ? Egm2008.Undulation : null,
        };
    }

    // ---- layers on disk --------------------------------------------------------------------------------

    /// <summary>
    /// A pyramid's layer from its manifest and a reader of its files (a folder on disk, or the game's pack through Godot):
    /// every tile of the bundles the manifest's levels name, with its level's bits. <paramref name="exists"/> and
    /// <paramref name="open"/> see the files; <paramref name="read"/> reads one entry, any thread.
    /// </summary>
    public static Layer FromFiles(string name, CubeManifest manifest, Func<string, bool> exists, Func<string, Stream> open,
        Func<string, long, int, byte[]> read, bool local)
    {
        var levels = manifest.LevelsOrLeaf.ToDictionary(l => l.Level);
        var heightBundles = new HashSet<CubeKey>();
        var colourBundles = new HashSet<CubeKey>();
        var featureBundles = new HashSet<CubeKey>();
        foreach (var t in manifest.Tiles)
        {
            var k = CubeKey.Parse(t.Key);
            var lv = levels.GetValueOrDefault(k.Level);
            heightBundles.Add(k.AncestorAt(lv?.BundleLevel ?? CubeKey.PyramidBundleLevel(k.Level)));
            if (lv is { Albedo: > 0 }) colourBundles.Add(k.AncestorAt(lv.AlbedoBundleLevel));
            if (lv is { Features: > 0 }) featureBundles.Add(k.AncestorAt(lv.BundleLevel));
        }
        var heights = new Dictionary<CubeKey, (string Path, long Offset, int Length)>();
        var colour = new Dictionary<CubeKey, (string Path, long Offset, int Length)>();
        var features = new Dictionary<CubeKey, (string Path, long Offset, int Length)>();
        void Index(string path, uint magic, Dictionary<CubeKey, (string, long, int)> into)
        {
            if (!exists(path)) return;
            using var s = open(path);
            foreach (var (key, at) in CubeBundle.ReadIndex(s, magic)) into[key] = (path, at.Offset, at.Length);
        }
        foreach (var b in heightBundles) Index(CubeBundle.FileName(b), CubeBundle.Magic, heights);
        foreach (var b in colourBundles) Index(CubeBundle.AlbedoFileName(b), CubeBundle.AlbedoMagic, colour);
        foreach (var b in featureBundles) Index(CubeFarFeaturesCodec.FileName(b), CubeFarFeaturesCodec.BundleMagic, features);
        var tiles = new Dictionary<CubeKey, Tile>(heights.Count);
        foreach (var (k, at) in heights)
        {
            int bits = levels.GetValueOrDefault(k.Level)?.Bits ?? CubeKey.LatticeBits(k.Level);
            var h = at;
            Func<int, byte[]?>? c = colour.TryGetValue(k, out var cat) ? _ => read(cat.Path, cat.Offset, cat.Length) : null;
            Func<byte[]?>? f = features.TryGetValue(k, out var fat) ? () => read(fat.Path, fat.Offset, fat.Length) : null;
            tiles[k] = new Tile(bits, () => read(h.Path, h.Offset, h.Length), c, local, Features: f);
        }
        return new Layer(name, manifest.Lakes, tiles);
    }

    /// <summary>A pyramid folder on this copy's disk (System.IO); null where it has no manifest.</summary>
    public static Layer? Folder(string dir, string? name = null)
    {
        string manifestPath = Path.Combine(dir, CubeManifest.FileName);
        if (!File.Exists(manifestPath)) return null;
        var manifest = CubeManifest.FromJson(File.ReadAllText(manifestPath));
        return FromFiles(name ?? dir, manifest, f => File.Exists(Path.Combine(dir, f)), f => File.OpenRead(Path.Combine(dir, f)),
            (f, offset, length) =>
            {
                using var handle = File.OpenHandle(Path.Combine(dir, f), FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = new byte[length];
                RandomAccess.Read(handle, bytes, offset);
                return bytes;
            }, local: true);
    }
}
