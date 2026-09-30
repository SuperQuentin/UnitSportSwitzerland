namespace UnitSport.Items;

/// <summary>
/// Turns what a player typed (<c>/spawn energybar 5</c>, <c>/spawn "swiss flag"</c>) into an item
/// and a count. Pure, so the same parse serves the offline chat and the server's check.
/// </summary>
public static class ItemLookup
{
    /// <summary>Most a single <c>/spawn</c> gives; francs are counted, not stacked, so they get more.</summary>
    public const int MaxSpawn = 999;
    public const int MaxSpawnFrancs = 9999;

    /// <summary>Every spawnable item's id name (<c>EnergyBar</c>), which is what completion offers.</summary>
    public static IEnumerable<string> Names() =>
        ItemDefs.All.Select(d => d.Id.ToString());

    /// <summary>
    /// Case, spaces, dashes and accents ignored: "Energy bar", "energybar" and "EnergyBar" are the
    /// same item. An unambiguous prefix also matches ("ene" is the energy bar, "b" is not anything).
    /// </summary>
    public static ItemDef? Find(string query)
    {
        string q = Normalize(query);
        if (q.Length == 0) return null;

        ItemDef? prefix = null;
        int prefixHits = 0;
        foreach (var def in ItemDefs.All)
        {
            string id = Normalize(def.Id.ToString()), name = Normalize(def.Name);
            if (id == q || name == q) return def;
            if (id.StartsWith(q, StringComparison.Ordinal) || name.StartsWith(q, StringComparison.Ordinal))
            {
                prefix = def;
                prefixHits++;
            }
        }
        return prefixHits == 1 ? prefix : null;
    }

    /// <summary>
    /// <c>&lt;item&gt; [count]</c>: the last word is the count when it is a number, the rest is the
    /// item name, so "energy bar 3" works. False, with the reason in <paramref name="error"/>, otherwise.
    /// </summary>
    public static bool TryParse(string text, out ItemDef def, out int count, out string error)
    {
        def = null!;
        count = 1;
        error = "Usage: /spawn <item> [count]";

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;

        if (words.Length > 1 && int.TryParse(words[^1], out int n))
        {
            count = n;
            words = words[..^1];
        }

        string name = string.Join(' ', words);
        if (Find(name) is not { } found)
        {
            error = $"No item matching '{name}'. Press Tab after /spawn to list them.";
            return false;
        }

        def = found;
        count = Math.Clamp(count, 1, found.Id == ItemId.Francs ? MaxSpawnFrancs : MaxSpawn);
        return true;
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s.Normalize(System.Text.NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
