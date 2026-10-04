using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Avatar;

/// <summary>One wheel of a heavy rig: its mesh, where its hub is (node space), the share of the steer it takes.</summary>
public sealed record HeavyWheel(ArrayMesh Mesh, Vector3 Hub, float Steer);

/// <summary>One leaf of a bus door: its mesh around its hinge, the hinge (node space), which door it belongs to, and how far it swings.</summary>
public sealed record HeavyDoorLeaf(int Door, ArrayMesh Mesh, Vector3 Hinge, float OpenYaw)
{
    /// <summary>The leaf's middle shut, node space: where a hand works a car door (#463).</summary>
    public Vector3 Centre { get; init; }
}

/// <summary>What a <see cref="HeavyRig"/> is assembled from.</summary>
public sealed record HeavyParts(ArrayMesh Body, ArrayMesh Head, ArrayMesh Tail, ArrayMesh Reverse,
    HeavyWheel[] Wheels, HeavyDoorLeaf[] Doors)
{
    /// <summary>The destination display's middle and width, node space (buses), or null.</summary>
    public (Vector3 At, float Width)? Display { get; init; }
    /// <summary>The driver's cockpit (the first section of a truck or bus), or null.</summary>
    public HeavyCockpit? Cockpit { get; init; }
    /// <summary>Every seat in this section, the driver's first where there is one (#158 sits passengers in the rest).</summary>
    public SeatAnchor[] Seats { get; init; } = System.Array.Empty<SeatAnchor>();
    /// <summary>A bus's saloon lights, unshaded: lit with the headlights. Null where there are none.</summary>
    public ArrayMesh? Glow { get; init; }
    /// <summary>What a walking player collides with inside and how far aboard reaches (#162), or null: not walkable.</summary>
    public VehicleDeck? Deck { get; init; }
    /// <summary>
    /// The leaves are car doors (the pickup, #463): one leaf a door, worked one by one from outside
    /// (<see cref="IHingedDoors"/>); a bus's leaves open together from its buttons.
    /// </summary>
    public bool CarDoors { get; init; }
    /// <summary>Meshes carried on the body, each at its place, node space: a boat on its trailer (#463).</summary>
    public (ArrayMesh Mesh, Vector3 At)[] Cargo { get; init; } = System.Array.Empty<(ArrayMesh, Vector3)>();
}

/// <summary>
/// One section of a truck, a bus or a trailer, drawn (#70): the body, every axle's wheels (steering
/// by their axle's share of the front angle, all spinning), brake, head and reversing lamps, a bus's
/// door leaves swinging out on their hinges, the kneel that lowers the door side, and the destination
/// display. The first section of a truck or bus has its cockpit (#157): a steering wheel turning at
/// <see cref="HeavyCockpit.SteerRatio"/>, working dials and gear display, warning lamps, pedals, the
/// driver at the wheel and, for the local driver in the seat, working mirrors. Origin on the ground
/// under the section's centre of mass — the same point its physics body turns about — facing −Z
/// like every node. The owner sets the properties; <c>_Process</c> applies them.
/// </summary>
public partial class HeavyRig : Node3D, IHingedDoors
{
    public float SteerAngle { get; set; }
    public float WheelSpin { get; set; }
    public bool BrakeLights { get; set; }
    public bool Headlights { get; set; }
    public bool ReverseLights { get; set; }
    /// <summary>Doors open, one bit per door of the whole vehicle (a section draws only its own).</summary>
    public byte DoorsOpen { get; set; }
    public bool Kneeling { get; set; }
    /// <summary>What the destination display reads.</summary>
    public string Destination { get; set; } = "";
    /// <summary>Body roll on the springs, rad (+ right side up): the wheels stay on the road.</summary>
    public float BodyRoll { get; set; }

    // ---- the cockpit (first section of a truck or bus) ----
    /// <summary>The steering wheel's turn, rad (+ anticlockwise as the driver sees it).</summary>
    public float WheelTurn { get; set; }
    public float Throttle { get; set; }
    public float Brake { get; set; }
    public float Clutch { get; set; }
    public float SpeedKmh { get; set; }
    public float Rpm { get; set; }
    /// <summary>What the gear display reads: N, R, A7, 4H, 12.</summary>
    public string Gear { get; set; } = "N";
    /// <summary>The air tank, bar.</summary>
    public float Air { get; set; } = HeavyDriveline.AirMax;
    public bool SpringBrakes { get; set; }
    public int Retarder { get; set; }
    public bool EngineRunning { get; set; } = true;
    /// <summary>What the local driver sees of their own figure from the seat; every remote copy is <see cref="CockpitView.Outside"/>.</summary>
    public CockpitView View { get; set; }
    /// <summary>The mirrors work (the setting); only ever for the local driver in the seat.</summary>
    public bool MirrorsOn { get; set; }
    /// <summary>Under this the low-air lamp lights (bar); the airstairs' small gauge reads the platform instead (#417).</summary>
    public float AirLowAt { get; set; } = HeavyDriveline.AirLow;
    /// <summary>The seats in this section, node space, the driver's first where there is one.</summary>
    public SeatAnchor[] Seats { get; private set; } = System.Array.Empty<SeatAnchor>();
    /// <summary>This section's deck, for walking about in it (#162); null when it has none.</summary>
    public VehicleDeck? Deck { get; private set; }
    /// <summary>Somebody at the wheel: off while it rolls on driverless with its passengers (#158).</summary>
    public bool DriverShown { get; set; } = true;
    /// <summary>The cockpit this section was built with (the first of a truck or bus), or null.</summary>
    public HeavyCockpit? Cockpit => _cockpit;

    private const float DoorTime = 1.2f, KneelTime = 1.5f, KneelDrop = 0.08f;

    private Node3D _body = null!;
    private readonly List<(Node3D Pivot, Node3D Spin, float Steer)> _wheels = new();
    private readonly List<(Node3D Pivot, int Door, float OpenYaw)> _doors = new();
    private float[] _doorOpen = System.Array.Empty<float>();
    /// <summary>Car doors (#463): each one's index, middle (node space) and hinge; empty on a bus.</summary>
    private readonly List<(int Door, Vector3 Centre, Node3D Pivot)> _carDoors = new();

    // ---- car doors worked one by one (IHingedDoors): the pickup's ----

    public int DoorCount => _carDoors.Count;

    public Vector3 DoorCentre(byte bit)
    {
        foreach (var d in _carDoors)
            if (1 << d.Door == bit) return _body.ToGlobal(d.Centre);
        return GlobalPosition;
    }

    public Node3D? DoorPivot(byte bit)
    {
        foreach (var d in _carDoors)
            if (1 << d.Door == bit) return d.Pivot;
        return null;
    }

    public (byte Bit, float Distance) NearestDoor(Vector3 point)
    {
        (byte Bit, float Distance) best = (0, float.MaxValue);
        foreach (var d in _carDoors)
        {
            float dist = _body.ToGlobal(d.Centre).DistanceTo(point);
            if (dist < best.Distance) best = ((byte)(1 << d.Door), dist);
        }
        return best;
    }
    private float _kneel;
    private StandardMaterial3D _head = null!, _tail = null!, _reverse = null!, _glow = null!, _glass = null!;
    private Label3D? _display;

    private HeavyCockpit? _cockpit;
    private Node3D _wheel = null!, _tach = null!, _speedo = null!, _air = null!;
    private MeshInstance3D[] _gearChars = System.Array.Empty<MeshInstance3D>();
    private MeshInstance3D[] _lamps = System.Array.Empty<MeshInstance3D>();
    private Node3D[] _pedals = System.Array.Empty<Node3D>();
    private HumanPalette? _driverPalette;
    private MeshInstance3D? _driverBody, _driverHead;
    private (int Turn, int Throttle, int Brake, bool Smooth) _driverPose = (int.MinValue, 0, 0, false);
    private readonly Dictionary<(int Turn, int Throttle, int Brake, bool Smooth), ArrayMesh> _driverPoses = new();
    /// <summary>Whether the driver's head was built smooth (<see cref="HumanMeshBuilder.SmoothFigures"/>).</summary>
    private bool _driverSmooth;
    private float _rpmShown, _speedShown, _airShown = HeavyDriveline.AirMax;
    private string _gearShown = "";
    private CabMirrors? _mirrors;
    /// <summary>The glass's tint from the driver's seat, as a car's: a windscreen is all but clear from inside.</summary>
    private const float GlassFromSeat = 0.35f;

    /// <param name="driver">The figure at the wheel, in its colours; null for an empty vehicle (parked, previewed).</param>
    public static HeavyRig Create(HeavySpec spec, int section, float load, HumanPalette? driver = null) =>
        Assemble(spec.Class switch
        {
            HeavyClass.Tractor or HeavyClass.Rigid => TruckMeshBuilder.Build(spec, section, load),
            HeavyClass.Pickup => PickupMeshBuilder.Build(spec, section, load),
            _ => BusMeshBuilder.Build(spec, section, load),
        }, driver);

    /// <summary>A rig from parts another builder made (the airstairs truck, #417).</summary>
    public static HeavyRig Create(HeavyParts parts, HumanPalette? driver) => Assemble(parts, driver);

    public static HeavyRig CreateTrailer(TrailerSpec spec, int section, float load, bool boatShown = false) =>
        Assemble(TrailerMeshBuilder.Build(spec, section, load, boatShown), null);

    private static HeavyRig Assemble(HeavyParts p, HumanPalette? driver)
    {
        var rig = new HeavyRig { Name = "Heavy", _driverPalette = driver, Seats = p.Seats, Deck = p.Deck };
        var body = HumanMeshBuilder.FigureMaterial();   // the driver wears clothes, maybe with a finish (#251)
        var glass = rig._glass = CarRig.GlassMaterial();
        rig._head = TrafficMeshBuilder.LampMaterial();
        rig._tail = TrafficMeshBuilder.LampMaterial();
        rig._reverse = TrafficMeshBuilder.LampMaterial();
        rig._glow = TrafficMeshBuilder.LampMaterial();

        rig._body = new Node3D { Name = "Body" };
        rig.AddChild(rig._body);
        var shell = new MeshInstance3D { Name = "Shell", Mesh = p.Body };
        MeshScratch.Paint(shell, body, glass);
        rig._body.AddChild(shell);
        if (p.Glow is { } glow && glow.GetSurfaceCount() > 0)
            rig._body.AddChild(new MeshInstance3D { Name = "SaloonLights", Mesh = glow, MaterialOverride = rig._glow });
        rig._body.AddChild(new MeshInstance3D { Name = "Headlamps", Mesh = p.Head, MaterialOverride = rig._head });
        rig._body.AddChild(new MeshInstance3D { Name = "Taillamps", Mesh = p.Tail, MaterialOverride = rig._tail });
        rig._body.AddChild(new MeshInstance3D { Name = "Reversing", Mesh = p.Reverse, MaterialOverride = rig._reverse });
        for (int i = 0; i < p.Cargo.Length; i++)
        {
            var cargo = new MeshInstance3D { Name = $"Cargo{i}", Mesh = p.Cargo[i].Mesh, Position = p.Cargo[i].At };
            MeshScratch.Paint(cargo, body, glass);
            rig._body.AddChild(cargo);
        }

        for (int i = 0; i < p.Doors.Length; i++)
        {
            var leaf = p.Doors[i];
            var pivot = new Node3D { Name = $"Door{leaf.Door}_{i}", Position = leaf.Hinge };
            var panel = new MeshInstance3D { Mesh = leaf.Mesh };
            MeshScratch.Paint(panel, body, glass);
            pivot.AddChild(panel);
            rig._body.AddChild(pivot);
            rig._doors.Add((pivot, leaf.Door, leaf.OpenYaw));
            if (p.CarDoors) rig._carDoors.Add((leaf.Door, leaf.Centre, pivot));
        }
        rig._doorOpen = new float[rig._doors.Count];

        for (int i = 0; i < p.Wheels.Length; i++)
        {
            var w = p.Wheels[i];
            var pivot = new Node3D { Name = $"Wheel{i}", Position = w.Hub };
            var spin = new Node3D { Name = "Spin" };
            spin.AddChild(new MeshInstance3D { Mesh = w.Mesh, MaterialOverride = body });
            pivot.AddChild(spin);
            rig.AddChild(pivot);
            rig._wheels.Add((pivot, spin, w.Steer));
        }

        if (p.Display is { } display && DisplayServer.GetName() != "headless")
        {
            // the LED matrix: amber on black, readable from in front; the text is the owner's to set
            rig._display = new Label3D
            {
                Name = "Destination",
                Position = display.At,
                Rotation = new Vector3(0, Mathf.Pi, 0),
                PixelSize = 0.0045f,
                FontSize = 48,
                OutlineSize = 0,
                Modulate = new Color(1f, 0.62f, 0.1f),
                Width = display.Width / 0.0045f,
                AutowrapMode = TextServer.AutowrapMode.Off,
                Shaded = false,
                DoubleSided = false,
            };
            rig._body.AddChild(rig._display);
        }
        if (p.Cockpit is { } cockpit) rig.AssembleCockpit(cockpit, body);
        rig.ApplyLamps();
        return rig;
    }

    /// <summary>The cockpit's moving parts and instruments, and the driver if there is one.</summary>
    private void AssembleCockpit(HeavyCockpit c, Material body)
    {
        _cockpit = c;
        // the instruments are backlit: unshaded, so they read at night and in a tunnel
        var lit = TrafficMeshBuilder.LampMaterial();
        _body.AddChild(new MeshInstance3D { Name = "Instruments", Mesh = c.Instruments, MaterialOverride = lit });
        _wheel = new Node3D { Name = "SteeringWheel", Position = c.SteeringWheel.Pivot };
        _wheel.AddChild(new MeshInstance3D { Mesh = c.SteeringWheel.Mesh, MaterialOverride = body });
        _body.AddChild(_wheel);
        Node3D NeedleNode(string name, CarNeedle needle)
        {
            var node = new Node3D { Name = name, Position = needle.Pivot };
            node.AddChild(new MeshInstance3D { Mesh = needle.Mesh, MaterialOverride = lit });
            _body.AddChild(node);
            return node;
        }
        _tach = NeedleNode("Tach", c.Tach);
        _speedo = NeedleNode("Speedo", c.Speedo);
        _air = NeedleNode("AirGauge", c.Air);
        _gearChars = c.GearChars.Select((_, i) => new MeshInstance3D { Name = $"Gear{i}", MaterialOverride = lit }).ToArray();
        foreach (var ch in _gearChars) _body.AddChild(ch);
        _lamps = c.Lamps.Select((mesh, i) => new MeshInstance3D { Name = $"Lamp{i}", Mesh = mesh, MaterialOverride = lit, Visible = false }).ToArray();
        foreach (var lamp in _lamps) _body.AddChild(lamp);
        _pedals = c.Pedals.Select((pedal, i) =>
        {
            var node = new Node3D { Name = $"Pedal{i}", Position = pedal.Pivot };
            node.AddChild(new MeshInstance3D { Mesh = pedal.Mesh, MaterialOverride = body });
            _body.AddChild(node);
            return node;
        }).ToArray();
        _mirrors = new CabMirrors(this, _body, c.Mirrors, Vector3.Zero);

        if (_driverPalette is { } palette)
        {
            _driverBody = new MeshInstance3D { Name = "Driver", MaterialOverride = body };
            _driverSmooth = HumanMeshBuilder.SmoothFigures;
            _driverHead = new MeshInstance3D { Name = "DriverHead", Mesh = HumanMeshBuilder.DriverHead(palette, c.Seat), MaterialOverride = body };
            _body.AddChild(_driverBody);
            _body.AddChild(_driverHead);
        }
    }

    /// <summary>
    /// The driver's eye, where the first-person camera goes, in the rig's own frame: on the body,
    /// so it kneels and rolls with it. Identity on a section with no cockpit.
    /// </summary>
    public Transform3D EyeFrame => _cockpit == null ? Transform3D.Identity
        : _body.Transform * new Transform3D(Basis.Identity, _cockpit.Eye);

    /// <summary>The steering wheel for VR hands (#243), as <see cref="CarRig.SteeringGrip"/>. Null on a section with no cockpit.</summary>
    public (Node3D Wheel, Vector3 Axis, float Radius)? SteeringGrip =>
        _cockpit is { } c ? (_wheel, c.ColumnAxis, c.Seat.WheelRadius) : null;

    /// <summary>A seat's hip in the rig's own frame, on the kneeling, rolling body: where someone sitting in it goes.</summary>
    public Transform3D SeatFrame(SeatAnchor seat) => _body.Transform * new Transform3D(Basis.Identity, seat.Hip);

    private void ApplyLamps()
    {
        _tail.AlbedoColor = BrakeLights ? Colors.White : new Color(0.42f, 0.42f, 0.42f);
        _head.AlbedoColor = Headlights ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
        _reverse.AlbedoColor = ReverseLights ? Colors.White : new Color(0.5f, 0.5f, 0.5f);
        _glow.AlbedoColor = Headlights ? Colors.White : new Color(0.5f, 0.5f, 0.48f);
    }

    /// <summary>
    /// Wheel, needles, gear display, lamps and pedals from the properties, and the driver re-posed
    /// when what they hold or press has moved enough to see (the figure is one mesh, rebuilt).
    /// </summary>
    private void ApplyCockpit(float dt)
    {
        if (_cockpit is not { } c) return;
        _wheel.Basis = new Basis(c.ColumnAxis, WheelTurn);
        // needles swing to a reading rather than jump to it, like a real movement's damping
        float ease = MathX.Damp(10f, dt);
        _rpmShown = Mathf.Lerp(_rpmShown, EngineRunning ? Rpm : 0f, ease);
        _speedShown = Mathf.Lerp(_speedShown, Mathf.Abs(SpeedKmh), ease);
        _airShown = Mathf.Lerp(_airShown, Air, MathX.Damp(3f, dt));
        _tach.Basis = new Basis(c.Tach.Axis, CarNeedle.Angle(_rpmShown / c.Gauges.TachRpm));
        _speedo.Basis = new Basis(c.Speedo.Axis, CarNeedle.Angle(_speedShown / c.Gauges.SpeedoKmh));
        _air.Basis = new Basis(c.Air.Axis, CarNeedle.Angle(_airShown / HeavyDriveline.AirMax));
        if (Gear != _gearShown)
        {
            _gearShown = Gear;
            // right-aligned, N and R as seven segments can draw them
            var text = Gear.Replace('N', 'n').Replace('R', 'r');
            if (text.Length > _gearChars.Length) text = text[^_gearChars.Length..];
            text = text.PadLeft(_gearChars.Length);
            for (int i = 0; i < _gearChars.Length; i++)
                _gearChars[i].Mesh = c.GearChars[i].TryGetValue(text[i], out var mesh) ? mesh : null;
        }
        _lamps[HeavyCockpit.LampSpringBrake].Visible = SpringBrakes;
        _lamps[HeavyCockpit.LampLowAir].Visible = Air < AirLowAt;
        _lamps[HeavyCockpit.LampLights].Visible = Headlights;
        _lamps[HeavyCockpit.LampRetarder].Visible = Retarder > 0;
        _lamps[HeavyCockpit.LampDoors].Visible = DoorsOpen != 0;
        _lamps[HeavyCockpit.LampEngine].Visible = !EngineRunning;
        _pedals[HeavyCockpit.PedalThrottle].Basis = new Basis(Vector3.Right, DriverSeat.PedalTravel * Mathf.Clamp(Throttle, 0f, 1f));
        _pedals[HeavyCockpit.PedalBrake].Basis = new Basis(Vector3.Right, DriverSeat.PedalTravel * Mathf.Clamp(Brake, 0f, 1f));
        if (_pedals.Length > HeavyCockpit.PedalClutch)
            _pedals[HeavyCockpit.PedalClutch].Basis = new Basis(Vector3.Right, DriverSeat.PedalTravel * Mathf.Clamp(Clutch, 0f, 1f));

        _glass.AlbedoColor = Colors.White with { A = View == CockpitView.Outside ? 1f : GlassFromSeat };
        _mirrors?.Update(View != CockpitView.Outside, MirrorsOn);

        if (_driverBody == null || _driverHead == null || _driverPalette is not { } palette) return;
        _driverBody.Visible = DriverShown && View != CockpitView.Bare;
        _driverHead.Visible = DriverShown && View == CockpitView.Outside;
        if (!_driverBody.Visible) return;
        var pose = (Mathf.RoundToInt(WheelTurn / 0.03f), Mathf.RoundToInt(Throttle * 8f), Mathf.RoundToInt(Brake * 8f),
            HumanMeshBuilder.SmoothFigures);
        if (pose == _driverPose) return;
        // a restyle to or from a lit style (#311): the head is rebuilt with the body
        if (pose.Item4 != _driverSmooth) (_driverHead.Mesh, _driverSmooth) = (HumanMeshBuilder.DriverHead(palette, c.Seat), pose.Item4);
        _driverPose = pose;
        _driverBody.Mesh = HumanMeshBuilder.DriverBody(_driverPoses, pose, palette, c.Seat);
    }

    public override void _Process(double delta)
    {
        if (_body == null) return;
        float dt = (float)delta;
        _kneel = Mathf.MoveToward(_kneel, Kneeling ? 1f : 0f, dt / KneelTime);
        // kneeling lowers the door side (the right, +X): down and rolled toward it
        float k = Mathf.SmoothStep(0f, 1f, _kneel);
        _body.Position = new Vector3(0, -KneelDrop * 0.5f * k, 0);
        _body.Rotation = new Vector3(0, 0, BodyRoll - k * KneelDrop / 1.25f);

        foreach (var (pivot, spin, steer) in _wheels)
        {
            pivot.Rotation = new Vector3(0, SteerAngle * steer, 0);
            spin.Rotation = new Vector3(-WheelSpin, 0, 0);
        }

        float step = dt / DoorTime;
        for (int i = 0; i < _doors.Count; i++)
        {
            var (pivot, door, yaw) = _doors[i];
            float target = (DoorsOpen >> door & 1) != 0 ? 1f : 0f;
            if (_doorOpen[i] == target) continue;
            _doorOpen[i] = Mathf.MoveToward(_doorOpen[i], target, step);
            pivot.Rotation = new Vector3(0, yaw * Mathf.SmoothStep(0f, 1f, _doorOpen[i]), 0);
        }

        if (_display != null && _display.Text != Destination) _display.Text = Destination;
        ApplyLamps();
        ApplyCockpit(dt);
    }
}

/// <summary>Pieces every heavy builder shares: wheels, lamps, the body cut round its wheel arches.</summary>
public static class HeavyMesh
{
    public static readonly Color Glass = new(0.26f, 0.33f, 0.4f);
    /// <summary>The glass of a pane: the same tint, translucent (<see cref="CarRig.GlassMaterial"/>).</summary>
    public static readonly Color PaneTint = Glass with { A = 0.55f };
    public static readonly Color Rubber = new(0.07f, 0.07f, 0.08f);
    public static readonly Color Trim = new(0.08f, 0.08f, 0.09f);
    public static readonly Color Steel = new(0.55f, 0.56f, 0.6f);
    public static readonly Color Rim = new(0.72f, 0.73f, 0.75f);
    public static readonly Color HeadLamp = new(1f, 0.96f, 0.8f);
    public static readonly Color TailLamp = new(1f, 0.12f, 0.09f);
    public static readonly Color White = new(0.97f, 0.97f, 0.95f);
    public static readonly Color Amber = new(1f, 0.6f, 0.12f);

    private static readonly Dictionary<(string, bool), ArrayMesh> _wheels = new();

    /// <summary>
    /// A wheel on its hub, facing ±X: a tyre, a rim, a twin pair side by side. Built once per size.
    /// </summary>
    public static ArrayMesh Wheel(string tyre, bool twin)
    {
        if (_wheels.TryGetValue((tyre, twin), out var known)) return known;
        float r = Tyre.Radius(tyre) * 0.97f, w = Tyre.Width(tyre), rim = Tyre.RimRadius(tyre);
        var s = new MeshScratch();
        int n = twin ? 2 : 1;
        float span = twin ? 2f * w + 0.03f : w;
        for (int i = 0; i < n; i++)
        {
            float cx = twin ? (i == 0 ? -1f : 1f) * (w * 0.5f + 0.015f) : 0f;
            s.Tube(new Vector3(cx - w * 0.5f, 0, 0), new Vector3(cx + w * 0.5f, 0, 0), r, Rubber, 12);
        }
        // the outer rim face and its hub, proud of the tyre so they read at a distance
        s.Tube(new Vector3(-span * 0.5f - 0.012f, 0, 0), new Vector3(span * 0.5f + 0.012f, 0, 0), rim * 0.95f, Rim, 10);
        s.Tube(new Vector3(-span * 0.5f - 0.05f, 0, 0), new Vector3(span * 0.5f + 0.05f, 0, 0), rim * 0.3f, Steel, 6);
        return _wheels[(tyre, twin)] = s.Build();
    }

    /// <summary>
    /// The wheels of a section: two per axle, their outer faces a few centimetres in from the body
    /// side, at the height of their own radius. <paramref name="cg"/> is the centre of mass, metres
    /// behind the section's front (the rig's origin).
    /// </summary>
    public static HeavyWheel[] Wheels(SectionSpec s, float cg)
    {
        var list = new List<HeavyWheel>();
        foreach (var a in s.Axles)
        {
            float r = Tyre.Radius(a.Tyre) * 0.97f, w = Tyre.Width(a.Tyre);
            float span = a.Twin ? 2f * w + 0.03f : w;
            float x = s.Width * 0.5f - 0.05f - span * 0.5f;
            var mesh = Wheel(a.Tyre, a.Twin);
            // node space: forward is −Z, left is −X
            list.Add(new HeavyWheel(mesh, new Vector3(-x, r, -(cg - a.At)), a.Steer));
            list.Add(new HeavyWheel(mesh, new Vector3(x, r, -(cg - a.At)), a.Steer));
        }
        return list.ToArray();
    }

    /// <summary>
    /// A box along the section (authored space: +Z forward, z = cg − at) with gaps where the wheels
    /// are, so they show through their arches: from <paramref name="fromAt"/> to <paramref name="toAt"/>
    /// metres behind the front, between heights <paramref name="y0"/> and <paramref name="y1"/>.
    /// </summary>
    public static void Skirt(MeshScratch m, SectionSpec s, float cg, float fromAt, float toAt, float y0, float y1, float width, Color colour)
    {
        var cuts = new List<(float, float)>();
        foreach (var a in s.Axles)
        {
            float r = Tyre.Radius(a.Tyre) + 0.1f;
            cuts.Add((a.At - r, a.At + r));
        }
        cuts.Sort();
        float at = fromAt;
        foreach (var (c0, c1) in cuts)
        {
            if (c1 <= at) continue;
            if (c0 > at) Along(m, cg, at, Mathf.Min(c0, toAt), y0, y1, width, colour);
            at = Mathf.Max(at, c1);
            if (at >= toAt) return;
        }
        if (at < toAt) Along(m, cg, at, toAt, y0, y1, width, colour);
    }

    /// <summary>A box between two stations (metres behind the front), centred across, authored space.</summary>
    public static void Along(MeshScratch m, float cg, float fromAt, float toAt, float y0, float y1, float width, Color colour, float x = 0f)
    {
        if (toAt - fromAt < 0.01f || y1 - y0 < 0.005f) return;
        m.Box(new Vector3(x, (y0 + y1) * 0.5f, cg - (fromAt + toAt) * 0.5f), new Vector3(width, y1 - y0, toAt - fromAt), colour);
    }

    /// <summary>A panel on both sides (or one: side +1 is authored +X, the left of the vehicle), proud of a body <paramref name="width"/> wide.</summary>
    public static void Sides(MeshScratch m, float cg, float fromAt, float toAt, float y0, float y1, float width, Color colour, int side = 0)
    {
        foreach (int sx in new[] { -1, 1 })
            if (side == 0 || side == sx)
                Along(m, cg, fromAt, toAt, y0, y1, 0.02f, colour, sx * (width * 0.5f + 0.01f));
    }

    /// <summary>Metres behind the vehicle's front where section <paramref name="k"/> of it starts (0 for the first).</summary>
    public static float SectionFront(IReadOnlyList<SectionSpec> sections, int k)
    {
        float front = 0f;
        for (int i = 1; i <= k && i < sections.Count; i++) front += sections[i - 1].HitchAt - sections[i].PivotAt;
        return front;
    }

    /// <summary>The centre of mass with this much of the payload aboard, metres behind the section's front: the rig's origin.</summary>
    public static float Cg(SectionSpec s, float load) => new HeavyTrain.Body(s, s.PayloadMax * load).CgAt;

    /// <summary>A window in the face, square to the road: from −<paramref name="halfWidth"/> to + across, at authored z <paramref name="z"/>.</summary>
    public static void FrontPane(MeshScratch m, float z, float halfWidth, float y0, float y1) =>
        m.Pane(new[] { new Vector3(-halfWidth, y0, z), new Vector3(halfWidth, y0, z), new Vector3(halfWidth, y1, z), new Vector3(-halfWidth, y1, z) }, PaneTint);

    /// <summary>A window in a side wall at authored x <paramref name="x"/>, between authored z <paramref name="z0"/> and <paramref name="z1"/>.</summary>
    public static void SidePane(MeshScratch m, float x, float z0, float z1, float y0, float y1) =>
        m.Pane(new[] { new Vector3(x, y0, z0), new Vector3(x, y0, z1), new Vector3(x, y1, z1), new Vector3(x, y1, z0) }, PaneTint);

    /// <summary>A lamp-sized box, authored space.</summary>
    public static void Lamp(MeshScratch m, float x, float y, float z, float w, float h, float d, Color colour) =>
        m.Box(new Vector3(x, y, z), new Vector3(w, h, d), colour);
}
