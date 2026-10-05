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
    public float Damage { get; set; }
    /// <summary>Best placing ever, 1 = a win; 0 before the first match.</summary>
    public int BestPlace { get; set; }
    public double Survived { get; set; }

    /// <summary>"12 matches · 3 wins · 41 kills · best #1": the record in one line.</summary>
    public string Line() => FormattableString.Invariant(
        $"{Matches} {(Matches == 1 ? "match" : "matches")} · {Wins} {(Wins == 1 ? "win" : "wins")} · {Kills} {(Kills == 1 ? "kill" : "kills")} · top 5 ×{TopFive} · best #{BestPlace}");
}

/// <summary>
/// Every player's Battle Royale record (#479), kept by the server (<c>user://br/stats.json</c>): what each
/// finished match adds (<see cref="Add"/>), and the leaderboard. Players are known by display name,
/// case-insensitive. Pure.
/// </summary>
public sealed class BrStats
{
    public Dictionary<string, BrRecord> Players { get; set; } = new();

    private static string Key(string name) => name.Trim().ToLowerInvariant();

    public BrRecord? Find(string name) => Players.GetValueOrDefault(Key(name));

    /// <summary>One finished match, entrant by entrant: name, final place (1 = won), kills, damage dealt, seconds survived.</summary>
    public void Add(IEnumerable<(string Name, int Place, int Kills, float Damage, double Survived)> entrants)
    {
        foreach (var (name, place, kills, damage, survived) in entrants)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string key = Key(name);
            if (!Players.TryGetValue(key, out var r)) Players[key] = r = new BrRecord();
            r.Name = name.Trim();   // the latest spelling
            r.Matches++;
            if (place == 1) r.Wins++;
            if (place is >= 1 and <= 5) r.TopFive++;
            if (place >= 1 && (r.BestPlace == 0 || place < r.BestPlace)) r.BestPlace = place;
            r.Kills += Math.Max(0, kills);
            r.Damage += Math.Max(0f, damage);
            r.Survived += Math.Max(0, survived);
        }
    }

    /// <summary>The best <paramref name="n"/>: by wins, then kills, then fewest matches (the sharper player), then name.</summary>
    public IEnumerable<BrRecord> Top(int n) => Players.Values
        .OrderByDescending(r => r.Wins).ThenByDescending(r => r.Kills).ThenBy(r => r.Matches)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(n);

    /// <summary>"1. Anna — 3 wins, 41 kills": the leaderboard as lines.</summary>
    public IEnumerable<string> Board(int n) => Top(n).Select((r, i) => string.Create(CultureInfo.InvariantCulture,
        $"{i + 1}. {r.Name} — {r.Wins} {(r.Wins == 1 ? "win" : "wins")}, {r.Kills} {(r.Kills == 1 ? "kill" : "kills")}"));
}
