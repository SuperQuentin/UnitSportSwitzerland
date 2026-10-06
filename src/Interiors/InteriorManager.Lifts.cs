using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// Elevators (#557). Each one's state is a <see cref="LiftRide"/> the server owns and sends to
/// everyone in the building: where the cabin is, and the ride it is on. A ride is a teleport: the
/// doors shut, a moment passes, and when the cabin "arrives" the server tells everyone standing
/// in it to move themselves to the same spot one or more floors up or down (transforms are the
/// client's), so a group rides together. Then the doors open on the new floor.
///
/// <para>
/// Controls, the same on every device: E / Y (<see cref="PlayerInput.InteractMount"/>) at the
/// call button on a landing calls the cabin there; in the cabin it opens the floor list
/// (<see cref="FloorPicker"/>, arrows or the D-pad and A, or the VR pointer). In VR, gripping the
/// call button or the panel does the same (<see cref="TryInsideByHand"/>).
/// </para>
/// </summary>
public partial class InteriorManager
{
    /// <summary>How near the call button a player must stand to press it, m.</summary>
    private const float CallReach = 1.3f;
    /// <summary>A VR hand this near a button presses it, m.</summary>
    private const float HandButtonReach = 0.35f;
    /// <summary>Server: slack on the reach checks, for a body the server sees a little late.</summary>
    private const float ServerLiftSlack = 1.5f;

    /// <summary>Every elevator this peer knows the state of: (plan key, lift index).</summary>
    private readonly Dictionary<(string Key, int Lift), LiftRide> _lifts = new();
    /// <summary>Server: rides whose riders have been moved already.</summary>
    private readonly HashSet<(string Key, int Lift, double Start)> _carried = new();
    /// <summary>Client: the building whose elevators were last asked for.</summary>
    private string? _liftsAsked;
    /// <summary>Client: the arrival chimes already played.</summary>
    private readonly HashSet<(string Key, int Lift, double Start)> _chimed = new();
    private FloorPicker? _picker;

    /// <summary>The cabin's floor list, once it has been opened (for probes).</summary>
    public FloorPicker? Picker => _picker;

    private static double Clock => ClockSync.ServerNow;

    /// <summary>The state of one elevator: idle on the ground floor until anybody calls it.</summary>
    public LiftRide LiftOf(InteriorLayout l, int lift) =>
        _lifts.TryGetValue((l.Key, lift), out var r) ? r : LiftRide.Idle(Math.Clamp(l.Below, l.Lifts[lift].Bottom, l.Lifts[lift].Top));

    /// <summary>Which floor of a plan a local height (interior frame) stands on.</summary>
    public static int FloorAt(InteriorLayout l, float y)
    {
        int f = (int)MathF.Floor((y + 0.5f) / l.StoreyHeight) + l.Below;
        return Math.Clamp(f, 0, l.Floors.Count - 1);
    }

    /// <summary>Floor <paramref name="f"/>'s name, as the panel shows it.</summary>
    public static string FloorName(InteriorLayout l, int f)
    {
        int level = f - l.Below;
        return level switch { < -1 => $"Basement {-level}", -1 => "Basement", 0 => "Ground floor", _ => $"Floor {level}" };
    }

    /// <summary>What the player is at, among the elevators: in a cabin, or at a call button.</summary>
    private (int Lift, int Floor, bool InCabin)? LiftAt(InteriorLayout l, Vector3 local, float reach)
    {
        int floor = FloorAt(l, local.Y);
        float y0 = l.FloorY(floor);
        for (int i = 0; i < l.Lifts.Count; i++)
        {
            var lift = l.Lifts[i];
            if (!lift.Serves(floor)) continue;
            if (lift.Contains(local.X, local.Z, -0.05f)) return (i, floor, true);
            var call = lift.CallPoint(y0);
            if (new Vector2(call.X - local.X, call.Z - local.Z).Length() <= reach && !lift.Contains(local.X, local.Z, 0.2f))
                return (i, floor, false);
        }
        return null;
    }

    /// <summary>
    /// E indoors, before the cupboards and the street door: an elevator's call button or its
    /// cabin, a flat's front door. False when there is none in reach.
    /// </summary>
    public bool TryInside(FootPlayer p)
    {
        if (!p.Indoors || _current == null || CurrentNode is not { } node) return false;
        var local = node.ToLocal(p.GlobalPosition);
        if (_current.Lifts.Count > 0 && LiftAt(_current, local, CallReach) is { } at)
        {
            UseLift(at.Lift, at.Floor, at.InCabin);
            return true;
        }
        return TryInnerDoor(p, local);
    }

    /// <summary>A VR hand gripping (#557): a button it is on, or a flat's door it holds.</summary>
    public bool TryInsideByHand(FootPlayer p, Vector3 hand)
    {
        if (!p.Indoors || _current == null || CurrentNode is not { } node) return false;
        var local = node.ToLocal(hand);
        int floor = FloorAt(_current, node.ToLocal(p.GlobalPosition).Y);
        for (int i = 0; i < _current.Lifts.Count; i++)
        {
            var lift = _current.Lifts[i];
            if (!lift.Serves(floor)) continue;
            float y0 = _current.FloorY(floor);
            if (lift.CallPoint(y0).DistanceTo(local) <= HandButtonReach) { UseLift(i, floor, false); return true; }
            if (lift.PanelPoint(y0 + LiftPlan.ButtonHeight).DistanceTo(local) <= HandButtonReach + 0.2f) { UseLift(i, floor, true); return true; }
        }
        return TryInnerDoorByHand(p, local);
    }

    /// <summary>The prompt for what <see cref="TryInside"/> would do here, or null.</summary>
    private string? InsidePrompt(FootPlayer p)
    {
        if (_current == null || CurrentNode is not { } node) return null;
        var local = node.ToLocal(p.GlobalPosition);
        if (_current.Lifts.Count > 0 && LiftAt(_current, local, CallReach) is { } at)
        {
            var ride = LiftOf(_current, at.Lift);
            if (at.InCabin)
                return ride.Busy(Clock) ? "The elevator is moving" : InputHints.Prompt(PlayerInput.InteractMount, "Choose a floor");
            return ride.At(Clock) == at.Floor && !ride.Busy(Clock) ? null : InputHints.Prompt(PlayerInput.InteractMount, "Call the elevator");
        }
        return InnerDoorPrompt(local);
    }

    private void UseLift(int lift, int floor, bool inCabin)
    {
        var l = _current!;
        var ride = LiftOf(l, lift);
        if (!inCabin)
        {
            if (ride.At(Clock) == floor && !ride.Busy(Clock)) return;
            AskLift(l.Key, lift, floor);
            Hint(ride.Busy(Clock) ? "The elevator is busy: try again in a moment." : "Called the elevator.");
            return;
        }
        if (ride.Busy(Clock)) { Hint("The elevator is moving."); return; }
        if (_picker == null)
        {
            _picker = new FloorPicker { Name = "FloorPicker" };
            AddChild(_picker);
        }
        var lp = l.Lifts[lift];
        var names = new List<(int Floor, string Name)>();
        for (int f = lp.Top; f >= lp.Bottom; f--) names.Add((f, FloorName(l, f)));
        string key = l.Key;
        _picker.Open(names, floor, f => { if (f != floor) AskLift(key, lift, f); });
    }

    private void AskLift(string key, int lift, int floor)
    {
        if (Online) RpcId(1, MethodName.RequestLift, key, lift, floor);
        else ServeLift(MyId, key, lift, floor);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLift(string key, int lift, int floor)
    {
        if (!Multiplayer.IsServer()) return;
        ServeLift(Multiplayer.GetRemoteSenderId(), key, lift, floor);
    }

    /// <summary>
    /// Server (or offline): a call to <paramref name="floor"/>, from its landing or from inside the
    /// cabin. Starts a ride from where the cabin is, unless it is already there or still on one.
    /// </summary>
    private void ServeLift(long sender, string key, int lift, int floor)
    {
        if (!_cache.TryGetValue(key, out var l) || lift < 0 || lift >= l.Lifts.Count || !l.Lifts[lift].Serves(floor)) return;
        // only someone in the building, at the button or in the cabin
        if (Online && SpaceOf(sender) != key) return;
        if (LocalOf(sender, l) is { } local && (LiftAt(l, local, CallReach + ServerLiftSlack) is not { } near || near.Lift != lift))
        {
            Refuse(sender, "Too far from the elevator.");
            return;
        }
        double now = Clock;
        var ride = LiftOf(l, lift);
        if (ride.Busy(now) || ride.At(now) == floor) return;
        BroadcastLift(key, lift, new LiftRide(ride.At(now), floor, now));
    }

    /// <summary>A player's position in a plan's frame, or null when the server cannot see their body.</summary>
    private Vector3? LocalOf(long peer, InteriorLayout l)
    {
        var body = PlayerBody(peer);
        if (body == null || Origin == null || !IsInstanceValid(body)) return null;
        return PlacementFor(l, Origin).AffineInverse() * body.GlobalPosition;
    }

    private void BroadcastLift(string key, int lift, LiftRide ride)
    {
        SetLift(key, lift, ride.From, ride.To, ride.Start);
        if (!Online) return;
        foreach (var (peer, space) in _spaces)
            if (space == key && peer != MyId) RpcId(peer, MethodName.SetLift, key, lift, ride.From, ride.To, ride.Start);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetLift(string key, int lift, int from, int to, double start) => _lifts[(key, lift)] = new LiftRide(from, to, start);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLifts(string key)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        foreach (var ((k, lift), ride) in _lifts)
            if (k == key) RpcId(sender, MethodName.SetLift, k, lift, ride.From, ride.To, ride.Start);
    }

    /// <summary>
    /// Server (or offline), every frame: a cabin that has arrived carries everyone standing in it.
    /// Who that is is decided now, by where the server sees them, not when the ride was asked:
    /// whoever got in while the doors were closing rides, whoever got out did not.
    /// </summary>
    private void TickLifts()
    {
        if (_lifts.Count == 0) return;
        double now = Clock;
        foreach (var ((key, lift), ride) in _lifts)
        {
            if (!ride.Rides || now < ride.Arrive || !_carried.Add((key, lift, ride.Start))) continue;
            if (!_cache.TryGetValue(key, out var l)) continue;
            var cab = l.Lifts[lift];
            var peers = Online ? _spaces.Where(s => s.Value == key).Select(s => s.Key).ToList() : new List<long> { MyId };
            foreach (long peer in peers)
            {
                if (LocalOf(peer, l) is not { } at || FloorAt(l, at.Y) != ride.From || !cab.Contains(at.X, at.Z, 0.15f)) continue;
                if (peer == MyId) CarryLift(key, lift, ride.From, ride.To);
                else RpcId(peer, MethodName.CarryLift, key, lift, ride.From, ride.To);
            }
        }
    }

    /// <summary>The server says this player is in a cabin that has arrived: the same spot, the new floor.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CarryLift(string key, int lift, int from, int to)
    {
        var p = LocalPlayer?.Invoke();
        if (p == null || !IsInstanceValid(p) || !p.Indoors || p.InteriorKey != key || !_cache.TryGetValue(key, out var l)) return;
        p.RideLift(l.FloorY(to) - l.FloorY(from));
        _picker?.Close();
        GD.Print($"[lift] rode {key} lift {lift} from {FloorName(l, from)} to {FloorName(l, to)}");
    }

    /// <summary>Client, every frame: doors slide to what the clock says, a chime as a cabin arrives, the lifts of a building just entered asked for.</summary>
    private void PresentLifts()
    {
        if (_current is { Lifts.Count: > 0 } here && _liftsAsked != here.Key)
        {
            _liftsAsked = here.Key;
            if (Online && !Multiplayer.IsServer()) RpcId(1, MethodName.RequestLifts, here.Key);
        }
        double now = Clock;
        foreach (var (plan, node) in _built)
        {
            var l = node.Layout;
            for (int i = 0; i < l.Lifts.Count; i++)
            {
                var ride = LiftOf(l, i);
                for (int f = l.Lifts[i].Bottom; f <= l.Lifts[i].Top; f++) node.SetLiftDoors(i, f, ride.Doors(now, f));
                if (plan != _current?.Key || !ride.Rides || now < ride.Arrive || !_chimed.Add((plan, i, ride.Start))) continue;
                var door = l.Lifts[i].WallPoint(0, l.FloorY(ride.To) + 2.2f, 0.3f);
                _sounds?.Lift(node.GlobalTransform * door, chime: true);
            }
        }
    }
}
