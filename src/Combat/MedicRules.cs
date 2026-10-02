namespace UnitSport.Combat;

/// <summary>
/// The medic armband's rules (#218), pure so tier-0 tests run them: who may hurt whom, and the
/// server's attack cooldown per player identity. The node side is <see cref="Medic"/>.
/// </summary>
public sealed class MedicLedger
{
    /// <summary>Seconds after attacking a player before the armband may go on.</summary>
    public double Cooldown { get; set; } = 300;

    /// <summary>Seconds of standing still, unhurt, that putting it on takes.</summary>
    public double Delay { get; set; } = 10;

    // by identity (the player's name), not the peer: leaving and rejoining keeps the timer
    private readonly Dictionary<string, double> _attacked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, double> _hurt = new();

    /// <summary>Whether a hit from <paramref name="attacker"/> may hurt <paramref name="victim"/>: never if either wears the armband.</summary>
    public static bool Hurts(bool pvpAllows, bool attackerMedic, bool victimMedic) => pvpAllows && !attackerMedic && !victimMedic;

    /// <summary><paramref name="who"/> attacked a player at <paramref name="now"/>: the cooldown restarts.</summary>
    public void Attacked(string who, double now) => _attacked[who] = now;

    /// <summary>Peer <paramref name="peer"/> took player damage at <paramref name="now"/> (cancels a pending armband).</summary>
    public void Hurt(long peer, double now) => _hurt[peer] = now;

    public void Left(long peer) => _hurt.Remove(peer);

    public double CooldownLeft(string who, double now) =>
        _attacked.TryGetValue(who, out double t) ? Math.Max(0, t + Cooldown - now) : 0;

    public bool HurtSince(long peer, double since) => _hurt.TryGetValue(peer, out double t) && t >= since;

    /// <summary>Why <paramref name="who"/> may not put the armband on now, or null when they may.</summary>
    public string? Refusal(string who, bool inMatch, double now) =>
        inMatch ? "No medic armband in a Battle Royale match."
        : CooldownLeft(who, now) is > 0 and var left ? AvailableIn(left)
        : null;

    /// <summary>"Medic available in m:ss", rounded up.</summary>
    public static string AvailableIn(double seconds)
    {
        int s = (int)Math.Ceiling(seconds);
        return $"Medic available in {s / 60}:{s % 60:00}";
    }
}
