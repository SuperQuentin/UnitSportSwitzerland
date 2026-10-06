using Godot;
using L = UnitSport.Avatar.ExcavatorLayout;

namespace UnitSport.Avatar;

/// <summary>
/// Builds the excavator's meshes (#611). The upper structure — house, counterweight, cab and its
/// cockpit — is the heavy rig's body and turns with the vehicle's yaw, which is the cab's; the
/// rest is the <see cref="ExcavatorArm"/> node: the undercarriage turned back by the slew, and the
/// boom, stick and bucket, each a pivot node rotated in place, never rebuilt.
/// </summary>
public static class ExcavatorMeshBuilder
{
    private static readonly Color Body = new(0.95f, 0.72f, 0.08f);      // construction-machine yellow
    private static readonly Color Dark = new(0.15f, 0.15f, 0.17f);
    private static readonly Color Track = new(0.11f, 0.11f, 0.12f);
    private static readonly Color Steel = new(0.66f, 0.67f, 0.70f);
    private static readonly Color Chrome = new(0.85f, 0.86f, 0.88f);
    private static readonly Color Beacon = new(1f, 0.5f, 0.05f);

    /// <summary>
    /// The cab as its cockpit is derived from: a narrow cab in the middle of the house. Its face is
    /// pulled back behind the dials, as the forklift's is: <see cref="HeavyCabin.Build"/> draws a
    /// full-width dash ahead of a face that is ahead of them, and an operator looks down past the
    /// cab's front at the bucket — a dash across it is what would make digging blind.
    /// </summary>
    public static readonly CabFrame Frame = new()
    {
        Front = L.HouseFront - 0.55f, Floor = L.CabFloor, Ceiling = L.CabTop - 0.08f,
        WsBase = 1.45f, WsTop = 2.85f, DashTop = 1.5f,
        InnerHalf = L.CabHalf - 0.04f, Nose = 0.3f, DriverX = 0f, HipRise = 0.5f, Recline = 0.12f,
        ColumnTilt = 0.75f, WheelRadius = 0.13f, DashToX = -(L.CabHalf - 0.04f), Clutch = false, PassengerSeat = false,
    };

    /// <summary>Its dials: travel speed to 10 km/h, the tach to 2200 rpm.</summary>
    public static readonly CarGauges Gauges = HeavyCabin.GaugesFor(10f, 2200f);

    public static HeavyParts Parts()
    {
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var reverse = new MeshScratch();
        const float rt = L.RingTop, hh = L.HouseHalf;

        // ---- the house: a deck on the ring, the engine behind the cab, the counterweight behind that
        m.Box(new Vector3(0, rt + 0.12f, (L.HouseFront + L.HouseBack) * 0.5f), new Vector3(hh * 2f, 0.24f, L.HouseFront - L.HouseBack), Dark);
        m.Box(new Vector3(0, (rt + L.HouseTop) * 0.5f, -0.8f), new Vector3(hh * 2f, L.HouseTop - rt, 1.6f), Body);
        m.Box(new Vector3(0, L.HouseTop + 0.02f, -0.8f), new Vector3(hh * 2f - 0.1f, 0.04f, 1.5f), Body.Darkened(0.2f));
        // the exhaust stack and a hand rail along the engine cover
        m.Tube(new Vector3(-0.9f, L.HouseTop, -0.4f), new Vector3(-0.9f, L.HouseTop + 0.45f, -0.4f), 0.07f, Dark, 6);
        m.Box(new Vector3(-hh + 0.05f, L.HouseTop + 0.25f, -0.8f), new Vector3(0.04f, 0.04f, 1.4f), Body);
        // the counterweight: dark, rounded at its back, the bulk that lets the arm reach out
        m.Box(new Vector3(0, (rt + 2.25f) * 0.5f, (L.HouseBack - 1.6f) * 0.5f), new Vector3(hh * 2f, 2.25f - rt, -1.6f - L.HouseBack), Dark.Lightened(0.08f));
        m.Tube(new Vector3(-hh, (rt + 2.25f) * 0.5f, L.HouseBack + 0.05f), new Vector3(hh, (rt + 2.25f) * 0.5f, L.HouseBack + 0.05f), 0.55f, Dark.Lightened(0.08f), 8);
        // the tank and toolbox left of the cab, the boom's foot right of it
        m.Box(new Vector3(hh - 0.42f, (rt + 1.95f) * 0.5f, 0.65f), new Vector3(0.84f, 1.95f - rt, 1.3f), Body);
        m.Box(new Vector3(L.BoomFoot.X, (rt + L.BoomFoot.Y + 0.2f) * 0.5f, L.BoomFoot.Z - 0.3f), new Vector3(0.62f, L.BoomFoot.Y + 0.2f - rt, 1.0f), Body.Darkened(0.12f));

        // ---- the cab: posts, roof, back wall and lower panels; the glass is left open to dig through
        const float c = L.CabHalf, cb = L.CabBack, cf = L.HouseFront, fl = L.CabFloor, ct = L.CabTop;
        m.Box(new Vector3(0, (rt + fl) * 0.5f, (cb + cf) * 0.5f), new Vector3(c * 2f, fl - rt, cf - cb), Body);
        foreach (int side in new[] { 1, -1 })
            foreach (float z in new[] { cb + 0.04f, cf - 0.04f })
                m.Box(new Vector3(side * (c - 0.04f), (fl + ct) * 0.5f, z), new Vector3(0.08f, ct - fl, 0.08f), Dark);
        m.Box(new Vector3(0, ct - 0.04f, (cb + cf) * 0.5f), new Vector3(c * 2f + 0.06f, 0.08f, cf - cb + 0.06f), Body);
        m.Box(new Vector3(0, (fl + ct) * 0.5f, cb + 0.03f), new Vector3(c * 2f, ct - fl, 0.06f), Dark.Lightened(0.05f));
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (c - 0.02f), fl + 0.35f, (cb + cf) * 0.5f), new Vector3(0.04f, 0.7f, cf - cb - 0.1f), Body);
        m.Box(new Vector3(0, fl + 0.2f, cf - 0.03f), new Vector3(c * 2f, 0.4f, 0.04f), Body);
        // the beacon on the roof, work lamps along its front, tail lamps on the counterweight
        m.Tube(new Vector3(0.3f, ct, cb + 0.3f), new Vector3(0.3f, ct + 0.13f, cb + 0.3f), 0.075f, Beacon, 6);
        foreach (int side in new[] { 1, -1 })
        {
            HeavyMesh.Lamp(head, side * (c - 0.12f), ct - 0.12f, cf + 0.02f, 0.14f, 0.1f, 0.04f, HeavyMesh.HeadLamp);
            HeavyMesh.Lamp(tail, side * (hh - 0.2f), 1.9f, L.HouseBack - 0.5f, 0.16f, 0.1f, 0.04f, HeavyMesh.TailLamp);
            HeavyMesh.Lamp(reverse, side * (hh - 0.45f), 1.9f, L.HouseBack - 0.5f, 0.1f, 0.1f, 0.04f, HeavyMesh.White);
        }

        // the cockpit: seat, the small wheel, the dials, the pedals; no mirrors
        var cockpit = HeavyCabin.Build(m, Frame, Gauges, 0f, 1f, System.Array.Empty<(string, Vector3, Vector2)>());
        var seats = new[] { new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, L.CabFloor) };
        // the two joysticks either side of the seat: what a VR hand grips (XrCabControls "excavator")
        foreach (int side in new[] { 1, -1 })
        {
            var at = cockpit.Seat.Hip + new Vector3(side * 0.32f, 0.05f, 0.25f);
            m.Box(at with { Y = (fl + at.Y) * 0.5f }, new Vector3(0.12f, at.Y - fl, 0.3f), Dark);
            m.Tube(at, at + new Vector3(0, 0.16f, 0.02f), 0.015f, Steel, 5);
            m.Box(at + new Vector3(0, 0.19f, 0.02f), new Vector3(0.04f, 0.07f, 0.04f), Dark.Lightened(0.15f));
        }

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), reverse.Build(), System.Array.Empty<HeavyWheel>(),
            System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
        };
    }

    // ---- the undercarriage and the arm, each authored about its own pivot ---------------------

    /// <summary>The undercarriage about the slewing ring's axis: the car body, the ring, both tracks' frames and their curved ends.</summary>
    [Core.Showcase("Parts", "Excavator undercarriage")]
    internal static ArrayMesh UndercarriageMesh()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.75f, 0), new Vector3(L.HalfGauge * 2f - 0.4f, 0.5f, 2.0f), Dark);
        s.Tube(new Vector3(0, 0.98f, 0), new Vector3(0, L.RingTop, 0), 1.0f, Dark.Lightened(0.1f), 12);
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * L.HalfGauge, half = L.TrackHalfLength, r = L.TrackHeight * 0.5f;
            // the frame between the runs, the top run, the bottom run, and the two ends round the idler and sprocket
            s.Box(new Vector3(x, r, 0), new Vector3(L.ShoeWidth * 0.7f, L.TrackHeight * 0.5f, (half - r) * 2f), Body.Darkened(0.25f));
            s.Box(new Vector3(x, L.TrackHeight - 0.06f, 0), new Vector3(L.ShoeWidth, 0.12f, (half - r) * 2f), Track);
            s.Box(new Vector3(x, 0.06f, 0), new Vector3(L.ShoeWidth, 0.12f, (half - r) * 2f), Track);
            foreach (int end in new[] { 1, -1 })
            {
                var hub = new Vector3(x, r, end * (half - r));
                s.Tube(hub - new Vector3(L.ShoeWidth * 0.5f, 0, 0), hub + new Vector3(L.ShoeWidth * 0.5f, 0, 0), r, Track, 10);
                s.Tube(hub - new Vector3(L.ShoeWidth * 0.52f, 0, 0), hub + new Vector3(L.ShoeWidth * 0.52f, 0, 0), r * 0.62f, Steel.Darkened(0.3f), 8);
            }
            // the bottom rollers along the frame
            for (int i = -2; i <= 2; i++)
                s.Tube(new Vector3(x - 0.2f, 0.2f, i * 0.65f), new Vector3(x + 0.2f, 0.2f, i * 0.65f), 0.12f, Steel.Darkened(0.4f), 6);
        }
        return s.Build();
    }

    /// <summary>The spacing of the grousers on a track's run, m: what a row of them is shifted by, modulo.</summary>
    public const float ShoePitch = 0.24f;

    /// <summary>A row of grousers along one run of a track, about its middle: moved, they make the track run.</summary>
    [Core.Showcase("Parts", "Excavator track shoes")]
    internal static ArrayMesh ShoeRowMesh()
    {
        var s = new MeshScratch();
        float half = L.TrackHalfLength - L.TrackHeight * 0.5f - ShoePitch;
        for (float z = -half; z <= half; z += ShoePitch)
            s.Box(new Vector3(0, 0, z), new Vector3(L.ShoeWidth + 0.02f, 0.05f, 0.07f), Track.Lightened(0.12f));
        return s.Build();
    }

    /// <summary>The boom, from its foot pivot along +Z, with its cylinders.</summary>
    [Core.Showcase("Parts", "Excavator boom")]
    internal static ArrayMesh BoomMesh()
    {
        var s = new MeshScratch();
        const float len = L.BoomLength;
        s.Box(new Vector3(0, 0, len * 0.5f), new Vector3(0.48f, 0.62f, len + 0.3f), Body);
        s.Box(new Vector3(0, 0.33f, len * 0.5f), new Vector3(0.5f, 0.04f, len * 0.9f), Body.Darkened(0.15f));
        // the boom's own rams beneath it, and the stick's ram along its back
        s.Tube(new Vector3(0, -0.45f, 0.4f), new Vector3(0, -0.36f, len * 0.42f), 0.1f, Steel, 6);
        s.Tube(new Vector3(0, -0.36f, len * 0.3f), new Vector3(0, -0.33f, len * 0.42f), 0.065f, Chrome, 6);
        s.Tube(new Vector3(0, 0.45f, len * 0.3f), new Vector3(0, 0.45f, len * 0.85f), 0.09f, Steel, 6);
        s.Tube(new Vector3(0, 0.45f, len * 0.7f), new Vector3(0, 0.45f, len * 0.95f), 0.06f, Chrome, 6);
        s.Tube(new Vector3(-0.3f, 0, len), new Vector3(0.3f, 0, len), 0.12f, Dark, 8);
        return s.Build();
    }

    /// <summary>The stick, from the boom's tip along +Z, with the bucket's ram.</summary>
    [Core.Showcase("Parts", "Excavator stick")]
    internal static ArrayMesh StickMesh()
    {
        var s = new MeshScratch();
        const float len = L.StickLength;
        s.Box(new Vector3(0, 0, len * 0.5f - 0.15f), new Vector3(0.38f, 0.46f, len + 0.3f), Body);
        s.Tube(new Vector3(0, 0.36f, 0.1f), new Vector3(0, 0.36f, len * 0.6f), 0.08f, Steel, 6);
        s.Tube(new Vector3(0, 0.36f, len * 0.5f), new Vector3(0, 0.36f, len * 0.85f), 0.05f, Chrome, 6);
        s.Tube(new Vector3(-0.25f, 0, len), new Vector3(0.25f, 0, len), 0.1f, Dark, 8);
        return s.Build();
    }

    /// <summary>The bucket, from the stick's tip: its back plate, its sides, the lip and the teeth at <see cref="ExcavatorLayout.BucketLength"/>.</summary>
    [Core.Showcase("Parts", "Excavator bucket")]
    internal static ArrayMesh BucketMesh()
    {
        var s = new MeshScratch();
        const float w = 0.55f, len = L.BucketLength;
        // the back, curving from the hinge down and forward to the lip, as three plates
        s.Box(new Vector3(0, -0.12f, 0.35f), new Vector3(w * 2f, 0.12f, 0.7f), Dark.Lightened(0.1f));
        s.Box(new Vector3(0, -0.45f, 0.82f), new Vector3(w * 2f, 0.55f, 0.12f), Dark.Lightened(0.1f));
        s.Box(new Vector3(0, -0.7f, 1.15f), new Vector3(w * 2f, 0.1f, 0.62f), Dark.Lightened(0.1f));
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * w, -0.4f, 0.8f), new Vector3(0.05f, 0.75f, 1.2f), Body.Darkened(0.1f));
        // the cutting lip and its teeth, at the edge the arm's reach is measured to
        s.Box(new Vector3(0, -0.72f, len - 0.04f), new Vector3(w * 2f + 0.04f, 0.06f, 0.08f), Steel);
        for (int i = -2; i <= 2; i++)
            s.Box(new Vector3(i * 0.22f, -0.72f, len + 0.06f), new Vector3(0.08f, 0.06f, 0.16f), Steel.Lightened(0.1f));
        return s.Build();
    }

    /// <summary>
    /// The drawn excavator: a heavy rig (cab, cockpit, driver, lamps: a truck's first person) with
    /// the undercarriage and the arm as its child node "Arm", posed as the machine is.
    /// </summary>
    /// <param name="driver">The figure in the cab; null parked.</param>
    public static HeavyRig CreateRig(Player.Excavator machine, HumanPalette? driver)
    {
        // the mini (#614): its own body and parts, posed by the same arm node
        var rig = HeavyRig.Create(machine.Mini ? MiniExcavatorMeshBuilder.Parts() : Parts(), driver);
        rig.Name = machine.Mini ? "MiniExcavator" : "Excavator";
        rig.AirLowAt = -1f;     // the small gauge reads nothing a low-air lamp is about
        var arm = ExcavatorArm.Create(machine.Spec, machine.Mini ? MiniExcavatorMeshBuilder.ArmMeshes() : ArmMeshes());
        rig.AddChild(arm);
        arm.Pose(machine.Slew, machine.Boom, machine.Stick, machine.Bucket, 0f, 0f, machine.Blade);
        return rig;
    }

    /// <summary>The meshes <see cref="ExcavatorArm"/> poses for the 20 t machine: no blade.</summary>
    public static ExcavatorArm.ArmParts ArmMeshes() => new(UndercarriageMesh(), ShoeRowMesh(), ShoePitch,
        BoomMesh(), StickMesh(), BucketMesh(), null, null);

    /// <summary>The arm node of a drawn excavator, or null (headless: nothing drawn).</summary>
    public static ExcavatorArm? ArmOf(Node3D? visual) => visual?.GetNodeOrNull<ExcavatorArm>("Arm");
}

/// <summary>
/// The parts of an excavator that move against its cab (#611): the undercarriage, turned back by
/// the slew so the tracks stay where they are while the house turns, its grouser rows run by the
/// tracks' speeds, and the boom, stick and bucket, each a pivot rotated about its own X; a mini's
/// dozer blade (#614) on the undercarriage, turned about its arms' pivot. Nothing is rebuilt; a
/// pose that has not changed by a hundredth of a degree is not even reassigned.
/// </summary>
public partial class ExcavatorArm : Node3D
{
    /// <summary>What an arm is drawn with: one size's parts, the lug spacing its tracks run by, its blade or none.</summary>
    public sealed record ArmParts(ArrayMesh Undercarriage, ArrayMesh ShoeRow, float ShoePitch,
        ArrayMesh Boom, ArrayMesh Stick, ArrayMesh Bucket, ArrayMesh? BladeArms, ArrayMesh? Blade);

    private Node3D _under = null!, _boom = null!, _stick = null!, _bucket = null!;
    private Node3D? _blade, _plate;
    private float _pitch;
    private readonly Node3D[] _shoes = new Node3D[4];
    private Vector4 _drawn = new(float.NaN, 0, 0, 0);
    private float _drawnBlade = float.NaN;

    /// <summary>The joint angles the arm is drawn at: slew, boom, stick, bucket.</summary>
    public Vector4 Drawn => _drawn;

    /// <summary>The blade's angle as drawn (0 without one).</summary>
    public float DrawnBlade => _blade == null ? 0f : _drawnBlade;

    /// <summary>Poses the undercarriage, the joints and the blade, and runs the tracks' grousers (m run by each track so far).</summary>
    public void Pose(float slew, float boom, float stick, float bucket, float scrollLeft, float scrollRight, float blade = 0f)
    {
        if (_blade != null && !(Mathf.Abs(blade - _drawnBlade) <= 1e-4f))
        {
            _drawnBlade = blade;
            _blade.Rotation = new Vector3(blade, 0, 0);
            // the plate stays upright as its arms swing it up
            _plate!.Rotation = new Vector3(-blade, 0, 0);
        }
        var pose = new Vector4(slew, boom, stick, bucket);
        if (!(float.IsNaN(_drawn.X) || (pose - _drawn).LengthSquared() > 1e-8f))
        {
            Run(scrollLeft, scrollRight);
            return;
        }
        _drawn = pose;
        // node space is the authored frame turned half round: the tracks turn back by the slew,
        // and a part along −Z rises for a positive turn about X
        _under.Rotation = new Vector3(0, -slew, 0);
        _boom.Rotation = new Vector3(boom, 0, 0);
        _stick.Rotation = new Vector3(stick, 0, 0);
        _bucket.Rotation = new Vector3(bucket, 0, 0);
        Run(scrollLeft, scrollRight);
    }

    /// <summary>The grouser rows slid along their runs: the bottom run stands on the ground (back past the machine), the top runs forward.</summary>
    private void Run(float left, float right)
    {
        for (int i = 0; i < 4; i++)
        {
            float run = i < 2 ? left : right;
            bool top = (i & 1) == 0;
            float shift = Mathf.PosMod(top ? run : -run, _pitch);
            var p = _shoes[i].Position;
            _shoes[i].Position = p with { Z = -shift };
        }
    }

    /// <summary>The 20 t machine's arm.</summary>
    public static ExcavatorArm Create() => Create(ExcavatorLayout.Spec, ExcavatorMeshBuilder.ArmMeshes());

    public static ExcavatorArm Create(ExcavatorSpec spec, ArmParts meshes)
    {
        var material = HumanMeshBuilder.FigureMaterial();
        MeshInstance3D Mesh(string name, ArrayMesh mesh) => new() { Name = name, Mesh = mesh, MaterialOverride = material };
        var node = new ExcavatorArm { Name = "Arm", _pitch = meshes.ShoePitch };
        node._under = new Node3D { Name = "Under" };
        node._under.AddChild(Mesh("Tracks", meshes.Undercarriage));
        int k = 0;
        foreach (int side in new[] { 1, -1 })
            foreach (bool top in new[] { true, false })
            {
                var row = new Node3D { Name = $"Shoes{k}" };
                var at = CarMeshBuilder.Turned(new Vector3(side * spec.HalfGauge, top ? spec.TrackHeight + 0.01f : -0.01f, 0));
                var holder = new Node3D { Name = $"Run{k}", Position = at };
                holder.AddChild(row);
                row.AddChild(Mesh("Grousers", meshes.ShoeRow));
                node._under.AddChild(holder);
                node._shoes[k++] = row;
            }
        // the blade rides the tracks, so it is the undercarriage's: it turns back with them under a slewed house
        if (meshes.BladeArms != null && meshes.Blade != null)
        {
            node._blade = new Node3D { Name = "Blade", Position = CarMeshBuilder.Turned(MiniExcavatorLayout.BladePivot) };
            node._blade.AddChild(Mesh("Arms", meshes.BladeArms));
            var edge = MiniExcavatorLayout.BladeReach;
            node._plate = new Node3D { Name = "Plate", Position = CarMeshBuilder.Turned(new Vector3(0, edge.Y, edge.X)) };
            node._plate.AddChild(Mesh("Plate", meshes.Blade));
            node._blade.AddChild(node._plate);
            node._under.AddChild(node._blade);
        }
        node.AddChild(node._under);
        node._boom = new Node3D { Name = "Boom", Position = CarMeshBuilder.Turned(spec.BoomFoot) };
        node._boom.AddChild(Mesh("Beam", meshes.Boom));
        node.AddChild(node._boom);
        node._stick = new Node3D { Name = "Stick", Position = new Vector3(0, 0, -spec.BoomLength) };
        node._stick.AddChild(Mesh("Beam", meshes.Stick));
        node._boom.AddChild(node._stick);
        node._bucket = new Node3D { Name = "Bucket", Position = new Vector3(0, 0, -spec.StickLength) };
        node._bucket.AddChild(Mesh("Bucket", meshes.Bucket));
        node._stick.AddChild(node._bucket);
        return node;
    }
}
