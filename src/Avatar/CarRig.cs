using Godot;
using UnitSport.Core;

namespace UnitSport.Avatar;

/// <summary>The silhouette: where the glass is and how the roof runs into the tail.</summary>
public enum BodyShape
{
    /// <summary>Three-door hatchback, roof carried back to a near-vertical hatch (AE86 hatch, Civic).</summary>
    Hatchback,
    /// <summary>Two-door notchback: short roof, separate boot (AE86 coupe, S13, Integra coupe).</summary>
    Coupe,
    /// <summary>Long raked glass hatch down to a short tail (RX-7s, 180SX, Supra, GT-R R32 is a Coupe).</summary>
    Fastback,
    /// <summary>Four doors and a boot (Impreza, Lancer Evolution).</summary>
    Sedan,
    /// <summary>Open two-seater, no roof (Roadster, S2000).</summary>
    Roadster,
    /// <summary>Mid-engined: cab forward, long rear deck (MR2, NSX).</summary>
    Midship,
    /// <summary>A rental go-kart (#715): a tube frame, no body to speak of, built by <see cref="KartMeshBuilder"/> and not by <see cref="CarMeshBuilder"/>.</summary>
    Kart,
}

/// <summary>A rear wing, from none to a GT wing on tall stands.</summary>
public enum WingSize { None, Lip, Small, Big, Gt }

/// <summary>
/// How the doors open: front-hinged and swinging out (every real car in the catalog), rear-hinged
/// (suicide), up about the front hinge (scissor, the Lambo), up and out (butterfly), or up about
/// the roof (gull-wing). A four-door's rear doors only ever swing out, front- or rear-hinged.
/// </summary>
public enum DoorStyle { Conventional, Suicide, Scissor, Butterfly, GullWing }

/// <summary>
/// Everything a <see cref="CarRig"/> needs to draw one car: shape, real dimensions and livery.
/// Lengths in metres; colours authored in sRGB like every palette here.
/// </summary>
public sealed record CarBody
{
    public BodyShape Shape { get; init; } = BodyShape.Coupe;
    public float Length { get; init; } = 4.3f;
    public float Width { get; init; } = 1.7f;
    public float Height { get; init; } = 1.3f;
    public float WheelRadius { get; init; } = 0.3f;
    // ---- a preset's look (Player/CarSetup, #40); the defaults are the catalog car ----
    /// <summary>Body raised (+) or lowered (−) over the wheels, m.</summary>
    public float Lift { get; init; }
    /// <summary>Tyre tread: 0 smooth, 1 blocks, 2 big mud lugs.</summary>
    public int Tread { get; init; }
    /// <summary>A roof rack with a spare wheel and jerrycans.</summary>
    public bool RoofRack { get; init; }
    /// <summary>A tubular bumper bar over the nose.</summary>
    public bool BullBar { get; init; }
    public Color Paint { get; init; } = new(0.9f, 0.9f, 0.9f);
    /// <summary>Two-tone: the lower body, sills and bumpers (the AE86 "panda" black). Null = paint.</summary>
    public Color? Lower { get; init; }
    /// <summary>A bonnet in another colour (carbon, primer). Null = paint.</summary>
    public Color? Bonnet { get; init; }
    public Color Rim { get; init; } = new(0.78f, 0.79f, 0.82f);
    public bool PopUps { get; init; }
    public WingSize Wing { get; init; }
    public bool Scoop { get; init; }
    /// <summary>Lowrider hydraulics: the body hops on its wheels while <see cref="CarRig.Bouncing"/> (#464).</summary>
    public bool Hydraulics { get; init; }

    // ---- garage parts (Player/CarTuning); the defaults are the catalog look ----
    /// <summary>Under the front bumper: 0 nothing, 1 a lip, 2 a splitter.</summary>
    public int FrontAero { get; init; }
    public bool Diffuser { get; init; }
    public bool Skirts { get; init; }
    /// <summary>How much lower than stock the body sits, m; the wheels stay where they are.</summary>
    public float Drop { get; init; }
    /// <summary>Window glass. Null = the plain blue-grey glass.</summary>
    public Color? Glass { get; init; }
    /// <summary>Neon under the car. Null = none.</summary>
    public Color? Underglow { get; init; }
    public DoorStyle Doors { get; init; }
    /// <summary>Bigger rims in the same rolling radius (a thinner sidewall): 0 stock, 1, 2.</summary>
    public int RimSize { get; init; }
    /// <summary>Wide, low racing slicks.</summary>
    public bool Slicks { get; init; }
    /// <summary>A kart's race number, on its plates (1..99); 0 = none (#715).</summary>
    public int Number { get; init; }
}

/// <summary>What the local driver sees of their own figure from the seat (<see cref="CarRig.View"/>).</summary>
public enum CockpitView
{
    /// <summary>Not in the seat's eye: everything is drawn (every remote copy, every chase camera).</summary>
    Outside,
    /// <summary>First person with a body: arms on the wheel, legs on the pedals, no head in the lens.</summary>
    Body,
    /// <summary>First person without one: the wheel and the pedals alone.</summary>
    Bare,
}

/// <summary>
/// A drivable car's visual: body, four wheels that turn and steer, brake lights, headlights, doors
/// on hinges that swing open (<see cref="DoorsOpen"/>), and on the cars that have them a soft top
/// that folds and pop-up headlights. Inside, behind translucent glass, the cabin
/// (<see cref="CarCabin"/>): a steering wheel that turns with the front wheels, working dials,
/// a gear digit, warning lamps, pedals, and the driver at the wheel when there is one. Origin on the
/// ground under the middle of the wheelbase, facing −Z like every node (built +Z, turned by
/// <see cref="MeshScratch.Build()"/>). The owner sets the properties; the rig applies them in
/// <c>_Process</c>, the roof and the pods moving there over a moment rather than snapping —
/// except on the first frame, so a car built with its top down does not fold it in front of you.
/// </summary>
public partial class CarRig : Node3D, IHingedDoors
{
    /// <summary>Front road-wheel angle, radians, + = left.</summary>
    public float SteerAngle { get; set; }
    /// <summary>The steering wheel's turn, radians, + = anticlockwise from the seat (a left turn): the road-wheel angle times the steering ratio.</summary>
    public float WheelTurn { get; set; }
    /// <summary>Accumulated wheel rotation, radians; positive rolls forward.</summary>
    public float WheelSpin { get; set; }
    /// <summary>Body pitch, radians, + = nose up (squat under power, dive under braking is −).</summary>
    public float BodyPitch { get; set; }
    public bool BrakeLights { get; set; }
    /// <summary>Which doors are open, one bit each (<see cref="DoorLeft"/> .. <see cref="DoorRearRight"/>); each eases there.</summary>
    public byte DoorsOpen { get; set; }

    // ---- what the pedals, dials and lamps show ----
    /// <summary>Pedals, 0..1: they swing on their hinges and the driver's right foot presses them.</summary>
    public float Throttle { get; set; }
    public float Brake { get; set; }
    public bool Handbrake { get; set; }
    public float SpeedKmh { get; set; }
    public float Rpm { get; set; }
    /// <summary>1-based forward gear, −1 reverse, 0 neutral.</summary>
    public int Gear { get; set; } = 1;
    /// <summary>Off, the dials drop to zero and the engine lamp lights.</summary>
    public bool EngineRunning { get; set; } = true;

    /// <summary>The local driver's view from the seat: what of their own figure is drawn. Outside on every other copy.</summary>
    public CockpitView View { get; set; }
    /// <summary>Draw the mirrors (local driver, first person, the setting on): each a small extra render.</summary>
    public bool MirrorsOn { get; set; }
    /// <summary>Somebody at the wheel: off while it rolls on driverless with its passengers (#158).</summary>
    public bool DriverShown { get; set; } = true;
    /// <summary>The seats a player can take, the driver's first (#158).</summary>
    public SeatAnchor[] Seats => _cabin.Seats;

    public const byte DoorLeft = 1, DoorRight = 2, DoorRearLeft = 4, DoorRearRight = 8;
    /// <summary>These are right-hand-drive cars: the driver gets in and out on the right.</summary>
    public const byte DriverDoor = DoorRight;
    /// <summary>Seconds for a door to swing fully open or shut.</summary>
    private const float DoorTime = 0.4f;
    /// <summary>Headlights on. On a car with pop-ups this also raises them.</summary>
    public bool Headlights { get; set; }
    /// <summary>Soft top down (and side glass wound down). Nothing on a car with a fixed roof.</summary>
    public bool RoofOpen { get; set; }
    /// <summary>Hydraulics pumping: the body hops, nose and tail in turn, to its clip. Nothing on a car without them.</summary>
    public bool Bouncing { get; set; }
    /// <summary>
    /// A kart over on its side (#715): −1 .. 1, + onto its left side, 0 upright. The body and the wheels
    /// roll about the outer wheels' contact line; it eases there, and back, over a fraction of a second.
    /// </summary>
    public float Tip { get; set; }
    /// <summary>
    /// A kart's solid rear axle in a hard corner (#715): −1 .. 1, + = the left rear wheel lifts, − the
    /// right one, as a share of <see cref="InsideLift"/>. Nothing on a car with suspension.
    /// </summary>
    public float Lift { get; set; }

    /// <summary>Seconds for the top to fold and the pods to rise.</summary>
    private const float RoofTime = 2.2f, FlapTime = 0.6f;
    /// <summary>Of the roof's travel, where the side glass is fully down, and where the top starts to fold.</summary>
    private const float WindowsDown = 0.35f, FoldFrom = 0.25f;
    /// <summary>What is left of the top's height and length, folded behind the seats.</summary>
    private const float FoldedHeight = 0.22f, FoldedLength = 0.28f;
    /// <summary>What is left of a pod's height folded onto the nose: a lid about a centimetre thick.</summary>
    private const float FoldedPod = 0.08f;

    /// <summary>The shell hangs this far below the body's pitch pivot (roughly the centre of mass).</summary>
    private static readonly Vector3 ShellOffset = new(0, -0.5f, 0);

    private Node3D _body = null!;
    private CarDoor[] _doors = System.Array.Empty<CarDoor>();
    private Node3D[] _doorPivots = System.Array.Empty<Node3D>();
    private float[] _doorOpen = System.Array.Empty<float>();
    private readonly Node3D[] _steer = new Node3D[4];   // FL, FR, RL, RR
    private readonly Node3D[] _spin = new Node3D[4];
    private StandardMaterial3D _tailMaterial = null!, _headMaterial = null!;
    private Node3D? _top, _windows, _flaps, _flapLamps;
    private float _roof, _pods;   // 0 closed .. 1 open
    private bool _settled;
    private bool _hydraulics;
    private float _bodyY;
    /// <summary>Time into the hops, s, and how far into a bounce the hydraulics are (0 settled .. 1 full hops).</summary>
    private float _hopTime, _hop;
    private AudioStreamPlayer3D? _clip, _thud;
    private static readonly System.Random HopRng = new(464);
    /// <summary>One hop, s, its height at the body's pivot, m, and the nose-or-tail tilt that leads it, rad.</summary>
    private const float HopPeriod = 0.62f, HopHeight = 0.32f, HopTilt = 0.09f;
    /// <summary>A tipped kart lies this far over, rad; the inside rear wheel of a hard corner rises this far, m.</summary>
    private const float TipAngle = 1.45f, InsideLift = 0.045f;
    /// <summary>Seconds to go over, in the eased value's units per second.</summary>
    private const float TipSpeed = 5f;
    private float _tipShown;
    private bool _kartMoved;
    private readonly Vector3[] _wheelHome = new Vector3[4];
    private float _kartHalfWidth = 0.7f;
    /// <summary>The clip played while it bounces, if the file is there; the landings thud either way.</summary>
    public const string BounceClipRes = "res://assets/audio/yaris_bounce.ogg";

    private CarCabin _cabin = null!;
    private Node3D _wheel = null!, _tach = null!, _speedo = null!;
    private MeshInstance3D _digit = null!;
    private MeshInstance3D[] _lamps = System.Array.Empty<MeshInstance3D>();
    private Node3D[] _pedals = System.Array.Empty<Node3D>();
    private HumanPalette? _driverPalette;
    private MeshInstance3D? _driverBody, _driverHead;
    private (int Turn, int Throttle, int Brake, bool Smooth) _driverPose = (int.MinValue, 0, 0, false);
    private readonly Dictionary<(int Turn, int Throttle, int Brake, bool Smooth), ArrayMesh> _driverPoses = new();
    /// <summary>Whether the driver's head was built smooth (<see cref="HumanMeshBuilder.SmoothFigures"/>).</summary>
    private bool _driverSmooth;
    private float _rpmShown, _speedShown;
    private CabMirrors _mirrors = null!;
    private StandardMaterial3D _glass = null!;
    /// <summary>
    /// How much of the glass's tint the driver sees from the seat: a real windscreen is all but
    /// clear from inside, while from outside the same glass reads as glass only with a tint.
    /// </summary>
    private const float GlassFromSeat = 0.35f;

    /// <param name="gauges">The dials' full scales; the default suits a sports car.</param>
    /// <param name="driver">The figure at the wheel, in its colours; null for an empty car (parked, previewed).</param>
    public static CarRig Create(CarBody body, float wheelbase, CarGauges? gauges = null, HumanPalette? driver = null)
    {
        var rig = new CarRig { Name = "Car", _driverPalette = driver };
        rig.Assemble(CarMeshBuilder.Build(body, wheelbase, gauges));
        // a preset's ride height, tread and off-road kit: the body moves with everything on it,
        // the wheels stay on the road (they are the rig's own children)
        rig._body.Position += Vector3.Up * body.Lift;
        CarKit.Fit(rig._body, body, wheelbase, rig._spin);
        rig._bodyY = rig._body.Position.Y;
        rig._hydraulics = body.Hydraulics;
        rig._kartHalfWidth = body.Width * 0.5f;
        return rig;
    }

    /// <summary>Translucent, a little glossy: glass the cabin shows through and the sun glints off.</summary>
    public static StandardMaterial3D GlassMaterial() => new()
    {
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
        Roughness = 0.2f,
    };

    private void Assemble(CarParts p)
    {
        var body = HumanMeshBuilder.FigureMaterial();   // the driver wears clothes, maybe with a finish (#251)
        var glass = _glass = GlassMaterial();
        _headMaterial = TrafficMeshBuilder.LampMaterial();
        _tailMaterial = TrafficMeshBuilder.LampMaterial();
        MeshInstance3D Part(string name, ArrayMesh mesh, Vector3 at)
        {
            var part = new MeshInstance3D { Name = name, Mesh = mesh, Position = at };
            MeshScratch.Paint(part, body, glass);
            return part;
        }

        // pitch about the middle of the car at roughly the centre of mass height
        // (lowered: the whole body drops, the wheels stay on the road)
        _body = new Node3D { Name = "Body", Position = new Vector3(0, 0.5f - p.Drop, 0) };
        AddChild(_body);
        var offset = ShellOffset;
        _body.AddChild(Part("Shell", p.Body, offset));
        _body.AddChild(new MeshInstance3D { Name = "Headlamps", Mesh = p.Head, MaterialOverride = _headMaterial, Position = offset });
        _body.AddChild(new MeshInstance3D { Name = "Taillamps", Mesh = p.Tail, MaterialOverride = _tailMaterial, Position = offset });
        if (p.Glow is { } glow)
            // the neon strip is in the lamp mesh; this is the pool of its colour on the road
            _body.AddChild(new OmniLight3D
            {
                Name = "Underglow", LightColor = glow, LightEnergy = 1.5f, OmniRange = 2.6f,
                Position = new Vector3(0, -0.3f, 0),
            });

        // each door on its own hinge, named like the wheels (deterministic, the same on every peer)
        _doors = p.Doors;
        _doorPivots = new Node3D[_doors.Length];
        _doorOpen = new float[_doors.Length];
        for (int i = 0; i < _doors.Length; i++)
        {
            var pivot = new Node3D { Name = _doors[i].Name, Position = _doors[i].Hinge + offset };
            pivot.AddChild(Part("Panel", _doors[i].Mesh, Vector3.Zero));
            _body.AddChild(pivot);
            _doorPivots[i] = pivot;
        }
        if (p.Top is { } top)
        {
            _top = Part("SoftTop", top.Mesh, top.Pivot + offset);
            _windows = Part("SideGlass", p.Windows!.Mesh, p.Windows.Pivot + offset);
            _body.AddChild(_top);
            _body.AddChild(_windows);
        }
        if (p.Flaps is { } flaps)
        {
            _flaps = new Node3D { Name = "PopUps", Position = flaps.Pivot + offset };
            _flapLamps = new MeshInstance3D { Mesh = flaps.Lamp, MaterialOverride = _headMaterial };
            _flaps.AddChild(new MeshInstance3D { Mesh = flaps.Mesh, MaterialOverride = body });
            _flaps.AddChild(_flapLamps);
            _body.AddChild(_flaps);
        }
        AssembleCabin(p.Cabin, body, glass);

        string[] names = { "FL", "FR", "RL", "RR" };
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2, left = i % 2 == 0;
            // node space faces −Z, so the front axle is at −Z and left is −X
            var pivot = new Node3D
            {
                Name = "Wheel" + names[i],
                // a kart's front tyres are smaller than its rear ones (#715)
                Position = new Vector3((left ? -1f : 1f) * p.HalfTrack, front ? p.FrontWheelRadius ?? p.WheelRadius : p.WheelRadius,
                    front ? -p.FrontAxleZ : -p.RearAxleZ),
            };
            _wheelHome[i] = pivot.Position;
            AddChild(pivot);
            var spin = new Node3D { Name = "Spin" };
            spin.AddChild(new MeshInstance3D { Mesh = front ? p.FrontWheel ?? p.Wheel : p.Wheel, MaterialOverride = body });
            pivot.AddChild(spin);
            _steer[i] = pivot;
            _spin[i] = spin;
        }
        ApplyLamps();
    }

    /// <summary>The cabin's meshes and moving parts, and the driver if there is one.</summary>
    private void AssembleCabin(CarCabin cabin, Material body, Material glass)
    {
        _cabin = cabin;
        var offset = ShellOffset;
        _mirrors = new CabMirrors(this, _body, cabin.Mirrors, offset);
        // the dials are backlit: unshaded, so they read at night and in a tunnel
        var lit = TrafficMeshBuilder.LampMaterial();
        var interior = new MeshInstance3D { Name = "Cabin", Mesh = cabin.Interior, Position = offset };
        MeshScratch.Paint(interior, body, glass);
        _body.AddChild(interior);
        _body.AddChild(new MeshInstance3D { Name = "Instruments", Mesh = cabin.Instruments, MaterialOverride = lit, Position = offset });

        _wheel = new Node3D { Name = "SteeringWheel", Position = cabin.SteeringWheel.Pivot + offset };
        _wheel.AddChild(new MeshInstance3D { Mesh = cabin.SteeringWheel.Mesh, MaterialOverride = body });
        _body.AddChild(_wheel);
        Node3D NeedleNode(string name, CarNeedle needle)
        {
            var node = new Node3D { Name = name, Position = needle.Pivot + offset };
            node.AddChild(new MeshInstance3D { Mesh = needle.Mesh, MaterialOverride = lit });
            _body.AddChild(node);
            return node;
        }
        _tach = NeedleNode("Tach", cabin.Tach);
        _speedo = NeedleNode("Speedo", cabin.Speedo);
        _digit = new MeshInstance3D { Name = "Gear", MaterialOverride = lit, Position = offset };
        _body.AddChild(_digit);
        _lamps = cabin.Lamps.Select((mesh, i) => new MeshInstance3D { Name = $"Lamp{i}", Mesh = mesh, MaterialOverride = lit, Position = offset, Visible = false }).ToArray();
        foreach (var lamp in _lamps) _body.AddChild(lamp);
        _pedals = cabin.Pedals.Select((pedal, i) =>
        {
            var node = new Node3D { Name = $"Pedal{i}", Position = pedal.Pivot + offset };
            node.AddChild(new MeshInstance3D { Mesh = pedal.Mesh, MaterialOverride = body });
            _body.AddChild(node);
            return node;
        }).ToArray();

        if (_driverPalette is { } palette)
        {
            _driverBody = new MeshInstance3D { Name = "Driver", MaterialOverride = body, Position = offset };
            _driverSmooth = HumanMeshBuilder.SmoothFigures;
            _driverHead = new MeshInstance3D { Name = "DriverHead", Mesh = HumanMeshBuilder.DriverHead(palette, cabin.Seat), MaterialOverride = body, Position = offset };
            _body.AddChild(_driverBody);
            _body.AddChild(_driverHead);
        }
    }

    /// <summary>
    /// The driver's eye, where the first-person camera goes, in the rig's own frame: on the body,
    /// so it pitches with it and the dash stays put in the lens.
    /// </summary>
    public Transform3D EyeFrame => _body.Transform * new Transform3D(Basis.Identity, _cabin.Eye + ShellOffset);

    /// <summary>The driver's seat, author space: with <see cref="DriverFrame"/>, where the driver's figure is.</summary>
    public DriverSeat DriverSeat => _cabin.Seat;

    /// <summary>The driver figure's frame in the rig's own frame, on the pitching body (author +Z still to flip).</summary>
    public Transform3D DriverFrame => _body.Transform * new Transform3D(Basis.Identity, ShellOffset);

    /// <summary>A seat's hip in the rig's own frame, on the pitching body: where someone sitting in it goes.</summary>
    public Transform3D SeatFrame(SeatAnchor seat) => _body.Transform * new Transform3D(Basis.Identity, seat.Hip + ShellOffset);

    /// <summary>
    /// The steering wheel for VR hands (#243): its node (hub at the origin, turned by
    /// <see cref="WheelTurn"/> about <c>Axis</c>, which is in the node's parent's frame) and the rim's radius.
    /// Null on a car built without its cabin.
    /// </summary>
    public (Node3D Wheel, Vector3 Axis, float Radius)? SteeringGrip =>
        _cabin == null ? null : (_wheel, _cabin.ColumnAxis, _cabin.Seat.WheelRadius);

    /// <summary>How many hinged doors the car has.</summary>
    public int DoorCount => _doors.Length;

    /// <summary>The middle of a door in world space (the door shut), or the car's origin for a bit it has not got.</summary>
    public Vector3 DoorCentre(byte bit)
    {
        foreach (var door in _doors)
            if (door.Bit == bit) return _body.ToGlobal(door.Centre + ShellOffset);
        return GlobalPosition;
    }

    /// <summary>The hinge node a door swings on (its panel and glass under it), for outlining it; null for a bit it has not got.</summary>
    public Node3D? DoorPivot(byte bit)
    {
        for (int i = 0; i < _doors.Length; i++)
            if (_doors[i].Bit == bit) return _doorPivots[i];
        return null;
    }

    /// <summary>The bit of the door whose middle is nearest a world point, and how far it is.</summary>
    public (byte Bit, float Distance) NearestDoor(Vector3 point)
    {
        (byte Bit, float Distance) best = (0, float.MaxValue);
        foreach (var door in _doors)
        {
            float dist = _body.ToGlobal(door.Centre + ShellOffset).DistanceTo(point);
            if (dist < best.Distance) best = (door.Bit, dist);
        }
        return best;
    }

    private void ApplyLamps()
    {
        _tailMaterial.AlbedoColor = BrakeLights ? Colors.White : new Color(0.42f, 0.42f, 0.42f);
        _headMaterial.AlbedoColor = Headlights ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
    }

    /// <summary>Moves the top and pods toward what is asked, or straight there on the first frame.</summary>
    private void ApplyMovingParts(float dt)
    {
        float roof = RoofOpen && _top != null ? 1f : 0f, pods = Headlights ? 1f : 0f;
        _roof = _settled ? Mathf.MoveToward(_roof, roof, dt / RoofTime) : roof;
        _pods = _settled ? Mathf.MoveToward(_pods, pods, dt / FlapTime) : pods;
        _settled = true;

        if (_top != null && _windows != null)
        {
            // the glass winds down into the doors first, then the top folds back onto its hinge
            float down = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(_roof / WindowsDown, 0f, 1f));
            _windows.Visible = down < 0.99f;
            _windows.Scale = new Vector3(1f, Mathf.Max(1f - down, 0.01f), 1f);
            float fold = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((_roof - FoldFrom) / (1f - FoldFrom), 0f, 1f));
            _top.Scale = new Vector3(1f, Mathf.Lerp(1f, FoldedHeight, fold), Mathf.Lerp(1f, FoldedLength, fold));
        }
        if (_flaps != null && _flapLamps != null)
        {
            _flaps.Scale = new Vector3(1f, Mathf.Lerp(FoldedPod, 1f, Mathf.SmoothStep(0f, 1f, _pods)), 1f);
            _flapLamps.Visible = _pods > 0.15f;
        }
    }

    /// <summary>
    /// Wheel, needles, digit, lamps and pedals from the properties, and the driver re-posed when
    /// what they hold or press has moved enough to see (the figure is one mesh, rebuilt).
    /// </summary>
    private void ApplyCabin(float dt)
    {
        _wheel.Basis = new Basis(_cabin.ColumnAxis, WheelTurn);
        // needles swing to a reading rather than jump to it, like a real movement's damping
        float rpm = EngineRunning ? Rpm : 0f;
        float ease = MathX.Damp(14f, dt);
        _rpmShown = Mathf.Lerp(_rpmShown, rpm, ease);
        _speedShown = Mathf.Lerp(_speedShown, Mathf.Abs(SpeedKmh), ease);
        _tach.Basis = new Basis(_cabin.Tach.Axis, CarNeedle.Angle(_rpmShown / _cabin.Gauges.TachRpm));
        _speedo.Basis = new Basis(_cabin.Speedo.Axis, CarNeedle.Angle(_speedShown / _cabin.Gauges.SpeedoKmh));
        _digit.Mesh = _cabin.GearDigits[CarCabin.DigitFor(Gear)];
        _lamps[CarCabin.LampHandbrake].Visible = Handbrake;
        _lamps[CarCabin.LampLights].Visible = Headlights;
        _lamps[CarCabin.LampEngine].Visible = !EngineRunning;
        _pedals[CarCabin.PedalThrottle].Basis = new Basis(Vector3.Right, DriverSeat.PedalTravel * Mathf.Clamp(Throttle, 0f, 1f));
        _pedals[CarCabin.PedalBrake].Basis = new Basis(Vector3.Right, DriverSeat.PedalTravel * Mathf.Clamp(Brake, 0f, 1f));

        _glass.AlbedoColor = Colors.White with { A = View == CockpitView.Outside ? 1f : GlassFromSeat };

        if (_driverBody == null || _driverHead == null || _driverPalette is not { } palette) return;
        _driverBody.Visible = DriverShown && View != CockpitView.Bare;
        _driverHead.Visible = DriverShown && View == CockpitView.Outside;
        if (!_driverBody.Visible) return;
        var pose = (Mathf.RoundToInt(WheelTurn / 0.03f), Mathf.RoundToInt(Throttle * 8f), Mathf.RoundToInt(Brake * 8f),
            HumanMeshBuilder.SmoothFigures);
        if (pose == _driverPose) return;
        // a restyle to or from a lit style (#311): the head is rebuilt with the body
        if (pose.Item4 != _driverSmooth) (_driverHead.Mesh, _driverSmooth) = (HumanMeshBuilder.DriverHead(palette, _cabin.Seat), pose.Item4);
        _driverPose = pose;
        _driverBody.Mesh = HumanMeshBuilder.DriverBody(_driverPoses, pose, palette, _cabin.Seat);
    }

    /// <summary>
    /// The hydraulics (#464): hops of <see cref="HopPeriod"/>, nose then tail leading, easing in and
    /// out over a hop; the wheels stay on the road. Each landing thuds, and the clip plays (and plays
    /// again) for as long as it bounces. Every peer runs this from the replicated flag, so everybody
    /// near sees and hears the same car. Returns the body's tilt, rad.
    /// </summary>
    private float ApplyHydraulics(float dt)
    {
        float before = _hopTime;
        _hop = Mathf.MoveToward(_hop, Bouncing ? 1f : 0f, dt / HopPeriod);
        if (_hop <= 0f)
        {
            if (_hopTime == 0f) return 0f;
            _hopTime = 0f;
            _body.Position = new Vector3(_body.Position.X, _bodyY, _body.Position.Z);
            if (_clip is { Playing: true }) _clip.Stop();
            return 0f;
        }
        _hopTime += dt;
        float phase = _hopTime / HopPeriod;
        float up = Mathf.Abs(Mathf.Sin(phase * Mathf.Pi));   // a bounce: sharp at the bottom, round at the top
        _body.Position = new Vector3(_body.Position.X, _bodyY + HopHeight * _hop * up, _body.Position.Z);
        if (Mathf.FloorToInt(before / HopPeriod) != Mathf.FloorToInt(phase)) Thud();
        if (Bouncing) PlayClip();
        else if (_clip is { Playing: true }) _clip.Stop();
        // odd hops lead with the nose, even ones with the tail
        return HopTilt * _hop * up * (Mathf.FloorToInt(phase) % 2 == 0 ? 1f : -1f);
    }

    private AudioStreamPlayer3D Voice(string name)
    {
        var voice = new AudioStreamPlayer3D { Name = name, UnitSize = 8f, MaxDistance = 90f, Bus = Audio.SfxBus.Name };
        AddChild(voice);
        return voice;
    }

    private void Thud()
    {
        if (DisplayServer.GetName() == "headless") return;
        _thud ??= Voice("HopThud");
        var (stream, pitch, db) = Audio.SfxSynth.LandingBank.Pick(HopRng);
        _thud.Stream = stream;
        _thud.PitchScale = 0.7f * pitch;
        _thud.VolumeDb = db - 2f;
        _thud.Play();
    }

    private void PlayClip()
    {
        if (_clip is { Playing: true } || DisplayServer.GetName() == "headless" || BounceClip is not { } stream) return;
        _clip ??= Voice("BounceClip");
        _clip.Stream = stream;
        _clip.Play();
    }

    /// <summary>The clip, loaded once for every car; null when the file is not in the project.</summary>
    private static AudioStream? BounceClip
    {
        get
        {
            if (_bounceClipLoaded) return _bounceClip;
            _bounceClipLoaded = true;
            return _bounceClip = ResourceLoader.Exists(BounceClipRes) ? ResourceLoader.Load<AudioStream>(BounceClipRes) : null;
        }
    }
    private static AudioStream? _bounceClip;
    private static bool _bounceClipLoaded;

    /// <summary>
    /// A kart's own movement (#715): the inside rear wheel rising in a hard corner (a solid axle has no
    /// differential and no suspension, so one wheel comes off), and the whole kart rolling over onto its
    /// side about the outer wheels' contact line when it trips. Nothing else in the rig moves like this,
    /// so it costs a car one float compare a frame.
    /// </summary>
    private void ApplyKart(float dt)
    {
        if (!_kartMoved && Lift == 0f && Tip == 0f) return;
        float lift = Mathf.Clamp(Lift, -1f, 1f);
        _tipShown = Mathf.MoveToward(_tipShown, Mathf.Clamp(Tip, -1f, 1f), TipSpeed * dt);
        // one more pass once both are back at 0 puts every part home
        _kartMoved = lift != 0f || _tipShown != 0f;
        var rearLeft = _wheelHome[2] + Vector3.Up * (lift > 0f ? lift * InsideLift : 0f);
        var rearRight = _wheelHome[3] + Vector3.Up * (lift < 0f ? -lift * InsideLift : 0f);
        _steer[2].Position = rearLeft;
        _steer[3].Position = rearRight;
        if (_tipShown == 0f)
        {
            _body.Position = new Vector3(0, _bodyY, 0);
            _steer[0].Position = _wheelHome[0];
            _steer[1].Position = _wheelHome[1];
            return;
        }
        // over its left side (−X) for a + tip: roll about the left outer tyre's edge, + about Z lifts +X
        var edge = new Vector3(-Mathf.Sign(_tipShown) * _kartHalfWidth, 0f, 0f);
        var over = new Transform3D(new Basis(Vector3.Back, _tipShown * TipAngle), Vector3.Zero);
        var about = new Transform3D(Basis.Identity, edge) * over * new Transform3D(Basis.Identity, -edge);
        _body.Transform = about * new Transform3D(Basis.FromEuler(new Vector3(BodyPitch, 0, 0)), new Vector3(0, _bodyY, 0));
        for (int i = 0; i < 4; i++)
        {
            var pos = i == 2 ? rearLeft : i == 3 ? rearRight : _wheelHome[i];
            _steer[i].Transform = about * new Transform3D(Basis.FromEuler(new Vector3(0, i < 2 ? SteerAngle : 0f, 0)), pos);
        }
    }

    public override void _Process(double delta)
    {
        if (_body == null) return;
        float dt = (float)delta;
        float hopPitch = _hydraulics ? ApplyHydraulics(dt) : 0f;
        _body.Rotation = new Vector3(BodyPitch + hopPitch, 0, 0);   // + rotates −Z (the nose) up
        for (int i = 0; i < 4; i++)
        {
            _steer[i].Rotation = new Vector3(0, i < 2 ? SteerAngle : 0f, 0);   // + yaw turns −Z toward −X: left
            _spin[i].Rotation = new Vector3(-WheelSpin, 0, 0);                  // top edge moves toward −Z, forward
        }
        float step = dt / DoorTime;
        for (int i = 0; i < _doors.Length; i++)
        {
            float target = (DoorsOpen & _doors[i].Bit) != 0 ? 1f : 0f;
            if (_doorOpen[i] == target) continue;
            _doorOpen[i] = Mathf.MoveToward(_doorOpen[i], target, step);
            _doorPivots[i].Quaternion = Quaternion.Identity.Slerp(_doors[i].Open, Mathf.SmoothStep(0f, 1f, _doorOpen[i]));
        }
        ApplyKart(dt);
        ApplyMovingParts(dt);
        ApplyLamps();
        ApplyCabin(dt);
        _mirrors.Update(View != CockpitView.Outside, MirrorsOn);
    }
}
