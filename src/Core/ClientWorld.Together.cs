using UnitSport.Adventure.Together;
using UnitSport.Player;

namespace UnitSport.Core;

public partial class ClientWorld
{
    /// <summary>
    /// Playing together (#1266): the owner's together count for its own loot, the links drawn on every body, the
    /// shared pot's table and panel; offline this client plays the server's part.
    /// </summary>
    private void AddTogether(Adventure.AdventureService adventure, Items.ItemController items)
    {
        var together = TogetherService.Create(adventure, server: false, networked: Launch.Networked);
        together.Me = () => _onFoot ? LocalPlayer : null;
        together.BodyOf = peer => peer == Multiplayer.GetUniqueId() ? LocalPlayer : _players?.GetNodeOrNull<FootPlayer>(peer.ToString());
        together.NameOf = peer => Net.Parties.Client?.Members.FirstOrDefault(m => m.Peer == peer).Name is { Length: > 0 } n ? n
            : peer == Multiplayer.GetUniqueId() || !Launch.Networked && peer == Adventure.AdventureService.LocalPeer ? GameSettings.Current.PlayerName : $"#{peer}";
        AddChild(new TogetherChip());
    }
}
