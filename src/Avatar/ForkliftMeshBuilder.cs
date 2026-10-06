using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Builds the forklift's meshes (#583). The static half — chassis, counterweight, overhead guard,
/// the outer mast, the cab and its cockpit — is the rig's body; the mast's moving half is its own
/// node (<see cref="ForkliftMast"/>), positioned rather than rebuilt, because a carriage sliding up
/// a channel is a translation and nothing about it changes shape.
/// </summary>
public static class ForkliftMeshBuilder
{
    private static readonly Color Body = new(0.86f, 0.44f, 0.06f);     // the orange every yard forklift is
    private static readonly Color Dark = new(0.16f, 0.16f, 0.18f);
    private static readonly Color Steel = new(0.72f, 0.73f, 0.75f);
    private static readonly Color Tyre = new(0.08f, 0.08f, 0.09f);
    private static readonly Color Fork = new(0.58f, 0.59f, 0.62f);
    private static readonly Color Beacon = new(1f, 0.5f, 0.05f);
    private static readonly Color Hazard = new(0.9f, 0.76f, 0.1f);

    /// <summary>
    /// The cab as its cockpit is derived from. <see cref="CabFrame.Front"/> is pulled back behind
    /// the binnacle on purpose: <see cref="HeavyCabin.Build"/> only draws its full-width dash when
    /// the face is ahead of the dials, and a forklift's driver looks out <b>through the mast</b> —
    /// a dash across that is the one thing that would make it unusable. The cowl is drawn here.
    /// <para>
    /// The seat is a chair, not a truck's: <see cref="CabFrame.HipRise"/> high and the back nearly
    /// upright. <see cref="HeavyCabin.SeatFor"/> keeps hip to pedal at one leg's length, so with the
    /// pedals this close under the cowl a truck's low hip put the legs out flat and the driver
    /// lounging (phase 1's shot); a high one drops the shins under the knees.
    /// </para>
    /// </summary>
    public static readonly CabFrame Frame = new()
    {
        Front = 0.62f, Floor = ForkliftLayout.Floor, Ceiling = ForkliftLayout.GuardTop - 0.06f,
        WsBase = 1.0f, WsTop = 1.9f, DashTop = 1.02f,
        InnerHalf = ForkliftLayout.HalfWidth - 0.04f, Nose = 0.26f, DriverX = 0f, HipRise = 0.56f,
        Recline = 0.08f, ColumnTilt = 0.72f, WheelRadius = 0.16f, DashToX = -(ForkliftLayout.HalfWidth - 0.04f),
        Clutch = false, PassengerSeat = false,
    };

    /// <summary>Its dials: a speedometer to 20 km/h, a tach to 2400 rpm, and the small gauge reads the forks, 0-3.5 m.</summary>
    public static readonly CarGauges Gauges = HeavyCabin.GaugesFor(18f, 2400f);

    public static HeavyParts Parts()
    {
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var reverse = new MeshScratch();
        const float W = ForkliftLayout.HalfWidth, L = ForkliftLayout.Rear, mz = ForkliftLayout.MastZ;
        const float fl = ForkliftLayout.Floor, bt = ForkliftLayout.BonnetTop;

        // ---- chassis: a low box the whole length, the counterweight cast on the back of it ----
        m.Box(new Vector3(0, 0.26f, (L + mz) * 0.5f), new Vector3(W * 2f, 0.3f, mz - L), Dark);
        // the counterweight: taller and full width, the thing that stops it tipping over its forks
        m.Box(new Vector3(0, (0.11f + bt) * 0.5f, L + 0.3f), new Vector3(W * 2f, bt - 0.11f, 0.6f), Body);
        m.Box(new Vector3(0, bt - 0.02f, L + 0.3f), new Vector3(W * 2f - 0.06f, 0.04f, 0.56f), Body.Darkened(0.15f));
        // the bonnet over the battery, between the seat and the counterweight, with the seat on it
        m.Box(new Vector3(0, (0.41f + bt) * 0.5f, L + 0.72f), new Vector3(W * 2f - 0.04f, bt - 0.41f, 0.3f), Body);
        // the footplate and its step on the left
        m.Box(new Vector3(0, fl - 0.015f, 0.1f), new Vector3((W - 0.03f) * 2f, 0.03f, 0.86f), Dark.Lightened(0.1f));
        m.Box(new Vector3(W + 0.06f, 0.2f, -0.3f), new Vector3(0.16f, 0.03f, 0.28f), Steel);
        // the side panels, hazard-striped along the bottom as a works machine is
        foreach (int side in new[] { 1, -1 })
        {
            m.Box(new Vector3(side * (W - 0.02f), 0.26f, (L + mz) * 0.5f), new Vector3(0.04f, 0.3f, mz - L), Body);
            for (int i = 0; i < 5; i++)
                m.Box(new Vector3(side * (W + 0.005f), 0.14f, L + 0.2f + i * 0.4f), new Vector3(0.012f, 0.07f, 0.2f),
                    i % 2 == 0 ? Hazard : Dark);
        }

        // ---- the overhead guard: four posts and a slatted roof ----
        foreach (int side in new[] { 1, -1 })
            foreach (float z in new[] { 0.52f, -0.78f })
                m.Box(new Vector3(side * (W - ForkliftLayout.Post * 0.5f), (bt + ForkliftLayout.GuardTop) * 0.5f, z),
                    new Vector3(ForkliftLayout.Post, ForkliftLayout.GuardTop - bt, ForkliftLayout.Post), Body);
        for (int i = 0; i < 5; i++)
            m.Box(new Vector3(0, ForkliftLayout.GuardTop - 0.03f, -0.74f + i * 0.33f), new Vector3(W * 2f, 0.05f, 0.12f), Body.Darkened(0.1f));
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (W - 0.03f), ForkliftLayout.GuardTop - 0.03f, -0.13f), new Vector3(0.06f, 0.05f, 1.3f), Body.Darkened(0.1f));
        // the beacon on the guard, and work lamps under its front edge
        m.Tube(new Vector3(-0.3f, ForkliftLayout.GuardTop, -0.2f), new Vector3(-0.3f, ForkliftLayout.GuardTop + 0.13f, -0.2f), 0.075f, Beacon, 6);
        foreach (int side in new[] { 1, -1 })
        {
            HeavyMesh.Lamp(head, side * (W - 0.14f), ForkliftLayout.GuardTop - 0.1f, 0.56f, 0.14f, 0.1f, 0.04f, HeavyMesh.HeadLamp);
            HeavyMesh.Lamp(tail, side * (W - 0.16f), 0.78f, L + 0.01f, 0.16f, 0.1f, 0.04f, HeavyMesh.TailLamp);
            HeavyMesh.Lamp(reverse, side * (W - 0.38f), 0.78f, L + 0.01f, 0.1f, 0.1f, 0.04f, HeavyMesh.White);
        }

        // ---- the outer mast: two channels either side of the driver's view, tied at the top ----
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * ForkliftLayout.MastX, (0.18f + ForkliftLayout.MastTop) * 0.5f, mz),
                new Vector3(ForkliftLayout.MastRail, ForkliftLayout.MastTop - 0.18f, ForkliftLayout.MastRail * 1.4f), Steel.Darkened(0.35f));
        m.Box(new Vector3(0, ForkliftLayout.MastTop - 0.05f, mz), new Vector3(ForkliftLayout.MastX * 2f, 0.1f, ForkliftLayout.MastRail), Steel.Darkened(0.35f));
        // the tilt rams from the chassis to the mast's foot
        foreach (int side in new[] { 1, -1 })
            m.Tube(new Vector3(side * 0.36f, 0.5f, 0.35f), new Vector3(side * ForkliftLayout.MastX, 0.32f, mz - 0.05f), 0.035f, Steel, 5);

        // the cowl in front of the driver: small, so the view through the mast stays open
        m.Box(new Vector3(0, (fl + 1.0f) * 0.5f, 0.52f), new Vector3(W * 2f - 0.08f, 1.0f - fl, 0.12f), Dark.Lightened(0.05f));

        // the cockpit: seat, the small flat wheel, the dials, the pedals; no mirrors (it has none)
        var cockpit = HeavyCabin.Build(m, Frame, Gauges, 0f, ForkliftLayout.LiftDial,
            System.Array.Empty<(string, Vector3, Vector2)>());
        var seats = new[] { new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, ForkliftLayout.Floor) };

        // the mast lever, right of the wheel: what a VR hand grips (XrCabControls "forklift")
        m.Tube(new Vector3(-0.26f, 1.0f, 0.46f), new Vector3(-0.26f, 1.2f, 0.42f), 0.018f, Steel, 5);
        m.Box(new Vector3(-0.26f, 1.22f, 0.42f), new Vector3(0.05f, 0.05f, 0.05f), Beacon);

        var wheels = new List<HeavyWheel>();
        foreach (int side in new[] { 1, -1 })
            wheels.Add(new HeavyWheel(FrontWheel(), new Vector3(-side * ForkliftLayout.FrontTrack, ForkliftLayout.FrontRadius, -ForkliftLayout.FrontAxle), 0f));
        foreach (int side in new[] { 1, -1 })
            wheels.Add(new HeavyWheel(RearWheel(), new Vector3(-side * ForkliftLayout.RearTrack, ForkliftLayout.RearRadius, -ForkliftLayout.RearAxle), 1f));

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), reverse.Build(), wheels.ToArray(),
            System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
        };
    }

    private static ArrayMesh? _front, _rear;

    private static ArrayMesh FrontWheel() => _front ??= WheelMesh(ForkliftLayout.FrontRadius, ForkliftLayout.FrontWidth);
    private static ArrayMesh RearWheel() => _rear ??= WheelMesh(ForkliftLayout.RearRadius, ForkliftLayout.RearWidth);

    /// <summary>One cushion tyre on its rim, facing ±X.</summary>
    private static ArrayMesh WheelMesh(float radius, float width)
    {
        var s = new MeshScratch();
        float w = width * 0.5f;
        s.Tube(new Vector3(-w, 0, 0), new Vector3(w, 0, 0), radius, Tyre, 10);
        s.Tube(new Vector3(-w - 0.012f, 0, 0), new Vector3(w + 0.012f, 0, 0), radius * 0.55f, Steel.Darkened(0.2f), 6);
        return s.Build();
    }

    /// <summary>
    /// The drawn forklift: a heavy rig (cockpit, driver, wheels, lamps: a truck's first person) with
    /// the mast's moving half as its child node "Mast".
    /// </summary>
    /// <param name="driver">The figure at the wheel; null parked.</param>
    /// <param name="carrying">What is on the forks (<c>Forklift.Carrying</c>): 0 nothing, else 1 + a pallet's load byte.</param>
    public static HeavyRig CreateRig(float lift, HumanPalette? driver, int carrying = 0)
    {
        var rig = HeavyRig.Create(Parts(), driver);
        rig.Name = "Forklift";
        rig.AirLowAt = -1f;      // the small gauge is the forks' height: no low-air lamp
        var mast = ForkliftMast.Create(lift);
        rig.AddChild(mast);
        mast.Carrying = carrying;
        return rig;
    }

    /// <summary>The mast node of a drawn forklift, or null (headless: nothing drawn).</summary>
    public static ForkliftMast? MastOf(Node3D? visual) => visual?.GetNodeOrNull<ForkliftMast>("Mast");

    /// <summary>The inner mast stage, authored at its retracted place: two channels inside the outer pair.</summary>
    internal static ArrayMesh StageMesh()
    {
        var s = new MeshScratch();
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * (ForkliftLayout.MastX - ForkliftLayout.MastRail * 0.75f), (0.22f + ForkliftLayout.MastTop - 0.1f) * 0.5f, ForkliftLayout.MastZ + 0.03f),
                new Vector3(ForkliftLayout.MastRail * 0.7f, ForkliftLayout.MastTop - 0.32f, ForkliftLayout.MastRail), Steel);
        return s.Build();
    }

    /// <summary>
    /// The carriage and its two tines, authored with the tines' upper faces at y 0, so the node's
    /// own Y <b>is</b> the fork height and nothing has to be rebuilt as it rises.
    /// </summary>
    internal static ArrayMesh CarriageMesh()
    {
        var s = new MeshScratch();
        const float mz = ForkliftLayout.MastZ, tl = ForkliftLayout.TineLength;
        const float tw = ForkliftLayout.TineWidth, tt = ForkliftLayout.TineThick;
        // the carriage plate across the mast, and the load backrest above it
        s.Box(new Vector3(0, ForkliftLayout.CarriageTop * 0.5f, mz + 0.05f),
            new Vector3(ForkliftLayout.MastX * 2f + 0.16f, ForkliftLayout.CarriageTop, 0.06f), Steel.Darkened(0.15f));
        for (int i = 0; i < 4; i++)
            s.Box(new Vector3(-0.42f + i * 0.28f, (ForkliftLayout.CarriageTop + ForkliftLayout.BackrestTop) * 0.5f, mz + 0.04f),
                new Vector3(0.05f, ForkliftLayout.BackrestTop - ForkliftLayout.CarriageTop, 0.04f), Steel.Darkened(0.25f));
        s.Box(new Vector3(0, ForkliftLayout.BackrestTop - 0.03f, mz + 0.04f), new Vector3(1.0f, 0.06f, 0.05f), Steel.Darkened(0.25f));
        // the two tines: a short heel down the carriage, then the blade forward, its top face at y 0
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * ForkliftLayout.TineX;
            s.Box(new Vector3(x, 0.14f, mz + 0.09f), new Vector3(tw, 0.28f + tt, tt), Fork);
            s.Box(new Vector3(x, -tt * 0.5f, mz + tl * 0.5f), new Vector3(tw, tt, tl), Fork);
            // the tapered tip, as a thinner block: a tine you can actually slide into a pallet
            s.Box(new Vector3(x, -tt * 0.3f, mz + tl - 0.1f), new Vector3(tw, tt * 0.6f, 0.2f), Fork.Lightened(0.1f));
        }
        return s.Build();
    }
}

/// <summary>
/// The moving half of a forklift's mast (#583): the inner stage and the fork carriage, each a child
/// moved in place — the carriage to the fork height, the stage to whatever it has extended
/// (<see cref="ForkliftLayout.Stage"/>). Nothing is rebuilt and nothing runs per frame at rest.
/// A child of the truck's <see cref="HeavyRig"/>, named "Mast".
/// </summary>
public partial class ForkliftMast : Node3D
{
    private Node3D _stage = null!;
    private Node3D _carriage = null!;
    private float _drawn = float.NaN;

    /// <summary>What rides on the forks, drawn on the carriage; null when they are empty.</summary>
    public Node3D? Load { get; private set; }

    private int _carrying;

    /// <summary>
    /// What is on the forks, as <c>Forklift.Carrying</c> holds it (#583): 0 nothing, else 1 + a
    /// pallet's load byte, drawn by <see cref="Items.PalletNode.Carried"/>. Rebuilt only when it changes.
    /// </summary>
    public int Carrying
    {
        get => _carrying;
        set
        {
            if (value == _carrying) return;
            _carrying = value;
            Carry(Items.Pallets.LoadCarried(value) is { } load ? Items.PalletNode.Carried(load) : null);
        }
    }

    /// <summary>The fork height over the ground, m. Moved when it changes by 5 mm.</summary>
    public float Lift
    {
        get => _drawn;
        set
        {
            if (!float.IsNaN(_drawn) && Mathf.Abs(value - _drawn) < 0.005f) return;
            _drawn = value;
            float lift = ForkliftLayout.Clamp(value);
            _carriage.Position = new Vector3(0, lift, 0);
            _stage.Position = new Vector3(0, ForkliftLayout.Stage(value), 0);
        }
    }

    /// <summary>
    /// Puts <paramref name="load"/> on the forks, or clears them. The node is positioned for the
    /// caller at <see cref="ForkliftLayout.LoadCentre"/>, turned into node space like every mesh
    /// this builder makes, so a pallet drawn about its own centre lands on the tines.
    /// </summary>
    public void Carry(Node3D? load)
    {
        if (Load != null && Load != load) { Load.QueueFree(); Load = null; }
        Load = load;
        if (load == null) return;
        load.Name = "Load";
        load.Position = CarMeshBuilder.Turned(ForkliftLayout.LoadCentre);
        _carriage.AddChild(load);
    }

    public static ForkliftMast Create(float lift)
    {
        var material = HumanMeshBuilder.FigureMaterial();
        var node = new ForkliftMast { Name = "Mast" };
        node._stage = new Node3D { Name = "Stage" };
        node._stage.AddChild(new MeshInstance3D { Name = "Rails", Mesh = ForkliftMeshBuilder.StageMesh(), MaterialOverride = material });
        node.AddChild(node._stage);
        node._carriage = new Node3D { Name = "Carriage" };
        node._carriage.AddChild(new MeshInstance3D { Name = "Forks", Mesh = ForkliftMeshBuilder.CarriageMesh(), MaterialOverride = material });
        node.AddChild(node._carriage);
        node.Lift = lift;
        return node;
    }
}
