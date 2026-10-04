namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// Server: what a PvP hit must also pass before it reaches its victim (#468), beside the rules
/// <c>ItemEvents.RelayHit</c> already had (PvP on, damage cap, range, nobody down). The shooter's
/// client traces its own shots, so a modified one could fire as fast as it liked, or through a
/// mountain; this bounds both without second-guessing an honest client:
/// <list type="bullet">
/// <item><see cref="TryShot"/>: a fire-rate budget per shooter and weapon, with room for jitter.</item>
/// <item><see cref="TerrainClear"/>: no hill between the shooter's eye and the hit.</item>
/// </list>
/// </summary>
public sealed class HitGuard
{
    /// <summary>Shots the budget holds: packets bunched by the network still pass.</summary>
    public const float Burst = 3f;

    /// <summary>The budget refills this much faster than the weapon fires.</summary>
    public const float Leeway = 1f / 0.85f;

    /// <summary>Hits arriving this close together are one shot's (a volley of pellets, several victims).</summary>
    public const double VolleySeconds = 0.05;

    private sealed class Gun
    {
        public float Tokens = Burst;
        public double At;
        public double VolleyAt = double.NegativeInfinity;
        public readonly HashSet<long> Victims = new();
    }

    private readonly Dictionary<(long Shooter, int Weapon), Gun> _guns = new();

    /// <summary>
    /// Whether a hit on <paramref name="victim"/> from <paramref name="shooter"/>'s
    /// <paramref name="weapon"/> fits a real rate of fire at <paramref name="now"/> (seconds). Hits
    /// within <see cref="VolleySeconds"/> of the shot's first are that shot's: up to
    /// <paramref name="pellets"/> different victims, each once for free. A new shot spends one of
    /// <see cref="Burst"/> tokens, which come back at <see cref="Leeway"/> / <paramref name="interval"/> a second.
    /// </summary>
    public bool TryShot(long shooter, int weapon, float interval, int pellets, long victim, double now)
    {
        var key = (shooter, weapon);
        if (!_guns.TryGetValue(key, out var gun))
            _guns[key] = gun = new Gun { At = now };

        // another victim of the same shot is free; the same victim again is another shot (two
        // shots the network delivered together), which pays like any
        if (now - gun.VolleyAt <= VolleySeconds && gun.Victims.Count < Math.Max(1, pellets) && !gun.Victims.Contains(victim))
        {
            gun.Victims.Add(victim);
            return true;
        }

        float rate = Leeway / Math.Max(0.01f, interval);
        gun.Tokens = Math.Min(Burst, gun.Tokens + (float)Math.Max(0, now - gun.At) * rate);
        gun.At = now;
        if (gun.Tokens < 1f) return false;
        gun.Tokens -= 1f;
        gun.VolleyAt = now;
        gun.Victims.Clear();
        gun.Victims.Add(victim);
        return true;
    }

    /// <summary>
    /// Whether no ground rises between <paramref name="from"/> (the shooter's eye) and
    /// <paramref name="to"/> (where the hit landed), both LV95 (E, N, altitude), by
    /// <paramref name="ground"/> (the surface at E, N; null where unknown) sampled every
    /// <paramref name="step"/> metres. A sample counts as blocking only when the ground is more than
    /// <paramref name="margin"/> above the line PLUS what each end itself lies under the surface,
    /// blended along the shot: the ground is a coarse lattice that rounds ridges off and fills
    /// valleys in, and a player in a room or a tunnel is under it already. Pure.
    /// </summary>
    public static bool TerrainClear((double E, double N, double Alt) from, (double E, double N, double Alt) to,
        Func<double, double, float?> ground, float step = 10f, float margin = 15f)
    {
        double under0 = Under(from, ground), under1 = Under(to, ground);
        double de = to.E - from.E, dn = to.N - from.N;
        int n = (int)(Math.Sqrt(de * de + dn * dn) / step);
        for (int i = 1; i < n; i++)
        {
            double t = i / (double)n;
            double e = from.E + de * t, north = from.N + dn * t, alt = from.Alt + (to.Alt - from.Alt) * t;
            if (ground(e, north) is { } g && g - alt > margin + under0 * (1 - t) + under1 * t) return false;
        }
        return true;
    }

    /// <summary>How far a point lies under the surface, 0 when it is above it or the ground is unknown.</summary>
    private static double Under((double E, double N, double Alt) p, Func<double, double, float?> ground) =>
        ground(p.E, p.N) is { } g ? Math.Max(0, g - p.Alt) : 0;
}
