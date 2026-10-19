using Godot;

// Plain C# over Godot maths: linked into the unit tests (docs/notes/general/testing.md).

namespace UnitSport.Farming;

/// <summary>
/// Where a cow is and what it does (#863), as a pure function of its herd, its number and the
/// shared simulation clock: every peer computes the same cow at the same place without a byte on
/// the wire, and a tile that comes back finds its herd where it would have been. A cow's day is a
/// string of rounds: walk a few metres to the next spot of its herd's pasture (<see cref="Herd.Spots"/>),
/// then graze there, now and then lifting its head to look around.
/// </summary>
public static class CowMotion
{
    /// <summary>A cow's walk, m/s.</summary>
    public const float WalkSpeed = 0.75f;

    /// <summary>
    /// One cow's pose. <paramref name="Offset"/> is metres east (X) and north (Y) of its herd's anchor;
    /// <paramref name="Yaw"/> the Godot yaw it faces (0 = north, −Z); <paramref name="Head"/> 0 up .. 1
    /// down in the grass; <paramref name="Gait"/> 0 standing .. 1 walking.
    /// </summary>
    public readonly record struct Pose(Vector2 Offset, float Yaw, float Head, float Gait);

    /// <summary>Seconds of one round (a walk and a graze) for cow <paramref name="cow"/>.</summary>
    public static float Period(Herd herd, int cow) => 26f + 18f * HerdPlan.Unit(herd.Seed, (uint)cow, 1);

    public static Pose At(Herd herd, int cow, double t)
    {
        float period = Period(herd, cow);
        double phased = t + HerdPlan.Unit(herd.Seed, (uint)cow, 2) * period;
        long k = (long)Math.Floor(phased / period);
        float u = (float)(phased - k * period);

        var from = Spot(herd, cow, k - 1);
        var to = Spot(herd, cow, k);
        var step = to - from;
        float d = step.Length();
        // the way it faces: where it last walked, or its own hashed bearing if it hardly moved
        float yaw = d > 0.5f ? YawOf(step) : HerdPlan.Unit(herd.Seed, (uint)cow, (uint)(k & 0xFFFF) + 3) * Mathf.Tau;
        float walk = d > 0.5f ? Mathf.Min(d / WalkSpeed, period * 0.8f) : 0f;

        if (u < walk)
        {
            // ease in and out of the walk
            float f = u / walk;
            float eased = f * f * (3f - 2f * f);
            return new Pose(from.Lerp(to, eased), yaw, 0.15f, 1f);
        }

        // grazing: head down, a slow sway of the body, the head up for a look round now and then
        float still = (u - walk) / Mathf.Max(period - walk, 0.01f);
        float look = HerdPlan.Unit(herd.Seed, (uint)cow, (uint)(k & 0xFFFF) + 5);
        bool up = still > look * 0.6f + 0.2f && still < look * 0.6f + 0.33f;
        float sway = Mathf.Sin((float)(t * 0.21) + cow * 1.7f) * 0.18f;
        return new Pose(to, yaw + sway, up ? 0f : 1f, 0f);
    }

    /// <summary>
    /// The spot of round <paramref name="k"/>: a target drifting smoothly over the herd's patch
    /// (value noise of the round, so one round's spot is a few metres from the last), snapped to the
    /// nearest pasture spot, with this cow's own nudge so the herd does not stack.
    /// </summary>
    public static Vector2 Spot(Herd herd, int cow, long k)
    {
        uint cs = herd.Seed ^ (uint)(cow + 1) * 0x9E3779B9u;
        double x = k * 0.2 + HerdPlan.Unit(cs, 1, 0x77) * 50.0;
        long i = (long)Math.Floor(x);
        float f = (float)(x - i), s = f * f * (3f - 2f * f);
        float N(long at, uint axis) => HerdPlan.Unit(cs, (uint)(at & 0xFFFFFFFF), 0x5907 + axis) * 2f - 1f;
        var target = new Vector2(Mathf.Lerp(N(i, 0), N(i + 1, 0), s), Mathf.Lerp(N(i, 1), N(i + 1, 1), s)) * HerdPlan.Spread;
        var spot = herd.Spots[0];
        float best = float.MaxValue;
        foreach (var p in herd.Spots)
        {
            float d = p.DistanceSquaredTo(target);
            if (d < best) { best = d; spot = p; }
        }
        float a = HerdPlan.Unit(cs, 9, 0x3A) * Mathf.Tau, r = 0.8f + HerdPlan.Unit(cs, 10, 0x3B) * 1.8f;
        return spot + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
    }

    /// <summary>The Godot yaw facing a step of (east, north) metres.</summary>
    public static float YawOf(Vector2 eastNorth) => Mathf.Atan2(-eastNorth.X, eastNorth.Y);
}
