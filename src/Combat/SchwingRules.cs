namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md). Schwingen, #1269.

/// <summary>A Schwinger's throw (named, as in the sport). Append only: the value is on the wire and in the replicated pose.</summary>
public enum SchwingThrow : byte
{
    None = 0,
    /// <summary>Pull in and lift.</summary>
    Kurz = 1,
    /// <summary>Step over, sideways.</summary>
    Uebersprung = 2,
    /// <summary>Hip throw.</summary>
    Hueften = 3,
    /// <summary>Arm over the neck, back.</summary>
    Brienzer = 4,
    /// <summary>Leg hook.</summary>
    Gammen = 5,
    /// <summary>Inside leg.</summary>
    Wyberhaken = 6,
}

/// <summary>Where a wrestler leans, in <b>its own</b> frame: towards the rival, away, or round to its left or right.</summary>
public enum SchwingLean : byte { None = 0, In = 1, Out = 2, Left = 3, Right = 4 }

public enum SchwingPhase : byte { Intro = 0, Live = 1, Ground = 2, Done = 3 }

public enum SchwingEnd : byte
{
    None = 0,
    /// <summary>Both shoulder blades in the sawdust, the winner's hand still on the trousers.</summary>
    Won = 1,
    /// <summary>The clock ran out, or the pin was lost with the grip: "gestellt".</summary>
    Gestellt = 2,
    Forfeit = 3,
}

/// <summary>How the defender met a throw.</summary>
public enum SchwingAnswer : byte { None = 0, Braced = 1, Countered = 2, Landed = 3, Down = 4 }

/// <summary>
/// What a wrestler's body is doing, as the replicated <c>FootPlayer.SchwingPose</c> carries it. 0 = not wrestling.
/// A throw being made is <see cref="ThrowBase"/> + its <see cref="SchwingThrow"/>. Append only.
/// </summary>
public enum SchwingStance : byte
{
    None = 0,
    /// <summary>Gripped, upright, a hand on the belt and a hand on the rolled trouser leg.</summary>
    Grip = 1,
    LeanIn = 2,
    LeanOut = 3,
    LeanLeft = 4,
    LeanRight = 5,
    /// <summary>Block held: weight dropped, hips back.</summary>
    Brace = 6,
    /// <summary>Thrown and lying on the back in the sawdust.</summary>
    Down = 7,
    /// <summary>Bodenarbeit on top: kneeling over the rival.</summary>
    GroundTop = 8,
    /// <summary>Bodenarbeit underneath: on the back, bridging.</summary>
    GroundBottom = 9,
    /// <summary>A throw that was answered: the thrower is off balance.</summary>
    Stumble = 10,
    /// <summary>The winner brushing the sawdust off the loser's back, then a handshake.</summary>
    Brush = 11,
    /// <summary>The loser, standing again, dusting down.</summary>
    Dusted = 12,
    ThrowBase = 32,
}

/// <summary>The tuning of one throw. Directions are in the <b>defender's</b> frame.</summary>
public readonly record struct ThrowDef(SchwingThrow Throw, string Name, float Weight, SchwingLean Dir, SchwingLean Brace, SchwingLean Counter);

/// <summary>A server event for the manager to send: a throw wound up, resolved, a ground phase.</summary>
public readonly record struct SchwingEvent(SchwingEventKind Kind, long Who, SchwingThrow Throw, SchwingAnswer Answer);

public enum SchwingEventKind : byte { Telegraph, Resolved, GroundBegan, Escaped, Ended }

/// <summary>
/// Schwingen's rules, pure (#1269): balance, grip, the throws and their answers, Bodenarbeit and the result. The fist fight's strike
/// rules do not fit a grapple, so this is its own small model that the <c>SchwingMatch</c> shell runs at 10 Hz on the server.
/// </summary>
public static class SchwingRules
{
    public const float Telegraph = 0.35f;
    /// <summary>After a throw resolves, the thrower cannot start another for this long.</summary>
    public const float Cooldown = 0.9f;
    public const float MaxBalance = 100f, MaxGrip = 100f;
    /// <summary>A defender under this balance goes down.</summary>
    public const float DownBalance = 20f;
    public const float GripCost = 10f;
    /// <summary>A throw needs at least this much grip.</summary>
    public const float GripNeeded = 15f;
    public const float GangSeconds = 180f, SchlussgangSeconds = 360f;
    public const float IntroSeconds = 2.4f, DoneSeconds = 4f, GroundSeconds = 6f;
    /// <summary>The marks of a gripped pair, apart.</summary>
    public const float GripGap = 0.9f;
    /// <summary>The ring is this wide (m); a bout starts at its middle, off a festival wherever the two stand.</summary>
    public const float RingDiameter = 14f;
    public const float ChallengeRange = 12f, ChallengeSeconds = 30f;
    /// <summary>A body further than this from the bout's centre has left it: a forfeit.</summary>
    public const float LeaveDistance = 18f;
    /// <summary>Acts from one wrestler: the server takes at most this many in a second.</summary>
    public const int ActsPerSecond = 20;
    /// <summary>The Bodenarbeit tug: seconds between two presses of one wrestler.</summary>
    public const float TugGap = 0.08f;

    // lean economy, per second
    public const float LeanGripCost = 8f, PushCost = 3f, GripRegen = 3f, BalanceRegen = 6f;

    private static readonly ThrowDef[] Table =
    [
        new(SchwingThrow.Kurz, "Kurz", 22, SchwingLean.In, SchwingLean.Out, SchwingLean.Left),
        new(SchwingThrow.Uebersprung, "Übersprung", 26, SchwingLean.Left, SchwingLean.Right, SchwingLean.In),
        new(SchwingThrow.Hueften, "Hüfter", 28, SchwingLean.In, SchwingLean.Out, SchwingLean.Right),
        new(SchwingThrow.Brienzer, "Brienzer", 34, SchwingLean.Out, SchwingLean.In, SchwingLean.Left),
        new(SchwingThrow.Gammen, "Gammen", 30, SchwingLean.Right, SchwingLean.Left, SchwingLean.In),
        new(SchwingThrow.Wyberhaken, "Wyberhaken", 32, SchwingLean.Left, SchwingLean.Right, SchwingLean.Out),
    ];

    public static IReadOnlyList<ThrowDef> Throws => Table;

    public static ThrowDef Def(SchwingThrow t) => t is >= SchwingThrow.Kurz and <= SchwingThrow.Wyberhaken ? Table[(int)t - 1] : default;

    public static bool Valid(int throwId) => throwId is >= 1 and <= 6;

    public static string Name(SchwingThrow t) => Def(t).Name ?? "";

    /// <summary>Which throw the attack buttons and the stick make: punch + stick, kick = Gammen (Wyberhaken with the stick back).</summary>
    public static SchwingThrow Choose(bool kick, SchwingLean stick) => kick
        ? stick == SchwingLean.Out ? SchwingThrow.Wyberhaken : SchwingThrow.Gammen
        : stick switch
        {
            SchwingLean.Out => SchwingThrow.Brienzer,
            SchwingLean.Left => SchwingThrow.Hueften,
            SchwingLean.Right => SchwingThrow.Uebersprung,
            _ => SchwingThrow.Kurz,
        };

    /// <summary>How the defender's lean at the end of the telegraph meets <paramref name="t"/>.</summary>
    public static SchwingAnswer Answer(SchwingThrow t, SchwingLean lean, bool brace)
    {
        var d = Def(t);
        if (d.Throw == SchwingThrow.None) return SchwingAnswer.None;
        if (lean == d.Counter) return SchwingAnswer.Countered;
        if (brace && lean == d.Brace) return SchwingAnswer.Braced;
        return SchwingAnswer.Landed;
    }

    /// <summary>The lean the stick makes, in the wrestler's own frame (<paramref name="toward"/> is towards the rival, <paramref name="round"/> to its left).</summary>
    public static SchwingLean LeanOf(float toward, float round)
    {
        float at = Math.Abs(toward), ar = Math.Abs(round);
        if (Math.Max(at, ar) < 0.5f) return SchwingLean.None;
        if (at >= ar) return toward > 0 ? SchwingLean.In : SchwingLean.Out;
        return round > 0 ? SchwingLean.Left : SchwingLean.Right;
    }

    /// <summary>The pose of a wrestler standing in the grip, from its lean and brace.</summary>
    public static SchwingStance Standing(SchwingLean lean, bool brace) => brace ? SchwingStance.Brace : lean switch
    {
        SchwingLean.In => SchwingStance.LeanIn,
        SchwingLean.Out => SchwingStance.LeanOut,
        SchwingLean.Left => SchwingStance.LeanLeft,
        SchwingLean.Right => SchwingStance.LeanRight,
        _ => SchwingStance.Grip,
    };

    public static int PoseOf(SchwingThrow t) => (int)SchwingStance.ThrowBase + (int)t;

    public static SchwingThrow ThrowOf(int pose) => pose > (int)SchwingStance.ThrowBase && pose <= (int)SchwingStance.ThrowBase + 6 ? (SchwingThrow)(pose - (int)SchwingStance.ThrowBase) : SchwingThrow.None;

    /// <summary>Whether the body is lying in the sawdust (flat on the floor).</summary>
    public static bool Lying(int pose) => pose is (int)SchwingStance.Down or (int)SchwingStance.GroundBottom;

    /// <summary>What a bout is worth to a player: (Batzen, party points). A Schlussgang counts half as much again.</summary>
    public static (int Batzen, int Points) Reward(bool won, bool gestellt, bool schlussgang)
    {
        var (b, p) = won ? (30, 100) : gestellt ? (20, 70) : (12, 40);
        return schlussgang ? (b * 3 / 2, p * 3 / 2) : (b, p);
    }

    /// <summary>The bout's points as the sport counts them: a win 10, gestellt 9 each, a loss 8.5.</summary>
    public static float Score(bool won, bool gestellt) => won ? 10f : gestellt ? 9f : 8.5f;
}

/// <summary>
/// One bout (#1269), run on the server at 10 Hz from <c>SchwingManager</c>. Two wrestlers start gripped; leaning costs grip, a named throw
/// has a 0.35 s telegraph the defender answers by bracing or countering; a throw that lands past the balance floor ends on the ground,
/// where a tug of presses decides who has both shoulder blades down.
/// </summary>
public sealed class SchwingMatch
{
    public readonly long A, B;
    public readonly bool Schlussgang;
    public SchwingPhase Phase = SchwingPhase.Intro;
    public SchwingEnd End;
    /// <summary>0 while going, or after a time-out draw ("gestellt").</summary>
    public long Winner;
    public float BalanceA = SchwingRules.MaxBalance, BalanceB = SchwingRules.MaxBalance;
    public float GripA = SchwingRules.MaxGrip, GripB = SchwingRules.MaxGrip;
    public SchwingLean LeanA, LeanB;
    public bool BraceA, BraceB;
    /// <summary>The throw winding up: who made it and which, and when it resolves.</summary>
    public long ThrowBy;
    public SchwingThrow Pending;
    /// <summary>Bodenarbeit: who is on top (the thrower), and the tug meter 0..100 (100 = pinned).</summary>
    public long Top;
    public float Tug = 50f;
    /// <summary>The last throw and how it ended, for the poses.</summary>
    public SchwingThrow LastThrow;
    public SchwingAnswer LastAnswer;
    public long LastBy;
    public double LastAt = double.NegativeInfinity;
    public readonly List<SchwingEvent> Events = new();

    private double _phaseAt, _liveAt, _groundAt, _lastTick;
    private double _coolA = double.NegativeInfinity, _coolB = double.NegativeInfinity;
    private double _throwAt;
    private double _tugA = double.NegativeInfinity, _tugB = double.NegativeInfinity;
    private int _actsA, _actsB;
    private double _actSecondA, _actSecondB;

    public SchwingMatch(long a, long b, double now, bool schlussgang = false)
    {
        A = a;
        B = b;
        Schlussgang = schlussgang;
        _phaseAt = _lastTick = now;
    }

    public bool Has(long who) => who == A || who == B;
    public long Other(long who) => who == A ? B : A;
    public float Balance(long who) => who == A ? BalanceA : BalanceB;
    public float Grip(long who) => who == A ? GripA : GripB;
    public SchwingLean Lean(long who) => who == A ? LeanA : LeanB;
    public bool Brace(long who) => who == A ? BraceA : BraceB;
    public float Length => Schlussgang ? SchwingRules.SchlussgangSeconds : SchwingRules.GangSeconds;

    public float TimeLeft(double now) => Phase switch
    {
        SchwingPhase.Intro => Length,
        SchwingPhase.Done => 0f,
        _ => (float)Math.Max(0, Length - (now - _liveAt)),
    };

    /// <summary>The pending throw's age through its telegraph, 0..1 (0 when none).</summary>
    public float Winding(double now) => Pending == SchwingThrow.None ? 0f : (float)Math.Clamp((now - _throwAt) / SchwingRules.Telegraph, 0, 1);

    private void SetBalance(long who, float v) { if (who == A) BalanceA = v; else BalanceB = v; }
    private void SetGrip(long who, float v) { if (who == A) GripA = v; else GripB = v; }

    /// <summary>The 20-a-second budget of acts per wrestler; false when this one is over it.</summary>
    public bool Allow(long who, double now)
    {
        ref double sec = ref (who == A ? ref _actSecondA : ref _actSecondB);
        ref int n = ref (who == A ? ref _actsA : ref _actsB);
        if (now - sec >= 1.0) { sec = now; n = 0; }
        return ++n <= SchwingRules.ActsPerSecond;
    }

    /// <summary>A wrestler's lean and brace (what it holds now). Only while standing in the grip.</summary>
    public bool SetLean(long who, SchwingLean lean, bool brace)
    {
        if (!Has(who) || Phase != SchwingPhase.Live) return false;
        if (who == A) { LeanA = lean; BraceA = brace; } else { LeanB = lean; BraceB = brace; }
        return true;
    }

    /// <summary>A throw: needs the grip, a free pair of hands, and no throw under way. True when it started.</summary>
    public bool StartThrow(long who, SchwingThrow t, double now)
    {
        if (!Has(who) || Phase != SchwingPhase.Live || Pending != SchwingThrow.None || !SchwingRules.Valid((int)t)) return false;
        if (Grip(who) < SchwingRules.GripNeeded || now < (who == A ? _coolA : _coolB)) return false;
        Pending = t;
        ThrowBy = who;
        _throwAt = now;
        SetGrip(who, Grip(who) - SchwingRules.GripCost);
        Events.Add(new SchwingEvent(SchwingEventKind.Telegraph, who, t, SchwingAnswer.None));
        return true;
    }

    /// <summary>A press of the Bodenarbeit tug. True when it counted (the rate gap held).</summary>
    public bool PressTug(long who, double now)
    {
        if (!Has(who) || Phase != SchwingPhase.Ground) return false;
        ref double last = ref (who == A ? ref _tugA : ref _tugB);
        if (now - last < SchwingRules.TugGap) return false;
        last = now;
        // the thrower needs the first beat to take its hold
        if (who == Top && now - _groundAt < 0.3) return false;
        Tug = Math.Clamp(Tug + (who == Top ? 7f : -7f), 0f, 100f);
        return true;
    }

    public void Forfeit(long who, double now)
    {
        if (!Has(who) || Phase == SchwingPhase.Done) return;
        Finish(Other(who), SchwingEnd.Forfeit, now);
    }

    private void Finish(long winner, SchwingEnd end, double now)
    {
        Winner = winner;
        End = end;
        Phase = SchwingPhase.Done;
        _phaseAt = now;
        Pending = SchwingThrow.None;
        Events.Add(new SchwingEvent(SchwingEventKind.Ended, winner, SchwingThrow.None, SchwingAnswer.None));
    }

    /// <summary>The bout is over and the sawdust brushed: the manager can drop it.</summary>
    public bool Expired(double now) => Phase == SchwingPhase.Done && now - _phaseAt >= SchwingRules.DoneSeconds;

    /// <summary>Advances the bout to <paramref name="now"/>. True when something the clients show changed.</summary>
    public bool Tick(double now)
    {
        float dt = (float)Math.Clamp(now - _lastTick, 0, 0.5);
        _lastTick = now;
        bool changed = false;
        switch (Phase)
        {
            case SchwingPhase.Intro when now - _phaseAt >= SchwingRules.IntroSeconds:
                Phase = SchwingPhase.Live;
                _liveAt = now;
                return true;
            case SchwingPhase.Live:
                changed |= TickLive(dt, now);
                break;
            case SchwingPhase.Ground:
                changed |= TickGround(dt, now);
                break;
        }
        return changed;
    }

    private bool TickLive(float dt, double now)
    {
        // the clock: a time-out is gestellt
        if (now - _liveAt >= Length && Pending == SchwingThrow.None)
        {
            Finish(0, SchwingEnd.Gestellt, now);
            return true;
        }
        bool a = LeanA != SchwingLean.None, b = LeanB != SchwingLean.None;
        if (a && b)
        {
            // pulling against each other wears both grips
            GripA = Math.Max(0, GripA - SchwingRules.LeanGripCost * dt);
            GripB = Math.Max(0, GripB - SchwingRules.LeanGripCost * dt);
        }
        else
        {
            if (a) BalanceB = Math.Max(0, BalanceB - SchwingRules.PushCost * dt);
            if (b) BalanceA = Math.Max(0, BalanceA - SchwingRules.PushCost * dt);
            if (!a) GripA = Math.Min(SchwingRules.MaxGrip, GripA + SchwingRules.GripRegen * dt);
            if (!b) GripB = Math.Min(SchwingRules.MaxGrip, GripB + SchwingRules.GripRegen * dt);
        }
        if (!b) BalanceA = Math.Min(SchwingRules.MaxBalance, BalanceA + SchwingRules.BalanceRegen * dt);
        if (!a) BalanceB = Math.Min(SchwingRules.MaxBalance, BalanceB + SchwingRules.BalanceRegen * dt);

        if (Pending != SchwingThrow.None && now - _throwAt >= SchwingRules.Telegraph) Resolve(now);
        return true;
    }

    private void Resolve(double now)
    {
        long by = ThrowBy, def = Other(by);
        var t = Pending;
        var d = SchwingRules.Def(t);
        var answer = SchwingRules.Answer(t, Lean(def), Brace(def));
        Pending = SchwingThrow.None;
        if (by == A) _coolA = now + SchwingRules.Cooldown; else _coolB = now + SchwingRules.Cooldown;
        LastThrow = t;
        LastBy = by;
        LastAt = now;
        switch (answer)
        {
            case SchwingAnswer.Braced:
                SetBalance(by, Math.Max(0, Balance(by) - 10f));
                SetGrip(def, Math.Max(0, Grip(def) - 5f));
                break;
            case SchwingAnswer.Countered:
                SetBalance(by, Math.Max(0, Balance(by) - 18f));
                break;
            default:
                // the harder the thrower still holds, the harder it lands; leaning into the thrower makes it worse
                float hold = 0.6f + 0.4f * Grip(by) / SchwingRules.MaxGrip;
                float into = Lean(def) == SchwingLean.In ? 1.2f : 1f;
                float left = Balance(def) - d.Weight * hold * into;
                SetBalance(def, Math.Max(0, left));
                answer = left < SchwingRules.DownBalance ? SchwingAnswer.Down : SchwingAnswer.Landed;
                break;
        }
        LastAnswer = answer;
        Events.Add(new SchwingEvent(SchwingEventKind.Resolved, by, t, answer));
        if (answer == SchwingAnswer.Down)
        {
            Phase = SchwingPhase.Ground;
            Top = by;
            Tug = 50f;
            _groundAt = now;
            LeanA = LeanB = SchwingLean.None;
            BraceA = BraceB = false;
            Events.Add(new SchwingEvent(SchwingEventKind.GroundBegan, by, t, answer));
        }
    }

    private bool TickGround(float dt, double now)
    {
        SetGrip(Top, Math.Max(0, Grip(Top) - 1.5f * dt));
        bool timeout = now - _groundAt >= SchwingRules.GroundSeconds;
        if (Tug >= 100f || timeout && Tug > 50f)
        {
            // both shoulder blades down; the winner must still hold the trousers
            if (Grip(Top) > 0) Finish(Top, SchwingEnd.Won, now);
            else Finish(0, SchwingEnd.Gestellt, now);
            return true;
        }
        if (Tug <= 0f || timeout)
        {
            // the one underneath twists out: both up again, the thrower tired
            Phase = SchwingPhase.Live;
            SetBalance(A, 60f);
            SetBalance(B, 60f);
            SetGrip(Top, Math.Max(0, Grip(Top) - 10f));
            Events.Add(new SchwingEvent(SchwingEventKind.Escaped, Other(Top), SchwingThrow.None, SchwingAnswer.None));
            return true;
        }
        if (now - _liveAt >= Length && Phase == SchwingPhase.Ground && Tug <= 50f)
        {
            // the clock ran out with nobody pinned
            Finish(0, SchwingEnd.Gestellt, now);
            return true;
        }
        return true;
    }

    /// <summary>What <paramref name="who"/> should draw now: its pose, from the bout's state and the shared clock.</summary>
    public int PoseOf(long who, double now)
    {
        if (!Has(who)) return 0;
        bool won = Winner == who, lost = Winner != 0 && Winner != who;
        switch (Phase)
        {
            case SchwingPhase.Done:
                return (int)(Winner == 0 ? SchwingStance.Grip : won ? SchwingStance.Brush : End == SchwingEnd.Forfeit ? SchwingStance.Dusted : SchwingStance.Down);
            case SchwingPhase.Ground:
                return (int)(who == Top ? SchwingStance.GroundTop : SchwingStance.GroundBottom);
        }
        if (Pending != SchwingThrow.None && ThrowBy == who) return SchwingRules.PoseOf(Pending);
        // the thrower's stumble after an answered throw, and a thrown body's recovery
        if (now - LastAt < 0.7 && LastBy == who && LastAnswer is SchwingAnswer.Braced or SchwingAnswer.Countered) return (int)SchwingStance.Stumble;
        if (now - LastAt < 0.7 && LastBy == who && LastAnswer == SchwingAnswer.Landed) return SchwingRules.PoseOf(LastThrow);
        return (int)SchwingRules.Standing(Lean(who), Brace(who));
    }
}
