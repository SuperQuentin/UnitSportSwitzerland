using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// How the car is driven through a corner, as in the series: the drift cars slide the tight ones,
/// the grip cars (Nakazato's R32 and his "real racing is grip" line, the Lancers, the Type Rs, the
/// NSX, the MR2s) never do — they brake later and carry more speed on the racing line instead.
/// </summary>
public enum DriveStyle { Drift, Grip }

/// <summary>
/// What sits between the driven wheels. It decides how much of the axle's grip the engine can use
/// once the tyres are at their limit: an open diff lets the lightly loaded inside wheel spin away and
/// the car simply stops accelerating — which is why a stock car will not hold a drift on the throttle
/// the way one with a mechanical LSD will.
/// </summary>
public enum Differential { Open, Viscous, Mechanical, Torsen }

/// <summary>Which axles the engine turns.</summary>
public enum Drivetrain { Rear, All, Front }

/// <summary>
/// One car as numbers. Real-world figures for the machines each one stands for, so the Sim
/// profile drives like the car and not like a tuned guess. The roster is <see cref="CarCatalog"/>.
/// </summary>
public sealed record CarSpec
{
    /// <summary>Assigned by <see cref="CarCatalog"/> from the entry's position; never set by hand.</summary>
    public RideKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Blurb { get; init; }
    /// <summary>What it looks like: shape, size, livery.</summary>
    public required CarBody Body { get; init; }
    /// <summary>What it sounds like.</summary>
    public EngineLayout Engine { get; init; } = EngineLayout.Inline4;

    /// <summary>Kerb mass with a driver, kg.</summary>
    public float Mass { get; init; }
    /// <summary>Centre of mass to front / rear axle, m. Their sum is the wheelbase; the split is the weight distribution (rear share = FrontAxle / Wheelbase).</summary>
    public float FrontAxle { get; init; }
    public float RearAxle { get; init; }
    /// <summary>Centre of mass height, m: sets how much load a brake or a throttle moves.</summary>
    public float CgHeight { get; init; } = 0.5f;
    /// <summary>Peak tyre friction coefficient on tarmac.</summary>
    public float Grip { get; init; } = 1f;

    public float PeakKw { get; init; }
    public float PeakRpm { get; init; }
    public float IdleRpm { get; init; } = 850f;
    public float Redline { get; init; }
    /// <summary>Forward gear ratios, first to top.</summary>
    public float[] Gears { get; init; } = System.Array.Empty<float>();
    public float FinalDrive { get; init; }
    public float Reverse { get; init; } = 3.5f;
    public Drivetrain Drive { get; init; } = Drivetrain.Rear;
    /// <summary>Drift or grip through corners, for the scripted drivers.</summary>
    public DriveStyle Style { get; init; } = DriveStyle.Drift;
    /// <summary>Share of AWD torque sent to the rear axle.</summary>
    public float RearBias { get; init; } = 0.6f;

    /// <summary>Road-wheel lock at full steer, radians.</summary>
    public float MaxSteer { get; init; } = 0.6f;
    /// <summary>
    /// Steering-wheel turns from lock to lock, as road tests publish it; with <see cref="MaxSteer"/> it
    /// sets the steering ratio. 2.5 (900°) when not given.
    /// </summary>
    public float LockTurns { get; init; } = 2.5f;
    /// <summary>
    /// Steering-wheel turn over road-wheel angle, from <see cref="LockTurns"/>: half the lock-to-lock
    /// over the lock. What the cockpit's wheel turns and what a real steering wheel steers through.
    /// </summary>
    public float SteerRatio => LockTurns * Mathf.Pi / MaxSteer;
    /// <summary>Assisted steering: light to turn on the spot. Off for the unassisted racks (heavy when parked).</summary>
    public bool PowerSteering { get; init; } = true;
    /// <summary>Drag area Cd·A, m².</summary>
    public float DragArea { get; init; } = 0.65f;

    public float Wheelbase => FrontAxle + RearAxle;

    /// <summary>The dials' full scales: round numbers above the published top speed and the redline.</summary>
    public CarGauges Gauges => CarGauges.For(RefTopKmh > 0 ? RefTopKmh : 220f, Redline > 0 ? Redline : 7000f);

    // ---- the real car, as published ----
    /// <summary>Engine torque curve at the crank, (rpm, N·m) ascending. Empty: a generic curve peaking at <see cref="PeakKw"/>.</summary>
    public (float Rpm, float Nm)[] Torque { get; init; } = System.Array.Empty<(float, float)>();
    /// <summary>Tyre size as stamped on the sidewall, e.g. "185/60R14"; sets the rolling radius.</summary>
    public string Tyre { get; init; } = "";
    /// <summary>Best braking, m/s², from the published 100-0 km/h distance (v²/2d). 0: from grip.</summary>
    public float BrakeDecel { get; init; }
    public Differential Diff { get; init; } = Differential.Open;
    /// <summary>Published 0-100 km/h, s, and top speed, km/h — what <c>--driftcheck</c> checks the model against.</summary>
    public float RefZeroTo100 { get; init; }
    public float RefTopKmh { get; init; }

    // ---- the preset over the real car (CarSetup, #40); the defaults are the catalog car ----
    /// <summary>The <see cref="CarSetups"/> id this spec was built with; 0 = the catalog car.</summary>
    public int SetupId { get; init; }
    /// <summary>What the tyres grip off tarmac (on tarmac: <see cref="Grip"/>).</summary>
    public TyreType TyreType { get; init; } = TyreType.Road;
    /// <summary>Suspension travel, m: how much rough ground it swallows (<see cref="CarSetups.Harshness"/>).</summary>
    public float Travel { get; init; } = StockTravel;
    /// <summary>Spring rate against stock: body roll and pitch, and harshness off-road.</summary>
    public float Stiffness { get; init; } = 1f;
    /// <summary>Wheel diameter against the <see cref="Tyre"/> size: bigger off-road wheels.</summary>
    public float WheelScale { get; init; } = 1f;

    /// <summary>A road car's suspension travel, m.</summary>
    public const float StockTravel = 0.15f;

    /// <summary>Rolling radius from the tyre size (rim + sidewall), m; 0.3 when no tyre is given.</summary>
    public float WheelRadius
    {
        get
        {
            // 185/60R14: 185 mm wide, sidewall 60% of that, 14 in rim
            var t = Tyre.Replace(" ", "").ToUpperInvariant();
            int slash = t.IndexOf('/'), r = t.IndexOf('R');
            if (slash < 1 || r < slash
                || !float.TryParse(t[..slash], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float w)
                || !float.TryParse(t[(slash + 1)..r], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ar)
                || !float.TryParse(new string(t[(r + 1)..].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rim))
                return 0.3f * WheelScale;
            return (rim * 25.4f * 0.5f + w * ar / 100f) / 1000f * 0.97f * WheelScale;   // loaded: ~3% squat
        }
    }

    /// <summary>Crank torque at an rpm, N·m: the published curve, else a generic one peaking at the rated power.</summary>
    public float TorqueAt(float rpm)
    {
        if (Torque.Length == 0)
        {
            float peak = PeakKw * 1000f / (PeakRpm * Mathf.Tau / 60f);
            float x = Mathf.Min(rpm / Redline, 1f);
            return peak * (0.7f + 1.2f * x * (1f - x));
        }
        if (rpm <= Torque[0].Rpm) return Torque[0].Nm;
        for (int i = 1; i < Torque.Length; i++)
            if (rpm <= Torque[i].Rpm)
            {
                var (r0, n0) = Torque[i - 1];
                var (r1, n1) = Torque[i];
                return Mathf.Lerp(n0, n1, (rpm - r0) / Mathf.Max(1f, r1 - r0));
            }
        return Torque[^1].Nm;
    }

    /// <summary>Share of the driven axle's grip the engine can use at the limit.</summary>
    public float DiffFactor => Diff switch
    {
        Differential.Open => 0.72f, Differential.Viscous => 0.88f, _ => 1f,
    };
}

/// <summary>
/// A car on a planar bicycle model, built to be drifted.
///
/// <para>
/// Unlike the bike and the skis, a car does not go where it points: the tyres generate side force
/// from their <i>slip angle</i>, and once the rear ones pass the peak of their curve the force
/// falls away, the rear swings out, and the car travels at an angle to its nose —
/// <see cref="RideMotion.Slip"/>. Holding that angle is counter-steering the fronts toward the
/// direction of travel and balancing it on the throttle, which is the whole of drifting, and every
/// way into one falls out of the model rather than being scripted:
/// </para>
/// <list type="bullet">
/// <item>handbrake: locks the rears, whose side grip collapses (friction circle);</item>
/// <item>power over: drive force on the rear eats the same friction circle;</item>
/// <item>braking into a corner: load moves forward, the rear lightens and lets go (the feint).</item>
/// </list>
///
/// <para>
/// State lives in <see cref="RideMotion"/> — speed, slip and yaw rate are the whole planar state —
/// so anything outside that edits the speed (a wall, a sloppy landing, boost) applies here too.
/// Game adds grip, power and a counter-steer assist on the same equations; Sim has none of them.
/// </para>
/// </summary>
public sealed partial class Car : Rideable, IEngined
{
    /// <summary>The car as tuned: the catalog's numbers under the garage's body (<see cref="CarTuning.Apply"/>).</summary>
    public CarSpec Spec { get; }
    /// <summary>This car's garage parts. They belong to the car, not the player: a new car is Stock.</summary>
    public CarTuning Tuning { get; }
    /// <summary>The tyres on it, which replace the stock tyre curve, handbrake grip and throttle sustain.</summary>
    public TyreModel Tyres { get; }

    public Car(CarSpec spec, CarTuning tuning = default)
    {
        Spec = tuning.Apply(spec);
        Tuning = tuning;
        Tyres = tuning.Tyres;
    }

    public override RideKind Kind => Spec.Kind;
    public override string Label => Spec.Label;
    public override string Blurb => Spec.Blurb;
    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => IsKart ? 110f : 160f;
    public override float WheelLock => Core.SteeringWheel.LockOverride ?? Spec.LockTurns * Mathf.Tau;
    /// <summary>Steering-wheel turn over road-wheel angle: the spec's, unless <c>--wheellock</c> sets another lock.</summary>
    private float Ratio => Core.SteeringWheel.LockOverride is { } l ? l * 0.5f / Spec.MaxSteer : Spec.SteerRatio;
    public override Core.WheelFeel Feel => _feel;
    private Core.WheelFeel _feel;

    // the driver's own eye, in the visual's frame (faces −Z, so +X is the driver's right): the
    // seat is derived from the body (CarCabin), right-hand drive — these are Japanese-market cars
    public override Vector3 FirstPersonEye => _eye ??=
        HumanMeshBuilder.MountsForDriver(CarMeshBuilder.SeatFor(Spec.Body, Spec.Wheelbase)).Eye
        + new Vector3(0, Spec.Body.Lift - Spec.Body.Drop, 0);
    private Vector3? _eye;
    public override float EyeHeight => IsKart ? 0.8f : 1.1f;
    // Up and behind, looking down ~25° over the roof: from a level camera at roof height the car
    // itself hid the road you were about to drive onto. A kart is 1 m high: closer and lower.
    public override float ChaseDistance => IsKart ? 4.2f : 6.2f;
    public override float ChaseHeight => IsKart ? 2.4f : 4.1f;
    public override float ChasePitch => IsKart ? -0.34f : -0.44f;
    // stays above the car in a drift rather than swinging round to the side of it
    public override float ChaseFollowsTravel => 0.15f;
    public override float BaseFov => 68f;
    public override float MaxFov => 90f;
    // a kart tops out at 18 m/s: the view opens fully there, so 65 km/h at ground level feels like it
    public override float FovSpeed => IsKart ? 18f : 45f;
    // a kart is 1.4 m wide and 1.1 m high with its driver: the car's capsule would be a van's
    public override float BodyRadius => IsKart ? 0.7f : 0.85f;
    public override float BodyHeight => IsKart ? 1.2f : 1.7f;
    // its hull starts where the frame does, not at a car's 0.45 m, or only the driver would collide
    public override float HullLift => IsKart ? 0.1f : 0.45f;
    public override float DismountSpeed => 1.5f;
    // measured from this model's own mesh (Rideable.Measured): an AE86 is not an NSX. Cached per
    // model and preset (an SUV stands taller than the same car on semi-slicks), so always that
    // preset's look with stock garage parts and the doors shut: an open door is not hull
    public override (Vector3 Centre, Vector3 Size) ParkedBox => Measured((Kind, Spec.SetupId), _ =>
    {
        var body = CarCatalog.For(Kind) is { } stock ? CarSetups.For(Spec.SetupId).Apply(stock).Body : Spec.Body;
        return CarRig.Create(body, Spec.Wheelbase);
    });

    // ---- what the feel layer and the rig read ----
    /// <summary>Engine speed, rpm.</summary>
    public float Rpm { get; private set; }
    public EngineProfile Sound => _sound ??= EngineProfile.For(Spec.Engine, Spec.IdleRpm, Spec.Redline);
    private EngineProfile? _sound;
    /// <summary>idle 0 .. redline 1, for <c>EngineSynth.Set</c>.</summary>
    public float Rpm01 => Mathf.Clamp((Rpm - Spec.IdleRpm) / (Spec.Redline - Spec.IdleRpm), 0f, 1f);
    /// <summary>1-based forward gear, −1 reverse; 0 neutral, only in the driver's own box (<see cref="Gearbox"/>).</summary>
    public int Gear { get; private set; } = 1;
    /// <summary>Throttle actually applied (the pedal, or the brake pedal in reverse), 0..1.</summary>
    public float Throttle { get; private set; }
    /// <summary>How hard the tyres are sliding, 0..1: squeal and smoke.</summary>
    public float TyreSlide { get; private set; }

    // ---- wear (Settings -> Movement, off by default) ----
    /// <summary>Tyre wear per axle, 0 new .. 1 finished: the sliding work the tyre has done over its life.</summary>
    public float TyreWearFront { get; private set; }
    public float TyreWearRear { get; private set; }
    /// <summary>Brake disc temperature, °C, and pad wear 0..1.</summary>
    public float BrakeTemp { get; private set; } = 20f;
    public float PadWear { get; private set; }
    /// <summary>What is left of the braking, 0..1: fade from heat times what worn pads still grip.</summary>
    public float BrakeFactor => BrakeWearOn
        ? (1f - Mathf.Clamp((BrakeTemp - FadeFrom) / FadeSpan, 0f, 0.6f)) * (1f - 0.5f * PadWear) : 1f;

    private static bool TyreWearOn => Core.GameSettings.Current.TyreWear;
    private static bool BrakeWearOn => Core.GameSettings.Current.BrakeWear;

    /// <summary>Peak-grip left in a tyre at this wear: little lost until late in its life, 30% when finished.</summary>
    private static float WornGrip(float wear) => TyreWearOn ? 1f - 0.3f * Mathf.Pow(Mathf.Clamp(wear, 0f, 1f), 1.5f) : 1f;

    /// <summary>
    /// Sliding work one axle's tyres take over their life, J: ~5 minutes of continuous drifting
    /// (a rear axle doing ~60 kW of sliding) or an hour and a half of brisk grip driving.
    /// </summary>
    private const float TyreLife = 25e6f;
    /// <summary>Heat capacity of the four discs per kg of car, J/(K·kg) — a light car has small discs (an
    /// AE86's ~20 kg of iron against a GT-R's ~35) — their cooling rate (1/s at rest, more with airflow),
    /// the temperature where the pads start to fade and the span to full fade, and the energy a set of
    /// pads absorbs over its life.</summary>
    private const float BrakeHeatPerKg = 9f, BrakeCooling = 0.015f, FadeFrom = 450f, FadeSpan = 350f, PadLife = 250e6f;
    /// <summary>Front road-wheel angle, radians, + = left.</summary>
    public float SteerAngle { get; private set; }
    /// <summary>Accumulated wheel rotation, radians, for the rig.</summary>
    public float WheelSpin { get; private set; }
    public bool Braking { get; private set; }
    /// <summary>The brake pedal, 0..1 (in reverse, the gas pedal against the motion), and the handbrake lever: for the cockpit.</summary>
    public float BrakePedal { get; private set; }
    public bool HandbrakeOn { get; private set; }
    /// <summary>Headlights on (pop-ups raised), as the driver set them: L / D-pad right.</summary>
    public bool Headlights { get; set; }
    /// <summary>Soft top down, as the driver set it: O / D-pad left. Only ever true on a car that has one.</summary>
    public bool RoofOpen { get; set; }
    /// <summary>An open car: its top folds away.</summary>
    public bool HasSoftTop => Spec.Body.Shape == BodyShape.Roadster;
    /// <summary>Hydraulics pumping, as the driver set them: O / D-pad left on a car that has them (#464).</summary>
    public bool Bouncing { get; set; }
    public bool HasHydraulics => Spec.Body.Hydraulics;
    /// <summary>A rental go-kart (#715): no suspension, a solid rear axle, a driver who sits on the road, and a body that can trip and tip over.</summary>
    public bool IsKart => Spec.Body.Shape == BodyShape.Kart;
    /// <summary>Longitudinal and lateral acceleration, m/s² (+ forward, + left), for body pitch and roll.</summary>
    public float AccelX { get; private set; }
    public float AccelY { get; private set; }

    private float WheelRadius => Spec.WheelRadius;
    private const float Driveline = 0.85f;
    private const float AirDensity = 1.2f;
    private const float RollingResistance = 0.013f;
    /// <summary>Tyre curve: sin(C·atan(B·α)). B sets the peak slip angle (~0.15 rad), C the fall past it.</summary>
    private const float TyreB = 14f, SimTyreC = 1.45f, ArcadeTyreC = 1.3f;
    /// <summary>Game: counter-steer the fronts this fraction of the way toward the direction of travel.</summary>
    private const float ArcadeAssist = 0.45f, ArcadeYawDamp = 0.08f;
    /// <summary>Game: the drift angle beyond which the car is caught (~35°), and how hard, 1/s² and 1/s.</summary>
    private const float ArcadeMaxAngle = 0.6f, ArcadeCatch = 14f, ArcadeCatchDamp = 4f;
    /// <summary>Game: share of rear side grip the throttle takes away once the car is sideways.</summary>
    private const float ArcadeSustain = 0.3f;
    /// <summary>More for a front-driver, whose driven wheels pull it straight the moment the gas goes on.</summary>
    private const float ArcadeSustainFf = 0.55f;
    private const float ArcadePower = 1.35f, ArcadeGrip = 1.12f;
    // rear side grip left under the handbrake: per tyre, TyreModel.Handbrake (stock 0.35)
    private const int Substeps = 4;

    private float _steer;   // eased steering input, −1..1

    // ---- a kart that has tripped (#715) ----
    /// <summary>Seconds left on its side, and which side: + onto its left.</summary>
    private float _tipTime;
    private float _tipSide;
    /// <summary>How long it has been sliding sideways past what its tyres hold, s.</summary>
    private float _trip;
    /// <summary>Lying on its side: the kart scrapes to a halt and nobody steers it.</summary>
    public bool Tipped => _tipTime > 0f;
    /// <summary>Seconds a tripped kart lies there before it is stood up again, and how hard the road scrapes it to a stop, m/s².</summary>
    private const float TipDuration = 1.8f, TipScrape = 11f;
    /// <summary>
    /// A kart trips when it slides sideways, wheels square to nothing: past this angle between its
    /// nose and its travel (rad) for this long (s), at speed. Loose ground digs in sooner than tarmac.
    /// </summary>
    private const float TripTime = 0.12f, TripSpeed = 7f;
    internal static float TripAngle(Audio.Surface surface) => surface switch
    {
        Audio.Surface.Asphalt or Audio.Surface.Wood or Audio.Surface.Indoor => 0.8f,
        Audio.Surface.Gravel or Audio.Surface.Snow or Audio.Surface.Ice => 0.6f,
        Audio.Surface.Grass or Audio.Surface.Forest => 0.45f,
        _ => 0.7f,
    };
    /// <summary>
    /// Game: how much of the arcade help is left, 1 all .. 0 none. The foot brake taking the whole
    /// rear circle while the rear slides past its peak (a locked rear let go, last step) takes it away: the counter-steer assist, the yaw
    /// damping and the catch past 35° held every car short of a spin however badly it was braked
    /// (measured: 63° peak with the pedal floored and a sweeper's steer at 200 km/h). The handbrake
    /// is not the foot brake, so a drift entry keeps all of it.
    /// </summary>
    private float _help = 1f;
    private float _shiftTimer;

    /// <summary>
    /// A copy with the same gear, rack position and read-outs: <see cref="Step"/> is pure apart from
    /// this state, so stepping a clone forward is a look into the car's future — which is how a
    /// driver can try a drift before committing to it.
    /// </summary>
    public Car Clone() => (Car)MemberwiseClone();

    public override Node3D BuildVisual(int riderIndex, Avatar.Outfit outfit = default) =>
        CarRig.Create(Dressed(riderIndex), Spec.Wheelbase, Spec.Gauges, HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    /// <summary>
    /// The body as <paramref name="riderIndex"/> sees it: a rental kart wears the colour and number its
    /// rider's seed picks (<see cref="KartMeshBuilder.Dress"/>), unless the garage has painted it; every
    /// other car is the catalog's own.
    /// </summary>
    private CarBody Dressed(int riderIndex) =>
        !IsKart ? Spec.Body
        : Spec.SetupId == CarSetups.ArmyId ? KartMeshBuilder.Army(Spec.Body, riderIndex)
        : Tuning[TuneSlot.Paint] == 0 ? KartMeshBuilder.Dress(Spec.Body, riderIndex) : Spec.Body;

    public override Avatar.SeatAnchor[] Seats => SeatsOf((Kind, Spec.SetupId), () =>
    {
        var rig = CarRig.Create(Spec.Body, Spec.Wheelbase, Spec.Gauges);
        var seats = rig.Seats;
        rig.Free();
        return seats;
    });

    public override bool Driverless => true;

    /// <summary>Left in the world: the same car with nobody at the wheel.</summary>
    public override Node3D BuildParkedVisual(int riderIndex) => CarRig.Create(Dressed(riderIndex), Spec.Wheelbase, Spec.Gauges);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        var s = Spec;
        bool arcade = Arcade;
        // what the tyres find under them (the road, else the cover: RideGround.Surface): the preset's
        // tyre type per surface (CarSetup.cs) on the garage tyre's tarmac grip; off tarmac a garage
        // rally tyre claws back its share of the gap to 1, as on the motorbikes. Less what the bumps
        // take when the suspension cannot keep the wheels on the ground.
        float tarmac = s.Grip * Tyres.Grip;
        float onGround = s.TyreType.Grip(ground.Surface, tarmac);
        if (Tyres.Offroad > 0f && CarSetups.Roughness(ground.Surface) > 0f)
            onGround = Mathf.Min(tarmac, onGround + (1f - onGround) * Tyres.Offroad);
        float harsh = CarSetups.Harshness(ground.Surface, motion.Speed, s.Travel, s.Stiffness);
        float grip = onGround * CarSetups.BumpGrip(harsh) * (arcade ? ArcadeGrip : 1f);
        float roughDrag = CarSetups.RoughDrag(ground.Surface, harsh);
        float tyreC = (arcade ? ArcadeTyreC : SimTyreC) * Tyres.CurveC;

        if (IsKart && TipStep(ground, dt, ref motion)) return;

        // planar state in the body frame: u forward, w to the left
        float u = motion.Speed * Mathf.Cos(motion.Slip);
        float w = motion.Speed * Mathf.Sin(motion.Slip);
        float r = motion.YawRate;

        // gear: brake at a standstill selects reverse, throttle selects first. The driver's own box
        // (#290) has a reverse gear of its own, driven on the gas like any other
        // An automatic worked by a wheel's selector (P R N D) takes its gear from the lever instead
        bool auto = Gearbox == CarGearbox.Automatic;
        bool pedalsPick = auto && Selector == DriveSelector.None;
        StepClutch(dt);
        if (auto && !pedalsPick) SelectGear(u);
        else if (pedalsPick && Gear > 0 && u < 0.5f && input.Brake > 0.3f && input.Throttle < 0.05f) Gear = -1;
        else if (pedalsPick && Gear < 0 && u > -0.5f && input.Throttle > 0.3f) Gear = 1;
        bool reverse = Gear < 0;
        float pedal = reverse && pedalsPick ? input.Brake : input.Throttle;
        float brake = reverse && pedalsPick ? input.Throttle : input.Brake;
        // in reverse the brake pedal drives; braking then is the gas pedal against the motion
        if (reverse && pedalsPick && u > 0.5f) { brake = Mathf.Max(brake, pedal); pedal = 0f; }
        // P: the pawl holds the car once it is down to walking pace
        if (auto && Selector == DriveSelector.Park && Mathf.Abs(u) < 1.5f) brake = 1f;
        Throttle = pedal;
        Braking = brake > 0.05f;
        BrakePedal = brake;
        HandbrakeOn = input.Handbrake;

        float slipNow = MathX.WrapAngle(motion.Slip);
        float delta;
        if (!float.IsNaN(input.WheelAngle))
        {
            // A steering wheel: the rack follows the driver's hands through the steering ratio, to
            // the lock stop. None of the helpers below: the easing, the speed-scaled lock and the
            // counter-steer assist all stand in for hands a wheel already has.
            delta = Mathf.Clamp(-input.WheelAngle / Ratio, -s.MaxSteer, s.MaxSteer);
            _steer = -delta / s.MaxSteer;
        }
        else
        {
            // Keyboard steering is ±1 in a frame; a rack takes a moment to wind on, and without it
            // every tap is a flick that throws the car into a spin. Faster back to centre.
            float steerTarget = input.Steer;
            float rate = Mathf.Abs(steerTarget) < Mathf.Abs(_steer) || steerTarget * _steer < 0 ? 9f : 5f;
            _steer = Mathf.MoveToward(_steer, steerTarget, rate * dt);
            // at speed the same input asks for less lock, or 200 km/h would be twitchier than 20
            // — but never in a slide: catching a drift needs the full lock, and at 60 km/h the speed
            // scaling alone left 22° of it, which cannot catch anything
            float lockScale = Mathf.Lerp(1f / (1f + Mathf.Max(u, 0f) / 28f), 1f, Mathf.Clamp(Mathf.Abs(slipNow) / 0.3f, 0f, 1f));
            delta = -_steer * s.MaxSteer * lockScale;   // +steer is right, + angle is left
            // Game: the fronts point part of the way down the direction of travel and lean against
            // the rotation, as a driver's hands would, so a drift is held rather than spun
            if (arcade && u > 3f)
                delta = Mathf.Clamp(delta + _help * (ArcadeAssist * slipNow - ArcadeYawDamp * motion.YawRate), -s.MaxSteer, s.MaxSteer);
        }
        float rearLock = 0f;
        SteerAngle = delta;

        float powerScale = arcade ? ArcadePower : 1f;
        float slideAccum = 0f;
        float h = dt / Substeps;

        float feelFy = 0f, feelAlpha = 0f, feelU = 0f;
        for (int i = 0; i < Substeps; i++)
        {
            float m = s.Mass, a = s.FrontAxle, b = s.RearAxle, L = s.Wheelbase;

            if (!ground.OnFloor)
            {
                // airborne: only the air acts; the yaw rate carries on, gently damped
                float drag0 = 0.5f * AirDensity * s.DragArea * u * Mathf.Abs(u) / m;
                u -= drag0 * h;
                r *= 1f - 0.5f * h;
                continue;
            }

            // --- engine and gearbox ---
            float drive;
            if (Gearbox == CarGearbox.Manual) drive = ManualDrive(pedal, u, powerScale, h);
            else if (Gear == 0)
            {
                // the sequential box in neutral: the engine revs on the throttle, nothing drives
                Rpm = Mathf.MoveToward(Mathf.Max(Rpm, s.IdleRpm), Mathf.Lerp(s.IdleRpm, s.Redline * 0.95f, pedal), 15000f * h);
                drive = 0f;
            }
            else
            {
                float ratio = GearRatio(Gear);
                float wheelRpm = Mathf.Abs(u) / WheelRadius * 60f / Mathf.Tau;
                // the drive and the box's shift points take the engine as coupled to the wheels; what
                // the tach, the sound and the wheel's shake follow is the converter's slipping engine
                _coupledRpm = Mathf.Max(s.IdleRpm, wheelRpm * ratio);
                Rpm = auto ? ConverterRpm(wheelRpm * ratio, pedal, h) : _coupledRpm;
                float torque = _coupledRpm >= s.Redline ? 0f : s.TorqueAt(_coupledRpm) * powerScale;
                drive = _shiftTimer > 0 ? 0f : pedal * torque * ratio * Driveline / WheelRadius;
                // in D or R on the selector, the converter creeps the car at walking pace off the pedals
                drive = Mathf.Max(drive, Creep(u, reverse));
                if (reverse) drive = -drive;
            }

            // --- load transfer from last substep's longitudinal acceleration ---
            float nf = m * (Gravity * b - AccelX * s.CgHeight) / L;
            float nr = m * Gravity - nf;
            nf = Mathf.Max(nf, 0.1f * m * Gravity);
            nr = Mathf.Max(nr, 0.1f * m * Gravity);

            // --- longitudinal forces per axle ---
            float sign = Mathf.Sign(u);
            // the car's own brakes (published 100-0 km/h), never more than the tyres can take
            float brakeForce = brake * m * Mathf.Min(s.BrakeDecel > 0 ? s.BrakeDecel * (arcade ? 1.1f : 1f) : 99f, grip * Gravity * 0.95f)
                * BrakeFactor;
            if (BrakeWearOn)
            {
                // the discs take the braking power as heat and shed it to the air, faster at speed;
                // the pads wear with the energy put through them
                float power = brakeForce * Mathf.Abs(u);
                BrakeTemp += (power / (BrakeHeatPerKg * m) - BrakeCooling * (1f + Mathf.Abs(u) / 20f) * (BrakeTemp - 20f)) * h;
                PadWear = Mathf.Min(1f, PadWear + power * h / PadLife);
            }
            float fxF = -sign * brakeForce * 0.65f;
            float fxR = -sign * brakeForce * 0.35f;
            if (input.Handbrake) fxR = -sign * 0.8f * grip * nr;
            switch (s.Drive)
            {
                case Drivetrain.All: fxF += drive * (1f - s.RearBias); fxR += drive * s.RearBias; break;
                case Drivetrain.Front: fxF += drive; break;
                default: fxR += drive; break;
            }
            // no force to push against below walking pace once stopped; nor when the brake holds the creep
            if (Mathf.Abs(u) < 0.3f && (drive == 0f || brakeForce >= Mathf.Abs(drive) && pedal < 0.02f)) { fxF = 0f; fxR = 0f; u = Mathf.MoveToward(u, 0f, 3f * h); }
            float capF = grip * nf * WornGrip(TyreWearFront), capR = grip * nr * WornGrip(TyreWearRear);
            // demand past the circle, on whichever axle is driven
            float wheelspin = Mathf.Max(0f, Mathf.Max(Mathf.Abs(fxR) / capR, Mathf.Abs(fxF) / capF) - 0.9f) * 10f;
            fxF = Mathf.Clamp(fxF, -capF, capF);
            fxR = Mathf.Clamp(fxR, -capR, capR);
            // an open diff lets the inside wheel spin away: the driven axle stops pushing at a
            // fraction of its grip, which leaves side grip — the car will not power over as easily
            if (drive != 0f)
            {
                float lim = s.DiffFactor;
                if (s.Drive != Drivetrain.Front && fxR * drive > 0) fxR = Mathf.Clamp(fxR, -capR * lim, capR * lim);
                if (s.Drive != Drivetrain.Rear && fxF * drive > 0) fxF = Mathf.Clamp(fxF, -capF * lim, capF * lim);
            }

            // --- lateral forces: slip angle through the tyre curve, inside what the circle leaves ---
            float speed = Mathf.Max(Mathf.Abs(u), 1f);
            float alphaF = Mathf.Atan2(w + a * r, speed) - delta * sign;
            float alphaR = Mathf.Atan2(w - b * r, speed);
            // the foot brake taking the whole rear circle AND the rear sliding sideways past its peak:
            // a rear let go under braking. Both: a straight stop saturates the rear too, and with
            // the help intact it stays straight, as a player braking in a line expects
            rearLock = Mathf.Max(rearLock, Mathf.Clamp((brakeForce * 0.35f / capR - 0.85f) / 0.15f, 0f, 1f)
                * Mathf.Clamp((Mathf.Abs(alphaR) - 0.08f) / 0.08f, 0f, 1f));
            float latF = Mathf.Sqrt(Mathf.Max(capF * capF - fxF * fxF, 0.01f * capF * capF));
            float latR = Mathf.Sqrt(Mathf.Max(capR * capR - fxR * fxR, 0.01f * capR * capR));
            if (input.Handbrake) latR *= Tyres.Handbrake;
            // Game: once sideways, the gas keeps the rear sliding, whatever drives the wheels — so a
            // front-driver, a mid-engined car or a 90 hp roadster holds a drift on the throttle
            // exactly like the FR cars do. Sim leaves each car to its own layout and power.
            if (arcade)
                latR *= 1f - Mathf.Min(0.9f, (s.Drive == Drivetrain.Front ? ArcadeSustainFf : ArcadeSustain) * Tyres.Sustain) * pedal
                    // from 14°, not 7°: a bump in an ordinary bend reaches 7° and was tipping plain
                    // driving into a drift nobody asked for; a real drift passes 14° at once
                    * Mathf.Clamp((Mathf.Abs(slipNow) - 0.25f) / 0.2f, 0f, 1f);
            float fyF = -latF * Mathf.Sin(tyreC * Mathf.Atan(TyreB * alphaF));
            float fyR = -latR * Mathf.Sin(tyreC * Mathf.Atan(TyreB * alphaR));
            (feelFy, feelAlpha, feelU) = (fyF, alphaF, u);
            if (TyreWearOn)
            {
                // sliding power: side force times how fast the contact patch slides sideways, plus
                // wheelspin on the driven axle; a grip driver barely scrubs, a drifter burns rubber
                float slideSpeed = Mathf.Abs(u) + 0.5f;
                float wearF = Mathf.Abs(fyF) * slideSpeed * Mathf.Abs(Mathf.Sin(alphaF));
                float wearR = Mathf.Abs(fyR) * slideSpeed * Mathf.Abs(Mathf.Sin(alphaR));
                float spin = wheelspin * 2f;   // m/s of patch slip, roughly
                if (s.Drive != Drivetrain.Front) wearR += Mathf.Abs(fxR) * spin;
                if (s.Drive != Drivetrain.Rear) wearF += Mathf.Abs(fxF) * spin;
                TyreWearFront = Mathf.Min(1f, TyreWearFront + wearF * h / TyreLife);
                TyreWearRear = Mathf.Min(1f, TyreWearRear + wearR * h / TyreLife);
            }

            // --- resistances and gravity along the grade ---
            float drag = 0.5f * AirDensity * s.DragArea * u * Mathf.Abs(u) * (1f - ground.Draft);
            float roll = (RollingResistance + roughDrag) * m * Gravity * sign;
            float cos = Mathf.Cos(delta), sin = Mathf.Sin(delta);

            float fx = fxR + fxF * cos - fyF * sin - drag - roll;
            float fy = fyR + fyF * cos + fxF * sin;
            float mz = a * (fyF * cos + fxF * sin) - b * fyR;
            // Game: past ArcadeMaxAngle a yaw moment pulls the nose back toward the travel, damped,
            // so holding the turn with the gas pinned is a held drift and not a spin. The wheels
            // alone cannot do it: at 60° of angle the fronts are already on the lock stop.
            if (arcade)
            {
                float angle = Mathf.Atan2(w, Mathf.Abs(u));
                float excess = angle - Mathf.Clamp(angle, -ArcadeMaxAngle, ArcadeMaxAngle);
                if (excess != 0f) mz += _help * m * a * b * (ArcadeCatch * excess - ArcadeCatchDamp * r);
            }

            float ax = fx / m + SlopeAccel(ground.Grade);
            float ay = fy / m;
            AccelX = ax;
            AccelY = ay;
            u += (ax + r * w) * h;
            w += (ay - r * u) * h;
            r += mz / (m * a * b) * h;

            // Below a few m/s the slip angles are dominated by noise and the model is stiff; blend
            // to rolling without slip, where the yaw rate is simply what the wheels ask for.
            // On the TOTAL speed: at 70° of drift u is small while the car is still doing 60 km/h
            // sideways, and blending on u zeroed that sideways speed — 50 km/h lost in 0.3 s.
            float k = Mathf.Clamp((Mathf.Sqrt(u * u + w * w) - 1f) / 3f, 0f, 1f);
            float rKin = u * Mathf.Tan(delta) / L;
            r = Mathf.Lerp(rKin, r, k);
            w = Mathf.Lerp(0f, w, k);
            // the wheel's feel follows the same blend: crawling, the fronts carry only the side force
            // the turn needs (front mass × u × yaw rate), not the tyre curve's force at noise-sized
            // slip angles — that made a parked wheel pull back harder the further it turned (#68)
            feelFy = Mathf.Lerp(m * b / L * u * rKin, feelFy, k);

            slideAccum += Mathf.Clamp(Mathf.Max(Mathf.Abs(alphaR), Mathf.Abs(alphaF) * 0.6f) * 3f
                + wheelspin + (input.Handbrake && Mathf.Abs(u) > 2f ? 0.6f : 0f), 0f, 1f);
        }

        // a locked rear takes the arcade help away within ~0.15 s; off the pedal it comes back as fast
        _help = Mathf.MoveToward(_help, 1f - rearLock, 7f * dt);

        // automatic gearbox: up near the redline, down when it bogs; a brief cut of drive on each
        _shiftTimer = Mathf.Max(0f, _shiftTimer - dt);
        CheckStall();
        if (auto && Gear > 0 && ground.OnFloor)
        {
            if (_coupledRpm > s.Redline * 0.94f && Gear < s.Gears.Length && pedal > 0.2f) { Gear++; _shiftTimer = 0.18f; }
            else if (Gear > 1 && _coupledRpm < s.PeakRpm * 0.55f) Gear--;
        }

        TyreSlide = ground.OnFloor ? Mathf.Clamp(slideAccum / Substeps, 0f, 1f) * Mathf.Clamp(Mathf.Abs(u) / 4f, 0f, 1f) : 0f;

        // the wheel's feel: the front axle against what it gives at its peak on tarmac (a car on ice
        // goes light), the road's roughness, and the weight of an unassisted rack at a standstill
        float frontPeak = s.Mass * Gravity * s.RearAxle / s.Wheelbase * s.Grip * Tyres.Grip;
        var (road, roadHz) = Core.WheelFeel.RoadFrom(ground.OnFloor ? CarSetups.Roughness(ground.Surface) * Mathf.Sqrt(Mathf.Max(s.Stiffness, 0.1f)) : 0f, u);
        _feel = new Core.WheelFeel(
            ground.OnFloor ? Core.WheelFeel.Aligning(feelFy, feelAlpha, frontPeak, feelU) : 0f,
            road, roadHz,
            ground.OnFloor ? Core.WheelFeel.WeightFrom(s.PowerSteering ? 0.25f : 0.8f, u) : 0f);
        WheelSpin += u / WheelRadius * dt;

        motion.Speed = Mathf.Sqrt(u * u + w * w);
        motion.Slip = motion.Speed > 0.05f ? Mathf.Atan2(w, u) : (reverse ? Mathf.Pi : 0f);
        motion.YawRate = r;
        motion.Yaw += r * dt;
        motion.Bank = 0f;
        // body roll: outward, a few degrees at the limit
        motion.Lean = Mathf.Clamp(-AccelY * 0.012f / s.Stiffness, -0.09f, 0.09f);
    }

    /// <summary>
    /// A kart's trip (#715): sliding sideways at speed (the angle between its nose and its travel past
    /// <see cref="TripAngle"/>) long enough digs the tyres in and it goes over onto the side it was
    /// sliding toward. Over, it scrapes to a stop in a second or two with nothing to steer or drive, then
    /// is stood up again. True while it lies there: the step is over.
    /// </summary>
    private bool TipStep(in RideGround ground, float dt, ref RideMotion motion)
    {
        if (_tipTime <= 0f)
        {
            float across = Mathf.Atan2(Mathf.Abs(Mathf.Sin(motion.Slip)), Mathf.Abs(Mathf.Cos(motion.Slip)));
            if (ground.OnFloor && motion.Speed > TripSpeed && across > TripAngle(ground.Surface)) _trip += dt;
            else _trip = Mathf.Max(0f, _trip - 2f * dt);
            if (_trip < TripTime) return false;
            (_trip, _tipTime, _tipSide) = (0f, TipDuration, motion.Slip >= 0f ? 1f : -1f);
        }
        _tipTime -= dt;
        Throttle = BrakePedal = 0f;
        Braking = HandbrakeOn = false;
        Rpm = Spec.IdleRpm;
        float before = motion.Speed;
        motion.Speed = Mathf.MoveToward(motion.Speed, 0f, TipScrape * dt);
        motion.YawRate = Mathf.MoveToward(motion.YawRate, 0f, 5f * dt);
        motion.Yaw += motion.YawRate * dt;
        motion.Lean = motion.Bank = 0f;
        AccelX = (motion.Speed - before) / dt;
        AccelY = 0f;
        TyreSlide = motion.Speed > 1f ? 1f : 0f;
        SteerAngle = Mathf.MoveToward(SteerAngle, 0f, 2f * dt);
        _steer = 0f;
        if (_tipTime <= 0f) { _tipSide = 0f; motion.Slip = 0f; }
        return true;
    }

    /// <summary>
    /// What another player needs to draw this car's moving parts: the body's slide already travels
    /// in the replicated transform, so these are the front-wheel angle (which also turns the
    /// driver's wheel and hands), the wheels' spin RATE (each peer turns its own wheels by it — an
    /// accumulated angle would wrap and stutter), rpm for the rev needle and engine note, and in W
    /// the lamps and roof as bit flags (<see cref="PoseBrake"/>…) and the throttle in eighths for
    /// the driver's right foot: small integers are exact in a float, and <c>Anim</c> is taken
    /// as-is, never blended.
    /// </summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(SteerAngle, motion.Speed * Mathf.Cos(motion.Slip) / WheelRadius, Rpm01,
            (Braking ? PoseBrake : 0) | (Headlights ? PoseHeadlights : 0) | (RoofOpen ? PoseRoof : 0) | (Bouncing ? PoseBounce : 0)
            | Mathf.RoundToInt(Mathf.Clamp(Throttle, 0f, 1f) * PoseThrottleSteps) << PoseThrottleShift
            | KartPose());

    private const int PoseBrake = 1, PoseHeadlights = 2, PoseRoof = 4;
    private const int PoseThrottleShift = 3, PoseThrottleSteps = 7;
    /// <summary>Above the throttle's three bits.</summary>
    private const int PoseBounce = 64;
    /// <summary>A kart's (#715): over on its left side / its right, then the lifted rear wheel's height in eighths (3 bits) and whether it is the left one.</summary>
    private const int PoseTipLeft = 128, PoseTipRight = 256, PoseLiftShift = 9, PoseLiftSteps = 7, PoseLiftLeft = 4096;

    /// <summary>How far the inside rear wheel comes up in a corner: 0 below 0.65 g of side force, 1 at 1 g, + the left wheel.</summary>
    private float Lift => IsKart ? Mathf.Clamp((Mathf.Abs(AccelY) / Gravity - 0.65f) / 0.35f, 0f, 1f) * Mathf.Sign(AccelY) : 0f;

    private int KartPose()
    {
        if (!IsKart) return 0;
        float lift = Lift;
        return (_tipTime > 0f ? (_tipSide > 0f ? PoseTipLeft : PoseTipRight) : 0)
            | Mathf.RoundToInt(Mathf.Abs(lift) * PoseLiftSteps) << PoseLiftShift | (lift > 0f ? PoseLiftLeft : 0);
    }

    private float _remoteSpin;

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (visual is not CarRig rig) return;
        _remoteSpin += pose.Y * dt;
        rig.SteerAngle = pose.X;
        rig.WheelSpin = _remoteSpin;
        int flags = Mathf.RoundToInt(pose.W);
        rig.BrakeLights = (flags & PoseBrake) != 0;
        rig.Headlights = (flags & PoseHeadlights) != 0;
        rig.RoofOpen = (flags & PoseRoof) != 0;
        rig.Bouncing = (flags & PoseBounce) != 0;
        if (IsKart)
        {
            rig.Tip = (flags & PoseTipLeft) != 0 ? 1f : (flags & PoseTipRight) != 0 ? -1f : 0f;
            rig.Lift = ((flags >> PoseLiftShift) & PoseLiftSteps) / (float)PoseLiftSteps * ((flags & PoseLiftLeft) != 0 ? 1f : -1f);
        }
        Rpm = Mathf.Lerp(Spec.IdleRpm, Spec.Redline, pose.Z);
        rig.WheelTurn = pose.X * Ratio;
        rig.Throttle = ((flags >> PoseThrottleShift) & PoseThrottleSteps) / (float)PoseThrottleSteps;
        rig.Brake = rig.BrakeLights ? 1f : 0f;
        rig.Rpm = Rpm;
        rig.SpeedKmh = pose.Y * WheelRadius * 3.6f;
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is not CarRig rig) return;
        rig.SteerAngle = SteerAngle;
        rig.WheelSpin = WheelSpin;
        // a kart has no suspension: it does not pitch, and its inside rear wheel lifts instead
        rig.BodyPitch = IsKart ? 0f : Mathf.Clamp(AccelX * 0.006f / Spec.Stiffness, -0.05f, 0.05f);
        if (IsKart) (rig.Tip, rig.Lift) = (_tipTime > 0f ? _tipSide : 0f, Lift);
        rig.BrakeLights = Braking;
        rig.Headlights = Headlights;
        rig.RoofOpen = RoofOpen;
        rig.Bouncing = Bouncing;
        rig.WheelTurn = SteerAngle * Ratio;
        rig.Throttle = Throttle;
        rig.Brake = BrakePedal;
        rig.Clutch = ClutchPedal;
        rig.Handbrake = HandbrakeOn;
        rig.Rpm = Rpm;
        rig.Gear = Gear;
        rig.SpeedKmh = motion.Speed * Mathf.Cos(motion.Slip) * 3.6f;
    }
}
