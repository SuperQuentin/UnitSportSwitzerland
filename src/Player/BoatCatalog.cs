using Godot;

namespace UnitSport.Player;

/// <summary>
/// The boats (#302), as <see cref="BoatSpec"/> numbers: <c>RideKind</c> 121 the jetski, 122 the
/// speedboat. Append-only like every catalog (the kind is replicated as an int): the paddle steamer
/// (#303) is 123. Plain C# (tier-0 tested). What is published is used; what is assumed says so.
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
        HumpSpeed = 3.5f, PlaneSpeed = 7f, LiftShare = 0.75f, HumpDrag = 0.16f, HumpTrim = 0.09f, PlaneTrim = 0.05f, SkegArea = 0.004f,
        TopSpeed = 22.5f,            // ~81 km/h
        LateralDrag = 1.0f, HeaveDamping = 0.8f, SlamDrag = 0.6f,
        BankPerG = 0.9f, MaxBank = 0.6f,
        WindArea = 1.2f,
        IdleRpm = 1600f, MaxRpm = 8000f, SpoolRate = 3.5f,
        ThrowsRider = true, ThrowLanding = 7.5f, ThrowTilt = 0.75f, FlipAngle = 1.6f,
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
        HumpSpeed = 5f, PlaneSpeed = 9.5f, LiftShare = 0.7f, HumpDrag = 0.2f, HumpTrim = 0.1f, PlaneTrim = 0.06f, SkegArea = 0.01f,
        TopSpeed = 19.5f,            // ~70 km/h, 38 kn
        LateralDrag = 1.6f, HeaveDamping = 0.8f, SlamDrag = 0.6f,
        BankPerG = 0.5f, MaxBank = 0.3f,
        WindArea = 4f,
        IdleRpm = 650f, MaxRpm = 5200f, SpoolRate = 2f,
    };

    /// <summary>By <c>RideKind</c> − <see cref="First"/>.</summary>
    public static readonly BoatSpec[] All = { Jetski, Speedboat };

    public static BoatSpec? For(int kind) => kind >= First && kind < First + All.Length ? All[kind - First] : null;
}
