using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

// Plain C#, no I/O: linked into the unit tests (tests/UnitSportSwitzerland.Tests/GiverPickTests.cs).

/// <summary>
/// What the givers stage (#1339) takes from OpenStreetMap and the building tiles, as pure functions: which tag set makes which giver,
/// the alp dairy against the village dairy by height, and the thinning (at most one farm and one Beiz per 2 km cell, picked by the
/// hash of their source id so a rerun and the nationwide build agree). Kinds are the game's <c>GiverKind</c> numbers.
/// </summary>
public static class GiverPick
{
    public const int Farmer = 3, Senn = 4, Dairy = 5, Postmaster = 6, Host = 7;
    /// <summary>A dairy at or above this height is an alp dairy (a Senn).</summary>
    public const double SennFromM = 1200;
    public const double CellM = 2000;
    /// <summary>The barn of a farm is at least this big (m², footprint box) and a dwelling stands within <see cref="DwellingM"/> of it.</summary>
    public const double BarnM2 = 120, DwellingM = 40;
    /// <summary>Two givers of a kind this close are one place (an OSM node and its outline).</summary>
    public const double MergeM = 25;

    private static readonly Regex DairyName = new(@"k(ä|ae)serei|sennerei|ch(ä|ae)si|fromagerie|caseificio|laiterie|latteria|sennh(ü|ue)tte|alpk(ä|ae)se",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A giver found in the source, before its height and canton: <see cref="Kind"/> Dairy stands for a Senn or a Dairy.</summary>
    public sealed record Candidate(string Source, int Kind, double E, double N, string Name, double Heading = double.NaN);

    /// <summary>The OSM tag keys the stage reads.</summary>
    public static readonly HashSet<string> Keys = new()
    {
        "amenity", "craft", "shop", "name", "cuisine", "place", "landuse", "building", "disused", "operator", "brand",
    };

    /// <summary>The kind of giver a tag set makes, or 0 for none. A dairy is <see cref="Dairy"/> here: the height splits it later.</summary>
    public static int KindOf(IReadOnlyDictionary<string, string> t)
    {
        if (t.ContainsKey("disused")) return 0;
        string amenity = t.GetValueOrDefault("amenity") ?? "";
        string name = t.GetValueOrDefault("name") ?? "";
        if (amenity == "post_office") return Postmaster;
        string shop = t.GetValueOrDefault("shop") ?? "";
        if (t.GetValueOrDefault("craft") == "cheese_maker" || shop is "cheese" or "dairy" || DairyName.IsMatch(name) && amenity is not ("restaurant" or "cafe" or "bar" or "pub" or "fast_food"))
            return Dairy;
        if (amenity == "restaurant")
        {
            var cuisine = ";" + (t.GetValueOrDefault("cuisine") ?? "").ToLowerInvariant().Replace(" ", "") + ";";
            if (cuisine.Contains(";regional;") || cuisine.Contains(";swiss;")) return Host;
            return 0;
        }
        if (t.GetValueOrDefault("place") == "farm" || t.GetValueOrDefault("landuse") == "farmyard" || t.GetValueOrDefault("building") == "farm")
            return Farmer;
        return 0;
    }

    public static int Resolve(int kind, double elevation) => kind == Dairy && elevation >= SennFromM ? Senn : kind;

    public static string Tag(int kind) => kind switch
    {
        Farmer => "farm", Senn => "senn", Dairy => "dairy", Postmaster => "post", Host => "host", _ => "giver",
    };

    /// <summary>The default name of a giver the source did not name.</summary>
    public static string DefaultName(int kind) => kind switch
    {
        Farmer => "Farm", Senn => "Alp dairy", Dairy => "Dairy", Postmaster => "Post office", Host => "Restaurant", _ => "Giver",
    };

    /// <summary>FNV-1a of a string, mixed: the stage's own hash, stable across runs (ids <c>giver:farm:0a1b2c3d4e</c>).</summary>
    public static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
        return h;
    }

    public static string IdOf(int kind, string source) => $"giver:{Tag(kind)}:{(Hash(source) & 0xFFFFFFFFFFUL):x10}";

    /// <summary>
    /// The rows: each candidate's kind split by height and canton resolved, merged where two of a kind lie within 25 m, and thinned
    /// to one farm and one Beiz per 2 km cell (the lowest hash of the source id wins). Sorted by id.
    /// </summary>
    public static List<GiverRecord> Pick(IEnumerable<Candidate> candidates, Func<double, double, double> elevationAt, Func<double, double, string> cantonAt)
    {
        var rows = new List<GiverRecord>();
        var byCell = new Dictionary<(int Kind, long CellE, long CellN), (Candidate C, ulong Score)>();
        var others = new List<Candidate>();
        foreach (var c in candidates.OrderBy(c => c.Source, StringComparer.Ordinal))
        {
            if (c.Kind is Farmer or Host)
            {
                var key = (c.Kind, (long)Math.Floor(c.E / CellM), (long)Math.Floor(c.N / CellM));
                ulong score = Hash(c.Source);
                if (!byCell.TryGetValue(key, out var best) || score < best.Score) byCell[key] = (c, score);
            }
            else others.Add(c);
        }
        var chosen = new List<Candidate>(byCell.Values.Select(v => v.C));
        // dairies and post offices: all, a pair of the same kind within 25 m is one (the node and its outline)
        var grid = new Dictionary<(int, long, long), List<Candidate>>();
        foreach (var c in others)
        {
            var key = (c.Kind, (long)Math.Floor(c.E / MergeM), (long)Math.Floor(c.N / MergeM));
            bool dup = false;
            for (long de = -1; de <= 1 && !dup; de++)
                for (long dn = -1; dn <= 1 && !dup; dn++)
                    if (grid.TryGetValue((c.Kind, key.Item2 + de, key.Item3 + dn), out var list))
                        dup = list.Any(o => (o.E - c.E) * (o.E - c.E) + (o.N - c.N) * (o.N - c.N) < MergeM * MergeM);
            if (dup) continue;
            if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = new List<Candidate>();
            cell.Add(c);
            chosen.Add(c);
        }
        foreach (var c in chosen)
        {
            double z = elevationAt(c.E, c.N);
            int kind = Resolve(c.Kind, double.IsFinite(z) ? z : 0);
            double heading = double.IsNaN(c.Heading) ? Hash(c.Source + "#h") % 628 / 100.0 : c.Heading;
            rows.Add(new GiverRecord(IdOf(kind, c.Source), kind, Math.Round(c.E, 1), Math.Round(c.N, 1), (float)heading, cantonAt(c.E, c.N),
                (float)Math.Round(double.IsFinite(z) ? z : 0), c.Name.Length > 0 ? c.Name : DefaultName(kind), c.Source));
        }
        rows.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return rows;
    }

    /// <summary>A farm from the building tiles: a barn of at least 120 m² (its footprint box) with a dwelling within 40 m. The point is the barn's.</summary>
    public readonly record struct Solid(double E, double N, double AreaM2, bool Barn, bool Dwelling, string Source);

    /// <summary>The farms among a tile's solids (Hof): each barn with a dwelling near it, one candidate per barn.</summary>
    public static IEnumerable<Candidate> Farms(IReadOnlyList<Solid> solids)
    {
        foreach (var s in solids)
        {
            if (!s.Barn || s.AreaM2 < BarnM2) continue;
            bool home = false;
            foreach (var d in solids)
                if (d.Dwelling && (d.E - s.E) * (d.E - s.E) + (d.N - s.N) * (d.N - s.N) <= DwellingM * DwellingM) { home = true; break; }
            if (home) yield return new Candidate(s.Source, Farmer, s.E, s.N, "");
        }
    }
}
