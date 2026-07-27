using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Terrain.Format;

/// <summary>A settlement you can teleport to, with terrain confirmed present.</summary>
public sealed class Place
{
    public required string Name { get; set; }
    public required string Canton { get; set; }

    /// <summary>LV95 coordinates of the built-up core, not the municipal centroid.</summary>
    public double E { get; set; }
    public double N { get; set; }

    /// <summary>Building count, used to rank search results by size.</summary>
    public int Buildings { get; set; }

    [JsonIgnore]
    public TileId Tile => TileId.FromLv95(E, N);
}

/// <summary>
/// Searchable list of places covered by the imported tiles, written next to the chunks.
/// </summary>
public sealed class PlaceIndex
{
    public List<Place> Places { get; set; } = new();

    public const string FileName = "places.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static PlaceIndex FromJson(string json) =>
        JsonSerializer.Deserialize<PlaceIndex>(json, Options) ?? new PlaceIndex();

    /// <summary>
    /// Ranks matches: names starting with the query first, then by size. Typing "sion"
    /// should offer Sion before Sionne, and a city before a hamlet.
    /// </summary>
    public List<Place> Search(string query, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Places.OrderByDescending(p => p.Buildings).Take(limit).ToList();

        string q = Normalize(query);
        return Places
            .Select(p => (Place: p, Key: Normalize(p.Name)))
            .Where(x => x.Key.Contains(q, StringComparison.Ordinal))
            .OrderBy(x => x.Key.StartsWith(q, StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(x => x.Place.Buildings)
            .Take(limit)
            .Select(x => x.Place)
            .ToList();
    }

    /// <summary>Lowercase and strip accents, so "Genève" matches a typed "geneve".</summary>
    public static string Normalize(string s)
    {
        var decomposed = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
