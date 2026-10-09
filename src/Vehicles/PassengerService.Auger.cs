using Godot;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Vehicles;

/// <summary>
/// A combine's auger into a tipping trailer another player is driving (#494): one player acting on
/// another's driven vehicle, as a passenger's door button is (<see cref="PassengerService"/>).
///
/// <para>
/// A driven trailer's load lives in its driver's replicated trailer code, written only by the
/// driver's peer; a parked one is the server's (claim and park). So the combine's owner offers a
/// batch through the server (<see cref="OfferAuger"/>), which checks that both are farm machines,
/// slow and side by side, and passes it to the trailer's driver; that peer checks the spout over
/// its own bin as it sees the combine, takes what fits into its trailer code and answers how many;
/// the server passes the answer back (never more than was offered) and only then do they leave
/// the combine's tank. A lost answer leaves the sacks in the tank: none is ever counted twice.
/// </para>
/// </summary>
public partial class PassengerService
{
    /// <summary>Server: offers not answered yet, by the combine's peer: to whom, and how many.</summary>
    private readonly Dictionary<long, (long To, int Count)> _augerOffers = new();

    /// <summary>How far apart the two machines may be, by the server's copies, m (a combine and a tractor side by side).</summary>
    private const float AugerReach = 25f;

    /// <summary>The combine's owner: <paramref name="count"/> sacks of a crop for <paramref name="driverName"/>'s trailer.</summary>
    public void OfferAuger(string driverName, int crop, int count)
    {
        if (Online) RpcId(1, MethodName.RequestAuger, driverName, crop, count);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAuger(string driverName, int crop, int count)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        long driver = long.TryParse(driverName, out long d) ? d : 0;
        var (me, them) = (Player(sender), Player(driver));
        string? why = me == null || them == null || driver == sender ? "nobody there"
            : HeavyCatalog.For(me.Ride) is not { TankItems: > 0 } ? "not in a combine"
            : HeavyCatalog.For(them.Ride) is not { Farm: true, TankItems: 0 } || TrailerCatalog.For(them.TrailerCode) is not { TankItems: > 0 } ? "no tipping trailer"
            : count is <= 0 or > 64 || crop <= 0 ? "a bad batch"
            : me.WorldVelocity.Length() > FootPlayer.AugerDrivenSpeed + 1f || them.WorldVelocity.Length() > FootPlayer.AugerDrivenSpeed + 1f ? "too fast"
            : me.GlobalPosition.DistanceTo(them.GlobalPosition) > AugerReach ? "too far" : null;
        if (why != null)
        {
            GD.Print($"[passengers] auger from {sender} into {driverName}'s trailer refused: {why}");
            RpcId(sender, MethodName.AugerAnswered, 0);
            return;
        }
        _augerOffers[sender] = (driver, count);
        RpcId(driver, MethodName.AugerOffered, (int)sender, crop, count);
    }

    /// <summary>The trailer's driver: sacks from <paramref name="from"/>'s combine; how many fitted goes back.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AugerOffered(int from, int crop, int count)
    {
        int moved = Local is { } me ? me.AugerArrived(Player(from), (CropKind)crop, count) : 0;
        RpcId(1, MethodName.AugerTook, from, moved);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AugerTook(int from, int moved)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_augerOffers.TryGetValue(from, out var offer) || offer.To != sender) return;
        _augerOffers.Remove(from);
        int taken = Mathf.Clamp(moved, 0, offer.Count);
        if (taken > 0) GD.Print($"[passengers] auger: {taken} sacks from {from}'s combine into {sender}'s trailer");
        RpcId(from, MethodName.AugerAnswered, taken);
    }

    /// <summary>The combine's owner: this many of the offered sacks are in the trailer now.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AugerAnswered(int moved) => Local?.AugerTaken(moved);
}
