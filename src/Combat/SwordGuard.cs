namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>How the server answered a peer's announced move.</summary>
public enum SwordVerdict { Ok, BadMove, TooSoon, NoStamina }

/// <summary>
/// Server: what a sword blow must pass before it hurts anyone (#1371), in the spirit of <see cref="HitGuard"/>:
/// the client announces each move (<see cref="TryAct"/>) and each target it reached (<see cref="TryHit"/>), and the
/// server keeps its own stamina and move clock for the peer. A modified client cannot swing faster than the frame
/// data allows, dodge without stamina, hit twice with one swing, hit a target before the blade is live, or keep
/// swinging for ever from one announcement. A dodge's untouchable window is the server's too (<see cref="Invulnerable"/>).
/// </summary>
public sealed class SwordGuard
{
    /// <summary>The server's slack for a client's own clock and the network's jitter, seconds.</summary>
    public const double Jitter = 0.06;
    /// <summary>Stamina points it forgives: the client's meter runs a little ahead of this one.</summary>
    public const float StaminaLeeway = 12f;
    /// <summary>Targets one blow can reach.</summary>
    public const int MaxTargets = 6;

    private sealed class Peer
    {
        public SwordStamina Stamina = SwordStamina.Full();
        public SwordMove Move;
        public double At = double.NegativeInfinity;
        public double Seen;
        public double DodgeFrom = double.NegativeInfinity, DodgeTo = double.NegativeInfinity;
        public readonly HashSet<long> Hit = new();
        public bool Charging;
    }

    private readonly Dictionary<long, Peer> _peers = new();

    private Peer Of(long peer, double now)
    {
        if (!_peers.TryGetValue(peer, out var p)) _peers[peer] = p = new Peer { Seen = now };
        p.Stamina.Step((float)Math.Clamp(now - p.Seen, 0.0, 1.0));
        p.Seen = now;
        return p;
    }

    /// <summary>The peer left: its budget goes.</summary>
    public void Forget(long peer) => _peers.Remove(peer);

    /// <summary>The peer's stamina as the server counts it (a test, a log).</summary>
    public float StaminaOf(long peer, double now) => Of(peer, now).Stamina.Points;

    /// <summary>
    /// A peer announces <paramref name="move"/> at <paramref name="now"/> (the wall clock, seconds). The blows and the
    /// dodge must come when the last move allows (its cancel window, less the jitter) and be paid for. A
    /// <see cref="SwordMove.Charge"/> or <see cref="SwordMove.None"/> costs nothing and is always taken.
    /// </summary>
    public SwordVerdict TryAct(long peer, SwordMove move, double now)
    {
        var p = Of(peer, now);
        if (move is SwordMove.None or SwordMove.Charge)
        {
            p.Charging = move == SwordMove.Charge;
            return SwordVerdict.Ok;
        }
        if (!SwordRules.IsBlow(move) && move != SwordMove.Dodge) return SwordVerdict.BadMove;
        var def = SwordRules.Def(move);
        // the last move frees the hands at its cancel window; a charge must have been held for its minimum
        if (p.Move != SwordMove.None && p.Move != SwordMove.Charge)
        {
            var last = SwordRules.Def(p.Move);
            double free = p.Move == SwordMove.Plunge ? last.Cancel : last.Cancel;
            if (now - p.At < free - Jitter) return SwordVerdict.TooSoon;
        }
        if (move == SwordMove.Charged && !p.Charging) return SwordVerdict.TooSoon;
        if (!p.Stamina.TrySpend(def.Cost, StaminaLeeway)) return SwordVerdict.NoStamina;
        p.Charging = false;
        p.Move = move;
        p.At = now;
        p.Hit.Clear();
        if (move == SwordMove.Dodge)
        {
            p.DodgeFrom = now + SwordRules.IFramesFrom - Jitter;
            p.DodgeTo = now + SwordRules.IFramesTo + 0.1;
        }
        return SwordVerdict.Ok;
    }

    /// <summary>
    /// The peer says its last announced <paramref name="move"/> reached <paramref name="target"/> (a dummy's, a body's
    /// key). Taken when the blade could be live (not before half the startup, not after the move and the
    /// jitter), once per target per swing, up to <see cref="MaxTargets"/>.
    /// </summary>
    public bool TryHit(long peer, SwordMove move, long target, double now)
    {
        if (!_peers.TryGetValue(peer, out var p) || p.Move != move || !SwordRules.IsBlow(move)) return false;
        var def = SwordRules.Def(move);
        double since = now - p.At;
        if (since < def.Startup * 0.5 - Jitter || since > SwordRules.Window(move) + Jitter + 0.3) return false;
        if (p.Hit.Count >= MaxTargets || !p.Hit.Add(target)) return false;
        return true;
    }

    /// <summary>The peer's last swing already reached <paramref name="target"/> (a bot's second pass over the same blow skips it).</summary>
    public bool Reached(long peer, long target) => _peers.TryGetValue(peer, out var p) && p.Hit.Contains(target);

    /// <summary>The peer is inside a dodge's untouchable window now: a blow on it is refused.</summary>
    public bool Invulnerable(long peer, double now) => _peers.TryGetValue(peer, out var p) && now >= p.DodgeFrom && now <= p.DodgeTo;
}
