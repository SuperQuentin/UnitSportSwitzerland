using System.Globalization;

namespace UnitSport.Net;

/// <summary>
/// Parsing for the money admin commands (<c>/money</c>, <c>/bank</c>, #262), <c>/despawn</c> and <c>/tp</c>'s coordinates. Pure, no Godot, so the
/// unit tests cover it and the offline chat and the server share one reading.
/// </summary>
public static class AdminArgs
{
    /// <summary>Most an account can hold, so a typo cannot overflow anything downstream.</summary>
    public const long MaxBalance = 1_000_000_000;

    /// <summary><c>&lt;amount&gt;</c> as typed: "500", "-200", "+1k", "2.5k", "1m". Culture-free.</summary>
    public static bool TryAmount(string word, out long amount)
    {
        amount = 0;
        word = word.Trim().ToLowerInvariant();
        double scale = 1;
        if (word.EndsWith('k')) { scale = 1_000; word = word[..^1]; }
        else if (word.EndsWith('m')) { scale = 1_000_000; word = word[..^1]; }
        if (!double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return false;
        value *= scale;
        if (double.IsNaN(value) || Math.Abs(value) > MaxBalance) return false;
        amount = (long)Math.Round(value);
        return true;
    }

    /// <summary>
    /// <c>/bank [player] [set|add|take &lt;amount&gt;]</c>, split: who (null: the sender, or the one
    /// offline account), and the change (null set and 0 add: just read it).
    /// </summary>
    public static bool TryBank(string[] args, out string? who, out long? set, out long add, out string error)
    {
        who = null;
        set = null;
        add = 0;
        error = "Usage: /bank [player] [set|add|take <amount>]";
        int i = 0;
        if (i < args.Length && !IsBankVerb(args[i])) who = args[i++];
        if (i == args.Length) return true;
        if (i + 2 != args.Length || !TryAmount(args[i + 1], out long amount)) return false;
        switch (args[i].ToLowerInvariant())
        {
            case "set": set = amount; break;
            case "add": add = amount; break;
            case "take": add = -amount; break;
            default: return false;
        }
        return true;
    }

    private static bool IsBankVerb(string word) => word.ToLowerInvariant() is "set" or "add" or "take";

    /// <summary><c>/despawn</c>'s radius when none is typed, m (#1200).</summary>
    public const float DespawnRadius = 30f;

    /// <summary>The widest <c>/despawn</c> radius, m; past it, <c>everywhere</c>.</summary>
    public const float MaxDespawnRadius = 5000f;

    public const string DespawnUsage = "Usage: /despawn <vehicles | items | all> [radius m | everywhere]";

    /// <summary>
    /// <c>/despawn &lt;vehicles | items | all&gt; [radius m | everywhere]</c> (#1200): what to take out of
    /// the world, and within how many metres of the asker (null: everywhere).
    /// </summary>
    public static bool TryDespawn(string[] args, out bool vehicles, out bool items, out float? radius)
    {
        vehicles = items = false;
        radius = DespawnRadius;
        if (args.Length is 0 or > 2) return false;
        string what = args[0].ToLowerInvariant();
        vehicles = what is "vehicles" or "vehicle" or "all";
        items = what is "items" or "item" or "all";
        if (!vehicles && !items) return false;
        if (args.Length == 1) return true;
        if (args[1].ToLowerInvariant() == "everywhere") { radius = null; return true; }
        if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) || !(r > 0) || r > MaxDespawnRadius)
            return false;
        radius = r;
        return true;
    }

    /// <summary>
    /// <c>/tp &lt;E&gt; &lt;N&gt;</c> (#994): an LV95 point, "2600000 1200000" or "2600000,1200000" (what
    /// the F3 overlay and map.geo.admin.ch show, apostrophes allowed). Only points round Switzerland,
    /// so a player name made of digits or a swapped pair is not a jump into nowhere.
    /// </summary>
    public static bool TryLv95(string text, out double e, out double n)
    {
        e = n = 0;
        string[] words = text.Replace("'", "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 2
            && double.TryParse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture, out e)
            && double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out n)
            && e is >= 2_400_000 and <= 2_900_000
            && n is >= 1_000_000 and <= 1_350_000;
    }
}
