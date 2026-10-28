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
    }
}
