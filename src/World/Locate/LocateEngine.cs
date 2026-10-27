using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Locate;

/// <summary>A named thing on the map, from one of the lists the server holds (towns, POIs, venues, castles...).</summary>
public readonly record struct LocatePoint(LocateTarget Target, string Name, double E, double N, string Detail = "");

/// <summary>One answer: what it is, a few words about it, where, and how far from where the search began.</summary>
public readonly record struct LocateHit(string Title, string Detail, double E, double N, double Distance, IndexedBuilding? Building = null);

/// <summary>What a search gave: the hits nearest first, or why there are none.</summary>
public sealed record LocateResult(List<LocateHit> Hits, string? Refusal = null)
{
    public static LocateResult Refused(string why) => new([], why);
}

/// <summary>
/// <c>/locate</c>'s search (#1277), the same on a server and offline: the points of every list the
/// world holds (<see cref="LocatePoint"/>, gathered once by <c>LocateService</c>), the building index when
/// it is ready, and the live things (the steamer) asked for at each search. Pure C#, tier 0.
/// </summary>
public sealed class LocateEngine
{
    /// <summary>Hits closer than this to one already kept are the same thing (a castle's solids, a stop in two lists).</summary>
    public const double SameThing = 60;
    /// <summary>A church takes the name of the survey's church or chapel this close to its hall.</summary>
    public const double NameReach = 90;

    private readonly Dictionary<LocateTarget, List<LocatePoint>> _points = new();

    /// <summary>Null until the server's index is built (or forever in a world without building tiles).</summary>
    public BuildingIndex? Buildings { get; set; }

    /// <summary>Why the building nouns cannot answer yet ("still indexing, 40 %"); null once they can, or never will.</summary>
    public Func<string?> BuildingsPending { get; set; } = () => null;

    /// <summary>Things that move: asked at each search, never kept.</summary>
    public Func<IEnumerable<LocatePoint>> Live { get; set; } = () => [];

    public LocateEngine(IEnumerable<LocatePoint> points)
    {
        foreach (var p in points)
        {
            if (!_points.TryGetValue(p.Target, out var l)) _points[p.Target] = l = new List<LocatePoint>();
            l.Add(p);
        }
    }

    public int CountOf(LocateTarget t) => _points.TryGetValue(t, out var l) ? l.Count : 0;

    /// <summary>
    /// The <paramref name="max"/> nearest things <paramref name="q"/> asks for, from (<paramref name="e"/>,
    /// <paramref name="n"/>). With <see cref="LocateModifier.Parking"/> the building hits are only candidates
    /// (the roll and the box say a ramp fits; the footprint decides), so up to <paramref name="candidates"/> of them come back.
    /// </summary>
    public LocateResult Find(LocateQuery q, double e, double n, int max = 3, int candidates = 40)
    {
        bool fromBuildings = q.OnBuildings || (q.Target == LocateTarget.Church && Buildings != null);
        if (fromBuildings)
        {
            if (Buildings == null)
                return LocateResult.Refused(BuildingsPending() ?? "This world has no building data to search.");
            int want = q.Has(LocateModifier.Parking) ? candidates : max;
            var near = Buildings.Nearest(e, n, b => Matches(q, b), want * 3);
            var hits = new List<LocateHit>();
            foreach (var (b, d) in near)
            {
                if (hits.Any(h => Near(h.E, h.N, b.E, b.N, SameThing))) continue;
                hits.Add(new LocateHit(TitleOf(q, b), DetailOf(b), b.E, b.N, d, b));
                if (hits.Count >= want) break;
            }
            return new LocateResult(hits);
        }

        if (q.Target != LocateTarget.Steamer && CountOf(q.Target) == 0 && FromPoisOnly(q.Target))
            return LocateResult.Refused($"This map has no list of {LocateEngine.Plural(LocateVocabulary.Label(q.Target))}: its points of interest (pois.bin, the preprocessor's --pois) are not there.");
        IEnumerable<LocatePoint> pool = q.Target == LocateTarget.Steamer ? Live().Where(p => p.Target == LocateTarget.Steamer)
            : _points.TryGetValue(q.Target, out var list) ? list : [];
        var ranked = pool.Select(p => (p, d: Dist(e, n, p.E, p.N))).OrderBy(x => x.d);
        var kept = new List<LocateHit>();
        foreach (var (p, d) in ranked)
        {
            if (kept.Any(h => Near(h.E, h.N, p.E, p.N, SameThing))) continue;
            kept.Add(new LocateHit(p.Name, p.Detail, p.E, p.N, d));
            if (kept.Count >= max) break;
        }
        return new LocateResult(kept);
    }

    /// <summary>The nouns only the survey's points of interest (<c>pois.bin</c>) answer.</summary>
    public static bool FromPoisOnly(LocateTarget t) => t is LocateTarget.SummitCross or LocateTarget.Waterfall or LocateTarget.Lake
        or LocateTarget.Tower or LocateTarget.Chapel or LocateTarget.Bridge or LocateTarget.Dam or LocateTarget.Station
        or LocateTarget.BusStop or LocateTarget.CableCar or LocateTarget.Hut;

    /// <summary>Whether a building is what the query asks for, selectors and all (the garage only as a candidate).</summary>
    public static bool Matches(LocateQuery q, IndexedBuilding b)
    {
        switch (q.Target)
        {
            case LocateTarget.Church: if (!b.Is(IndexedFlags.Church)) return false; break;
            case LocateTarget.Bank: if (!b.Is(IndexedFlags.Bank)) return false; break;
            case LocateTarget.Building: break;
            default:
                if (LocateVocabulary.KindOf(q.Target) is { } kind && b.Kind != kind) return false;
                break;
        }
        if (q.Has(LocateModifier.Spire) && !b.Is(IndexedFlags.Spire)) return false;
        if (q.Has(LocateModifier.Parking) && !b.Is(IndexedFlags.GarageRoll)) return false;
        if (q.Has(LocateModifier.Tall) && b.Height < LocateVocabulary.TallHeight) return false;
        if (q.Has(LocateModifier.Old) && (b.Year == 0 || b.Year >= LocateVocabulary.OldBefore)) return false;
        if (q.Has(LocateModifier.New) && b.Year < LocateVocabulary.NewFrom) return false;
        if (q.MinFloors > 0 && b.Floors < q.MinFloors) return false;
        if (q.MinHeight > 0 && b.Height < q.MinHeight) return false;
        return true;
    }

    private string TitleOf(LocateQuery q, IndexedBuilding b)
    {
        if (b.Is(IndexedFlags.Church))
        {
            // the survey names churches and chapels: the nearest one to the hall
            string? name = null;
            double best = NameReach;
            foreach (var t in (ReadOnlySpan<LocateTarget>)[LocateTarget.Church, LocateTarget.Chapel])
                if (_points.TryGetValue(t, out var l))
                    foreach (var p in l)
                    {
                        double d = Dist(b.E, b.N, p.E, p.N);
                        if (d < best) { best = d; name = p.Name; }
                    }
            if (name != null) return name;
            return b.Is(IndexedFlags.Spire) ? "Church with a spire" : "Church";
        }
        if (b.Is(IndexedFlags.Bank) && q.Target == LocateTarget.Bank) return "Bank";
        string kind = KindName(b.Kind);
        return char.ToUpperInvariant(kind[0]) + kind[1..];
    }

    public static string KindName(BuildingKind k) => k switch
    {
        BuildingKind.House => "house",
        BuildingKind.Apartment => "apartment building",
        BuildingKind.Commercial => "commercial building",
        BuildingKind.Industrial => "industrial building",
        BuildingKind.Agricultural => "farm building",
        BuildingKind.Sacral => "church building",
        BuildingKind.Civic => "public building",
        BuildingKind.Annex => "annex",
        BuildingKind.UnderConstruction => "construction site",
        BuildingKind.Garage => "garage",
        _ => "building",
    };

    /// <summary>"8 floors, 24 m, built 1972, parking entrance".</summary>
    public static string DetailOf(IndexedBuilding b)
    {
        var parts = new List<string>(4);
        if (b.Floors > 0) parts.Add(b.Floors == 1 ? "1 floor" : $"{b.Floors} floors");
        if (b.Height > 0) parts.Add(b.Height.ToString("0", CultureInfo.InvariantCulture) + " m");
        if (b.Year > 0) parts.Add($"built {b.Year}");
        return string.Join(", ", parts);
    }

    private static double Dist(double e0, double n0, double e1, double n1) => Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0));
    private static bool Near(double e0, double n0, double e1, double n1, double r) => Dist(e0, n0, e1, n1) < r;

    /// <summary>
    /// The answer's lines: a heading, then one per hit, each ending in its <c>@E,N</c> (the chat puts the
    /// buttons under it). <paramref name="from"/> names where distances count from ("you", a town).
    /// </summary>
    public static List<string> Lines(LocateQuery q, LocateResult r, double e, double n, string from)
    {
        if (r.Refusal != null) return [r.Refusal];
        if (r.Hits.Count == 0) return [$"No {q.Describe()} found on this map."];
        var lines = new List<string>(r.Hits.Count + 1)
        {
            r.Hits.Count == 1 ? $"Nearest {q.Describe()} (from {from}):" : $"Nearest {q.Describe()}s (from {from}):",
        };
        // "Nearest church with a spires" reads badly: the plural goes on the noun
        if (r.Hits.Count > 1 && q.Describe().Contains(" with "))
        {
            string d = q.Describe();
            int at = d.IndexOf(" with ", StringComparison.Ordinal);
            lines[0] = $"Nearest {Plural(d[..at])}{d[at..]} (from {from}):";
        }
        else if (r.Hits.Count > 1) lines[0] = $"Nearest {Plural(q.Describe())} (from {from}):";
        foreach (var h in r.Hits)
        {
            string detail = h.Detail.Length > 0 ? $" ({h.Detail})" : "";
            lines.Add($"  {h.Title}{detail} · {LocateCoords.Distance(h.Distance)} {LocateCoords.Compass(e, n, h.E, h.N)} · {LocateCoords.Format(h.E, h.N)}");
        }
        return lines;
    }

    /// <summary>
    /// "Still indexing the buildings: 40 %, about 25 s left." The time left is the pace so far carried over the tiles
    /// still to read; before a tenth of a second's worth of tiles there is no pace yet.
    /// </summary>
    public static string Indexing(int done, int total, double elapsedSeconds)
    {
        int pct = total <= 0 ? 0 : Math.Clamp(100 * done / total, 0, 99);
        if (done <= 0 || elapsedSeconds < 0.1) return $"Still indexing the buildings: {pct} %, working out how long it takes. Try again in a few seconds.";
        double left = elapsedSeconds / done * Math.Max(0, total - done);
        string eta = left < 5 ? "a few seconds" : left < 90 ? $"about {Math.Ceiling(left / 5) * 5:0} s" : $"about {Math.Round(left / 60):0} min";
        return $"Still indexing the buildings: {pct} %, {eta} left.";
    }

    public static string Plural(string noun) =>
        noun.EndsWith("IKEA", StringComparison.Ordinal) ? noun + " stores"
        : noun.EndsWith("ss") || noun.EndsWith("ch") || noun.EndsWith("sh") || noun.EndsWith('x') ? noun + "es"
        : noun.EndsWith('y') && !noun.EndsWith("ay") && !noun.EndsWith("ey") ? noun[..^1] + "ies"
        : noun.EndsWith("food") || noun.EndsWith("open air") ? noun + " places"
        : noun + "s";
}
