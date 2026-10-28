using System;
using System.Collections.Generic;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md).

/// <summary>
/// The <c>errands</c> section (#1267): the cards taken on a Swiss day (three at most), the reputation points per canton,
/// the cards done with a friend, the cards whose friend bonus was paid, and the flags earned at level 3.
/// </summary>
public sealed class ErrandsProgress
{
    public static readonly SectionKey<ErrandsProgress> Section = new("errands", Version: 1);

    /// <summary>The Swiss day (yyyymmdd) <see cref="Taken"/> counts for.</summary>
    public int Day { get; set; }
    public List<string> Taken { get; set; } = new();
    /// <summary>Canton -> reputation points.</summary>
    public Dictionary<string, int> Rep { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Cards done together, for the title.</summary>
    public int Together { get; set; }
    /// <summary>Cards done (any way), for the strip.</summary>
    public int Done { get; set; }
    /// <summary>Card ids whose friend bonus was paid (pruned with the quests' 30-day <c>Done</c>).</summary>
    public List<string> BonusPaid { get; set; } = new();
    /// <summary>Canton flags earned (level 3) and the one worn ("" = Swiss).</summary>
    public List<string> Flags { get; set; } = new();
    public string Flag { get; set; } = "";
    /// <summary>World events helped, by <see cref="WorldEventKind"/> name (#1339): five of a kind earn its title.</summary>
    public Dictionary<string, int> Events { get; set; } = new(StringComparer.Ordinal);

    public const int DailyCards = 3;
    public const int TogetherTitleAt = 10;
    public const string TogetherTitle = "Gemeinsam unterwegs";

    /// <summary>A new Swiss day: the day's taken cards start again. True when it rolled.</summary>
    public bool Roll(int today)
    {
        if (Day == today) return false;
        Day = today;
        Taken.Clear();
        return true;
    }

    public bool CanTake(int today) => (Day == today ? Taken.Count : 0) < DailyCards;

    public int Points(string canton) => Rep.GetValueOrDefault(canton);

    public int Level(string canton) => Reputation.Level(Points(canton));

    /// <summary>Drops bonus records of cards older than <paramref name="keepFrom"/> (a day).</summary>
    public void Prune(int keepFrom) => BonusPaid.RemoveAll(id => Postkarte.DayOf(id) is { } d && d < keepFrom);
}

/// <summary>Canton reputation (#1267 part 7): four levels, computed from the points, never stored.</summary>
public static class Reputation
{
    public static readonly int[] Needed = { 10, 30, 60, 100 };
    public static readonly string[] Names = { "Unbekannt", "Bekannt", "Geschätzt", "Einheimisch", "Ehrenbürger" };

    public static int Level(int points)
    {
        int level = 0;
        while (level < Needed.Length && points >= Needed[level]) level++;
        return level;
    }

    public static string Name(int level) => Names[Math.Clamp(level, 0, Names.Length - 1)];

    /// <summary>Points to the next level, or null at the top.</summary>
    public static int? NextAt(int points) => Level(points) is var l && l < Needed.Length ? Needed[l] : null;

    /// <summary>The title of the top level in a canton.</summary>
    public static string TitleOf(string canton) => "Ehrenbürger von " + Places.AtlasChapters.NameOf(canton);

    /// <summary>The "rep" reward argument: <c>VS:+5</c>. Null when malformed.</summary>
    public static (string Canton, int Points)? Parse(string arg)
    {
        int colon = arg.IndexOf(':');
        if (colon != 2 || !int.TryParse(arg.AsSpan(colon + 1), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int pts) || pts <= 0 || pts > 1000) return null;
        return (arg[..2], pts);
    }
}
