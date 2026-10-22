using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A road bike, ridden on the real power equation rather than an arcade speed cap.
///
/// <para>
/// <c>(m + I/r²)·a = η·P/v − ½ρ·CdA·(1 − draft)·v² − Crr·m·g·cosθ − m·g·sinθ</c> (<see cref="BikePhysics"/>,
/// #1124). Every term is a measured cycling quantity, so the numbers that come out are the numbers a
/// rider would recognise: for the 72.5 kg reference rider at 500 m, 180 W holds 33 km/h on the flat,
/// the same 180 W climbs an 8% ramp at 9 km/h, and a tucked descent of that ramp settles at 75 km/h.
/// None of those were tuned — they fall out of the equation, which is the reason for using it. The
/// air thins with altitude, the tyres roll harder off tarmac, the drag area is the rider's own size
/// and position, and a wheel ahead shelters.
/// </para>
///
/// <para>
/// It is also the reason the input is <see cref="RiderWatts"/> and not "how fast the bike goes".
/// A home trainer measures watts; a cadence sensor measures rpm. When RideLink is connected, the
/// number it reports drops straight into this model and the Swiss road under the wheels supplies
/// the <c>sinθ</c> — which is the whole point of putting a bike in a terrain simulator.
/// </para>
/// </summary>
public sealed class Bicycle : Rideable
{
    public override RideKind Kind => RideKind.RoadBike;
    public override string Label => "Road bike";
    public override string Blurb => "{throttle} pedal, {tuck_boost} sprint, {brake} brake, {move_left}/{move_right} steer — climbs cost you";

    // ---- the rider and the machine ----
    /// <summary>The bike under the rider: the owner's Scott Addict RC 15 (#1124).</summary>
    public BicycleSpec Spec { get; } = BicycleCatalog.Default;

    /// <summary>Bottles, tools and a saddle bag, kg.</summary>
    private const float Kit = 1.5f;

    /// <summary>The Game profile's rider, kg: 82 with bike and bottles, whatever the settings say.</summary>
    private const float ArcadeRiderMass = 72.5f;

    /// <summary>
    /// The rider in kit, kg. Sim takes the rider's own weight from the settings, the number a home
    /// trainer's watts have to lift up a Swiss pass; Game keeps the reference rider.
    /// </summary>
    private static float RiderMass => Arcade ? ArcadeRiderMass : Core.GameSettings.Current.RiderMass;

    /// <summary>Where the rider's hands are now: chosen from what they are doing (#1124), no key of its own.</summary>
    public RidingPosition Position { get; private set; }

    /// <summary>The drag area's position factor, eased: getting down on the drops takes a moment.</summary>
    private float _positionFactor = 1f;
    private const float PositionChangeTime = 0.5f;

    /// <summary>Freewheeling downhill above this, m/s (40 km/h), the rider tucks.</summary>
    private const float TuckSpeed = 11f;

    /// <summary>
    /// Rolling resistance on what is under the tyres (#1124): a 28 mm road tyre at road pressure.
    /// Tarmac 0.0045 (roller drum measurements of good tyres), a gravel lane 0.012, a forest track
    /// 0.015, grass 0.03 and snow 0.05: a road bike crosses a meadow, it does not ride it.
    /// </summary>
    public static float RollingResistance(Audio.Surface surface) => surface switch
    {
        Audio.Surface.Asphalt or Audio.Surface.Indoor => 0.0045f,
        Audio.Surface.Wood => 0.006f,
        Audio.Surface.Rock => 0.010f,
        Audio.Surface.Gravel => 0.012f,
        Audio.Surface.Forest => 0.015f,
        Audio.Surface.Ice => 0.008f,
        Audio.Surface.Grass => 0.030f,
        Audio.Surface.Snow => 0.050f,
        Audio.Surface.Water => 0.150f,
        _ => 0.0045f,
    };

    /// <summary>The chain, the rings and the cassette, shifted for the rider as Di2's synchro shift does (#1124).</summary>
    public BikeDrivetrain Gears { get; }

    /// <summary>
    /// The watts are a power meter's or a trainer's, not the keys': the cadence curve does not touch
    /// them, because a real rider at 50 rpm making 250 W has already paid for the low cadence.
    /// </summary>
    public bool PowerMeasured { get; set; }

    /// <summary>A chest strap's reading, bpm: the body's heart rate is it instead of the model's (#1124).</summary>
    public float? MeasuredHeartRate { get; set; }

    /// <summary>The rider's threshold, reserve and heart rate (#1124): Sim only.</summary>
    public RiderPhysiology Body { get; } = new(Core.GameSettings.Current.RiderCriticalPower);

    /// <summary>Up a ramp steeper than this, grinding under this cadence, the rider stands (#1124).</summary>
    private const float StandingGrade = 0.08f, StandingCadence = 70f;

    /// <summary>A cadence sensor's reading, rpm: the legs turn at it instead of the gear's (#1124).</summary>
    public float? MeasuredCadence { get; set; }

    /// <summary>Every point of the bike and the rider's fit to it: the centre of mass for braking.</summary>
    public BikeFrame Frame { get; }

    public Bicycle()
    {
        Gears = new BikeDrivetrain(Spec);
        Frame = Spec == BicycleCatalog.Default ? BikeFrame.Default : new BikeFrame(Spec);
    }

    /// <summary>Steady effort, W. Roughly a fit rider's endurance power.</summary>
    public float RiderWatts { get; set; } = 180f;

    /// <summary>Shift. Not sustainable, but nothing here is asking it to be.</summary>
    public float SprintWatts { get; set; } = 520f;

    /// <summary>
    /// Cap on pedal thrust, N. <c>P/v</c> diverges at a standstill, and unclamped it would
    /// launch a stationary bike at 40 m/s². A rider can put roughly their own body weight
    /// through the pedals out of the saddle, which is what this is.
    /// </summary>
    private const float MaxThrust = 260f;

    /// <summary>The rear tyre is sliding under hard braking (#1124): the skid the feel layer shows.</summary>
    public bool RearLocked { get; private set; }

    /// <summary>Last step's acceleration along the road, m/s²: what the friction circle has left for leaning.</summary>
    private float _accel;

    // ---- steering ----
    /// <summary>
    /// Maximum lean, radians (~43°). A pro descending on dry tarmac reaches 45°; the old 34° was
    /// a nervous commuter's, and at 50 km/h it made a hairpin need the whole road.
    /// </summary>
    private const float MaxLean = 0.75f;

    // ---- the Game profile (Rideable.Arcade): a much fitter, braver rider on the same physics ----
    /// <summary>Cruise and sprint, W. 350 W holds ~40 km/h on the flat, 900 W sprints past 55.</summary>
    private const float ArcadeWatts = 350f, ArcadeSprintWatts = 900f;
    private const float ArcadeMaxThrust = 420f;
    /// <summary>Knee-down grip and a quicker roll-in: corners you would not dare in real life.</summary>
    private const float ArcadeMaxLean = 0.88f, ArcadeBankResponse = 7.5f, ArcadeMaxYawRate = 2.1f;
    private const float ArcadeBrakeDecel = 7.5f;

    /// <summary>Below this the bike is being wheeled, not ridden, and steering is direct.</summary>
    private const float WalkingPace = 1.2f;

    /// <summary>
    /// The figure's own eye, so first person sits where the drawn head is looking from: lower on the
    /// drops, lowest tucked, up and forward standing (#1124).
    /// </summary>
    public override Vector3 FirstPersonEye => Eyes[(int)Position];

    private static readonly Vector3[] Eyes = System.Array.ConvertAll(
        new[] { RidingPosition.Hoods, RidingPosition.Drops, RidingPosition.Tuck, RidingPosition.Standing },
        p => HumanMeshBuilder.MountsForCyclist(p).Eye
            + new Vector3(0, 0, -0.06f));   // just proud of the face, so the head never fills the lens

    public override float EyeHeight => 1.48f;
    public override float ChaseDistance => 3.9f;
    public override float ChaseHeight => 1.55f;
    public override float FovSpeed => 16f;

    private float _cadence;

    /// <summary>The cranks' rpm now: 0 freewheeling (the HUD, and the legs through <see cref="Animate"/>).</summary>
    public float Cadence => _cadence;

    public override Node3D BuildVisual(int riderIndex, Avatar.Outfit outfit = default) => Cyclist.Create(riderIndex, outfit);

    public override bool IsVehicle => true;

    public override Node3D BuildParkedVisual(int riderIndex) => new MeshInstance3D
    {
        Name = "Bike",
        Mesh = BikeMeshBuilder.Build(BikePalette.ForRider(riderIndex)),
        MaterialOverride = HumanMeshBuilder.Material(),
    };


    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        float v = motion.Speed;
        float riderMass = RiderMass;
        float mass = riderMass + Spec.Mass + Kit;
        // the wheels' spin is mass to speed up and slow down, but not weight on the tyres or the hill
        float inertia = mass + Spec.WheelEffectiveMass;

        // --- steering ---------------------------------------------------------------
        // A bicycle changes direction by leaning, so the turn radius grows with speed: the
        // same handlebar input that flicks you round a bollard at 5 km/h is a long sweeping
        // bend at 50. Modelling it the other way — a fixed turn rate — is what makes vehicles
        // in games feel like they are on rails.
        // Sim's lean is what the tyres hold on this ground after braking or pedalling took its share
        // of the friction circle (#1124): gravel gives ~33°, a hard stop stands the bike up.
        bool arcade = Arcade;
        float grip = Motorbike.SurfaceGrip(ground.Surface, BikePhysics.TyreGrip, 0f);
        float maxLean = arcade ? ArcadeMaxLean : Mathf.Min(MaxLean, BikePhysics.GripLean(grip, _accel));
        if (arcade)
            SteerByLean(ref motion, input.Steer, v, maxLean, ArcadeMaxYawRate, ArcadeBankResponse, WalkingPace, dt);
        else
            Balance(ref motion, input.Steer, v, maxLean, riderMass, dt);

        // --- the rider's position: sprinting on the drops, a fast descent tucked, out of the saddle
        // grinding up a ramp, else the hoods
        Position = input.Effort && input.Throttle > 0.05f ? RidingPosition.Drops
            : ground.OnFloor && input.Throttle < 0.05f && v > TuckSpeed && ground.Grade < -0.02f ? RidingPosition.Tuck
            : ground.OnFloor && input.Throttle > 0.05f && ground.Grade > StandingGrade && Gears.Cadence(v) < StandingCadence ? RidingPosition.Standing
            : RidingPosition.Hoods;
        _positionFactor += (BikePhysics.PositionFactor(Position) - _positionFactor) * (1f - Mathf.Exp(-dt / PositionChangeTime));
        float dragArea = BikePhysics.DragArea(riderMass, RidingPosition.Hoods) * _positionFactor;
        float air = BikePhysics.AirDensity(ground.Altitude);

        if (!ground.OnFloor)
        {
            // airborne off a lip: no thrust, no rolling resistance, just air
            v -= 0.5f * air * dragArea * v * v / inertia * dt;
            if (!arcade) Body.Step(0f, dt, MeasuredHeartRate);
            motion.Speed = Mathf.Max(0f, v);
            return;
        }

        // --- the power equation -----------------------------------------------------
        // Sim keeps RiderWatts, which is what a home trainer writes into; Game ignores it
        float watts = input.Throttle * (arcade
            ? (input.Effort ? ArcadeSprintWatts : ArcadeWatts)
            : (input.Effort ? SprintWatts : RiderWatts));
        // the gear the chain is in sets the cadence, and the keys' watts fall off away from 90 rpm
        bool pedalling = watts > 0f;
        Gears.AutoShift(v, pedalling, dt);
        float rpm = Gears.Cadence(v);
        // out of the saddle the rider's weight turns a low cadence that seated legs cannot
        if (!PowerMeasured) watts *= Position == RidingPosition.Standing ? Mathf.Max(BikeDrivetrain.PowerAtCadence(rpm), 0.95f) : BikeDrivetrain.PowerAtCadence(rpm);
        // Sim: the rider's body (#1124). Keys and pad are held to what the reserve allows; a power meter's
        // watts are the rider's real legs, already spent, and only drain the reserve shown
        if (!arcade)
        {
            float cp = Core.GameSettings.Current.RiderCriticalPower;
            if (cp != Body.CriticalPower) Body.Set(cp, Body.WPrime);
            if (!PowerMeasured) watts = Body.Available(watts);
            Body.Step(watts, dt, MeasuredHeartRate);
        }
        float thrust = pedalling
            ? Mathf.Min(BikePhysics.DrivetrainEfficiency * watts / Mathf.Max(v, 0.5f), arcade ? ArcadeMaxThrust : MaxThrust)
            : 0f;

        float resisting = BikePhysics.ResistingForce(v, mass, dragArea, ground.Draft, RollingResistance(ground.Surface), air, ground.Grade);
        v += (thrust - resisting) / inertia * dt;

        // Sim: front and rear brakes, the load moving forward, the rear locking, the over-the-bars
        // point (#1124). Game keeps its arcade stopping power.
        RearLocked = false;
        float braking = 0f;
        if (input.Brake > 0.01f && v > 0f)
        {
            if (arcade) braking = input.Brake * ArcadeBrakeDecel;
            else
            {
                var (toFront, toRear, height) = Frame.Balance(riderMass);
                var brake = BikePhysics.Brake(input.Brake, grip, BikePhysics.LeverStrength(Spec.Brakes), toFront, toRear, height);
                braking = brake.Decel;
                RearLocked = brake.RearLocked && v > 1f;
            }
            v -= braking * dt;
        }
        // what the tyres are asked for along the road: the pedals' push, or the brakes
        _accel = thrust / inertia - braking;

        // A bicycle does not roll backwards down a hill; it stops and you put a foot down.
        // Letting the speed go negative would drive the whole model backwards through itself.
        motion.Speed = Mathf.Max(0f, v);

        // The legs turn with the chain: pedalling, the freewheel is engaged and the cranks go round at
        // the gear's cadence (or the sensor's); off the pedals they stop, because a road bike freewheels.
        _cadence = pedalling ? Mathf.Min(MeasuredCadence ?? rpm, 150f) : 0f;
    }

    // ---- Sim's steering: the Whipple bicycle and its rider (#1124) ----
    private RiderBalance? _balance;
    private float _balanceMass = float.NaN;

    /// <summary>Sim: the rider's lean takes ~2ζ/ω to answer, the time a damped spring's response lags its input.</summary>
    public override float SteerLag => Arcade ? 0f : 2f * RiderBalance.LeanDamping / RiderBalance.LeanFrequency;

    /// <summary>The bars' angle, rad, + to the rider's right: the rider's hands on the Whipple model's steer.</summary>
    public float SteerAngle { get; private set; }

    /// <summary>
    /// Sim's steering: the input asks for a lean, the rider steers the bars to get there, and the bike
    /// leans and turns as the Whipple equations for this frame and this rider say (<see cref="RiderBalance"/>).
    /// Countersteer, the wobble at walking pace and the steadiness at speed all come out of them.
    /// </summary>
    private void Balance(ref RideMotion motion, float steer, float v, float maxLean, float riderMass, float dt)
    {
        // the rider's weight is in the matrices: a new one, a new model
        if (_balance == null || riderMass != _balanceMass)
        {
            float was = _balance?.Roll ?? 0f;
            _balance = new RiderBalance(new WhippleModel(WhippleParameters.For(Frame, riderMass, Kit)));
            _balance.Reset(was);
            _balanceMass = riderMass;
        }
        // the paper's signs: + right for roll, steer and yaw; Godot's yaw is + left and SteerByLean's
        // bank takes the yaw's sign, so the game's lean is the paper's roll negated
        float target = steer * maxLean;
        float yaw = _balance.Step(v, target, maxLean, dt);
        // the linearized model turns as tan φ ≈ φ: at 40° it would under-turn by 10%. A steady turn
        // turns at g·tanφ/v whatever the equations' order, so the yaw is scaled to that
        float roll = _balance.Roll;
        if (Mathf.Abs(roll) > 0.01f) yaw *= Mathf.Tan(roll) / roll;

        motion.Bank = -target;
        motion.Lean = -roll;
        motion.YawRate = -yaw;
        motion.Yaw += motion.YawRate * dt;
        SteerAngle = _balance.Steer;
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is not Cyclist cyclist) return;
        cyclist.CadenceRpm = _cadence;
        cyclist.SteerAngle = Arcade ? 0f : SteerAngle;
        cyclist.RidingPose = Position;
    }

    /// <summary>Cadence, and the crank angle itself so a remote rider's legs are where the owner's are.</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        visual is Cyclist c ? new Vector4(c.CadenceRpm, c.CrankAngle, c.SteerAngle, (int)c.RidingPose) : default;

    private float _seenCrank = float.NaN;

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (visual is not Cyclist cyclist) return;
        cyclist.CadenceRpm = pose.X;
        cyclist.SteerAngle = pose.Z;
        cyclist.RidingPose = (RidingPosition)Mathf.Clamp(Mathf.RoundToInt(pose.W), 0, (int)RidingPosition.Standing);
        // a fresh angle is taken as-is; between updates the cadence turns it
        if (pose.Y != _seenCrank) cyclist.SetCrankAngle(_seenCrank = pose.Y);
    }
}
