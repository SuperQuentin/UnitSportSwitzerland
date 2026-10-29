using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Adventure.Places;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md).

/// <summary>
/// Every giver of the region (#1267 part 1), the same on the server and every client without a byte sent: made from
/// the POI catalogue (huts, stations, ship landings) and, when a region was built with it, the embedded
/// <c>givers.tsv.gz</c>. Bucketed per 1 km tile; <see cref="Near"/> and <see cref="InCell"/> allocate nothing once the
/// caller's list has grown. Read-only once built.
/// </summary>
public sealed class GiverCatalogue
{
    private static readonly Giver[] None = Array.Empty<Giver>();

    private readonly Giver[] _all;
    private readonly Dictionary<(int, int), Giver[]> _tiles = new();
    private readonly Dictionary<string, Giver> _byId = new(StringComparer.Ordinal);
    private readonly Giver[] _trade, _huts;

    public static GiverCatalogue Empty { get; } = new(None);

    public IReadOnlyList<Giver> All => _all;
    public int Count => _all.Length;

    /// <summary>Givers a specialty can be delivered to: a Host, a Dairy, a Postmaster, a StationMaster.</summary>
    public ReadOnlySpan<Giver> Trade => _trade;
    /// <summary>The hut wardens, whom supplies are carried up to.</summary>
    public ReadOnlySpan<Giver> Huts => _huts;

    public GiverCatalogue(IEnumerable<Giver> givers)
    {
        _all = givers.GroupBy(g => g.Id, StringComparer.Ordinal).Select(g => g.First()).OrderBy(g => g.Id, StringComparer.Ordinal).ToArray();
        foreach (var g in _all) _byId[g.Id] = g;
        foreach (var bucket in _all.GroupBy(g => Tile(g.E, g.N))) _tiles[bucket.Key] = bucket.ToArray();
        _trade = _all.Where(g => g.Kind is GiverKind.Host or GiverKind.Dairy or GiverKind.Postmaster or GiverKind.StationMaster).ToArray();
        _huts = _all.Where(g => g.Kind == GiverKind.HutWarden).ToArray();
    }

    private static (int, int) Tile(double e, double n) => ((int)Math.Floor(e / 1000), (int)Math.Floor(n / 1000));

    public Giver? Resolve(string id) => id != null && _byId.TryGetValue(id, out var g) ? g : null;

    /// <summary>The givers within <paramref name="radiusM"/> of a point into <paramref name="into"/> (cleared first).</summary>
    public void Near(double e, double n, float radiusM, List<Giver> into)
    {
        into.Clear();
        double r2 = (double)radiusM * radiusM;
        var (e0, n0) = Tile(e - radiusM, n - radiusM);
        var (e1, n1) = Tile(e + radiusM, n + radiusM);
        for (int te = e0; te <= e1; te++)
            for (int tn = n0; tn <= n1; tn++)
            {
                if (!_tiles.TryGetValue((te, tn), out var bucket)) continue;
                foreach (var g in bucket)
                {
                    double de = g.E - e, dn = g.N - n;
                    if (de * de + dn * dn <= r2) into.Add(g);
                }
            }
    }

    /// <summary>The givers in a 5 km cell (<see cref="Postkarte.CellOf"/>), in id order.</summary>
    public void InCell(int cellE, int cellN, List<Giver> into)
    {
        into.Clear();
        for (int te = cellE * Postkarte.CellKm; te < (cellE + 1) * Postkarte.CellKm; te++)
            for (int tn = cellN * Postkarte.CellKm; tn < (cellN + 1) * Postkarte.CellKm; tn++)
                if (_tiles.TryGetValue((te, tn), out var bucket)) into.AddRange(bucket);
        into.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
    }

    // ---- making them ---------------------------------------------------------------------------------

    /// <summary>The wardens of the huts, the masters of the stations and landings of a POI catalogue.</summary>
    public static IEnumerable<Giver> FromPois(PoiCatalogue pois)
    {
        foreach (var poi in pois.All)
        {
            GiverKind kind;
            switch (poi.Kind)
            {
                case PoiKind.Hut: kind = GiverKind.HutWarden; break;
                case PoiKind.RailStation: kind = GiverKind.StationMaster; break;
                case PoiKind.ShipLanding: kind = GiverKind.ShipMaster; break;
                default: continue;
            }
            string id = $"giver:{Tag(kind)}:{ErrandHash.Hex(poi.Hash)}";
            // a hut's warden stands on a side of the hut by its hash; a station's and a landing's master 5 m east of the point
            double angle = kind == GiverKind.HutWarden ? ErrandHash.Unit(ErrandHash.Mix(poi.Hash)) * Math.PI * 2 : Math.PI / 2;
            double dist = kind == GiverKind.HutWarden ? 6 : 5;
            double e = poi.E + Math.Sin(angle) * dist, n = poi.N + Math.Cos(angle) * dist;
            yield return new Giver(id, kind, e, n, (float)Wrap(angle + Math.PI), poi.Canton, poi.Elevation, poi.Name, "poi:" + poi.Id);
        }
    }

    /// <summary>The givers of <c>givers.tsv.gz</c> (the preprocessor's farms, dairies, post offices and Beizen); a row of a kind this build does not know is skipped.</summary>
    public static IEnumerable<Giver> FromRecords(IEnumerable<GiverRecord> rows)
    {
        foreach (var r in rows)
            if (r.Kind is >= (int)GiverKind.Farmer and <= (int)GiverKind.Host)
                yield return new Giver(r.Id, (GiverKind)r.Kind, r.E, r.N, r.Heading, r.Canton, r.Elevation, r.Name, r.Source);
    }

    /// <summary>The embedded list, empty when the build has none (a data step, not a code one: <c>docs/notes/adventure/errands.md</c>).</summary>
    public static IEnumerable<Giver> FromEmbedded(System.Reflection.Assembly assembly)
    {
        using var s = assembly.GetManifestResourceStream(ResourceName);
        if (s == null) return Array.Empty<Giver>();
        return FromRecords(GiverFormat.Read(s)).ToList();
    }

    public const string ResourceName = "givers.tsv.gz";

    public static string Tag(GiverKind kind) => kind switch
    {
        GiverKind.HutWarden => "hut", GiverKind.StationMaster => "station", GiverKind.ShipMaster => "ship", GiverKind.Farmer => "farm",
        GiverKind.Senn => "senn", GiverKind.Dairy => "dairy", GiverKind.Postmaster => "post", _ => "host",
    };

    private static double Wrap(double a) => a - Math.Floor(a / (Math.PI * 2)) * Math.PI * 2;

    /// <summary>
    /// The fixture set (<c>--givers fixture</c>): eight givers round the fixture start, all in VS, at the fixture POIs'
    /// places. Their declared elevations (a Senn at 1 500 m, a Dairy at 900 m, a warden at 1 800 m) are what the cards
    /// read: the fixture ground itself is flat.
    /// </summary>
    public static GiverCatalogue Fixture(double startE = PoiCatalogue.FixtureE, double startN = PoiCatalogue.FixtureN)
    {
        Giver G(GiverKind kind, string name, double x, double y, float elevation) =>
            new($"giver:{Tag(kind)}:{ErrandHash.Hex(ErrandHash.Of("fixture:" + name))}", kind, startE + x, startN + y, 0f, "VS", elevation, name, "fixture");
        return new GiverCatalogue(new[]
        {
            G(GiverKind.Postmaster, "Fixture Post Office", -70, -30, 500),
            G(GiverKind.Farmer, "Fixture Farm", -90, -70, 500),
            G(GiverKind.HutWarden, "Fixture Hut", 60, 90, 1800),
            G(GiverKind.StationMaster, "Fixture Station", 305, 120, 500),
            G(GiverKind.ShipMaster, "Fixture Landing", 155, 260, 500),
            G(GiverKind.Senn, "Fixture Alp Dairy", 240, -60, 1500),
            G(GiverKind.Dairy, "Fixture Village Dairy", 130, 40, 900),
            G(GiverKind.Host, "Fixture Beiz", -40, 120, 500),
        });
    }
}
