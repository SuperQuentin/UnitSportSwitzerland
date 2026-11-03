// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-framework.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>How a festival ends: the closing show of its last real day (part 2 of #1269 builds the shows).</summary>
public enum ClosingShow { None, Fireworks, Concert, Both, Bonfires }

/// <summary>A place a festival is held: a real one first, fallbacks after it (<see cref="FestivalSites"/>).</summary>
public sealed record FestivalSite(string Name, double E, double N, double RadiusM, string? PoiId = null);

/// <summary>One game of a festival: its Batzen are capped per day (<see cref="DayCap"/>) so no game can be farmed.</summary>
public sealed record FestivalGame(string Id, string Title, int DayCap = 100);

/// <summary>From day <see cref="From"/> (1 = the window's first date) these games are open, and stay open.</summary>
public sealed record FestivalDay(int From, string[] Opens, string Blurb);

/// <summary>The party score that pays <see cref="Batzen"/> to every member who contributed to it.</summary>
public sealed record FestivalMilestone(int Points, int Batzen, string Title);

/// <summary>
/// A festival as data (#1269 part 1, a catalogue like <c>QuestCatalog</c>): its id is the occasion's id, the dates are the
/// occasion entry's (<see cref="Schedule"/> is only the default an operator may move in <c>occasions.json</c>).
/// </summary>
/// <param name="Relocates">False for a festival that needs its real place (Montreux Jazz's stage, #1394): a region without it does not hold the festival, no town stands in.</param>
/// <param name="TokenCap">The most Batzen a player can earn from games in one festival: 600, about CHF 600 once converted.</param>
public sealed record FestivalDef(
    string Id,
    string Title,
    FestivalSite[] Sites,
    FestivalGame[] Games,
    FestivalDay[] Days,
    ClosingShow Show,
    (string From, string To)[] Schedule,
    FestivalMilestone[] Milestones,
    int TokenCap = FestivalDef.DefaultTokenCap,
    bool Relocates = true)
{
    /// <summary>1 Batzen = CHF 1 (decided in #1270); the cap makes a festival worth about CHF 600 at most from its games.</summary>
    public const int DefaultTokenCap = 600;

    /// <summary>Milestones pay on top of the cap, but together they stay a quarter of it: the festival's worth is bounded.</summary>
    public const double MilestoneShare = 0.25;

    public FestivalGame? Game(string id) => Array.Find(Games, g => g.Id == id);

    /// <summary>The first day <paramref name="game"/> is open, or int.MaxValue for a game this festival does not have.</summary>
    public int OpensOn(string game)
    {
        int first = int.MaxValue;
        foreach (var d in Days)
            if (Array.IndexOf(d.Opens, game) >= 0) first = Math.Min(first, d.From);
        return first;
    }

    /// <summary>Open from its day to the end: a late player meets every game at once.</summary>
    public bool IsOpen(string game, int day) => OpensOn(game) <= day;

    public IEnumerable<FestivalGame> OpenGames(int day) => Games.Where(g => IsOpen(g.Id, day));

    /// <summary>The next day something opens after <paramref name="day"/>, or 0 when nothing does.</summary>
    public int NextOpening(int day)
    {
        int next = int.MaxValue;
        foreach (var d in Days)
            if (d.From > day) next = Math.Min(next, d.From);
        return next == int.MaxValue ? 0 : next;
    }

    /// <summary>The day table's row for <paramref name="day"/>: the latest row that has begun.</summary>
    public FestivalDay? DayRow(int day)
    {
        FestivalDay? row = null;
        foreach (var d in Days)
            if (d.From <= day && (row == null || d.From > row.From)) row = d;
        return row;
    }

    public int MilestoneTotal => Milestones.Sum(m => m.Batzen);

    /// <summary>The festival's length in days, from its default schedule's first window (0 when it cannot be told).</summary>
    public int Length
    {
        get
        {
            if (Schedule.Length == 0) return 0;
            var (from, to) = Schedule[0];
            return FestivalCalendar.SpanOf(from, to, 2027) is { } span ? span.Length : 0;
        }
    }

    /// <summary>What is wrong with this definition, for the tests that walk the catalogue; empty when it is sound.</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        if (Id.Length == 0 || Id != Id.ToLowerInvariant()) p.Add("the id is empty or not lower-case");
        if (Sites.Length == 0) p.Add("no site");
        if (Schedule.Length == 0) p.Add("no default schedule");
        if (Games.Select(g => g.Id).Distinct().Count() != Games.Length) p.Add("a game id twice");
        foreach (var d in Days)
            foreach (string id in d.Opens)
                if (Game(id) == null) p.Add($"day {d.From} opens '{id}', which is not a game");
        foreach (var g in Games)
            if (OpensOn(g.Id) == int.MaxValue) p.Add($"'{g.Id}' never opens");
        for (int i = 1; i < Days.Length; i++)
            if (Days[i].From <= Days[i - 1].From) p.Add("the days are not in order");
        if (Days.Length > 0 && Days[0].From != 1) p.Add("nothing is open on day 1");
        for (int i = 1; i < Milestones.Length; i++)
            if (Milestones[i].Points <= Milestones[i - 1].Points) p.Add("the milestones are not in order");
        if (TokenCap is <= 0 or > 1000) p.Add("the token cap is out of range");
        if (MilestoneTotal > TokenCap * MilestoneShare) p.Add($"the milestones pay {MilestoneTotal}, over a quarter of the cap");
        // the games alone must be able to reach the cap, else it is not the cap
        long reachable = 0;
        foreach (var g in Games) reachable += (long)g.DayCap * Math.Max(0, Length - OpensOn(g.Id) + 1);
        if (Length > 0 && reachable < TokenCap) p.Add("the games cannot earn up to the cap in this many days");
        return p;
    }
}
