using System;
using System.Collections.Generic;
using UnitSport.Adventure.Navigation;
using UnitSport.Adventure.Places;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md).

/// <summary>
/// The Postkarten as a quest source (#1267 part 4.1): <see cref="For"/> lists the cards of the player's own cell for today (all
/// <see cref="QuestStart.Given"/>, so the log never offers them), <see cref="Resolve"/> rebuilds any card from its id. Offers are
/// computed once per (day, cell) and kept for 64 cells.
/// </summary>
public sealed class PostkarteSource : IQuestSource
{
    public const string SourceId = "postkarte";
    private const int MaxCells = 64;

    private readonly Func<GiverCatalogue> _givers;
    private readonly Func<PoiCatalogue> _pois;
    private readonly Func<TravelRules> _rules;
    private readonly Func<int> _today;
    private readonly Dictionary<(int, int, int), List<Offer>> _cells = new();
    private GiverCatalogue? _seenGivers;
    private PoiCatalogue? _seenPois;
    private TravelRules? _seenRules;

    public PostkarteSource(Func<GiverCatalogue> givers, Func<PoiCatalogue> pois, Func<TravelRules> rules, Func<int> today)
    {
        _givers = givers;
        _pois = pois;
        _rules = rules;
        _today = today;
    }

    public string Id => SourceId;

    /// <summary>The cards of a day and cell (cached); empty when the cell has no givers.</summary>
    public IReadOnlyList<Offer> Cell(int day, int cellE, int cellN)
    {
        var givers = _givers();
        var pois = _pois();
        var rules = _rules();
        if (givers != _seenGivers || pois != _seenPois || rules != _seenRules)
        {
            _cells.Clear();
            (_seenGivers, _seenPois, _seenRules) = (givers, pois, rules);
        }
        var key = (day, cellE, cellN);
        if (_cells.TryGetValue(key, out var offers)) return offers;
        if (_cells.Count >= MaxCells) _cells.Clear();
        return _cells[key] = Postkarte.Offers(day, cellE, cellN, givers, pois, rules);
    }

    /// <summary>The card an id names, or null when it is malformed or this build has no such card.</summary>
    public Offer? Find(string id)
    {
        if (!Postkarte.TryParse(id, out int day, out int cellE, out int cellN, out int slot)) return null;
        var cell = Cell(day, cellE, cellN);
        return slot < cell.Count && cell[slot].Id == id ? cell[slot] : null;
    }

    public IEnumerable<QuestDef> For(QuestContext ctx)
    {
        if (double.IsNaN(ctx.E) || double.IsNaN(ctx.N)) yield break;
        var (ce, cn) = Postkarte.CellOf(ctx.E, ctx.N);
        foreach (var offer in Cell(_today(), ce, cn)) yield return offer.Quest;
    }

    public QuestDef? Resolve(string questId) => Find(questId)?.Quest;
}
