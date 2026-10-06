using Godot;
using UnitSport.Core;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>Where a crane's moving parts are at one moment: the jib's yaw, the trolley out along it, the hook's height.</summary>
/// <param name="Yaw">The game's yaw (0 faces −Z) of the jib.</param>
/// <param name="Trolley">Metres out from the mast along the jib.</param>
/// <param name="HookY">The hook's height, tile-local.</param>
/// <param name="Loaded">Whether a load hangs on the hook.</param>
public readonly record struct CranePose(float Yaw, float Trolley, float HookY, bool Loaded);

/// <summary>
/// What one crane is set to do (#610): where it stands, how high its jib is, where it picks
/// loads up and where it sets them down, all tile-local.
/// </summary>
public sealed record CraneJob(string Key, Vector2 Base, float JibY, float JibLength, float RestYaw,
    Vector3 Pick, IReadOnlyList<Vector3> Drops);

/// <summary>
/// A building site's crane moving with nothing sent (#610): its pose is a pure function of the
/// clocks every peer already shares, the way the traffic lights' phases are.
///
/// <para>
/// <b>Two clocks, on purpose</b> (<c>docs/notes/core/three-clocks.md</c>). <i>When</i> it works is
/// environment time: 07:00-12:00 and 13:00-17:00, not on Sundays, otherwise it weathervanes, its
/// jib drifting slowly with the wind. <i>How fast</i> it moves is the simulation clock: a lift is
/// physical motion, and at a 24-minute day the environment clock would whirl the jib round 60
/// times too fast. So a cycle (pick up, slew, set down, slew back) takes <see cref="Cycle"/>
/// simulated seconds, and the cycle number picks which drop it serves.
/// </para>
/// </summary>
public static class CraneMotion
{
    /// <summary>One lift, start to start, simulated seconds.</summary>
    public const double Cycle = 140;
    /// <summary>The trolley never runs closer to the mast than this, m.</summary>
    public const float MinTrolley = 4f;
    /// <summary>The hook's lowest point over what it lifts: the load's height, m.</summary>
    public const float LoadHeight = 1.4f;

    /// <summary>Whether a crane is working at this hour of this day (0 = the clock's first day).</summary>
    public static bool Working(double hour, long day) =>
        Weekday(day) != 6 && (hour >= 7 && hour < 12 || hour >= 13 && hour < 17);

    /// <summary>The day of the week, Monday 0 to Sunday 6, of an environment day number.</summary>
    public static int Weekday(long day) => (int)(((day % 7) + 7) % 7);

    /// <summary>The crane's pose at a moment.</summary>
    /// <param name="sim">Simulated seconds (the server's, as every peer knows it).</param>
    /// <param name="hour">The environment hour, 0-24.</param>
    /// <param name="day">The environment day number.</param>
    public static CranePose Pose(CraneJob job, double sim, double hour, long day)
    {
        // each crane on its own phase, so two cranes of a site never lift in step
        double offset = Fnv.Unit(job.Key + "|crane|phase") * Cycle;
        float top = job.JibY - 1.2f;
        if (!Working(hour, day) || job.Drops.Count == 0)
        {
            // weathervaning: free to slew, it turns slowly with the wind, the trolley parked in
            float drift = (float)(0.9 * Math.Sin(sim / 517.0 + offset) + 0.4 * Math.Sin(sim / 181.0 + 2 * offset));
            return new CranePose(Wrap(job.RestYaw + drift), MinTrolley, top, false);
        }

        double t = sim + offset;
        long n = (long)Math.Floor(t / Cycle);
        float f = (float)(t / Cycle - n);
        var drop = job.Drops[(int)(Fnv.Unit($"{job.Key}|crane|drop|{n}") * job.Drops.Count) % job.Drops.Count];
        var (pickYaw, pickOut) = Aim(job, job.Pick);
        var (dropYaw, dropOut) = Aim(job, drop);
        float pickLow = job.Pick.Y + LoadHeight, dropLow = drop.Y + LoadHeight;

        // the cycle: down at the pick, hooked on, up; slew and run out to the drop; down, unhook,
        // up; and back to the pick
        if (f < 0.10f) return new CranePose(pickYaw, pickOut, Mathf.Lerp(top, pickLow, S(f / 0.10f)), false);
        if (f < 0.15f) return new CranePose(pickYaw, pickOut, pickLow, f >= 0.12f);
        if (f < 0.25f) return new CranePose(pickYaw, pickOut, Mathf.Lerp(pickLow, top, S((f - 0.15f) / 0.10f)), true);
        if (f < 0.50f)
        {
            float k = S((f - 0.25f) / 0.25f);
            return new CranePose(Turn(pickYaw, dropYaw, k), Mathf.Lerp(pickOut, dropOut, k), top, true);
        }
        if (f < 0.60f) return new CranePose(dropYaw, dropOut, Mathf.Lerp(top, dropLow, S((f - 0.50f) / 0.10f)), true);
        if (f < 0.65f) return new CranePose(dropYaw, dropOut, dropLow, f < 0.62f);
        if (f < 0.75f) return new CranePose(dropYaw, dropOut, Mathf.Lerp(dropLow, top, S((f - 0.65f) / 0.10f)), false);
        float back = S((f - 0.75f) / 0.25f);
        return new CranePose(Turn(dropYaw, pickYaw, back), Mathf.Lerp(dropOut, pickOut, back), top, false);
    }

    /// <summary>The yaw that points the jib at a target, and how far out the trolley must run.</summary>
    public static (float Yaw, float Out) Aim(CraneJob job, Vector3 target)
    {
        float dx = target.X - job.Base.X, dz = target.Z - job.Base.Y;
        // yaw 0 faces -Z: the heading whose facing is (dx, dz) is atan2(-dx, -dz)
        return (Wrap(MathF.Atan2(-dx, -dz)), Math.Clamp(MathF.Sqrt(dx * dx + dz * dz), MinTrolley, job.JibLength - 1f));
    }

    /// <summary>From one yaw to another the short way round.</summary>
    private static float Turn(float from, float to, float k)
    {
        float d = Wrap(to - from + MathF.PI) - MathF.PI;
        return Wrap(from + d * k);
    }

    /// <summary>Ease in and out: a crane starts and stops gently.</summary>
    private static float S(float x) => x * x * (3 - 2 * x);

    private static float Wrap(float a)
    {
        a %= MathF.Tau;
        return a < 0 ? a + MathF.Tau : a;
    }
}
