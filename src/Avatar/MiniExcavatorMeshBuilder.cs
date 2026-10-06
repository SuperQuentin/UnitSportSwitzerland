using Godot;
using L = UnitSport.Avatar.MiniExcavatorLayout;

namespace UnitSport.Avatar;

/// <summary>
/// Builds the mini excavator's meshes (#614): a short-tail house under an open four-post canopy,
/// rubber tracks with a dozer blade, and an arm on a swing bracket in front of the operator. Drawn
/// and posed as the big one is (<see cref="ExcavatorArm"/>): only the parts and their sizes differ.
/// </summary>
public static class MiniExcavatorMeshBuilder
{
    private static readonly Color Body = new(0.93f, 0.45f, 0.08f);      // the orange minis come in
    private static readonly Color Dark = new(0.15f, 0.15f, 0.17f);
    private static readonly Color Rubber = new(0.09f, 0.09f, 0.1f);
    private static readonly Color Steel = new(0.66f, 0.67f, 0.70f);
    private static readonly Color Chrome = new(0.85f, 0.86f, 0.88f);
    private static readonly Color Beacon = new(1f, 0.5f, 0.05f);

    /// <summary>The operator's place under the canopy: no glass to clear, a low panel ahead for the dials.</summary>
    public static readonly CabFrame Frame = new()
    {
        Front = L.CabFront + 0.03f, Floor = L.CabFloor, Ceiling = L.CabTop - 0.08f,
        WsBase = 1.1f, WsTop = 2.3f, DashTop = 1.05f,
        InnerHalf = L.CabHalf - 0.04f, Nose = 0.05f, DriverX = 0f, HipRise = 0.5f, Recline = 0.12f,
        ColumnTilt = 0.75f, WheelRadius = 0.11f, DashToX = -(L.CabHalf - 0.04f), Clutch = false, PassengerSeat = false,
    };

    /// <summary>Its dials: travel speed to 6 km/h, the tach to 2800 rpm.</summary>
    public static readonly CarGauges Gauges = HeavyCabin.GaugesFor(6f, 2800f);

    /// <summary>Where a VR hand finds the blade's lever: left of the seat, ahead of the left joystick (<c>XrCabControls</c> "miniexcavator").</summary>
    public static Vector3 BladeLever(Vector3 hip) => hip + new Vector3(0.36f, 0.0f, 0.5f);

    public static HeavyParts Parts()
    {
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var reverse = new MeshScratch();
        const float rt = L.RingTop, hh = L.HouseHalf;

        // ---- the house: a deck on the ring, the engine hood behind the seat, a round tail -------
        m.Box(new Vector3(0, rt + 0.06f, (L.HouseFront + L.HouseBack) * 0.5f), new Vector3(hh * 2f, 0.12f, L.HouseFront - L.HouseBack), Dark);
        // (the seat's back reaches to about -0.5: the hood stays behind it)
        m.Box(new Vector3(0, (rt + L.HouseTop) * 0.5f, -0.74f), new Vector3(hh * 2f, L.HouseTop - rt, 0.36f), Body);
        // the counterweight: the tail's dark curve, so the tail hardly swings past the tracks
        m.Tube(new Vector3(-hh + 0.05f, (rt + L.HouseTop) * 0.5f - 0.05f, L.HouseBack + 0.1f),
            new Vector3(hh - 0.05f, (rt + L.HouseTop) * 0.5f - 0.05f, L.HouseBack + 0.1f), (L.HouseTop - rt) * 0.5f - 0.05f, Dark.Lightened(0.08f), 10);
        m.Tube(new Vector3(-0.45f, L.HouseTop, -0.8f), new Vector3(-0.45f, L.HouseTop + 0.3f, -0.8f), 0.04f, Dark, 6);
        // the floor plate the operator sits on, and the step either side
        m.Box(new Vector3(0, (rt + L.CabFloor) * 0.5f, 0.05f), new Vector3(hh * 2f, L.CabFloor - rt, 0.8f), Body.Darkened(0.1f));
        // the swing bracket the boom stands on, in front of the floor
        m.Box(new Vector3(0, (rt + L.BoomFoot.Y + 0.1f) * 0.5f, L.BoomFoot.Z - 0.12f), new Vector3(0.36f, L.BoomFoot.Y + 0.1f - rt, 0.42f), Body.Darkened(0.15f));
        m.Tube(new Vector3(0, rt, L.BoomFoot.Z - 0.15f), new Vector3(0, L.BoomFoot.Y + 0.15f, L.BoomFoot.Z - 0.15f), 0.07f, Steel, 6);

        // ---- the canopy: four posts and a roof, open all round ---------------------------------
        const float c = L.CabHalf, cb = L.CabBack, cf = L.CabFront, fl = L.CabFloor, ct = L.CabTop;
        foreach (int side in new[] { 1, -1 })
            foreach (float z in new[] { cb + 0.04f, cf - 0.04f })
            {
                // the back posts stand on the hood, the front ones on the floor plate
                float foot = z < 0 ? L.HouseTop : fl;
                m.Box(new Vector3(side * (c - 0.04f), (foot + ct) * 0.5f, z), new Vector3(0.07f, ct - foot, 0.07f), Dark);
            }
        m.Box(new Vector3(0, ct - 0.03f, (cb + cf) * 0.5f), new Vector3(c * 2f + 0.08f, 0.06f, cf - cb + 0.08f), Body);
        m.Box(new Vector3(0, ct - 0.08f, (cb + cf) * 0.5f), new Vector3(c * 2f - 0.1f, 0.04f, cf - cb - 0.1f), Dark.Lightened(0.05f));
        // the beacon on the roof, two work lamps under its front, a tail lamp each side of the tail
        m.Tube(new Vector3(-0.25f, ct, cb + 0.2f), new Vector3(-0.25f, ct + 0.1f, cb + 0.2f), 0.06f, Beacon, 6);
        foreach (int side in new[] { 1, -1 })
        {
            HeavyMesh.Lamp(head, side * (c - 0.1f), ct - 0.1f, cf + 0.02f, 0.1f, 0.08f, 0.04f, HeavyMesh.HeadLamp);
            HeavyMesh.Lamp(tail, side * (hh - 0.15f), L.HouseTop - 0.25f, L.HouseBack - 0.01f, 0.1f, 0.08f, 0.03f, HeavyMesh.TailLamp);
            HeavyMesh.Lamp(reverse, side * (hh - 0.32f), L.HouseTop - 0.25f, L.HouseBack - 0.01f, 0.07f, 0.07f, 0.03f, HeavyMesh.White);
        }

        // the seat, the small wheel and the dials as the cab's own; no mirrors
        var cockpit = HeavyCabin.Build(m, Frame, Gauges, 0f, 1f, System.Array.Empty<(string, Vector3, Vector2)>());
        var seats = new[] { new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, L.CabFloor) };
        // the two joysticks on consoles either side of the seat, and the blade's lever ahead of the left one
        foreach (int side in new[] { 1, -1 })
        {
            var at = cockpit.Seat.Hip + new Vector3(side * 0.3f, 0.05f, 0.2f);
            m.Box(at with { Y = (fl + at.Y) * 0.5f }, new Vector3(0.1f, at.Y - fl, 0.26f), Dark);
            m.Tube(at, at + new Vector3(0, 0.15f, 0.02f), 0.013f, Steel, 5);
            m.Box(at + new Vector3(0, 0.17f, 0.02f), new Vector3(0.035f, 0.06f, 0.035f), Dark.Lightened(0.15f));
        }
        var blade = BladeLever(cockpit.Seat.Hip);
        m.Box(blade with { Y = (fl + blade.Y) * 0.5f }, new Vector3(0.06f, blade.Y - fl, 0.08f), Dark);
        m.Tube(blade, blade + new Vector3(0, 0.2f, 0), 0.011f, Steel, 5);
        m.Tube(blade + new Vector3(0, 0.2f, 0), blade + new Vector3(0, 0.26f, 0), 0.025f, Body, 6);

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), reverse.Build(), System.Array.Empty<HeavyWheel>(),
            System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
        };
    }

    /// <summary>The meshes <see cref="ExcavatorArm"/> poses for a mini.</summary>
    public static ExcavatorArm.Meshes ArmMeshes() => new(UndercarriageMesh(), ShoeRowMesh(), ShoePitch,
        BoomMesh(), StickMesh(), BucketMesh(), BladeArmsMesh(), BladeMesh());

    /// <summary>The rubber tracks' lug spacing, m.</summary>
    public const float ShoePitch = 0.14f;

    /// <summary>The undercarriage: the centre frame, the ring, both rubber tracks round their idlers, and the blade's pivot lugs.</summary>
    [Core.Showcase("Parts", "Mini excavator undercarriage")]
    internal static ArrayMesh UndercarriageMesh()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.36f, 0), new Vector3(L.HalfGauge * 2f - 0.25f, 0.26f, 1.0f), Dark);
        s.Tube(new Vector3(0, 0.46f, 0), new Vector3(0, L.RingTop, 0), 0.5f, Dark.Lightened(0.1f), 12);
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * L.HalfGauge, half = L.TrackHalfLength, r = L.TrackHeight * 0.5f;
            s.Box(new Vector3(x, r, 0), new Vector3(L.ShoeWidth * 0.7f, L.TrackHeight * 0.5f, (half - r) * 2f), Body.Darkened(0.3f));
            s.Box(new Vector3(x, L.TrackHeight - 0.04f, 0), new Vector3(L.ShoeWidth, 0.08f, (half - r) * 2f), Rubber);
            s.Box(new Vector3(x, 0.04f, 0), new Vector3(L.ShoeWidth, 0.08f, (half - r) * 2f), Rubber);
            foreach (int end in new[] { 1, -1 })
            {
                var hub = new Vector3(x, r, end * (half - r));
                s.Tube(hub - new Vector3(L.ShoeWidth * 0.5f, 0, 0), hub + new Vector3(L.ShoeWidth * 0.5f, 0, 0), r, Rubber, 10);
                s.Tube(hub - new Vector3(L.ShoeWidth * 0.52f, 0, 0), hub + new Vector3(L.ShoeWidth * 0.52f, 0, 0), r * 0.6f, Steel.Darkened(0.3f), 8);
            }
        }
        // the lugs the blade's arms hang from, on the frame's front
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * 0.4f, L.BladePivot.Y, L.BladePivot.Z - 0.08f), new Vector3(0.08f, 0.16f, 0.2f), Body.Darkened(0.3f));
        return s.Build();
    }

    /// <summary>A row of rubber lugs along one run of a track, about its middle.</summary>
    [Core.Showcase("Parts", "Mini excavator track lugs")]
    internal static ArrayMesh ShoeRowMesh()
    {
        var s = new MeshScratch();
        float half = L.TrackHalfLength - L.TrackHeight * 0.5f - ShoePitch;
        for (float z = -half; z <= half; z += ShoePitch)
            s.Box(new Vector3(0, 0, z), new Vector3(L.ShoeWidth - 0.04f, 0.03f, 0.05f), Rubber.Lightened(0.1f));
        return s.Build();
    }

    /// <summary>The boom, from its foot along +Z: bent upward at a third of its length, as a mini's is, with its ram underneath.</summary>
    [Core.Showcase("Parts", "Mini excavator boom")]
    internal static ArrayMesh BoomMesh()
    {
        var s = new MeshScratch();
        const float len = L.BoomLength;
        s.Box(new Vector3(0, 0, len * 0.5f), new Vector3(0.22f, 0.3f, len + 0.14f), Body);
        s.Box(new Vector3(0, 0.17f, len * 0.5f), new Vector3(0.24f, 0.03f, len * 0.85f), Body.Darkened(0.15f));
        s.Tube(new Vector3(0, -0.24f, 0.15f), new Vector3(0, -0.2f, len * 0.4f), 0.05f, Steel, 6);
        s.Tube(new Vector3(0, -0.2f, len * 0.3f), new Vector3(0, -0.18f, len * 0.42f), 0.035f, Chrome, 6);
        s.Tube(new Vector3(0, 0.24f, len * 0.35f), new Vector3(0, 0.24f, len * 0.85f), 0.045f, Steel, 6);
        s.Tube(new Vector3(0, 0.24f, len * 0.7f), new Vector3(0, 0.24f, len * 0.95f), 0.03f, Chrome, 6);
        s.Tube(new Vector3(-0.15f, 0, len), new Vector3(0.15f, 0, len), 0.06f, Dark, 8);
        return s.Build();
    }

    /// <summary>The stick, from the boom's tip along +Z, with the bucket's ram.</summary>
    [Core.Showcase("Parts", "Mini excavator stick")]
    internal static ArrayMesh StickMesh()
    {
        var s = new MeshScratch();
        const float len = L.StickLength;
        s.Box(new Vector3(0, 0, len * 0.5f - 0.08f), new Vector3(0.18f, 0.22f, len + 0.16f), Body);
        s.Tube(new Vector3(0, 0.18f, 0.05f), new Vector3(0, 0.18f, len * 0.6f), 0.04f, Steel, 6);
        s.Tube(new Vector3(0, 0.18f, len * 0.5f), new Vector3(0, 0.18f, len * 0.85f), 0.025f, Chrome, 6);
        s.Tube(new Vector3(-0.12f, 0, len), new Vector3(0.12f, 0, len), 0.05f, Dark, 8);
        return s.Build();
    }

    /// <summary>The bucket, from the stick's tip: 60 cm wide, its teeth at <see cref="MiniExcavatorLayout.BucketLength"/>.</summary>
    [Core.Showcase("Parts", "Mini excavator bucket")]
    internal static ArrayMesh BucketMesh()
    {
        var s = new MeshScratch();
        const float w = 0.3f, len = L.BucketLength;
        s.Box(new Vector3(0, -0.05f, 0.15f), new Vector3(w * 2f, 0.06f, 0.3f), Dark.Lightened(0.1f));
        s.Box(new Vector3(0, -0.2f, 0.34f), new Vector3(w * 2f, 0.26f, 0.06f), Dark.Lightened(0.1f));
        s.Box(new Vector3(0, -0.3f, 0.48f), new Vector3(w * 2f, 0.05f, 0.26f), Dark.Lightened(0.1f));
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * w, -0.17f, 0.33f), new Vector3(0.03f, 0.34f, 0.5f), Body.Darkened(0.1f));
        s.Box(new Vector3(0, -0.31f, len - 0.02f), new Vector3(w * 2f + 0.02f, 0.03f, 0.04f), Steel);
        for (int i = -1; i <= 1; i++)
            s.Box(new Vector3(i * 0.2f, -0.31f, len + 0.04f), new Vector3(0.05f, 0.03f, 0.08f), Steel.Lightened(0.1f));
        return s.Build();
    }

    /// <summary>
    /// The dozer blade's arms about their pivot, forward and down to the blade's cutting edge
    /// (<see cref="MiniExcavatorLayout.BladeReach"/> from the pivot: on the ground, the arms at 0),
    /// and the ram that lifts them.
    /// </summary>
    [Core.Showcase("Parts", "Mini excavator blade arms")]
    internal static ArrayMesh BladeArmsMesh()
    {
        var s = new MeshScratch();
        var edge = new Vector3(0, L.BladeReach.Y, L.BladeReach.X);
        foreach (int side in new[] { 1, -1 })
            s.Tube(new Vector3(side * 0.4f, 0, 0), edge + new Vector3(side * 0.4f, L.BladeHeight * 0.6f, -0.06f), 0.05f, Body.Darkened(0.1f), 6);
        s.Tube(new Vector3(0, 0.05f, -0.05f), edge + new Vector3(0, L.BladeHeight * 0.7f, -0.08f), 0.04f, Steel, 6);
        return s.Build();
    }

    /// <summary>
    /// The blade itself, about its cutting edge: a plate standing up from the edge, its top lip,
    /// the steel edge at its foot. Turned back by the arms' angle, so it stays upright as it rises.
    /// </summary>
    [Core.Showcase("Parts", "Mini excavator blade")]
    internal static ArrayMesh BladeMesh()
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, L.BladeHeight * 0.5f, 0.02f), new Vector3(L.BladeWidth, L.BladeHeight, 0.06f), Body);
        s.Box(new Vector3(0, L.BladeHeight - 0.02f, -0.02f), new Vector3(L.BladeWidth, 0.04f, 0.1f), Body.Darkened(0.15f));
        s.Box(new Vector3(0, 0.025f, 0.03f), new Vector3(L.BladeWidth, 0.05f, 0.07f), Steel);
        return s.Build();
    }
}
