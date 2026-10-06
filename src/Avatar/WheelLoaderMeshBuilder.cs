using Godot;
using L = UnitSport.Avatar.WheelLoaderLayout;

namespace UnitSport.Avatar;

/// <summary>
/// Builds the wheel loader's meshes (#612). The rear frame — engine, counterweight, cab and its
/// cockpit, the rear wheels — is the heavy rig's body; the front frame is its child node
/// <see cref="WheelLoaderFront"/>, swung about the hinge to steer, carrying the front wheels, the
/// lift arm and the bucket, each a pivot moved in place, never rebuilt.
/// </summary>
public static class WheelLoaderMeshBuilder
{
    private static readonly Color Body = new(0.95f, 0.72f, 0.08f);
    private static readonly Color Dark = new(0.15f, 0.15f, 0.17f);
    private static readonly Color Tyre = new(0.09f, 0.09f, 0.1f);
    private static readonly Color Steel = new(0.66f, 0.67f, 0.70f);
    private static readonly Color Chrome = new(0.85f, 0.86f, 0.88f);
    private static readonly Color Beacon = new(1f, 0.5f, 0.05f);

    /// <summary>The cab as its cockpit is derived from: a wide cab with a dash, a wheel and a view over the bucket.</summary>
    public static readonly CabFrame Frame = new()
    {
        Front = L.CabFront - 0.05f, Floor = L.CabFloor, Ceiling = L.CabTop - 0.08f,
        WsBase = 2.05f, WsTop = 3.2f, DashTop = 2.2f,
        InnerHalf = L.CabHalf - 0.05f, Nose = 0.35f, DriverX = 0f, HipRise = 0.5f, Recline = 0.15f,
        ColumnTilt = 0.8f, WheelRadius = 0.19f, DashToX = -(L.CabHalf - 0.05f), Clutch = false, PassengerSeat = false,
    };

    /// <summary>Its dials: a speedometer to 40 km/h, the tach to 2400 rpm.</summary>
    public static readonly CarGauges Gauges = HeavyCabin.GaugesFor(40f, 2400f);

    public static HeavyParts Parts()
    {
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var reverse = new MeshScratch();
        const float rh = L.RearHalf;

        // ---- the rear frame: chassis to the hinge, the engine cover, the counterweight ----------
        m.Box(new Vector3(0, 1.1f, (L.Tail + 0.3f) * 0.5f), new Vector3(rh * 1.4f, 0.6f, -L.Tail + 0.3f), Dark);
        m.Box(new Vector3(0, (1.35f + L.EngineTop) * 0.5f, (L.Tail + L.CabBack) * 0.5f), new Vector3(rh * 2f, L.EngineTop - 1.35f, L.CabBack - L.Tail), Body);
        m.Box(new Vector3(0, L.EngineTop + 0.02f, (L.Tail + L.CabBack) * 0.5f), new Vector3(rh * 2f - 0.12f, 0.05f, L.CabBack - L.Tail - 0.2f), Body.Darkened(0.18f));
        m.Box(new Vector3(0, 1.4f, L.Tail + 0.2f), new Vector3(rh * 2f, 1.0f, 0.4f), Dark.Lightened(0.08f));
        m.Tube(new Vector3(-0.7f, L.EngineTop, -2.2f), new Vector3(-0.7f, L.EngineTop + 0.5f, -2.2f), 0.08f, Dark, 6);
        // the rear fenders over the wheels, the hinge's two plates
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (L.HalfTrack + 0.05f), L.WheelRadius * 2f + 0.05f, L.RearAxle), new Vector3(L.WheelWidth + 0.15f, 0.06f, 1.4f), Body.Darkened(0.1f));
        foreach (float y in new[] { 0.85f, 1.45f })
            m.Box(new Vector3(0, y, 0), new Vector3(0.9f, 0.12f, 0.7f), Steel.Darkened(0.4f));

        // ---- the cab: posts, roof, back wall and a skirt; the glass left open ------------------
        const float c = L.CabHalf, cb = L.CabBack, cf = L.CabFront, fl = L.CabFloor, ct = L.CabTop;
        m.Box(new Vector3(0, (1.35f + fl) * 0.5f, (cb + cf) * 0.5f), new Vector3(c * 2f, fl - 1.35f, cf - cb), Body);
        foreach (int side in new[] { 1, -1 })
            foreach (float z in new[] { cb + 0.05f, cf - 0.05f })
                m.Box(new Vector3(side * (c - 0.05f), (fl + ct) * 0.5f, z), new Vector3(0.09f, ct - fl, 0.09f), Dark);
        m.Box(new Vector3(0, ct - 0.05f, (cb + cf) * 0.5f), new Vector3(c * 2f + 0.1f, 0.1f, cf - cb + 0.12f), Body);
        m.Box(new Vector3(0, (fl + ct) * 0.5f, cb + 0.03f), new Vector3(c * 2f, ct - fl, 0.06f), Dark.Lightened(0.05f));
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (c - 0.02f), fl + 0.35f, (cb + cf) * 0.5f), new Vector3(0.04f, 0.7f, cf - cb - 0.1f), Body);
        // the ladder up to it behind the left front wheel
        for (int i = 0; i < 5; i++)
            m.Box(new Vector3(rh + 0.2f, 0.35f + i * 0.3f, -0.7f), new Vector3(0.3f, 0.04f, 0.18f), Steel);
        m.Tube(new Vector3(0.45f, ct, cb + 0.35f), new Vector3(0.45f, ct + 0.13f, cb + 0.35f), 0.075f, Beacon, 6);
        foreach (int side in new[] { 1, -1 })
        {
            HeavyMesh.Lamp(head, side * (c - 0.15f), ct - 0.15f, cf + 0.04f, 0.16f, 0.1f, 0.05f, HeavyMesh.HeadLamp);
            HeavyMesh.Lamp(tail, side * (rh - 0.2f), 1.7f, L.Tail - 0.01f, 0.16f, 0.12f, 0.04f, HeavyMesh.TailLamp);
            HeavyMesh.Lamp(reverse, side * (rh - 0.45f), 1.7f, L.Tail - 0.01f, 0.1f, 0.1f, 0.04f, HeavyMesh.White);
        }

        var cockpit = HeavyCabin.Build(m, Frame, Gauges, 0f, 1f, System.Array.Empty<(string, Vector3, Vector2)>());
        var seats = new[] { new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, L.CabFloor) };
        // the arm's two levers, right of the seat: what a VR hand grips (XrCabControls "loader")
        var lever = cockpit.Seat.Hip + new Vector3(-0.34f, 0.1f, 0.3f);
        m.Box(lever with { Y = (fl + lever.Y) * 0.5f }, new Vector3(0.14f, lever.Y - fl, 0.32f), Dark);
        foreach (float dz in new[] { -0.06f, 0.06f })
        {
            m.Tube(lever + new Vector3(0, 0, dz), lever + new Vector3(0, 0.15f, dz + 0.02f), 0.013f, Steel, 5);
            m.Box(lever + new Vector3(0, 0.17f, dz + 0.02f), new Vector3(0.035f, 0.05f, 0.035f), Dark.Lightened(0.2f));
        }

        // the rear wheels turn with the rig's spin; the front ones are on the front frame
        var wheels = new[]
        {
            new HeavyWheel(Wheel(), CarMeshBuilder.Turned(new Vector3(L.HalfTrack, L.WheelRadius, L.RearAxle)), 0f),
            new HeavyWheel(Wheel(), CarMeshBuilder.Turned(new Vector3(-L.HalfTrack, L.WheelRadius, L.RearAxle)), 0f),
        };
        return new HeavyParts(m.Build(), head.Build(), tail.Build(), reverse.Build(), wheels, System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
        };
    }

    private static ArrayMesh? _wheel;

    /// <summary>One loader tyre on its rim, facing ±X: big, with a chunky tread band.</summary>
    [Core.Showcase("Parts", "Wheel loader wheel")]
    internal static ArrayMesh Wheel()
    {
        if (_wheel != null) return _wheel;
        var s = new MeshScratch();
        float w = L.WheelWidth * 0.5f;
        s.Tube(new Vector3(-w, 0, 0), new Vector3(w, 0, 0), L.WheelRadius, Tyre, 12);
        s.Tube(new Vector3(-w - 0.015f, 0, 0), new Vector3(w + 0.015f, 0, 0), L.WheelRadius * 0.55f, Body.Darkened(0.2f), 8);
        // the tread's lugs, so a turning wheel reads as one
        for (int i = 0; i < 12; i++)
        {
            float a = i * Mathf.Tau / 12f;
            var at = new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * (L.WheelRadius - 0.02f);
            s.Box(at, new Vector3(L.WheelWidth * 0.9f, 0.08f, 0.14f), Tyre.Lightened(0.08f),
                new Basis(Vector3.Right, -a));
        }
        return _wheel = s.Build();
    }

    /// <summary>The front frame about the hinge: its chassis, its fenders, the arm's towers.</summary>
    [Core.Showcase("Parts", "Wheel loader front frame")]
    internal static ArrayMesh FrontFrameMesh()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 1.1f, 1.2f), new Vector3(1.3f, 0.6f, 2.4f), Dark);
        foreach (int side in new[] { 1, -1 })
        {
            s.Box(new Vector3(side * (L.HalfTrack + 0.05f), L.WheelRadius * 2f + 0.05f, L.FrontAxle), new Vector3(L.WheelWidth + 0.15f, 0.06f, 1.4f), Body.Darkened(0.1f));
            // the tower the arm pivots on
            s.Box(new Vector3(side * 0.5f, (1.2f + L.ArmPivot.Y) * 0.5f, L.ArmPivot.Z), new Vector3(0.2f, L.ArmPivot.Y - 1.2f + 0.2f, 0.45f), Body);
        }
        return s.Build();
    }

    /// <summary>The lift arm from its pivot along +Z to the bucket's pin, its two booms and their cross tube, and the lift ram.</summary>
    [Core.Showcase("Parts", "Wheel loader lift arm")]
    internal static ArrayMesh ArmMesh()
    {
        var s = new MeshScratch();
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * 0.5f, 0, L.ArmLength * 0.5f), new Vector3(0.18f, 0.42f, L.ArmLength + 0.3f), Body);
        s.Tube(new Vector3(-0.5f, -0.05f, L.ArmLength * 0.55f), new Vector3(0.5f, -0.05f, L.ArmLength * 0.55f), 0.13f, Body.Darkened(0.1f), 8);
        // the tilt ram along the top, the lift rams below
        s.Tube(new Vector3(0, 0.35f, 0.3f), new Vector3(0, 0.3f, L.ArmLength * 0.7f), 0.09f, Steel, 6);
        s.Tube(new Vector3(0, 0.3f, L.ArmLength * 0.5f), new Vector3(0, 0.28f, L.ArmLength * 0.85f), 0.06f, Chrome, 6);
        foreach (int side in new[] { 1, -1 })
            s.Tube(new Vector3(side * 0.5f, -0.35f, 0.2f), new Vector3(side * 0.5f, -0.25f, L.ArmLength * 0.45f), 0.08f, Steel, 6);
        s.Tube(new Vector3(-0.6f, 0, L.ArmLength), new Vector3(0.6f, 0, L.ArmLength), 0.1f, Dark, 8);
        return s.Build();
    }

    /// <summary>The bucket about its pin: its back, floor and sides, and the cutting edge at its lip.</summary>
    [Core.Showcase("Parts", "Wheel loader bucket")]
    internal static ArrayMesh BucketMesh()
    {
        var s = new MeshScratch();
        const float w = L.BucketHalf, r = L.BucketReach, h = L.BucketHeight;
        s.Box(new Vector3(0, -h * 0.4f, 0.12f), new Vector3(w * 2f, h, 0.12f), Dark.Lightened(0.08f));
        s.Box(new Vector3(0, -h * 0.9f, r * 0.5f), new Vector3(w * 2f, 0.1f, r), Dark.Lightened(0.08f));
        s.Box(new Vector3(0, h * 0.12f, 0.35f), new Vector3(w * 2f, 0.1f, 0.5f), Dark.Lightened(0.08f));
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * w, -h * 0.4f, r * 0.45f), new Vector3(0.06f, h, r * 0.9f), Body.Darkened(0.1f));
        s.Box(new Vector3(0, -h * 0.9f, r + 0.02f), new Vector3(w * 2f + 0.05f, 0.06f, 0.1f), Steel);
        return s.Build();
    }

    /// <summary>The drawn loader: the heavy rig (cab, cockpit, driver, rear wheels) with its front frame as the child node "Front".</summary>
    public static HeavyRig CreateRig(Player.WheelLoader machine, HumanPalette? driver)
    {
        var rig = HeavyRig.Create(Parts(), driver);
        rig.Name = "WheelLoader";
        rig.AirLowAt = -1f;
        var front = WheelLoaderFront.Create(machine.Forks);
        rig.AddChild(front);
        front.Carrying = machine.Carrying;
        front.Pose(machine.Articulation, machine.Lift, machine.Tilt, 0f);
        return rig;
    }

    public static WheelLoaderFront? FrontOf(Node3D? visual) => visual?.GetNodeOrNull<WheelLoaderFront>("Front");

    /// <summary>The fork carriage about the arm's pin, level (#615): the frame across the arm's two booms, the backrest, and two tines.</summary>
    [Core.Showcase("Parts", "Wheel loader forks")]
    internal static ArrayMesh ForksMesh()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0f, 0.2f), new Vector3(1.6f, 0.62f, 0.1f), Dark);
        s.Box(new Vector3(0, 0.6f, 0.2f), new Vector3(1.4f, 0.6f, 0.05f), Dark.Lightened(0.1f));
        s.Tube(new Vector3(-0.6f, 0, 0), new Vector3(0.6f, 0, 0), 0.1f, Dark, 8);
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * (L.TineHalfSpan - 0.07f);
            s.Box(new Vector3(x, -(L.ForkTop - 0.05f) * 0.5f, L.ForkFace - 0.03f), new Vector3(0.14f, L.ForkTop - 0.05f + 0.3f, 0.06f), Steel.Darkened(0.2f));
            s.Box(new Vector3(x, -L.ForkTop - 0.03f, L.ForkFace + L.ForkLength * 0.5f), new Vector3(0.14f, 0.06f, L.ForkLength), Steel.Darkened(0.2f));
        }
        return s.Build();
    }
}

/// <summary>
/// A wheel loader's front frame (#612): swung about the hinge (turned about Y by the articulation),
/// its two wheels spinning, its lift arm raised about its pivot and the bucket tilted about its
/// pin. Nothing rebuilt; an unchanged pose is not reassigned.
/// </summary>
public partial class WheelLoaderFront : Node3D
{
    private Node3D _arm = null!, _bucket = null!;
    private readonly Node3D[] _spin = new Node3D[2];
    private Vector3 _drawn = new(float.NaN, 0, 0);

    /// <summary>The articulation, lift and tilt it is drawn at.</summary>
    public Vector3 Drawn => _drawn;

    private bool _forks;

    /// <summary>What rides on the forks or in the bucket (#615), drawn on the carriage or the bucket's floor; null when empty.</summary>
    public Node3D? Load { get; private set; }

    private int _carrying;

    /// <summary>What is on the forks or in the bucket, as <c>WheelLoader.Carrying</c> holds it: drawn by <see cref="Items.PalletNode.Carried"/>, rebuilt only when it changes.</summary>
    public int Carrying
    {
        get => _carrying;
        set
        {
            if (value == _carrying) return;
            _carrying = value;
            if (Load != null) { Load.QueueFree(); Load = null; }
            if (Items.Pallets.LoadCarried(value) is not { } load) return;
            Load = Items.PalletNode.Carried(load, Items.Pallets.CarriedAcross(value));
            Load.Name = "Load";
            // on the tines, where the rule says one rides; or on the bucket's floor, its underside on it
            Load.Position = _forks
                ? CarMeshBuilder.Turned(new Vector3(0f, -WheelLoaderLayout.ForkTop, WheelLoaderLayout.ForkFace + Items.Pallets.LoadAhead))
                : CarMeshBuilder.Turned(new Vector3(0f, WheelLoaderLayout.BucketFloorY + Items.Pallets.Seat, WheelLoaderLayout.BucketFloorZ));
            _bucket.AddChild(Load);
        }
    }

    public void Pose(float articulation, float lift, float tilt, float wheelSpin)
    {
        foreach (var w in _spin) w.Rotation = new Vector3(-wheelSpin, 0, 0);
        var pose = new Vector3(articulation, lift, tilt);
        if (!float.IsNaN(_drawn.X) && (pose - _drawn).LengthSquared() < 1e-8f) return;
        _drawn = pose;
        // node space is the authored frame turned half round: + about Y swings the front to the
        // left, + about X raises a part that points along −Z
        Rotation = new Vector3(0, articulation, 0);
        _arm.Rotation = new Vector3(lift, 0, 0);
        // a bucket tilts against the arm; a fork carriage is levelled by the linkage, then pitched
        _bucket.Rotation = new Vector3(_forks ? tilt - lift : tilt, 0, 0);
    }

    public static WheelLoaderFront Create(bool forks = false)
    {
        var material = HumanMeshBuilder.FigureMaterial();
        MeshInstance3D Mesh(string name, ArrayMesh mesh) => new() { Name = name, Mesh = mesh, MaterialOverride = material };
        var node = new WheelLoaderFront { Name = "Front", _forks = forks };
        node.AddChild(Mesh("Frame", WheelLoaderMeshBuilder.FrontFrameMesh()));
        int k = 0;
        foreach (int side in new[] { 1, -1 })
        {
            var hub = new Node3D { Name = $"Wheel{k}", Position = CarMeshBuilder.Turned(new Vector3(side * WheelLoaderLayout.HalfTrack, WheelLoaderLayout.WheelRadius, WheelLoaderLayout.FrontAxle)) };
            var spin = new Node3D { Name = "Spin" };
            spin.AddChild(Mesh("Tyre", WheelLoaderMeshBuilder.Wheel()));
            hub.AddChild(spin);
            node.AddChild(hub);
            node._spin[k++] = spin;
        }
        node._arm = new Node3D { Name = "Arm", Position = CarMeshBuilder.Turned(WheelLoaderLayout.ArmPivot) };
        node._arm.AddChild(Mesh("Booms", WheelLoaderMeshBuilder.ArmMesh()));
        node.AddChild(node._arm);
        node._bucket = new Node3D { Name = "Bucket", Position = new Vector3(0, 0, -WheelLoaderLayout.ArmLength) };
        node._bucket.AddChild(forks ? Mesh("Forks", WheelLoaderMeshBuilder.ForksMesh()) : Mesh("Bucket", WheelLoaderMeshBuilder.BucketMesh()));
        node._arm.AddChild(node._bucket);
        return node;
    }
}
