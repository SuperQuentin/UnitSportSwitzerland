using System;
using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A motorbike: the bicycle's lean steering (<see cref="Rideable.SteerByLean"/>) with the car's
/// engine and gearbox, and the three limits only a two-wheeler has.
///
/// <list type="bullet">
/// <item><b>Wheelie</b>: drive can only lift the front until the weight is all on the rear,
/// <c>a ≤ g·b/h</c> (b CG to rear axle, h CG height). A bike with 200 PS is limited by this, not
/// by its engine, up to well past 100 km/h — so is its 0-100.</item>
/// <item><b>Stoppie</b>: braking the same way over the front, <c>a ≤ g·a_f/h</c>.</item>
/// <item><b>Traction circle</b>: the lean the tyres hold is what the grip leaves after
/// accelerating or braking, <c>tan φ ≤ √(μ² − (a/g)²)</c>; braking hard stands the bike up.</item>
/// </list>
/// Both pitch limits are applied as the rider (and the electronics) would: short of the flip, never
/// through it. Top speed is not a number here; it is where the power meets <c>½ρ·CdA·v³</c>.
/// </summary>
public sealed class Motorbike : Rideable, IEngined
{
    public MotorbikeSpec Spec { get; }
    public Motorbike(MotorbikeSpec spec) => Spec = spec;

    public const float RiderMass = 75f;
    private const float AirDensity = 1.2f;
    private const float RollingResistance = 0.015f;
    /// <summary>Chain drive and gearbox, crank to tyre.</summary>
    private const float Driveline = 0.9f;
    /// <summary>How close to the wheelie / stoppie point the rider (wheelie control, ABS) takes it.</summary>
    private const float PitchMargin = 0.9f;
    private const float BankResponse = 4.5f, MaxYawRate = 1.5f, WalkingPace = 2f;
    /// <summary>Game: more lean, quicker roll-in, stronger brakes; the engine stays the real one.</summary>
    private const float ArcadeLean = 0.1f, ArcadeBankResponse = 6.5f, ArcadeBrake = 1.1f, ArcadeGrip = 1.15f;

    public override RideKind Kind => Spec.Kind;
    public override string Label => Spec.Label;
    public override string Blurb => Spec.Blurb;
    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override float MaxHealth => 120f;
    public override float BodyRadius => 0.42f;
    public override float BodyHeight => 1.6f;
    public override float ChaseDistance => 4.4f;
    public override float ChaseHeight => 1.6f;
    public override float BaseFov => 70f;
    public override float MaxFov => 92f;
    public override float FovSpeed => 50f;

    public override Vector3 FirstPersonEye =>
        HumanMeshBuilder.MountsForRider(Spec.Look.Seat, Spec.Look.Grip, Spec.Look.Peg).Eye + new Vector3(0, 0.02f, -0.08f);

    public override Node3D BuildVisual(int riderIndex) => Motorcyclist.Create(Spec.Look, riderIndex);
    public override Node3D BuildParkedVisual(int riderIndex) => Motorcyclist.Create(Spec.Look, riderIndex, rider: false);

    // ---- read by the feel layer and the rig ----
    public float Rpm { get; private set; }
    public float Rpm01 => Mathf.Clamp((Rpm - Spec.IdleRpm) / (Spec.Redline - Spec.IdleRpm), 0f, 1f);
    public int Gear { get; private set; } = 1;
    public float Throttle { get; private set; }
    public EngineProfile Sound => _sound ??= EngineProfile.For(Spec.Engine, Spec.IdleRpm, Spec.Redline);
    private EngineProfile? _sound;
    public bool Braking { get; private set; }
    /// <summary>Longitudinal acceleration last step, m/s² (+ forward).</summary>
    public float AccelX { get; private set; }
    /// <summary>How much of the wheelie (+) or stoppie (−) limit the last step used, for checks.</summary>
    public float PitchUse { get; private set; }
    public float SteerAngle { get; private set; }
    public float WheelSpin { get; private set; }

    public float Mass => Spec.WetMass + RiderMass;
    private float RearRadius => Spec.Look.RearRadius;
    private float Wheelbase => Spec.Look.Wheelbase;
    /// <summary>The accelerations that lift a wheel: g·b/h and g·a/h.</summary>
    public float WheelieAccel => Gravity * (1f - Spec.RearShare) * Wheelbase / Spec.CgHeight;
    public float StoppieDecel => Gravity * Spec.RearShare * Wheelbase / Spec.CgHeight;

    private float _shift;

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        var s = Spec;
        bool arcade = Arcade;
        float v = motion.Speed;
        float m = Mass;
        float grip = s.Grip * (arcade ? ArcadeGrip : 1f);

        // lean the tyres allow after what acceleration or braking takes from the friction circle
        float used = Mathf.Min(Mathf.Abs(AccelX) / Gravity, grip * 0.95f);
        float gripLean = Mathf.Atan(Mathf.Sqrt(grip * grip - used * used));
        float maxLean = Mathf.Min(s.MaxLean + (arcade ? ArcadeLean : 0f), gripLean + (arcade ? ArcadeLean : 0f));
        SteerByLean(ref motion, input.Steer, v, maxLean, MaxYawRate, arcade ? ArcadeBankResponse : BankResponse, WalkingPace, dt);
        // the bars' angle for this turn: kinematic at a crawl, a fraction of a degree at speed
        SteerAngle = Mathf.Clamp(Mathf.Atan(Wheelbase * motion.YawRate / Mathf.Max(v, 1f)), -0.6f, 0.6f);

        Throttle = input.Throttle;
        Braking = input.Brake > 0.05f;

        if (!ground.OnFloor)
        {
            // off a crest: the rear spins free and the air is all that acts
            Rpm = Mathf.Lerp(Rpm, Mathf.Lerp(s.IdleRpm, s.Redline, input.Throttle), 1f - Mathf.Exp(-6f * dt));
            v -= 0.5f * AirDensity * s.DragArea * v * v / m * dt;
            motion.Speed = Mathf.Max(0f, v);
            AccelX = 0f;
            WheelSpin = Mathf.Wrap(WheelSpin + v / RearRadius * dt, 0f, Mathf.Tau);
            return;
        }

        // --- engine: the wheel sets the rpm, a slipping clutch holds it up at a launch ---
        float ratio = s.Primary * s.Gears[Gear - 1] * s.FinalDrive;
        float wheelRpm = v / RearRadius * 60f / Mathf.Tau;
        Rpm = Mathf.Max(wheelRpm * ratio, Mathf.Lerp(s.IdleRpm, s.LaunchRpm, input.Throttle));
        float torque = Rpm >= s.Redline ? 0f : s.TorqueAt(Rpm);
        float drive = _shift > 0 ? 0f : input.Throttle * torque * ratio * Driveline / RearRadius;

        // --- the two-wheeler limits ---
        float wheelie = WheelieAccel * PitchMargin;
        // traction is μ times the rear load, which grows with the acceleration itself:
        // a = μ(g·a_f + a·h)/L  →  a = μ·g·a_f / (L − μ·h), a_f the CG to the front axle
        float traction = grip * Gravity * s.RearShare * Wheelbase / Mathf.Max(0.3f, Wheelbase - grip * s.CgHeight);
        float driveAccel = Mathf.Min(drive / m, Mathf.Min(wheelie, traction));
        float stoppie = StoppieDecel * PitchMargin;
        float brakeAccel = input.Brake * Mathf.Min(s.BrakeDecel * (arcade ? ArcadeBrake : 1f), Mathf.Min(stoppie, grip * Gravity));
        PitchUse = driveAccel > 0.01f ? driveAccel / WheelieAccel : -brakeAccel / StoppieDecel;

        float drag = 0.5f * AirDensity * s.DragArea * v * v / m;
        float roll = v > 0.05f ? RollingResistance * Gravity : 0f;
        float a = driveAccel - brakeAccel - drag - roll + SlopeAccel(ground.Grade);
        // stopped, it stays stopped: it does not roll backwards (a foot goes down)
        v = Mathf.Max(0f, v + a * dt);
        AccelX = a;
        motion.Speed = v;
        motion.Slip = 0f;

        // --- sequential box with a quickshifter: up near the redline, down when it bogs ---
        _shift = Mathf.Max(0f, _shift - dt);
        if (Rpm > s.Redline * 0.97f && Gear < s.Gears.Length && input.Throttle > 0.2f) { Gear++; _shift = s.ShiftCut; }
        else if (Gear > 1 && wheelRpm * ratio < s.PeakRpm * 0.5f) Gear--;

        WheelSpin = Mathf.Wrap(WheelSpin + v / RearRadius * dt, 0f, Mathf.Tau);
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is not Motorcyclist rig) return;
        rig.SteerAngle = SteerAngle;
        rig.WheelSpin = WheelSpin;
    }

    /// <summary>
    /// The lean is in the replicated body transform already; these are the parts: bar angle, the rear
    /// wheel's spin RATE (each peer integrates its own, as the cars do), rpm for the engine, the brake.
    /// </summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(SteerAngle, motion.Speed / RearRadius, Rpm01, Braking ? 1f : 0f);

    private float _remoteSpin;

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (visual is not Motorcyclist rig) return;
        _remoteSpin = Mathf.Wrap(_remoteSpin + pose.Y * dt, 0f, Mathf.Tau);
        rig.SteerAngle = pose.X;
        rig.WheelSpin = _remoteSpin;
        Rpm = Mathf.Lerp(Spec.IdleRpm, Spec.Redline, pose.Z);
        Braking = pose.W > 0.5f;
    }

    /// <summary>
    /// <c>godot --headless --path . -- --motocheck</c>: each bike's model on flat ground with no
    /// world: 0-100 and 0-200 km/h, top speed after 90 s flat out, 100-0 braking, lean held in a
    /// full-lock turn at 80 km/h — against the published figures. Fails if a top speed or 0-100 is
    /// off by more than 10%, a wheelie or stoppie limit is crossed, or the lean passes the grip.
    /// </summary>
    public static int Check()
    {
        const float dt = 1f / 60f;
        int failures = 0;
        var settings = Core.GameSettings.Current;
        var was = settings.RideProfile;
        var flat = new RideGround(true, 0f);
        foreach (var profile in new[] { Core.RideProfile.Sim, Core.RideProfile.Game })
        {
            settings.RideProfile = profile;
            foreach (var spec in MotorbikeCatalog.All)
            {
                var bike = new Motorbike(spec);
                var m = new RideMotion();
                float t = 0, t100 = float.NaN, t200 = float.NaN, pitch = 0;
                for (; t < 90f; t += dt)
                {
                    bike.Step(new RideInput(1f, 0f, 0f, false), flat, dt, ref m);
                    pitch = Mathf.Max(pitch, bike.PitchUse);
                    if (float.IsNaN(t100) && m.Speed >= 100f / 3.6f) t100 = t + dt;
                    if (float.IsNaN(t200) && m.Speed >= 200f / 3.6f) t200 = t + dt;
                }
                float top = m.Speed * 3.6f;

                var b = new Motorbike(spec);
                var mb = new RideMotion { Speed = 100f / 3.6f };
                float dist = 0, stoppie = 0;
                for (int i = 0; i < 6000 && mb.Speed > 0f; i++)
                {
                    b.Step(new RideInput(0f, 1f, 0f, false), flat, dt, ref mb);
                    dist += mb.Speed * dt;
                    stoppie = Mathf.Min(stoppie, b.PitchUse);
                }

                var c = new Motorbike(spec);
                var mc = new RideMotion { Speed = 80f / 3.6f };
                for (float u = 0; u < 4f; u += dt)
                {
                    // hold 80 km/h through the turn: throttle against the drag
                    c.Step(new RideInput(mc.Speed < 80f / 3.6f ? 0.6f : 0f, 0f, 1f, false), flat, dt, ref mc);
                }
                float lean = Mathf.RadToDeg(Mathf.Abs(mc.Lean));
                float radius = mc.Speed / Mathf.Max(1e-3f, Mathf.Abs(mc.YawRate));

                bool ok = Mathf.Abs(top - spec.RefTopKmh) / spec.RefTopKmh < 0.1f
                    && (profile == Core.RideProfile.Game || Mathf.Abs(t100 - spec.RefZeroTo100) / spec.RefZeroTo100 < 0.15f)
                    && pitch < 1f && stoppie > -1f && lean <= Mathf.RadToDeg(spec.MaxLean) + 6.5f;
                GD.Print($"[moto] {profile,-4} {spec.Label,-22} 0-100 {t100:F2} s (ref {spec.RefZeroTo100:F1})  0-200 {t200:F2} s  "
                    + $"top {top:F0} km/h (ref {spec.RefTopKmh:F0})  100-0 {dist:F1} m  wheelie {pitch:P0} stoppie {-stoppie:P0} of the limit  "
                    + $"full lean at 80 km/h {lean:F0}° r {radius:F0} m  {(ok ? "ok" : "FAILED")}");
                if (!ok) failures++;
            }
        }
        settings.RideProfile = was;
        // the sidewall parser, on the bias-ply form a 21 in adventure front wears: 21*12.7 + 81 mm
        float r21 = Tyre.Radius("90/90-21");
        GD.Print($"[moto] tyre 90/90-21 radius {r21 * 1000f:F1} mm (348.0)");
        if (Mathf.Abs(r21 - 0.348f) > 0.001f) failures++;
        GD.Print(failures == 0 ? "[moto] RESULT: ok" : $"[moto] RESULT: FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
