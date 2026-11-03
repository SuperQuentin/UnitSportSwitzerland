using System;
using UnitSport.Items;

namespace UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/adventure-events.md).

/// <summary>
/// Something a player did that adventure objectives may count (#1223). Raised on the server only
/// (offline on the client playing its part), after the raising system has checked the act and written
/// its own section. <see cref="Key"/> is the player's progress key. A plan adds its own events as new
/// records deriving from this one, in its own file: the quest engine matches by type.
/// </summary>
public abstract record AdventureEvent(long Peer, string Key);

/// <summary>A point of interest found (places plan). <see cref="Kind"/> e.g. <c>"pass"</c>, <see cref="Canton"/> the two-letter code.</summary>
public sealed record PoiDiscovered(long Peer, string Key, string PoiId, string Kind, string Canton) : AdventureEvent(Peer, Key);
/// <summary>A viewpoint synced: the map round it revealed (places plan).</summary>
public sealed record ViewpointSynced(long Peer, string Key, string PoiId, string Canton) : AdventureEvent(Peer, Key);
/// <summary>An edelweiss, a cairn or another collectible taken (rewards plan).</summary>
public sealed record CollectibleTaken(long Peer, string Key, string CollectibleId, string Kind, string Canton) : AdventureEvent(Peer, Key);
/// <summary>A chest opened (rewards plan); <see cref="PoiId"/> the POI it stands at, if any.</summary>
public sealed record ChestOpened(long Peer, string Key, string ChestId, int Tier, string? PoiId) : AdventureEvent(Peer, Key);
/// <summary>A timed trial finished (rewards plan); <see cref="Medal"/> 0 none, 1 bronze, 2 silver, 3 gold.</summary>
public sealed record TrialFinished(long Peer, string Key, string TrialId, double Seconds, int Medal) : AdventureEvent(Peer, Key);
/// <summary>A photo target taken (rewards plan).</summary>
public sealed record PhotoTaken(long Peer, string Key, string TargetId, string? PoiId) : AdventureEvent(Peer, Key);
/// <summary>
/// A travel point unlocked (navigation plan); <see cref="Kind"/> station, busstop, landing, cablecar; <see cref="Canton"/>
/// the point's; <see cref="Points"/> and <see cref="Cantons"/> the player's network after it (the "Grand tour").
/// </summary>
public sealed record TravelUnlocked(long Peer, string Key, string PointId, string Kind, string Canton = "", int Points = 0, int Cantons = 0) : AdventureEvent(Peer, Key);
/// <summary>Items given to the player by a server-checked source (a quest, a chest).</summary>
public sealed record ItemObtained(long Peer, string Key, ItemId Item, int Count, string Source) : AdventureEvent(Peer, Key);
/// <summary>Items handed over at a delivery target (<c>AdventureService.Deliver</c>).</summary>
public sealed record ItemDelivered(long Peer, string Key, ItemId Item, int Count, string TargetId) : AdventureEvent(Peer, Key);
/// <summary>An interactable used: raised by the interactable's own server method, never by a quest.</summary>
public sealed record Interacted(long Peer, string Key, string TargetId) : AdventureEvent(Peer, Key);
/// <summary>A running quest was given up (#1267): the step and the count it had reached, so its cargo can be taken back.</summary>
public sealed record QuestAbandoned(long Peer, string Key, string QuestId, int Step, int Count) : AdventureEvent(Peer, Key);
/// <summary>A quest finished (raised by the service itself, so quests chain).</summary>
public sealed record QuestCompleted(long Peer, string Key, string QuestId) : AdventureEvent(Peer, Key);

/// <summary>
/// The server's adventure event bus (#1223): synchronous, on the main thread, nothing kept. An event
/// whose peer is not adventuring now (<see cref="IsActive"/> false: a Battle Royale entrant, the
/// system off) is dropped. No log line per event.
/// </summary>
public sealed class AdventureEvents
{
    /// <summary>Whether a peer adventures now; null lets every event through (tests).</summary>
    public Func<long, bool>? IsActive { get; set; }

    public event Action<AdventureEvent>? Raised;

    /// <summary>Events dropped because their peer was inactive (checks).</summary>
    public int Dropped { get; private set; }

    public void Raise(AdventureEvent e)
    {
        if (IsActive != null && !IsActive(e.Peer))
        {
            Dropped++;
            return;
        }
        Raised?.Invoke(e);
    }
}
