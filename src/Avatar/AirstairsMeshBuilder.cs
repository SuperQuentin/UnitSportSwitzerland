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
    /// <summary>Wheels: radius, axles, half track.</summary>
    public const float WheelRadius = 0.45f, FrontAxle = 2.4f, RearAxle = -2.4f, Track = 0.95f;
    public const float Wheelbase = FrontAxle - RearAxle;
    /// <summary>The cab: from its back wall to the bumper, its roof.</summary>
    public const float CabRear = 2.25f, CabTop = 2.2f;

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
    /// <summary>The platform's range (its underside clears the cab), and where it rides when driven.</summary>
    public const float MinHeight = 2.4f, MaxHeight = 5.6f, TravelHeight = 2.5f;
    /// <summary>Lift columns under the platform's back, just behind the cab.</summary>
    public const float ColumnZ = 2.08f, ColumnX = 0.55f;
    /// <summary>The canopy over the platform: its underside over the platform floor.</summary>
    public const float CanopyOver = 2.2f;

    /// <summary>The driver's eye and the cab's door (left, a left-hand-drive cab).</summary>
    public static readonly Vector3 Eye = new(0.45f, 1.85f, 2.95f);
    public static readonly Vector3 CabDoor = new(HalfWidth + 0.15f, 0f, 2.8f);

    public static float Clamp(float h) => Mathf.Clamp(h, MinHeight, MaxHeight);
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

    /// <summary>The truck itself: chassis, wheels, cab, beacon. It never changes.</summary>
    public static ArrayMesh Truck()
    {
        var s = new MeshScratch();
        const float L = AirstairsLayout.ChassisRear, F = AirstairsLayout.Bumper, W = AirstairsLayout.HalfWidth;
        // frame rails and the deck plate over them
        s.Box(new Vector3(0, (AirstairsLayout.FrameLow + AirstairsLayout.FrameTop) * 0.5f, (L + AirstairsLayout.CabRear) * 0.5f),
            new Vector3(W * 2f - 0.3f, AirstairsLayout.FrameTop - AirstairsLayout.FrameLow, AirstairsLayout.CabRear - L), Chassis);
        // mudguards and side skirts in the cab's colour, a black and yellow rear bumper
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * (W - 0.08f), 0.95f, (L + AirstairsLayout.CabRear) * 0.5f), new Vector3(0.16f, 0.22f, AirstairsLayout.CabRear - L), Cab);
        for (int i = 0; i < 6; i++)
            s.Box(new Vector3(-W + 0.2f + i * (W * 2f - 0.4f) / 5f, 0.55f, L - 0.05f), new Vector3(0.36f, 0.22f, 0.12f), i % 2 == 0 ? Cab : Stripe);
        // wheels
        foreach (float z in new[] { AirstairsLayout.FrontAxle, AirstairsLayout.RearAxle })
            foreach (int side in new[] { 1, -1 })
            {
                var c = new Vector3(side * AirstairsLayout.Track, AirstairsLayout.WheelRadius, z);
                s.Tube(c - new Vector3(0.16f, 0, 0), c + new Vector3(0.16f, 0, 0), AirstairsLayout.WheelRadius, Tyre, 8);
                s.Tube(c + new Vector3(side * 0.15f, 0, 0), c + new Vector3(side * 0.17f, 0, 0), 0.24f, Steel, 6);
            }
        // the cab: a low box, a raked windscreen, side windows, a beacon on the roof
        float cr = AirstairsLayout.CabRear, ct = AirstairsLayout.CabTop;
        s.Box(new Vector3(0, (AirstairsLayout.FrameTop + 1.35f) * 0.5f, (cr + F) * 0.5f), new Vector3(W * 2f, 1.35f - AirstairsLayout.FrameTop + 0.3f, F - cr), Cab);
        s.Box(new Vector3(0, (1.5f + ct) * 0.5f, (cr + F - 0.25f) * 0.5f), new Vector3(W * 2f - 0.06f, ct - 1.5f, F - 0.25f - cr), Cab);
        s.Box(new Vector3(0, 1.82f, F - 0.12f), new Vector3(W * 2f - 0.2f, 0.6f, 0.06f), Glass);
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * W, 1.82f, (cr + F) * 0.5f + 0.1f), new Vector3(0.04f, 0.5f, 0.75f), Glass);
        s.Box(new Vector3(0, 0.7f, F + 0.05f), new Vector3(W * 2f, 0.3f, 0.12f), Stripe);      // front bumper
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * (W - 0.25f), 1.15f, F + 0.01f), new Vector3(0.25f, 0.14f, 0.04f), new Color(1f, 0.97f, 0.85f));
        s.Tube(new Vector3(-0.6f, ct, cr + 0.4f), new Vector3(-0.6f, ct + 0.18f, cr + 0.4f), 0.11f, Beacon, 6);
        return s.Build();
    }

    /// <summary>The stairs at platform height <paramref name="h"/>: flight, platform, plate, rails, lift and canopy.</summary>
    public static void Stairs(MeshScratch s, float h)
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

        // stringers either side of the treads, and level treads every 18-20 cm of rise
        foreach (int side in new[] { 1, -1 })
            s.Box(new Vector3(side * AirstairsLayout.RailX, 0, 0) + (foot + top) * 0.5f - flight.Y * 0.1f,
                new Vector3(0.08f, 0.3f, length), Steel, flight);
        int steps = Mathf.CeilToInt(h / 0.2f);
        float tread = run.Z / steps;
        for (int i = 1; i <= steps; i++)
        {
            var at = foot + run * (i / (float)steps);
            s.Box(at - new Vector3(0, 0.03f, tread * 0.5f), new Vector3(AirstairsLayout.FlightHalfWidth * 2f, 0.06f, tread + 0.04f), Tread);
            s.Box(at - new Vector3(0, 0.005f, 0.02f), new Vector3(AirstairsLayout.FlightHalfWidth * 2f, 0.012f, 0.05f), Cab);   // nosing
        }
        // handrails on posts up the flight
        int posts = Mathf.Max(2, Mathf.CeilToInt(length / 1.4f));
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

        // the platform, its side rails, the bridge plate
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

        // the lift: two columns from the frame under the platform's back, a strut under the flight
        foreach (int side in new[] { 1, -1 })
        {
            float bottom = AirstairsLayout.FrameTop;
            float topY = h - AirstairsLayout.PlatformThick;
            s.Box(new Vector3(side * AirstairsLayout.ColumnX, (bottom + topY) * 0.5f, AirstairsLayout.ColumnZ),
                new Vector3(0.16f, topY - bottom, 0.16f), Steel);
            float mid = -1.0f;
            float under = (mid - fz) / run.Z * h - 0.25f;
            if (under > bottom + 0.1f)
                s.Box(new Vector3(side * (AirstairsLayout.ColumnX - 0.1f), (bottom + under) * 0.5f, mid), new Vector3(0.1f, under - bottom, 0.1f), Steel);
        }
    }

    /// <summary>The walkable deck at platform height <paramref name="h"/>, the drawn truck's node frame (one section, 0).</summary>
    public static VehicleDeck Deck(float h)
    {
        h = AirstairsLayout.Clamp(h);
        // stations behind the origin: a DeckBuilder's z = cg − station, cg 0
        static float At(float z) => -z;
        var dk = new DeckBuilder(0f);
        float pr = AirstairsLayout.PlatformRear, pf = AirstairsLayout.PlatformFront, pw = AirstairsLayout.PlatformHalfWidth;
        // the flight: one ramp from a little under the ground at its foot to the platform's back edge,
        // flush with the platform's floor (the walk has no step-up: every change of height is a ramp)
        dk.RampAlong(At(AirstairsLayout.FootZ), -0.04f, At(pr), h, AirstairsLayout.FlightHalfWidth * 2f + 0.12f);
        // the platform and the bridge plate down onto a sill
        dk.Along(At(pf), At(pr), h - AirstairsLayout.PlatformThick, h, pw * 2f);
        dk.RampAlong(At(AirstairsLayout.LipZ), h - AirstairsLayout.PlateDrop, At(pf), h, 1.2f);
        // the platform's side rails, solid to the rail's height
        foreach (int side in new[] { 1, -1 })
            dk.Along(At(pf), At(pr), h, h + AirstairsLayout.RailHeight, 0.06f, side * (pw + 0.03f));
        // holds: the rails' tops, on the plan, as a bus's poles
        foreach (int side in new[] { 1, -1 })
            for (float z = AirstairsLayout.FootZ + 0.5f; z <= pf; z += 1f)
                dk.Hold(side * (z < pr ? AirstairsLayout.RailX : pw), At(z));
        // never "aboard": nobody is carried by a parked stair truck, its deck is just there to walk on
        var deck = dk.Build(0, new Aabb(new Vector3(0, -100f, 0), new Vector3(0.01f, 0.01f, 0.01f)));
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

    private static Vector3 Node(Vector3 authored) => new(-authored.X, authored.Y, -authored.Z);
}

/// <summary>
/// The drawn airstairs (#417): the truck's mesh and the stairs' mesh, rebuilt in place (one mesh, one
/// scratch) only when the platform has moved a couple of centimetres; nothing per frame at rest.
/// </summary>
public partial class AirstairsRig : Node3D
{
    private MeshInstance3D _stairs = null!;
    private readonly MeshScratch _scratch = new();
    private readonly ArrayMesh _stairsMesh = new();
    private float _drawn = float.NaN;

    /// <summary>The platform's height over the ground, m; the stairs are redrawn when it moves 2 cm.</summary>
    public float Height
    {
        get => _drawn;
        set
        {
            if (!float.IsNaN(_drawn) && Mathf.Abs(value - _drawn) < 0.02f) return;
            _drawn = value;
            _scratch.Clear();
            AirstairsMeshBuilder.Stairs(_scratch, value);
            _scratch.BuildInto(_stairsMesh);
        }
    }

    public static AirstairsRig Create(float height)
    {
        var rig = new AirstairsRig { Name = "Airstairs" };
        var material = HumanMeshBuilder.FigureMaterial();
        rig.AddChild(new MeshInstance3D { Name = "Truck", Mesh = AirstairsMeshBuilder.Truck(), MaterialOverride = material });
        rig._stairs = new MeshInstance3D { Name = "Stairs", Mesh = rig._stairsMesh, MaterialOverride = material };
        rig.AddChild(rig._stairs);
        rig.Height = height;
        return rig;
    }
}
