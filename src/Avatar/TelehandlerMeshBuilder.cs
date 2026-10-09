using Godot;
using L = UnitSport.Avatar.TelehandlerLayout;

namespace UnitSport.Avatar;

/// <summary>
/// Builds the telehandler's meshes (#614). The chassis, the cab on its left and its cockpit, the
/// engine on its right and the front wheels (steered by the rig) are the heavy rig's body; the boom,
/// the fork carriage and the rear wheels (steered by the mode) are its child node
/// <see cref="TelehandlerBoom"/>, each a pivot moved in place, never rebuilt.
/// </summary>
public static class TelehandlerMeshBuilder
{
    private static readonly Color Body = new(0.93f, 0.2f, 0.12f);       // the red some makes come in
    private static readonly Color Dark = new(0.15f, 0.15f, 0.17f);
    private static readonly Color Tyre = new(0.09f, 0.09f, 0.1f);
    private static readonly Color Steel = new(0.66f, 0.67f, 0.70f);
    private static readonly Color Chrome = new(0.85f, 0.86f, 0.88f);
    private static readonly Color Beacon = new(1f, 0.5f, 0.05f);

    /// <summary>The cab on the left of the boom: its dash from wall to wall, the driver in its middle.</summary>
    public static readonly CabFrame Frame = new()
    {
        Front = L.CabFront - 0.05f, Floor = L.CabFloor, Ceiling = L.CabTop - 0.08f,
        WsBase = 1.35f, WsTop = 2.35f, DashTop = 1.45f,
        InnerHalf = L.CabLeft - 0.04f, Nose = 0.35f, DriverX = (L.CabLeft + L.CabRight) * 0.5f, HipRise = 0.5f, Recline = 0.15f,
        ColumnTilt = 0.85f, WheelRadius = 0.17f, DashToX = L.CabRight + 0.04f, Clutch = false, PassengerSeat = false,
    };

    /// <summary>Its dials: a speedometer to 40 km/h, the tach to 2600 rpm.</summary>
    public static readonly CarGauges Gauges = HeavyCabin.GaugesFor(40f, 2600f);

    public static HeavyParts Parts()
    {
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var reverse = new MeshScratch();
        const float ch = L.ChassisHalf;

        // ---- the chassis: a low frame between the axles, the counterweight at the back ---------
        m.Box(new Vector3(0, 0.8f, (L.Nose + L.Tail) * 0.5f), new Vector3(0.9f, 0.5f, L.Nose - L.Tail), Dark);
        m.Box(new Vector3(0, 0.95f, L.Tail + 0.35f), new Vector3(ch * 2f, 0.9f, 0.7f), Body.Darkened(0.15f));
        foreach (int side in new[] { 1, -1 })
            foreach (float axle in new[] { L.FrontAxle, L.RearAxle })
                m.Box(new Vector3(side * L.HalfTrack, L.WheelRadius * 2f + 0.05f, axle), new Vector3(L.WheelWidth + 0.12f, 0.05f, 1.15f), Body.Darkened(0.1f));
        // the engine on the right of the boom, its grille facing out
        // (low and outboard, so the boom lies along the middle above it in plain view)
        m.Box(new Vector3(-0.82f, 1.0f, -0.5f), new Vector3(0.56f, 0.9f, 2.4f), Body);
        m.Box(new Vector3(-1.1f, 1.0f, -0.5f), new Vector3(0.02f, 0.55f, 1.6f), Dark);
        m.Tube(new Vector3(-0.85f, 1.45f, -1.2f), new Vector3(-0.85f, 1.9f, -1.2f), 0.06f, Dark, 6);
        // the boom's mount at the back, between the cab and the engine
        m.Box(new Vector3(L.BoomPivot.X, (1.05f + L.BoomPivot.Y + 0.15f) * 0.5f, L.BoomPivot.Z), new Vector3(0.5f, L.BoomPivot.Y + 0.15f - 1.05f, 0.5f), Body.Darkened(0.2f));
        // the carriage's front frame between the front wheels, under where the forks come down
        m.Box(new Vector3(0, 0.75f, L.Nose - 0.2f), new Vector3(1.2f, 0.4f, 0.4f), Body.Darkened(0.15f));

        // ---- the cab: posts, roof, back wall, the door frame; the glass left open ----------------
        const float cl = L.CabLeft, cr = L.CabRight, cb = L.CabBack, cf = L.CabFront, fl = L.CabFloor, ct = L.CabTop;
        float cx = (cl + cr) * 0.5f, cw = cl - cr;
        m.Box(new Vector3(cx, (0.9f + fl) * 0.5f, (cb + cf) * 0.5f), new Vector3(cw, fl - 0.9f, cf - cb), Body);
        foreach (float x in new[] { cl - 0.04f, cr + 0.04f })
            foreach (float z in new[] { cb + 0.04f, cf - 0.04f })
                m.Box(new Vector3(x, (fl + ct) * 0.5f, z), new Vector3(0.07f, ct - fl, 0.07f), Dark);
        m.Box(new Vector3(cx, ct - 0.04f, (cb + cf) * 0.5f), new Vector3(cw + 0.06f, 0.08f, cf - cb + 0.08f), Body);
        m.Box(new Vector3(cx, (fl + ct) * 0.5f, cb + 0.03f), new Vector3(cw, ct - fl, 0.05f), Dark.Lightened(0.05f));
        m.Box(new Vector3(cr + 0.02f, fl + 0.3f, (cb + cf) * 0.5f), new Vector3(0.04f, 0.6f, cf - cb - 0.1f), Body);
        m.Tube(new Vector3(cx, ct, cb + 0.25f), new Vector3(cx, ct + 0.12f, cb + 0.25f), 0.07f, Beacon, 6);
        foreach (float x in new[] { cl - 0.12f, cr + 0.12f })
            HeavyMesh.Lamp(head, x, ct - 0.12f, cf + 0.03f, 0.12f, 0.09f, 0.04f, HeavyMesh.HeadLamp);
        foreach (int side in new[] { 1, -1 })
        {
            HeavyMesh.Lamp(tail, side * (ch - 0.15f), 1.15f, L.Tail - 0.01f, 0.14f, 0.1f, 0.04f, HeavyMesh.TailLamp);
            HeavyMesh.Lamp(reverse, side * (ch - 0.38f), 1.15f, L.Tail - 0.01f, 0.09f, 0.09f, 0.04f, HeavyMesh.White);
        }

        var cockpit = HeavyCabin.Build(m, Frame, Gauges, 0f, 1f, System.Array.Empty<(string, Vector3, Vector2)>());
        var seats = new[] { new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, L.CabFloor) };
        // the boom's joystick and the extend rocker, right of the seat (XrCabControls "telehandler")
        var stick = cockpit.Seat.Hip + new Vector3(-0.3f, 0.08f, 0.3f);
        m.Box(stick with { Y = (fl + stick.Y) * 0.5f }, new Vector3(0.12f, stick.Y - fl, 0.3f), Dark);
        m.Tube(stick, stick + new Vector3(0, 0.15f, 0.02f), 0.014f, Steel, 5);
        m.Box(stick + new Vector3(0, 0.18f, 0.02f), new Vector3(0.04f, 0.07f, 0.05f), Dark.Lightened(0.2f));

        // the front wheels steer with the rig; the rear ones are the boom node's, steered by the mode
        var wheels = new[]
        {
            new HeavyWheel(Wheel(), CarMeshBuilder.Turned(new Vector3(L.HalfTrack, L.WheelRadius, L.FrontAxle)), 1f),
            new HeavyWheel(Wheel(), CarMeshBuilder.Turned(new Vector3(-L.HalfTrack, L.WheelRadius, L.FrontAxle)), 1f),
        };
        return new HeavyParts(m.Build(), head.Build(), tail.Build(), reverse.Build(), wheels, System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
        };
    }

    private static ArrayMesh? _wheel;

    /// <summary>One tyre on its rim, facing ±X, with a bar tread.</summary>
    [Core.Showcase("Parts", "Telehandler wheel")]
    internal static ArrayMesh Wheel()
    {
        if (_wheel != null) return _wheel;
        var s = new MeshScratch();
        float w = L.WheelWidth * 0.5f;
        s.Tube(new Vector3(-w, 0, 0), new Vector3(w, 0, 0), L.WheelRadius, Tyre, 12);
        s.Tube(new Vector3(-w - 0.015f, 0, 0), new Vector3(w + 0.015f, 0, 0), L.WheelRadius * 0.55f, Steel.Darkened(0.2f), 8);
        for (int i = 0; i < 12; i++)
        {
            float a = i * Mathf.Tau / 12f;
            var at = new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * (L.WheelRadius - 0.02f);
            s.Box(at, new Vector3(L.WheelWidth * 0.9f, 0.06f, 0.1f), Tyre.Lightened(0.08f), new Basis(Vector3.Right, -a));
        }
        return _wheel = s.Build();
    }

    /// <summary>The boom's outer section from its pivot along +Z, with the lift ram under it.</summary>
    [Core.Showcase("Parts", "Telehandler boom")]
    internal static ArrayMesh BoomMesh()
    {
        var s = new MeshScratch();
        const float len = L.BoomLength;
        s.Box(new Vector3(0, 0, len * 0.5f - 0.2f), new Vector3(0.46f, 0.5f, len + 0.2f), Body);
        s.Box(new Vector3(0, 0.27f, len * 0.5f), new Vector3(0.48f, 0.04f, len * 0.9f), Body.Darkened(0.15f));
        s.Tube(new Vector3(-0.3f, 0, 0), new Vector3(0.3f, 0, 0), 0.12f, Dark, 8);
        // the lift ram from the chassis up to the boom's middle
        s.Tube(new Vector3(0, -0.45f, 1.0f), new Vector3(0, -0.28f, len * 0.45f), 0.09f, Steel, 6);
        s.Tube(new Vector3(0, -0.4f, 1.5f), new Vector3(0, -0.3f, len * 0.42f), 0.06f, Chrome, 6);
        return s.Build();
    }

    /// <summary>The inner section, its tip at its origin and its body running back inside the outer one.</summary>
    [Core.Showcase("Parts", "Telehandler boom tip")]
    internal static ArrayMesh InnerMesh()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0, -L.ExtendMax * 0.5f), new Vector3(0.34f, 0.36f, L.ExtendMax + 0.3f), Body.Lightened(0.08f));
        s.Tube(new Vector3(-0.28f, 0, 0), new Vector3(0.28f, 0, 0), 0.09f, Dark, 8);
        return s.Build();
    }

    /// <summary>The fork carriage about its pin, level: the plate, the backrest and the two forks.</summary>
    [Core.Showcase("Parts", "Telehandler forks")]
    internal static ArrayMesh CarriageMesh()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, -0.15f, 0.12f), new Vector3(1.2f, 0.7f, 0.08f), Dark);
        s.Box(new Vector3(0, 0.35f, 0.12f), new Vector3(1.0f, 0.4f, 0.04f), Dark.Lightened(0.1f));
        foreach (int side in new[] { 1, -1 })
        {
            s.Box(new Vector3(side * 0.35f, -(L.ForkDrop - 0.05f) * 0.5f, 0.17f), new Vector3(0.12f, L.ForkDrop - 0.05f, 0.05f), Steel.Darkened(0.2f));
            s.Box(new Vector3(side * 0.35f, -L.ForkDrop + 0.025f, 0.15f + L.ForkLength * 0.5f), new Vector3(0.12f, 0.05f, L.ForkLength), Steel.Darkened(0.2f));
        }
        return s.Build();
    }

    /// <summary>The drawn telehandler: the heavy rig (cab, cockpit, driver, front wheels) with the boom and the rear wheels as the child node "Boom".</summary>
    public static HeavyRig CreateRig(Player.Telehandler machine, HumanPalette? driver)
    {
        var rig = HeavyRig.Create(Parts(), driver);
        rig.Name = "Telehandler";
        rig.AirLowAt = -1f;
        var boom = TelehandlerBoom.Create();
        rig.AddChild(boom);
        boom.Pose(machine.Lift, machine.Extend, machine.Tilt, TelehandlerLayout.RearSteer(machine.Steer, machine.Mode), 0f);
        return rig;
    }

    public static TelehandlerBoom? BoomOf(Node3D? visual) => visual?.GetNodeOrNull<TelehandlerBoom>("Boom");
}

/// <summary>
/// A telehandler's moving parts past the rig (#614): the boom raised about its pivot, its inner
/// section slid out, the fork carriage kept level and tilted, and the rear wheels steered and
/// turning. An unchanged pose is not reassigned.
/// </summary>
public partial class TelehandlerBoom : Node3D
{
    private Node3D _boom = null!, _inner = null!, _carriage = null!;
    private readonly Node3D[] _rearPivot = new Node3D[2], _rearSpin = new Node3D[2];
    private Vector4 _drawn = new(float.NaN, 0, 0, 0);

    /// <summary>The lift, extension, tilt and rear wheel angle it is drawn at.</summary>
    public Vector4 Drawn => _drawn;

    /// <summary>What rides on the forks, drawn on the carriage; null when they are empty.</summary>
    public Node3D? Load { get; private set; }

    private int _carrying;

    /// <summary>
    /// What is on the forks, as <c>Telehandler.Carrying</c> holds it (#615): 0 nothing, else 1 + a
    /// pallet's load byte, drawn by <see cref="Items.PalletNode.Carried"/> on the carriage, where the
    /// pallets' rule says it rides. Rebuilt only when it changes.
    /// </summary>
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
            Load.Position = CarMeshBuilder.Turned(new Vector3(0f, -TelehandlerLayout.ForkTop, TelehandlerLayout.ForkFace + Items.Pallets.LoadAhead));
            _carriage.AddChild(Load);
        }
    }

    public void Pose(float lift, float extend, float tilt, float rearSteer, float wheelSpin)
    {
        foreach (var s in _rearSpin) s.Rotation = new Vector3(-wheelSpin, 0, 0);
        var pose = new Vector4(lift, extend, tilt, rearSteer);
        if (!float.IsNaN(_drawn.X) && (pose - _drawn).LengthSquared() < 1e-8f) return;
        _drawn = pose;
        // node space is the authored frame turned half round: + about X raises a part along −Z
        _boom.Rotation = new Vector3(lift, 0, 0);
        _inner.Position = new Vector3(0, 0, -(TelehandlerLayout.BoomLength + extend));
        // self-levelling: the carriage turns back by the boom's angle, then by the tilt
        _carriage.Rotation = new Vector3(-lift + tilt, 0, 0);
        foreach (var p in _rearPivot) p.Rotation = new Vector3(0, rearSteer, 0);
    }

    public static TelehandlerBoom Create()
    {
        var material = HumanMeshBuilder.FigureMaterial();
        MeshInstance3D Mesh(string name, ArrayMesh mesh) => new() { Name = name, Mesh = mesh, MaterialOverride = material };
        var node = new TelehandlerBoom { Name = "Boom" };
        node._boom = new Node3D { Name = "Outer", Position = CarMeshBuilder.Turned(TelehandlerLayout.BoomPivot) };
        node._boom.AddChild(Mesh("Outer", TelehandlerMeshBuilder.BoomMesh()));
        node.AddChild(node._boom);
        node._inner = new Node3D { Name = "Inner", Position = new Vector3(0, 0, -TelehandlerLayout.BoomLength) };
        node._inner.AddChild(Mesh("Inner", TelehandlerMeshBuilder.InnerMesh()));
        node._boom.AddChild(node._inner);
        node._carriage = new Node3D { Name = "Carriage" };
        node._carriage.AddChild(Mesh("Forks", TelehandlerMeshBuilder.CarriageMesh()));
        node._inner.AddChild(node._carriage);
        int k = 0;
        foreach (int side in new[] { 1, -1 })
        {
            var pivot = new Node3D { Name = $"Rear{k}", Position = CarMeshBuilder.Turned(new Vector3(side * TelehandlerLayout.HalfTrack, TelehandlerLayout.WheelRadius, TelehandlerLayout.RearAxle)) };
            var spin = new Node3D { Name = "Spin" };
            spin.AddChild(Mesh("Tyre", TelehandlerMeshBuilder.Wheel()));
            pivot.AddChild(spin);
            node.AddChild(pivot);
            node._rearPivot[k] = pivot;
            node._rearSpin[k++] = spin;
        }
        return node;
    }
}
