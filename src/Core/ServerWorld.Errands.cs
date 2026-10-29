using UnitSport.Adventure.Errands;

namespace UnitSport.Core;

public partial class ServerWorld
{
    /// <summary>The errands (#1267): the cards, their taking, the reputation and the friend bonus, kept here for every adventuring peer.</summary>
    private void AddErrands(Adventure.AdventureService adventure)
    {
        GiverHost.Configure((SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N));
        var errands = ErrandService.Create(adventure, server: true, networked: true);
        if (_chat is { } chat) errands.NameOf = chat.NameOfPeer;
        // the world events (#1339): planned near adventuring players; off for a player in a Versteckis game
        var events = Adventure.Errands.WorldEvents.Create(adventure, server: true, networked: true);
        events.GroundAt = (e, n) => _chunks != null && _origin != null && _chunks.TryGetHeight(_origin.ToWorld(e, n, 0), out float h) ? h : null;
        events.ForestAt = (e, n) => _chunks != null && _origin != null && events.GroundAt?.Invoke(e, n) is { } g
            && _chunks.TryGetCover(_origin.ToWorld(e, n, g), out var cover) ? cover is Terrain.Format.CoverClass.Forest : null;
        events.Busy = peer => Adventure.Versteckis.VersteckisManager.Server?.InRound(peer) == true;
    }
}
