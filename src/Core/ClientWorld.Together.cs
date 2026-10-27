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
            : peer == Multiplayer.GetUniqueId() || !Launch.Networked && peer == Adventure.AdventureService.LocalPeer
                ? GameSettings.Current.PlayerName is { Length: > 0 } me ? me : "You" : $"#{peer}";
        together.Toast = Toast;
        // a check's partner body is where it stands, not where the local player is (offline the service asks one body)
        var position = adventure.PositionOf;
        adventure.PositionOf = peer => together.PairPeer != 0 && peer == together.PairPeer ? together.Body(peer)?.Global : position?.Invoke(peer);
        together.GlacierAt = g => _chunks != null && _worldOrigin is { } o && _chunks.TryGetCover(o.ToWorld(g), out var cover)
            ? cover is Terrain.Format.CoverClass.Glacier or Terrain.Format.CoverClass.Snowfield : null;
        AddChild(new TogetherChip());
        AddChild(new FonduePanel());
        TogetherPage.Register();
        if (FondueCheck.Requested) AddChild(new FondueCheck(items, () => LocalPlayer));
        if (TogetherCheck.Requested) AddChild(new TogetherCheck(items, () => LocalPlayer));
    }
}
