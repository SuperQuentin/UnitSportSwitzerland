using System;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md, world-events.md).

/// <summary>A hiker (or another escorted figure) reached the target area with the player's help (#1339); raised by the world events' server.</summary>
public sealed record Escorted(long Peer, string Key, string EntityId, string TargetId) : AdventureEvent(Peer, Key);

/// <summary>A world event was helped through to its end (#1339); <see cref="Success"/> false when the world solved it first.</summary>
public sealed record WorldEventHelped(long Peer, string Key, int EventId, WorldEventKind Kind, string Canton, bool Success) : AdventureEvent(Peer, Key);

/// <summary>A photo of a spot was judged good, the first time or again (#1339): the postcard card counts it even for a spot already in the album.</summary>
public sealed record PhotoSeen(long Peer, string Key, string SpotId) : AdventureEvent(Peer, Key);

/// <summary>Bring a figure (<see cref="EntityId"/>) to an area: the lost hiker's walk to the hut. Advances on <see cref="Escorted"/>.</summary>
public sealed record EscortStep(string EntityId, AreaTarget At) : Step
{
    public override TargetRef? Target => At;
    public override string? Plan => "errands";
    public override bool Matches(AdventureEvent e) => e is Escorted s && s.EntityId == EntityId && s.TargetId == At.Id;
    public override string Describe() => $"Bring the hiker to {At.Name}";
}

/// <summary>Help at world events of a kind (any kind when null): a later catalog quest can say "help at 3 events".</summary>
public sealed record EventStep(WorldEventKind? Kind = null) : Step
{
    public override string? Plan => "errands";
    public override bool Matches(AdventureEvent e) => e is WorldEventHelped { Success: true } h && (Kind == null || h.Kind == Kind);
    public override string Describe() => Kind is { } k ? $"Help at {Count} {WorldEventRules.Name(k).ToLowerInvariant()} event{(Count == 1 ? "" : "s")}" : $"Help at {Count} world event{(Count == 1 ? "" : "s")}";
}

/// <summary>Take a good photo of a famous view (again if it is in the album already): the postcard home's first step. Advances on <see cref="PhotoSeen"/>.</summary>
public sealed record PostcardStep(string SpotId, string Name) : Step
{
    public override string? Plan => "rewards";
    public override bool Matches(AdventureEvent e) => e is PhotoSeen p && p.SpotId == SpotId;
    public override string Describe() => $"Photograph {Name}";
}
