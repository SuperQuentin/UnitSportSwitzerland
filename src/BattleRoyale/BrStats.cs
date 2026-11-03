using System.Globalization;

namespace UnitSport.BattleRoyale;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>One player's Battle Royale career (#479). Serialised as it is: names are the JSON's.</summary>
public sealed class BrRecord
{
    public string Name { get; set; } = "";
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int TopFive { get; set; }
    public int Kills { get; set; }
    /// <summary>How many of <see cref="Kills"/> were bots (#837).</summary>
    public int BotKills { get; set; }
    public float Damage { get; set; }
    /// <summary>Best placing ever, 1 = a win; 0 before the first match.</summary>
    public int BestPlace { get; set; }
    public double Survived { get; set; }

    /// <summary>"12 matches · 3 wins · 41 kills (3 bots) · best #1": the record in one line.</summary>
    public string Line() => FormattableString.Invariant(
        $"{Matches} {(Matches == 1 ? "match" : "matches")} · {Wins} {(Wins == 1 ? "win" : "wins")} · {BrStats.KillText(Kills, BotKills)} · top 5 ×{TopFive} · best #{BestPlace}");
}

/// <summary>
/// Every player's Battle Royale record (#479), kept by the server (<c>user://br/stats.json</c>): what each
/// finished match adds (<see cref="Add"/>), and the leaderboard. Records are keyed by player id (#1359,
/// <see cref="Net.PlayerIds"/>), never by name; <see cref="BrRecord.Name"/> is only a cache of the last name seen, and
/// the callers show the live name through a resolver. A file older than #1359 keys by lower-case name:
/// <see cref="ClaimLegacy"/> moves such a record to its owner's id. Pure.
/// </summary>
public sealed class BrStats
{
    public Dictionary<string, BrRecord> Players { get; set; } = new();

    private static string LegacyKey(string name) => name.Trim().ToLowerInvariant();

    /// <summary>The record kept under this id (or legacy name key).</summary>
    public BrRecord? Find(string id) => Players.GetValueOrDefault(id);

    /// <summary>
    /// The record of whoever is called <paramref name="name"/> now (case-insensitive): <paramref name="nameOf"/> gives an
    /// id's current name. A record still under an old name key matches by its name.
    /// </summary>
    public BrRecord? FindByName(string name, Func<string, string?, string> nameOf)
    {
        string want = name.Trim();
        if (want.Length == 0) return null;
        foreach (var (key, r) in Players)
            if (string.Equals(NameOf(key, r, nameOf), want, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    private static string NameOf(string key, BrRecord r, Func<string, string?, string>? nameOf) =>
        nameOf != null && Net.PlayerIds.IsId(key) ? nameOf(key, r.Name) : r.Name;

    /// <summary>
    /// A peer is known by id and name now: a record still under that name (an old file's) moves to the id, unless the id
    /// has a record already. True when it moved (save then).
    /// </summary>
    public bool ClaimLegacy(string id, string name)
    {
        string old = LegacyKey(name);
        if (old.Length == 0 || Net.PlayerIds.IsId(old) || Players.ContainsKey(id) || !Players.Remove(old, out var r)) return false;
        r.Name = name.Trim();
        Players[id] = r;
        return true;
    }

    /// <summary>"1 kill", "10 kills", "10 kills (3 bots)", "1 kill (1 bot)" (#837): kills of bots count, and show.</summary>
    public static string KillText(int kills, int botKills) => string.Create(CultureInfo.InvariantCulture,
        $"{kills} {(kills == 1 ? "kill" : "kills")}{(botKills > 0 ? $" ({botKills} {(botKills == 1 ? "bot" : "bots")})" : "")}");

    /// <summary>One finished match, entrant by entrant: id, name now (the cache), final place (1 = won), kills, damage dealt, seconds survived.</summary>
    public void Add(IEnumerable<(string Id, string Name, int Place, int Kills, float Damage, double Survived)> entrants) =>
        Add(entrants.Select(e => (e.Id, e.Name, e.Place, e.Kills, 0, e.Damage, e.Survived)));

    /// <summary>The same, with how many of each entrant's kills were bots (#837). Bots themselves are never added.</summary>
    public void Add(IEnumerable<(string Id, string Name, int Place, int Kills, int BotKills, float Damage, double Survived)> entrants)
    {
        foreach (var (id, name, place, kills, botKills, damage, survived) in entrants)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!Players.TryGetValue(id, out var r)) Players[id] = r = new BrRecord();
            if (!string.IsNullOrWhiteSpace(name)) r.Name = name.Trim();   // the cache
            r.Matches++;
            if (place == 1) r.Wins++;
            if (place is >= 1 and <= 5) r.TopFive++;
            if (place >= 1 && (r.BestPlace == 0 || place < r.BestPlace)) r.BestPlace = place;
            r.Kills += Math.Max(0, kills);
            r.BotKills += Math.Clamp(botKills, 0, Math.Max(0, kills));
            r.Damage += Math.Max(0f, damage);
            r.Survived += Math.Max(0, survived);
        }
    }

    /// <summary>The best <paramref name="n"/> (key, record): by wins, then kills, then fewest matches (the sharper player), then name now.</summary>
    public IEnumerable<(string Key, BrRecord Record, string Name)> Top(int n, Func<string, string?, string>? nameOf = null) => Players
        .Select(p => (Key: p.Key, Record: p.Value, Name: NameOf(p.Key, p.Value, nameOf)))
        .OrderByDescending(e => e.Record.Wins).ThenByDescending(e => e.Record.Kills).ThenBy(e => e.Record.Matches)
        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(n);

    /// <summary>"1. Anna — 3 wins, 41 kills (3 bots)": the leaderboard as lines, names as they are now.</summary>
    public IEnumerable<string> Board(int n, Func<string, string?, string>? nameOf = null) => Top(n, nameOf).Select((e, i) => string.Create(CultureInfo.InvariantCulture,
        $"{i + 1}. {e.Name} — {e.Record.Wins} {(e.Record.Wins == 1 ? "win" : "wins")}, {KillText(e.Record.Kills, e.Record.BotKills)}"));
}
