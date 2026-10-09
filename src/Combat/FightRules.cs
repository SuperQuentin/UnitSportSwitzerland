namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>A fist-fight move (#495). Append only: the value is on the wire and in the replicated pose.</summary>
public enum FightMove : byte
{
    None = 0,
    Jab = 1,
    Kick = 2,
    LowJab = 3,
    Sweep = 4,
    JumpKick = 5,
    Uppercut = 6,
    /// <summary>The third hit of jab, jab, kick: a kick pressed while the second jab recovers.</summary>
    StringKick = 7,
    /// <summary>Down, down + kick on a dazed loser: the Fatality.</summary>
    Finisher = 8,
}

/// <summary>Where a move lands: what guard stops it, and whom it misses.</summary>
public enum HitLevel : byte { High, Mid, Low, Overhead }

/// <summary>
/// What a fighter's body is doing, as the replicated <c>FootPlayer.FightPose</c> carries it
/// (#495). 0 = not fighting. A move is <see cref="MoveBase"/> + its <see cref="FightMove"/>.
/// Append only: the value is on the wire.
/// </summary>
public enum FightStance : byte
{
    None = 0,
    Stand = 1,
    GuardHigh = 2,
    Crouch = 3,
    GuardLow = 4,
    Air = 5,
    HitStun = 6,
    Down = 7,
    Dazed = 8,
    Victory = 9,
    BlockStun = 10,
    MoveBase = 16,
}

/// <summary>A move's frame data, in seconds and metres.</summary>
/// <param name="Reach">From the attacker's centre to the front of the victim's body.</param>
/// <param name="Push">How far a hit shoves the victim back (half that on a block).</param>
public readonly record struct MoveDef(
    FightMove Move, float Startup, float Active, float Recovery, float Reach, int Damage, HitLevel Level,
    float HitStun, float BlockStun, float Push, bool Knockdown)
{
    public float Total => Startup + Active + Recovery;
}

/// <summary>How a strike ended on the server.</summary>
public enum StrikeOutcome : byte { Refused, Whiff, Blocked, Hit }

/// <summary>
/// The fist fight's rules (#495): the move table, which guard stops which move, and the input
/// recognizer for the special and the string. Pure so the unit tests own the numbers.
/// </summary>
public static class FightRules
{
    public const int MaxHp = 100;
    public const float RoundSeconds = 60f;
    public const int WinsNeeded = 2;
    /// <summary>The fighters' start: this far apart, facing along the arena's axis.</summary>
    public const float StartGap = 3f;
    /// <summary>Each fighter stays within this of the arena's centre, along its axis.</summary>
    public const float HalfLength = 6f;
    /// <summary>A body is a cylinder of this radius (as <c>PlayerHits</c>).</summary>
    public const float BodyRadius = 0.42f;
    /// <summary>Server: room for the network on the reach check (the copies it sees are ~0.1-0.2 s old).</summary>
    public const float LagSlack = 1.0f;
    /// <summary>A challenge waits this long for an answer.</summary>
    public const float ChallengeSeconds = 30f;
    /// <summary>A challenge or a fight starts only between bodies this close.</summary>
    public const float ChallengeRange = 12f;
    /// <summary>The window after a direction for the next input of a sequence.</summary>
    public const float SequenceWindow = 0.45f;

    private static readonly MoveDef[] Table =
    {
        default,
        //   move                startup active recov reach dmg level            hitstun blockstun push knockdown
        new(FightMove.Jab,        0.10f, 0.06f, 0.16f, 1.15f, 5, HitLevel.High,     0.30f, 0.16f, 0.35f, false),
        new(FightMove.Kick,       0.20f, 0.08f, 0.28f, 1.45f, 9, HitLevel.Mid,      0.40f, 0.22f, 0.60f, false),
        new(FightMove.LowJab,     0.12f, 0.06f, 0.18f, 1.10f, 4, HitLevel.Low,      0.28f, 0.14f, 0.30f, false),
        new(FightMove.Sweep,      0.24f, 0.10f, 0.38f, 1.50f, 8, HitLevel.Low,      0.90f, 0.24f, 0.40f, true),
        new(FightMove.JumpKick,   0.14f, 0.14f, 0.20f, 1.40f, 10, HitLevel.Overhead, 0.45f, 0.24f, 0.70f, false),
        new(FightMove.Uppercut,   0.16f, 0.10f, 0.45f, 1.20f, 14, HitLevel.Mid,     0.90f, 0.30f, 1.20f, true),
        new(FightMove.StringKick, 0.14f, 0.08f, 0.30f, 1.40f, 10, HitLevel.Mid,     0.50f, 0.24f, 0.90f, false),
        new(FightMove.Finisher,   0.30f, 0.20f, 0.60f, 1.80f, 0, HitLevel.Mid,      2.00f, 0.00f, 4.00f, true),
    };

    public static bool IsMove(FightMove m) => m > FightMove.None && (int)m < Table.Length;

    public static MoveDef Def(FightMove m) => IsMove(m) ? Table[(int)m] : default;

    /// <summary>The replicated pose of a move.</summary>
    public static byte PoseOf(FightMove m) => (byte)((int)FightStance.MoveBase + (int)m);

    /// <summary>The move a replicated pose shows, or <see cref="FightMove.None"/>.</summary>
    public static FightMove MoveOf(byte pose) =>
        pose >= (byte)FightStance.MoveBase && IsMove((FightMove)(pose - (byte)FightStance.MoveBase))
            ? (FightMove)(pose - (byte)FightStance.MoveBase) : FightMove.None;

    /// <summary>
    /// What a move does to a victim in <paramref name="pose"/>: highs fly over a crouch, lows under a
    /// jump; a standing guard stops all but lows, a crouching guard all but overheads; nothing lands
    /// on a body that is down. Only the finisher reaches a dazed loser, and nothing else does.
    /// </summary>
    public static StrikeOutcome Resolve(FightMove move, byte pose)
    {
        if (!IsMove(move)) return StrikeOutcome.Refused;
        var level = Def(move).Level;
        var stance = (FightStance)pose;
        if (stance == FightStance.Dazed) return move == FightMove.Finisher ? StrikeOutcome.Hit : StrikeOutcome.Whiff;
        if (move == FightMove.Finisher) return StrikeOutcome.Whiff;
        switch (stance)
        {
            case FightStance.None:
            case FightStance.Down:
                return StrikeOutcome.Whiff;
            case FightStance.Crouch:
                return level == HitLevel.High ? StrikeOutcome.Whiff : StrikeOutcome.Hit;
            case FightStance.Air:
                return level == HitLevel.Low ? StrikeOutcome.Whiff : StrikeOutcome.Hit;
            case FightStance.GuardHigh:
                return level == HitLevel.Low ? StrikeOutcome.Hit : StrikeOutcome.Blocked;
            case FightStance.GuardLow:
                return level == HitLevel.High ? StrikeOutcome.Whiff
                    : level == HitLevel.Overhead ? StrikeOutcome.Hit : StrikeOutcome.Blocked;
            case FightStance.BlockStun:
                // still behind the guard it blocked with; a low after a high block gets through
                return level == HitLevel.Low ? StrikeOutcome.Hit : StrikeOutcome.Blocked;
        }
        // a move of its own's crouching versions duck highs as the crouch does
        var own = MoveOf(pose);
        if ((own == FightMove.LowJab || own == FightMove.Sweep) && level == HitLevel.High) return StrikeOutcome.Whiff;
        if (own == FightMove.JumpKick && level == HitLevel.Low) return StrikeOutcome.Whiff;
        return StrikeOutcome.Hit;
    }

    /// <summary>
    /// Whether the server takes a strike reported at <paramref name="distance"/> metres between the
    /// two bodies' centres (its own, slightly old, copies of them).
    /// </summary>
    public static bool InReach(FightMove move, float distance) =>
        IsMove(move) && distance <= Def(move).Reach + BodyRadius + LagSlack;
}

/// <summary>A button or direction fed to <see cref="FightInput"/>. Directions are the fighter's own: forward is towards the opponent.</summary>
public enum FightKey : byte { Down, Forward, Back, Up, Punch, Kick }

/// <summary>
/// Turns the presses of one fighter into moves (#495), the same on every device: the input comes
/// from named actions. Down, forward + punch is the uppercut; down, down + kick the finisher (only
/// asked for when the caller allows it); a kick during the second jab of a jab, jab is the string.
/// </summary>
public sealed class FightInput
{
    private readonly (FightKey Key, double At)[] _recent = new (FightKey, double)[8];
    private int _count;
    private FightMove _last;
    private int _jabs;
    private double _lastAt = double.NegativeInfinity;

    /// <summary>Remembers a direction (an edge: the moment it is first pushed).</summary>
    public void Direction(FightKey key, double now)
    {
        if (key is FightKey.Punch or FightKey.Kick) return;
        if (_count == _recent.Length)
        {
            Array.Copy(_recent, 1, _recent, 0, _recent.Length - 1);
            _count--;
        }
        _recent[_count++] = (key, now);
    }

    /// <summary>
    /// The move a punch or kick pressed at <paramref name="now"/> starts.
    /// <paramref name="crouching"/> and <paramref name="airborne"/> are the body's state;
    /// <paramref name="finisherAllowed"/> whether the opponent is a dazed loser.
    /// </summary>
    public FightMove Press(FightKey button, double now, bool crouching, bool airborne, bool finisherAllowed)
    {
        FightMove move;
        if (button == FightKey.Kick && finisherAllowed && Sequence(now, FightKey.Down, FightKey.Down))
            move = FightMove.Finisher;
        else if (button == FightKey.Punch && !airborne && Sequence(now, FightKey.Down, FightKey.Forward))
            move = FightMove.Uppercut;
        else if (airborne)
            move = button == FightKey.Kick ? FightMove.JumpKick : FightMove.None;
        else if (crouching)
            move = button == FightKey.Kick ? FightMove.Sweep : FightMove.LowJab;
        else if (button == FightKey.Kick && _last == FightMove.Jab && _jabs >= 2
                 && now - _lastAt <= FightRules.Def(FightMove.Jab).Total + 0.12)
            move = FightMove.StringKick;
        else
            move = button == FightKey.Kick ? FightMove.Kick : FightMove.Jab;

        if (move == FightMove.None) return move;
        _jabs = move == FightMove.Jab ? (_last == FightMove.Jab && now - _lastAt <= FightRules.Def(FightMove.Jab).Total + 0.12 ? _jabs + 1 : 1) : 0;
        _last = move;
        _lastAt = now;
        if (move is FightMove.Uppercut or FightMove.Finisher) _count = 0;
        return move;
    }

    /// <summary>Whether the last directions were <paramref name="a"/> then <paramref name="b"/>, each within the window.</summary>
    private bool Sequence(double now, FightKey a, FightKey b)
    {
        if (_count < 2) return false;
        var (k2, t2) = _recent[_count - 1];
        var (k1, t1) = _recent[_count - 2];
        return k1 == a && k2 == b && t2 - t1 <= FightRules.SequenceWindow && now - t2 <= FightRules.SequenceWindow;
    }

    public void Reset()
    {
        _count = 0;
        _last = FightMove.None;
        _jabs = 0;
        _lastAt = double.NegativeInfinity;
    }
}

/// <summary>The phase a match is in.</summary>
public enum FightPhase : byte
{
    /// <summary>"ROUND n" then "FIGHT!": the fighters are placed and hold.</summary>
    Intro = 0,
    Live = 1,
    /// <summary>K.O. or TIME shown; the next round follows.</summary>
    RoundOver = 2,
    /// <summary>FINISH HIM: the deciding round is over, the loser dazed, the winner may finish.</summary>
    FinishHim = 3,
    Done = 4,
}

/// <summary>Why a round or a match ended, for the banner.</summary>
public enum FightEnd : byte { None, Ko, Perfect, Time, Draw, Forfeit, Fatality }

/// <summary>
/// One match between two fighters, owned by the server (#495): HP, rounds won, the round timer and
/// the phases, and the strike budget. The Godot side feeds it strikes (already checked for reach)
/// and the clock, and broadcasts what it says.
/// </summary>
public sealed class FightMatch
{
    public const float IntroSeconds = 2.4f;
    public const float RoundOverSeconds = 3.2f;
    public const float FinishSeconds = 5f;
    public const float DoneSeconds = 4f;
    /// <summary>The strike budget: a move counts only if the last one ended, less this much for the network.</summary>
    public const float RateLeeway = 0.3f;

    public readonly long A, B;
    public int HpA = FightRules.MaxHp, HpB = FightRules.MaxHp;
    public int WinsA, WinsB;
    public int Round = 1;
    public FightPhase Phase = FightPhase.Intro;
    public FightEnd LastEnd;
    /// <summary>The round's or the match's winner, 0 for none (a draw, or still going).</summary>
    public long Winner;
    private double _phaseAt;
    private double _roundStart;
    private readonly Dictionary<long, (FightMove Move, double At)> _lastStrike = new();

    public FightMatch(long a, long b, double now)
    {
        A = a;
        B = b;
        _phaseAt = now;
    }

    public bool Has(long who) => who == A || who == B;
    public long Other(long who) => who == A ? B : A;
    public int Hp(long who) => who == A ? HpA : HpB;

    /// <summary>Seconds left in the round (the whole round before it starts).</summary>
    public float TimeLeft(double now) => Phase == FightPhase.Live
        ? (float)Math.Max(0, FightRules.RoundSeconds - (now - _roundStart))
        : Phase == FightPhase.Intro ? FightRules.RoundSeconds : 0;

    /// <summary>Advances the phases. True when something the clients show changed.</summary>
    public bool Tick(double now)
    {
        double since = now - _phaseAt;
        switch (Phase)
        {
            case FightPhase.Intro when since >= IntroSeconds:
                Phase = FightPhase.Live;
                _roundStart = now;
                _phaseAt = now;
                return true;
            case FightPhase.Live when now - _roundStart >= FightRules.RoundSeconds:
                EndRound(HpA == HpB ? 0 : HpA > HpB ? A : B, HpA == HpB ? FightEnd.Draw : FightEnd.Time, now);
                return true;
            case FightPhase.RoundOver when since >= RoundOverSeconds:
                Round++;
                HpA = HpB = FightRules.MaxHp;
                Winner = 0;
                LastEnd = FightEnd.None;
                Phase = FightPhase.Intro;
                _phaseAt = now;
                _lastStrike.Clear();
                return true;
            case FightPhase.FinishHim when since >= FinishSeconds:
                Phase = FightPhase.Done;
                _phaseAt = now;
                return true;
        }
        return false;
    }

    /// <summary>Whether the match is over and its last banner has been shown long enough to drop it.</summary>
    public bool Expired(double now) => Phase == FightPhase.Done && now - _phaseAt >= DoneSeconds;

    /// <summary>
    /// A strike from <paramref name="attacker"/>, already in reach, on a victim in
    /// <paramref name="victimPose"/>. Applies the damage and ends the round on a K.O.
    /// </summary>
    public StrikeOutcome Strike(long attacker, FightMove move, byte victimPose, double now)
    {
        if (!Has(attacker) || !FightRules.IsMove(move)) return StrikeOutcome.Refused;
        bool finishing = Phase == FightPhase.FinishHim && attacker == Winner;
        if (move == FightMove.Finisher ? !finishing : Phase != FightPhase.Live) return StrikeOutcome.Refused;
        var def = FightRules.Def(move);
        if (_lastStrike.TryGetValue(attacker, out var last))
        {
            // a string's kick may cut into the jab's recovery
            float need = FightRules.Def(last.Move).Total - RateLeeway;
            if (move == FightMove.StringKick && last.Move == FightMove.Jab) need = FightRules.Def(FightMove.Jab).Startup;
            if (now - last.At < need) return StrikeOutcome.Refused;
        }
        _lastStrike[attacker] = (move, now);

        var outcome = FightRules.Resolve(move, victimPose);
        if (outcome != StrikeOutcome.Hit) return outcome;
        if (move == FightMove.Finisher)
        {
            LastEnd = FightEnd.Fatality;
            Phase = FightPhase.Done;
            _phaseAt = now;
            return outcome;
        }
        long victim = Other(attacker);
        if (victim == A) HpA = Math.Max(0, HpA - def.Damage);
        else HpB = Math.Max(0, HpB - def.Damage);
        if (Hp(victim) == 0)
            EndRound(attacker, Hp(attacker) == FightRules.MaxHp ? FightEnd.Perfect : FightEnd.Ko, now);
        return outcome;
    }

    /// <summary><paramref name="quitter"/> left or disconnected: the other wins the match.</summary>
    public void Forfeit(long quitter, double now)
    {
        if (!Has(quitter) || Phase == FightPhase.Done) return;
        Winner = Other(quitter);
        LastEnd = FightEnd.Forfeit;
        Phase = FightPhase.Done;
        _phaseAt = now;
    }

    private void EndRound(long winner, FightEnd end, double now)
    {
        Winner = winner;
        LastEnd = end;
        // a draw is a round for both, as in Tekken
        if (winner == A || winner == 0) WinsA++;
        if (winner == B || winner == 0) WinsB++;
        _phaseAt = now;
        bool over = WinsA >= FightRules.WinsNeeded || WinsB >= FightRules.WinsNeeded;
        if (!over) Phase = FightPhase.RoundOver;
        else
        {
            Winner = WinsA == WinsB ? 0 : WinsA > WinsB ? A : B;
            // only a K.O. leaves someone standing to be finished
            Phase = Winner != 0 && end is FightEnd.Ko or FightEnd.Perfect ? FightPhase.FinishHim : FightPhase.Done;
            if (Phase == FightPhase.Done && Winner == 0) LastEnd = FightEnd.Draw;
        }
    }
}
