using UnitSport.Adventure.Alcohol;

namespace UnitSport.Core;

public partial class ServerWorld
{
    /// <summary>Alcohol (#1271): the drink logs and every breath reading, kept here for each peer; <c>--alcohol off|mild|real</c>.</summary>
    private void AddAlcohol(Adventure.AdventureService adventure)
    {
        var alcohol = AlcoholService.Create(adventure, server: true, networked: true);
        alcohol.BodyOf = peer => _players?.GetNodeOrNull<Player.FootPlayer>(peer.ToString());
        if (_chat is { } chat) alcohol.NameOf = chat.NameOfPeer;
    }
}
