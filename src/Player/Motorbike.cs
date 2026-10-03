using System;
using System.Linq;
using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;
using UnitSport.Core;

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
/// through it — unless the rider pulls a <b>wheelie</b> (#410): holding {tuck_boost} pulls back on the
/// bars and turns the wheelie control off. The front then lifts wherever the drive passes
/// <c>g·b/h</c>, and a tap of it on the throttle pops the clutch, a kick that hops even a 42 PS bike
/// up. The bike then pivots on its rear contact, <see cref="Pitch"/>, with throttle lifting it and the
/// rear brake bringing it down; past the balance point (CG over the rear contact) it loops out and
/// throws the rider (Sim). In Game the throttle sets a wheelie angle and the drive holds it there.
/// Top speed is not a number here; it is where the power meets <c>½ρ·CdA·v³</c>.
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
    /// <summary>How far back the rider shifts the CG pulling a wheelie, m.</summary>
    private const float PullBack = 0.05f;
    /// <summary>The bike's own radius of gyration about its CG, m, on top of the CG's lever about the rear contact.</summary>
    private const float Gyration = 0.32f;
    /// <summary>Pitch rate a clutch pop at full drive gives, rad/s (a kick of drive for a fraction of a second).</summary>
    private const float ClutchPop = 2f;
    /// <summary>Past the balance point by this much, rad, it is over: the rider is thrown off the back.</summary>
    private const float LoopOutPast = 0.3f;
    /// <summary>Rear brake alone, m/s²: all there is with the front in the air.</summary>
    private const float RearBrakeDecel = 4.5f;

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

    public override Node3D BuildVisual(int riderIndex, Avatar.Outfit outfit = default) => Motorcyclist.Create(Spec.Look, riderIndex, outfit: outfit);
    public override Node3D BuildParkedVisual(int riderIndex) => Motorcyclist.Create(Spec.Look, riderIndex, rider: false);

    /// <summary>The rider's place and a pillion's (#158).</summary>
    public override SeatAnchor[] Seats => SeatsOf(Kind, () => Motorcyclist.SeatsFor(Spec.Look));

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
    /// <summary>Front wheel up about the rear contact, radians (0 on both wheels). Replicated in the pose.</summary>
    public float Pitch { get; private set; }
    public float PitchRate { get; private set; }
    /// <summary>The highest <see cref="Pitch"/> since mounting (checks).</summary>
    public float MaxPitch { get; private set; }
    /// <summary>Set when a wheelie goes over the top; the rider reads it and is thrown off the back.</summary>
    public bool LoopedOut { get; private set; }
    /// <summary>The pitch at which the CG stands over the rear contact (with the rider pulled back).</summary>
    public float BalancePitch => Mathf.Atan2((1f - Spec.RearShare) * Wheelbase - PullBack, Spec.CgHeight);
    private bool _pulled;

    public float Mass => Spec.WetMass + RiderMass;
    private float RearRadius => Spec.Look.RearRadius;
    private float Wheelbase => Spec.Look.Wheelbase;
    /// <summary>The accelerations that lift a wheel: g·b/h and g·a/h.</summary>
    public float WheelieAccel => Gravity * (1f - Spec.RearShare) * Wheelbase / Spec.CgHeight;
    public float StoppieDecel => Gravity * Spec.RearShare * Wheelbase / Spec.CgHeight;

    private float _shift, _sinceShift;
    /// <summary>The CVT's ratio now, between its high and low.</summary>
    private float _cvt = float.MaxValue;

    /// <summary>What the wheels were on last step.</summary>
    public Surface Surface { get; private set; } = Surface.Asphalt;

    /// <summary>
    /// Peak friction on <paramref name="surface"/> for a tyre with <paramref name="tarmacGrip"/> on
    /// tarmac. Off tarmac the ground, not the rubber, sets the limit: a road tyre gets 0.65 on gravel
    /// or a dirt road and 0.5 on grass, whatever its compound; an adventure tyre's tread claws back
    /// <paramref name="offroadTyre"/> of the gap to 1 (0 = road tyre), never more than it grips on tarmac.
    /// </summary>
    public static float SurfaceGrip(Surface surface, float tarmacGrip, float offroadTyre)
    {
        float loose = surface switch
        {
            Surface.Gravel => 0.65f,
            Surface.Rock => 0.8f,
            Surface.Grass or Surface.Forest => 0.5f,
            Surface.Water => 0.4f,
            Surface.Snow => 0.3f,
            Surface.Ice => 0.1f,
            _ => float.NaN,   // asphalt, wood, indoor: the tyre's own tarmac grip
        };
        if (float.IsNaN(loose)) return tarmacGrip;
        return Mathf.Min(tarmacGrip, loose + (1f - loose) * Mathf.Clamp(offroadTyre, 0f, 1f));
    }

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        var s = Spec;
        bool arcade = Arcade;
        float v = motion.Speed;
        float m = Mass;
        float grip = SurfaceGrip(ground.Surface, s.Grip, s.OffroadTyre) * (arcade ? ArcadeGrip : 1f);
        Surface = ground.Surface;

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
            // off a crest: the rear spins free and the air is all that acts; a wheelie settles
            // (the airborne trick pitch is the rider's, FootPlayer)
            Pitch = Mathf.MoveToward(Pitch, 0f, 1.5f * dt);
            PitchRate = 0f;
            Rpm = Mathf.Lerp(Rpm, Mathf.Lerp(s.IdleRpm, s.Redline, input.Throttle), MathX.Damp(6f, dt));
            v -= 0.5f * AirDensity * s.DragArea * v * v / m * dt;
            motion.Speed = Mathf.Max(0f, v);
            AccelX = 0f;
            WheelSpin = Mathf.Wrap(WheelSpin + v / RearRadius * dt, 0f, Mathf.Tau);
            return;
        }

        // --- engine: the wheel sets the rpm, a slipping clutch holds it up at a launch ---
        float wheelRpm = v / RearRadius * 60f / Mathf.Tau;
        if (s.Cvt)
        {
            // the variator holds the engine at the rpm the throttle asks for, within its range
            float hold = Mathf.Lerp(s.LaunchRpm, s.CvtRpm, input.Throttle);
            _cvt = Mathf.Clamp(hold / Mathf.Max(1f, wheelRpm * s.Primary * s.FinalDrive), s.CvtHigh, s.Gears[0]);
        }
        float ratio = s.Primary * (s.Cvt ? _cvt : s.Gears[Gear - 1]) * s.FinalDrive;
        Rpm = Mathf.Max(wheelRpm * ratio, Mathf.Lerp(s.IdleRpm, s.LaunchRpm, input.Throttle));
        float torque = Rpm >= s.Redline ? 0f : s.TorqueAt(Rpm);
        float drive = _shift > 0 ? 0f : input.Throttle * torque * ratio * Driveline / RearRadius;

        // --- the two-wheeler limits ---
        // pulling back on the bars (#410) turns the wheelie control off: the drive is held by traction alone
        bool pull = input.Effort;
        float wheelie = pull ? float.MaxValue : WheelieAccel * PitchMargin;
        // traction is μ times the rear load, which grows with the acceleration itself:
        // a = μ(g·a_f + a·h)/L  →  a = μ·g·a_f / (L − μ·h), a_f the CG to the front axle;
        // never more than the whole weight on the rear, μ·g, which is what it has with the front up
        float traction = Mathf.Min(grip * Gravity, grip * Gravity * s.RearShare * Wheelbase / Mathf.Max(0.3f, Wheelbase - grip * s.CgHeight));
        float driveAccel = Mathf.Min(drive / m, Mathf.Min(wheelie, traction));
        float stoppie = StoppieDecel * PitchMargin;
        // with the front in the air only the rear brake does anything
        float brakeAccel = Pitch > 0f
            ? input.Brake * Mathf.Min(RearBrakeDecel, grip * Gravity)
            : input.Brake * Mathf.Min(s.BrakeDecel * (arcade ? ArcadeBrake : 1f), Mathf.Min(stoppie, grip * Gravity));

        float drag = 0.5f * AirDensity * s.DragArea * v * v / m * (1f - ground.Draft);
        float roll = v > 0.05f ? RollingResistance * Gravity : 0f;
        float resist = drag + roll - SlopeAccel(ground.Grade);

        // --- the wheelie: the bike pivots on its rear contact ---
        // CG b ahead of and h above the rear contact; pitched by θ it sits at angle (α₀ − θ) from
        // vertical, α₀ = atan(b/h). The contact accelerating at a gives, per unit mass,
        // θ'' = (a·h' − g·b') / (r² + k²), h' = r·cos(α₀ − θ), b' = r·sin(α₀ − θ).
        float b = (1f - s.RearShare) * Wheelbase - (pull ? PullBack : 0f);
        float h = s.CgHeight;
        float r2 = b * b + h * h, r = Mathf.Sqrt(r2), alpha0 = Mathf.Atan2(b, h);
        float inertia = r2 + Gyration * Gyration;
        float lever = alpha0 - Pitch;
        float hp = r * Mathf.Cos(lever), bp = r * Mathf.Sin(lever);
        // a clutch pop: pulling with the throttle open kicks the front up, in proportion to the drive on offer
        if (pull && !_pulled && input.Throttle > 0.5f && _shift <= 0f)
            PitchRate += ClutchPop * Mathf.Clamp(input.Throttle * torque * ratio * Driveline / RearRadius / (m * WheelieAccel), 0.6f, 1.2f);
        _pulled = pull;
        if (arcade && pull && Pitch > 0f)
        {
            // Game: the throttle picks a wheelie angle and the drive holds it there (a PD on the
            // pitch), with the whole drive the gear has on offer; the brake still brings it down
            float target = alpha0 * Mathf.Lerp(0.25f, 0.85f, input.Throttle);
            float want = 6f * (target - Pitch) - 3.5f * PitchRate;
            float hold = (want * inertia + Gravity * bp) / Mathf.Max(0.1f, hp) + brakeAccel + resist;
            // (a shift's drive cut is let off: Game wheelies carry through the gears)
            float full = Rpm >= s.Redline ? 0f : s.TorqueAt(Mathf.Max(wheelRpm * ratio, s.LaunchRpm)) * ratio * Driveline / RearRadius;
            driveAccel = Mathf.Clamp(hold, 0f, Mathf.Min(full / m, traction));
        }
        else if (!pull && Pitch > 0f)
        {
            // let go: the wheelie control (or the rider's wrist) eases the drive until the front
            // comes down — g·b'/h' holds it where it is, so well under that
            driveAccel = Mathf.Min(driveAccel, 0.6f * Gravity * bp / Mathf.Max(0.1f, hp));
        }
        PitchUse = driveAccel > 0.01f ? driveAccel / WheelieAccel : -brakeAccel / StoppieDecel;
        float a = driveAccel - brakeAccel - resist;
        float pitchAccel = (a * hp - Gravity * bp) / inertia;
        if (LoopedOut) { }   // on its back: nothing more to integrate, the rider is being thrown
        else if (Pitch > 0f || PitchRate > 0f || pitchAccel > 0f)
        {
            PitchRate += pitchAccel * dt;
            Pitch += PitchRate * dt;
            if (Pitch <= 0f) { Pitch = 0f; PitchRate = 0f; }   // the front wheel comes down
        }
        MaxPitch = Mathf.Max(MaxPitch, Pitch);
        // Game never goes over: it stops at the balance point; Sim loops out a little past it
        if (arcade && Pitch > alpha0) { Pitch = alpha0; PitchRate = Mathf.Min(PitchRate, 0f); }
        else if (Pitch > alpha0 + LoopOutPast) LoopedOut = true;

        // stopped, it stays stopped: it does not roll backwards (a foot goes down)
        v = Mathf.Max(0f, v + a * dt);
        AccelX = a;
        motion.Speed = v;
        motion.Slip = 0f;

        _shift = Mathf.Max(0f, _shift - dt);
        _sinceShift += dt;
        if (s.Cvt) { }   // nothing to shift
        else if (s.Dct) ShiftDct(wheelRpm, input.Throttle);
        // --- sequential box with a quickshifter: up near the redline, down when it bogs ---
        else if (Rpm > s.Redline * 0.97f && Gear < s.Gears.Length && input.Throttle > 0.2f) { Gear++; _shift = s.ShiftCut; }
        else if (Gear > 1 && wheelRpm * ratio < s.PeakRpm * 0.5f) Gear--;

        WheelSpin = Mathf.Wrap(WheelSpin + v / RearRadius * dt, 0f, Mathf.Tau);
    }

    /// <summary>
    /// Honda's DCT in D: the up-shift point rides on the throttle — early and short-shifted when
    /// cruising, near the redline flat out — and it down-shifts as the rpm sags or on a kick-down.
    /// Neither shift is taken if the rpm it lands on would call straight for the other (no hunting),
    /// and a shift holds for 0.6 s. No clutch, no cut: the next gear is already engaged.
    /// </summary>
    private void ShiftDct(float wheelRpm, float throttle)
    {
        var s = Spec;
        if (_sinceShift < 0.6f) return;
        float up = Mathf.Lerp(0.42f, 0.97f, throttle * throttle) * s.Redline;
        float down = Mathf.Lerp(0.27f, 0.55f, throttle) * s.PeakRpm;
        float RpmIn(int gear) => wheelRpm * s.Primary * s.Gears[gear - 1] * s.FinalDrive;
        if (Gear < s.Gears.Length && RpmIn(Gear) > up && RpmIn(Gear + 1) > down * 1.15f)
        { Gear++; _shift = s.ShiftCut; _sinceShift = 0f; }
        else if (Gear > 1 && RpmIn(Gear) < down && RpmIn(Gear - 1) < up * 0.9f)
        { Gear--; _sinceShift = 0f; }
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is not Motorcyclist rig) return;
        rig.SteerAngle = SteerAngle;
        rig.WheelSpin = WheelSpin;
        rig.Pitch = Pitch;
    }

    /// <summary>
    /// The lean is in the replicated body transform already; these are the parts: bar angle, the rear
    /// wheel's spin RATE (each peer integrates its own, as the cars do), rpm for the engine, the brake
    /// and the wheelie pitch together in w: the pitch, plus <see cref="BrakeFlag"/> while braking.
    /// </summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(SteerAngle, motion.Speed / RearRadius, Rpm01, Pitch + (Braking ? BrakeFlag : 0f));

    /// <summary>Added to the pitch in the pose's w while braking; a pitch never reaches it.</summary>
    private const float BrakeFlag = 10f;

    private float _remoteSpin;

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (visual is not Motorcyclist rig) return;
        _remoteSpin = Mathf.Wrap(_remoteSpin + pose.Y * dt, 0f, Mathf.Tau);
        rig.SteerAngle = pose.X;
        rig.WheelSpin = _remoteSpin;
        Rpm = Mathf.Lerp(Spec.IdleRpm, Spec.Redline, pose.Z);
        Braking = pose.W >= BrakeFlag * 0.5f;
        Pitch = Braking ? pose.W - BrakeFlag : pose.W;
        rig.Pitch = Pitch;
    }

    /// <summary>
    /// <c>godot --headless --path . -- --motocheck [N,M,...]</c>: each bike's model (or catalog
    /// entries N, M...) on flat ground with no world: 0-100 and 0-200 km/h, top speed after 90 s
    /// flat out, 100-0 braking, lean held in a full-lock turn at 80 km/h — against the published
    /// figures where there are measured ones. Fails if a top speed is off by more than 10% (0-100:
    /// 15%), a wheelie or stoppie limit is crossed, the lean passes the grip, or a DCT hunts
    /// (down-shifts flat out, or reverses a shift at a steady part throttle). Sim also
    /// runs 0-100 and the 80 km/h lean on gravel and on grass.
    /// </summary>
    public static int Check()
    {
        int failures = 0;
        var settings = Core.GameSettings.Current;
        var was = settings.RideProfile;
        var only = CmdArgs.Value("--motocheck", notFlag: true) is { } list
            ? list.Split(',').Select(int.Parse).ToHashSet() : null;
        foreach (var profile in new[] { Core.RideProfile.Sim, Core.RideProfile.Game })
        {
            settings.RideProfile = profile;
            for (int n = 0; n < MotorbikeCatalog.All.Count; n++)
            {
                if (only != null && !only.Contains(n)) continue;
                var spec = MotorbikeCatalog.All[n];
                var run = Launch(spec, Surface.Asphalt);
                var (dist, stoppie) = Stop(spec);
                var (lean, radius) = Lean(spec, Surface.Asphalt);
                int hunts = spec.Dct ? Cruise(spec) : 0;

                bool ok = (spec.RefTopKmh <= 0 || Mathf.Abs(run.Top - spec.RefTopKmh) / spec.RefTopKmh < 0.1f)
                    && (profile == Core.RideProfile.Game || spec.RefZeroTo100 <= 0 || Mathf.Abs(run.T100 - spec.RefZeroTo100) / spec.RefZeroTo100 < 0.15f)
                    && run.Pitch < 1f && stoppie > -1f && lean <= Mathf.RadToDeg(spec.MaxLean) + 6.5f
                    && run.Downs == 0 && hunts == 0;
                string Ref(float v, string f) => v > 0 ? v.ToString(f, System.Globalization.CultureInfo.InvariantCulture) : "-";
                GD.Print($"[moto] {profile,-4} {n,2} {spec.Label}");
                GD.Print($"[moto]        0-100 {run.T100:F2} s (ref {Ref(spec.RefZeroTo100, "F2")})  0-200 {run.T200:F2} s  "
                    + $"top {run.Top:F0} km/h (ref {Ref(spec.RefTopKmh, "F0")})  100-0 {dist:F1} m  wheelie {run.Pitch:P0} stoppie {-stoppie:P0} of the limit  "
                    + $"lean at 80 km/h {lean:F0}° r {radius:F0} m  shifts {run.Ups} up {run.Downs} down"
                    + (spec.Dct ? $", {hunts} reversals at 35% throttle" : "") + $"  {(ok ? "ok" : "FAILED")}");
                if (!ok) failures++;

                // wheelies (#410): a clutch pop lifts every bike's front; Game holds one at part throttle
                var w = Wheelie(spec);
                bool wOk = w.Pop > Mathf.DegToRad(3f) && (profile == Core.RideProfile.Sim || (w.Held && !w.Looped));
                GD.Print($"[moto]        wheelie: pop {Mathf.RadToDeg(w.Pop):F0}°, held at 60% {(w.Held ? "yes" : "no")} (max {Mathf.RadToDeg(w.Max):F0}°, "
                    + $"balance {Mathf.RadToDeg(w.Balance):F0}°{(w.Looped ? ", looped out" : "")}), power wheelie flat out {Mathf.RadToDeg(w.Power):F0}°"
                    + $"{(w.PowerLooped ? " looped out" : "")}  {(wOk ? "ok" : "FAILED")}");
                if (!wOk) failures++;

                if (profile != Core.RideProfile.Sim) continue;
                foreach (var ground in new[] { Surface.Gravel, Surface.Grass })
                {
                    var off = Launch(spec, ground, 30f);
                    var (offLean, offRadius) = Lean(spec, ground);
                    // lower but controllable: less than on tarmac, still some
                    bool offOk = offLean < lean && offLean > 15f && off.Pitch < 1f && !float.IsNaN(off.T100);
                    GD.Print($"[moto]        {ground,-6} μ {SurfaceGrip(ground, spec.Grip, spec.OffroadTyre):F2}  0-100 {off.T100:F2} s  "
                        + $"lean at 80 km/h {offLean:F0}° r {offRadius:F0} m  {(offOk ? "ok" : "FAILED")}");
                    if (!offOk) failures++;
                }
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

    private const float CheckDt = 1f / 60f;

    /// <summary>Flat out from rest: 0-100, 0-200, top speed, worst wheelie use, gear changes.</summary>
    private static (float T100, float T200, float Top, float Pitch, int Ups, int Downs) Launch(MotorbikeSpec spec, Surface surface, float seconds = 90f)
    {
        var bike = new Motorbike(spec);
        var m = new RideMotion();
        var ground = new RideGround(true, 0f, surface);
        float t100 = float.NaN, t200 = float.NaN, pitch = 0;
        int ups = 0, downs = 0, gear = 1;
        for (float t = 0; t < seconds; t += CheckDt)
        {
            bike.Step(new RideInput(1f, 0f, 0f, false), ground, CheckDt, ref m);
            pitch = Mathf.Max(pitch, bike.PitchUse);
            if (bike.Gear > gear) ups++; else if (bike.Gear < gear) downs++;
            gear = bike.Gear;
            if (float.IsNaN(t100) && m.Speed >= 100f / 3.6f) t100 = t + CheckDt;
            if (float.IsNaN(t200) && m.Speed >= 200f / 3.6f) t200 = t + CheckDt;
        }
        return (t100, t200, m.Speed * 3.6f, pitch, ups, downs);
    }

    /// <summary>
    /// Wheelies at 25 km/h in first: the pitch a clutch pop at 60% throttle reaches (front down
    /// again before the next); then pop and hold the pull at 60% for 5 s — still up at the end?
    /// (max pitch, loop-out); then flat out with the pull held for 3 s from 25 km/h (power wheelie).
    /// </summary>
    private static (float Pop, bool Held, float Max, bool Looped, float Balance, float Power, bool PowerLooped) Wheelie(MotorbikeSpec spec)
    {
        var flat = new RideGround(true, 0f);
        (Motorbike Bike, RideMotion Motion) Rolling()
        {
            var bike = new Motorbike(spec);
            var motion = new RideMotion { Speed = 25f / 3.6f };
            bike.Step(new RideInput(0f, 0f, 0f, false), flat, CheckDt, ref motion);
            return (bike, motion);
        }

        var (a, ma) = Rolling();
        for (float t = 0; t < 2f && !a.LoopedOut; t += CheckDt) a.Step(new RideInput(0.6f, 0f, 0f, t < 0.1f), flat, CheckDt, ref ma);
        var (b, mb) = Rolling();
        for (float t = 0; t < 5f && !b.LoopedOut; t += CheckDt) b.Step(new RideInput(0.6f, 0f, 0f, true), flat, CheckDt, ref mb);
        var (c, mc) = Rolling();
        for (float t = 0; t < 3f && !c.LoopedOut; t += CheckDt) c.Step(new RideInput(1f, 0f, 0f, true), flat, CheckDt, ref mc);
        return (a.MaxPitch, b.Pitch > 0.1f, b.MaxPitch, b.LoopedOut, b.BalancePitch, c.MaxPitch, c.LoopedOut);
    }

    /// <summary>100-0 km/h on tarmac: distance and worst stoppie use (negative).</summary>
    private static (float Dist, float Stoppie) Stop(MotorbikeSpec spec)
    {
        var b = new Motorbike(spec);
        var mb = new RideMotion { Speed = 100f / 3.6f };
        var flat = new RideGround(true, 0f);
        float dist = 0, stoppie = 0;
        for (int i = 0; i < 6000 && mb.Speed > 0f; i++)
        {
            b.Step(new RideInput(0f, 1f, 0f, false), flat, CheckDt, ref mb);
            dist += mb.Speed * CheckDt;
            stoppie = Mathf.Min(stoppie, b.PitchUse);
        }
        return (dist, stoppie);
    }

    /// <summary>Full lock at 80 km/h for 4 s, holding the speed: lean held (degrees) and radius.</summary>
    private static (float Lean, float Radius) Lean(MotorbikeSpec spec, Surface surface)
    {
        var c = new Motorbike(spec);
        var mc = new RideMotion { Speed = 80f / 3.6f };
        var ground = new RideGround(true, 0f, surface);
        for (float u = 0; u < 4f; u += CheckDt)
            c.Step(new RideInput(mc.Speed < 80f / 3.6f ? 0.6f : 0f, 0f, 1f, false), ground, CheckDt, ref mc);
        return (Mathf.RadToDeg(Mathf.Abs(mc.Lean)), mc.Speed / Mathf.Max(1e-3f, Mathf.Abs(mc.YawRate)));
    }

    /// <summary>A DCT pulling away at a steady 35% throttle for a minute: shifts that reverse the previous one (0 = no hunting).</summary>
    private static int Cruise(MotorbikeSpec spec)
    {
        var bike = new Motorbike(spec);
        var m = new RideMotion();
        var flat = new RideGround(true, 0f);
        int reversals = 0, gear = 1, last = 0;
        for (float t = 0; t < 60f; t += CheckDt)
        {
            bike.Step(new RideInput(0.35f, 0f, 0f, false), flat, CheckDt, ref m);
            int dir = System.Math.Sign(bike.Gear - gear);
            if (dir != 0) { if (last != 0 && dir != last) reversals++; last = dir; }
            gear = bike.Gear;
        }
        return reversals;
    }
}
