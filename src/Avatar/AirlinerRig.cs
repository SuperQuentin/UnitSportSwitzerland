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
    /// <summary>Doors an airliner can have: one bit each in its pose and flags (bits 13-16).</summary>
    public const int MaxDoors = 4;

    private AirlinerSpec _spec = AirlinerCatalog.A320;
    private Node3D? _flapInL, _flapInR, _flapOutL, _flapOutR, _aileronL, _aileronR, _spoilerL, _spoilerR,
        _elevatorL, _elevatorR, _rudder, _gearL, _gearR, _gearNose;
    private Node3D?[] _fans = System.Array.Empty<Node3D?>();
    private float[] _fanSign = System.Array.Empty<float>();
    /// <summary>What each door moves: its hinge nodes, each about a local axis by an angle (fully open).</summary>
    private readonly List<(int Door, Node3D Node, Vector3 Axis, float Angle)> _doorParts = new();
    private readonly float[] _doorRate = { 0.4f, 0.4f, 0.4f, 0.4f };
    private Node3D? _navL, _navR, _navTail, _beaconTop, _beaconBottom, _strobeL, _strobeR, _landingL, _landingR;
    private float _stowL, _stowR, _stowNose;
    /// <summary>Main legs that rise straight up to stow (the freighter's, into its sponsons), metres; 0 when they fold.</summary>
    private float _gearLift;
    private Vector3 _gearLDown, _gearRDown;

    // where the parts are now, eased toward the look
    private float _gear = 1f, _flaps, _spoilers, _fanSpin, _clock;
    private Vector2 _stick;
    private readonly float[] _doorAt = new float[MaxDoors];
    private bool _fresh = true;

    private const float Deg = Mathf.Pi / 180f;

    public static AirlinerRig CreateA320(Color tail)
    {
        var rig = new AirlinerRig { Name = "A320", _spec = AirlinerCatalog.A320 };
        var model = A320MeshBuilder.Build(tail);
        model.Name = "Model";
        rig.AddChild(model);
        rig.Find(model, 2);
        rig._fanSign = new[] { 1f, -1f };
        rig._stowL = A320MeshBuilder.GearStowAngle("GearMainL");
        rig._stowR = A320MeshBuilder.GearStowAngle("GearMainR");
        rig._stowNose = A320MeshBuilder.GearStowAngle("GearNose");
        for (int i = 0; i < A320Layout.DoorCount; i++)
            if (model.GetNodeOrNull<Node3D>($"Door{i}") is { } door) rig._doorParts.Add((i, door, Vector3.Up, A320MeshBuilder.DoorOpenAngle(i)));
        return rig;
    }

    /// <summary>The military cargo plane (#420): four propellers, mains rising into the sponsons, the ramp and its upper door.</summary>
    public static AirlinerRig CreateFreighter()
    {
        var rig = new AirlinerRig { Name = "Freighter", _spec = AirlinerCatalog.Freighter };
        var model = FreighterMeshBuilder.Build();
        model.Name = "Model";
        rig.AddChild(model);
        rig.Find(model, FreighterLayout.EngineX.Length);
        rig._fanSign = new[] { 1f, 1f, 1f, 1f };
        rig._gearLift = FreighterMeshBuilder.GearLift;
        rig._gearLDown = rig._gearL?.Position ?? Vector3.Zero;
        rig._gearRDown = rig._gearR?.Position ?? Vector3.Zero;
        rig._stowNose = FreighterMeshBuilder.NoseStowAngle;
        for (int i = 0; i < FreighterLayout.DoorCount; i++)
        {
            rig._doorRate[i] = FreighterMeshBuilder.DoorRate(i);
            foreach (var (name, axis, angle) in FreighterMeshBuilder.DoorMotions(i))
                if (model.GetNodeOrNull<Node3D>(name) is { } part) rig._doorParts.Add((i, part, axis, angle));
        }
        return rig;
    }

    private void Find(Node3D model, int fans)
    {
        Node3D? N(string name) => model.GetNodeOrNull<Node3D>(name);
        _flapInL = N("FlapInL"); _flapInR = N("FlapInR"); _flapOutL = N("FlapOutL"); _flapOutR = N("FlapOutR");
        _aileronL = N("AileronL"); _aileronR = N("AileronR"); _spoilerL = N("SpoilerL"); _spoilerR = N("SpoilerR");
        _elevatorL = N("ElevatorL"); _elevatorR = N("ElevatorR"); _rudder = N("Rudder");
        _gearL = N("GearMainL"); _gearR = N("GearMainR"); _gearNose = N("GearNose");
        _fans = new Node3D?[fans];
        for (int i = 0; i < fans; i++) _fans[i] = N($"Fan{i}");
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
                _doorAt[i] = Mathf.MoveToward(_doorAt[i], (look.Doors >> i & 1) != 0 ? 1f : 0f, _doorRate[i] * dt);
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
        if (_gearLift > 0f)
        {
            if (_gearL != null) _gearL.Position = _gearLDown + Vector3.Up * stow * _gearLift;
            if (_gearR != null) _gearR.Position = _gearRDown + Vector3.Up * stow * _gearLift;
        }
        else
        {
            if (_gearL != null) _gearL.Rotation = new Vector3(0, 0, stow * _stowL);
            if (_gearR != null) _gearR.Rotation = new Vector3(0, 0, stow * _stowR);
        }
        if (_gearNose != null) _gearNose.Rotation = new Vector3(stow * _stowNose, 0, 0);

        _fanSpin = Mathf.Wrap(_fanSpin + look.Spool * 30f * dt, 0f, Mathf.Tau);
        for (int i = 0; i < _fans.Length; i++)
            if (_fans[i] is { } fan) fan.Rotation = new Vector3(0, 0, _fanSign[i] * _fanSpin);

        foreach (var (door, node, axis, angle) in _doorParts)
            node.Basis = new Basis(axis, _doorAt[door] * angle);

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
