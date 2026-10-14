using Godot;
using UnitSport.Items;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>An operator's three levers, each −1..1: slew (+ turns left), trolley (+ runs out), hoist (+ raises).</summary>
public record struct CraneLevers(float Slew, float Trolley, float Hoist);

/// <summary>
/// A crane worked by a player (#618): how fast each motion runs, where it stops, and when the hook
/// can take a pallet or set one down. The operator steps the pose; everyone else is sent it.
/// </summary>
public static class CraneControl
{
    /// <summary>The slew at full lever, rad/s: a real crane's 0.7 rpm made quicker to play.</summary>
    public const float SlewRate = 0.25f;
    /// <summary>The trolley at full lever, m/s.</summary>
    public const float TrolleyRate = 2.0f;
    /// <summary>The hoist at full lever, m/s.</summary>
    public const float HoistRate = 2.5f;
    /// <summary>Seconds from standing to full speed, and back: a crane's drives start and stop gently.</summary>
    public const float Ramp = 1.0f;
    /// <summary>The hook is let down slower than this over what is under it, m/s per metre of clearance.</summary>
    public const float LandingRate = 0.8f;
    /// <summary>The slowest the hook comes down onto what is under it, m/s.</summary>
    public const float LandingMin = 0.35f;

    /// <summary>The hook block's underside under the hook's origin, m (<see cref="CranePlans.Hook"/>).</summary>
    public const float HookBottom = 0.95f;
    /// <summary>The slings from the hook to a pallet's top, m.</summary>
    public const float Slings = 1.0f;
    /// <summary>A hung pallet's underside under the hook's origin, m.</summary>
    public const float SlingHang = HookBottom + Slings + Pallets.Height;
    /// <summary>How far off a pallet's centre the hook can be to take it, m, flat.</summary>
    public const float HookReach = 0.9f;
    /// <summary>The hook block's underside at most this far over a pallet's top to take it, m.</summary>
    public const float HookOnGap = 0.6f;
    /// <summary>A hung pallet's underside at most this far over what is under it to set it down, m.</summary>
    public const float SetDownGap = 0.3f;

    /// <summary>The highest the hook goes: just under the trolley.</summary>
    public static float Top(CraneJob job) => job.JibY - 1.2f;

    /// <summary>Where the hook's origin is, tile-local.</summary>
    public static Vector3 HookAt(CraneJob job, CranePose pose) => new(
        job.Base.X - MathF.Sin(pose.Yaw) * pose.Trolley, pose.HookY, job.Base.Y - MathF.Cos(pose.Yaw) * pose.Trolley);

    /// <summary>
    /// One step of the operator's levers. Each drive eases towards its lever over <see cref="Ramp"/>
    /// and stops dead at its end; the hook never goes under <paramref name="floor"/> (the lowest its
    /// origin may go over what is under it) and comes down slower the nearer it is.
    /// </summary>
    /// <param name="speed">Each drive's speed, −1..1 of its rate: kept by the caller between steps.</param>
    public static CranePose Step(CraneJob job, CranePose pose, CraneLevers levers, ref CraneLevers speed, float floor, float dt)
    {
        float k = dt / Ramp;
        speed = new CraneLevers(Toward(speed.Slew, levers.Slew, k), Toward(speed.Trolley, levers.Trolley, k),
            Toward(speed.Hoist, levers.Hoist, k));

        float yaw = CraneMotion.Wrap(pose.Yaw + speed.Slew * SlewRate * dt);

        float trolley = pose.Trolley + speed.Trolley * TrolleyRate * dt, far = job.JibLength - 1f;
        if (trolley <= CraneMotion.MinTrolley || trolley >= far) speed = speed with { Trolley = 0 };
        trolley = Math.Clamp(trolley, CraneMotion.MinTrolley, far);

        float top = Top(job), lowest = Math.Min(floor, top);
        float v = speed.Hoist * HoistRate;
        if (v < 0)
            v = Math.Max(v, -Math.Max(LandingMin, (pose.HookY - lowest) * LandingRate));
        float hook = pose.HookY + v * dt;
        if (hook <= lowest || hook >= top) speed = speed with { Hoist = 0 };
        hook = Math.Clamp(hook, lowest, top);
        return new CranePose(yaw, trolley, hook, false);
    }

    /// <summary>Whether the hook, at <paramref name="hook"/>, can take a pallet standing at <paramref name="pallet"/> (its underside's centre).</summary>
    public static bool Hooks(Vector3 hook, Vector3 pallet)
    {
        float dx = hook.X - pallet.X, dz = hook.Z - pallet.Z;
        float gap = hook.Y - HookBottom - (pallet.Y + Pallets.Height);
        return dx * dx + dz * dz <= HookReach * HookReach && gap >= -0.15f && gap <= HookOnGap;
    }

    /// <summary>
    /// Where a pallet on the hook is drawn: its underside <see cref="SlingHang"/> under the hook, or
    /// still on the ground it was taken from while the slings are slack.
    /// </summary>
    /// <param name="rest">Where it stood when hooked on (its underside), or NaN once it has been lifted.</param>
    public static float HungAt(float hookY, float rest) =>
        float.IsNaN(rest) ? hookY - SlingHang : Math.Max(hookY - SlingHang, rest);

    /// <summary>Whether a pallet hung with its underside at <paramref name="under"/> can be set down on what is at <paramref name="surface"/>.</summary>
    public static bool SetsDown(float under, float surface) => under - surface is >= -0.2f and <= SetDownGap;

    private static float Toward(float from, float to, float step) =>
        from < to ? Math.Min(to, from + step) : Math.Max(to, from - step);
}
