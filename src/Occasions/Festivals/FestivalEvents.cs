using UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/adventure-events.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// A festival is on, and this is its <paramref name="Day"/> (1 = the first): raised for every adventuring peer when a day
/// opens, and for a peer when its progress loads during a festival. The errands plan counts a running festival as one of
/// its world events.
/// </summary>
public sealed record FestivalOpened(long Peer, string Key, string Festival, int Day) : AdventureEvent(Peer, Key);

/// <summary>A play of a festival game ended with a score (<see cref="Game"/> is the game's id), raised after its Batzen and points were written.</summary>
public sealed record FestivalGamePlayed(long Peer, string Key, string Festival, string Game, int Score) : AdventureEvent(Peer, Key);

/// <summary>The party score reached its <paramref name="Index"/>-th milestone (0-based) and this member was paid.</summary>
public sealed record FestivalMilestoneReached(long Peer, string Key, string Festival, int Index) : AdventureEvent(Peer, Key);
