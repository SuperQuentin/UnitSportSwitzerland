using UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-framework.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>What one player has of one festival instance (<c>"test-2027"</c>): the tokens, the score, what they did.</summary>
public sealed class FestivalInstance
{
    public string Festival { get; set; } = "";

    /// <summary>Batzen held now. Converted to francs once the instance is over; 0 after.</summary>
    public int Batzen { get; set; }

    /// <summary>Batzen earned from games, spent or not: what the festival's cap counts.</summary>
    public int Earned { get; set; }

    /// <summary>This player's own points; the party's score is the board's.</summary>
    public int Points { get; set; }

    /// <summary>The best score of one play, per game.</summary>
    public Dictionary<string, int> Best { get; set; } = new();

    /// <summary>Plays, per <c>"game:day"</c>.</summary>
    public Dictionary<string, int> Played { get; set; } = new();

    /// <summary>Batzen paid, per <c>"game:day"</c>: what a game's daily cap counts.</summary>
    public Dictionary<string, int> Paid { get; set; } = new();

    /// <summary>The party milestones (by index in the festival's list) that paid this player.</summary>
    public List<int> Milestones { get; set; } = new();

    /// <summary>Paid into the account (1 Batzen = CHF 1). Once.</summary>
    public bool Converted { get; set; }

    public int ConvertedChf { get; set; }
}

/// <summary>
/// The <c>festivals</c> section of a player's progress (#1269 section 1.3): one <see cref="FestivalInstance"/> per
/// festival instance, kept for past ones as a record. Batzen are held here, not as items: the section is the server's.
/// </summary>
public sealed class FestivalBook
{
    public static readonly SectionKey<FestivalBook> Section = new("festivals", Version: 1);

    public Dictionary<string, FestivalInstance> Instances { get; set; } = new();

    /// <summary>This player's record of an instance, made on first use.</summary>
    public FestivalInstance Of(string instance, string festival)
    {
        if (!Instances.TryGetValue(instance, out var i)) Instances[instance] = i = new FestivalInstance { Festival = festival };
        return i;
    }
}

/// <summary>The rules on a <see cref="FestivalInstance"/>: what a play may earn, the milestone pay, the conversion. Pure.</summary>
public static class FestivalRules
{
    public static string PlayKey(string game, int day) => $"{game}:{day}";

    /// <summary>
    /// A play ended: <paramref name="batzen"/> and <paramref name="points"/> earned in <paramref name="game"/> on
    /// <paramref name="day"/>. The tokens are cut to the game's daily cap and the festival's cap; the points are not.
    /// Returns the Batzen actually paid.
    /// </summary>
    public static int Earn(FestivalDef def, FestivalInstance inst, string game, int day, int batzen, int points)
    {
        string key = PlayKey(game, day);
        int room = def.TokenCap - inst.Earned;
        if (def.Game(game) is { } g) room = Math.Min(room, g.DayCap - inst.Paid.GetValueOrDefault(key));
        int granted = Math.Clamp(Math.Min(batzen, room), 0, int.MaxValue);
        inst.Played[key] = inst.Played.GetValueOrDefault(key) + 1;
        inst.Paid[key] = inst.Paid.GetValueOrDefault(key) + granted;
        inst.Earned += granted;
        inst.Batzen += granted;
        if (points > 0)
        {
            inst.Points += points;
            inst.Best[game] = Math.Max(inst.Best.GetValueOrDefault(game), points);
        }
        return granted;
    }

    /// <summary>
    /// Spends <paramref name="batzen"/> held (a stall's ware, #1348): false when the player holds fewer. <see cref="FestivalInstance.Earned"/> is not
    /// touched, so what the cap counts stays what the games paid.
    /// </summary>
    public static bool Spend(FestivalInstance inst, int batzen)
    {
        if (batzen < 0 || inst.Batzen < batzen) return false;
        inst.Batzen -= batzen;
        return true;
    }

    /// <summary>
    /// A party milestone pays this player: outside the cap, which counts what games earn, and once (the index is
    /// remembered). Returns false when it already paid.
    /// </summary>
    public static bool PayMilestone(FestivalInstance inst, int index, int batzen)
    {
        if (inst.Milestones.Contains(index)) return false;
        inst.Milestones.Add(index);
        inst.Batzen += batzen;
        return true;
    }

    /// <summary>
    /// Pays out every instance <paramref name="ended"/> says is over and not paid yet, at 1 Batzen = CHF 1: the instance is
    /// marked and its Batzen set to 0, so it can never pay twice. Returns what to credit, per instance.
    /// </summary>
    public static List<(string Instance, string Festival, int Chf)> Convert(FestivalBook book, Func<string, bool> ended)
    {
        var paid = new List<(string, string, int)>();
        foreach (var (key, inst) in book.Instances)
        {
            if (inst.Converted || !ended(key)) continue;
            inst.Converted = true;
            inst.ConvertedChf += inst.Batzen;   // a milestone paid late converts again: the record adds up
            paid.Add((key, inst.Festival, inst.Batzen));
            inst.Batzen = 0;
        }
        return paid;
    }

    /// <summary>The Batzen waiting for the end of the festivals not over yet, and so not converted (the Journal's "CHF x after the show").</summary>
    public static int Waiting(FestivalBook book) => book.Instances.Values.Where(i => !i.Converted).Sum(i => i.Batzen);
}
