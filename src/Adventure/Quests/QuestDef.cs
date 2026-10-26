using System;
using System.Collections.Generic;
using UnitSport.Items;

namespace UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/quests.md).

/// <summary>How a quest starts: picked in the log, by itself on the event of its first step, or handed out by a source.</summary>
public enum QuestStart { FromLog, OnEvent, Given }

/// <summary>
/// A quest (#1223): data, never code. Its steps run in order and listen to the adventure events the
/// other systems raise; its rewards are applied once, by the server, when the last step is done.
/// </summary>
public sealed record QuestDef(string Id, string Title, string Blurb, Step[] Steps, Reward[] Rewards)
{
    /// <summary>Quest ids done first.</summary>
    public string[] Requires { get; init; } = [];
    /// <summary>The canton it belongs to, for the log's grouping and the Atlas.</summary>
    public string? Canton { get; init; }
    public QuestStart Start { get; init; } = QuestStart.FromLog;
    public bool Repeatable { get; init; }
    /// <summary>The source it comes from: <c>catalog</c>, <c>postkarte</c>...</summary>
    public string Source { get; init; } = "catalog";
}

/// <summary>Where a step points: a POI of the places catalogue, or an area of the quest's own (LV95, metres).</summary>
public abstract record TargetRef(string Id);

/// <summary>A point of interest (places plan); its position comes from <see cref="QuestEngine.ResolvePoi"/>.</summary>
public sealed record PoiTarget(string PoiId) : TargetRef(PoiId);

/// <summary>An area of the quest's own: a disc round an LV95 point.</summary>
public sealed record AreaTarget(string Name, double E, double N, float RadiusM) : TargetRef("area:" + Name);

/// <summary>
/// One step of a quest. <see cref="Matches"/> says whether an event counts for it; <see cref="Amount"/>
/// how much (an item step counts the items); <see cref="Distinct"/> an id counted once (a POI found
/// twice is one). A plan adds a step kind as a new record in its own file.
/// </summary>
public abstract record Step
{
    /// <summary>How many to do (POIs, items, travel points...).</summary>
    public int Count { get; init; } = 1;
    /// <summary>The tracker's line; a default from the step when empty.</summary>
    public string Text { get; init; } = "";
    /// <summary>Shown in the log before the step starts (later steps are hidden otherwise).</summary>
    public bool Reveal { get; init; }

    public virtual TargetRef? Target => null;

    /// <summary>The Adventure plan whose system raises this step's events; null when this plan's own service does.</summary>
    public virtual string? Plan => null;

    public abstract bool Matches(AdventureEvent e);
    public virtual int Amount(AdventureEvent e) => Matches(e) ? 1 : 0;
    public virtual string? Distinct(AdventureEvent e) => null;
    public abstract string Describe();
    public string Line => Text.Length > 0 ? Text : Describe();

    protected static bool Is(string? filter, string value) => filter == null || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Be there: within the target's radius (the server's 2 Hz check), or the POI discovered.</summary>
public sealed record ReachStep(TargetRef At) : Step
{
    public override TargetRef? Target => At;
    public override string? Plan => At is PoiTarget ? "places" : null;
    public override bool Matches(AdventureEvent e) => e is PoiDiscovered d && At is PoiTarget p && d.PoiId == p.PoiId;
    public override string Describe() => At is AreaTarget a ? $"Reach {a.Name}" : $"Reach {At.Id}";
}

/// <summary>Discover POIs: distinct ones, of a kind and canton when given.</summary>
public sealed record DiscoverStep(string? Kind = null, string? Canton = null) : Step
{
    public override string? Plan => "places";
    public override bool Matches(AdventureEvent e) => e is PoiDiscovered d && Is(Kind, d.Kind) && Is(Canton, d.Canton);
    public override string? Distinct(AdventureEvent e) => (e as PoiDiscovered)?.PoiId;
    public override string Describe() => $"Discover {Count} {Kind ?? "place"}{(Count == 1 ? "" : "s")}{(Canton != null ? " in " + Canton : "")}";
}

/// <summary>Collect collectibles of a kind, or items given by a server-checked source.</summary>
public sealed record CollectStep(string? Kind = null, ItemId? Item = null, string? Canton = null) : Step
{
    public override string? Plan => "rewards";
    public override bool Matches(AdventureEvent e) => e switch
    {
        CollectibleTaken c => Item == null && Is(Kind, c.Kind) && Is(Canton, c.Canton),
        ItemObtained o => Item == o.Item,
        _ => false,
    };
    public override int Amount(AdventureEvent e) => !Matches(e) ? 0 : e is ItemObtained o ? Math.Max(1, o.Count) : 1;
    public override string? Distinct(AdventureEvent e) => (e as CollectibleTaken)?.CollectibleId;
    public override string Describe() => $"Collect {Count} {(Item is { } i ? i.ToString() : Kind ?? "collectible")}";
}

/// <summary>Hand items over at a target: the client asks, the server checks where (<c>AdventureService.Deliver</c>).</summary>
public sealed record DeliverStep(ItemId Item, TargetRef At) : Step
{
    public override TargetRef? Target => At;
    public override string? Plan => At is PoiTarget ? "places" : null;
    public override bool Matches(AdventureEvent e) => e is ItemDelivered d && d.Item == Item && d.TargetId == At.Id;
    public override int Amount(AdventureEvent e) => Matches(e) ? Math.Max(1, ((ItemDelivered)e).Count) : 0;
    public override string Describe() => $"Hand over {Count} {Item} at {(At is AreaTarget a ? a.Name : At.Id)}";
}

/// <summary>Open a chest, of a tier or at a POI when given (rewards plan).</summary>
public sealed record OpenChestStep(int? Tier = null, string? PoiId = null) : Step
{
    public override string? Plan => "rewards";
    public override bool Matches(AdventureEvent e) => e is ChestOpened c && (Tier == null || c.Tier >= Tier) && (PoiId == null || c.PoiId == PoiId);
    public override string? Distinct(AdventureEvent e) => (e as ChestOpened)?.ChestId;
    public override string Describe() => PoiId != null ? $"Open the chest at {PoiId}" : "Open a chest";
}

/// <summary>Finish a timed trial, under a time or with a medal when given (rewards plan).</summary>
public sealed record TrialStep(string TrialId, double? MaxSeconds = null, int? Medal = null) : Step
{
    public override string? Plan => "rewards";
    public override bool Matches(AdventureEvent e) => e is TrialFinished t && t.TrialId == TrialId
        && (MaxSeconds == null || t.Seconds <= MaxSeconds) && (Medal == null || t.Medal >= Medal);
    public override string Describe() => MaxSeconds is { } s ? $"Finish {TrialId} under {s:0} s" : $"Finish {TrialId}";
}

/// <summary>Photograph a target (rewards plan).</summary>
public sealed record PhotoStep(string TargetId) : Step
{
    public override string? Plan => "rewards";
    public override bool Matches(AdventureEvent e) => e is PhotoTaken p && p.TargetId == TargetId;
    public override string Describe() => $"Photograph {TargetId}";
}

/// <summary>Use an interactable (a stall, a sign; raised by its own server method, places plan).</summary>
public sealed record InteractStep(string TargetId) : Step
{
    public override string? Plan => "places";
    public override bool Matches(AdventureEvent e) => e is Interacted i && i.TargetId == TargetId;
    public override string Describe() => $"Visit {TargetId}";
}

/// <summary>Unlock travel points: of a kind, or one point, when given (navigation plan). Distinct points.</summary>
public sealed record UnlockStep(string? PointKind = null, string? PointId = null) : Step
{
    public override string? Plan => "navigation";
    public override bool Matches(AdventureEvent e) => e is TravelUnlocked t && Is(PointKind, t.Kind) && (PointId == null || t.PointId == PointId);
    public override string? Distinct(AdventureEvent e) => (e as TravelUnlocked)?.PointId;
    public override string Describe() => PointId != null ? $"Unlock {PointId}" : $"Unlock {Count} travel point{(Count == 1 ? "" : "s")}";
}

/// <summary>Finish another quest (chains).</summary>
public sealed record FinishStep(string QuestId) : Step
{
    public override bool Matches(AdventureEvent e) => e is QuestCompleted q && q.QuestId == QuestId;
    public override string Describe() => $"Complete {QuestId}";
}

/// <summary>What a finished quest gives, applied once by the server.</summary>
public abstract record Reward
{
    public abstract string Describe();
}

/// <summary>Francs to the bank account (not the pocket, which a knock-out empties).</summary>
public sealed record Cash(int Chf) : Reward
{
    public override string Describe() => $"CHF {Chf}";
}

/// <summary>Items to the player's pack (what does not fit is dropped at its feet).</summary>
public sealed record Item(ItemId Id, int Count = 1) : Reward
{
    public override string Describe() => Count == 1 ? Id.ToString() : $"{Count} x {Id}";
}

/// <summary>A title the player may wear.</summary>
public sealed record Title(string Name) : Reward
{
    public override string Describe() => $"Title \"{Name}\"";
}

/// <summary>Anything another plan grants: <c>"reveal"</c> (places), <c>"travel"</c> (navigation), <c>"stamina"</c> (stamina).</summary>
public sealed record Unlock(string Kind, string Arg) : Reward
{
    public override string Describe() => Kind switch
    {
        "reveal" => $"The map round {Arg}",
        "travel" => $"Travel to {Arg}",
        "stamina" => "More stamina",
        _ => $"{Kind} {Arg}",
    };
}

/// <summary>A quest source: the catalog, later the Postkarte errands generated per day.</summary>
public interface IQuestSource
{
    string Id { get; }
    /// <summary>The quests this source offers the player now.</summary>
    IEnumerable<QuestDef> For(QuestContext ctx);
    /// <summary>A quest by id (a generated one rebuilt from its id and seed), or null when not this source's.</summary>
    QuestDef? Resolve(string questId);
}

/// <summary>What a source may base its offer on: the player's quests and where it is (LV95; NaN when unknown).</summary>
public sealed record QuestContext(QuestsProgress Progress, double E = double.NaN, double N = double.NaN, string? Canton = null);
