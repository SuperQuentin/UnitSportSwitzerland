using System.Globalization;

namespace UnitSport.Net;

/// <summary>
/// Parsing for the money admin commands (<c>/money</c>, <c>/bank</c>, #262). Pure, no Godot, so the
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
}
