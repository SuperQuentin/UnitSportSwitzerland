using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Entertainment venues (#1232): the bowling centres, cinemas, arcades, restaurants, cafés and bars
/// OpenStreetMap knows, from <c>venues.tsv.gz</c> (written by <c>TerrainPreprocessor --venues</c>,
/// embedded in the game assembly), and which solid each one is in.
///
/// <para>
/// Like <see cref="Castles"/> and <see cref="Landmarks"/>, a solid is a venue only when its real
/// footprint (<see cref="Building.Covers"/>) holds the venue's point, so the answer is unique and a
/// pure function of the tile: server and clients agree without a byte sent. The list is in the
/// assembly, so it is there before the first tile is planned and never arrives late. Rules, numbers
/// and how to regenerate: <c>docs/notes/terrain/entertainment-venues.md</c>.
/// </para>
/// </summary>
public static class Venues
{
    /// <summary>The embedded list's resource name (<c>UnitSportSwitzerland.csproj</c>).</summary>
    public const string ResourceName = "venues.tsv.gz";

    /// <summary>
    /// How far outside a tile a point may lie for that tile to look at it: a solid belongs to the tile
    /// holding its centre, and a mall's footprint can reach well into the next one.
    /// </summary>
    public const float Reach = 300f;

    private static readonly Lazy<Index> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every venue, sorted by E then N. Empty if the resource is missing or unreadable.</summary>
    public static IReadOnlyList<VenueRecord> List => Loaded.Value.Rows;

    /// <summary>
    /// Replaces the embedded list (the checks' hand-made venues); null restores it. Set before the
    /// tiles it should apply to are read: <see cref="BuildingTypes.For"/> caches per tile object.
    /// </summary>
    public static IReadOnlyList<VenueRecord>? Override
    {
        set => _override = value == null ? null : new Index(value.ToList());
    }
    private static Index? _override;

    private static Index Current => _override ?? Loaded.Value;

    public static VenueRecord At(int venue) => Current.Rows[venue];

    /// <summary>
    /// Which venue wins a building that holds several (a mall with a cinema and five restaurants): the
    /// one that needs the building most. The big halls first, then the places with a room of their own.
    /// </summary>
    public static int Rank(VenueType kind) => kind switch
    {
        VenueType.Bowling => 10,
        VenueType.Cinema => 9,
        VenueType.Arcade => 8,
        VenueType.Nightclub => 7,
        VenueType.Pub => 6,
        VenueType.Bar => 5,
        VenueType.Restaurant => 4,
        VenueType.Biergarten => 3,
        VenueType.Cafe => 2,
        VenueType.FastFood => 1,
        _ => 0,
    };

    /// <summary>A venue that is a hall of its own (lanes, screens, rows of machines), not a room on a ground floor.</summary>
    public static bool IsHall(VenueType kind) => kind is VenueType.Bowling or VenueType.Cinema or VenueType.Arcade;

    /// <summary>A place to eat or drink.</summary>
    public static bool IsDining(VenueType kind) => kind is VenueType.Restaurant or VenueType.Cafe or VenueType.FastFood
        or VenueType.Bar or VenueType.Pub or VenueType.Nightclub or VenueType.Biergarten;

    /// <summary>
    /// For each building of the tile, the index of the venue it is (see <see cref="At"/>), or -1.
    /// Null when the tile holds none. A building holding several takes the highest <see cref="Rank"/>
    /// (then the first in the list); the rest are not shown.
    /// </summary>
    public static int[]? Match(BuildingTile tile)
    {
        var index = Current;
        int[]? venue = null;
        foreach (var (v, i) in Contained(tile))
        {
            venue ??= Enumerable.Repeat(-1, tile.Buildings.Count).ToArray();
            // Contained is in list order, so on a tie the first venue in the list keeps the building
            if (venue[i] < 0 || Rank(index.Rows[v].Type) > Rank(index.Rows[venue[i]].Type)) venue[i] = v;
        }
        return venue;
    }

    /// <summary>
    /// Every venue whose point a solid of the tile covers, with that solid's index, in list order:
    /// all of them, before a building's several venues are ranked down to one.
    /// </summary>
    public static List<(int Venue, int Building)> Contained(BuildingTile tile)
    {
        var index = Current;
        var id = tile.Id;
        var found = new List<(int, int)>();
        var near = new List<(int Venue, float X, float Z)>();
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                if (!index.ByTile.TryGetValue((id.E + de, id.N + dn), out var cell)) continue;
                foreach (int v in cell)
                {
                    var r = index.Rows[v];
                    if (r.E < id.MinE - Reach || r.E > id.MinE + ChunkFormat.TileSizeM + Reach
                        || r.N < id.MinN - Reach || r.N > id.MaxN + Reach) continue;
                    near.Add((v, (float)(r.E - id.MinE), (float)(id.MaxN - r.N)));
                }
            }
        if (near.Count == 0) return found;
        near.Sort((a, b) => a.Venue.CompareTo(b.Venue));

        // each solid's plan bounds, so only the few solids around a point run the triangle test
        int count = tile.Buildings.Count;
        var bounds = new (float X0, float Z0, float X1, float Z1)[count];
        for (int i = 0; i < count; i++)
        {
            var t = tile.Buildings[i].Triangles;
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                x0 = MathF.Min(x0, t[k]); x1 = MathF.Max(x1, t[k]);
                z0 = MathF.Min(z0, t[k + 2]); z1 = MathF.Max(z1, t[k + 2]);
            }
            bounds[i] = (x0, z0, x1, z1);
        }

        foreach (var (v, x, z) in near)
            for (int i = 0; i < count; i++)
            {
                var (x0, z0, x1, z1) = bounds[i];
                if (x < x0 || x > x1 || z < z0 || z > z1) continue;
                if (!tile.Buildings[i].Covers(x, z)) continue;
                found.Add((v, i));
                break;
            }
        return found;
    }

    /// <summary>What the sign says when OSM gives no name.</summary>
    public static string GenericName(VenueType kind, string cuisine = "") => kind switch
    {
        VenueType.Bowling => "Bowling",
        VenueType.Cinema => "Cinema",
        VenueType.Arcade => "Arcade",
        VenueType.Cafe => "Café",
        VenueType.FastFood => cuisine is "kebab" ? "Kebab" : cuisine is "burger" ? "Burger" : cuisine is "pizza" ? "Pizza" : "Take-away",
        VenueType.Bar => "Bar",
        VenueType.Pub => "Pub",
        VenueType.Nightclub => "Club",
        VenueType.Biergarten => "Biergarten",
        _ => cuisine is "pizza" or "italian" ? "Pizzeria" : "Restaurant",
    };

    /// <summary>The name a venue shows: OSM's, else <see cref="GenericName"/>.</summary>
    public static string NameOf(VenueRecord r) => r.Name.Length > 0 ? r.Name : GenericName(r.Type, r.Cuisine);

    private sealed class Index
    {
        public readonly List<VenueRecord> Rows;
        /// <summary>Venue indices by the kilometre tile holding their point.</summary>
        public readonly Dictionary<(int E, int N), int[]> ByTile;

        public Index(List<VenueRecord> rows)
        {
            Rows = rows;
            ByTile = Enumerable.Range(0, rows.Count)
                .GroupBy(i => { var t = TileId.FromLv95(rows[i].E, rows[i].N); return (t.E, t.N); })
                .ToDictionary(g => g.Key, g => g.ToArray());
        }
    }

    private static Index Load()
    {
        try
        {
            using var s = typeof(Venues).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null)
            {
                Godot.GD.PushWarning($"[venues] no embedded {ResourceName}: no venue is recognised");
                return new Index(new List<VenueRecord>());
            }
            return new Index(VenueFormat.Read(s));
        }
        catch (Exception e)
        {
            Godot.GD.PushWarning($"[venues] {ResourceName}: {e.Message}");
            return new Index(new List<VenueRecord>());
        }
    }
}
