using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The mobile airstairs truck (#417): a low forward-control cab on a short chassis, the stair flight
/// running up from the ground behind it to a platform over the cab, raised to a door's sill. One set
/// of numbers for the drawn model, its hull and the walkable deck. AUTHORED frame like every avatar
/// mesh: +Z forward, +X the left side, origin on the ground in the middle of the wheelbase. Metres,
/// after the common self-propelled stairs (TLD/Mallaghan class: 2.4-5.6 m platform, ~9 m long).
/// </summary>
public static class AirstairsLayout
{
    /// <summary>Chassis: rear end, front bumper, half width; frame rails between these heights.</summary>
    public const float ChassisRear = -4.4f, Bumper = 3.4f, HalfWidth = 1.15f, FrameLow = 0.55f, FrameTop = 0.85f;
    /// <summary>Wheels: radius, width, axles, half track. The front axle is under the cab's back (forward control).</summary>
    public const float WheelRadius = 0.45f, TyreWidth = 0.3f, FrontAxle = 1.35f, RearAxle = -2.4f, Track = 0.92f;
    public const float Wheelbase = FrontAxle - RearAxle;
    /// <summary>
    /// The cab, hollow (#417 review): from its back wall to the bumper, its floor, roof and windows.
    /// Low enough for the platform to pass over it (<see cref="MinHeight"/> − the slab), so the floor
    /// sits low, ahead of the front axle.
    /// </summary>
    public const float CabRear = 1.8f, CabTop = 2.2f, CabFloor = 0.7f, CabWall = 0.06f, WinLow = 1.38f, WinHigh = 2.04f;

    /// <summary>The platform: from the flight's top to its front edge; its half width; its slab.</summary>
    public const float PlatformRear = 2.0f, PlatformFront = 4.0f, PlatformHalfWidth = 0.75f, PlatformThick = 0.15f;
    /// <summary>
    /// The bridge plate in front of the platform, sloping down onto a sill: its lip and how far it
    /// drops. Docked, the lip lies <see cref="SillOverlap"/> inside the sill's outer edge, and the
    /// platform <see cref="DockAbove"/> over the sill: the plate's slope then crosses the sill's top
    /// just inside its edge, so neither way has a lip to stop the walk (the walk has no step-up).
    /// </summary>
    public const float LipZ = 4.35f, PlateDrop = 0.07f, SillOverlap = 0.2f, DockAbove = 0.04f;
    /// <summary>The flight: its foot on the ground behind the chassis, its inner half width, the rails' height.</summary>
    public const float FootZ = -4.7f, FlightHalfWidth = 0.6f, RailX = 0.66f, RailHeight = 1.0f;
    /// <summary>The ramp's slab, under its walking face.</summary>
    public const float RampThick = 0.1f;
    /// <summary>The platform's range (its underside clears the cab), and where it rides when driven.</summary>
    public const float MinHeight = 2.4f, MaxHeight = 5.6f, TravelHeight = 2.5f;
    /// <summary>Lift columns either side of the cab, outside its walls.</summary>
    public const float ColumnZ = 2.1f, ColumnX = 1.24f;
    /// <summary>The canopy over the platform: its underside over the platform floor.</summary>
    public const float CanopyOver = 2.2f;

    /// <summary>The cab's door (left, a left-hand-drive cab), where E gets in.</summary>
    public static readonly Vector3 CabDoor = new(HalfWidth + 0.15f, 0f, 2.5f);

    public static float Clamp(float h) => Mathf.Clamp(h, MinHeight, MaxHeight);

    /// <summary>
    /// The walking surface at authored station <paramref name="z"/> with the platform at
    /// <paramref name="h"/>: the flight's ramp behind the platform, the platform over it. What a
    /// walker standing there rises or sinks by when the platform moves.
    /// </summary>
    public static float FloorAt(float h, float z)
    {
        h = Clamp(h);
        if (z >= PlatformRear) return h;
        float t = Mathf.Clamp((z - FootZ) / (PlatformRear - FootZ), 0f, 1f);
        return -0.04f + (h + 0.04f) * t;
    }
}

/// <summary>Builds the airstairs' meshes and walkable deck (#417) for a platform height.</summary>
public static class AirstairsMeshBuilder
{
    private static readonly Color Cab = new(0.95f, 0.74f, 0.12f);       // airside yellow
    private static readonly Color Chassis = new(0.18f, 0.18f, 0.2f);
    private static readonly Color Tyre = new(0.08f, 0.08f, 0.09f);
    private static readonly Color Glass = new(0.25f, 0.36f, 0.45f);
    private static readonly Color Steel = new(0.78f, 0.79f, 0.8f);
    private static readonly Color Tread = new(0.36f, 0.37f, 0.39f);
    private static readonly Color Rail = new(0.9f, 0.9f, 0.88f);
    private static readonly Color Stripe = new(0.12f, 0.12f, 0.12f);
    private static readonly Color Beacon = new(1f, 0.5f, 0.05f);
    private static readonly Color Canopy = new(0.92f, 0.92f, 0.94f);
    private static readonly Color Headliner = new(0.42f, 0.38f, 0.33f);

    /// <summary>The cab as its cockpit is derived from (<see cref="HeavyCabin.SeatFor"/>), authored.</summary>
    public static readonly CabFrame Frame = new()
    {
        Front = AirstairsLayout.Bumper - 0.05f, Floor = AirstairsLayout.CabFloor, Ceiling = AirstairsLayout.CabTop - AirstairsLayout.CabWall,
        WsBase = AirstairsLayout.WinLow, WsTop = AirstairsLayout.WinHigh, DashTop = AirstairsLayout.WinLow,
        InnerHalf = AirstairsLayout.HalfWidth - AirstairsLayout.CabWall, Nose = 0.3f, DriverX = 0.5f, HipRise = 0.4f,
        Recline = 0.2f, ColumnTilt = 0.95f, WheelRadius = 0.2f, DashToX = -(AirstairsLayout.HalfWidth - AirstairsLayout.CabWall),
        Clutch = false, PassengerSeat = true,
    };

    /// <summary>Its dials: a speedometer to 40 km/h, a tach to 3000 rpm, and the small gauge reads the platform, 0-6 m.</summary>
    public static readonly CarGauges Gauges = HeavyCabin.GaugesFor(25f, 2600f);
    public const float PlatformDial = 6f;

    /// <summary>
    /// The truck as a heavy rig's parts (#417 review): chassis, wheels, lamps, and a hollow cab with
    /// its cockpit (<see cref="HeavyCabin.Build"/>: seats, wheel, dials, pedals, mirrors). The stairs
    /// are not in it: they are their own node (<see cref="AirstairsStairs"/>), redrawn as they rise.
    /// </summary>
    public static HeavyParts Parts()
    {
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var reverse = new MeshScratch();
        const float L = AirstairsLayout.ChassisRear, F = AirstairsLayout.Bumper, W = AirstairsLayout.HalfWidth;
        const float cr = AirstairsLayout.CabRear, ct = AirstairsLayout.CabTop, fl = AirstairsLayout.CabFloor, wall = AirstairsLayout.CabWall;
        const float wl = AirstairsLayout.WinLow, wh = AirstairsLayout.WinHigh;
        // frame rails behind the cab, mudguards and skirts, a black and yellow rear bumper
        m.Box(new Vector3(0, (AirstairsLayout.FrameLow + AirstairsLayout.FrameTop) * 0.5f, (L + cr) * 0.5f),
            new Vector3(W * 2f - 0.3f, AirstairsLayout.FrameTop - AirstairsLayout.FrameLow, cr - L), Chassis);
        foreach (int side in new[] { 1, -1 })
        {
            // the skirt, cut round the rear wheels
            float r = AirstairsLayout.WheelRadius + 0.08f, ra = AirstairsLayout.RearAxle;
            m.Box(new Vector3(side * (W - 0.08f), 0.95f, (L + ra - r) * 0.5f), new Vector3(0.16f, 0.22f, ra - r - L), Cab);
            m.Box(new Vector3(side * (W - 0.08f), 0.95f, (ra + r + cr) * 0.5f), new Vector3(0.16f, 0.22f, cr - ra - r), Cab);
        }
        for (int i = 0; i < 6; i++)
            m.Box(new Vector3(-W + 0.2f + i * (W * 2f - 0.4f) / 5f, 0.55f, L - 0.05f), new Vector3(0.36f, 0.22f, 0.12f), i % 2 == 0 ? Cab : Stripe);
        foreach (int side in new[] { 1, -1 })
        {
            HeavyMesh.Lamp(tail, side * (W - 0.25f), 0.72f, L - 0.12f, 0.2f, 0.1f, 0.04f, HeavyMesh.TailLamp);
            HeavyMesh.Lamp(reverse, side * (W - 0.5f), 0.72f, L - 0.12f, 0.12f, 0.1f, 0.04f, HeavyMesh.White);
            HeavyMesh.Lamp(head, side * (W - 0.25f), 1.05f, F + 0.06f, 0.28f, 0.14f, 0.04f, HeavyMesh.HeadLamp);
        }

        // ---- the cab: hollow above its floor, glass you see through ----
        // under the floor: solid from the front axle's arch to the bumper
        // (#755) solid from the back wall (it used to start 55 cm on): the floor's underside there shared its plane with the seats' feet
        m.Box(new Vector3(0, (0.45f + fl) * 0.5f, (cr + wall + F) * 0.5f), new Vector3(W * 2f, fl - 0.45f, F - cr - wall), Cab);
        m.Box(new Vector3(0, fl + 0.005f, (cr + F) * 0.5f), new Vector3((W - wall) * 2f, 0.01f, F - cr - 2f * wall), HeavyCabin.FloorColour);
        // the face: under the windscreen, over it, the two front pillars, the windscreen
        m.Box(new Vector3(0, (0.45f + wl) * 0.5f, F - wall * 0.5f), new Vector3(W * 2f, wl - 0.45f, wall), Cab);
        m.Box(new Vector3(0, (wh + ct) * 0.5f, F - wall * 0.5f), new Vector3(W * 2f, ct - wh, wall), Cab);
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (W - 0.06f), (wl + wh) * 0.5f, F - wall * 0.5f), new Vector3(0.12f, wh - wl, wall), Cab);
        HeavyMesh.FrontPane(m, F - 0.03f, W - 0.12f, wl, wh);
        // the roof and its lining, the back wall with a small window
        m.Box(new Vector3(0, ct - wall * 0.5f, (cr + F) * 0.5f), new Vector3(W * 2f, wall, F - cr), Cab);
        // the headliner: a warm grey, not the trucks' pale one, which in a lit style's cool shade reads as
        // the sky through an open roof; ribs across it and a dome lamp, so it reads as a ceiling
        m.Box(new Vector3(0, ct - wall - 0.01f, (cr + F) * 0.5f), new Vector3((W - wall) * 2f, 0.02f, F - cr - 2f * wall), Headliner);
        for (int i = 1; i <= 3; i++)
            m.Box(new Vector3(0, ct - wall - 0.035f, cr + (F - cr) * i / 4f), new Vector3((W - wall) * 2f, 0.03f, 0.06f), HeavyMesh.Trim);
        m.Box(new Vector3(0, ct - wall - 0.04f, cr + (F - cr) * 0.375f), new Vector3(0.22f, 0.03f, 0.12f), new Color(1f, 0.95f, 0.8f));
        // a sun visor over the driver
        m.Box(new Vector3(Frame.DriverX, ct - wall - 0.06f, F - 0.2f), new Vector3(0.5f, 0.02f, 0.22f), HeavyMesh.Trim);
        m.Box(new Vector3(0, (fl + wl) * 0.5f, cr + wall * 0.5f), new Vector3(W * 2f, wl - fl, wall), Cab);
        m.Box(new Vector3(0, (wh + ct) * 0.5f, cr + wall * 0.5f), new Vector3(W * 2f, ct - wh, wall), Cab);
        foreach (int side in new[] { 1, -1 })
            m.Box(new Vector3(side * (W - 0.32f), (wl + wh) * 0.5f, cr + wall * 0.5f), new Vector3(0.64f, wh - wl, wall), Cab);
        m.Pane(new[] { new Vector3(0.51f, wl, cr), new Vector3(-0.51f, wl, cr), new Vector3(-0.51f, wh, cr), new Vector3(0.51f, wh, cr) }, HeavyMesh.PaneTint);
        // the side walls with the door's window in each, a pillar behind it
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * (W - wall * 0.5f);
            m.Box(new Vector3(x, (fl + wl) * 0.5f, (cr + F) * 0.5f), new Vector3(wall, wl - fl, F - cr), Cab);
            m.Box(new Vector3(x, (wh + ct) * 0.5f, (cr + F) * 0.5f), new Vector3(wall, ct - wh, F - cr), Cab);
            m.Box(new Vector3(x, (wl + wh) * 0.5f, cr + 0.12f), new Vector3(wall, wh - wl, 0.24f), Cab);
            HeavyMesh.SidePane(m, side * W, F - 0.12f, cr + 0.24f, wl, wh);
            // the door: its outline and its handle
            // (#755) the outline and handle stand 1 cm clear of the door glass at the wall's face
            m.Box(new Vector3(side * (W + 0.015f), (fl + wh) * 0.5f, cr + 0.26f), new Vector3(0.01f, wh - fl + 0.1f, 0.02f), Stripe);
            m.Box(new Vector3(side * (W + 0.035f), 1.2f, cr + 0.4f), new Vector3(0.03f, 0.04f, 0.16f), Stripe);
        }
        // grille, bumper, beacon on the roof
        m.Box(new Vector3(0, 0.95f, F + 0.02f), new Vector3(W * 2f - 0.5f, 0.35f, 0.04f), Stripe);
        m.Box(new Vector3(0, 0.6f, F + 0.06f), new Vector3(W * 2f, 0.28f, 0.12f), Stripe);
        m.Tube(new Vector3(-0.6f, ct, cr + 0.3f), new Vector3(-0.6f, ct + 0.16f, cr + 0.3f), 0.1f, Beacon, 6);
        // mirrors out on short arms from the front corners
        var mirrors = new List<(string, Vector3, Vector2)>();
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * (W + 0.16f);
            m.Tube(new Vector3(side * W, 1.95f, F - 0.2f), new Vector3(x, 1.95f, F - 0.28f), 0.02f, HeavyMesh.Trim, 4);
            m.Tube(new Vector3(x, 1.95f, F - 0.28f), new Vector3(x, 1.42f, F - 0.28f), 0.018f, HeavyMesh.Trim, 4);
            mirrors.Add((side > 0 ? "MirrorLeft" : "MirrorRight", new Vector3(x, 1.68f, F - 0.33f), new Vector2(0.16f, 0.3f)));
        }
        // the cockpit: seats, column and wheel, dash and dials, pedals, the mirrors' faces
        var cockpit = HeavyCabin.Build(m, Frame, Gauges, 0f, PlatformDial, mirrors);
        var seats = new[]
        {
            new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, AirstairsLayout.CabFloor),
            new SeatAnchor(0, CarMeshBuilder.Turned(cockpit.Seat.Hip with { X = -cockpit.Seat.Hip.X }), cockpit.Seat.Recline, AirstairsLayout.CabFloor),
        };

        var wheel = Wheel();
        var wheels = new List<HeavyWheel>();
        foreach (float z in new[] { AirstairsLayout.FrontAxle, AirstairsLayout.RearAxle })
            foreach (int side in new[] { 1, -1 })
                wheels.Add(new HeavyWheel(wheel, new Vector3(-side * AirstairsLayout.Track, AirstairsLayout.WheelRadius, -z),
                    z == AirstairsLayout.FrontAxle ? 1f : 0f));
        return new HeavyParts(m.Build(), head.Build(), tail.Build(), reverse.Build(), wheels.ToArray(), System.Array.Empty<HeavyDoorLeaf>())
        {
            Cockpit = cockpit,
            Seats = seats,
        };
    }

    private static ArrayMesh? _wheel;

    /// <summary>One wheel on its hub, facing ±X: the tyre and a steel rim on both faces.</summary>
    private static ArrayMesh Wheel()
    {
        if (_wheel != null) return _wheel;
        var s = new MeshScratch();
        float w = AirstairsLayout.TyreWidth * 0.5f;
        s.Tube(new Vector3(-w, 0, 0), new Vector3(w, 0, 0), AirstairsLayout.WheelRadius, Tyre, 10);
        s.Tube(new Vector3(-w - 0.015f, 0, 0), new Vector3(w + 0.015f, 0, 0), 0.24f, Steel, 6);
        return _wheel = s.Build();
    }

    /// <summary>
    /// The drawn airstairs: a heavy rig (cockpit, driver, wheels, lamps, mirrors: everything a truck's
    /// first person has) with the stairs as its child node "Stairs".
    /// </summary>
    /// <param name="driver">The figure at the wheel; null parked.</param>
    public static HeavyRig CreateRig(float height, HumanPalette? driver)
    {
        var rig = HeavyRig.Create(Parts(), driver);
        rig.Name = "Airstairs";
        // the small gauge is the platform's: no low-air lamp
        rig.AirLowAt = -1f;
        rig.AddChild(AirstairsStairs.Create(height));
        return rig;
    }

    /// <summary>The stairs node of a drawn airstairs, or null (headless: nothing drawn).</summary>
    public static AirstairsStairs? StairsOf(Node3D? visual) => visual?.GetNodeOrNull<AirstairsStairs>("Stairs");

    /// <summary>The stairs at platform height <paramref name="h"/>: flight, platform, plate, rails, lift and canopy.</summary>
    public static void Stairs(MeshScratch s, float h)
    {
        h = AirstairsLayout.Clamp(h);
        Flight(s, h);
        Platform(s, h);
        float beam = Beam(h);
        foreach (int side in new[] { 1, -1 })
        {
            s.Box(new Vector3(side * AirstairsLayout.ColumnX, (ColumnFoot + beam) * 0.5f, AirstairsLayout.ColumnZ),
                new Vector3(0.14f, beam - ColumnFoot, 0.14f), Steel);
            if (StrutTop(h) is { } under)
                s.Box(new Vector3(side * 0.45f, (AirstairsLayout.FrameTop + under) * 0.5f, StrutZ), new Vector3(0.1f, under - AirstairsLayout.FrameTop, 0.1f), Steel);
        }
    }

    /// <summary>How many treads the flight has at platform height <paramref name="h"/> (every 18-20 cm of rise), and how many rail posts.</summary>
    public static (int Treads, int Posts) FlightCounts(float h)
    {
        h = AirstairsLayout.Clamp(h);
        float length = new Vector2(AirstairsLayout.PlatformRear - AirstairsLayout.FootZ, h).Length();
        return (Mathf.CeilToInt(h / 0.2f), Mathf.Max(2, Mathf.CeilToInt(length / 1.4f)));
    }

    /// <summary>The flight up to platform height <paramref name="h"/>: stringers, level treads, handrails on their posts.</summary>
    public static void Flight(MeshScratch s, float h)
    {
        h = AirstairsLayout.Clamp(h);
        float fz = AirstairsLayout.FootZ, tz = AirstairsLayout.PlatformRear;
        var foot = new Vector3(0, 0f, fz);
        var top = new Vector3(0, h, tz);
        var run = top - foot;
        float length = run.Length();
        var along = run / length;
        // a frame for the flight's sloped parts: x across, z up the slope, y out of its face
        var flight = new Basis(Vector3.Right, along.Cross(Vector3.Right), along).Orthonormalized();
        var (steps, posts) = FlightCounts(h);

        // stringers either side of the treads, and level treads every 18-20 cm of rise
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * AirstairsLayout.RailX, 0, 0) + (foot + top) * 0.5f - flight.Y * 0.1f,
                new Vector3(0.08f, 0.3f, length), Steel, flight);
        float tread = run.Z / steps;
        for (int i = 1; i <= steps; i++)
        {
            var at = foot + run * (i / (float)steps);
            // (#755) a tread ends at its nosing's line, not 2 cm past it, so the top one does not lie in the platform's edge
            s.Box(at - new Vector3(0, 0.03f, tread * 0.5f + 0.01f), new Vector3(AirstairsLayout.FlightHalfWidth * 2f, 0.06f, tread + 0.02f), Tread);
            s.Box(at + new Vector3(0, 0.005f, -0.005f), new Vector3(AirstairsLayout.FlightHalfWidth * 2f, 0.03f, 0.05f), Cab);   // nosing, (#755) 2 cm proud of the tread
        }
        // handrails on posts up the flight
        foreach (int side in new[] { 1, -1 })
        {
            var off = new Vector3(side * AirstairsLayout.RailX, AirstairsLayout.RailHeight, 0);
            s.Tube(foot + off, top + off, 0.035f, Rail, 5);
            s.Tube(foot + off - new Vector3(0, 0.5f, 0), top + off - new Vector3(0, 0.5f, 0), 0.025f, Rail, 4);
            for (int i = 0; i <= posts; i++)
            {
                var p = foot + run * (i / (float)posts) + new Vector3(side * AirstairsLayout.RailX, 0, 0);
                s.Tube(p, p + Vector3.Up * AirstairsLayout.RailHeight, 0.03f, Rail, 4);
            }
        }
    }

    /// <summary>
    /// The platform at height <paramref name="h"/>: its side rails, the bridge plate, the canopy over
    /// it and the lift's beam under it. Everything here rises with the platform as one.
    /// </summary>
    public static void Platform(MeshScratch s, float h)
    {
        float pr = AirstairsLayout.PlatformRear, pf = AirstairsLayout.PlatformFront, pw = AirstairsLayout.PlatformHalfWidth;
        s.Box(new Vector3(0, h - AirstairsLayout.PlatformThick * 0.5f, (pr + pf) * 0.5f), new Vector3(pw * 2f, AirstairsLayout.PlatformThick, pf - pr), Steel);
        s.Box(new Vector3(0, h + 0.004f, (pr + pf) * 0.5f), new Vector3(pw * 2f - 0.1f, 0.01f, pf - pr - 0.1f), Tread);
        foreach (int side in new[] { 1, -1 })
        {
            float x = side * pw;
            s.Tube(new Vector3(x, h + AirstairsLayout.RailHeight, pr), new Vector3(x, h + AirstairsLayout.RailHeight, pf), 0.035f, Rail, 5);
            s.Box(new Vector3(x, h + 0.15f, (pr + pf) * 0.5f), new Vector3(0.03f, 0.3f, pf - pr), Cab);   // kick plate
            foreach (float z in new[] { pr, (pr + pf) * 0.5f, pf })
                s.Tube(new Vector3(x, h, z), new Vector3(x, h + AirstairsLayout.RailHeight, z), 0.03f, Rail, 4);
            // the canopy's posts
            s.Tube(new Vector3(x, h + AirstairsLayout.RailHeight, pr), new Vector3(x, h + AirstairsLayout.CanopyOver, pr), 0.04f, Steel, 4);
            s.Tube(new Vector3(x, h + AirstairsLayout.RailHeight, pf - 0.1f), new Vector3(x, h + AirstairsLayout.CanopyOver, pf - 0.1f), 0.04f, Steel, 4);
        }
        s.Box(new Vector3(0, h + AirstairsLayout.CanopyOver + 0.05f, (pr + pf) * 0.5f - 0.1f), new Vector3(pw * 2f + 0.2f, 0.1f, pf - pr + 0.4f), Canopy);
        s.Box(new Vector3(0, h + AirstairsLayout.CanopyOver - 0.08f, pf + 0.08f), new Vector3(pw * 2f + 0.2f, 0.16f, 0.06f), Cab);
        var plate = new Vector3(0, h - AirstairsLayout.PlateDrop * 0.5f - 0.02f, (pf + AirstairsLayout.LipZ) * 0.5f);
        var slope = new Vector3(0, -AirstairsLayout.PlateDrop, AirstairsLayout.LipZ - pf).Normalized();
        s.Box(plate, new Vector3(1.2f, 0.04f, AirstairsLayout.LipZ - pf), Stripe, new Basis(Vector3.Right, slope.Cross(Vector3.Right), slope).Orthonormalized());
        // the lift's beam under the platform
        s.Box(new Vector3(0, Beam(h), AirstairsLayout.ColumnZ), new Vector3(AirstairsLayout.ColumnX * 2f + 0.16f, 0.12f, 0.14f), Steel);
    }

    /// <summary>The lift: a column either side of the cab from <see cref="ColumnFoot"/> up to the beam; one metre tall, scaled to it.</summary>
    public static void Columns(MeshScratch s)
    {
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * AirstairsLayout.ColumnX, 0.5f, AirstairsLayout.ColumnZ), new Vector3(0.14f, 1f, 0.14f), Steel);
    }

    /// <summary>The struts under the flight, from the frame up; one metre tall, scaled to <see cref="StrutTop"/>.</summary>
    public static void Struts(MeshScratch s)
    {
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * 0.45f, 0.5f, StrutZ), new Vector3(0.1f, 1f, 0.1f), Steel);
    }

    /// <summary>The lift's beam under a platform at <paramref name="h"/>.</summary>
    public static float Beam(float h) => h - AirstairsLayout.PlatformThick - 0.06f;

    /// <summary>Where the lift's columns stand, and the struts' station under the flight.</summary>
    public const float ColumnFoot = 0.6f, StrutZ = -1.0f;

    /// <summary>The struts' top under the ramp at <paramref name="h"/>, or null when the ramp is too low for them.</summary>
    public static float? StrutTop(float h)
    {
        float under = AirstairsLayout.FloorAt(h, StrutZ) - AirstairsLayout.RampThick - 0.15f;
        return under > AirstairsLayout.FrameTop + 0.1f ? under : null;
    }

    /// <summary>
    /// The walkable deck at platform height <paramref name="h"/>, the drawn truck's node frame (one
    /// section, 0). <paramref name="atDoor"/>: docked, the platform's front is open onto the plate;
    /// otherwise a gate closes it. Always the same boxes in the same order for a given
    /// <paramref name="atDoor"/>, so a deck built round a walker is moved box by box as it rises.
    /// </summary>
    public static VehicleDeck Deck(float h, bool atDoor)
    {
        h = AirstairsLayout.Clamp(h);
        // stations behind the origin: a DeckBuilder's z = cg − station, cg 0
        static float At(float z) => -z;
        var dk = new DeckBuilder(0f);
        float pr = AirstairsLayout.PlatformRear, pf = AirstairsLayout.PlatformFront, pw = AirstairsLayout.PlatformHalfWidth;
        const float W = AirstairsLayout.HalfWidth;
        // the flight: one ramp from a little under the ground at its foot to the platform's back edge,
        // flush with the platform's floor (the walk has no step-up: every change of height is a ramp)
        dk.RampAlong(At(AirstairsLayout.FootZ), -0.04f, At(pr), h, AirstairsLayout.FlightHalfWidth * 2f + 0.12f);
        // the platform and the bridge plate down onto a sill
        dk.Along(At(pf), At(pr), h - AirstairsLayout.PlatformThick, h, pw * 2f);
        dk.RampAlong(At(AirstairsLayout.LipZ), h - AirstairsLayout.PlateDrop, At(pf), h, 1.2f);
        // the platform's side rails, solid to the rail's height
        foreach (int side in new[] { 1, -1 })
            dk.Along(At(pf), At(pr), h, h + AirstairsLayout.RailHeight, 0.06f, side * (pw + 0.03f));
        // not at a door: a gate across the platform's front
        if (!atDoor) dk.Along(At(pf), At(pf - 0.06f), h, h + AirstairsLayout.RailHeight, pw * 2f);
        // the truck itself, solid to a walker (its hull is not: a deck's vehicle ignores its walkers):
        // the cab under the platform, the bed under the flight where the ramp is above it, and the bed
        // either side of the flight's low end
        dk.Along(At(AirstairsLayout.Bumper + 0.1f), At(AirstairsLayout.CabRear), 0.4f, AirstairsLayout.CabTop, W * 2f);
        float bed = AirstairsLayout.FrameTop + 0.1f;
        float clear = AirstairsLayout.FootZ + (bed + RampUnder + 0.04f) / (h + 0.04f) * (pr - AirstairsLayout.FootZ);
        dk.Along(At(AirstairsLayout.CabRear), At(clear), 0.4f, bed, W * 2f);
        foreach (int side in new[] { 1, -1 })
            dk.Along(At(clear), At(AirstairsLayout.ChassisRear), 0.4f, bed, W - AirstairsLayout.RailX - 0.06f,
                side * (W + AirstairsLayout.RailX + 0.06f) * 0.5f);
        // holds: the rails' tops, on the plan, as a bus's poles
        foreach (int side in new[] { 1, -1 })
            for (float z = AirstairsLayout.FootZ + 0.5f; z <= pf; z += 1f)
                dk.Hold(side * (z < pr ? AirstairsLayout.RailX : pw), At(z));
        // aboard: between the rails from where the ramp is off the ground to the lip: standing there,
        // a walker is carried with the truck (driven, docking, rising) and only the decks hold them
        float from = AirstairsLayout.FootZ + 0.3f;
        dk.PlanAt(AirstairsLayout.RailX - 0.03f, At(from));
        dk.PlanAt(AirstairsLayout.RailX - 0.03f, At(pr));
        dk.PlanAt(pw, At(pr));
        dk.PlanAt(pw, At(AirstairsLayout.LipZ));
        dk.PlanAt(-pw, At(AirstairsLayout.LipZ));
        dk.PlanAt(-pw, At(pr));
        dk.PlanAt(-(AirstairsLayout.RailX - 0.03f), At(pr));
        dk.PlanAt(-(AirstairsLayout.RailX - 0.03f), At(from));
        var deck = dk.Build(0, new Aabb(new Vector3(-pw, -0.3f, from), new Vector3(pw * 2f, h + 2.5f, AirstairsLayout.LipZ - from)));
        // the flight's rails, tilted with it: panels from the treads to the handrail
        var foot = Node(new Vector3(0, -0.04f, AirstairsLayout.FootZ));
        var topN = Node(new Vector3(0, h, pr));
        var run = topN - foot;
        float length = run.Length();
        var along = run / length;
        var up = along.Cross(Vector3.Right).Normalized();
        if (up.Y < 0f) up = -up;
        // right-handed: x across (to the node's left), y out of the slope, z up it
        var basis = new Basis(Vector3.Left, up, along).Orthonormalized();
        var rails = new DeckBox[2];
        for (int i = 0; i < 2; i++)
        {
            float x = (i == 0 ? 1 : -1) * (AirstairsLayout.RailX + 0.03f);
            var centre = (foot + topN) * 0.5f + up * (AirstairsLayout.RailHeight * 0.5f) + new Vector3(x, 0, 0);
            rails[i] = new DeckBox(centre, new Vector3(0.06f, AirstairsLayout.RailHeight, length), basis);
        }
        return deck with { Boxes = deck.Boxes.Concat(rails).ToArray() };
    }

    /// <summary>The ramp's underside below its face, vertically, at the steepest the flight goes (a margin over the slab).</summary>
    private const float RampUnder = 0.15f;

    private static Vector3 Node(Vector3 authored) => new(-authored.X, authored.Y, -authored.Z);
}

/// <summary>
/// The stairs of a drawn airstairs (#417): flight, platform, rails, lift, canopy and the gate, each
/// built once and moved as the platform rises (#1202): the platform, canopy and beam ride up as one,
/// the columns and struts stretch to it, and the flight (one mesh per count of treads and posts) is
/// sheared up from its foot, so its rails, posts and treads stay where they were drawn. Nothing
/// is rebuilt per frame. A child of the truck's <see cref="HeavyRig"/>, named "Stairs".
/// </summary>
public partial class AirstairsStairs : Node3D
{
    // one flight mesh per count of treads and posts, and the height it was drawn at
    private static readonly Dictionary<(int, int), (ArrayMesh Mesh, float Height)> Flights = new();
    private static ArrayMesh? _platform, _columns, _struts, _gateMesh;

    private MeshInstance3D _flight = null!, _platformNode = null!, _columnNode = null!, _strutNode = null!, _gate = null!;
    private float _drawn = float.NaN;

    /// <summary>The platform's height over the ground, m: the stairs' parts move to it.</summary>
    public float Height
    {
        get => _drawn;
        set
        {
            if (value == _drawn) return;
            _drawn = value;
            float h = AirstairsLayout.Clamp(value);
            var counts = AirstairsMeshBuilder.FlightCounts(h);
            if (!Flights.TryGetValue(counts, out var flight))
            {
                var s = new MeshScratch();
                AirstairsMeshBuilder.Flight(s, h);
                Flights[counts] = flight = (s.Build(), h);
            }
            if (_flight.Mesh != flight.Mesh) _flight.Mesh = flight.Mesh;
            // node space (+Z aft): a point rises with how far up the flight it is from its foot
            float k = (h - flight.Height) / (AirstairsLayout.PlatformRear - AirstairsLayout.FootZ);
            _flight.Transform = new Transform3D(new Basis(Vector3.Right, Vector3.Up, new Vector3(0, -k, 1f)),
                new Vector3(0, -k * AirstairsLayout.FootZ, 0));
            _platformNode.Position = new Vector3(0, h, 0);
            float beam = AirstairsMeshBuilder.Beam(h);
            _columnNode.Transform = new Transform3D(Basis.FromScale(new Vector3(1f, beam - AirstairsMeshBuilder.ColumnFoot, 1f)),
                new Vector3(0, AirstairsMeshBuilder.ColumnFoot, 0));
            var strut = AirstairsMeshBuilder.StrutTop(h);
            _strutNode.Visible = strut != null;
            if (strut is { } top)
                _strutNode.Transform = new Transform3D(Basis.FromScale(new Vector3(1f, top - AirstairsLayout.FrameTop, 1f)),
                    new Vector3(0, AirstairsLayout.FrameTop, 0));
            _gate.Position = new Vector3(0, h, 0);
        }
    }

    /// <summary>At a door: the gate across the platform's front is folded away.</summary>
    public bool AtDoor
    {
        get => !_gate.Visible;
        set { if (_gate.Visible == value) _gate.Visible = !value; }
    }

    public static AirstairsStairs Create(float height)
    {
        var material = HumanMeshBuilder.FigureMaterial();
        var node = new AirstairsStairs { Name = "Stairs" };
        MeshInstance3D Part(string name, ArrayMesh? mesh)
        {
            var part = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = material };
            node.AddChild(part);
            return part;
        }
        node._flight = Part("Flight", null);
        node._platformNode = Part("Platform", _platform ??= Built(s => AirstairsMeshBuilder.Platform(s, 0f)));
        node._columnNode = Part("Columns", _columns ??= Built(AirstairsMeshBuilder.Columns));
        node._strutNode = Part("Struts", _struts ??= Built(AirstairsMeshBuilder.Struts));
        node._gate = Part("Gate", _gateMesh ??= Built(Gate));
        node.Height = height;
        return node;
    }

    private static ArrayMesh Built(Action<MeshScratch> draw)
    {
        var s = new MeshScratch();
        draw(s);
        return s.Build();
    }

    /// <summary>The gate: a yellow and black bar across the front at hand height, its posts (height 0 here).</summary>
    private static void Gate(MeshScratch g)
    {
        float pw = AirstairsLayout.PlatformHalfWidth, z = AirstairsLayout.PlatformFront - 0.03f;
        for (int i = 0; i < 6; i++)
            g.Box(new Vector3(-pw + (i + 0.5f) * pw / 3f, AirstairsLayout.RailHeight - 0.05f, z), new Vector3(pw / 3f, 0.08f, 0.04f),
                i % 2 == 0 ? new Color(0.95f, 0.74f, 0.12f) : new Color(0.12f, 0.12f, 0.12f));
        g.Box(new Vector3(0, 0.5f, z), new Vector3(pw * 2f, 0.04f, 0.03f), new Color(0.9f, 0.9f, 0.88f));
    }
}
