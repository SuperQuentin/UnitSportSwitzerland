using System.Globalization;
using System.Text;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// Finding a venue of <see cref="StadiumCatalog"/> by what a player types (<c>/stadium</c>, #994):
/// its number in the list, its name or its town, case and accents ignored. Pure, so the server, an
/// offline game and the Tab completer all read the same answer.
/// </summary>
public static class StadiumLookup
{
    /// <summary>"stadium", "arena", "open air": the kind as the list prints it.</summary>
    public static string KindName(VenueKind kind) => kind switch
    {
        VenueKind.Arena => "arena",
        VenueKind.OpenAir => "open air",
        _ => "stadium",
    };

    /// <summary>The words <c>/stadium list</c> takes to keep one kind.</summary>
    public static readonly string[] KindWords = ["stadium", "arena", "openair"];

    public static bool TryKind(string word, out VenueKind kind)
    {
        switch (Fold(word))
        {
            case "stadium" or "stadiums" or "stade" or "stadion": kind = VenueKind.Stadium; return true;
            case "arena" or "arenas": kind = VenueKind.Arena; return true;
            case "openair" or "openairs" or "festival": kind = VenueKind.OpenAir; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>"Stadion Wankdorf (Bern)".</summary>
    public static string Label(StadiumSpec s) => s.Town.Length == 0 ? s.Name : $"{s.Name} ({s.Town})";

    /// <summary>The 1-based number a venue has in the list (and <c>/stadium #n</c>).</summary>
    public static int Number(StadiumSpec s) => Array.IndexOf(StadiumCatalog.All, s) + 1;

    /// <summary>
    /// The venues a query names: <c>#n</c> or <c>n</c> the n-th, a name typed whole that one alone,
    /// else every venue whose name and town hold all the query's words. Empty when none does.
    /// </summary>
    public static IReadOnlyList<StadiumSpec> Find(string query)
    {
        var all = StadiumCatalog.All;
        string q = query.Trim().TrimStart('#');
        if (q.Length == 0) return [];
        if (int.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            return n >= 1 && n <= all.Length ? [all[n - 1]] : [];

        string whole = Fold(q);
        foreach (var s in all)
            if (Fold(s.Name) == whole) return [s];

        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Fold).Where(w => w.Length > 0).ToArray();
        return all.Where(s =>
        {
            string hay = Fold(s.Name + " " + s.Town);
            return words.All(hay.Contains);
        }).ToList();
    }

    /// <summary>The venue nearest an LV95 point and how far it is, m.</summary>
    public static (StadiumSpec Venue, double Distance) Nearest(double e, double n) =>
        StadiumCatalog.All
            .Select(s => (s, Math.Sqrt((s.E - e) * (s.E - e) + (s.N - n) * (s.N - n))))
            .MinBy(x => x.Item2);

    /// <summary>"12 km", "800 m".</summary>
    public static string Distance(double metres) => metres >= 2000
        ? string.Create(CultureInfo.InvariantCulture, $"{metres / 1000:F0} km")
        : string.Create(CultureInfo.InvariantCulture, $"{metres:F0} m");

    /// <summary><c>/stadium list [kind]</c>: one line per kind, every venue numbered.</summary>
    public static IEnumerable<string> List(VenueKind? only = null)
    {
        foreach (var kind in new[] { VenueKind.Stadium, VenueKind.Arena, VenueKind.OpenAir })
        {
            if (only is { } k && k != kind) continue;
            var rows = StadiumCatalog.All.Where(s => s.Kind == kind).Select(s => $"{Number(s)} {Label(s)}").ToList();
            if (rows.Count == 0) continue;
            string title = kind == VenueKind.OpenAir ? "Open airs" : char.ToUpperInvariant(KindName(kind)[0]) + KindName(kind)[1..] + "s";
            yield return $"{title} ({rows.Count}): {string.Join(", ", rows)}";
        }
    }

    /// <summary>Lower case, accents off, only letters and digits: "Brügglifeld" and "brugglifeld" are one.</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
