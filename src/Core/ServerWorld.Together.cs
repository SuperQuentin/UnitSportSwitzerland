using UnitSport.Adventure.Together;

namespace UnitSport.Core;

public partial class ServerWorld
{
    /// <summary>
    /// Playing together (#1266): the together rule, the links, the shared pots, the week and the mentors, kept
    /// here for every adventuring peer, from the proxies' published state and the parties.
    /// </summary>
    private void AddTogether(Adventure.AdventureService adventure)
    {
        var together = TogetherService.Create(adventure, server: true, networked: true);
        var parties = _parties;
        together.PartyOf = peer => parties?.Of(peer)?.Members;
        together.BodyOf = peer => _players?.GetNodeOrNull<Player.FootPlayer>(peer.ToString());
        if (_chat is { } chat) together.NameOf = chat.NameOfPeer;
    }
}
