using Godot;
using L = UnitSport.Avatar.MiniDumperLayout;

namespace UnitSport.Avatar;

/// <summary>
/// Builds the mini dumper's meshes (#614): the rubber tracks and the chassis over them, the engine
/// hood under the seat behind the skip, the seat with its cockpit, the folding roll bar, and the
/// skip at the front, a part of its own that tips forward about its hinge (the heavy rig's tipping
/// node, #677). The tracks' end rollers spin as the rig's wheels.
/// </summary>
public static class MiniDumperMeshBuilder
{
    private static readonly Color Body = new(0.95f, 0.72f, 0.08f);
    private static readonly Color Dark = new(0.15f, 0.15f, 0.17f);
    private static readonly Color Rubber = new(0.09f, 0.09f, 0.1f);
    private static readonly Color Steel = new(0.66f, 0.67f, 0.70f);
    private static readonly Color Beacon = new(1f, 0.5f, 0.05f);

    /// <summary>An open seat behind the skip: the dials and levers on a low console ahead of the driver.</summary>
    public static readonly CabFrame Frame = new()
    {
        Front = 0.12f, Floor = L.Floor, Ceiling = L.RopsTop - 0.1f,
        WsBase = 1.1f, WsTop = 2.0f, DashTop = 1.15f,
        InnerHalf = L.HalfWidth - 0.05f, Nose = 0.02f, DriverX = 0f, HipRise = 0.45f, Recline = 0.12f,
        ColumnTilt = 0.85f, WheelRadius = 0.12f, DashToX = -(L.HalfWidth - 0.05f), Clutch = false, PassengerSeat = false,
    };

    /// <summary>Its dials: speed to 6 km/h, the tach to 3200 rpm.</summary>
    public static readonly CarGauges Gauges = HeavyCabin.GaugesFor(6f, 3200f);

    /// <summary>The end rollers' radius: what the tracks' speed turns.</summary>
    public const float RollerRadius = L.TrackHeight * 0.5f;

    public static HeavyParts Parts()
    {
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var reverse = new MeshScratch();
        const float hw = L.HalfWidth;

        // ---- the tracks: frames, the runs top and bottom; the end rollers are the rig's wheels ----
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * L.HalfGauge, half = L.TrackHalfLength, r = RollerRadius;
            m.Box(new Vector3(x, r, 0), new Vector3(L.ShoeWidth * 0.7f, L.TrackHeight * 0.55f, (half - r) * 2f), Dark);
            m.Box(new Vector3(x, L.TrackHeight - 0.03f, 0), new Vector3(L.ShoeWidth, 0.06f, (half - r) * 2f), Rubber);
            m.Box(new Vector3(x, 0.03f, 0), new Vector3(L.ShoeWidth, 0.06f, (half - r) * 2f), Rubber);
            for (float z = -half + r + 0.12f; z < half - r; z += 0.14f)
                m.Box(new Vector3(x, 0.0f, z), new Vector3(L.ShoeWidth - 0.04f, 0.03f, 0.05f), Rubber.Lightened(0.1f));
        }
        // ---- the chassis and the engine hood under the seat --------------------------------------
        m.Box(new Vector3(0, (L.TrackHeight + L.DeckTop) * 0.5f, (L.Nose + L.Tail) * 0.5f), new Vector3(hw * 1.5f, L.DeckTop - L.TrackHeight, L.Nose - L.Tail - 0.3f), Dark);
        m.Box(new Vector3(0, (L.DeckTop + L.HoodTop) * 0.5f, (L.Tail + L.SkipBack - 0.15f) * 0.5f), new Vector3(hw * 2f, L.HoodTop - L.DeckTop, L.SkipBack - 0.15f - L.Tail), Body);
        m.Box(new Vector3(0, L.HoodTop + 0.015f, (L.Tail + L.SkipBack - 0.15f) * 0.5f), new Vector3(hw * 2f - 0.08f, 0.03f, L.SkipBack - 0.25f - L.Tail), Body.Darkened(0.2f));
        // the skip's cradle and its ram's foot ahead of the hood
        m.Box(new Vector3(0, (L.DeckTop + L.SkipFloor) * 0.5f, (L.SkipBack + L.SkipFront) * 0.5f), new Vector3(0.6f, L.SkipFloor - L.DeckTop, L.SkipFront - L.SkipBack), Dark.Lightened(0.08f));
        m.Tube(new Vector3(0, L.DeckTop, L.SkipBack - 0.05f), new Vector3(0, L.SkipFloor + 0.05f, L.SkipBack + 0.25f), 0.05f, Steel, 6);
        // the roll bar behind the seat, and the beacon on it
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (hw - 0.04f), (L.DeckTop + L.RopsTop) * 0.5f, L.RopsZ), new Vector3(0.06f, L.RopsTop - L.DeckTop, 0.06f), Dark);
        m.Box(new Vector3(0, L.RopsTop - 0.03f, L.RopsZ), new Vector3(hw * 2f, 0.06f, 0.06f), Dark);
        m.Tube(new Vector3(0.25f, L.RopsTop, L.RopsZ), new Vector3(0.25f, L.RopsTop + 0.09f, L.RopsZ), 0.05f, Beacon, 6);
        foreach (int side in new[] { 1, -1 })
        {
            HeavyMesh.Lamp(head, side * (hw - 0.08f), L.RopsTop - 0.1f, L.RopsZ + 0.05f, 0.08f, 0.06f, 0.03f, HeavyMesh.HeadLamp);
            HeavyMesh.Lamp(tail, side * (hw - 0.1f), L.HoodTop - 0.15f, L.Tail - 0.01f, 0.08f, 0.06f, 0.03f, HeavyMesh.TailLamp);
            HeavyMesh.Lamp(reverse, side * (hw - 0.25f), L.HoodTop - 0.15f, L.Tail - 0.01f, 0.06f, 0.06f, 0.03f, HeavyMesh.White);
        }

        var cockpit = HeavyCabin.Build(m, Frame, Gauges, 0f, 1f, System.Array.Empty<(string, Vector3, Vector2)>());
        var seats = new[] { new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, L.Floor) };

        // the end rollers: the rig spins them with the tracks' speed
        var wheels = new List<HeavyWheel>();
        foreach (int side in new[] { 1, -1 })
            foreach (int end in new[] { 1, -1 })
                wheels.Add(new HeavyWheel(Roller(), CarMeshBuilder.Turned(new Vector3(side * L.HalfGauge, RollerRadius, end * (L.TrackHalfLength - RollerRadius))), 0f));

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), reverse.Build(), wheels.ToArray(), System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
            Tip = (SkipMesh(), CarMeshBuilder.Turned(L.Hinge), L.TipAngle),
            Bed = L.Bed,
        };
    }

    private static ArrayMesh? _roller;

    /// <summary>A track's end roller with the rubber round it, facing ±X.</summary>
    [Core.Showcase("Parts", "Mini dumper roller")]
    internal static ArrayMesh Roller()
    {
        if (_roller != null) return _roller;
        var s = new MeshScratch();
        float w = L.ShoeWidth * 0.5f;
        s.Tube(new Vector3(-w, 0, 0), new Vector3(w, 0, 0), RollerRadius, Rubber, 10);
        s.Tube(new Vector3(-w - 0.01f, 0, 0), new Vector3(w + 0.01f, 0, 0), RollerRadius * 0.55f, Steel.Darkened(0.3f), 8);
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.Tau / 6f;
            s.Box(new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * (RollerRadius - 0.01f), new Vector3(L.ShoeWidth, 0.03f, 0.05f), Rubber.Lightened(0.1f),
                new Basis(Vector3.Right, -a));
        }
        return _roller = s.Build();
    }

    /// <summary>
    /// The skip about its hinge low at its front: a floor sloping up to a lip, two flared sides, the
    /// back wall toward the driver. It tips forward, its back rising (#614).
    /// </summary>
    [Core.Showcase("Parts", "Mini dumper skip")]
    internal static ArrayMesh SkipMesh()
    {
        var s = new MeshScratch();
        const float back = L.SkipBack, front = L.SkipFront, floor = L.SkipFloor, top = L.SkipTop, half = L.SkipHalf;
        s.Box(new Vector3(0, floor + 0.03f, (back + front - 0.3f) * 0.5f), new Vector3(half * 2f, 0.06f, front - back - 0.3f), Body);
        // the front slopes up to the lip, so a load slides out over it
        s.Box(new Vector3(0, floor + 0.25f, front - 0.15f), new Vector3(half * 2f, 0.06f, 0.6f), Body, new Basis(Vector3.Right, -0.85f));
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * half, (floor + top) * 0.5f, (back + front) * 0.5f), new Vector3(0.05f, top - floor, front - back), Body.Darkened(0.08f));
        s.Box(new Vector3(0, (floor + top) * 0.5f, back + 0.03f), new Vector3(half * 2f, top - floor, 0.06f), Body);
        s.Box(new Vector3(0, top - 0.02f, back + 0.03f), new Vector3(half * 2f + 0.06f, 0.06f, 0.1f), Dark);
        return s.Build(L.Hinge);
    }

    /// <summary>The drawn mini dumper: the heavy rig (seat, cockpit, driver, the tracks' rollers, the skip on its tipping node).</summary>
    public static HeavyRig CreateRig(Player.MiniDumper machine, HumanPalette? driver)
    {
        var rig = HeavyRig.Create(Parts(), driver);
        rig.Name = "MiniDumper";
        rig.AirLowAt = -1f;
        rig.Tipped = machine.Tipped;
        rig.BedLoad = machine.BedLoad;
        return rig;
    }
}
