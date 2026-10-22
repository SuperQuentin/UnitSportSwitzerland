using Godot;

namespace UnitSport.Player;

/// <summary>
/// The bicycle's own parameters in the Whipple model's terms (#1124): Meijaard, Papadopoulos,
/// Ruina &amp; Schwab, "Linearized dynamics equations for the balance and steer of a bicycle: a
/// benchmark and review", Proc. R. Soc. A 463 (2007), table 1. Axes as the paper's: x forward from
/// the rear contact, z down (so heights are negative), the rear body B (frame and rider), the front
/// assembly H (fork and bars), the wheels R and F. SI units.
/// </summary>
public sealed record WhippleParameters(
    float Wheelbase, float Trail, float SteerAxisTilt,
    float RearRadius, float RearMass, float RearIxx, float RearIyy,
    float BodyX, float BodyZ, float BodyMass, float BodyIxx, float BodyIxz, float BodyIzz,
    float FrontX, float FrontZ, float FrontMass, float FrontIxx, float FrontIxz, float FrontIzz,
    float FrontRadius, float FrontWheelMass, float FrontWheelIxx, float FrontWheelIyy)
{
    /// <summary>The paper's benchmark bicycle: the matrices it publishes are the unit test's reference.</summary>
    public static readonly WhippleParameters Benchmark = new(
        Wheelbase: 1.02f, Trail: 0.08f, SteerAxisTilt: Mathf.Pi / 10f,
        RearRadius: 0.3f, RearMass: 2f, RearIxx: 0.0603f, RearIyy: 0.12f,
        BodyX: 0.3f, BodyZ: -0.9f, BodyMass: 85f, BodyIxx: 9.2f, BodyIxz: 2.4f, BodyIzz: 2.8f,
        FrontX: 0.9f, FrontZ: -0.7f, FrontMass: 4f, FrontIxx: 0.05892f, FrontIxz: -0.00756f, FrontIzz: 0.00708f,
        FrontRadius: 0.35f, FrontWheelMass: 3f, FrontWheelIxx: 0.1405f, FrontWheelIyy: 0.28f);

    /// <summary>The front assembly (fork, stem, bars) of a carbon road bike, kg.</summary>
    private const float ForkAndBars = 1.0f;
    private const float RearWheelMass = 1.5f, FrontWheelMassRoad = 1.1f;

    /// <summary>
    /// A real bike and rider in the paper's terms: the geometry from <paramref name="frame"/>, the
    /// rider's centre of mass from <see cref="BikeFrame.Balance"/>, and the body's inertia the
    /// benchmark's 85 kg rider-and-frame scaled by mass (same build, same height).
    /// </summary>
    public static WhippleParameters For(BikeFrame frame, float riderMass, float kit = 1.5f)
    {
        var spec = frame.Spec;
        float r = frame.WheelRadius;
        float rearIyy = spec.WheelInertia * 0.53f, frontIyy = spec.WheelInertia * 0.47f;
        float body = riderMass + kit + Mathf.Max(spec.Mass - RearWheelMass - FrontWheelMassRoad - ForkAndBars, 1f);
        var (_, toRear, height) = frame.Balance(riderMass);
        float scale = body / Benchmark.BodyMass;
        float tilt = Mathf.DegToRad(90f - spec.Geometry.HeadAngle);
        // the fork and bars' centre: halfway down the steerer from the stem to the axle
        var h = (frame.BarClamp + frame.FrontAxle) * 0.5f;
        float frontScale = ForkAndBars / Benchmark.FrontMass;
        return new WhippleParameters(
            Wheelbase: frame.Wheelbase, Trail: frame.Trail, SteerAxisTilt: tilt,
            RearRadius: r, RearMass: RearWheelMass, RearIxx: rearIyy / 2f, RearIyy: rearIyy,
            BodyX: toRear, BodyZ: -height, BodyMass: body,
            BodyIxx: Benchmark.BodyIxx * scale, BodyIxz: Benchmark.BodyIxz * scale, BodyIzz: Benchmark.BodyIzz * scale,
            FrontX: h.Z - frame.RearAxle.Z, FrontZ: -h.Y, FrontMass: ForkAndBars,
            FrontIxx: Benchmark.FrontIxx * frontScale, FrontIxz: Benchmark.FrontIxz * frontScale, FrontIzz: Benchmark.FrontIzz * frontScale,
            FrontRadius: r, FrontWheelMass: FrontWheelMassRoad, FrontWheelIxx: frontIyy / 2f, FrontWheelIyy: frontIyy);
    }
}

/// <summary>
/// The Whipple bicycle's linearized equations, <c>M·q̈ + v·C1·q̇ + (g·K0 + v²·K2)·q = f</c> over
/// roll φ and steer δ (Meijaard et al. 2007, appendix A). The matrices are what the bike is: its
/// geometry, trail and masses decide how it leans when steered, why it countersteers, why it wobbles
/// at walking pace and steadies at speed.
/// </summary>
public sealed class WhippleModel
{
    public const float Gravity = 9.81f;

    public float Mpp { get; }
    public float Mpd { get; }
    public float Mdd { get; }
    public float C1pd { get; }
    public float C1dp { get; }
    public float C1dd { get; }
    public float K0pp { get; }
    public float K0pd { get; }
    public float K0dd { get; }
    public float K2pd { get; }
    public float K2dd { get; }
    public float Wheelbase { get; }
    public float Trail { get; }
    public float CosTilt { get; }

    public WhippleModel(WhippleParameters p)
    {
        float w = p.Wheelbase, c = p.Trail, lam = p.SteerAxisTilt;
        float sin = Mathf.Sin(lam), cos = Mathf.Cos(lam);
        float rR = p.RearRadius, rF = p.FrontRadius;
        float mR = p.RearMass, mB = p.BodyMass, mH = p.FrontMass, mF = p.FrontWheelMass;
        float xB = p.BodyX, zB = p.BodyZ, xH = p.FrontX, zH = p.FrontZ;

        float mT = mR + mB + mH + mF;
        float xT = (xB * mB + xH * mH + w * mF) / mT;
        float zT = (-rR * mR + zB * mB + zH * mH - rF * mF) / mT;
        float iTxx = p.RearIxx + p.BodyIxx + p.FrontIxx + p.FrontWheelIxx + mR * rR * rR + mB * zB * zB + mH * zH * zH + mF * rF * rF;
        float iTxz = p.BodyIxz + p.FrontIxz - mB * xB * zB - mH * xH * zH + mF * w * rF;
        // the wheels are symmetric discs: their z inertia is their x inertia
        float iTzz = p.RearIxx + p.BodyIzz + p.FrontIzz + p.FrontWheelIxx + mB * xB * xB + mH * xH * xH + mF * w * w;

        float mA = mH + mF;
        float xA = (xH * mH + w * mF) / mA;
        float zA = (zH * mH - rF * mF) / mA;
        float iAxx = p.FrontIxx + p.FrontWheelIxx + mH * (zH - zA) * (zH - zA) + mF * (rF + zA) * (rF + zA);
        float iAxz = p.FrontIxz - mH * (xH - xA) * (zH - zA) + mF * (w - xA) * (rF + zA);
        float iAzz = p.FrontIzz + p.FrontWheelIxx + mH * (xH - xA) * (xH - xA) + mF * (w - xA) * (w - xA);
        float uA = (xA - w - c) * cos - zA * sin;
        float iAll = mA * uA * uA + iAxx * sin * sin + 2f * iAxz * sin * cos + iAzz * cos * cos;
        float iAlx = -mA * uA * zA + iAxx * sin + iAxz * cos;
        float iAlz = mA * uA * xA + iAxz * sin + iAzz * cos;
        float mu = c / w * cos;
        float sR = p.RearIyy / rR, sF = p.FrontWheelIyy / rF, sT = sR + sF;
        float sA = mA * uA + mu * mT * xT;

        Mpp = iTxx;
        Mpd = iAlx + mu * iTxz;
        Mdd = iAll + 2f * mu * iAlz + mu * mu * iTzz;
        K0pp = mT * zT;
        K0pd = -sA;
        K0dd = -sA * sin;
        K2pd = (sT - mT * zT) * cos / w;
        K2dd = (sA + sF * sin) * cos / w;
        C1pd = mu * sT + sF * cos + iTxz * cos / w - mu * mT * zT;
        C1dp = -(mu * sT + sF * cos);
        C1dd = iAlz * cos / w + mu * (sA + iTzz * cos / w);
        Wheelbase = w;
        Trail = c;
        CosTilt = cos;
    }

    /// <summary>The roll stiffness steering gives at <paramref name="speed"/>: how hard a steer angle pushes the bike upright, N·m/rad.</summary>
    public float SteerToRoll(float speed) => Gravity * K0pd + speed * speed * K2pd;

    /// <summary>
    /// Roll acceleration with the steer prescribed (the rider holds the bars): the first row of the
    /// equations, no torque at the hips.
    /// </summary>
    public float RollAccel(float speed, float roll, float rollRate, float steer, float steerRate, float steerAccel) =>
        -(Mpd * steerAccel + speed * C1pd * steerRate + Gravity * K0pp * roll + SteerToRoll(speed) * steer) / Mpp;

    /// <summary>The steer a steady turn at <paramref name="roll"/> needs: the roll row with nothing changing.</summary>
    public float SteadySteer(float speed, float roll) => -Gravity * K0pp * roll / SteerToRoll(speed);

    /// <summary>Rear frame yaw rate from the steer, rad/s (the paper's kinematic relation).</summary>
    public float YawRate(float speed, float steer, float steerRate) => (speed * steer + Trail * steerRate) * CosTilt / Wheelbase;
}

/// <summary>
/// A bicycle balanced and steered by its rider (#1124): the Whipple model's roll, with the steer
/// angle the rider's hands put on the bars. The player asks for a lean (the stick, the keys, the
/// autopilot); the rider steers to get there the way people do, into the fall. To lean right from
/// upright that means a flick of the bars left first, and the bike falls right: countersteer is
/// not written anywhere, it comes out of the equations.
/// </summary>
public sealed class RiderBalance
{
    /// <summary>The rider's lean response: ~0.5 s to most of a requested lean, a little damped.</summary>
    public const float LeanFrequency = 6f, LeanDamping = 0.85f;

    /// <summary>The rider's arms on the bars: a stiff, damped servo to the steer they want.</summary>
    public const float ArmFrequency = 18f, ArmDamping = 0.9f;

    /// <summary>Steer the rider can put on at most, rad, and how fast, rad/s.</summary>
    public const float MaxSteer = 0.7f, MaxSteerRate = 4f;

    /// <summary>Below this the rider is not balancing a moving bike but walking or wheeling it, m/s.</summary>
    public const float BalanceSpeed = 1.5f;

    /// <summary>Integration step, s: the steer servo and the fast castering of the front at speed need small ones.</summary>
    private const float Substep = 1f / 360f;

    private readonly WhippleModel _model;

    /// <summary>Roll, rad, + to the right (the paper's sign).</summary>
    public float Roll { get; private set; }
    public float RollRate { get; private set; }
    /// <summary>Steer, rad, + to the right.</summary>
    public float Steer { get; private set; }
    public float SteerRate { get; private set; }

    public RiderBalance(WhippleModel model) => _model = model;

    /// <summary>
    /// One frame: the rider chases <paramref name="targetRoll"/> (rad, + right) at <paramref name="speed"/>.
    /// </summary>
    /// <param name="maxRoll">What the tyres hold here: the bike never goes past it.</param>
    /// <returns>The rear frame's yaw rate, rad/s, + to the right.</returns>
    public float Step(float speed, float targetRoll, float maxRoll, float dt)
    {
        targetRoll = Mathf.Clamp(targetRoll, -maxRoll, maxRoll);
        float yawSum = 0f;
        int n = Mathf.Max(1, Mathf.CeilToInt(dt / Substep));
        float h = dt / n;
        for (int i = 0; i < n; i++) yawSum += Substepped(speed, targetRoll, maxRoll, h) * h;
        return yawSum / dt;
    }

    private float Substepped(float v, float target, float maxRoll, float h)
    {
        if (v < BalanceSpeed)
        {
            // walking pace: the rider's feet and hands keep it upright; the bars steer it directly
            float want = Mathf.Clamp(target / Mathf.Max(maxRoll, 0.1f), -1f, 1f) * MaxSteer;
            float rate = Mathf.Clamp((want - Steer) / h, -MaxSteerRate, MaxSteerRate);
            Steer += rate * h;
            SteerRate = rate;
            Roll += (0f - Roll) * (1f - Mathf.Exp(-4f * h));
            RollRate = 0f;
            return _model.YawRate(v, Steer, SteerRate);
        }

        // the rider's steer: what holds the present lean, plus the error toward the wanted one, shaped
        // so the roll answers like a damped spring at LeanFrequency
        float k = _model.SteerToRoll(v);
        float gain = _model.Mpp / Mathf.Max(k, 1f);
        float wantSteer = _model.SteadySteer(v, Roll)
            + gain * (LeanFrequency * LeanFrequency * (Roll - target) + 2f * LeanDamping * LeanFrequency * RollRate);
        // the bars turn only so far, and at speed only a margin past the steer the tightest turn the
        // tyres hold needs (w·g·tanφ/v²): a rider does not yank the bars at 50 km/h
        float limit = Mathf.Min(MaxSteer,
            1.5f * _model.Wheelbase * WhippleModel.Gravity * Mathf.Tan(maxRoll) / (Mathf.Max(v * v, 1f) * _model.CosTilt) + 0.05f);
        wantSteer = Mathf.Clamp(wantSteer, -limit, limit);

        float steerAccel = ArmFrequency * ArmFrequency * (wantSteer - Steer) - 2f * ArmDamping * ArmFrequency * SteerRate;
        float rollAccel = _model.RollAccel(v, Roll, RollRate, Steer, SteerRate, steerAccel);

        // semi-implicit Euler: velocities first, then positions with the new velocities
        SteerRate = Mathf.Clamp(SteerRate + steerAccel * h, -MaxSteerRate, MaxSteerRate);
        Steer = Mathf.Clamp(Steer + SteerRate * h, -MaxSteer, MaxSteer);
        RollRate += rollAccel * h;
        Roll += RollRate * h;
        // past the tyres' grip the bike would slide out; the rider is assumed to stay on it
        if (Mathf.Abs(Roll) > maxRoll + 0.05f)
        {
            Roll = Mathf.Sign(Roll) * (maxRoll + 0.05f);
            RollRate = 0f;
        }
        return _model.YawRate(v, Steer, SteerRate);
    }

    /// <summary>Puts the bike at a lean with nothing moving: a respawn, a test.</summary>
    public void Reset(float roll = 0f)
    {
        Roll = roll;
        Steer = 0f;
        RollRate = SteerRate = 0f;
    }
}
