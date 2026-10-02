using Godot;
using UnitSport.Player;
using UnitSport.Core;

namespace UnitSport.Vehicles;

/// <summary>
/// Passengers (#158), at <c>World/Passengers</c> on the server and every client (RPCs route by path).
///
/// <para>
/// A vehicle being driven lives inside one player, its <b>host</b> (<see cref="FootPlayer"/>): the
/// driver, or, once the driver has jumped out, a passenger it rolls on with, driverless, under its
/// own physics. Its other riders sit in its seats (<see cref="FootPlayer.RidingWith"/>,
/// <see cref="FootPlayer.SeatIndex"/>) and are drawn and moved from the host's copy on every peer.
/// The server hands out the seats, so two players never sit in one, and moves the vehicle from one
/// host to the next: when the driver gets out it goes on with its first passenger aboard, and the
/// "take the wheel" binding brings whoever asks into the free driver's seat. Only a vehicle left
/// with nobody aboard is parked (<see cref="VehicleManager"/>).
/// </para>
/// </summary>
public partial class PassengerService : Node
{
    public const string NodeName = "Passengers";

    public static PassengerService? Instance { get; private set; }

    /// <summary>Client: something to tell the local player (a refused seat, too fast to get out).</summary>
    public static event Action<string>? Said;
    /// <summary>Drops the subscribers a world left behind when it was freed (<see cref="Core.WorldStatics"/>).</summary>
    internal static void ResetEvents() => Said = null;

    public static void Say(string message) => Said?.Invoke(message);

    /// <summary>How a standing passenger feels the vehicle move (#162): carried exactly, swaying, or every acceleration.</summary>
    public enum DeckInertia { Steady, Sway, Full }

    /// <summary>The server's choice (<c>--deck-inertia</c>, <c>/inertia</c>), sent to every client; offline, this client's.</summary>
    public static DeckInertia Inertia { get; private set; } = DeckInertia.Sway;

    public static bool TryParseInertia(string text, out DeckInertia mode) =>
        Enum.TryParse(text, ignoreCase: true, out mode) && Enum.IsDefined(mode);

    /// <summary>Server: the players, <c>World/Players</c>.</summary>
    public Node? Players { get; set; }

    /// <summary>How far from a vehicle someone may get into one of its seats, m beyond its half-length.</summary>
    public const float BoardReach = 3f;
    /// <summary>The vehicle must be this slow to get in or out of a passenger seat, m/s.</summary>
    public const float BoardSpeed = 3f;

    /// <summary>One driven vehicle with people aboard: its host, the host's seat (0 = at the wheel), the riders in boarding order.</summary>
    private sealed class Ride
    {
        public long Host;
        public int HostSeat;
        public readonly List<(long Peer, int Seat)> Riders = new();
        /// <summary>A hand-over asked of the host and not answered yet: nothing else moves it meanwhile.</summary>
        public long PendingTo;
    }

    private readonly Dictionary<long, Ride> _byHost = new();
    private readonly Dictionary<long, Ride> _byRider = new();

    public static PassengerService Create(Node world)
    {
        var service = new PassengerService { Name = NodeName };
        world.AddChild(service);
        Instance = service;
        if (CmdArgs.Value("--deck-inertia") is { } inertia && TryParseInertia(inertia, out var mode)) Inertia = mode;
        return service;
    }

    /// <summary>Server: how standing passengers feel the vehicles move, for everyone from now on.</summary>
    public void SetInertia(DeckInertia mode)
    {
        Inertia = mode;
        if (Online && Multiplayer.IsServer()) Rpc(MethodName.InertiaIs, (int)mode);
    }

    /// <summary>Server: tells a client that just joined.</summary>
    public void SendTo(long peer)
    {
        if (Online && Multiplayer.IsServer()) RpcId(peer, MethodName.InertiaIs, (int)Inertia);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>Server: the two players are aboard one vehicle (host or rider), so each must always see the other.</summary>
    public bool Together(long a, long b)
    {
        var ride = RideOf(a);
        return ride != null && ride == RideOf(b);
    }

    private Ride? RideOf(long peer) => _byHost.GetValueOrDefault(peer) ?? _byRider.GetValueOrDefault(peer);

    private FootPlayer? Player(long peer) => Players?.GetNodeOrNull<FootPlayer>(peer.ToString())
        ?? GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p.Name == peer.ToString());

    /// <summary>This peer's own player.</summary>
    private FootPlayer? Local => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
        .FirstOrDefault(p => p.IsMultiplayerAuthority() && !p.Npc);

    // ---- client side: asking ----------------------------------------------------------------

    /// <summary>On foot beside <paramref name="host"/>'s vehicle: a seat in it, please.</summary>
    public void AskSeat(FootPlayer host) { if (Online) RpcId(1, MethodName.RequestSeat, host.Name.ToString()); }

    /// <summary>Getting out of a passenger seat.</summary>
    public void LeaveSeat() { if (Online) RpcId(1, MethodName.SeatLeft); }

    /// <summary>A passenger, or a host sat in its own driverless vehicle: the driver's seat, please.</summary>
    public void AskWheel() { if (Online) RpcId(1, MethodName.RequestWheel); }

    /// <summary>At a door's button of <paramref name="host"/>'s bus (#162): open or shut that door.</summary>
    public void PressDoor(FootPlayer host, int door) { if (Online) RpcId(1, MethodName.RequestDoorPress, host.Name.ToString(), door); }

    /// <summary>Walking about in <paramref name="host"/>'s vehicle (#162): that seat, please.</summary>
    public void AskSeatAt(FootPlayer host, int seat) { if (Online) RpcId(1, MethodName.RequestSeatAt, host.Name.ToString(), seat); }

    /// <summary>Walking about in <paramref name="host"/>'s vehicle, rolling with nobody at its wheel: the wheel, please.</summary>
    public void AskWheelOf(FootPlayer host) { if (Online) RpcId(1, MethodName.RequestWheelOf, host.Name.ToString()); }

    /// <summary>The host gets out with people aboard: the vehicle goes on with them (or is parked if they have gone).</summary>
    public void HostLeaving(VehicleState state) { if (Online) RpcId(1, MethodName.HostLeft, state.ToDict()); }

    /// <summary>The host was asked to hand the vehicle over (<see cref="FootPlayer.GiveUpVehicle"/>): its state, to pass on.</summary>
    public void HandOver(long to, VehicleState state) { if (Online) RpcId(1, MethodName.HandedOver, to, state.ToDict()); }

    /// <summary>The host's vehicle was wrecked: everyone aboard is thrown out.</summary>
    public void Wrecked(Vector3 velocity) { if (Online) RpcId(1, MethodName.HostWrecked, velocity); }

    // ---- server side ----------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSeat(string hostName)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        string? why = Seat(sender, long.TryParse(hostName, out long h) ? h : 0, out int seat);
        if (why != null) { RpcId(sender, MethodName.Refused, why); return; }
        RpcId(sender, MethodName.Seated, (int)h, seat);
    }

    /// <summary>Server: the seat <paramref name="peer"/> gets in <paramref name="host"/>'s vehicle, or why not.</summary>
    private string? Seat(long peer, long host, out int seat)
    {
        seat = 0;
        if (peer == host || RideOf(peer) != null) return "You are already aboard.";
        var (me, them) = (Player(peer), Player(host));
        if (me == null || them == null || them.Ride == RideKind.OnFoot || them.RidingAlong) return "Nobody is driving that.";
        if (me.Ride != RideKind.OnFoot) return "Get off first.";
        var vehicle = CarSetups.Ride(them.Ride, them.CarSetupId, them.TuningBits) ?? Rideable.Create(them.Ride);
        var seats = vehicle.Seats;
        if (seats.Length < 2) return "There is no seat for a passenger.";
        if (vehicle is Truck { IsBus: true } && them.BusDoors == 0) return "The doors are shut.";
        if (them.WorldVelocity.Length() > BoardSpeed) return "It is moving.";
        float reach = vehicle.ParkedBox.Size.Z * 0.5f + BoardReach;
        if (me.GlobalPosition.DistanceTo(them.GlobalPosition) > reach) return "Too far from it.";

        var ride = _byHost.GetValueOrDefault(host);
        if (ride is { PendingTo: not 0 }) return "Wait a moment.";
        // a host sat in its own driverless vehicle is not at the wheel: its copy says where it sits
        int hostSeat = ride?.HostSeat ?? them.SeatIndex;
        var taken = new HashSet<int> { hostSeat };
        if (ride != null) foreach (var (_, s) in ride.Riders) taken.Add(s);
        float best = float.MaxValue;
        for (int i = 1; i < seats.Length; i++)
        {
            if (taken.Contains(i)) continue;
            float d = them.ToGlobal(vehicle.SeatPosition(i)).DistanceTo(me.GlobalPosition);
            if (d < best) { best = d; seat = i; }
        }
        if (seat == 0) return "It is full.";

        if (ride == null) _byHost[host] = ride = new Ride { Host = host, HostSeat = hostSeat };
        ride.Riders.Add((peer, seat));
        _byRider[peer] = ride;
        GD.Print($"[passengers] {peer} takes seat {seat} in {host}'s {them.Ride} ({ride.Riders.Count} aboard)");
        return null;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDoorPress(string hostName, int door)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        long host = long.TryParse(hostName, out long h) ? h : 0;
        var (me, them) = (Player(sender), Player(host));
        if (me == null || them == null || them.Ride == RideKind.OnFoot) return;
        var vehicle = CarSetups.Ride(them.Ride, them.CarSetupId, them.TuningBits) ?? Rideable.Create(them.Ride);
        // only someone standing at one of that door's buttons (the bus straight: a joint moves little)
        bool there = vehicle.Decks.SelectMany(d => d.Buttons.Where(b => b.Door == door).Select(b =>
            them.ToGlobal(d.Section == 0 || vehicle is not Truck t ? b.At : t.NodeLocal(d.Section) * b.At)))
            .Any(at => at.DistanceTo(me.GlobalPosition + Vector3.Up * 1.1f) < ButtonReach + 1f);
        if (there) RpcId(host, MethodName.DoorPress, door);
    }

    /// <summary>How far from a door's button a player may press it, m (a hand's reach from the chest).</summary>
    public const float ButtonReach = 0.9f;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DoorPress(int door) => Local?.DoorPressed(door);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSeatAt(string hostName, int seat)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        long host = long.TryParse(hostName, out long h) ? h : 0;
        string? why = SeatAt(sender, host, seat);
        if (why != null) { RpcId(sender, MethodName.Refused, why); return; }
        RpcId(sender, MethodName.Seated, (int)host, seat);
    }

    /// <summary>Server: <paramref name="peer"/>, walking about in <paramref name="host"/>'s vehicle, sits in <paramref name="seat"/>, or why not. Moving or not.</summary>
    private string? SeatAt(long peer, long host, int seat)
    {
        if (peer == host || RideOf(peer) != null) return "You are already sat down.";
        var (me, them) = (Player(peer), Player(host));
        if (me == null || them == null || them.Ride == RideKind.OnFoot || them.RidingAlong) return "Nobody is driving that.";
        var vehicle = CarSetups.Ride(them.Ride, them.CarSetupId, them.TuningBits) ?? Rideable.Create(them.Ride);
        if (seat <= 0 || seat >= vehicle.Seats.Length) return "There is no such seat.";
        if (me.GlobalPosition.DistanceTo(them.ToGlobal(vehicle.SeatPosition(seat))) > SeatReach) return "Too far from that seat.";
        var ride = _byHost.GetValueOrDefault(host);
        if (ride is { PendingTo: not 0 }) return "Wait a moment.";
        int hostSeat = ride?.HostSeat ?? them.SeatIndex;
        if (seat == hostSeat || ride?.Riders.Any(r => r.Seat == seat) == true) return "Somebody sits there.";
        if (ride == null) _byHost[host] = ride = new Ride { Host = host, HostSeat = hostSeat };
        ride.Riders.Add((peer, seat));
        _byRider[peer] = ride;
        GD.Print($"[passengers] {peer} sits down in seat {seat} of {host}'s {them.Ride} ({ride.Riders.Count} sat)");
        return null;
    }

    /// <summary>How far from a seat (its hip) a player standing aboard may be to sit in it, m: what the server allows.</summary>
    public const float SeatReach = 2.5f;

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestWheelOf(string hostName)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        long host = long.TryParse(hostName, out long h) ? h : 0;
        var (me, them) = (Player(sender), Player(host));
        if (me == null || them == null || them.Ride == RideKind.OnFoot || RideOf(sender) != null) return;
        var ride = _byHost.GetValueOrDefault(host);
        int hostSeat = ride?.HostSeat ?? them.SeatIndex;
        if (hostSeat == 0) { RpcId(sender, MethodName.Refused, "Somebody is driving."); return; }
        var vehicle = CarSetups.Ride(them.Ride, them.CarSetupId, them.TuningBits) ?? Rideable.Create(them.Ride);
        if (me.GlobalPosition.DistanceTo(them.ToGlobal(vehicle.SeatPosition(0))) > SeatReach) { RpcId(sender, MethodName.Refused, "Too far from the wheel."); return; }
        if (ride == null) _byHost[host] = ride = new Ride { Host = host, HostSeat = hostSeat };
        if (ride.PendingTo != 0) return;
        ride.PendingTo = sender;
        RpcId(host, MethodName.GiveUp, (int)sender);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SeatLeft()
    {
        if (!Multiplayer.IsServer()) return;
        Drop(Multiplayer.GetRemoteSenderId());
    }

    /// <summary>Server: a rider is no longer aboard.</summary>
    private void Drop(long peer)
    {
        if (!_byRider.Remove(peer, out var ride)) return;
        ride.Riders.RemoveAll(r => r.Peer == peer);
        if (ride.Riders.Count == 0 && ride.HostSeat == 0 && ride.PendingTo == 0) _byHost.Remove(ride.Host);
        GD.Print($"[passengers] {peer} got out of {ride.Host}'s vehicle");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestWheel()
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (RideOf(sender) is not { } ride || ride.PendingTo != 0) return;
        if (ride.HostSeat == 0) { RpcId(sender, MethodName.Refused, "Somebody is driving."); return; }
        if (sender == ride.Host)
        {
            // the host is sat in its own driverless vehicle: it only moves over
            ride.HostSeat = 0;
            if (ride.Riders.Count == 0) _byHost.Remove(ride.Host);
            RpcId(sender, MethodName.WheelTaken);
            GD.Print($"[passengers] {sender} takes the wheel of its own vehicle");
            return;
        }
        ride.PendingTo = sender;
        RpcId(ride.Host, MethodName.GiveUp, (int)sender);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HandedOver(long to, Godot.Collections.Dictionary data)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_byHost.TryGetValue(sender, out var ride) || ride.PendingTo != to) return;
        // the old host stays in the seat it sat in, now a rider like the others
        int toSeat = ride.Riders.FirstOrDefault(r => r.Peer == to).Seat;
        ride.Riders.RemoveAll(r => r.Peer == to);
        _byRider.Remove(to);
        ride.Riders.Insert(0, (sender, ride.HostSeat));
        _byRider[sender] = ride;
        Rehost(ride, sender, to, 0, VehicleState.FromDict(data));
        GD.Print($"[passengers] {to} takes the wheel from {sender} (it was in seat {toSeat})");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HostLeft(Godot.Collections.Dictionary data)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        LeaveVehicle(sender, VehicleState.FromDict(data));
    }

    /// <summary>
    /// Server: <paramref name="host"/> is out of its vehicle (got out, or gone). With riders aboard
    /// it rolls on driverless with the first of them as its host, sat where they were; a motorbike
    /// cannot, so its pillion is put off; with nobody left it is parked as if the host had parked it.
    /// </summary>
    private void LeaveVehicle(long host, VehicleState state)
    {
        _byHost.Remove(host, out var ride);
        var riders = ride?.Riders ?? new List<(long Peer, int Seat)>();
        bool rolls = Rideable.Create(state.Kind).Driverless;
        if (riders.Count == 0 || !rolls)
        {
            foreach (var (peer, _) in riders) { _byRider.Remove(peer); RpcId(peer, MethodName.Eject, state.Velocity); }
            VehicleManager.Instance?.ParkFor(host, state);
            return;
        }
        var (next, seat) = riders[0];
        riders.RemoveAt(0);
        _byRider.Remove(next);
        var moved = new Ride { Host = next, HostSeat = seat };
        moved.Riders.AddRange(riders);
        _byHost[next] = moved;
        foreach (var (peer, _) in riders) _byRider[peer] = moved;
        Rehost(moved, host, next, seat, state);
        GD.Print($"[passengers] {host} got out of its {state.Kind}: it rolls on driverless with {next} aboard ({riders.Count} more)");
    }

    /// <summary>Server: <paramref name="ride"/> now lives in <paramref name="to"/> (sat in <paramref name="seat"/>), taken from <paramref name="from"/>.</summary>
    private void Rehost(Ride ride, long from, long to, int seat, VehicleState state)
    {
        ride.Host = to;
        ride.HostSeat = seat;
        ride.PendingTo = 0;
        _byHost.Remove(from);
        _byHost[to] = ride;
        VehicleManager.Instance?.TransferDriving(from, to, state.Units);
        RpcId(to, MethodName.TakeVehicle, state.ToDict(), seat);
        foreach (var (peer, s) in ride.Riders) RpcId(peer, MethodName.Moved, (int)to, s);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HostWrecked(Vector3 velocity)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_byHost.Remove(sender, out var ride)) return;
        foreach (var (peer, _) in ride.Riders) { _byRider.Remove(peer); RpcId(peer, MethodName.Eject, velocity); }
    }

    /// <summary>
    /// Server: a player left the game. A host with people aboard: its vehicle goes on with them, as
    /// if it had jumped out (its state from the server's copy). A rider: its seat is free.
    /// </summary>
    public void PeerLeft(long peer)
    {
        Drop(peer);
        if (!_byHost.TryGetValue(peer, out var ride)) return;
        if (ride.PendingTo != 0) ride.PendingTo = 0;
        if (Player(peer)?.VehicleStateOfCopy() is { } state) LeaveVehicle(peer, state);
        else
        {
            _byHost.Remove(peer);
            foreach (var (rider, _) in ride.Riders) { _byRider.Remove(rider); RpcId(rider, MethodName.Eject, Vector3.Zero); }
        }
    }

    // ---- client side: answers ---------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Seated(int host, int seat) => Local?.BoardAs(host, seat);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Refused(string why) => Say(why);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WheelTaken() => Local?.MoveToWheel();

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GiveUp(int to) => Local?.GiveUpVehicle(to);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TakeVehicle(Godot.Collections.Dictionary data, int seat) => Local?.TakeVehicle(VehicleState.FromDict(data), seat);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Moved(int host, int seat) => Local?.BoardAs(host, seat);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Eject(Vector3 velocity) => Local?.ThrownOut(velocity);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void InertiaIs(int mode) => Inertia = (DeckInertia)mode;
}
