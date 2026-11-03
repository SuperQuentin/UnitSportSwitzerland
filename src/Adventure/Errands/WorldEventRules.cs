using System;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/world-events.md).

/// <summary>The four small emergencies (#1339, errands plan 6). The byte goes on the wire.</summary>
public enum WorldEventKind : byte { RunawayCow = 1, LostHiker = 2, Avalanche = 3, ForestFire = 4 }

/// <summary><c>--world-events off|rare|normal</c>: the server's switch. Normal is the default.</summary>
public enum WorldEventMode : byte { Off, Rare, Normal }

/// <summary>What a helper does at an event; the byte goes on the wire.</summary>
public enum EventAct : byte { Talk = 1, DigStart = 2, DigStop = 3, BeatStart = 4, BeatStop = 5 }

/// <summary>
/// The planning rules of the world events (#1339): a roll per 120 s slot and 2 km cell that holds an adventuring player, the
/// season and hour of each kind, the rewards. Pure, so the server and a check agree and a unit test pins the numbers. Nothing
/// here costs a player health: smoke dims the view, a buried hiker is dug out, a cow only walks.
/// </summary>
public static class WorldEventRules
{
    public const double SlotS = 120;
    public const int CellM = 2000;
    public const int OneInNormal = 15, OneInRare = 40;
    public const double CellCooldownS = 900;
    public const int MaxRunning = 6, MaxPerCell = 1;
    /// <summary>A plan goes to the peers this close; one is drawn within <see cref="DrawM"/>.</summary>
    public const float InformM = 3000, DrawM = 2500;
    /// <summary>Whoever did at least one act and stands this close at the end is paid.</summary>
    public const float PaidNearM = 150;
    /// <summary>A helper's body must be this close to what it acts on, plus the slack of a moving body.</summary>
    public const float ActReachM = 4, ActSlackM = 2;
    public const int RepPoints = 5, TitleAt = 5;

    public static WorldEventMode ParseMode(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "off" or "0" or "false" => WorldEventMode.Off,
        "rare" => WorldEventMode.Rare,
        _ => WorldEventMode.Normal,
    };

    public static long Slot(double t) => (long)Math.Floor(t / SlotS);

    public static (int E, int N) Cell(double e, double n) => ((int)Math.Floor(e / CellM), (int)Math.Floor(n / CellM));

    /// <summary>The event of a cell and slot, if any: one slot in 15 (normal) or 40 (rare). The seed makes the kind and the place.</summary>
    public static bool Roll(int cellE, int cellN, long slot, WorldEventMode mode, out uint seed)
    {
        ulong h = ErrandHash.Of(cellE, cellN, slot, 0x57AB);
        seed = (uint)(h >> 32);
        if (mode == WorldEventMode.Off) return false;
        return (int)(h % (ulong)(mode == WorldEventMode.Rare ? OneInRare : OneInNormal)) == 0;
    }

    /// <summary>The kinds in a hash order of the seed: the first one whose anchor passes is the event.</summary>
    public static WorldEventKind[] Order(uint seed)
    {
        var kinds = new[] { WorldEventKind.RunawayCow, WorldEventKind.LostHiker, WorldEventKind.Avalanche, WorldEventKind.ForestFire };
        Array.Sort(kinds, (a, b) => ErrandHash.Of(seed, (long)a).CompareTo(ErrandHash.Of(seed, (long)b)));
        return kinds;
    }

    /// <summary>Whether a kind may come in this month (1-12), at this hour, at this height. A lost hiker is twice as likely at dusk (<see cref="DuskWeight"/>).</summary>
    public static bool InSeason(WorldEventKind kind, int month, double elevation) => kind switch
    {
        WorldEventKind.Avalanche => elevation >= 1500 && (month is 12 or 1 or 2 or 3 or 4 || elevation >= 2600),
        WorldEventKind.ForestFire => month is >= 6 and <= 9 && elevation <= 1800,
        _ => true,
    };

    /// <summary>The weight a kind gets at an hour: the lost hiker doubles between 17 and 21 h.</summary>
    public static int DuskWeight(WorldEventKind kind, double hour) => kind == WorldEventKind.LostHiker && hour is >= 17 and < 21 ? 2 : 1;

    /// <summary>Whether the weight lets a kind through at this seed (a lost hiker passes always at dusk, half the time else).</summary>
    public static bool Passes(WorldEventKind kind, double hour, uint seed) =>
        kind != WorldEventKind.LostHiker || DuskWeight(kind, hour) == 2 || (seed & 1) == 0;

    /// <summary>How long it may run before the world solves it without a reward, seconds.</summary>
    public static double LengthS(WorldEventKind kind) => kind switch
    {
        WorldEventKind.RunawayCow => 480,
        WorldEventKind.LostHiker => 900,
        WorldEventKind.Avalanche => 600,
        _ => 1200,
    };

    public static int Chf(WorldEventKind kind) => kind switch
    {
        WorldEventKind.RunawayCow => 30,
        WorldEventKind.Avalanche => 60,
        _ => 40,
    };

    public static string Name(WorldEventKind kind) => kind switch
    {
        WorldEventKind.RunawayCow => "A cow got loose",
        WorldEventKind.LostHiker => "A hiker is lost",
        WorldEventKind.Avalanche => "An avalanche came down",
        _ => "Smoke over the forest",
    };

    /// <summary>The title earned after <see cref="TitleAt"/> helps of a kind.</summary>
    public static string Title(WorldEventKind kind) => kind switch
    {
        WorldEventKind.RunawayCow => "Kuhflüsterer",
        WorldEventKind.LostHiker => "Bergfreund",
        WorldEventKind.Avalanche => "Lawinenhund",
        _ => "Feuerwehr",
    };

    /// <summary>The line when the world ended it without help.</summary>
    public static string TimeoutLine(WorldEventKind kind) => kind switch
    {
        WorldEventKind.RunawayCow => "The cow found its own way home.",
        WorldEventKind.LostHiker => "A Rega helicopter picked the hiker up.",
        WorldEventKind.Avalanche => "The rescue team arrived.",
        _ => "The fire brigade took over.",
    };

    public static string SuccessLine(WorldEventKind kind) => kind switch
    {
        WorldEventKind.RunawayCow => "The cow is back with its herd.",
        WorldEventKind.LostHiker => "The hiker is safe.",
        WorldEventKind.Avalanche => "Everyone is dug out.",
        _ => "The fire is out.",
    };

    /// <summary>The stable id of the event of a cell and slot (a non-negative int, so the wire and the keys agree).</summary>
    public static int Id(int cellE, int cellN, long slot, WorldEventKind kind) => (int)(ErrandHash.Of(cellE, cellN, slot, (long)kind) & 0x3FFFFFFF);

    /// <summary>The id of the hiker a Postkarte card holds (the same for everyone holding the card).</summary>
    public static int CardId(string cardId) => (int)(ErrandHash.Of(cardId) & 0x3FFFFFFF) | 0x40000000;

    /// <summary>The target id the hiker of a card goes by in the card's steps.</summary>
    public static string HikerEntity(string cardId) => "hiker:" + cardId;

    /// <summary>A point of <paramref name="radius"/> metres from (e, n) on a bearing the unit <paramref name="u"/> (0..1) picks.</summary>
    public static (double E, double N) Offset(double e, double n, double u, double radius) =>
        (e + Math.Sin(u * Math.Tau) * radius, n + Math.Cos(u * Math.Tau) * radius);
}

/// <summary>The progress counts of helps per kind and the titles they earn (kept in <see cref="ErrandsProgress.Events"/>).</summary>
public static class WorldEventTally
{
    /// <summary>Adds a help; true when this one earns the kind's title.</summary>
    public static bool Add(ErrandsProgress p, WorldEventKind kind)
    {
        string key = kind.ToString();
        int n = p.Events.GetValueOrDefault(key) + 1;
        p.Events[key] = n;
        return n == WorldEventRules.TitleAt;
    }
}
