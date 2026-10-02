using Godot;
using UnitSport.Player;

namespace UnitSport.Avatar;

/// <summary>
/// The boats' low-poly meshes (#302), authored like every other machine (+Z forward, +X the boat's
/// left, origin on the keel under the centre of mass) and flipped to node space by
/// <see cref="MeshScratch.Build()"/>. The hull is lofted from the same lines it floats on
/// (<see cref="BoatSpec.Shape"/>, <see cref="BoatSpec.HalfBeam"/>), so the drawn waterline is the
/// physical one. Drawn in the figure material like the cars and aircraft, so the styles restyle them.
/// </summary>
public static class BoatMeshBuilder
{
    // ---- the jetski: a sit-down three-seater -----------------------------------------------

    /// <summary>The rider's seat surface, right grip and right foot (author space): the driver's straddle.</summary>
    private static readonly Vector3 JetSeat = new(0, 0.97f, -0.3f);
    private static readonly Vector3 JetGrip = new(-0.36f, 1.12f, 0.34f);
    private static readonly Vector3 JetPeg = new(-0.4f, 0.57f, -0.02f);
    /// <summary>The saddle runs from here forward to the hood, author z.</summary>
    private const float JetSeatAft = -1.25f, JetSeatFront = 0.1f;

    /// <summary>
    /// A sit-down PWC: a low vee hull to the footwells, a long raised saddle, and a big rounded hood
    /// over the engine running down to the bow, with the bars on its crown.
    /// </summary>
    public static ArrayMesh JetskiHull(BoatSpec s, Color paint)
    {
        var m = new MeshScratch();
        var white = new Color(0.93f, 0.93f, 0.92f);
        var bottom = new Color(0.22f, 0.23f, 0.26f);
        var black = new Color(0.1f, 0.1f, 0.11f);
        Hull(m, s, bottom, paint, white, white, cockpit: null);
        float deck = s.Shape.Sheer(s.Depth, 0.35f);

        // the footwell mats, either side of the saddle
        foreach (float x in new[] { 0.42f, -0.42f })
            m.Box(new Vector3(x, deck + 0.04f, -0.55f), new Vector3(0.22f, 0.02f, 1.3f), black);
        // the saddle on its plinth
        float seatLen = JetSeatFront - JetSeatAft;
        m.Box(new Vector3(0, (deck + JetSeat.Y - 0.12f) * 0.5f + 0.02f, (JetSeatAft + JetSeatFront) * 0.5f),
            new Vector3(0.5f, JetSeat.Y - 0.12f - deck, seatLen), paint);
        m.Box(new Vector3(0, JetSeat.Y - 0.07f, (JetSeatAft + JetSeatFront) * 0.5f - 0.02f), new Vector3(0.44f, 0.14f, seatLen - 0.06f), black);

        // the hood: a loft from the saddle's front down to the bow
        float bow = s.Length - s.Shape.SternZ;
        var stations = new (float Z, float Half, float Top)[]
        {
            (JetSeatFront - 0.05f, 0.4f, 1.0f), (0.55f, 0.44f, 0.98f), (1.05f, 0.38f, 0.88f), (1.5f, 0.24f, 0.78f), (bow - 0.08f, 0.06f, 0.7f),
        };
        var hood = new List<Vector3[]>();
        foreach (var (z, half, top) in stations)
            hood.Add(new[]
            {
                new Vector3(half, deck - 0.02f, z), new Vector3(half, top - 0.13f, z), new Vector3(half * 0.6f, top, z),
                new Vector3(-half * 0.6f, top, z), new Vector3(-half, top - 0.13f, z), new Vector3(-half, deck - 0.02f, z),
            });
        m.Loft(hood, new[] { paint, white, white, white, paint, paint }, white);
        // the console's screen and the bars on their post
        m.Box(new Vector3(0, 1.02f, 0.22f), new Vector3(0.3f, 0.12f, 0.04f), black, new Basis(Vector3.Right, -0.7f));
        m.Tube(new Vector3(0, 0.98f, JetGrip.Z + 0.05f), new Vector3(0, JetGrip.Y, JetGrip.Z), 0.04f, black);
        m.Tube(JetGrip, JetGrip with { X = -JetGrip.X }, 0.022f, black);
        m.Tube(JetGrip with { X = JetGrip.X + 0.02f }, JetGrip with { X = JetGrip.X - 0.1f }, 0.033f, bottom);
        m.Tube(JetGrip with { X = -JetGrip.X - 0.02f }, JetGrip with { X = -JetGrip.X + 0.1f }, 0.033f, bottom);
        // the jet's nozzle under the transom, the boarding step on the swim platform
        float transom = -s.Shape.SternZ;
        m.Tube(new Vector3(0, s.ThrustAt.Y, transom + 0.15f), new Vector3(0, s.ThrustAt.Y, transom - 0.12f), 0.1f, 0.075f, black, 8);
        m.Box(new Vector3(0, deck - 0.12f, transom - 0.08f), new Vector3(0.5f, 0.05f, 0.18f), black);
        Stripe(m, s, 0.6f, paint.Darkened(0.35f));
        return m.Build();
    }

    /// <summary>The rider astride the jetski, author space: one mesh, its own node (so it can be hidden).</summary>
    public static ArrayMesh JetskiRider(HumanPalette palette)
    {
        var m = new MeshScratch();
        HumanMeshBuilder.AppendRider(m, palette, JetSeat, JetGrip, JetPeg, fullFace: false);   // nobody wears a helmet on a jetski
        return m.Build();
    }

    /// <summary>The driver and two behind (#158 seats), astride, node space.</summary>
    public static SeatAnchor[] JetskiSeats() => new[]
    {
        new SeatAnchor(0, Flip(JetSeat), 0f, 0f) { Pose = SeatPose.Straddle, Grip = Flip(JetGrip), Peg = Flip(JetPeg) },
        new SeatAnchor(0, Flip(JetSeat + new Vector3(0, 0.01f, -0.4f)), 0f, 0f)
        {
            Pose = SeatPose.Straddle,
            Grip = Flip(JetSeat + new Vector3(-0.16f, 0.25f, -0.2f)),
            Peg = Flip(JetPeg + new Vector3(0, 0f, -0.42f)),
        },
        new SeatAnchor(0, Flip(JetSeat + new Vector3(0, 0.02f, -0.8f)), 0f, 0f)
        {
            Pose = SeatPose.Straddle,
            Grip = Flip(JetSeat + new Vector3(-0.16f, 0.26f, -0.6f)),
            Peg = Flip(JetPeg + new Vector3(0, 0f, -0.82f)),
        },
    };

    // ---- the speedboat: a 7 m mahogany runabout -------------------------------------------

    /// <summary>The cockpit: from this far along the hull to that, its floor this high over the keel.</summary>
    private const float CockpitFrom = 0.22f, CockpitTo = 0.6f, CockpitFloor = 0.42f;
    /// <summary>The seats' hips (author space): the driver's on the right, a passenger beside, a bench of three aft.</summary>
    private static readonly Vector3 HelmHip = new(-0.46f, 0.72f, 0.15f);
    private const float SeatRecline = 0.22f;
    private static readonly Color Mahogany = new(0.6f, 0.27f, 0.13f);
    private static readonly Color Teak = new(0.78f, 0.6f, 0.4f);
    private static readonly Color Cream = new(0.92f, 0.9f, 0.84f);
    private static readonly Color Chrome = new(0.82f, 0.84f, 0.86f);

    /// <summary>The cockpit's z range, author space, for a hull of these lines.</summary>
    private static (float From, float To) CockpitZ(BoatSpec s) =>
        (-s.Shape.SternZ + CockpitFrom * s.Length, -s.Shape.SternZ + CockpitTo * s.Length);

    public static ArrayMesh RunaboutHull(BoatSpec s, Color trim)
    {
        var m = new MeshScratch();
        var bottom = new Color(0.9f, 0.9f, 0.88f);
        Hull(m, s, bottom, Mahogany, Mahogany, Teak, cockpit: (CockpitFrom, CockpitTo, CockpitFloor));
        var (c0, c1) = CockpitZ(s);

        // the foredeck's pale caulking lines, and the engine hatch aft with its own
        float fore = s.Shape.Sheer(s.Depth, 0.8f);
        for (int i = -2; i <= 2; i++)
            m.Box(new Vector3(i * 0.2f, fore + 0.035f, c1 + 1.3f), new Vector3(0.025f, 0.02f, 2.2f - Mathf.Abs(i) * 0.4f), Cream);
        float aft = s.Shape.Sheer(s.Depth, 0.1f);
        m.Box(new Vector3(0, aft + 0.04f, (c0 - s.Shape.SternZ) * 0.5f), new Vector3(1.4f, 0.08f, c0 + s.Shape.SternZ - 0.3f), Mahogany.Darkened(0.12f));
        m.Box(new Vector3(0, aft + 0.085f, (c0 - s.Shape.SternZ) * 0.5f), new Vector3(0.03f, 0.02f, c0 + s.Shape.SternZ - 0.4f), Cream);
        // the rubbing strake along the sheer, and a waterline stripe in the boat's colour
        Stripe(m, s, 0.98f, Chrome);
        Stripe(m, s, 0.34f, trim);

        // the windscreen's chrome frame and dash; the glass is a pane
        float ws = c1 - 0.15f, top = s.Shape.Sheer(s.Depth, CockpitTo);
        float halfIn = s.Beam * 0.5f * BoatSpec.HalfBeam(CockpitTo) - 0.12f;
        var l0 = new Vector3(halfIn, top, ws);
        var l1 = new Vector3(halfIn - 0.1f, top + 0.36f, ws - 0.18f);
        var r0 = new Vector3(-halfIn, top, ws);
        var r1 = new Vector3(-halfIn + 0.1f, top + 0.36f, ws - 0.18f);
        m.Pane(new[] { l0, r0, r1, l1 }, new Color(0.7f, 0.8f, 0.85f, 0.35f));
        m.Tube(l0, l1, 0.018f, Chrome);
        m.Tube(r0, r1, 0.018f, Chrome);
        m.Tube(l1, r1, 0.018f, Chrome);
        m.Box(new Vector3(0, top - 0.12f, ws - 0.12f), new Vector3(halfIn * 2f, 0.2f, 0.18f), Mahogany.Darkened(0.25f), new Basis(Vector3.Right, -0.35f));
        // the wheel, at the driver's place on the right
        var hub = new Vector3(HelmHip.X, 0.98f, HelmHip.Z + 0.45f);
        m.Ring(hub, Wheel.Axis, 0.15f, 0.19f, 0.03f, Cream, 12);
        m.Tube(hub, hub + Wheel.Axis * 0.25f, 0.025f, Chrome);

        // the seats: two buckets forward, a bench aft, cream leather
        foreach (float x in new[] { HelmHip.X, -HelmHip.X })
            Seat(m, new Vector3(x, 0, HelmHip.Z), 0.5f);
        Seat(m, new Vector3(0, 0, HelmHip.Z - 1.15f), 1.75f);
        return m.Build();
    }

    /// <summary>A seat at <paramref name="at"/> (x, z), <paramref name="width"/> across: cushion and backrest on the cockpit floor.</summary>
    private static void Seat(MeshScratch m, Vector3 at, float width)
    {
        float cushion = HelmHip.Y - 0.08f;
        m.Box(new Vector3(at.X, (CockpitFloor + cushion) * 0.5f, at.Z + 0.05f), new Vector3(width, cushion - CockpitFloor, 0.5f), Cream);
        m.Box(new Vector3(at.X, cushion + 0.28f, at.Z - 0.22f), new Vector3(width, 0.6f, 0.12f), Cream, new Basis(Vector3.Right, SeatRecline));
    }

    /// <summary>The driver's wheel: hub ahead of the hip, tilted back toward the driver.</summary>
    private static class Wheel
    {
        public static readonly Vector3 Axis = new Vector3(0, 0.55f, -0.84f).Normalized();
    }

    /// <summary>The driver at the wheel, author space.</summary>
    public static ArrayMesh RunaboutDriver(HumanPalette palette)
    {
        var m = new MeshScratch();
        var hub = new Vector3(HelmHip.X, 0.98f, HelmHip.Z + 0.45f);
        float floor = CockpitFloor + 0.04f;
        var seat = new DriverSeat(HelmHip, SeatRecline, hub, Wheel.Axis, 0.17f,
            Throttle: new Vector3(HelmHip.X - 0.1f, floor, HelmHip.Z + 0.62f),
            Brake: new Vector3(HelmHip.X + 0.08f, floor, HelmHip.Z + 0.62f),
            Rest: new Vector3(HelmHip.X + 0.12f, floor, HelmHip.Z + 0.58f));
        HumanMeshBuilder.AppendDriver(m, palette, seat, 0f, 0.3f, 0f);
        return m.Build();
    }

    /// <summary>The driver, a passenger beside, three on the bench (#158 seats), node space.</summary>
    public static SeatAnchor[] RunaboutSeats()
    {
        var bench = HelmHip.Z - 1.15f;
        return new[]
        {
            new SeatAnchor(0, Flip(HelmHip), SeatRecline, CockpitFloor),
            new SeatAnchor(0, Flip(HelmHip with { X = -HelmHip.X }), SeatRecline, CockpitFloor),
            new SeatAnchor(0, Flip(new Vector3(-0.58f, HelmHip.Y, bench)), SeatRecline, CockpitFloor),
            new SeatAnchor(0, Flip(new Vector3(0f, HelmHip.Y, bench)), SeatRecline, CockpitFloor),
            new SeatAnchor(0, Flip(new Vector3(0.58f, HelmHip.Y, bench)), SeatRecline, CockpitFloor),
        };
    }

    /// <summary>A Swiss flag on a staff at the transom (its own node: "Flag", left out of the parked box).</summary>
    public static ArrayMesh Flag(BoatSpec s)
    {
        var m = new MeshScratch();
        float transom = -s.Shape.SternZ;
        var foot = new Vector3(0, s.Shape.Sheer(s.Depth, 0f), transom + 0.1f);
        var tip = foot + new Vector3(0, 1.0f, -0.25f);
        m.Tube(foot, tip, 0.015f, Chrome);
        var red = new Color(0.85f, 0.1f, 0.1f);
        var at = tip + new Vector3(0, -0.2f, -0.2f);
        m.Box(at, new Vector3(0.02f, 0.34f, 0.34f), red);
        m.Box(at, new Vector3(0.03f, 0.21f, 0.06f), Colors.White);
        m.Box(at, new Vector3(0.03f, 0.06f, 0.21f), Colors.White);
        return m.Build();
    }

    // ---- shared -------------------------------------------------------------------------------

    /// <summary>
    /// The hull, lofted through its stations transom to stem: a vee bottom, flared topsides, a deck
    /// (dropped to <paramref name="cockpit"/>'s floor between its two stations, inside walls 8 cm
    /// thick). The stations and their heights are <see cref="BoatSpec.Shape"/>'s.
    /// </summary>
    private static void Hull(MeshScratch m, BoatSpec s, Color bottom, Color sides, Color deck, Color inside,
        (float From, float To, float Floor)? cockpit)
    {
        var shape = s.Shape;
        const float wall = 0.08f;
        var ts = new List<float>();
        for (int i = 0; i <= 12; i++) ts.Add(i / 12f * 0.97f);
        ts.Add(1f);
        var sections = new List<Vector3[]>();
        Vector3[] Ring(float t, bool sunk)
        {
            float z = -shape.SternZ + t * s.Length;
            float half = t >= 1f ? 0.03f : s.Beam * 0.5f * BoatSpec.HalfBeam(t);
            float keel = shape.Keel(t);
            float chine = keel + shape.Deadrise * 0.92f;
            float sheer = shape.Sheer(s.Depth, t);
            float floor = sunk ? cockpit!.Value.Floor : sheer + 0.03f;
            float inner = Mathf.Max(0.02f, half - wall);
            return new[]
            {
                new Vector3(0, keel, z),
                new Vector3(half * 0.92f, chine, z),
                new Vector3(half, sheer, z),
                new Vector3(inner, sheer, z),
                new Vector3(inner, floor, z),
                new Vector3(-inner, floor, z),
                new Vector3(-inner, sheer, z),
                new Vector3(-half, sheer, z),
                new Vector3(-half * 0.92f, chine, z),
            };
        }
        // the stations in order, the cockpit's ends doubled at one z: deck then floor going in,
        // floor then deck coming out (the loft between two rings at one z is the bulkhead)
        var stations = new List<(float T, int Order, bool Sunk)>();
        foreach (float t in ts)
            stations.Add((t, 0, cockpit is { } c && t > c.From && t < c.To));
        if (cockpit is { } k)
        {
            stations.Add((k.From, 0, false));
            stations.Add((k.From, 1, true));
            stations.Add((k.To, 0, true));
            stations.Add((k.To, 1, false));
        }
        stations.Sort((x, y) => x.T != y.T ? x.T.CompareTo(y.T) : x.Order.CompareTo(y.Order));
        foreach (var (t, _, sunk) in stations) sections.Add(Ring(t, sunk));
        // bottom, chine-to-sheer topsides, the deck's edge, the inside walls and floor, and back
        var colours = new[] { bottom, sides, deck, inside, inside, inside, deck, sides, bottom };
        m.Loft(sections, colours, sides);
    }

    /// <summary>A thin band along both topsides at <paramref name="height"/> of the depth, standing 1 cm proud.</summary>
    private static void Stripe(MeshScratch m, BoatSpec s, float height, Color colour)
    {
        const int n = 10;
        for (int i = 0; i < n; i++)
        {
            float t0 = (i + 0.05f) / n * 0.92f, t1 = (i + 0.95f) / n * 0.92f;
            float y0 = Height(s, t0, height), y1 = Height(s, t1, height);
            float z0 = -s.Shape.SternZ + t0 * s.Length, z1 = -s.Shape.SternZ + t1 * s.Length;
            float x0 = SideAt(s, t0, y0) + 0.01f, x1 = SideAt(s, t1, y1) + 0.01f;
            foreach (float side in new[] { 1f, -1f })
            {
                var a = new Vector3(x0 * side, y0, z0);
                var b = new Vector3(x1 * side, y1, z1);
                var mid = (a + b) * 0.5f;
                var along = b - a;
                float yaw = Mathf.Atan2(along.X, along.Z);
                m.Box(mid, new Vector3(0.02f, 0.05f, along.Length()), colour, new Basis(Vector3.Up, yaw));
            }
        }
    }

    /// <summary>A height <paramref name="share"/> of the way from the chine to the sheer at t.</summary>
    private static float Height(BoatSpec s, float t, float share)
    {
        float chine = s.Shape.Keel(t) + s.Shape.Deadrise * 0.92f;
        return chine + (s.Shape.Sheer(s.Depth, t) - chine) * share;
    }

    /// <summary>The topsides' half-width at height y at t (they flare from the chine to the sheer).</summary>
    private static float SideAt(BoatSpec s, float t, float y)
    {
        float half = s.Beam * 0.5f * BoatSpec.HalfBeam(t);
        float chine = s.Shape.Keel(t) + s.Shape.Deadrise * 0.92f, sheer = s.Shape.Sheer(s.Depth, t);
        float u = Mathf.Clamp((y - chine) / Mathf.Max(0.01f, sheer - chine), 0f, 1f);
        return Mathf.Lerp(half * 0.92f, half, u);
    }

    /// <summary>Author space (+Z forward) to node space (−Z forward), as <see cref="MeshScratch.Build()"/> turns every vertex.</summary>
    public static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);
}

/// <summary>
/// A boat as drawn (#302): the hull, the driver (hidden while nobody holds the helm), a flag, and
/// its wake and spray. Built for the rider's own peer, remote copies and parked boats alike; the
/// particles only where something renders.
/// </summary>
public partial class BoatRig : Node3D
{
    private MeshInstance3D? _driver;
    private GpuParticles3D? _wake, _spray, _jet;
    private ParticleProcessMaterial? _sprayMat;
    private BoatSpec _spec = null!;

    /// <summary>Someone is at the helm (a driverless boat's helm is empty).</summary>
    public bool DriverShown
    {
        get => _driver?.Visible ?? false;
        set { if (_driver != null && _driver.Visible != value) _driver.Visible = value; }
    }

    public static BoatRig Create(BoatSpec spec, int kind, HumanPalette? driver, int hueIndex)
    {
        var rig = new BoatRig { Name = spec.Name, _spec = spec };
        var body = HumanMeshBuilder.FigureMaterial();
        float hue = (hueIndex * 0.37f + 0.55f) % 1f;
        bool jet = spec.Drive == BoatDrive.Jet;
        var hull = new MeshInstance3D
        {
            Name = "Hull",
            Mesh = jet ? BoatMeshBuilder.JetskiHull(spec, Color.FromHsv(hue, 0.6f, 0.8f))
                : BoatMeshBuilder.RunaboutHull(spec, Color.FromHsv(hue, 0.6f, 0.7f)),
        };
        rig.AddChild(hull);
        MeshScratch.Paint(hull, body, CarRig.GlassMaterial());
        if (!jet) rig.AddChild(new MeshInstance3D { Name = "Flag", Mesh = BoatMeshBuilder.Flag(spec), MaterialOverride = body });
        if (driver != null)
        {
            rig._driver = new MeshInstance3D
            {
                Name = "Driver",
                Mesh = jet ? BoatMeshBuilder.JetskiRider(driver) : BoatMeshBuilder.RunaboutDriver(driver),
                MaterialOverride = body,
            };
            rig.AddChild(rig._driver);
        }
        if (DisplayServer.GetName() != "headless") rig.AddWater(jet);
        return rig;
    }

    // ---- wake and spray ----------------------------------------------------------------------

    private static readonly Color Foam = new(0.93f, 0.96f, 1f);

    private void AddWater(bool jet)
    {
        float transom = _spec.Shape.SternZ;   // node space: +Z is aft
        // the wake: foam left on the water behind the transom, spreading and fading
        _wake = Emitter("Wake", 320, 4f, 0.8f, flat: true, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(_spec.Beam * 0.3f, 0.02f, 0.3f),
            // thrown aft and out to both sides: the wake's vee
            Direction = new Vector3(0, 0, 1),
            Spread = 25f,
            InitialVelocityMin = 0.8f,
            InitialVelocityMax = 2.2f,
            Gravity = Vector3.Zero,
            DampingMin = 0.5f,
            DampingMax = 1f,
            ScaleMin = 0.8f,
            ScaleMax = 1.6f,
            ScaleCurve = Grow(),
            Color = Foam,
            ColorRamp = Fade(0.7f),
        });
        _wake.Position = _wakeAnchor = new Vector3(0, 0.08f, transom + 0.2f);
        // spray off the chines when it planes or slams: thrown out and up both sides
        _spray = Emitter("Spray", 120, 0.7f, 0.09f, flat: false, _sprayMat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(_spec.Beam * 0.5f, 0.05f, _spec.Length * 0.12f),
            Direction = new Vector3(0, 0.7f, 0.4f),
            Spread = 60f,
            InitialVelocityMin = 2f,
            InitialVelocityMax = 5f,
            Gravity = new Vector3(0, -9.8f, 0),
            ScaleMin = 0.6f,
            ScaleMax = 1.4f,
            Color = Foam,
            ColorRamp = Fade(0.9f),
        });
        _spray.Position = _sprayAnchor = new Vector3(0, 0.15f, -_spec.Length * 0.12f);
        if (jet)
        {
            // the jet's rooster tail out of the nozzle
            _jet = Emitter("Jet", 100, 0.9f, 0.11f, flat: false, new ParticleProcessMaterial
            {
                Direction = new Vector3(0, 0.45f, 1f),
                Spread = 8f,
                InitialVelocityMin = 5f,
                InitialVelocityMax = 8f,
                Gravity = new Vector3(0, -9.8f, 0),
                ScaleMin = 0.8f,
                ScaleMax = 1.6f,
                Color = Foam,
                ColorRamp = Fade(0.9f),
            });
            _jet.Position = new Vector3(0, _spec.ThrustAt.Y + 0.05f, _spec.ThrustAt.Z + 0.15f);
        }
    }

    /// <summary>An emitter of white specks (<paramref name="flat"/>: foam patches lying on the water instead of facing the camera).</summary>
    private GpuParticles3D Emitter(string name, int amount, float life, float size, bool flat, ParticleProcessMaterial mat)
    {
        var p = new GpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = life,
            Emitting = false,
            ProcessMaterial = mat,
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(size, size),
                Orientation = flat ? PlaneMesh.OrientationEnum.Y : PlaneMesh.OrientationEnum.Z,
                Material = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    BillboardMode = flat ? BaseMaterial3D.BillboardModeEnum.Disabled : BaseMaterial3D.BillboardModeEnum.Particles,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    VertexColorUseAsAlbedo = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
            },
            // left on the water where they fell, not dragged along with the boat
            LocalCoords = false,
            VisibilityAabb = new Aabb(new Vector3(-20, -6, -20), new Vector3(40, 12, 40)),
        };
        AddChild(p);
        return p;
    }

    private static CurveTexture Grow()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0.4f));
        c.AddPoint(new Vector2(1, 1.6f));
        return new CurveTexture { Curve = c };
    }

    private static GradientTexture1D Fade(float alpha)
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, alpha));
        g.SetColor(1, new Color(1, 1, 1, 0));
        return new GradientTexture1D { Gradient = g };
    }

    private float _shownWake = -1f, _shownSpray = -1f, _shownJet = -1f;

    /// <summary>
    /// Per frame: the wake by speed through the water while the hull is in it, spray by how hard it
    /// planes (and a moment after a slam), the jet's tail by thrust. Only changes are written.
    /// </summary>
    public void Water(float speed, float wet, float thrust01, bool afloat)
    {
        if (_wake == null) return;
        float wake = afloat && wet > 0.05f ? Mathf.Clamp((speed - 1f) / 12f, 0f, 1f) : 0f;
        float spray = afloat && wet > 0.05f ? Mathf.Clamp((speed - 4f) / 14f, 0f, 1f) : 0f;
        float jet = afloat ? Mathf.Clamp(thrust01, 0f, 1f) * Mathf.Clamp(speed / 6f, 0.3f, 1f) : 0f;
        Set(_wake, wake, ref _shownWake);
        Set(_spray, spray, ref _shownSpray);
        // foam lies on the water, not on the keel under it: the emitters ride the surface
        if (wake > 0f) OnSurface(_wake, _wakeAnchor);
        if (spray > 0f) OnSurface(_spray, _sprayAnchor);
        if (_jet != null) Set(_jet, jet, ref _shownJet);
    }

    private Vector3 _wakeAnchor, _sprayAnchor;

    /// <summary>Puts an emitter at its anchor on the hull (rig space), lifted or lowered to the surface there.</summary>
    private void OnSurface(GpuParticles3D p, Vector3 anchor)
    {
        var at = GlobalTransform * anchor;
        if (World.WaterField.TryLevelAt(at, out float level)) p.GlobalPosition = at with { Y = level + 0.03f };
    }

    private static void Set(GpuParticles3D p, float amount, ref float shown)
    {
        float q = Mathf.Round(amount * 10f) / 10f;
        if (q == shown) return;
        shown = q;
        p.Emitting = q > 0f;
        if (q > 0f) p.AmountRatio = q;
    }
}
