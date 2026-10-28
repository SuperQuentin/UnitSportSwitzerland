using UnitSport.Adventure.Errands;
using UnitSport.Player;

namespace UnitSport.Core;

public partial class ClientWorld
{
    /// <summary>
    /// The errands (#1267): the givers as figures, the dialogue, the daily Postkarten and the Journal's Errands tab; offline this client
    /// plays the server's part (it hands the cards out and keeps the reputation).
    /// </summary>
    private void AddErrands(Adventure.AdventureService adventure, Items.ItemController items)
    {
        var start = _fixtureStart ?? (SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
        GiverHost.Configure(start);
        var errands = ErrandService.Create(adventure, server: false, networked: Launch.Networked);
        errands.Toast = Toast;
        errands.NameOf = peer => Net.Parties.Client?.Members.FirstOrDefault(m => m.Peer == peer).Name is { Length: > 0 } n ? n : $"#{peer}";
        AddChild(new ErrandGivers(() => _onFoot ? LocalPlayer : null, () => _worldOrigin,
            p => _chunks != null && _chunks.TryGetHeight(p, out float h) ? h : null));
        AddChild(new ErrandDialog(() => _onFoot ? LocalPlayer : null));
        ErrandsPage.Where = () => _onFoot ? LocalPlayer?.Global : null;
        ErrandsPage.Register();
    }
}
