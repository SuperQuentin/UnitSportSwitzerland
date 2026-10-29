namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// The sword's moves (#1371). The value goes over the wire (<c>SwordService.Act</c>): append only.
/// <see cref="Charge"/> is not a blow but the wind-up a remote peer draws; <see cref="None"/> ends it.
/// </summary>
public enum SwordMove : byte
{
    None = 0,
    Slash1 = 1, Slash2 = 2, Slash3 = 3, Slash4 = 4, Slash5 = 5,
    Charged = 6,
    Plunge = 7,
    Dodge = 8,
    Charge = 9,
}

/// <summary>One move's frame data and effect. Times are seconds from the press, except where said.</summary>
/// <param name="Startup">Until the blade is live.</param>
/// <param name="Active">How long it is live (the hit window).</param>
/// <param name="Recovery">After the active frames, until the hands are free again.</param>
/// <param name="Cancel">From here a press of attack (the next swing) or of dodge is taken instead of waiting out the recovery.</param>
/// <param name="Reach">Metres from the body's centre to the blade's far end.</param>
/// <param name="ConeDeg">Half-angle of the blow in front of the body, degrees (180: all round).</param>
/// <param name="Damage">Health taken from the target.</param>
/// <param name="Knock">Metres per second the target is pushed away at the start of the push.</param>
/// <param name="HitStop">Seconds the attacker's hands freeze on a hit (the blow's weight).</param>
/// <param name="Cost">Stamina points paid when it starts (a held charge: when it is let go).</param>
/// <param name="Lunge">Metres per second the attacker steps forward during startup and active.</param>
public readonly record struct SwordMoveDef(SwordMove Move, float Startup, float Active, float Recovery, float Cancel,
    float Reach, float ConeDeg, float Damage, float Knock, float HitStop, float Cost, float Lunge)
{
    public float Total => Startup + Active + Recovery;
}

/// <summary>What the machine does at a frame boundary, read after <see cref="SwordMachine.Step"/>.</summary>
public enum SwordPhase { Idle, Charging, Startup, Active, Recovery }

/// <summary>The held buttons of one frame, the machine's input: gameplay never reads a key (a bot sets the same).</summary>
public struct SwordInput
{
    /// <summary>The attack button is down now (a press is the edge: the machine finds it).</summary>
    public bool Attack;
    /// <summary>The dodge button is down now.</summary>
    public bool Dodge;
    /// <summary>The body is off the ground.</summary>
    public bool Airborne;
    /// <summary>The body is falling (or gliding) fast enough for a plunge.</summary>
    public bool Falling;
    /// <summary>The body just stands on something again (ends a plunge's dive).</summary>
    public bool Grounded;
}

/// <summary>
/// The sword's stamina (#1371): a plain meter beside the climbing one (that is infinite outside the Adventure
/// layer; a fight needs it everywhere). Spent by a dodge and a charged blow, back after a short pause.
/// </summary>
public struct SwordStamina
{
    public const float Max = 100f;
    /// <summary>Seconds after a spend before it refills.</summary>
    public const float Delay = 0.7f;
    /// <summary>Points a second once it refills.</summary>
    public const float Regen = 30f;

    public float Points;
    private float _since;

    public static SwordStamina Full() => new() { Points = Max, _since = Delay };

    public readonly float Share => Points / Max;

    public void Step(float dt)
    {
        _since += dt;
        if (_since >= Delay) Points = Math.Min(Max, Points + Regen * dt);
    }

    /// <summary>Pays <paramref name="cost"/> if it is there, less <paramref name="leeway"/> (the server's slack for a client's clock).</summary>
    public bool TrySpend(float cost, float leeway = 0f)
    {
        if (cost <= 0f) return true;
        if (Points < cost - leeway) return false;
        Points = Math.Max(0f, Points - cost);
        _since = 0f;
        return true;
    }
}

/// <summary>The sword's frame data, geometry and the helper rules both ends share (#1371, <c>docs/notes/combat/sword-combat.md</c>).</summary>
public static class SwordRules
{
    public const int ComboLength = 5;
    /// <summary>A swing's next press continues the combo when it comes this soon after the last one ended; later it starts over.</summary>
    public const double ComboGrace = 0.35;
    /// <summary>A press this long before the cancel window opens is kept and used when it does.</summary>
    public const double BufferWindow = 0.22;
    /// <summary>Held this long after a swing, the attack is a charge; letting go earlier than <see cref="ChargeMin"/> does nothing.</summary>
    public const double ChargeMin = 0.45, ChargeMax = 1.2;
    public const float DodgeCost = 20f, ChargedCost = 25f;
    /// <summary>The dodge's untouchable window, seconds from its start.</summary>
    public const float IFramesFrom = 0.03f, IFramesTo = 0.27f;
    /// <summary>Metres per second of a dodge, along the stick (or back when it is still).</summary>
    public const float DodgeSpeed = 9f;
    /// <summary>A pause before the next dodge, seconds from the end of the last.</summary>
    public const double DodgeGap = 0.12;
    /// <summary>The longest a plunge dives before it gives up (nothing to land on).</summary>
    public const double PlungeMax = 2.5;
    /// <summary>Downward speed of the dive, m/s.</summary>
    public const float PlungeSpeed = 22f;
    /// <summary>Falling faster than this (m/s) an attack is a plunge, not a swing in the air.</summary>
    public const float PlungeFall = 2.5f;
    /// <summary>Horizontal radius of a body for a blow: a cylinder, as <c>PlayerHits</c> and the dummy.</summary>
    public const float BodyRadius = 0.42f;
    /// <summary>The dummy's and a body's height, for a plunge (above the feet) and the vertical reach.</summary>
    public const float BodyHeight = 1.85f;
    /// <summary>How far the server lets a body be off from where its owner drew it, metres (a quarter second of running).</summary>
    public const float LagSlack = 1.6f;
    /// <summary>The server adds this many degrees to a blow's cone (the owner's view of the aim is a little old).</summary>
    public const float ConeSlackDeg = 30f;
    /// <summary>Soft lock-on: the nearest target within this many metres and degrees of the aim is where a swing turns to.</summary>
    public const float SoftRange = 5.5f, SoftConeDeg = 55f;
    /// <summary>Hard lock-on: a target stays locked out to this far and until it is gone.</summary>
    public const float HardRange = 16f;

    private static readonly SwordMoveDef[] Defs =
    {
        //                          move             startup active recov cancel reach cone dmg  knock stop cost lunge
        new(SwordMove.None,         0f,    0f,    0f,   0f,   0f,  0f,  0f,  0f,  0f,  0f,   0f),
        new(SwordMove.Slash1,       0.10f, 0.08f, 0.16f, 0.20f, 2.3f, 55f, 12f, 3.5f, 0.05f, 0f, 2.5f),
        new(SwordMove.Slash2,       0.10f, 0.08f, 0.16f, 0.20f, 2.3f, 55f, 13f, 3.5f, 0.05f, 0f, 2.5f),
        new(SwordMove.Slash3,       0.12f, 0.08f, 0.18f, 0.22f, 2.4f, 60f, 15f, 4.5f, 0.06f, 0f, 2.5f),
        new(SwordMove.Slash4,       0.12f, 0.10f, 0.20f, 0.24f, 2.4f, 60f, 16f, 5.0f, 0.06f, 0f, 3.0f),
        new(SwordMove.Slash5,       0.18f, 0.12f, 0.34f, 0.40f, 2.7f, 75f, 24f, 9.0f, 0.10f, 0f, 3.5f),
        new(SwordMove.Charged,      0.15f, 0.12f, 0.45f, 0.50f, 3.0f, 85f, 40f, 11f, 0.13f, ChargedCost, 4.0f),
        new(SwordMove.Plunge,       0.00f, 0.12f, 0.40f, 0.30f, 2.8f, 180f, 30f, 9.0f, 0.10f, 0f, 0f),
        new(SwordMove.Dodge,        0.00f, 0.00f, 0.38f, 0.26f, 0f,  0f,  0f,  0f,  0f,   DodgeCost, 0f),
        new(SwordMove.Charge,       0f,    0f,    0f,   0f,   0f,  0f,  0f,  0f,  0f,  0f,   0f),
    };

    public static SwordMoveDef Def(SwordMove move) => (int)move < Defs.Length ? Defs[(int)move] : Defs[0];

    public static bool IsBlow(SwordMove move) => move is >= SwordMove.Slash1 and <= SwordMove.Plunge;

    /// <summary>The swing of the combo at <paramref name="index"/> (1..5).</summary>
    public static SwordMove Slash(int index) => (SwordMove)Math.Clamp(index, 1, ComboLength);

    /// <summary>The longest a peer may take over a move before it is over, for the server's window on a hit.</summary>
    public static double Window(SwordMove move) => move == SwordMove.Plunge ? PlungeMax + Def(move).Total : Def(move).Total;

    /// <summary>
    /// Whether a body at (<paramref name="tx"/>, <paramref name="tz"/>) is inside a blow from
    /// (<paramref name="ax"/>, <paramref name="az"/>) facing (<paramref name="fx"/>, <paramref name="fz"/>) (a unit vector):
    /// within <paramref name="reach"/> plus the body's radius, and within <paramref name="coneDeg"/> of the facing.
    /// Pure: the owner traces with it, the server checks the same with its slack.
    /// </summary>
    public static bool InCone(float ax, float az, float fx, float fz, float tx, float tz, float reach, float coneDeg, out float distance)
    {
        float dx = tx - ax, dz = tz - az;
        distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance > reach + BodyRadius) return false;
        if (coneDeg >= 179f || distance < 0.05f) return true;
        float cos = (dx * fx + dz * fz) / distance;
        // the body is a disc: its edge is up to asin(r / d) off the centre line
        float edge = distance > BodyRadius ? MathF.Asin(BodyRadius / distance) * 180f / MathF.PI : 90f;
        return MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI <= coneDeg + edge;
    }

    /// <summary>Whether a target's feet (<paramref name="dy"/> above the attacker's) are within a blade's vertical reach.</summary>
    public static bool InHeight(float dy) => dy > -2.2f && dy < BodyHeight;

    /// <summary>A candidate for a lock or a blow: a place on the ground plane and an id the caller knows.</summary>
    public readonly record struct Candidate(float X, float Z, long Id);

    /// <summary>
    /// The target a swing turns to (soft lock-on) or a lock picks: the candidate nearest the aim, by angle plus a
    /// little distance, within <paramref name="range"/> and <paramref name="coneDeg"/> of (<paramref name="fx"/>, <paramref name="fz"/>).
    /// Returns its index in <paramref name="candidates"/>, or -1.
    /// </summary>
    public static int Best(ReadOnlySpan<Candidate> candidates, float ax, float az, float fx, float fz, float range, float coneDeg)
    {
        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < candidates.Length; i++)
        {
            float dx = candidates[i].X - ax, dz = candidates[i].Z - az;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d > range || d < 0.05f) continue;
            float angle = MathF.Acos(Math.Clamp((dx * fx + dz * fz) / d, -1f, 1f));
            if (angle * 180f / MathF.PI > coneDeg) continue;
            float score = angle + d * 0.06f;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>
    /// The blade's pose a moment into a move, from <paramref name="t"/> (0..1 of its total): the arc it sweeps as
    /// (yaw, pitch, roll) in radians about the hand, and how far it reaches forward in metres. Drawn on the held node
    /// by every peer (a rigid part: nothing is rebuilt), so the one function is also what the unit test pins.
    /// </summary>
    public static (float Yaw, float Pitch, float Roll, float Forward) Arc(SwordMove move, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var d = Def(move);
        float total = Math.Max(0.0001f, d.Total);
        float s = d.Startup / total, a = (d.Startup + d.Active) / total;
        // wind-up back to the blow's start, a fast sweep through the active part, then easing home
        float w = t < s ? Ease(t / Math.Max(0.0001f, s)) : t < a ? 1f + (t - s) / Math.Max(0.0001f, a - s) : 2f + Ease((t - a) / Math.Max(0.0001f, 1f - a));
        // w: 0..1 wind-up, 1..2 sweep, 2..3 recovery
        switch (move)
        {
            case SwordMove.Slash1: return Sweep(w, 1.0f, -0.4f, 0.2f);     // right to left, a little down
            case SwordMove.Slash2: return Sweep(w, -1.0f, 0.4f, -0.2f);    // back left to right
            case SwordMove.Slash3: return Sweep(w, 0.9f, -0.9f, 0.5f);     // a rising cut
            case SwordMove.Slash4: return Sweep(w, -0.9f, 0.8f, -0.5f);
            case SwordMove.Slash5: return Chop(w, 1.2f);                   // an overhead finisher
            case SwordMove.Charged: return Chop(w, 1.7f);
            case SwordMove.Plunge: return PlungePose(w);
            case SwordMove.Charge: return (0.5f, -0.9f, 0.4f, -0.1f);      // held back over the shoulder
            default: return (0f, 0f, 0f, 0f);
        }
    }

    private static float Ease(float x) => x * x * (3f - 2f * x);

    private static (float, float, float, float) Sweep(float w, float side, float lift, float roll)
    {
        if (w < 1f) return (-side * 0.9f * w, lift * 0.6f * w, roll * w, -0.05f * w);
        if (w < 2f) { float k = w - 1f; return (-side * 0.9f + side * 1.9f * k, lift * 0.6f * (1f - k), roll * (1f - 2f * k), 0.28f * k - 0.05f * (1f - k)); }
        float r = 1f - (w - 2f);
        return (side * 1.0f * r, 0f, -roll * r, 0.28f * r);
    }

    private static (float, float, float, float) Chop(float w, float size)
    {
        if (w < 1f) return (0f, -size * w, 0f, -0.05f * w);
        if (w < 2f) { float k = w - 1f; return (0f, -size + (size + 0.6f) * k, 0f, 0.3f * k); }
        float r = 1f - (w - 2f);
        return (0f, 0.6f * r, 0f, 0.3f * r);
    }

    private static (float, float, float, float) PlungePose(float w)
    {
        // blade pointed down the whole time: held over the head, then driven into the ground
        if (w < 2f) return (0f, 1.35f, 0f, 0.2f);
        float r = 1f - (w - 2f);
        return (0f, 1.35f * r, 0f, 0.2f * r);
    }

    /// <summary>A blow's push on a target at <paramref name="distance"/>: a little less at the far end of the reach.</summary>
    public static float KnockAt(SwordMove move, float distance)
    {
        var d = Def(move);
        float far = d.Reach <= 0f ? 0f : Math.Clamp(distance / (d.Reach + BodyRadius), 0f, 1f);
        return d.Knock * (1f - 0.35f * far);
    }
}

/// <summary>
/// One fighter's sword, a state machine over the clock (#1371): the combo, the held charge, the plunge and the dodge.
/// Pure: the player's body, a bot's hands and the unit tests all step the same one. It reports in its public flags
/// (<see cref="Began"/>, <see cref="ActiveBegan"/>, <see cref="Ended"/>) what happened in the last <see cref="Step"/>.
/// </summary>
public sealed class SwordMachine
{
    public SwordMove Move { get; private set; }
    public SwordStamina Stamina = SwordStamina.Full();
    /// <summary>The combo's last swing, 1..5 (0 none).</summary>
    public int Combo { get; private set; }
    public double MoveAt { get; private set; }
    public double EndedAt { get; private set; } = double.NegativeInfinity;
    public bool Charging { get; private set; }
    public double ChargeAt { get; private set; }

    /// <summary>A move started this step, or its active frames began, or it ended.</summary>
    public bool Began { get; private set; }
    public bool ActiveBegan { get; private set; }
    public bool Ended { get; private set; }
    /// <summary>The charge began or was let go this step (a remote peer draws the wind-up).</summary>
    public bool ChargeChanged { get; private set; }
    /// <summary>The attack needed stamina it did not have this step (the HUD blinks).</summary>
    public bool Refused { get; private set; }

    /// <summary>The hands are frozen until this time: a hit's weight (<see cref="SwordMoveDef.HitStop"/>).</summary>
    public double FrozenUntil { get; private set; }

    private bool _fired, _landed, _attackWas, _dodgeWas;
    private double _atkBuf = double.NegativeInfinity, _dodgeBuf = double.NegativeInfinity, _last, _dodgeReadyAt;

    /// <summary>Seconds into the current move (0 when idle).</summary>
    public double Into(double now) => Move == SwordMove.None ? 0 : now - MoveAt;

    public SwordPhase Phase(double now)
    {
        if (Move == SwordMove.None) return Charging ? SwordPhase.Charging : SwordPhase.Idle;
        var d = SwordRules.Def(Move);
        double into = now - MoveAt;
        if (Move == SwordMove.Plunge && !_landed) return SwordPhase.Startup;
        return into < d.Startup ? SwordPhase.Startup : into < d.Startup + d.Active ? SwordPhase.Active : SwordPhase.Recovery;
    }

    /// <summary>0..1 of the current move's total (the blade's arc).</summary>
    public float Progress(double now)
    {
        if (Move == SwordMove.None) return 0f;
        var d = SwordRules.Def(Move);
        if (Move == SwordMove.Plunge && !_landed) return 0f;
        return d.Total <= 0f ? 0f : (float)Math.Clamp((now - MoveAt) / d.Total, 0.0, 1.0);
    }

    /// <summary>The charge, 0..1 of <see cref="SwordRules.ChargeMax"/> (0 when not charging).</summary>
    public float ChargeShare(double now) => Charging ? (float)Math.Clamp((now - ChargeAt) / SwordRules.ChargeMax, 0.0, 1.0) : 0f;

    /// <summary>The body cannot be hurt right now: inside a dodge's window.</summary>
    public bool Invulnerable(double now) => Move == SwordMove.Dodge && now - MoveAt is var into
        && into >= SwordRules.IFramesFrom && into <= SwordRules.IFramesTo;

    /// <summary>The blade is live: the active frames of a blow.</summary>
    public bool Live(double now) => SwordRules.IsBlow(Move) && Phase(now) == SwordPhase.Active;

    /// <summary>The hands are in a blow or a dodge: the body is not free to act another way.</summary>
    public bool Busy => Move != SwordMove.None;

    /// <summary>Freezes the hands for <paramref name="seconds"/> from <paramref name="now"/> (a blow landed).</summary>
    public void Freeze(double now, double seconds) => FrozenUntil = Math.Max(FrozenUntil, now + seconds);

    /// <summary>Drops everything (the sword is put away, the body is down).</summary>
    public void Reset()
    {
        bool had = Move != SwordMove.None || Charging;
        Move = SwordMove.None;
        Charging = false;
        Combo = 0;
        _fired = _landed = false;
        Ended = had;
        ChargeChanged = false;
    }

    /// <summary>Advances to <paramref name="now"/> (seconds, the game's clock) with this frame's held buttons.</summary>
    public void Step(double now, in SwordInput input)
    {
        Began = ActiveBegan = Ended = ChargeChanged = Refused = false;
        double dt = Math.Clamp(now - _last, 0.0, 0.25);
        _last = now;

        bool pressed = input.Attack && !_attackWas, dodgePressed = input.Dodge && !_dodgeWas;
        _attackWas = input.Attack;
        _dodgeWas = input.Dodge;
        if (pressed) _atkBuf = now;
        if (dodgePressed) _dodgeBuf = now;

        // a landed blow's weight: the clocks wait with the hands
        if (now < FrozenUntil)
        {
            MoveAt += dt;
            if (Charging) ChargeAt += dt;
            return;
        }
        if (!Charging) Stamina.Step((float)dt);

        bool atk = now - _atkBuf <= SwordRules.BufferWindow, dodge = now - _dodgeBuf <= SwordRules.BufferWindow;

        if (Move != SwordMove.None)
        {
            var d = SwordRules.Def(Move);
            double into = now - MoveAt;
            if (Move == SwordMove.Plunge && !_landed)
            {
                if (input.Grounded && into > 0.06) { _landed = true; MoveAt = now; _fired = true; ActiveBegan = true; return; }
                if (into > SwordRules.PlungeMax) End(now);
                return;
            }
            if (!_fired && into >= d.Startup)
            {
                _fired = true;
                if (SwordRules.IsBlow(Move)) ActiveBegan = true;
            }
            bool open = into >= d.Cancel;
            if (open && dodge && Move != SwordMove.Dodge && TryStart(SwordMove.Dodge, now)) return;
            if (open && atk && Move != SwordMove.Dodge && Move != SwordMove.Plunge && TryAttack(now, input, chained: true)) return;
            if (open && atk && Move == SwordMove.Dodge && TryAttack(now, input, chained: false)) return;
            if (into >= d.Total)
            {
                bool wasSlash = Move is >= SwordMove.Slash1 and <= SwordMove.Slash5;
                End(now);
                // still holding the button after a swing: the hands draw back for a charge
                if (wasSlash && input.Attack && Stamina.Points >= SwordRules.ChargedCost) BeginCharge(now);
            }
            return;
        }

        if (Charging)
        {
            if (dodge && TryStart(SwordMove.Dodge, now)) { EndCharge(); return; }
            if (!input.Attack)
            {
                double held = now - ChargeAt;
                EndCharge();
                if (held >= SwordRules.ChargeMin && TryStart(SwordMove.Charged, now)) return;
            }
            return;
        }

        if (dodge && TryStart(SwordMove.Dodge, now)) return;
        if (atk) TryAttack(now, input, chained: false);
    }

    private bool TryAttack(double now, in SwordInput input, bool chained)
    {
        _atkBuf = double.NegativeInfinity;
        if (input.Airborne && input.Falling && !chained)
            return TryStart(SwordMove.Plunge, now);
        int next = chained || now - EndedAt <= SwordRules.ComboGrace ? Combo % SwordRules.ComboLength + 1 : 1;
        return TryStart(SwordRules.Slash(next), now);
    }

    private bool TryStart(SwordMove move, double now)
    {
        var d = SwordRules.Def(move);
        if (move == SwordMove.Dodge)
        {
            if (now < _dodgeReadyAt) return false;
            if (!Stamina.TrySpend(d.Cost)) { Refused = true; return false; }
            _dodgeBuf = double.NegativeInfinity;
        }
        else if (!Stamina.TrySpend(d.Cost)) { Refused = true; return false; }
        bool chained = Move != SwordMove.None;
        if (chained) Ended = true;
        Move = move;
        MoveAt = now;
        _fired = false;
        _landed = false;
        Began = true;
        if (move is >= SwordMove.Slash1 and <= SwordMove.Slash5) Combo = (int)move;
        else if (move == SwordMove.Charged || move == SwordMove.Dodge || move == SwordMove.Plunge) Combo = 0;
        return true;
    }

    private void End(double now)
    {
        if (Move == SwordMove.Dodge) _dodgeReadyAt = now + SwordRules.DodgeGap;
        Move = SwordMove.None;
        EndedAt = now;
        Ended = true;
    }

    private void BeginCharge(double now)
    {
        Charging = true;
        ChargeAt = now;
        ChargeChanged = true;
    }

    private void EndCharge()
    {
        Charging = false;
        ChargeChanged = true;
    }
}
