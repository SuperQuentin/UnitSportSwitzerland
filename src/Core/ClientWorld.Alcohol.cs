using UnitSport.Adventure.Alcohol;
using UnitSport.Player;

namespace UnitSport.Core;

public partial class ClientWorld
{
    /// <summary>
    /// Alcohol (#1271): the owner's level from the synced drink log, its effects on the local body, the readings'
    /// screen and the edge blur; offline this client plays the server's part.
    /// </summary>
    private void AddAlcohol(Adventure.AdventureService adventure, Items.ItemController items)
    {
        var alcohol = AlcoholService.Create(adventure, server: false, networked: Launch.Networked);
        alcohol.Me = () => _onFoot ? LocalPlayer : null;
        alcohol.BodyOf = peer => peer == Multiplayer.GetUniqueId() || !Launch.Networked && peer == Adventure.AdventureService.LocalPeer
            ? LocalPlayer : _players?.GetNodeOrNull<FootPlayer>(peer.ToString());
        alcohol.NameOf = peer => Net.Parties.Client?.Members.FirstOrDefault(m => m.Peer == peer).Name is { Length: > 0 } n ? n
            : peer == Multiplayer.GetUniqueId() || !Launch.Networked && peer == Adventure.AdventureService.LocalPeer
                ? GameSettings.Current.PlayerName is { Length: > 0 } me ? me : "You" : $"#{peer}";
        alcohol.Toast = Toast;
        AddChild(new AlcoholHud());
        if (AlcoholCheck.Requested) AddChild(new AlcoholCheck(items, () => LocalPlayer));
        if (AlcoholNetProbe.Role != null) AddChild(new AlcoholNetProbe(items));
    }
}
