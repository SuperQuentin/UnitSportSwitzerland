using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// The shuttle's flight beyond an airliner's (#1492, #1430 C2), pure maths over Godot's managed types so
/// tier 0 tests it (tests/…/ShuttleTests). Below the Space handover it is an <see cref="AirlinerFlight"/>
/// with rockets; above it, <see cref="Airliner"/> flies its orbit through <see cref="SpaceOrbit"/> or the
/// cruise drive, and these are the parts: the levers and the rockets' spool, the reaction jets that turn it,
/// the drive's speed, keeping the attitude level as the Earth's up turns under it, and re-entry's heat.
/// </summary>
public static class ShuttleFlight
{
    /// <summary>
    /// The cruise drive's time constant, s (plan section 9): v = lever · d / τ, d the distance to the surface.
    /// From 400 km at full lever 40 km/s; it slows by itself as the surface nears, and a trip's time grows
    /// only with the logarithm of its distance.
    /// </summary>
    public const float DriveTau = 10f;

    /// <summary>The drive switches on only above this, m: the Space handover (<c>Curvature.SpaceEnterM</c>).</summary>
    public const double DriveFloorM = 60_000;

    /// <summary>The reaction jets' most pitch and roll rates, rad/s: a half turn in 12 s.</summary>
    public const float JetPitchRate = 0.26f, JetRollRate = 0.45f;

    /// <summary>In the drive, the nose turns toward a bank at this rate per radian of bank, rad/s (a plane's feel).</summary>
    public const float DriveTurnRate = 0.35f;

    /// <summary>
    /// Below this dynamic pressure, Pa, the air has no say in where it points: the reaction jets turn it
    /// (the stick's rates at full authority, hands-off it holds its attitude), not the law or the fin.
    /// Fades in from half of it. A rocket climb passes it near 40 km, a re-entry near 70 km.
    /// </summary>
    public const float JetQ = 1500f;

    /// <summary>Mass over drag area in Space (m / Cd·A), kg/m²: belly first at high alpha, Cd ~0.85 on 250 m².</summary>
    public const double BallisticCoefficient = 470;

    /// <summary>The drive's speed at a lever and a distance to the surface, m/s.</summary>
    public static float DriveSpeed(float lever, double distance) => (float)(Mathf.Clamp(lever, 0f, 1f) * System.Math.Max(distance, 0) / DriveTau);

    /// <summary>The lever that drives at <paramref name="speed"/> at a distance to the surface (the drive's inverse), 0..1.</summary>
    public static float DriveLever(float speed, double distance) => distance > 0 ? Mathf.Clamp((float)(speed * DriveTau / distance), 0f, 1f) : 0f;

    /// <summary>The share of control the reaction jets have at a dynamic pressure: 1 in vacuum, 0 in thick air.</summary>
    public static float JetShare(float q) => Mathf.Clamp(2f - 2f * q / JetQ, 0f, 1f);

    /// <summary>
    /// The levers and the rockets' spool in Space, as <see cref="AirlinerFlight.Step"/> moves them in the air:
    /// the levers at half travel a second, the spool after them. Returns the thrust, N.
    /// </summary>
    public static float Levers(AirlinerSpec spec, ref AirlinerFlight.State s, float leverUp, float leverDown, bool noPilot, bool running, float dt)
    {
        if (!noPilot) s.Lever = Mathf.Clamp(s.Lever + (leverUp - leverDown) * 0.5f * dt, 0f, 1f);
        s.Reverse = 0f;
        float idle = spec.IdleSpool;
        float target = running ? idle + (1f - idle) * s.Lever : 0f;
        s.Spool = Mathf.MoveToward(s.Spool, target, (1f - idle) / spec.SpoolTime * (target < s.Spool ? 1.3f : 1f) * dt);
        float thrust = running ? spec.Engines * spec.StaticThrust * AirlinerFlight.ThrustShare(spec, s.Spool) : 0f;
        s.Thrust = thrust;
        return thrust;
    }

    /// <summary>
    /// The reaction jets (#1492): the stick asks a pitch rate (back: nose up) and a roll rate (right: right
    /// wing down); let go, the rates die out and the attitude is held. In the drive a bank also turns the
    /// nose toward it, as a plane's would, so it is steered like one. Updates the attitude, the rates and the yaw.
    /// </summary>
    public static void Jets(ref AirlinerFlight.State s, Vector2 stick, bool drive, float dt)
    {
        s.Stick = stick;
        float qCmd = stick.Y * JetPitchRate, pCmd = stick.X * JetRollRate;
        s.PitchRate += (qCmd - s.PitchRate) * MathX.Damp(3f, dt);
        s.RollRate += (pCmd - s.RollRate) * MathX.Damp(3f, dt);
        var att = s.Attitude.Orthonormalized();
        att = att * new Basis(Vector3.Right, s.PitchRate * dt);
        att = att * new Basis(Vector3.Back, -s.RollRate * dt);
        if (drive)
        {
            // the turn toward the bank, about the local up (+ bank is the right wing down: turn right)
            float bank = AirlinerFlight.BankOf(att);
            att = new Basis(Vector3.Up, -bank * DriveTurnRate * dt) * att;
        }
        s.Attitude = att.Orthonormalized();
        Heading(ref s);
    }

    /// <summary>The yaw from the nose (kept when it points straight up or down).</summary>
    public static void Heading(ref AirlinerFlight.State s)
    {
        var nose = -s.Attitude.Z;
        var flat = new Vector2(nose.X, nose.Z);
        if (flat.Length() > 0.05f) s.Yaw = Mathf.Atan2(-flat.X, -flat.Y);
    }

    /// <summary>
    /// The attitude carried along as the local up turns from <paramref name="from"/> to <paramref name="to"/>
    /// (the craft moved round the Earth): held level with the horizon rather than with the stars, so a craft
    /// flying level in orbit or in the drive stays level. The least rotation between the two ups.
    /// </summary>
    public static Basis Relevel(Basis attitude, Vector3 from, Vector3 to)
    {
        var axis = from.Cross(to);
        float sin = axis.Length(), cos = from.Dot(to);
        if (sin < 1e-9f) return attitude;
        return new Basis(axis / sin, Mathf.Atan2(sin, cos)) * attitude;
    }

    /// <summary>
    /// Re-entry's glow, 0 .. 1, from the stagnation heating's law (Sutton-Graves: √ρ · v³): white hot at the
    /// orbiter's peak heating (7 km/s at 70 km), a dull glow from a twentieth of it, nothing below.
    /// </summary>
    public static float Heat(float density, float speed)
    {
        float flux = Mathf.Sqrt(Mathf.Max(density, 0f)) * speed * speed * speed;
        return Mathf.Clamp((flux / PeakFlux - 0.05f) / 0.95f, 0f, 1f);
    }

    /// <summary>√ρ · v³ at the orbiter's peak heating: 7 km/s through 8.3·10⁻⁵ kg/m³ (70 km).</summary>
    public const float PeakFlux = 3.1e9f;
}
