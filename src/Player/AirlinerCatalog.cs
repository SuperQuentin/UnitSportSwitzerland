using Godot;

namespace UnitSport.Player;

/// <summary>
/// The numbers of one heavy aircraft (#414): what <see cref="AirlinerFlight"/> flies. Pure data with
/// Godot's managed maths only, so tier 0 tests it (tests/…/AirlinerTests). Masses in kg, forces in N,
/// speeds in m/s, angles in radians, lengths in metres.
/// </summary>
/// <remarks>
/// The aerodynamics are a polar per flap setting (lift at zero angle of attack, the stall's lift, the
/// drag the flaps add), a lift slope and an induced-drag factor. The values are round numbers chosen
/// so the published speeds come out (stall, rotation, approach, take-off roll), not wind-tunnel data.
/// </remarks>
public sealed class AirlinerSpec
{
    public required string Name { get; init; }
    public required int Engines { get; init; }
    public required float EmptyMass { get; init; }
    public required float MaxTakeoffMass { get; init; }
    /// <summary>What it weighs when it is spawned: crew, some passengers or freight, and fuel.</summary>
    public required float OperatingMass { get; init; }
    public required float FuelCapacity { get; init; }
    public required float WingArea { get; init; }
    public required float Span { get; init; }
    public required float Length { get; init; }

    /// <summary>Sea-level static thrust of one engine, N.</summary>
    public required float StaticThrust { get; init; }
    /// <summary>Share of thrust lost per m/s of airspeed (a turbofan ~0.0026, a propeller far more).</summary>
    public required float ThrustLapse { get; init; }
    /// <summary>Share of the forward thrust the reversers give (propellers reverse their pitch: more).</summary>
    public float ReverseShare { get; init; } = 0.35f;
    /// <summary>Spool at flight idle (N1 share); a fan turning at this gives a few percent of its thrust.</summary>
    public float IdleSpool { get; init; } = 0.22f;
    /// <summary>Seconds from idle to full spool.</summary>
    public required float SpoolTime { get; init; }

    public required float Cd0 { get; init; }
    public required float InducedK { get; init; }
    /// <summary>Lift coefficient per radian of angle of attack.</summary>
    public float LiftSlope { get; init; } = 5.2f;

    /// <summary>Flap settings, retracted first: the name on the lever, then per setting the polar.</summary>
    public required string[] FlapNames { get; init; }
    public required float[] FlapCl0 { get; init; }
    public required float[] FlapClMax { get; init; }
    public required float[] FlapCd { get; init; }
    /// <summary>Highest speed each setting may be out at (VFE), m/s; the first entry is VMO.</summary>
    public required float[] FlapLimit { get; init; }
    /// <summary>How far the flaps are down at each setting, rad (drawn only: a setting with slats alone is 0).</summary>
    public required float[] FlapAngles { get; init; }
    /// <summary>Flap settings a second the flaps travel.</summary>
    public float FlapRate { get; init; } = 0.22f;

    public float GearCd { get; init; } = 0.02f;
    /// <summary>Highest speed with the gear down or moving (VLO/VLE), m/s.</summary>
    public required float GearLimit { get; init; }
    public float GearTransit { get; init; } = 9f;

    public float SpoilerCd { get; init; } = 0.06f;
    public float SpoilerCl { get; init; } = 0.45f;

    /// <summary>Most the stick asks of the pitch and the roll, rad/s.</summary>
    public required float MaxPitchRate { get; init; }
    public required float MaxRollRate { get; init; }
    /// <summary>Pitch on the ground at which the tail touches the runway.</summary>
    public required float TailStrike { get; init; }
    /// <summary>Nose gear ahead of the main gear, m.</summary>
    public required float Wheelbase { get; init; }
    /// <summary>The tiller's lock at walking pace; the rudder pedals give the nose wheel 6° at speed.</summary>
    public float MaxTiller { get; init; } = 1.31f;
    /// <summary>Flown by wire (A320 normal law): it trims itself and keeps its protections in Sim too.</summary>
    public required bool FlyByWire { get; init; }

    /// <summary>Wheel brakes' friction with anti-skid, dry runway.</summary>
    public float BrakeMu { get; init; } = 0.38f;
    /// <summary>Sink at touchdown, m/s: a hard landing above the first, the gear gives way above the second, a crash above the third.</summary>
    public float HardLanding { get; init; } = 3.0f;
    public float GearCollapse { get; init; } = 4.6f;
    public float CrashSink { get; init; } = 7.0f;

    public int FlapSettings => FlapNames.Length;
    public float Vmo => FlapLimit[0];

    /// <summary>1-g stall speed at sea level for a mass and a flap setting, m/s.</summary>
    public float StallSpeed(float mass, int flaps) =>
        Mathf.Sqrt(2f * mass * AirlinerFlight.G / (AirlinerFlight.SeaLevelDensity * WingArea * FlapClMax[flaps]));
}

/// <summary>Every heavy aircraft, append-only like the other catalogs.</summary>
public static class AirlinerCatalog
{
    private const float Kt = 0.514444f, Deg = Mathf.Pi / 180f;

    /// <summary>
    /// Airbus A320ceo with CFM56-5B (#416): 78 t MTOW, 122.6 m², 2 × 120 kN, 37.6 m long, 35.8 m
    /// over the sharklets. Flap lever 0 / 1 / 1+F / 2 / 3 / FULL, VFE 230/215/200/185/177 kt, VMO
    /// 350 kt, VLO 250 kt. Tail strike 11.5°, wheelbase 12.6 m. Measured (AirlinerTests): stall
    /// clean ~146 kt and FULL ~103 kt at 64 t, rotation ~135 kt, take-off roll ~1.1 km at TOGA.
    /// </summary>
    public static readonly AirlinerSpec A320 = new()
    {
        Name = "Airbus A320",
        Engines = 2,
        EmptyMass = 42600f,
        MaxTakeoffMass = 78000f,
        OperatingMass = 64000f,
        FuelCapacity = 19000f,
        WingArea = 122.6f,
        Span = 35.8f,
        Length = 37.57f,
        StaticThrust = 120000f,
        ThrustLapse = 0.0026f,
        SpoolTime = 6f,
        Cd0 = 0.021f,
        InducedK = 0.038f,
        LiftSlope = 5.2f,
        FlapNames = new[] { "0", "1", "1+F", "2", "3", "FULL" },
        FlapCl0 = new[] { 0.20f, 0.32f, 0.62f, 0.85f, 1.05f, 1.40f },
        FlapClMax = new[] { 1.45f, 1.75f, 2.05f, 2.30f, 2.50f, 2.85f },
        FlapCd = new[] { 0f, 0.004f, 0.016f, 0.030f, 0.045f, 0.075f },
        FlapLimit = new[] { 350 * Kt, 230 * Kt, 215 * Kt, 200 * Kt, 185 * Kt, 177 * Kt },
        FlapAngles = new[] { 0f, 0f, 10 * Deg, 15 * Deg, 20 * Deg, 40 * Deg },
        GearLimit = 250 * Kt,
        MaxPitchRate = 0.105f,
        MaxRollRate = 0.26f,
        TailStrike = 0.20f,
        Wheelbase = 12.64f,
        FlyByWire = true,
    };

    /// <summary>
    /// Antonov AN-124-100 Ruslan (#419): 405 t MTOW, 628 m², 4 × 229.5 kN D-18T, 69.1 m, 73.3 m span.
    /// Flown conventionally here (steam gauges, trim by hand in Sim).
    /// </summary>
    public static readonly AirlinerSpec An124 = new()
    {
        Name = "Antonov AN-124",
        Engines = 4,
        EmptyMass = 175000f,
        MaxTakeoffMass = 405000f,
        OperatingMass = 300000f,
        FuelCapacity = 212000f,
        WingArea = 628f,
        Span = 73.3f,
        Length = 69.1f,
        StaticThrust = 229500f,
        ThrustLapse = 0.0024f,
        SpoolTime = 8f,
        Cd0 = 0.024f,
        InducedK = 0.042f,
        LiftSlope = 5.0f,
        FlapNames = new[] { "0", "15", "25", "30", "40" },
        FlapCl0 = new[] { 0.25f, 0.60f, 0.90f, 1.15f, 1.45f },
        FlapClMax = new[] { 1.50f, 2.10f, 2.45f, 2.70f, 2.95f },
        FlapCd = new[] { 0f, 0.012f, 0.030f, 0.050f, 0.080f },
        FlapLimit = new[] { 310 * Kt, 220 * Kt, 200 * Kt, 185 * Kt, 170 * Kt },
        FlapAngles = new[] { 0f, 15 * Deg, 25 * Deg, 30 * Deg, 40 * Deg },
        FlapRate = 0.15f,
        GearCd = 0.025f,
        GearLimit = 250 * Kt,
        GearTransit = 12f,
        MaxPitchRate = 0.07f,
        MaxRollRate = 0.17f,
        TailStrike = 0.21f,
        Wheelbase = 22f,
        MaxTiller = 1.22f,
        FlyByWire = false,
    };

    /// <summary>
    /// The military cargo plane (#420, the Battle Royale model): a C-130J-sized four-turboprop
    /// lifter, 70 t, 162 m², 40.4 m span; propellers lose thrust with speed fast and reverse hard.
    /// </summary>
    public static readonly AirlinerSpec Freighter = new()
    {
        Name = "Military cargo plane",
        Engines = 4,
        EmptyMass = 34000f,
        MaxTakeoffMass = 70300f,
        OperatingMass = 55000f,
        FuelCapacity = 20000f,
        WingArea = 162.1f,
        Span = 40.4f,
        Length = 29.8f,
        StaticThrust = 60000f,
        ThrustLapse = 0.0045f,
        ReverseShare = 0.6f,
        IdleSpool = 0.3f,
        SpoolTime = 3f,
        Cd0 = 0.030f,
        InducedK = 0.040f,
        LiftSlope = 5.0f,
        FlapNames = new[] { "UP", "50%", "100%" },
        FlapCl0 = new[] { 0.25f, 0.85f, 1.35f },
        FlapClMax = new[] { 1.55f, 2.30f, 2.80f },
        FlapCd = new[] { 0f, 0.030f, 0.070f },
        FlapLimit = new[] { 290 * Kt, 180 * Kt, 145 * Kt },
        FlapAngles = new[] { 0f, 20 * Deg, 40 * Deg },
        FlapRate = 0.12f,
        GearCd = 0.025f,
        GearLimit = 168 * Kt,
        GearTransit = 15f,
        MaxPitchRate = 0.09f,
        MaxRollRate = 0.21f,
        TailStrike = 0.17f,
        Wheelbase = 9.8f,
        FlyByWire = false,
    };
}
