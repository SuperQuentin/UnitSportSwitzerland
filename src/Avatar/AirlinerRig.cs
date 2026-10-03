using Godot;
using UnitSport.Player;

namespace UnitSport.Avatar;

/// <summary>
/// What an airliner's moving parts show (#414): the owner passes where they are, a remote copy and a
/// parked one where they are going (lever positions from the replicated pose); the rig moves them at
/// the aircraft's own rates either way, so both see the same travel.
/// </summary>
public struct AirlinerLook
{
    /// <summary>Gear 0 up .. 1 down; flaps in settings; spoilers 0..1; door leaves 0..1, one per door.</summary>
    public float Gear, Flaps, Spoilers;
    public Vector2 Stick;
    public float Spool;
    public AirlinerLights Lights;
    public byte Doors;
}

[System.Flags]
public enum AirlinerLights : byte { None = 0, Nav = 1, Beacon = 2, Strobe = 4, Landing = 8 }

/// <summary>
/// The drawn airliner (#414): the model a builder made (<see cref="A320MeshBuilder"/>) under it, its
/// hinge nodes found by name once, and <see cref="Show"/> moving them each frame. Origin on the ground
/// between the main wheels, −Z forward, like the body that carries it.
/// </summary>
public partial class AirlinerRig : Node3D
{
    private AirlinerSpec _spec = AirlinerCatalog.A320;
    private Node3D? _flapInL, _flapInR, _flapOutL, _flapOutR, _aileronL, _aileronR, _spoilerL, _spoilerR,
        _elevatorL, _elevatorR, _rudder, _gearL, _gearR, _gearNose, _fan0, _fan1;
    private readonly Node3D?[] _doors = new Node3D?[A320Layout.DoorCount];
    private Node3D? _navL, _navR, _navTail, _beaconTop, _beaconBottom, _strobeL, _strobeR, _landingL, _landingR;
    private float _stowL, _stowR, _stowNose;
    private readonly float[] _doorOpen = new float[A320Layout.DoorCount];

    // where the parts are now, eased toward the look
    private float _gear = 1f, _flaps, _spoilers, _fanSpin, _clock;
    private Vector2 _stick;
    private readonly float[] _doorAt = new float[A320Layout.DoorCount];
    private bool _fresh = true;

    private const float Deg = Mathf.Pi / 180f;

    public static AirlinerRig CreateA320(Color tail)
    {
        var rig = new AirlinerRig { Name = "A320", _spec = AirlinerCatalog.A320 };
        var model = A320MeshBuilder.Build(tail);
        model.Name = "Model";
        rig.AddChild(model);
        rig.Find(model);
        rig._stowL = A320MeshBuilder.GearStowAngle("GearMainL");
        rig._stowR = A320MeshBuilder.GearStowAngle("GearMainR");
        rig._stowNose = A320MeshBuilder.GearStowAngle("GearNose");
        for (int i = 0; i < A320Layout.DoorCount; i++) rig._doorOpen[i] = A320MeshBuilder.DoorOpenAngle(i);
        return rig;
    }

    private void Find(Node3D model)
    {
        Node3D? N(string name) => model.GetNodeOrNull<Node3D>(name);
        _flapInL = N("FlapInL"); _flapInR = N("FlapInR"); _flapOutL = N("FlapOutL"); _flapOutR = N("FlapOutR");
        _aileronL = N("AileronL"); _aileronR = N("AileronR"); _spoilerL = N("SpoilerL"); _spoilerR = N("SpoilerR");
        _elevatorL = N("ElevatorL"); _elevatorR = N("ElevatorR"); _rudder = N("Rudder");
        _gearL = N("GearMainL"); _gearR = N("GearMainR"); _gearNose = N("GearNose");
        _fan0 = N("Fan0"); _fan1 = N("Fan1");
        for (int i = 0; i < _doors.Length; i++) _doors[i] = N($"Door{i}");
        _navL = N("NavL"); _navR = N("NavR"); _navTail = N("NavTail");
        _beaconTop = N("BeaconTop"); _beaconBottom = N("BeaconBottom");
        _strobeL = N("StrobeL"); _strobeR = N("StrobeR"); _landingL = N("LandingL"); _landingR = N("LandingR");
    }

    /// <summary>The flaps' angle at a position in settings (fractional while they travel).</summary>
    private float FlapAngle(float flaps)
    {
        var a = _spec.FlapAngles;
        float f = Mathf.Clamp(flaps, 0f, a.Length - 1);
        int i = Mathf.Min((int)f, a.Length - 2);
        return i < 0 ? a[0] : Mathf.Lerp(a[i], a[i + 1], f - i);
    }

    /// <summary>Moves the parts toward <paramref name="look"/> at the aircraft's rates; the first call snaps there.</summary>
    public void Show(in AirlinerLook look, float dt)
    {
        _clock += dt;
        if (_fresh)
        {
            _fresh = false;
            _gear = look.Gear;
            _flaps = look.Flaps;
            _spoilers = look.Spoilers;
            for (int i = 0; i < _doorAt.Length; i++) _doorAt[i] = (look.Doors >> i & 1) != 0 ? 1f : 0f;
        }
        else
        {
            _gear = Mathf.MoveToward(_gear, look.Gear, dt / _spec.GearTransit * 1.05f);
            _flaps = Mathf.MoveToward(_flaps, look.Flaps, _spec.FlapRate * 1.05f * dt);
            _spoilers = Mathf.MoveToward(_spoilers, look.Spoilers, 1.6f * dt);
            for (int i = 0; i < _doorAt.Length; i++)
                _doorAt[i] = Mathf.MoveToward(_doorAt[i], (look.Doors >> i & 1) != 0 ? 1f : 0f, 0.4f * dt);
        }
        _stick = _stick.MoveToward(look.Stick, 3f * dt);

        float flap = FlapAngle(_flaps);
        Hinge(_flapInL, flap); Hinge(_flapInR, flap); Hinge(_flapOutL, flap); Hinge(_flapOutR, flap);
        // a right roll: the right aileron's trailing edge up, the left's down
        Hinge(_aileronL, _stick.X * 20f * Deg); Hinge(_aileronR, -_stick.X * 20f * Deg);
        Hinge(_elevatorL, -_stick.Y * 25f * Deg); Hinge(_elevatorR, -_stick.Y * 25f * Deg);
        Hinge(_spoilerL, -_spoilers * 45f * Deg); Hinge(_spoilerR, -_spoilers * 45f * Deg);
        if (_rudder != null) _rudder.Rotation = new Vector3(0, _stick.X * 12f * Deg, 0);

        float stow = 1f - _gear;
        if (_gearL != null) _gearL.Rotation = new Vector3(0, 0, stow * _stowL);
        if (_gearR != null) _gearR.Rotation = new Vector3(0, 0, stow * _stowR);
        if (_gearNose != null) _gearNose.Rotation = new Vector3(stow * _stowNose, 0, 0);

        _fanSpin = Mathf.Wrap(_fanSpin + look.Spool * 30f * dt, 0f, Mathf.Tau);
        if (_fan0 != null) _fan0.Rotation = new Vector3(0, 0, _fanSpin);
        if (_fan1 != null) _fan1.Rotation = new Vector3(0, 0, -_fanSpin);

        for (int i = 0; i < _doors.Length; i++)
            if (_doors[i] is { } door) door.Rotation = new Vector3(0, _doorAt[i] * _doorOpen[i], 0);

        bool nav = (look.Lights & AirlinerLights.Nav) != 0;
        Lit(_navL, nav); Lit(_navR, nav); Lit(_navTail, nav);
        // the beacon a short red flash a second; the strobes a double white flash every 1.2 s
        bool beacon = (look.Lights & AirlinerLights.Beacon) != 0 && Mathf.PosMod(_clock, 1f) < 0.12f;
        Lit(_beaconTop, beacon); Lit(_beaconBottom, beacon);
        float s = Mathf.PosMod(_clock, 1.2f);
        bool strobe = (look.Lights & AirlinerLights.Strobe) != 0 && (s < 0.05f || s is > 0.15f and < 0.2f);
        Lit(_strobeL, strobe); Lit(_strobeR, strobe);
        bool landing = (look.Lights & AirlinerLights.Landing) != 0;
        Lit(_landingL, landing); Lit(_landingR, landing);
    }

    /// <summary>Door leaves open now, 0..1, for the deck (#416): a leaf half open is not a way in yet.</summary>
    public float DoorOpen(int door) => door >= 0 && door < _doorAt.Length ? _doorAt[door] : 0f;

    private static void Hinge(Node3D? node, float angle)
    {
        if (node != null) node.Rotation = new Vector3(angle, 0, 0);
    }

    private static void Lit(Node3D? node, bool on)
    {
        if (node != null && node.Visible != on) node.Visible = on;
    }
}
