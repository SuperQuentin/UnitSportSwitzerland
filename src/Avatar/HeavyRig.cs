using Godot;
using UnitSport.Player;

namespace UnitSport.Avatar;

/// <summary>One wheel of a heavy rig: its mesh, where its hub is (node space), the share of the steer it takes.</summary>
public sealed record HeavyWheel(ArrayMesh Mesh, Vector3 Hub, float Steer);

/// <summary>One leaf of a bus door: its mesh around its hinge, the hinge (node space), which door it belongs to, and how far it swings.</summary>
public sealed record HeavyDoorLeaf(int Door, ArrayMesh Mesh, Vector3 Hinge, float OpenYaw);

/// <summary>What a <see cref="HeavyRig"/> is assembled from.</summary>
public sealed record HeavyParts(ArrayMesh Body, ArrayMesh Head, ArrayMesh Tail, ArrayMesh Reverse,
    HeavyWheel[] Wheels, HeavyDoorLeaf[] Doors)
{
    /// <summary>The destination display's middle and width, node space (buses), or null.</summary>
    public (Vector3 At, float Width)? Display { get; init; }
}

/// <summary>
/// One section of a truck, a bus or a trailer, drawn (#70): the body, every axle's wheels (steering
/// by their axle's share of the front angle, all spinning), brake, head and reversing lamps, a bus's
/// door leaves swinging out on their hinges, the kneel that lowers the door side, and the destination
/// display. Origin on the ground under the section's centre of mass — the same point its physics
/// body turns about — facing −Z like every node. The owner sets the properties; <c>_Process</c>
/// applies them.
/// </summary>
public partial class HeavyRig : Node3D
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
    /// <summary>
    /// The body drawn. Off from the driver's seat in first person: the cab's glass and walls are
    /// boxes that would block the view from inside (a cockpit is #69's).
    /// </summary>
    public bool ShellVisible { get => _body?.Visible ?? true; set { if (_body != null) _body.Visible = value; } }

    private const float DoorTime = 1.2f, KneelTime = 1.5f, KneelDrop = 0.08f;

    private Node3D _body = null!;
    private readonly List<(Node3D Pivot, Node3D Spin, float Steer)> _wheels = new();
    private readonly List<(Node3D Pivot, int Door, float OpenYaw)> _doors = new();
    private float[] _doorOpen = System.Array.Empty<float>();
    private float _kneel;
    private StandardMaterial3D _head = null!, _tail = null!, _reverse = null!;
    private Label3D? _display;

    public static HeavyRig Create(HeavySpec spec, int section, float load) =>
        Assemble(spec.Class switch
        {
            HeavyClass.Tractor or HeavyClass.Rigid => TruckMeshBuilder.Build(spec, section, load),
            _ => BusMeshBuilder.Build(spec, section, load),
        }, spec.Label);

    public static HeavyRig CreateTrailer(TrailerSpec spec, int section, float load) =>
        Assemble(TrailerMeshBuilder.Build(spec, section, load), spec.Label);

    private static HeavyRig Assemble(HeavyParts p, string name)
    {
        var rig = new HeavyRig { Name = "Heavy" };
        var body = HumanMeshBuilder.Material();
        rig._head = TrafficMeshBuilder.LampMaterial();
        rig._tail = TrafficMeshBuilder.LampMaterial();
        rig._reverse = TrafficMeshBuilder.LampMaterial();

        rig._body = new Node3D { Name = "Body" };
        rig.AddChild(rig._body);
        rig._body.AddChild(new MeshInstance3D { Name = "Shell", Mesh = p.Body, MaterialOverride = body });
        rig._body.AddChild(new MeshInstance3D { Name = "Headlamps", Mesh = p.Head, MaterialOverride = rig._head });
        rig._body.AddChild(new MeshInstance3D { Name = "Taillamps", Mesh = p.Tail, MaterialOverride = rig._tail });
        rig._body.AddChild(new MeshInstance3D { Name = "Reversing", Mesh = p.Reverse, MaterialOverride = rig._reverse });

        for (int i = 0; i < p.Doors.Length; i++)
        {
            var leaf = p.Doors[i];
            var pivot = new Node3D { Name = $"Door{leaf.Door}_{i}", Position = leaf.Hinge };
            pivot.AddChild(new MeshInstance3D { Mesh = leaf.Mesh, MaterialOverride = body });
            rig._body.AddChild(pivot);
            rig._doors.Add((pivot, leaf.Door, leaf.OpenYaw));
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
        rig.ApplyLamps();
        return rig;
    }

    private void ApplyLamps()
    {
        _tail.AlbedoColor = BrakeLights ? Colors.White : new Color(0.42f, 0.42f, 0.42f);
        _head.AlbedoColor = Headlights ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
        _reverse.AlbedoColor = ReverseLights ? Colors.White : new Color(0.5f, 0.5f, 0.5f);
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
    }
}

/// <summary>Pieces every heavy builder shares: wheels, lamps, the body cut round its wheel arches.</summary>
public static class HeavyMesh
{
    public static readonly Color Glass = new(0.26f, 0.33f, 0.4f);
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

    /// <summary>A lamp-sized box, authored space.</summary>
    public static void Lamp(MeshScratch m, float x, float y, float z, float w, float h, float d, Color colour) =>
        m.Box(new Vector3(x, y, z), new Vector3(w, h, d), colour);
}
