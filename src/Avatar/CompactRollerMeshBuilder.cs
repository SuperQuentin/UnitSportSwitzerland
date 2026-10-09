using Godot;
using L = UnitSport.Avatar.CompactRollerLayout;

namespace UnitSport.Avatar;

/// <summary>
/// Builds the compact tandem roller's meshes (#614). The rear frame — the engine hood, the
/// operator's platform over the hinge, the seat and its cockpit, the roll bar, the rear drum — is
/// the heavy rig's body; the front frame is its child node <see cref="RollerFront"/>, swung about
/// the hinge to steer, carrying the water tank and the front drum. Nothing rebuilt.
/// </summary>
public static class CompactRollerMeshBuilder
{
    private static readonly Color Body = new(0.95f, 0.72f, 0.08f);
    private static readonly Color Dark = new(0.15f, 0.15f, 0.17f);
    private static readonly Color Drum = new(0.52f, 0.53f, 0.55f);
    private static readonly Color Steel = new(0.66f, 0.67f, 0.70f);
    private static readonly Color Beacon = new(1f, 0.5f, 0.05f);

    /// <summary>An open seat over the hinge: no glass, a low console ahead with the dials and a small wheel.</summary>
    public static readonly CabFrame Frame = new()
    {
        Front = 0.5f, Floor = L.Floor, Ceiling = L.RopsTop - 0.1f,
        WsBase = 1.3f, WsTop = 2.4f, DashTop = 1.35f,
        InnerHalf = L.FrameHalf - 0.06f, Nose = 0.05f, DriverX = 0f, HipRise = 0.48f, Recline = 0.1f,
        ColumnTilt = 0.85f, WheelRadius = 0.15f, DashToX = -(L.FrameHalf - 0.06f), Clutch = false, PassengerSeat = false,
    };

    /// <summary>Its dials: speed to 12 km/h, the tach to 3000 rpm.</summary>
    public static readonly CarGauges Gauges = HeavyCabin.GaugesFor(12f, 3000f);

    /// <summary>Where a VR hand finds the vibration button: on the console right of the wheel (<c>XrCabControls</c> "roller").</summary>
    public static Vector3 VibrationButton(Vector3 hip) => hip + new Vector3(-0.3f, 0.12f, 0.42f);

    public static HeavyParts Parts()
    {
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var reverse = new MeshScratch();
        const float fh = L.FrameHalf;

        // ---- the rear frame: the yoke round the rear drum, the engine hood, the platform --------
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (L.DrumHalf + 0.05f), L.DrumRadius + 0.1f, L.RearAxle), new Vector3(0.08f, 0.55f, 0.6f), Body.Darkened(0.15f));
        m.Box(new Vector3(0, (L.DrumRadius * 2f + 0.02f + L.HoodTop) * 0.5f, (L.Tail + L.RopsZ) * 0.5f),
            new Vector3(fh * 2f, L.HoodTop - L.DrumRadius * 2f - 0.02f, L.RopsZ - L.Tail), Body);
        m.Box(new Vector3(0, L.HoodTop + 0.02f, (L.Tail + L.RopsZ) * 0.5f), new Vector3(fh * 2f - 0.1f, 0.04f, L.RopsZ - L.Tail - 0.12f), Body.Darkened(0.2f));
        m.Box(new Vector3(0, L.HoodTop * 0.6f, L.Tail + 0.04f), new Vector3(fh * 2f, L.HoodTop * 0.5f, 0.08f), Dark);
        m.Tube(new Vector3(-0.4f, L.HoodTop, -0.95f), new Vector3(-0.4f, L.HoodTop + 0.28f, -0.95f), 0.04f, Dark, 6);
        // the platform over the hinge, and the hinge's plates under it
        m.Box(new Vector3(0, L.Floor - 0.06f, -0.12f), new Vector3(fh * 2f + 0.06f, 0.12f, 1.0f), Dark.Lightened(0.05f));
        m.Box(new Vector3(0, (L.DrumRadius * 2f + L.Floor - 0.12f) * 0.5f, -0.12f), new Vector3(0.7f, L.Floor - 0.12f - L.DrumRadius * 2f, 0.8f), Body.Darkened(0.1f));
        foreach (float y in new[] { 0.45f, 0.75f })
            m.Box(new Vector3(0, y, 0.05f), new Vector3(0.5f, 0.08f, 0.35f), Steel.Darkened(0.4f));
        // the roll bar behind the seat, folded up
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (fh - 0.04f), (L.HoodTop + L.RopsTop) * 0.5f, L.RopsZ), new Vector3(0.07f, L.RopsTop - L.HoodTop, 0.07f), Dark);
        m.Box(new Vector3(0, L.RopsTop - 0.035f, L.RopsZ), new Vector3(fh * 2f, 0.07f, 0.07f), Dark);
        m.Tube(new Vector3(0, L.RopsTop, L.RopsZ), new Vector3(0, L.RopsTop + 0.1f, L.RopsZ), 0.055f, Beacon, 6);
        foreach (int side in new[] { 1, -1 })
        {
            HeavyMesh.Lamp(head, side * (fh - 0.08f), L.RopsTop - 0.12f, L.RopsZ + 0.05f, 0.09f, 0.07f, 0.03f, HeavyMesh.HeadLamp);
            HeavyMesh.Lamp(tail, side * (fh - 0.12f), L.HoodTop - 0.2f, L.Tail - 0.01f, 0.09f, 0.07f, 0.03f, HeavyMesh.TailLamp);
            HeavyMesh.Lamp(reverse, side * (fh - 0.28f), L.HoodTop - 0.2f, L.Tail - 0.01f, 0.06f, 0.06f, 0.03f, HeavyMesh.White);
        }

        var cockpit = HeavyCabin.Build(m, Frame, Gauges, 0f, 1f, System.Array.Empty<(string, Vector3, Vector2)>());
        var seats = new[] { new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, L.Floor) };
        // the vibration button on its console, right of the wheel: what a VR hand pokes
        var button = VibrationButton(cockpit.Seat.Hip);
        m.Box(button with { Y = (L.Floor + button.Y) * 0.5f - 0.02f }, new Vector3(0.12f, button.Y - L.Floor - 0.04f, 0.14f), Dark);
        m.Tube(button - new Vector3(0, 0.02f, 0), button + new Vector3(0, 0.02f, 0), 0.03f, new Color(0.85f, 0.15f, 0.1f), 8);

        // the rear drum turns with the rig's spin; the front one is on the front frame
        var drums = new[] { new HeavyWheel(DrumMesh(), CarMeshBuilder.Turned(new Vector3(0, L.DrumRadius, L.RearAxle)), 0f) };
        return new HeavyParts(m.Build(), head.Build(), tail.Build(), reverse.Build(), drums, System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
        };
    }

    private static ArrayMesh? _drum;

    /// <summary>A steel drum facing ±X, with its end plates and a few cleats so a turning one reads as turning.</summary>
    [Core.Showcase("Parts", "Roller drum")]
    internal static ArrayMesh DrumMesh()
    {
        if (_drum != null) return _drum;
        var s = new MeshScratch();
        s.Tube(new Vector3(-L.DrumHalf, 0, 0), new Vector3(L.DrumHalf, 0, 0), L.DrumRadius, Drum, 16);
        s.Tube(new Vector3(-L.DrumHalf - 0.015f, 0, 0), new Vector3(L.DrumHalf + 0.015f, 0, 0), L.DrumRadius * 0.6f, Dark, 10);
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.Tau / 4f;
            var at = new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * (L.DrumRadius + 0.004f);
            s.Box(at, new Vector3(L.DrumHalf * 1.9f, 0.012f, 0.05f), Drum.Darkened(0.35f), new Basis(Vector3.Right, -a));
        }
        return _drum = s.Build();
    }

    /// <summary>The front frame about the hinge: the yoke round its drum, the water tank, the scraper bar.</summary>
    [Core.Showcase("Parts", "Roller front frame")]
    internal static ArrayMesh FrontFrameMesh()
    {
        var s = new MeshScratch();
        const float fh = L.FrameHalf;
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * (L.DrumHalf + 0.05f), L.DrumRadius + 0.1f, L.FrontAxle), new Vector3(0.08f, 0.55f, 0.6f), Body.Darkened(0.15f));
        // the arm from the hinge to the front frame, under the platform
        s.Box(new Vector3(0, 0.6f, 0.3f), new Vector3(0.45f, 0.3f, 0.6f), Body.Darkened(0.1f));
        // the water tank over the drum, its filler cap, the spray bar along its foot
        s.Box(new Vector3(0, (L.DrumRadius * 2f + 0.02f + L.TankTop) * 0.5f, (0.45f + L.Nose) * 0.5f),
            new Vector3(fh * 2f, L.TankTop - L.DrumRadius * 2f - 0.02f, L.Nose - 0.45f), Body);
        s.Tube(new Vector3(0.3f, L.TankTop, 0.8f), new Vector3(0.3f, L.TankTop + 0.05f, 0.8f), 0.07f, Dark, 8);
        s.Tube(new Vector3(-L.DrumHalf, L.DrumRadius * 2f - 0.02f, L.Nose - 0.05f), new Vector3(L.DrumHalf, L.DrumRadius * 2f - 0.02f, L.Nose - 0.05f), 0.02f, Steel, 6);
        // the scraper bar that keeps the drum clean
        s.Box(new Vector3(0, L.DrumRadius, L.FrontAxle + L.DrumRadius + 0.03f), new Vector3(L.DrumHalf * 2f, 0.04f, 0.03f), Dark);
        return s.Build();
    }

    /// <summary>The drawn roller: the heavy rig (seat, cockpit, driver, rear drum) with its front frame as the child node "Front".</summary>
    public static HeavyRig CreateRig(Player.CompactRoller machine, HumanPalette? driver)
    {
        var rig = HeavyRig.Create(Parts(), driver);
        rig.Name = "CompactRoller";
        rig.AirLowAt = -1f;
        var front = RollerFront.Create();
        rig.AddChild(front);
        front.Pose(machine.Articulation, 0f);
        // the drums' hum, heard while they vibrate (its volume follows the vibration in Animate)
        rig.AddChild(new AudioStreamPlayer3D
        {
            Name = "Drums", Stream = Player.CompactRoller.DrumLoop, Bus = Audio.SfxBus.Name, UnitSize = 6f, MaxDistance = 90f,
            VolumeDb = -80f, Position = CarMeshBuilder.Turned(new Vector3(0, L.DrumRadius, 0)),
        });
        return rig;
    }

    public static RollerFront? FrontOf(Node3D? visual) => visual?.GetNodeOrNull<RollerFront>("Front");

    public static AudioStreamPlayer3D? DrumsOf(Node3D? visual) => visual?.GetNodeOrNull<AudioStreamPlayer3D>("Drums");
}

/// <summary>
/// A tandem roller's front frame (#614): swung about the hinge (turned about Y by the
/// articulation), its drum spinning. An unchanged articulation is not reassigned.
/// </summary>
public partial class RollerFront : Node3D
{
    private Node3D _spin = null!;
    private float _drawn = float.NaN;

    /// <summary>The articulation it is drawn at.</summary>
    public float Drawn => _drawn;

    public void Pose(float articulation, float drumSpin)
    {
        _spin.Rotation = new Vector3(-drumSpin, 0, 0);
        if (!float.IsNaN(_drawn) && Mathf.Abs(articulation - _drawn) < 1e-5f) return;
        _drawn = articulation;
        // node space is the authored frame turned half round: + about Y swings the front to the left
        Rotation = new Vector3(0, articulation, 0);
    }

    public static RollerFront Create()
    {
        var material = HumanMeshBuilder.FigureMaterial();
        MeshInstance3D Mesh(string name, ArrayMesh mesh) => new() { Name = name, Mesh = mesh, MaterialOverride = material };
        var node = new RollerFront { Name = "Front" };
        node.AddChild(Mesh("Frame", CompactRollerMeshBuilder.FrontFrameMesh()));
        var hub = new Node3D { Name = "Drum", Position = CarMeshBuilder.Turned(new Vector3(0, CompactRollerLayout.DrumRadius, CompactRollerLayout.FrontAxle)) };
        node._spin = new Node3D { Name = "Spin" };
        node._spin.AddChild(Mesh("Drum", CompactRollerMeshBuilder.DrumMesh()));
        hub.AddChild(node._spin);
        node.AddChild(hub);
        return node;
    }
}
