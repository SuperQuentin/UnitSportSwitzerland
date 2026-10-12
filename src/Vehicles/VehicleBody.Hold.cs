using Godot;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// A vehicle parked in another's hold (#418, <c>docs/notes/vehicles/vehicles-in-holds.md</c>): it
/// stands where <see cref="VehicleState.Carrier"/> says in the carrier's frame, put there by every peer
/// from its own copy of the carrier each frame, with no physics of its own. Its carrier changing hands
/// (the pilot got out: a parked carrier now, a new key) it holds still where it is and takes the
/// carrier whose bay it stands in.
/// </summary>
public partial class VehicleBody
{
    /// <summary>The key of the vehicle carrying it (<see cref="FootPlayer.KeyOf"/>); empty when it stands in the world.</summary>
    public string Carrier { get; private set; } = "";

    /// <summary>Parked in a hold: carried, even while its carrier is between two forms.</summary>
    public bool InHold => _inHold;

    private bool _inHold;
    private int _carrierSection;
    private Vector3 _carrierPos;
    private float _carrierYaw;
    private Node3D? _carrierHost;
    private readonly List<PhysicsBody3D> _carrierBodies = new();
    private float _carrierLook;

    /// <summary>For checks: where it stands in its carrier's section frame.</summary>
    public Vector3 CarrierPos => _carrierPos;
    /// <summary>The carrier's section it stands in (a boat on the trailer behind a truck, #463).</summary>
    public int CarrierSection => _carrierSection;

    private void BeginHold(VehicleState s)
    {
        if (!s.InHold) return;
        _inHold = true;
        Carrier = s.Carrier;
        _carrierSection = s.CarrierSection;
        _carrierPos = s.CarrierPos;
        _carrierYaw = s.CarrierYaw;
        Velocity = Vector3.Zero;
        // after the carrier's copy has moved this frame, before the people walking in it (10)
        ProcessPriority = 5;
    }

    /// <summary>
    /// Every frame on every peer: where the carrier's section is drawn now, plus the spot in it. A
    /// carrier gone (it changed hands) is looked for again where the vehicle stands, four times a second.
    /// </summary>
    private void FollowCarrier(float dt)
    {
        if (_carrierHost == null || !IsInstanceValid(_carrierHost) || !_carrierHost.IsInsideTree()
            || FootPlayer.RideOfHost(_carrierHost) is not { } ride || !FootPlayer.HasHolds(ride))
        {
            if (_carrierHost != null) Unhook();
            if ((_carrierLook -= dt) > 0f) return;
            _carrierLook = 0.25f;
            if (Carrier != "" && FootPlayer.CarrierNamed(this, Carrier) is { } named && FootPlayer.RideOfHost(named) is { } r && FootPlayer.HasHolds(r))
                Hook(named);
            // where it stands now: its carrier's new form is standing round it
            else if (FootPlayer.CarrierAt(this, GlobalPosition, Ride.ParkedBox.Size, this, Ride.Kind) is { } found)
            {
                var b = GlobalTransform.Basis;
                Carrier = found.Key;
                _carrierSection = found.Section;
                _carrierPos = found.Frame.AffineInverse() * GlobalPosition;
                _carrierYaw = Mathf.Wrap(Mathf.Atan2(b.Z.X, b.Z.Z) - Mathf.Atan2(found.Frame.Basis.Z.X, found.Frame.Basis.Z.Z), -Mathf.Pi, Mathf.Pi);
                Hook(found.Host);
            }
            if (_carrierHost == null) return;
        }
        var frame = FootPlayer.HoldFrame(_carrierHost, _carrierSection);
        GlobalTransform = frame * new Transform3D(new Basis(Vector3.Up, _carrierYaw), _carrierPos);
        Velocity = Vector3.Zero;
        Posed = true;
        // its place on the wire follows, for the server's interest and a later claim
        if (IsMultiplayerAuthority()) _place.Publish(GlobalPosition);
    }

    /// <summary>The carrier's bodies and this one ignore each other: it stands inside the carrier's hull.</summary>
    private void Hook(Node3D host)
    {
        _carrierHost = host;
        // the carrier's own rays (its ground under its wheels) go past this one
        if (host is FootPlayer driver) driver.Cargo(this, true);
        else if (host is VehicleBody parked) parked.Cargo(this, true);
        foreach (var body in FootPlayer.CarrierBodies(host))
        {
            AddCollisionExceptionWith(body);
            body.AddCollisionExceptionWith(this);
            _carrierBodies.Add(body);
        }
    }

    private void Unhook()
    {
        foreach (var body in _carrierBodies)
            if (IsInstanceValid(body))
            {
                RemoveCollisionExceptionWith(body);
                body.RemoveCollisionExceptionWith(this);
            }
        _carrierBodies.Clear();
        if (_carrierHost is FootPlayer driver && IsInstanceValid(driver)) driver.Cargo(this, false);
        else if (_carrierHost is VehicleBody parked && IsInstanceValid(parked)) parked.Cargo(this, false);
        _carrierHost = null;
    }

    /// <summary>Parked in a hold, for <see cref="Capture"/>: its carrier now and its spot in it.</summary>
    private (string Key, int Section, Vector3 Pos, float Yaw) HoldPlace =>
        _inHold ? (Carrier, _carrierSection, _carrierPos, _carrierYaw) : ("", 0, Vector3.Zero, 0f);
}
