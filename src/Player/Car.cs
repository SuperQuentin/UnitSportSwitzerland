using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>Which axles the engine turns.</summary>
public enum Drivetrain { Rear, All }

/// <summary>
/// One car as numbers. Real-world figures for the machines each one stands for, so the Sim
/// profile drives like the car and not like a tuned guess.
/// </summary>
public sealed record CarSpec
{
    public required RideKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Blurb { get; init; }
    public required CarStyle Style { get; init; }
    public required Color Paint { get; init; }

    /// <summary>Kerb mass with a driver, kg.</summary>
    public float Mass { get; init; }
    /// <summary>Centre of mass to front / rear axle, m. Their sum is the wheelbase.</summary>
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
    /// <summary>Share of AWD torque sent to the rear axle.</summary>
    public float RearBias { get; init; } = 0.6f;

    /// <summary>Road-wheel lock at full steer, radians.</summary>
    public float MaxSteer { get; init; } = 0.6f;
    /// <summary>Drag area Cd·A, m².</summary>
    public float DragArea { get; init; } = 0.65f;

    public float Wheelbase => FrontAxle + RearAxle;

    /// <summary>A light 80s hatchback coupe: 130 hp, 950 kg, rear drive, a 7,800 rpm four.</summary>
    public static readonly CarSpec Coupe86 = new()
    {
        Kind = RideKind.Coupe86, Label = "Coupe 86",
        Blurb = "Light, rear drive, revs to 7,800. W / RT gas, S / LT brake, Space / A handbrake — kick the rear out and counter-steer",
        Style = CarStyle.Coupe86, Paint = new Color(0.94f, 0.94f, 0.92f),
        Mass = 1000f, FrontAxle = 1.12f, RearAxle = 1.28f, CgHeight = 0.5f, Grip = 1.0f,
        PeakKw = 96f, PeakRpm = 6600f, IdleRpm = 900f, Redline = 7800f,
        Gears = new[] { 3.59f, 2.02f, 1.38f, 1.00f, 0.86f }, FinalDrive = 4.3f,
        MaxSteer = 0.62f, DragArea = 0.62f,
    };

    /// <summary>A 90s twin-turbo rotary coupe: 280 hp in 1,300 kg, quick and snappy.</summary>
    public static readonly CarSpec RotaryFd = new()
    {
        Kind = RideKind.RotaryFd, Label = "Rotary FD",
        Blurb = "Twin-turbo rotary, rear drive, 280 hp. Snappy: throttle alone breaks the rear loose",
        Style = CarStyle.RotaryFd, Paint = new Color(0.93f, 0.8f, 0.12f),
        Mass = 1320f, FrontAxle = 1.2f, RearAxle = 1.23f, CgHeight = 0.46f, Grip = 1.05f,
        PeakKw = 206f, PeakRpm = 6500f, IdleRpm = 850f, Redline = 8000f,
        Gears = new[] { 3.48f, 2.02f, 1.39f, 1.00f, 0.72f }, FinalDrive = 4.1f,
        MaxSteer = 0.6f, DragArea = 0.58f,
    };

    /// <summary>A turbo boxer rally saloon: four-wheel drive, grips until it doesn't.</summary>
    public static readonly CarSpec Rally4wd = new()
    {
        Kind = RideKind.Rally4wd, Label = "Rally 4WD",
        Blurb = "Turbo boxer, four-wheel drive. Grips hard; drifts all four wheels on gravel",
        Style = CarStyle.Rally4wd, Paint = new Color(0.12f, 0.24f, 0.62f),
        Mass = 1360f, FrontAxle = 1.28f, RearAxle = 1.24f, CgHeight = 0.52f, Grip = 1.05f,
        PeakKw = 206f, PeakRpm = 6000f, IdleRpm = 850f, Redline = 7000f,
        Gears = new[] { 3.17f, 1.88f, 1.30f, 0.97f, 0.74f }, FinalDrive = 4.44f,
        Drive = Drivetrain.All, RearBias = 0.6f,
        MaxSteer = 0.58f, DragArea = 0.7f,
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
public sealed class Car : Rideable
{
    public CarSpec Spec { get; }

    public Car(CarSpec spec) => Spec = spec;

    public override RideKind Kind => Spec.Kind;
    public override string Label => Spec.Label;
    public override string Blurb => Spec.Blurb;
    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 160f;

    // the driver's seat, in the visual's frame (faces −Z, so +X is the driver's right):
    // these are Japanese-market cars, right-hand drive
    public override Vector3 FirstPersonEye => new(0.36f, 1.08f, 0.15f);
    public override float EyeHeight => 1.1f;
    public override float ChaseDistance => 5.4f;
    public override float ChaseHeight => 1.55f;
    public override float BaseFov => 68f;
    public override float MaxFov => 90f;
    public override float FovSpeed => 45f;
    public override float BodyRadius => 0.85f;
    public override float BodyHeight => 1.7f;
    public override float DismountSpeed => 1.5f;
    public override (Vector3 Centre, Vector3 Size) ParkedBox => (new Vector3(0, 0.65f, 0), new Vector3(1.7f, 1.3f, 4.2f));

    // ---- what the feel layer and the rig read ----
    /// <summary>Engine speed, rpm.</summary>
    public float Rpm { get; private set; }
    /// <summary>idle 0 .. redline 1, for <c>EngineSynth.Set</c>.</summary>
    public float Rpm01 => Mathf.Clamp((Rpm - Spec.IdleRpm) / (Spec.Redline - Spec.IdleRpm), 0f, 1f);
    /// <summary>1-based forward gear, −1 reverse.</summary>
    public int Gear { get; private set; } = 1;
    /// <summary>Throttle actually applied (the pedal, or the brake pedal in reverse), 0..1.</summary>
    public float Throttle { get; private set; }
    /// <summary>How hard the tyres are sliding, 0..1: squeal and smoke.</summary>
    public float TyreSlide { get; private set; }
    /// <summary>Front road-wheel angle, radians, + = left.</summary>
    public float SteerAngle { get; private set; }
    /// <summary>Accumulated wheel rotation, radians, for the rig.</summary>
    public float WheelSpin { get; private set; }
    public bool Braking { get; private set; }
    /// <summary>Longitudinal and lateral acceleration, m/s² (+ forward, + left), for body pitch and roll.</summary>
    public float AccelX { get; private set; }
    public float AccelY { get; private set; }

    private const float WheelRadius = 0.3f;
    private const float Driveline = 0.85f;
    private const float AirDensity = 1.2f;
    private const float RollingResistance = 0.013f;
    /// <summary>Tyre curve: sin(C·atan(B·α)). B sets the peak slip angle (~0.15 rad), C the fall past it.</summary>
    private const float TyreB = 14f, SimTyreC = 1.45f, ArcadeTyreC = 1.3f;
    /// <summary>Game: counter-steer the fronts this fraction of the way toward the direction of travel.</summary>
    private const float ArcadeAssist = 0.45f;
    private const float ArcadePower = 1.35f, ArcadeGrip = 1.12f;
    /// <summary>Rear side grip left while the handbrake locks them.</summary>
    private const float HandbrakeGrip = 0.35f;
    private const int Substeps = 4;

    private float _steer;   // eased steering input, −1..1
    private float _shiftTimer;

    public override Node3D BuildVisual(int riderIndex) => CarRig.Create(Spec.Style, Spec.Paint);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        var s = Spec;
        bool arcade = Arcade;
        float grip = s.Grip * (arcade ? ArcadeGrip : 1f);
        float tyreC = arcade ? ArcadeTyreC : SimTyreC;

        // planar state in the body frame: u forward, w to the left
        float u = motion.Speed * Mathf.Cos(motion.Slip);
        float w = motion.Speed * Mathf.Sin(motion.Slip);
        float r = motion.YawRate;

        // gear: brake at a standstill selects reverse, throttle selects first
        if (Gear > 0 && u < 0.5f && input.Brake > 0.3f && input.Throttle < 0.05f) Gear = -1;
        else if (Gear < 0 && u > -0.5f && input.Throttle > 0.3f) Gear = 1;
        bool reverse = Gear < 0;
        float pedal = reverse ? input.Brake : input.Throttle;
        float brake = reverse ? input.Throttle : input.Brake;
        // in reverse the brake pedal drives; braking then is the gas pedal against the motion
        if (reverse && u > 0.5f) { brake = Mathf.Max(brake, pedal); pedal = 0f; }
        Throttle = pedal;
        Braking = brake > 0.05f;

        // Keyboard steering is ±1 in a frame; a rack takes a moment to wind on, and without it
        // every tap is a flick that throws the car into a spin. Faster back to centre.
        float steerTarget = input.Steer;
        float rate = Mathf.Abs(steerTarget) < Mathf.Abs(_steer) || steerTarget * _steer < 0 ? 9f : 5f;
        _steer = Mathf.MoveToward(_steer, steerTarget, rate * dt);
        // at speed the same input asks for less lock, or 200 km/h would be twitchier than 20
        float lockScale = 1f / (1f + Mathf.Max(u, 0f) / 28f);
        float delta = -_steer * s.MaxSteer * lockScale;   // +steer is right, + angle is left
        if (arcade && u > 3f)
            delta = Mathf.Clamp(delta + ArcadeAssist * Mathf.Wrap(motion.Slip, -Mathf.Pi, Mathf.Pi), -s.MaxSteer, s.MaxSteer);
        SteerAngle = delta;

        float peakTorque = s.PeakKw * 1000f / (s.PeakRpm * Mathf.Tau / 60f) * (arcade ? ArcadePower : 1f);
        float slideAccum = 0f;
        float h = dt / Substeps;

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
            float ratio = (reverse ? s.Reverse : s.Gears[Gear - 1]) * s.FinalDrive;
            float wheelRpm = Mathf.Abs(u) / WheelRadius * 60f / Mathf.Tau;
            Rpm = Mathf.Max(s.IdleRpm, wheelRpm * ratio);
            float x = Mathf.Min(Rpm / s.Redline, 1f);
            float torque = Rpm >= s.Redline ? 0f : peakTorque * (0.7f + 1.2f * x * (1f - x));
            float drive = _shiftTimer > 0 ? 0f : pedal * torque * ratio * Driveline / WheelRadius;
            if (reverse) drive = -drive;

            // --- load transfer from last substep's longitudinal acceleration ---
            float nf = m * (Gravity * b - AccelX * s.CgHeight) / L;
            float nr = m * Gravity - nf;
            nf = Mathf.Max(nf, 0.1f * m * Gravity);
            nr = Mathf.Max(nr, 0.1f * m * Gravity);

            // --- longitudinal forces per axle ---
            float sign = Mathf.Sign(u);
            float brakeForce = brake * grip * m * Gravity * 0.95f;
            float fxF = -sign * brakeForce * 0.65f;
            float fxR = -sign * brakeForce * 0.35f;
            if (input.Handbrake) fxR = -sign * 0.8f * grip * nr;
            if (s.Drive == Drivetrain.All) { fxF += drive * (1f - s.RearBias); fxR += drive * s.RearBias; }
            else fxR += drive;
            // no force to push against below walking pace once stopped
            if (Mathf.Abs(u) < 0.3f && drive == 0f) { fxF = 0f; fxR = 0f; u = Mathf.MoveToward(u, 0f, 3f * h); }
            float capF = grip * nf, capR = grip * nr;
            fxF = Mathf.Clamp(fxF, -capF, capF);
            float wheelspin = Mathf.Max(0f, Mathf.Abs(fxR) / capR - 0.9f) * 10f;   // demand past the circle
            fxR = Mathf.Clamp(fxR, -capR, capR);

            // --- lateral forces: slip angle through the tyre curve, inside what the circle leaves ---
            float speed = Mathf.Max(Mathf.Abs(u), 1f);
            float alphaF = Mathf.Atan2(w + a * r, speed) - delta * sign;
            float alphaR = Mathf.Atan2(w - b * r, speed);
            float latF = Mathf.Sqrt(Mathf.Max(capF * capF - fxF * fxF, 0.01f * capF * capF));
            float latR = Mathf.Sqrt(Mathf.Max(capR * capR - fxR * fxR, 0.01f * capR * capR));
            if (input.Handbrake) latR *= HandbrakeGrip;
            float fyF = -latF * Mathf.Sin(tyreC * Mathf.Atan(TyreB * alphaF));
            float fyR = -latR * Mathf.Sin(tyreC * Mathf.Atan(TyreB * alphaR));

            // --- resistances and gravity along the grade ---
            float drag = 0.5f * AirDensity * s.DragArea * u * Mathf.Abs(u);
            float roll = RollingResistance * m * Gravity * sign;
            float cos = Mathf.Cos(delta), sin = Mathf.Sin(delta);

            float fx = fxR + fxF * cos - fyF * sin - drag - roll;
            float fy = fyR + fyF * cos + fxF * sin;
            float mz = a * (fyF * cos + fxF * sin) - b * fyR;

            float ax = fx / m + SlopeAccel(ground.Grade);
            float ay = fy / m;
            AccelX = ax;
            AccelY = ay;
            u += (ax + r * w) * h;
            w += (ay - r * u) * h;
            r += mz / (m * a * b) * h;

            // Below a few m/s the slip angles are dominated by noise and the model is stiff; blend
            // to rolling without slip, where the yaw rate is simply what the wheels ask for.
            float k = Mathf.Clamp((Mathf.Abs(u) - 1f) / 3f, 0f, 1f);
            float rKin = u * Mathf.Tan(delta) / L;
            r = Mathf.Lerp(rKin, r, k);
            w = Mathf.Lerp(0f, w, k);

            slideAccum += Mathf.Clamp(Mathf.Max(Mathf.Abs(alphaR), Mathf.Abs(alphaF) * 0.6f) * 3f
                + wheelspin + (input.Handbrake && Mathf.Abs(u) > 2f ? 0.6f : 0f), 0f, 1f);
        }

        // automatic gearbox: up near the redline, down when it bogs; a brief cut of drive on each
        _shiftTimer = Mathf.Max(0f, _shiftTimer - dt);
        if (!reverse && ground.OnFloor)
        {
            if (Rpm > s.Redline * 0.94f && Gear < s.Gears.Length && pedal > 0.2f) { Gear++; _shiftTimer = 0.18f; }
            else if (Gear > 1 && Rpm < s.PeakRpm * 0.55f) Gear--;
        }

        TyreSlide = ground.OnFloor ? Mathf.Clamp(slideAccum / Substeps, 0f, 1f) * Mathf.Clamp(Mathf.Abs(u) / 4f, 0f, 1f) : 0f;
        WheelSpin += u / WheelRadius * dt;

        motion.Speed = Mathf.Sqrt(u * u + w * w);
        motion.Slip = motion.Speed > 0.05f ? Mathf.Atan2(w, u) : (reverse ? Mathf.Pi : 0f);
        motion.YawRate = r;
        motion.Yaw += r * dt;
        motion.Bank = 0f;
        // body roll: outward, a few degrees at the limit
        motion.Lean = Mathf.Clamp(-AccelY * 0.012f, -0.09f, 0.09f);
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is not CarRig rig) return;
        rig.SteerAngle = SteerAngle;
        rig.WheelSpin = WheelSpin;
        rig.BodyPitch = Mathf.Clamp(AccelX * 0.006f, -0.05f, 0.05f);
        rig.BrakeLights = Braking;
    }
}
