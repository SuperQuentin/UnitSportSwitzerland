using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Items;

namespace UnitSport.Adventure.Versteckis;

// Plain C#, no Godot: linked into the unit tests (VersteckisTests; docs/notes/adventure/versteckis.md).

/// <summary>
/// A player's Versteckis record (#1268, plan section 5), the <c>versteckis</c> section of its <c>AdventureBook</c>:
/// games and rounds played, catches, rounds survived (and as which prop), objectives, Moos, the cantons played in,
/// the longest hide, and today's paid games (the francs count for five a day; the progress always counts).
/// </summary>
public sealed class VersteckisProgress
{
    public static readonly SectionKey<VersteckisProgress> Section = new("versteckis", Version: 1);

    public int Games { get; set; }
    public int Rounds { get; set; }
    public int SeekRounds { get; set; }
    public int Catches { get; set; }
    public int Survived { get; set; }
    public int[] ByProp { get; set; } = new int[VsProps.Count];
    public int Bells { get; set; }
    public int Fires { get; set; }
    public int Cans { get; set; }
    public int Moos { get; set; }
    public List<string> Cantons { get; set; } = new();
    public int BestHideSeconds { get; set; }
    /// <summary>The day (UTC, yyyy-MM-dd) the paid count is for, and the games paid that day.</summary>
    public string Day { get; set; } = "";
    public int DayGames { get; set; }

    /// <summary>Games paid on <paramref name="day"/> so far (a new day starts at 0).</summary>
    public int PaidOn(string day) => Day == day ? DayGames : 0;

    public int SurvivedAs(VsProp kind) => kind is > VsProp.None and <= VsProp.FlowerBox && ByProp.Length >= (int)kind ? ByProp[(int)kind - 1] : 0;
}

/// <summary>What one game added to a player (the server's tally of its entrant, folded in at the end).</summary>
public readonly record struct VsGameTally(int Rounds, int SeekRounds, int Catches, int Survived, int[] SurvivedAs, int Bells, int Fires,
    int Cans, int Moos, int BestHide, string Canton);

/// <summary>The titles and the two cosmetics (#1268), each earned by playing, never by winning.</summary>
public static class VersteckisRewards
{
    public const string Dorfchind = "Dorfchind", Spuernase = "Spürnase", Gartenzwerg = "Gartenzwerg", Sigrist = "Sigrist",
        Fuurwaechter = "Füürwächter", Dorftour = "Dorftour";
    public const int CatchesForSpuernase = 25, GnomeRoundsForGartenzwerg = 15, BellsForSigrist = 10, FiresForFuurwaechter = 10,
        CantonsForDorftour = 5, GamesForZipfelmuetze = 10, SurvivedForTreichel = 30;

    /// <summary>The titles a record holds.</summary>
    public static IEnumerable<string> Titles(VersteckisProgress p)
    {
        if (p.Games >= 1) yield return Dorfchind;
        if (p.Catches >= CatchesForSpuernase) yield return Spuernase;
        if (p.SurvivedAs(VsProp.Gnome) >= GnomeRoundsForGartenzwerg) yield return Gartenzwerg;
        if (p.Bells >= BellsForSigrist) yield return Sigrist;
        if (p.Fires >= FiresForFuurwaechter) yield return Fuurwaechter;
        if (p.Cantons.Count >= CantonsForDorftour) yield return Dorftour;
    }

    /// <summary>The cosmetics a record has earned.</summary>
    public static IEnumerable<ItemId> Cosmetics(VersteckisProgress p)
    {
        if (p.Games >= GamesForZipfelmuetze) yield return ItemId.Zipfelmuetze;
        if (p.Survived >= SurvivedForTreichel) yield return ItemId.Treichel;
    }

    /// <summary>
    /// Folds a game into a record: the tallies, the canton, the day's paid count. Returns the francs it pays
    /// (<see cref="VsRules.Payout"/>, 0 past five games today) and what it newly earned (titles, cosmetics).
    /// </summary>
    public static (int Francs, List<string> Titles, List<ItemId> Items) Fold(VersteckisProgress p, VsGameTally t, bool top, string day)
    {
        var titlesBefore = Titles(p).ToHashSet();
        var itemsBefore = Cosmetics(p).ToHashSet();
        int paid = p.PaidOn(day);
        int francs = VsRules.Payout(t.Rounds, top, paid);
        if (t.Rounds > 0)
        {
            p.Games++;
            if (p.Day != day) { p.Day = day; p.DayGames = 0; }
            if (francs > 0) p.DayGames++;
        }
        p.Rounds += t.Rounds;
        p.SeekRounds += t.SeekRounds;
        p.Catches += t.Catches;
        p.Survived += t.Survived;
        if (p.ByProp.Length < VsProps.Count)
        {
            var grown = p.ByProp;
            Array.Resize(ref grown, VsProps.Count);
            p.ByProp = grown;
        }
        for (int i = 0; i < Math.Min(VsProps.Count, t.SurvivedAs.Length); i++) p.ByProp[i] += t.SurvivedAs[i];
        p.Bells += t.Bells;
        p.Fires += t.Fires;
        p.Cans += t.Cans;
        p.Moos += t.Moos;
        p.BestHideSeconds = Math.Max(p.BestHideSeconds, t.BestHide);
        if (t.Canton.Length > 0 && t.Rounds > 0 && !p.Cantons.Contains(t.Canton)) p.Cantons.Add(t.Canton);
        return (francs, Titles(p).Where(x => !titlesBefore.Contains(x)).ToList(), Cosmetics(p).Where(x => !itemsBefore.Contains(x)).ToList());
    }
}

/// <summary>A game of Versteckis played to its end or left (raised at <c>Release</c>, once the player is out of the round).</summary>
public sealed record VersteckisPlayed(long Peer, string Key, int Rounds, int Score, bool Top, string Canton) : AdventureEvent(Peer, Key);
/// <summary>Hiders caught as the seeker, in one game.</summary>
public sealed record VersteckisCaught(long Peer, string Key, int Count) : AdventureEvent(Peer, Key);
/// <summary>An objective done (<c>bell</c>, <c>fire</c>, <c>can</c>).</summary>
public sealed record VersteckisObjective(long Peer, string Key, string Kind) : AdventureEvent(Peer, Key);

/// <summary>Play games of Versteckis (the "Versteckis im Dorf" quest, the errands' hook).</summary>
public sealed record VersteckisStep : Step
{
    public override string? Plan => "versteckis";
    public override bool Matches(AdventureEvent e) => e is VersteckisPlayed { Rounds: > 0 };
    public override string Describe() => Count == 1 ? "Play a game of Versteckis in a village" : $"Play {Count} games of Versteckis";
}
