using Godot;
using UnitSport.Audio;
using UnitSport.Farming;

namespace UnitSport.Player;

/// <summary>What a heavy vehicle is: which trailers it takes, how it is drawn, how its box shifts.</summary>
public enum HeavyClass
{
    Tractor, Rigid, CityBus, ArticulatedBus, Coach, Pickup,
    /// <summary>A farm tractor (#494): a drawbar and a three-point linkage, big rear wheels, a cab with doors.</summary>
    FarmTractor,
    /// <summary>A combine harvester (#494): a header lowered to cut, a grain tank, steered at the rear.</summary>
    Combine,
}

/// <summary>
/// How the automatic changes gear: an automated manual (a dry clutch the computer works, drive cut
/// for half a second each shift: every truck and the coach) or a torque converter that powershifts
/// under load (the city buses).
/// </summary>
public enum Transmission { Amt, TorqueConverter }

/// <summary>What hangs one section of a train on the one ahead of it.</summary>
public enum Coupling
{
    None,
    /// <summary>A semi-trailer's kingpin in a tractor's fifth wheel: carries the trailer's front.</summary>
    FifthWheel,
    /// <summary>A drawbar eye in the truck's hitch: pulls, carries nothing.</summary>
    Drawbar,
    /// <summary>A drawbar trailer's body on its dolly's turntable: carries the body's front.</summary>
    Turntable,
    /// <summary>An articulated bus's joint (Hübner turntable): carries the rear section's front, damped.</summary>
    BusJoint,
    /// <summary>
    /// A centre-axle trailer's coupler on a 50 mm tow ball (#463): carries the trailer's nose weight.
    /// A pickup's ball, or the ball of a rigid truck's combination coupling.
    /// </summary>
    Ball,
    /// <summary>
    /// A mounted implement on a tractor's three-point linkage (#494): rigid, no articulation, no
    /// wheels of its own; raised, its whole weight on the tractor's rear axle.
    /// </summary>
    ThreePoint,
}

/// <summary>
/// How the player shifts a truck (Settings → Movement, <c>--gearbox</c>). A city bus's converter
/// automatic takes <see cref="Sequential"/> for anything manual: it has no clutch pedal and no gates.
/// </summary>
public enum HeavyShift
{
    /// <summary>The box chooses (Opticruise, TipMatic, EcoLife).</summary>
    Automatic,
    /// <summary>Up and down by hand; the automated clutch launches and never stalls.</summary>
    Sequential,
    /// <summary>Up and down by hand with the clutch pedal down, or the box refuses and grinds.</summary>
    SequentialClutch,
    /// <summary>Six gates on the number keys, splitter on the shift keys, clutch for both.</summary>
    HPatternSplitter,
    /// <summary>Six gates with the clutch; the splitter changes by itself inside each gate.</summary>
    HPattern,
}

/// <summary>
/// One axle. Positions are metres behind the front of its section, as a data sheet gives them
/// (front overhang, then wheelbases).
/// </summary>
/// <param name="At">Metres behind the front of the section.</param>
/// <param name="Group">
/// 0 front, 1 rear: the two supports the section's weight is shared between. Axles in one group
/// share its load equally, as a compensated tandem or tridem does.
/// </param>
/// <param name="Steer">
/// Share of the front road-wheel angle this axle steers: 1 a front axle, 0 fixed, negative a
/// steered tag axle turning against the front one to point at the turn centre.
/// </param>
/// <param name="Driven">The engine turns it.</param>
/// <param name="Twin">Twin tyres each side (drive axles, a bus's rear).</param>
/// <param name="Tyre">Size as on the sidewall: sets the rolling radius and the drawn wheel.</param>
public sealed record AxleSpec(float At, int Group, float Steer = 0f, bool Driven = false, bool Twin = false, string Tyre = "315/70R22.5");

/// <summary>
/// One rigid body of a train: a tractor, a rigid truck, a bus or its rear half, a semi-trailer, a
/// drawbar trailer's dolly or its body. The physics (<see cref="HeavyTrain"/>) and the mesh
/// (<see cref="Avatar.HeavyRig"/>) both read this.
/// </summary>
public sealed record SectionSpec
{
    public required string Name { get; init; }
    /// <summary>Body length, width and height, m (the height is the drawn top: cab, box, tank).</summary>
    public float Length { get; init; }
    public float Width { get; init; } = 2.55f;
    public float Height { get; init; } = 3.9f;

    /// <summary>Mass empty (with the driver on a driven section), kg.</summary>
    public float Mass { get; init; }
    /// <summary>Centre of mass empty: metres behind the front, and height, m.</summary>
    public float CgAt { get; init; }
    public float CgHeight { get; init; } = 1.1f;

    public AxleSpec[] Axles { get; init; } = System.Array.Empty<AxleSpec>();

    /// <summary>Where this section hangs on the one ahead (kingpin, drawbar eye, joint): metres behind its front. NaN for the first section.</summary>
    public float PivotAt { get; init; } = float.NaN;
    /// <summary>How it hangs there. <see cref="Coupling.None"/> for the first section.</summary>
    public Coupling Pivot { get; init; }
    /// <summary>
    /// Height of the pivot above the ground with the section standing level, m; NaN: the hitch's it
    /// hangs on (a kingpin is built for a fifth wheel's plate). A ball trailer's coupler is built
    /// for a car's ball: on a truck's higher one it rides nose up.
    /// </summary>
    public float PivotHeight { get; init; } = float.NaN;
    /// <summary>Where the next section hangs on this one (fifth wheel, hitch, joint, turntable): metres behind the front; NaN when nothing can.</summary>
    public float HitchAt { get; init; } = float.NaN;
    /// <summary>Height of that point above the ground, m (a fifth wheel's plate, a hitch's jaw).</summary>
    public float HitchHeight { get; init; } = 1.1f;
    /// <summary>What can hang there.</summary>
    public Coupling Hitch { get; init; }

    /// <summary>How far this section may swing on its pivot before it hits the one ahead, rad.</summary>
    public float MaxArticulation { get; init; } = 1.4f;
    /// <summary>Damping on the pivot, N·m·s/rad: a pusher bus's joint control (Hübner), zero on a fifth wheel.</summary>
    public float JointDamping { get; init; }

    /// <summary>Drag area this section adds, Cd·A m² (the first section's frontal, a trailer's gap and skin).</summary>
    public float DragArea { get; init; }

    /// <summary>The most it may carry, kg, and where that load sits: metres behind the front, and height, m.</summary>
    public float PayloadMax { get; init; }
    public float PayloadAt { get; init; }
    public float PayloadHeight { get; init; } = 1.6f;
    /// <summary>The load is a liquid that sloshes (a tanker): its free surface is a mass on a spring.</summary>
    public bool Liquid { get; init; }

    /// <summary>Track width at the wheels, m: what keeps it on its wheels in a bend (rollover threshold).</summary>
    public float Track { get; init; } = 2.04f;
}

/// <summary>What a heavy vehicle looks like: an operator's colours on its body style. No logos.</summary>
public sealed record HeavyLook
{
    /// <summary>Main body colour, sRGB.</summary>
    public Color Paint { get; init; } = new(0.94f, 0.94f, 0.92f);
    /// <summary>A second colour: a band, a skirt, a stripe.</summary>
    public Color Accent { get; init; } = new(0.2f, 0.2f, 0.22f);
    /// <summary>The lower body under the belt (a bus skirt, a truck's bumper and steps).</summary>
    public Color Lower { get; init; } = new(0.2f, 0.2f, 0.22f);
    /// <summary>The cargo body's colour on a rigid truck.</summary>
    public Color Cargo { get; init; } = new(0.94f, 0.94f, 0.92f);
    /// <summary>Whose colours these are, for the picker's blurb; no name is painted on.</summary>
    public string Operator { get; init; } = "";
    /// <summary>Passenger doors per side, as metres behind the front and width, m (buses): drawn on the right.</summary>
    public (float At, float Width)[] Doors { get; init; } = System.Array.Empty<(float, float)>();
    /// <summary>What the destination display cycles through (buses): {destination} changes it.</summary>
    public string[] Destinations { get; init; } = System.Array.Empty<string>();
}

/// <summary>
/// One heavy vehicle as numbers: the real machine's published figures for the Sim profile, like the
/// cars. The roster is <see cref="HeavyCatalog"/>.
/// </summary>
public sealed record HeavySpec
{
    /// <summary>Assigned by <see cref="HeavyCatalog"/> from the entry's position; never set by hand.</summary>
    public RideKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Blurb { get; init; }
    public HeavyClass Class { get; init; }
    public HeavyLook Look { get; init; } = new();
    public EngineLayout Engine { get; init; } = EngineLayout.Diesel6;

    /// <summary>The sections it is built of: one, or two for an articulated bus. A trailer is not in here.</summary>
    public required SectionSpec[] Sections { get; init; }

    // ---- engine ----
    public float PeakKw { get; init; }
    public float PeakRpm { get; init; }
    public float IdleRpm { get; init; } = 600f;
    /// <summary>Where the governor cuts fuel, rpm.</summary>
    public float Redline { get; init; } = 2200f;
    /// <summary>Crank torque curve, (rpm, N·m) ascending.</summary>
    public (float Rpm, float Nm)[] Torque { get; init; } = System.Array.Empty<(float, float)>();
    /// <summary>Exhaust / compression brake at the crank near the governed speed, N·m.</summary>
    public float EngineBrakeNm { get; init; }
    /// <summary>Flywheel, clutch and crank, kg·m²: a truck diesel's is ten times a car engine's.</summary>
    public float EngineInertia { get; init; } = 3.5f;
    /// <summary>Where a torque converter holds the engine at full throttle against the brakes, rpm.</summary>
    public float StallRpm { get; init; } = 1900f;
    /// <summary>Air brakes (chamber lag, a tank, spring brakes); false: hydraulic, as on a pickup.</summary>
    public bool AirBrakes { get; init; } = true;
    /// <summary>Hydrodynamic retarder at the prop shaft, N·m and kW (it fades at low speed).</summary>
    public float RetarderNm { get; init; }
    public float RetarderKw { get; init; }

    // ---- gearbox ----
    public Transmission Box { get; init; } = Transmission.Amt;
    /// <summary>Forward ratios, first to top.</summary>
    public float[] Gears { get; init; } = System.Array.Empty<float>();
    public float Reverse { get; init; } = 11f;
    public float FinalDrive { get; init; }
    /// <summary>Seconds of an automated shift (clutch out, gear, clutch in): no drive for most of it.</summary>
    public float ShiftTime { get; init; } = 0.55f;
    /// <summary>The box is a range-splitter with six gates (twelve gears): the H-pattern takes it.</summary>
    public bool SixGates => Gears.Length == 12 && !Stepless;
    /// <summary>
    /// A stepless box (a tractor's CVT, #494), modelled as many close ratios the automatic walks
    /// through without a pause: the player has only the automatic, no clutch, no gates.
    /// </summary>
    public bool Stepless { get; init; }

    // ---- chassis ----
    /// <summary>Road-wheel lock at full steer, rad.</summary>
    public float MaxSteer { get; init; } = 0.7f;
    /// <summary>Peak tyre friction on dry tarmac: truck tyres grip less than a car's.</summary>
    public float Grip { get; init; } = 0.8f;
    /// <summary>Full service braking with the design load, m/s² (EBS, ABS).</summary>
    public float BrakeDecel { get; init; } = 6.5f;
    /// <summary>Speed limiter, km/h: fuel is cut there (downhill it runs on).</summary>
    public float LimiterKmh { get; init; } = 89f;
    /// <summary>Seats and standing places (buses): a passenger with luggage is 75 kg.</summary>
    public int Passengers { get; init; }

    // ---- farm machines (#494) ----
    /// <summary>A three-point linkage beside the hitch (a farm tractor): takes a mounted implement.</summary>
    public Coupling Mount { get; init; }
    /// <summary>What the machine itself works the ground with (a combine's header: Harvest), None for none.</summary>
    public FarmTool Tool { get; init; }
    /// <summary>Its working width, m (a combine's cut), and the bar's place, metres behind the front.</summary>
    public float WorkWidth { get; init; }
    public float WorkAt { get; init; }
    /// <summary>A grain tank's capacity in items (sacks), 0 for none.</summary>
    public int TankItems { get; init; }
    /// <summary>The top speed while working (a combine threshing), km/h.</summary>
    public float WorkKmh { get; init; } = float.PositiveInfinity;
    /// <summary>A tractor or a combine: its lowering, tank and work keys.</summary>
    public bool Farm => Class is HeavyClass.FarmTractor or HeavyClass.Combine;

    /// <summary>What can hang on the back: the last section's hitch.</summary>
    public Coupling Takes => Sections[^1].Hitch;

    /// <summary>
    /// Whether a trailer hangs on this vehicle: what its hitch takes, and a ball trailer on a rigid
    /// truck's drawbar jaw too — a Swiss distribution truck's combination coupling carries a 50 mm
    /// ball under the jaw (#463).
    /// </summary>
    public bool Accepts(TrailerSpec t) => Takes != Coupling.None
        && (t.Couples == Takes || t.Couples == Coupling.Ball && Takes == Coupling.Drawbar || Mount != Coupling.None && t.Couples == Mount);

    /// <summary>Crank torque at an rpm, N·m, from the published curve.</summary>
    public float TorqueAt(float rpm)
    {
        if (Torque.Length == 0) return PeakKw * 1000f / (PeakRpm * Mathf.Tau / 60f);
        if (rpm <= Torque[0].Rpm) return Torque[0].Nm * Mathf.Clamp(rpm / Torque[0].Rpm, 0f, 1f);
        for (int i = 1; i < Torque.Length; i++)
            if (rpm <= Torque[i].Rpm)
            {
                var (r0, n0) = Torque[i - 1];
                var (r1, n1) = Torque[i];
                return Mathf.Lerp(n0, n1, (rpm - r0) / Mathf.Max(1f, r1 - r0));
            }
        return rpm > Redline ? 0f : Torque[^1].Nm;
    }

    /// <summary>The peak of the curve, N·m.</summary>
    public float PeakTorque => Torque.Length == 0 ? TorqueAt(PeakRpm) : Torque.Max(t => t.Nm);
}

/// <summary>What a trailer's body is: sets the mesh and how the load sits.</summary>
public enum TrailerBody
{
    Curtainsider, Tanker, Timber, SwapBody, Boat,
    /// <summary>Farm implements (#494): a mounted reversible plough, seed drill and mower; a tipping trailer.</summary>
    Plough, SeedDrill, Mower, Tipper,
}

/// <summary>A trailer as numbers: its sections (one for a semi, dolly and body for a drawbar trailer) and its look.</summary>
public sealed record TrailerSpec
{
    public required string Label { get; init; }
    public required string Blurb { get; init; }
    public TrailerBody Body { get; init; }
    /// <summary>What it hangs on: a fifth wheel (semis) or a hitch (drawbar trailers).</summary>
    public Coupling Couples { get; init; }
    public required SectionSpec[] Sections { get; init; }
    public Color Paint { get; init; } = new(0.94f, 0.94f, 0.92f);
    public Color Accent { get; init; } = new(0.2f, 0.2f, 0.22f);
    public Color Frame { get; init; } = new(0.12f, 0.12f, 0.13f);
    public string Operator { get; init; } = "";

    /// <summary>
    /// A boat trailer's boat (#463), 0 for none: its load is the boat, aboard (load 1) or launched
    /// (load 0), and launched it is a boat of this kind in the water.
    /// </summary>
    public RideKind Boat { get; init; }
    /// <summary>Where the boat's origin (its keel under its centre of mass) sits: metres behind the trailer's front, and height, m.</summary>
    public float BoatAt { get; init; }
    public float BoatKeel { get; init; }
    /// <summary>The boat's bow (the winch post), metres behind the trailer's front.</summary>
    public float BowAt => BoatCatalog.For((int)Boat) is { } b ? BoatAt - (b.Length - b.Shape.SternZ) : 0f;

    // ---- farm implements (#494) ----
    /// <summary>What it works the ground with when lowered (None: it never works it, a trailer).</summary>
    public FarmTool Tool { get; init; }
    /// <summary>Its working width, m, and the working bar's place, metres behind its front.</summary>
    public float WorkWidth { get; init; }
    public float WorkAt { get; init; }
    /// <summary>How high the linkage lifts it off the ground for the road, m.</summary>
    public float LiftHeight { get; init; }
    /// <summary>A tipping trailer's body, in items (sacks): 0 for none.</summary>
    public int TankItems { get; init; }
    /// <summary>A plough's working depth, cm (its draft grows with it).</summary>
    public float DepthCm { get; init; } = 25f;
    /// <summary>A mounted implement: rigid on the three-point linkage, raised and lowered with {kneel}.</summary>
    public bool Mounted => Couples == Coupling.ThreePoint;
}
