using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

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
    /// <summary>Drag area Cd·A, m².</summary>
    public float DragArea { get; init; } = 0.65f;

    public float Wheelbase => FrontAxle + RearAxle;

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
                return 0.3f;
            return (rim * 25.4f * 0.5f + w * ar / 100f) / 1000f * 0.97f;   // loaded: ~3% squat
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
    // Up and behind, looking down ~25° over the roof: from a level camera at roof height the car
    // itself hid the road you were about to drive onto.
    public override float ChaseDistance => 6.2f;
    public override float ChaseHeight => 4.1f;
    public override float ChasePitch => -0.44f;
    // stays above the car in a drift rather than swinging round to the side of it
    public override float ChaseFollowsTravel => 0.15f;
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
    /// <summary>Rear side grip left while the handbrake locks them.</summary>
    private const float HandbrakeGrip = 0.35f;
    private const int Substeps = 4;

    private float _steer;   // eased steering input, −1..1
    private float _shiftTimer;

    /// <summary>
    /// A copy with the same gear, rack position and read-outs: <see cref="Step"/> is pure apart from
    /// this state, so stepping a clone forward is a look into the car's future — which is how a
    /// driver can try a drift before committing to it.
    /// </summary>
    public Car Clone() => (Car)MemberwiseClone();

    public override Node3D BuildVisual(int riderIndex) => CarRig.Create(Spec.Body, Spec.Wheelbase);

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
        // — but never in a slide: catching a drift needs the full lock, and at 60 km/h the speed
        // scaling alone left 22° of it, which cannot catch anything
        float slipNow = Mathf.Wrap(motion.Slip, -Mathf.Pi, Mathf.Pi);
        float lockScale = Mathf.Lerp(1f / (1f + Mathf.Max(u, 0f) / 28f), 1f, Mathf.Clamp(Mathf.Abs(slipNow) / 0.3f, 0f, 1f));
        float delta = -_steer * s.MaxSteer * lockScale;   // +steer is right, + angle is left
        // Game: the fronts point part of the way down the direction of travel and lean against
        // the rotation, as a driver's hands would, so a drift is held rather than spun
        if (arcade && u > 3f)
            delta = Mathf.Clamp(delta + ArcadeAssist * slipNow - ArcadeYawDamp * motion.YawRate, -s.MaxSteer, s.MaxSteer);
        SteerAngle = delta;

        float powerScale = arcade ? ArcadePower : 1f;
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
            float torque = Rpm >= s.Redline ? 0f : s.TorqueAt(Rpm) * powerScale;
            float drive = _shiftTimer > 0 ? 0f : pedal * torque * ratio * Driveline / WheelRadius;
            if (reverse) drive = -drive;

            // --- load transfer from last substep's longitudinal acceleration ---
            float nf = m * (Gravity * b - AccelX * s.CgHeight) / L;
            float nr = m * Gravity - nf;
            nf = Mathf.Max(nf, 0.1f * m * Gravity);
            nr = Mathf.Max(nr, 0.1f * m * Gravity);

            // --- longitudinal forces per axle ---
            float sign = Mathf.Sign(u);
            // the car's own brakes (published 100-0 km/h), never more than the tyres can take
            float brakeForce = brake * m * Mathf.Min(s.BrakeDecel > 0 ? s.BrakeDecel * (arcade ? 1.1f : 1f) : 99f, grip * Gravity * 0.95f);
            float fxF = -sign * brakeForce * 0.65f;
            float fxR = -sign * brakeForce * 0.35f;
            if (input.Handbrake) fxR = -sign * 0.8f * grip * nr;
            switch (s.Drive)
            {
                case Drivetrain.All: fxF += drive * (1f - s.RearBias); fxR += drive * s.RearBias; break;
                case Drivetrain.Front: fxF += drive; break;
                default: fxR += drive; break;
            }
            // no force to push against below walking pace once stopped
            if (Mathf.Abs(u) < 0.3f && drive == 0f) { fxF = 0f; fxR = 0f; u = Mathf.MoveToward(u, 0f, 3f * h); }
            float capF = grip * nf, capR = grip * nr;
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
            float latF = Mathf.Sqrt(Mathf.Max(capF * capF - fxF * fxF, 0.01f * capF * capF));
            float latR = Mathf.Sqrt(Mathf.Max(capR * capR - fxR * fxR, 0.01f * capR * capR));
            if (input.Handbrake) latR *= HandbrakeGrip;
            // Game: once sideways, the gas keeps the rear sliding, whatever drives the wheels — so a
            // front-driver, a mid-engined car or a 90 hp roadster holds a drift on the throttle
            // exactly like the FR cars do. Sim leaves each car to its own layout and power.
            if (arcade)
                latR *= 1f - (s.Drive == Drivetrain.Front ? ArcadeSustainFf : ArcadeSustain) * pedal
                    // from 14°, not 7°: a bump in an ordinary bend reaches 7° and was tipping plain
                    // driving into a drift nobody asked for; a real drift passes 14° at once
                    * Mathf.Clamp((Mathf.Abs(slipNow) - 0.25f) / 0.2f, 0f, 1f);
            float fyF = -latF * Mathf.Sin(tyreC * Mathf.Atan(TyreB * alphaF));
            float fyR = -latR * Mathf.Sin(tyreC * Mathf.Atan(TyreB * alphaR));

            // --- resistances and gravity along the grade ---
            float drag = 0.5f * AirDensity * s.DragArea * u * Mathf.Abs(u);
            float roll = RollingResistance * m * Gravity * sign;
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
                if (excess != 0f) mz += m * a * b * (ArcadeCatch * excess - ArcadeCatchDamp * r);
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
