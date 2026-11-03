using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

// Plain C#, no Godot: linked into the unit tests (AtlasFactsTests, AtlasCardsTests; docs/notes/adventure/atlas.md).

/// <summary>A POI's Wikidata facts (<c>atlas_facts.json</c>, the preprocessor's <c>--wikidata</c>; CC0).</summary>
public sealed record WikiFact(string? Qid, string? Label, double? Elevation, int? Year, double? Length, double? Area, string? Image);

/// <summary>A curated Atlas card (<c>res://assets/adventure/atlas_cards.json</c>): a short alias, the canonical id, a title and the fact.</summary>
public sealed record AtlasCard(string Alias, string Id, string Title, string Fact);

/// <summary>
/// The Atlas's card texts (#1224, places plan 1.4 and 7.2), in three layers: a curated card when there is
/// one, else a line generated from the POI's own data and the catalogue round it, followed by its
/// Wikidata facts. English; numbers invariant with a thin space as the thousands mark, whatever the locale.
/// </summary>
public static class AtlasFacts
{
    private const char Thin = ' ';

    /// <summary>"1 797" (a thin space), invariant.</summary>
    public static string Num(double v) => Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture).Replace(',', Thin);

    /// <summary>"2.6", invariant.</summary>
    public static string Dec(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public static string KindName(PoiKind kind) => kind switch
    {
        PoiKind.Peak => "summit", PoiKind.Pass => "pass", PoiKind.SummitCross => "summit cross",
        PoiKind.Waterfall => "waterfall", PoiKind.Lake => "lake", PoiKind.LookoutTower => "lookout tower",
        PoiKind.Church => "church", PoiKind.Chapel => "chapel", PoiKind.Castle => "castle",
        PoiKind.Bridge => "covered bridge", PoiKind.Dam => "dam", PoiKind.RailStation => "railway station",
        PoiKind.BusStop => "bus stop", PoiKind.ShipLanding => "boat landing", PoiKind.CableCarStation => "cable car",
        PoiKind.Hut => "mountain hut", _ => "place",
    };

    /// <summary>The generated line (layer 1) plus the Wikidata facts (layer 2) of a POI.</summary>
    public static string For(Poi poi, PoiCatalogue cat, WikiFact? wiki = null)
    {
        string where = poi.Canton.Length > 0 ? $" in {AtlasChapters.NameOf(poi.Canton)}" : "";
        string line = poi.Kind switch
        {
            PoiKind.Peak or PoiKind.SummitCross => PeakLine(poi, cat, where),
            PoiKind.Lake => LakeLine(poi, where),
            PoiKind.CableCarStation => CableCarLine(poi, cat, where),
            PoiKind.RailStation => StationLine(poi, cat, where),
            PoiKind.Pass => $"A {(poi.Is(PoiFlags.Viewpoint) ? "road pass" : "mountain pass")} at {Num(poi.Elevation)} m{where}.",
            _ => $"A {KindName(poi.Kind)} at {Num(poi.Elevation)} m{where}.",
        };
        if (wiki == null) return line;
        var more = new List<string>();
        if (wiki.Year is { } year && year > 0 && poi.Kind is not (PoiKind.Peak or PoiKind.Pass or PoiKind.Lake or PoiKind.Waterfall))
        {
            string y = year.ToString(CultureInfo.InvariantCulture);
            more.Add(poi.Kind is PoiKind.Castle or PoiKind.Church or PoiKind.Chapel ? $"It dates from {y}." : $"Built in {y}.");
        }
        if (wiki.Length is { } length && length > 0 && poi.Kind is PoiKind.Bridge or PoiKind.Dam)
            more.Add($"{Num(length)} m long.");
        if (wiki.Area is { } area && area > 0 && poi.Kind == PoiKind.Lake && poi.Outline == null)
            more.Add($"{Dec(area)} km² of water.");
        return more.Count == 0 ? line : line + " " + string.Join(" ", more);
    }

    private static string PeakLine(Poi poi, PoiCatalogue cat, string where)
    {
        string text = $"A summit of {Num(poi.Elevation)} m{where}";
        if (poi.Canton.Length > 0)
        {
            int rank = 1, count = 0;
            foreach (var p in cat.InCanton(poi.Canton))
            {
                if (p.Kind != PoiKind.Peak) continue;
                count++;
                if (p.Elevation > poi.Elevation) rank++;
            }
            if (poi.Kind == PoiKind.Peak && count > 1) text += rank == 1 ? $", the highest of the {Num(count)} on this map" : $", the {Ordinal(rank)} highest of the {Num(count)} on this map";
        }
        text += ".";
        if (poi.Is(PoiFlags.Cross)) text += " A cross stands on top.";
        if (Nearest(cat, poi, PoiKind.RailStation, 15000) is { } station) text += $" Nearest station: {station.Name}.";
        return text;
    }

    private static string LakeLine(Poi poi, string where)
    {
        double ha = poi.Outline is { } o ? Area(o) / 10_000 : 0;
        string size = ha >= 100 ? $"{Dec(ha / 100)} km²" : ha > 0 ? $"{Num(ha)} ha" : "";
        return size.Length > 0 ? $"A lake of {size} at {Num(poi.Elevation)} m{where}." : $"A lake at {Num(poi.Elevation)} m{where}.";
    }

    private static string CableCarLine(Poi poi, PoiCatalogue cat, string where)
    {
        Poi? other = null;
        if (poi.LineKey != 0)
            foreach (var p in cat.OfKind(PoiKind.CableCarStation))
                if (p != poi && p.LineKey == poi.LineKey) { other = p; break; }
        string end = poi.Is(PoiFlags.Top) ? "The top station of a cable car" : "The valley station of a cable car";
        if (other == null) return $"{end}, at {Num(poi.Elevation)} m{where}.";
        double rise = Math.Abs(poi.Elevation - other.Elevation);
        double length = Math.Sqrt((poi.E - other.E) * (poi.E - other.E) + (poi.N - other.N) * (poi.N - other.N) + rise * rise);
        return $"{end}, at {Num(poi.Elevation)} m{where}: {Num(rise)} m of rise over {Dec(length / 1000)} km, from {other.Name}.";
    }

    private static string StationLine(Poi poi, PoiCatalogue cat, string where)
    {
        string text = $"A railway station at {Num(poi.Elevation)} m{where}.";
        if (Nearest(cat, poi, PoiKind.Peak, 10000) is { } peak) text += $" The nearest summit is {peak.Name} ({Num(peak.Elevation)} m).";
        return text;
    }

    private static Poi? Nearest(PoiCatalogue cat, Poi from, PoiKind kind, float radiusM)
    {
        var near = new List<Poi>();
        cat.Near(from.E, from.N, radiusM, near, kind);
        Poi? best = null;
        double bd = double.MaxValue;
        foreach (var p in near)
        {
            if (p == from) continue;
            double d = (p.E - from.E) * (p.E - from.E) + (p.N - from.N) * (p.N - from.N);
            if (d < bd) { bd = d; best = p; }
        }
        return best;
    }

    /// <summary>Area of a ring (E, N pairs), m².</summary>
    public static double Area(double[] ring)
    {
        double a = 0;
        int count = ring.Length / 2;
        for (int i = 0, j = count - 1; i < count; j = i++)
            a += (ring[j * 2] - ring[i * 2]) * (ring[j * 2 + 1] + ring[i * 2 + 1]);
        return Math.Abs(a / 2);
    }

    public static string Ordinal(int n) => n % 100 is 11 or 12 or 13 ? $"{n}th"
        : (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" };

    // ---- the files --------------------------------------------------------------------------------

    /// <summary>
    /// Reads <c>atlas_facts.json</c> (<c>{ "&lt;canonical id&gt;": { "qid", "label", "elevation", "year",
    /// "length", "area", "image" } }</c>), keyed again by tag and hash so a renamed slug still finds its facts.
    /// </summary>
    public static Dictionary<(PoiKind, ulong), WikiFact> ParseFacts(string json)
    {
        var facts = new Dictionary<(PoiKind, ulong), WikiFact>();
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!PoiId.TryParse(prop.Name, out var kind, out ulong hash) || prop.Value.ValueKind != JsonValueKind.Object) continue;
            var v = prop.Value;
            facts[(kind, hash)] = new WikiFact(Str(v, "qid"), Str(v, "label"), Dbl(v, "elevation"),
                Dbl(v, "year") is { } y ? (int)y : null, Dbl(v, "length"), Dbl(v, "area"), Str(v, "image"));
        }
        return facts;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static double? Dbl(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    private static readonly Regex AliasShape = new("^[a-z]+:[a-z0-9-]+$", RegexOptions.CultureInvariant);

    /// <summary>Reads <c>atlas_cards.json</c> (an array of <c>{ "alias", "id", "title", "fact" }</c>).</summary>
    public static List<AtlasCard> ParseCards(string json) =>
        JsonSerializer.Deserialize<List<AtlasCard>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

    /// <summary>What is wrong with a set of cards (the tier-0 test): a malformed or repeated alias, an id that is not canonical, an alias of another kind than its id, an empty text.</summary>
    public static List<string> Problems(IEnumerable<AtlasCard> cards)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cards)
        {
            if (c.Alias == null || !AliasShape.IsMatch(c.Alias)) { problems.Add($"malformed alias '{c.Alias}'"); continue; }
            if (!seen.Add(c.Alias)) problems.Add($"alias {c.Alias} twice");
            if (!PoiId.TryParse(c.Id ?? "", out var kind, out _)) { problems.Add($"{c.Alias}: id '{c.Id}' is not canonical"); continue; }
            if (!ids.Add(c.Id!)) problems.Add($"{c.Alias}: id {c.Id} has two cards");
            if (c.Alias[..c.Alias.IndexOf(':')] != PoiId.Tag(kind)) problems.Add($"{c.Alias}: the alias's kind is not its id's ({c.Id})");
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Fact)) problems.Add($"{c.Alias}: no title or fact");
        }
        return problems;
    }
}
