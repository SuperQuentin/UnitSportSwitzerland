using System;
using System.Collections.Generic;
using UnitSport.Adventure.Places;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>Edelweiss offered at a summit cross or a chapel (rewards plan 4): <see cref="Offered"/> is the player's total after it.</summary>
public sealed record EdelweissOffered(long Peer, string Key, string ShrineId, int Offered) : AdventureEvent(Peer, Key);

/// <summary>
/// The <c>collect</c> section (#1227): the edelweiss and cairns this player took (ids; never inventory items,
/// so nothing to drop, lose on a knock-out or trade), the edelweiss held and offered, the cairns finished per
/// canton, and the fauna photographed.
/// </summary>
public sealed class CollectProgress
{
    public static readonly SectionKey<CollectProgress> Section = new("collect", Version: 1);

    /// <summary>Edelweiss and cairn ids taken.</summary>
    public HashSet<string> Taken { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Edelweiss picked and not offered yet.</summary>
    public int Held { get; set; }
    /// <summary>Edelweiss offered, all time (past the stamina cap too).</summary>
    public int Offered { get; set; }
    /// <summary>Edelweiss picked, all time.</summary>
    public int Picked { get; set; }
    /// <summary>Cairn stones added per canton.</summary>
    public Dictionary<string, int> Cairns { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Animals photographed: name -> count (the bird journal's Fauna page).</summary>
    public Dictionary<string, int> Fauna { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The offering's maths: five edelweiss make one stamina step, ten steps at most (decided Oct 2026).</summary>
public static class Offering
{
    public const int PerStep = 5;
    /// <summary>The stamina steps edelweiss can buy; offerings past it still count in <see cref="CollectProgress.Offered"/>.</summary>
    public const int MaxSteps = 10;
    /// <summary>Every this many steps an offering also gives a Precious roll.</summary>
    public const int PreciousEvery = 5;

    /// <summary>A POI one offers at: a summit cross (a crossed peak too) or a chapel (the places plan's kind tags <c>cross</c>, <c>chapel</c>).</summary>
    public static bool IsShrine(Poi poi) => poi.Kind is PoiKind.SummitCross or PoiKind.Chapel || poi.Kind == PoiKind.Peak && poi.Is(PoiFlags.Cross);

    /// <summary>
    /// Offers every whole five held: moves them from held to offered. Returns the steps offered and, for each,
    /// whether it raises stamina (within <see cref="MaxSteps"/>) and whether it gives a Precious roll.
    /// </summary>
    public static List<(int Step, bool Stamina, bool Precious)> Offer(CollectProgress c)
    {
        var steps = new List<(int, bool, bool)>();
        while (c.Held >= PerStep)
        {
            c.Held -= PerStep;
            c.Offered += PerStep;
            int step = c.Offered / PerStep;
            steps.Add((step, step <= MaxSteps, step % PreciousEvery == 0));
        }
        return steps;
    }

    /// <summary>The stamina steps offered so far (capped).</summary>
    public static int StaminaSteps(CollectProgress c) => Math.Min(MaxSteps, c.Offered / PerStep);
}

/// <summary>
/// Cairns (Steinmänner, rewards plan 4b): twenty per canton, at the canton's walking places below 2500 m (passes,
/// peaks, crosses, huts), the same twenty on every peer (by hash). A player adds a stone to each; the twentieth
/// finishes the canton.
/// </summary>
public static class Cairns
{
    public const int PerCanton = 20;
    public const double MaxAltitude = 2500;

    private static readonly Dictionary<(PoiCatalogue, string), HashSet<string>> Cache = new();

    /// <summary>Whether a POI may carry a cairn at all.</summary>
    public static bool Eligible(Poi p) => p.Kind is PoiKind.Pass or PoiKind.Peak or PoiKind.SummitCross or PoiKind.Hut && p.Elevation < MaxAltitude && p.Canton.Length == 2;

    /// <summary>The ids of the POIs that carry the canton's cairns (at most <see cref="PerCanton"/>).</summary>
    public static HashSet<string> Of(PoiCatalogue catalogue, string canton)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((catalogue, canton), out var set)) return set;
            var picks = new List<(ulong Order, string Id)>();
            foreach (var p in catalogue.InCanton(canton))
                if (Eligible(p)) picks.Add((AdventureHash.Of(p.Id, 0xca1e5UL), p.Id));
            picks.Sort((a, b) => a.Order.CompareTo(b.Order));
            set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < picks.Count && i < PerCanton; i++) set.Add(picks[i].Id);
            Cache[(catalogue, canton)] = set;
            return set;
        }
    }

    /// <summary>The cairn id at a POI.</summary>
    public static string IdOf(Poi p) => RewardRules.CairnPrefix + ":" + p.Id;
}
