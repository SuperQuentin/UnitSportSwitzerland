using Godot;

namespace UnitSport.Player;

/// <summary>
/// The boats (#302), as <see cref="BoatSpec"/> numbers: <c>RideKind</c> 121 the jetski, 122 the
/// speedboat, 123 the paddle steamer (#303). Append-only like every catalog (the kind is replicated as
/// an int). Plain C# (tier-0 tested). What is published is used; what is assumed says so.
/// </summary>
public static class BoatCatalog
{
    public const int First = 121;

    private static readonly HullShape JetskiLines = new(SternZ: 1.55f, Deadrise: 0.18f, BowRise: 0.28f, SheerRise: 0.15f, Stations: 5, Across: 3);
    private static readonly HullShape RunaboutLines = new(SternZ: 2.95f, Deadrise: 0.28f, BowRise: 0.45f, SheerRise: 0.15f, Stations: 7, Across: 3);

    /// <summary>
    /// A three-seat sit-down personal watercraft, after a Sea-Doo GTI 170 / Yamaha VX class machine:
    /// 3.45 m, 1.25 m beam, ~350 kg dry, a 1.6 L triple of ~125 kW (170 hp) on a jet pump, about
    /// 80 km/h flat out. Assumed: the static thrust (3.6 kN), the centre of mass with a rider aboard
    /// (0.45 m over the keel, 45 % of the length from the stern), the vee (18 cm deadrise).
    /// </summary>
    public static readonly BoatSpec Jetski = new()
    {
        Name = "Jetski",
        Length = 3.45f, Beam = 1.22f, Depth = 0.55f,   // to the footwells: the saddle and hood stand on that
        Mass = 480f,                 // 350 dry + fuel + one rider
        CentreHeight = 0.45f,
        Shape = JetskiLines,
        Columns = BoatSpec.PlaningHull(3.45f, 1.22f, 0.55f, JetskiLines),
        Drive = BoatDrive.Jet,
        PowerKw = 125f, Efficiency = 0.5f, StaticThrust = 3600f, ReverseShare = 0.3f,
        ThrustAt = new Vector3(0, 0.12f, 1.5f),
        MaxSteer = 0.5f, SteerRate = 4f,
        HumpSpeed = 3.5f, PlaneSpeed = 7f, LiftShare = 0.75f, HumpDrag = 0.16f, HumpTrim = 0.09f, PlaneTrim = 0.05f, SkegArea = 0.004f, FaceKick = 1.2f,
        TopSpeed = 22.5f,            // ~81 km/h
        LateralDrag = 1.0f, HeaveDamping = 0.8f, SlamDrag = 0.6f,
        BankPerG = 0.9f, MaxBank = 0.6f,
        WindArea = 1.2f,
        IdleRpm = 1600f, MaxRpm = 8000f, SpoolRate = 3.5f,
        ThrowsRider = true, ThrowLanding = 8f, ThrowTilt = 0.9f, FlipAngle = 1.6f,
    };

    /// <summary>
    /// A 7 m Swiss lake runabout in the manner of a Boesch: a deep-vee planing hull, a V8 inboard of
    /// ~260 kW (350 hp) on a shaft and a spade rudder, about 70 km/h (38 kn). The driver and up to
    /// five aboard. Assumed: the mass ready to go (1.9 t with one aboard), the static thrust (10 kN),
    /// the centre of mass (0.6 m up, 42 % from the transom), the 0.12 m² rudder.
    /// </summary>
    public static readonly BoatSpec Speedboat = new()
    {
        Name = "Speedboat",
        Length = 7.0f, Beam = 2.35f, Depth = 1.05f,
        Mass = 1900f,
        CentreHeight = 0.6f,
        Shape = RunaboutLines,
        Columns = BoatSpec.PlaningHull(7.0f, 2.35f, 1.05f, RunaboutLines),
        Drive = BoatDrive.Propeller,
        PowerKw = 260f, Efficiency = 0.55f, StaticThrust = 10000f, ReverseShare = 0.4f,
        ThrustAt = new Vector3(0, -0.25f, 1.9f),
        DiscArea = 0.11f, RudderArea = 0.12f,
        MaxSteer = 0.55f, SteerRate = 1.6f,
        HumpSpeed = 5f, PlaneSpeed = 9.5f, LiftShare = 0.7f, HumpDrag = 0.2f, HumpTrim = 0.1f, PlaneTrim = 0.06f, SkegArea = 0.01f, FaceKick = 0.6f,
        TopSpeed = 19.5f,            // ~70 km/h, 38 kn
        LateralDrag = 1.6f, HeaveDamping = 0.8f, SlamDrag = 0.6f,
        BankPerG = 0.5f, MaxBank = 0.3f,
        WindArea = 4f,
        IdleRpm = 650f, MaxRpm = 5200f, SpoolRate = 2f,
    };

    /// <summary>
    /// A CGN Belle Époque side-wheel paddle steamer of the <i>La Suisse</i> / <i>Montreux</i> class
    /// (#303), after <i>La Suisse</i> (Sulzer, 1910): 78 m over all, 73.8 m on the main deck, a
    /// hull 8.5 m wide and 15.9 m over the paddle boxes, 1.68 m of draught, ~518 t displaced, a
    /// two-cylinder compound diagonal engine of 1 400 hp (1 030 kW), 29.1 km/h on trials, ~26 in
    /// service. Sources: de.wikipedia "La Suisse (Schiff, 1910)" (dimensions, draught, power);
    /// swissitalianpaddlesteamers.com fleet list (518 t, 29.1 km/h); cgn.ch (78.5 m, 1 400 hp).
    /// Assumed: the hull's lines (<see cref="SteamerLines"/>), the centre of mass (2.4 m over the
    /// keel: the engine and boilers are low, the saloons and the upper deck high), the static thrust
    /// of the wheels (95 kN), the reverse share (0.65), the engine's pace through the telegraph
    /// (stop to full in ~12 s), a 4.5 m² rudder at the stern, the shaft's 46 rpm at full.
    /// </summary>
    public static readonly BoatSpec Steamer = new()
    {
        Name = "Paddle steamer",
        Length = SteamerLines.Length, Beam = SteamerLines.Beam, Depth = SteamerLines.Depth,
        Mass = 518_000f,
        CentreHeight = 2.4f,
        Columns = SteamerLines.Columns(),
        Drive = BoatDrive.Paddle,
        PowerKw = 1030f, Efficiency = 0.5f, StaticThrust = 95_000f, ReverseShare = 0.65f,
        // the floats of the starboard wheel (hull frame: +X starboard, −Z the bow), a little forward
        // of the centre of mass, their middle 0.7 m under the waterline
        ThrustAt = new Vector3(SteamerLines.WheelX, SteamerLines.Draught - 0.7f, -SteamerLines.WheelZ),
        RudderAt = new Vector3(0, 1.0f, SteamerLines.WaterlineHalf - 0.6f),
        RudderArea = 7f, SkegArea = 1.5f,
        MaxSteer = 0.6f, SteerRate = 0.18f,
        // a displacement hull: no hump within reach (its hull speed is ~11 m/s), no lift, no plane
        HumpSpeed = 12f, HumpDrag = 0.004f, LiftShare = 0f, FaceKick = 0f,
        TopSpeed = 8.1f,             // 29 km/h on trials
        LinearDrag = 0.004f,
        LateralDrag = 1.1f, HeaveDamping = 0.8f, SlamDrag = 0.6f,
        // it heels a little out of a turn, it does not lean into one
        BankPerG = 0f, MaxBank = 0f,
        WindArea = 60f,
        IdleRpm = 0f, MaxRpm = 46f, SpoolRate = 0.08f,
    };

    /// <summary>By <c>RideKind</c> − <see cref="First"/>.</summary>
    public static readonly BoatSpec[] All = { Jetski, Speedboat, Steamer };

    public static BoatSpec? For(int kind) => kind >= First && kind < First + All.Length ? All[kind - First] : null;
}

/// <summary>
/// The paddle steamer's hull lines (#303), shared by the columns it floats on and the hull it is
/// drawn as (<c>Avatar.SteamerMeshBuilder</c>), so the drawn waterline is the physical one. Hull
/// frame: the origin on the keel under the centre of mass, +Z aft (authored meshes flip it).
/// The underwater body runs <see cref="WaterlineHalf"/> either side of the centre of mass: a long
/// parallel middle, a fine bow and a rounded stern; the bottom is flat with a turn of the bilge (the
/// outer columns stand on it). Assumed, sized to <i>La Suisse</i>'s published 8.5 m beam and 1.68 m
/// draught at 518 t.
/// </summary>
public static class SteamerLines
{
    /// <summary>Main deck length, hull beam, keel to main deck amidships, m.</summary>
    public const float Length = 76f, Beam = 8.5f, Depth = 3.0f;
    /// <summary>The design waterline's draught, m (La Suisse: 1.68 max).</summary>
    public const float Draught = 1.68f;
    /// <summary>The underwater body's half length, m (the stem and the counter overhang it at the deck).</summary>
    public const float WaterlineHalf = 35f;
    /// <summary>The main deck's ends, hull frame z (+ aft): the counter stern, the raked stem.</summary>
    public const float SternZ = 38f, StemZ = -38f;
    /// <summary>The paddle wheels' axle: out from the centreline, forward of the centre of mass, its height; their radius.</summary>
    public const float WheelX = 5.9f, WheelZ = 1.5f, WheelY = 3.15f, WheelRadius = 2.15f;
    /// <summary>The turn of the bilge: the outer columns' bottom over the keel, m.</summary>
    public const float Bilge = 1.15f;
    public const int Stations = 14, Across = 4;

    /// <summary>The half-beam at the waterline as a share of the widest, t 0 (aft end) .. 1 (forward end).</summary>
    public static float HalfBeam(float t)
    {
        if (t >= 0.58f) { float u = (t - 0.58f) / 0.42f; return Mathf.Sqrt(Mathf.Max(0.01f, 1f - 0.95f * u * u)); }
        if (t <= 0.34f) { float u = (0.34f - t) / 0.34f; return 1f - 0.62f * u * u; }
        return 1f;
    }

    /// <summary>The keel's rise toward the ends, m over the flat bottom.</summary>
    public static float KeelRise(float t) =>
        t > 0.86f ? 1.1f * Mathf.Pow((t - 0.86f) / 0.14f, 2f) : t < 0.08f ? 0.7f * Mathf.Pow((0.08f - t) / 0.08f, 2f) : 0f;

    /// <summary>The hull's columns: <see cref="Stations"/> fore and aft by <see cref="Across"/> athwart, to the main deck.</summary>
    public static HullColumn[] Columns()
    {
        var cols = new HullColumn[Stations * Across];
        float dz = 2f * WaterlineHalf / Stations;
        int n = 0;
        for (int k = 0; k < Stations; k++)
        {
            float t = (k + 0.5f) / Stations;            // 0 aft .. 1 forward
            float z = WaterlineHalf - (k + 0.5f) * dz;
            float half = Beam * 0.5f * HalfBeam(t);
            float keel = KeelRise(t);
            for (int j = 0; j < Across; j++)
            {
                float x = (-1f + (2f * j + 1f) / Across) * half;
                bool outer = j == 0 || j == Across - 1;
                float foot = keel + (outer ? Bilge : 0f);
                cols[n++] = new HullColumn(new Vector3(x, foot, z), 2f * half / Across, dz, Mathf.Max(0.2f, Depth - foot));
            }
        }
        return cols;
    }
}

/// <summary>
/// The engine-order telegraph of a steamer's bridge (#303): the steps the helmsman rings down to the
/// engine room, from full astern to full ahead, and the shaft speed each asks for (−1..1).
/// </summary>
public static class Telegraph
{
    public const int Max = 4;
    private static readonly string[] Names =
        { "FULL ASTERN", "HALF ASTERN", "SLOW ASTERN", "DEAD SLOW ASTERN", "STOP", "DEAD SLOW AHEAD", "SLOW AHEAD", "HALF AHEAD", "FULL AHEAD" };
    private static readonly float[] Speeds = { 0f, 0.15f, 0.35f, 0.65f, 1f };

    public static int Clamp(int order) => System.Math.Clamp(order, -Max, Max);

    /// <summary>The shaft speed an order asks for, −1 full astern .. 1 full ahead.</summary>
    public static float Lever(int order) => System.Math.Sign(order) * Speeds[System.Math.Abs(Clamp(order))];

    public static string Name(int order) => Names[Clamp(order) + Max];
}
