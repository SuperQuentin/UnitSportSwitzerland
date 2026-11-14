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
    /// <summary>Off the ground: a freighter's open ramp goes level with the hold floor (a drop), not down to the ground (#420).</summary>
    public bool Airborne;

    // the cockpit (#421): the levers as the pilot set them, the switches, the warnings
    /// <summary>Thrust levers 0..1; flap lever setting; speedbrake lever 0/1/2; engines running (whole).</summary>
    public float Lever;
    public int FlapLever, SpeedbrakeLever, Lit;
    public bool Reverse, ParkingBrake, GearLever, GearBroken, Power, Autopilot;
    /// <summary>The red warning on now: 0 none, 1 stall, 2 overspeed, 3 gear not down.</summary>
    public int Warning;
    /// <summary>Fuel aboard, share of the tanks.</summary>
    public float Fuel;

    // the shuttle (#1492)
    /// <summary>Re-entry's glow, 0 none .. 1 white hot: the plasma round the belly and the nose.</summary>
    public float Heat;
    /// <summary>The cruise drive on: its glow instead of the rockets' plumes.</summary>
    public bool Drive;
    /// <summary>The belly jets' thrust, 0..1 (#1851): their plumes down from under the belly. Not in the pose yet (C3b).</summary>
    public float Belly;
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
    private readonly List<(int Door, Node3D Node, Vector3 Axis, float Angle, float AirAngle)> _doorParts = new();
    /// <summary>0 on the ground .. 1 in the air: between each door part's open angles (#420).</summary>
    private float _air;
    private readonly float[] _doorRate = { 0.4f, 0.4f, 0.4f, 0.4f };
    private Node3D? _navL, _navR, _navTail, _beaconTop, _beaconBottom, _strobeL, _strobeR, _landingL, _landingR;
    private float _stowL, _stowR, _stowNose;
    /// <summary>Main legs that rise straight up to stow (the freighter's, into its sponsons), metres; 0 when they fold.</summary>
    private float _gearLift;
    private Vector3 _gearLDown, _gearRDown;
    /// <summary>The AN-124's kneeling (#419): its door bit, how far the frame comes down, the parts whose open angle it changes.</summary>
    private int _kneelDoor = -1;
    private float _kneelDrop;
    private Vector3 _noseDown;
    private readonly List<(int Door, Node3D Node, Vector3 Axis, float Angle, float Kneel)> _kneelParts = new();

    // the shuttle (#1492): elevons, the split rudder as the speedbrake, the body flap, the legs swinging
    // forward under doors, the rockets' plumes, the drive's glow, re-entry's plasma
    private bool _shuttle;
    private Node3D? _brakeL, _brakeR, _bodyFlap, _driveGlow, _driveToggle;
    private readonly List<(Node3D Node, float Angle)> _gearDoors = new();
    private MeshInstance3D?[] _plumes = System.Array.Empty<MeshInstance3D?>();
    /// <summary>The belly jets' plumes (#1851): the main plume's mesh again, four under the belly, pointing down.</summary>
    private readonly System.Collections.Generic.List<MeshInstance3D> _bellyPlumes = new();
    private float _bellyShown = -1f;

    /// <summary>Where the belly jets fire, model space before the flip: either side of the keel, fore and aft of the bay's middle.</summary>
    private static readonly Vector3[] BellyJets =
    {
        new(1.3f, ShuttleLayout.BellyY - 0.05f, 15f), new(-1.3f, ShuttleLayout.BellyY - 0.05f, 15f),
        new(1.3f, ShuttleLayout.BellyY - 0.05f, 3f), new(-1.3f, ShuttleLayout.BellyY - 0.05f, 3f),
    };
    private MeshInstance3D? _plasma;
    private ShaderMaterial? _plumeMat, _plasmaMat;
    /// <summary>What the effects show now: changed (and their shader parameter set) only when the look moves past a step.</summary>
    private float _plumeShown = -1f, _heatShown = -1f, _gearDoorsShown = -1f;
    private int _driveShown = -1;
    private static readonly StringName PowerParam = "power", HeatParam = "heat";

    // where the parts are now, eased toward the look
    private float _gear = 1f, _flaps, _spoilers, _fanSpin, _clock;
    private Vector2 _stick;
    private readonly float[] _doorAt = new float[MaxDoors];
    private bool _fresh = true;

    private const float Deg = Mathf.Pi / 180f;

    /// <summary>The captain's eye, the rig's frame (−Z forward): where the cockpit camera sits (#421).</summary>
    public Transform3D EyeFrame { get; private set; }

    /// <summary>The flight deck's live controls, pilot and instruments (#421).</summary>
    public AircraftCockpit? Cockpit { get; private set; }

    private void AddCockpit(Node3D model, CockpitLayout layout)
    {
        Cockpit = new AircraftCockpit(model, layout, _spec);
        AddChild(Cockpit);
    }

    /// <summary>The pilots' seats' recline (the decks' <c>SeatAnchor</c>s).</summary>
    public const float PilotRecline = 0.2f;

    private void SetEye(Vector3 hipAuthored) =>
        EyeFrame = new Transform3D(Basis.Identity, AircraftMeshBuilder.Flip(HumanMeshBuilder.DriverEye(hipAuthored, PilotRecline)));

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
        rig.SetEye(A320Layout.CaptainHip);
        rig.AddCockpit(model, CockpitLayout.A320);
        for (int i = 0; i < A320Layout.DoorCount; i++)
            if (model.GetNodeOrNull<Node3D>($"Door{i}") is { } door) rig._doorParts.Add((i, door, Vector3.Up, A320MeshBuilder.DoorOpenAngle(i), A320MeshBuilder.DoorOpenAngle(i)));
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
        rig.SetEye(FreighterLayout.CaptainHip);
        rig.AddCockpit(model, CockpitLayout.Freighter);
        for (int i = 0; i < FreighterLayout.DoorCount; i++)
        {
            rig._doorRate[i] = FreighterMeshBuilder.DoorRate(i);
            foreach (var (name, axis, angle, air) in FreighterMeshBuilder.DoorMotions(i))
                if (model.GetNodeOrNull<Node3D>(name) is { } part) rig._doorParts.Add((i, part, axis, angle, air));
        }
        return rig;
    }

    /// <summary>
    /// The AN-124 (#419): four fans, mains rising into the fairings, the visor and nose ramp, the rear
    /// ramp and doors, and kneeling: the frame comes down (<c>Airliner.PoseShift</c>), the legs rise
    /// in it by as much so the wheels stay on the ground, and the ramps open less.
    /// </summary>
    public static AirlinerRig CreateAn124()
    {
        var rig = new AirlinerRig { Name = "An124", _spec = AirlinerCatalog.An124 };
        var model = An124MeshBuilder.Build();
        model.Name = "Model";
        rig.AddChild(model);
        rig.Find(model, An124Layout.EngineX.Length);
        rig._fanSign = new[] { 1f, 1f, 1f, 1f };
        rig._gearLift = An124MeshBuilder.GearLift;
        rig._gearLDown = rig._gearL?.Position ?? Vector3.Zero;
        rig._gearRDown = rig._gearR?.Position ?? Vector3.Zero;
        rig._noseDown = rig._gearNose?.Position ?? Vector3.Zero;
        rig._stowNose = An124MeshBuilder.NoseStow;
        rig.SetEye(An124Layout.PilotHip);
        rig.AddCockpit(model, CockpitLayout.An124);
        rig._kneelDoor = An124Layout.KneelDoor;
        rig._kneelDrop = An124Layout.KneelDrop;
        for (int i = 0; i < An124Layout.DoorCount; i++)
        {
            rig._doorRate[i] = An124MeshBuilder.DoorRate(i);
            foreach (var (name, axis, angle, kneel) in An124MeshBuilder.DoorMotions(i))
                if (model.GetNodeOrNull<Node3D>(name) is { } part) rig._kneelParts.Add((i, part, axis, angle, kneel));
        }
        return rig;
    }

    /// <summary>
    /// The space shuttle orbiter (#1492): elevons (inboard <c>Elevator</c>, outboard <c>Aileron</c>, each
    /// pitching and rolling), the split rudder that splays open as the speedbrake, the body flap trailing
    /// the elevons, the legs swinging forward into the belly under their doors, the side hatch; the main
    /// engines' plumes by the spool, the drive's glow when it is on (no plumes then), re-entry's plasma by
    /// the heat.
    /// </summary>
    public static AirlinerRig CreateShuttle()
    {
        var rig = new AirlinerRig { Name = "Shuttle", _spec = AirlinerCatalog.Shuttle, _shuttle = true };
        var model = ShuttleMeshBuilder.Build();
        model.Name = "Model";
        rig.AddChild(model);
        rig.Find(model, 0);
        rig._stowL = rig._stowR = ShuttleLayout.MainStow;
        rig._stowNose = ShuttleLayout.NoseStow;
        rig._brakeL = model.GetNodeOrNull<Node3D>("Rudder/SpoilerL");
        rig._brakeR = model.GetNodeOrNull<Node3D>("Rudder/SpoilerR");
        rig._bodyFlap = model.GetNodeOrNull<Node3D>("BodyFlap");
        rig._driveGlow = model.GetNodeOrNull<Node3D>("DriveGlow");
        rig._driveToggle = model.GetNodeOrNull<Node3D>("DriveSwitch/Toggle");
        foreach (var (name, angle) in ShuttleMeshBuilder.GearDoors)
            if (model.GetNodeOrNull<Node3D>(name) is { } door) rig._gearDoors.Add((door, angle));
        rig._plumes = new MeshInstance3D?[ShuttleLayout.Engines.Length];
        for (int i = 0; i < rig._plumes.Length; i++) rig._plumes[i] = model.GetNodeOrNull<MeshInstance3D>($"Plume{i}");
        rig._plumeMat = rig._plumes[0]?.MaterialOverride as ShaderMaterial;
        if (rig._plumes[0] is { } main)
            foreach (var at in BellyJets)
            {
                // the same mesh and material, turned so its length (+Z) points down
                var jet = new MeshInstance3D
                {
                    Name = "BellyJet", Mesh = main.Mesh, MaterialOverride = main.MaterialOverride, Visible = false,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = AircraftMeshBuilder.Flip(at),
                    Rotation = new Vector3(Mathf.Pi / 2, 0f, 0f),
                };
                model.AddChild(jet);
                rig._bellyPlumes.Add(jet);
            }
        rig._plasma = model.GetNodeOrNull<MeshInstance3D>("Plasma");
        rig._plasmaMat = rig._plasma?.MaterialOverride as ShaderMaterial;
        rig.SetEye(ShuttleLayout.CommanderHip);
        rig.AddCockpit(model, CockpitLayout.Shuttle);
        if (model.GetNodeOrNull<Node3D>("Door0") is { } hatch)
            rig._doorParts.Add((0, hatch, Vector3.Back, ShuttleMeshBuilder.HatchOpenAngle, ShuttleMeshBuilder.HatchOpenAngle));
        return rig;
    }

    /// <summary>The shuttle's own parts: the elevons, the speedbrake, the body flap, the gear doors, the effects.</summary>
    private void ShowShuttle(in AirlinerLook look)
    {
        // elevons: together for pitch, apart for roll (the outboard pair more); the body flap trims after them
        float pitch = -_stick.Y * 18f * Deg, roll = _stick.X * 10f * Deg;
        Hinge(_elevatorL, pitch + roll); Hinge(_elevatorR, pitch - roll);
        Hinge(_aileronL, pitch + roll * 1.6f); Hinge(_aileronR, pitch - roll * 1.6f);
        Hinge(_bodyFlap, pitch * 0.6f);
        // the speedbrake: the rudder's two halves splay apart about its hinge, each its own way
        float splay = _spoilers * ShuttleMeshBuilder.SpeedbrakeAngle;
        if (_brakeL != null) _brakeL.Basis = new Basis(Vector3.Up, -splay);
        if (_brakeR != null) _brakeR.Basis = new Basis(Vector3.Up, splay);

        // the gear doors open first and stay open while the gear is down
        float doors = Mathf.Clamp(_gear * 4f, 0f, 1f);
        if (doors != _gearDoorsShown)
        {
            _gearDoorsShown = doors;
            foreach (var (node, angle) in _gearDoors) node.Basis = new Basis(Vector3.Back, doors * angle);
        }

        // the main engines' plumes by the spool, none with the drive on; the drive's glow and its switch
        float spool = look.Drive ? 0f : Mathf.Clamp((look.Spool - 0.08f) / 0.92f, 0f, 1f);
        if (Mathf.Abs(spool - _plumeShown) > 0.004f || spool == 0f != (_plumeShown == 0f))
        {
            _plumeShown = spool;
            bool on = spool > 0f;
            var scale = new Vector3(ShuttleMeshBuilder.PlumeWidth(spool), ShuttleMeshBuilder.PlumeWidth(spool), ShuttleMeshBuilder.PlumeLength(spool));
            foreach (var plume in _plumes)
                if (plume != null)
                {
                    plume.Visible = on;
                    plume.Scale = scale;
                }
            _plumeMat?.SetShaderParameter(PowerParam, 0.35f + 0.65f * spool);
        }
        // the belly jets: shorter, narrower plumes down, by their lever
        float belly = Mathf.Clamp(look.Belly, 0f, 1f);
        if (Mathf.Abs(belly - _bellyShown) > 0.004f || belly == 0f != (_bellyShown == 0f))
        {
            _bellyShown = belly;
            bool on = belly > 0.02f;
            var scale = new Vector3(ShuttleMeshBuilder.PlumeWidth(belly) * 0.55f, ShuttleMeshBuilder.PlumeWidth(belly) * 0.55f, ShuttleMeshBuilder.PlumeLength(belly) * 0.3f);
            foreach (var jet in _bellyPlumes)
            {
                jet.Visible = on;
                jet.Scale = scale;
            }
            if (on && _plumeShown <= 0f) _plumeMat?.SetShaderParameter(PowerParam, 0.35f + 0.65f * belly);
        }
        int drive = look.Drive ? 1 : 0;
        if (drive != _driveShown)
        {
            _driveShown = drive;
            if (_driveGlow != null) _driveGlow.Visible = look.Drive;
            // thrown forward (node −Z) when on, back when off
            if (_driveToggle != null) _driveToggle.Basis = new Basis(Vector3.Right, look.Drive ? -0.45f : 0.45f);
        }

        // re-entry: the plasma fades in with the heat, nothing at all without it
        float heat = Mathf.Clamp(look.Heat, 0f, 1f);
        if (Mathf.Abs(heat - _heatShown) > 0.005f || heat < 0.01f != _heatShown < 0.01f)
        {
            _heatShown = heat;
            if (_plasma != null) _plasma.Visible = heat >= 0.01f;
            _plasmaMat?.SetShaderParameter(HeatParam, heat);
        }
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
            _air = look.Airborne ? 1f : 0f;
        }
        else
        {
            _gear = Mathf.MoveToward(_gear, look.Gear, dt / _spec.GearTransit * 1.05f);
            _flaps = Mathf.MoveToward(_flaps, look.Flaps, _spec.FlapRate * 1.05f * dt);
            _spoilers = Mathf.MoveToward(_spoilers, look.Spoilers, 1.6f * dt);
            for (int i = 0; i < _doorAt.Length; i++)
                _doorAt[i] = Mathf.MoveToward(_doorAt[i], (look.Doors >> i & 1) != 0 ? 1f : 0f, _doorRate[i] * dt);
            _air = Mathf.MoveToward(_air, look.Airborne ? 1f : 0f, 0.25f * dt);
        }
        _stick = _stick.MoveToward(look.Stick, 3f * dt);

        if (_shuttle) ShowShuttle(look);
        else
        {
            float flap = FlapAngle(_flaps);
            Hinge(_flapInL, flap); Hinge(_flapInR, flap); Hinge(_flapOutL, flap); Hinge(_flapOutR, flap);
            // a right roll: the right aileron's trailing edge up, the left's down
            Hinge(_aileronL, _stick.X * 20f * Deg); Hinge(_aileronR, -_stick.X * 20f * Deg);
            Hinge(_elevatorL, -_stick.Y * 25f * Deg); Hinge(_elevatorR, -_stick.Y * 25f * Deg);
            Hinge(_spoilerL, -_spoilers * 45f * Deg); Hinge(_spoilerR, -_spoilers * 45f * Deg);
        }
        if (_rudder != null) _rudder.Basis = RestOf(_rudder) * new Basis(Vector3.Up, _stick.X * 12f * Deg);   // about the fin's own hinge line (#755)

        float stow = 1f - _gear;
        float kneelAt = _kneelDoor >= 0 ? _doorAt[_kneelDoor] : 0f, kneel = kneelAt * _kneelDrop;
        if (_shuttle)
        {
            // the orbiter's mains swing forward into the wing's wells, like its nose leg
            if (_gearL != null) _gearL.Rotation = new Vector3(stow * _stowL, 0, 0);
            if (_gearR != null) _gearR.Rotation = new Vector3(stow * _stowR, 0, 0);
        }
        else if (_gearLift > 0f)
        {
            if (_gearL != null) _gearL.Position = _gearLDown + Vector3.Up * (stow * _gearLift + kneel);
            if (_gearR != null) _gearR.Position = _gearRDown + Vector3.Up * (stow * _gearLift + kneel);
        }
        else
        {
            if (_gearL != null) _gearL.Rotation = new Vector3(0, 0, stow * _stowL);
            if (_gearR != null) _gearR.Rotation = new Vector3(0, 0, stow * _stowR);
        }
        if (_gearNose != null) _gearNose.Rotation = new Vector3(stow * _stowNose, 0, 0);
        if (_kneelDoor >= 0 && _gearNose != null) _gearNose.Position = _noseDown + Vector3.Up * kneel;

        _fanSpin = Mathf.Wrap(_fanSpin + look.Spool * 30f * dt, 0f, Mathf.Tau);
        for (int i = 0; i < _fans.Length; i++)
            if (_fans[i] is { } fan) fan.Rotation = new Vector3(0, 0, _fanSign[i] * _fanSpin);

        foreach (var (door, node, axis, angle, air) in _doorParts)
            node.Basis = new Basis(axis, _doorAt[door] * Mathf.Lerp(angle, air, _air));
        foreach (var (door, node, axis, angle, k) in _kneelParts)
            node.Basis = new Basis(axis, _doorAt[door] * (angle + kneelAt * k));

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
        Cockpit?.Show(look, _flaps, _gear, _stick, dt);
    }

    /// <summary>Puts every part where <paramref name="look"/> has it at once, as the first <see cref="Show"/> does.</summary>
    public void Snap(in AirlinerLook look)
    {
        _fresh = true;
        Show(look, 0f);
    }

    /// <summary>Door leaves open now, 0..1, for the deck (#416): a leaf half open is not a way in yet.</summary>
    public float DoorOpen(int door) => door >= 0 && door < _doorAt.Length ? _doorAt[door] : 0f;

    /// <summary>The hinged parts' own bases as authored (turned to their swept hinge line), by node (#755).</summary>
    private readonly Dictionary<Node3D, Basis> _rest = new();

    private Basis RestOf(Node3D node)
    {
        if (!_rest.TryGetValue(node, out var rest)) _rest[node] = rest = node.Basis;
        return rest;
    }

    // Turning about the part's own X (its hinge line). Setting Rotation instead dropped the authored
    // basis, so every swept surface sat yawed off the wing it belongs to (#755).
    private void Hinge(Node3D? node, float angle)
    {
        if (node != null) node.Basis = RestOf(node) * new Basis(Vector3.Right, angle);
    }

    private static void Lit(Node3D? node, bool on)
    {
        if (node != null && node.Visible != on) node.Visible = on;
    }
}
