using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

/// <summary>A point of interest (#1224): immutable, built once when the catalogue loads.</summary>
public sealed class Poi
{
    /// <summary>The canonical id, <c>&lt;tag&gt;:&lt;slug&gt;.&lt;hash&gt;</c> (<see cref="PoiId"/>): what saved progress holds.</summary>
    public string Id { get; }
    public ulong Hash { get; }
    public PoiKind Kind { get; }
    public PoiFlags Flags { get; }
    /// <summary>1 Landmark, 2 Notable (both count toward a canton), 3 Minor.</summary>
    public byte Tier { get; }
    public string Name { get; }
    /// <summary>Two letters ("UR"), "" outside every canton.</summary>
    public string Canton { get; }
    public double E { get; }
    public double N { get; }
    public float Elevation { get; }
    public float DiscoverRadius { get; }
    public float RevealRadius { get; }
    public long LineKey { get; }
    /// <summary>A lake's outline, E, N pairs (LV95 m); null for every other kind.</summary>
    public double[]? Outline { get; }
    /// <summary>The outline's bounding box (the point itself without one).</summary>
    public double MinE { get; }
    public double MinN { get; }
    public double MaxE { get; }
    public double MaxN { get; }

    /// <summary>"peak", "pass", ... (<c>PoiDiscovered.Kind</c>).</summary>
    public string KindTag => PoiId.Tag(Kind);

    public bool Is(PoiFlags flag) => (Flags & flag) == flag;

    public Poi(PoiRecord r)
    {
        Id = r.Id;
        Hash = r.Hash;
        Kind = r.Kind;
        Flags = r.Flags;
        Tier = r.Tier;
        Name = r.Name;
        Canton = r.Canton;
        E = r.E;
        N = r.N;
        Elevation = r.Elevation;
        DiscoverRadius = r.DiscoverRadius;
        RevealRadius = r.RevealRadius;
        LineKey = r.LineKey;
        Outline = r.Outline is { Length: >= 6 } o ? o : null;
        (MinE, MinN, MaxE, MaxN) = (E, N, E, N);
        if (Outline is { } ring)
            for (int i = 0; i < ring.Length; i += 2)
            {
                MinE = Math.Min(MinE, ring[i]); MaxE = Math.Max(MaxE, ring[i]);
                MinN = Math.Min(MinN, ring[i + 1]); MaxN = Math.Max(MaxN, ring[i + 1]);
            }
    }

    public override string ToString() => Id;
}

/// <summary>A canton's totals in this server's map: what its completion is counted against.</summary>
public readonly record struct CantonTotals(int Tier1, int Tier2, int LandCells)
{
    public int Landmarks => Tier1 + Tier2;
}

/// <summary>
/// The region's points of interest (#1224, <c>docs/plans/adventure-places.md</c> 2), from <c>pois.bin</c>
/// (<see cref="PoiFile"/>): the same on the server and every client. Plain C#, no Godot
/// (tier 0: <c>PoiCatalogueTests</c>). Bucketed per 1 km tile; lakes, which span tiles, are also kept
/// apart and found by their bounding box. Read-only once built, so any thread may query it.
/// </summary>
public sealed class PoiCatalogue
{
    private static readonly Poi[] None = Array.Empty<Poi>();

    private readonly Poi[] _all;
    private readonly Dictionary<TileId, Poi[]> _tiles = new();
    private readonly Poi[] _lakes;
    private readonly Dictionary<(PoiKind, ulong), Poi> _byHash = new();
    private readonly Dictionary<string, Poi> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<Poi> _curated = new();
    private readonly Dictionary<string, List<Poi>> _byCanton = new(StringComparer.Ordinal);
    private readonly Dictionary<PoiKind, List<Poi>> _byKind = new();
    private readonly Dictionary<string, CantonTotals> _totals = new(StringComparer.Ordinal);

    /// <summary>No places: the generated world, an older server, a region built without <c>--pois</c>.</summary>
    public static PoiCatalogue Empty { get; } = new(new PoiData());

    public IReadOnlyList<Poi> All => _all;
    public int Count => _all.Length;
    public IEnumerable<string> Cantons => _totals.Keys;

    public PoiCatalogue(PoiData data)
    {
        _all = data.Pois.Select(r => new Poi(r)).ToArray();
        foreach (var p in _all)
        {
            _byHash.TryAdd((p.Kind, p.Hash), p);
            if (!_byKind.TryGetValue(p.Kind, out var k)) _byKind[p.Kind] = k = new List<Poi>();
            k.Add(p);
            if (p.Canton.Length > 0)
            {
                if (!_byCanton.TryGetValue(p.Canton, out var c)) _byCanton[p.Canton] = c = new List<Poi>();
                c.Add(p);
            }
        }
        foreach (var g in _all.GroupBy(p => TileId.FromLv95(p.E, p.N)))
            _tiles[g.Key] = g.ToArray();
        _lakes = _all.Where(p => p.Outline != null).ToArray();
        foreach (var c in data.Cantons) _totals[c.Code] = new CantonTotals(c.Tier1, c.Tier2, c.LandCells);
    }

    /// <summary>Decodes a <c>pois.bin</c>.</summary>
    public static PoiCatalogue Load(ReadOnlySpan<byte> file) => new(PoiFile.Decode(file));

    /// <summary>
    /// Adds curated aliases (<c>peak:rigi-kulm</c> -> its canonical id), from the Atlas cards. Returns the
    /// aliases whose id is not in this catalogue (a smaller map, or a TLM release that dropped the uuid).
    /// Before the catalogue is shared: not thread-safe.
    /// </summary>
    public List<string> AddAliases(IEnumerable<(string Alias, string Id)> aliases)
    {
        var missing = new List<string>();
        foreach (var (alias, id) in aliases)
        {
            if (Resolve(id) is { } poi)
            {
                _aliases[alias] = poi;
                _curated.Add(poi);
            }
            else missing.Add(alias);
        }
        return missing;
    }

    /// <summary>A curated Atlas card names this POI (a landmark, with a stamp post, whatever tier the file gave it).</summary>
    public bool IsCurated(Poi poi) => _curated.Contains(poi);

    /// <summary>The curated alias of a POI, or null.</summary>
    public string? AliasOf(Poi poi)
    {
        foreach (var (alias, p) in _aliases) if (p == poi) return alias;
        return null;
    }

    /// <summary>A landmark: tier 1, or named by a curated card.</summary>
    public bool IsLandmark(Poi poi) => poi.Tier == 1 || _curated.Contains(poi);

    /// <summary>
    /// The POI an id or a curated alias names: a canonical id matches on its tag and hash only (the slug is
    /// cosmetic, so a renamed place keeps its stamps); null when this map has none.
    /// </summary>
    public Poi? Resolve(string idOrAlias)
    {
        if (string.IsNullOrEmpty(idOrAlias)) return null;
        if (PoiId.TryParse(idOrAlias, out var kind, out ulong hash))
            return _byHash.GetValueOrDefault((kind, hash));
        return _aliases.GetValueOrDefault(idOrAlias);
    }

    /// <summary>
    /// The POIs within <paramref name="radiusM"/> (horizontal) of a point into <paramref name="into"/>
    /// (cleared first), of one kind or all. A lake counts when the point is within the radius of its
    /// bounding box. Allocates nothing once <paramref name="into"/> has grown.
    /// </summary>
    public void Near(double e, double n, float radiusM, List<Poi> into, PoiKind? kind = null)
    {
        into.Clear();
        double r2 = (double)radiusM * radiusM;
        int e0 = (int)Math.Floor((e - radiusM) / 1000), e1 = (int)Math.Floor((e + radiusM) / 1000);
        int n0 = (int)Math.Floor((n - radiusM) / 1000), n1 = (int)Math.Floor((n + radiusM) / 1000);
        for (int te = e0; te <= e1; te++)
            for (int tn = n0; tn <= n1; tn++)
            {
                if (!_tiles.TryGetValue(new TileId(te, tn), out var bucket)) continue;
                foreach (var p in bucket)
                {
                    if (p.Outline != null || kind is { } k && p.Kind != k) continue;
                    double de = p.E - e, dn = p.N - n;
                    if (de * de + dn * dn <= r2) into.Add(p);
                }
            }
        if (kind is not (null or PoiKind.Lake)) return;
        foreach (var lake in _lakes)
        {
            double de = Math.Max(0, Math.Max(lake.MinE - e, e - lake.MaxE));
            double dn = Math.Max(0, Math.Max(lake.MinN - n, n - lake.MaxN));
            if (de * de + dn * dn <= r2) into.Add(lake);
        }
    }

    /// <summary>The POIs whose point lies in a tile (a lake by its point, not its outline).</summary>
    public ReadOnlySpan<Poi> InTile(TileId t) => _tiles.TryGetValue(t, out var bucket) ? bucket : None;

    public IReadOnlyList<Poi> InCanton(string code) => _byCanton.TryGetValue(code, out var l) ? l : None;

    public IReadOnlyList<Poi> OfKind(PoiKind k) => _byKind.TryGetValue(k, out var l) ? l : None;

    /// <summary>A canton's tier 1/2 POIs and land fog cells in this map (from the file); zero when absent.</summary>
    public CantonTotals Totals(string code) => _totals.GetValueOrDefault(code);

    /// <summary>Whether a point is on a lake: inside its outline, or within <paramref name="bufferM"/> of its shore.</summary>
    public static bool InLake(Poi lake, double e, double n, float bufferM)
    {
        if (lake.Outline is not { } o) return false;
        if (e < lake.MinE - bufferM || e > lake.MaxE + bufferM || n < lake.MinN - bufferM || n > lake.MaxN + bufferM) return false;
        bool inside = false;
        double best = double.MaxValue;
        int count = o.Length / 2;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            double xi = o[i * 2], yi = o[i * 2 + 1], xj = o[j * 2], yj = o[j * 2 + 1];
            if ((yi > n) != (yj > n) && e < (xj - xi) * (n - yi) / (yj - yi) + xi) inside = !inside;
            double dx = xj - xi, dy = yj - yi, len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : Math.Clamp(((e - xi) * dx + (n - yi) * dy) / len2, 0, 1);
            double ex = xi + t * dx - e, ey = yi + t * dy - n;
            best = Math.Min(best, ex * ex + ey * ey);
        }
        return inside || best <= (double)bufferM * bufferM;
    }

    // ---- the fixture set --------------------------------------------------------------------------

    /// <summary>The fixture worlds' start (<c>SpawnPoint.DefaultLv95E/N</c>).</summary>
    public const double FixtureE = 2583250, FixtureN = 1113250;

    /// <summary>The fixture courses' flat ground (<c>FixtureCourse.FlatHeight</c>): the POIs' elevations are above it.</summary>
    public const double FixtureGround = 500;

    /// <summary>
    /// The fixture POIs (<c>--pois fixture</c>): ten places built in code at known offsets from the
    /// fixture world's start (metres, X east, Y north), in canton VS. The peak, the pass, the tower and the
    /// station are on the flat course's one tile (the start is 250 m in from its south-west corner), so a
    /// check walks to them; the elevations are above <see cref="FixtureGround"/>. The lake POI's outline is the
    /// <c>lake</c> course's ellipse (<c>Terrain/Fixture/FixtureLake</c>), so it is on the water there.
    /// </summary>
    public static PoiCatalogue Fixture(double startE = FixtureE, double startN = FixtureN) => new(FixtureData(startE, startN));

    /// <summary>The records of <see cref="Fixture"/>, as a <c>pois.bin</c> would hold them.</summary>
    public static PoiData FixtureData(double startE = FixtureE, double startN = FixtureN)
    {
        var data = new PoiData();
        PoiRecord Add(PoiKind kind, string name, double x, double y, short elevation, PoiFlags flags = PoiFlags.Named, byte tier = 2)
        {
            var r = new PoiRecord
            {
                Hash = PoiId.Hash("fixture:" + name), Kind = kind, Flags = flags | PoiFlags.Named, Tier = tier, Canton = "VS",
                E = startE + x, N = startN + y, Elevation = (short)(FixtureGround + elevation), Name = name,
                DiscoverRadius = kind switch
                {
                    PoiKind.Peak or PoiKind.SummitCross => 40, PoiKind.Pass => 80, PoiKind.Lake => 60,
                    PoiKind.BusStop => 30, PoiKind.ShipLanding => 40, _ => 60,
                },
            };
            if (flags.HasFlag(PoiFlags.Viewpoint)) r.RevealRadius = 3000;
            data.Pois.Add(r);
            return r;
        }
        Add(PoiKind.Peak, "Fixture Peak", 60, 40, 10, PoiFlags.Cross | PoiFlags.Viewpoint, tier: 1);
        Add(PoiKind.Pass, "Fixture Pass", -200, 400, 0, PoiFlags.Viewpoint);
        Add(PoiKind.LookoutTower, "Fixture Tower", 200, -150, 0, PoiFlags.Viewpoint);
        Add(PoiKind.Chapel, "Fixture Chapel", -150, -120, 0, tier: 3);
        Add(PoiKind.RailStation, "Fixture Station", 300, 120, 0, tier: 3);
        Add(PoiKind.BusStop, "Fixture Bus Stop", -60, -40, 0, PoiFlags.Named | PoiFlags.VillageStop, tier: 3);
        Add(PoiKind.CableCarStation, "Fixture Cable Car", 500, -600, 0, PoiFlags.Top | PoiFlags.Viewpoint).LineKey =
            (long)Math.Round(startE + 500) * 10_000_000L + (long)Math.Round(startN - 900);
        Add(PoiKind.Castle, "Fixture Castle", -700, -500, 0);
        Add(PoiKind.ShipLanding, "Fixture (lac)", 150, 260, 0, tier: 3);
        // FixtureLake: an ellipse 1 300 x 1 000 m whose west shore is 40 m east of the start
        var lake = Add(PoiKind.Lake, "Fixture Lake", 1340, 0, 0);
        const int points = 48;
        lake.Outline = new double[points * 2];
        for (int i = 0; i < points; i++)
        {
            double a = 2 * Math.PI * i / points;
            lake.Outline[i * 2] = startE + 1340 + 1300 * Math.Cos(a);
            lake.Outline[i * 2 + 1] = startN + 1000 * Math.Sin(a);
        }
        data.Cantons.Add(new PoiCanton("VS", data.Pois.Count(p => p.Tier == 1), data.Pois.Count(p => p.Tier == 2), 16));
        return data;
    }
}
