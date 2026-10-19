using System;
using System.Collections.Generic;

namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>An aura-battle move (#907). Append only: the value is on the wire and in the replicated pose.</summary>
public enum AuraMove : byte
{
    None = 0,
    StandTall = 1,
    ArmsCrossed = 2,
    PointToSky = 3,
    StareDown = 4,
    Sunglasses = 5,
    Yawn = 6,
    SlowWalk = 7,
    Griddy = 8,
    Siuuu = 9,
    Mewing = 10,
    SixSeven = 11,
    Lasso = 12,
    CutTheRope = 13,
}

/// <summary>The kind of a move, as the real battles mix them: variety and chains read it.</summary>
public enum AuraFamily : byte { Pose, Stare, Walk, Dance, Meme, Mime }

[Flags]
public enum AuraTag : byte
{
    None = 0,
    Opener = 1,
    Finisher = 2,
    Calm = 4,
    Hype = 8,
    Cool = 16,
    Attack = 32,
    Surprise = 64,
}

/// <summary>How well a Next press landed on its downbeat. Append only: on the wire.</summary>
public enum AuraGrade : byte { Pending, Perfect, Good, Ok, Miss }

/// <summary>
/// What a battler's body is doing, as the replicated <c>FootPlayer.AuraPose</c> carries it (#907).
/// 0 = not in a battle. A move is <see cref="MoveBase"/> + its <see cref="AuraMove"/>.
/// Append only: the value is on the wire.
/// </summary>
public enum AuraStance : byte
{
    None = 0,
    /// <summary>The rival performs: arms crossed, unimpressed.</summary>
    Waiting = 1,
    /// <summary>Planning a combo: hand on the chin.</summary>
    Planning = 2,
    /// <summary>A missed transition: a stagger.</summary>
    Stumble = 3,
    Victory = 4,
    Defeat = 5,
    MoveBase = 16,
}

/// <summary>A move's catalog row. <paramref name="Counters"/> = the rival moves it answers.</summary>
public sealed record AuraMoveDef(
    AuraMove Move, string Name, AuraFamily Family, int Beats, int Base, int Difficulty, AuraTag Tags,
    AuraMove[] Counters)
{
    public bool Has(AuraTag tag) => (Tags & tag) != 0;
}

/// <summary>A turn's score, part by part, so the verdict can count it up.</summary>
public struct AuraScore
{
    public float Base, Variety, Chain, Opener, Finisher, Answer, Streak, TryHard, Stumble, Repeat, Crowd;

    /// <summary>The parts in wire order (<see cref="FromParts"/> reads them back).</summary>
    public const int PartCount = 11;

    public readonly float Raw => Base + Variety + Chain + Opener + Finisher + Answer + Streak + TryHard + Stumble + Repeat + Crowd;

    public readonly int Total => Math.Max(0, (int)MathF.Round(Raw, MidpointRounding.AwayFromZero));

    public readonly float Part(int i) => i switch
    {
        0 => Base, 1 => Variety, 2 => Chain, 3 => Opener, 4 => Finisher, 5 => Answer,
        6 => Streak, 7 => TryHard, 8 => Stumble, 9 => Repeat, _ => Crowd,
    };

    public static readonly string[] PartNames =
        { "Base", "Variety", "Chain", "Opener", "Finisher", "Answer", "Streak", "Try-hard", "Stumble", "Repeat", "Crowd" };

    public static AuraScore FromParts(float[] p)
    {
        var s = new AuraScore();
        if (p == null || p.Length < PartCount) return s;
        s.Base = p[0]; s.Variety = p[1]; s.Chain = p[2]; s.Opener = p[3]; s.Finisher = p[4]; s.Answer = p[5];
        s.Streak = p[6]; s.TryHard = p[7]; s.Stumble = p[8]; s.Repeat = p[9]; s.Crowd = p[10];
        return s;
    }

    public readonly float[] ToParts()
    {
        var p = new float[PartCount];
        for (int i = 0; i < PartCount; i++) p[i] = Part(i);
        return p;
    }
}

/// <summary>What a turn is scored against: the performer's own past and the rival's last combo.</summary>
public sealed class AuraContext
{
    public static readonly AuraContext Empty = new();

    /// <summary>The performer's previous combo (stale ×0.5).</summary>
    public AuraMove[] OwnPrevious = Array.Empty<AuraMove>();
    /// <summary>Every move of the performer's turns before that one (stale ×0.7).</summary>
    public AuraMove[] OwnEarlier = Array.Empty<AuraMove>();
    /// <summary>The rival's last combo: answering it scores, copying it ("biting") halves.</summary>
    public AuraMove[] RivalLast = Array.Empty<AuraMove>();
    /// <summary>Spectators who cheered during the turn.</summary>
    public int Crowd;
}

/// <summary>
/// The aura battle's rules (#907): the move catalog, how a Next press is graded against its
/// downbeat, what a plan may hold, and the score. Pure so the unit tests own the numbers.
/// </summary>
public static class AuraRules
{
    public const float Bpm = 120f;
    public const double BeatSeconds = 60.0 / Bpm;
    public const int BeatsPerBar = 4;
    public const int MinMoves = 3;
    public const int MaxMoves = 6;
    public const int MaxBeats = 16;
    /// <summary>Both battlers stand this far apart, facing each other.</summary>
    public const float StartGap = 4f;
    /// <summary>Leaving this far from the arena's centre forfeits.</summary>
    public const float LeashRadius = 18f;
    public const float ChallengeSeconds = 30f;
    public const float ChallengeRange = 12f;
    /// <summary>Cheering spectators count within this of the arena's centre.</summary>
    public const float CrowdRadius = 15f;

    // timing windows around a downbeat, in seconds
    public const double PerfectWindow = 0.05, GoodWindow = 0.10, OkWindow = 0.16;
    /// <summary>A difficulty-3 move narrows every window to this.</summary>
    public const double HardNarrowing = 0.85;
    /// <summary>No press this long after the downbeat: the move ran on, a Miss ("frozen").</summary>
    public const double LateLimit = 0.25;

    private static readonly AuraMove[] None = Array.Empty<AuraMove>();

    private static readonly AuraMoveDef[] Table =
    {
        new(AuraMove.None, "", AuraFamily.Pose, 0, 0, 0, AuraTag.None, None),
        //  move                    name             family             beats base diff tags
        new(AuraMove.StandTall,   "Stand tall",    AuraFamily.Pose,  2, 5,  1, AuraTag.Calm | AuraTag.Finisher, None),
        new(AuraMove.ArmsCrossed, "Arms crossed",  AuraFamily.Pose,  2, 6,  1, AuraTag.Calm | AuraTag.Finisher, None),
        new(AuraMove.PointToSky,  "Point to sky",  AuraFamily.Pose,  2, 9,  2, AuraTag.Hype | AuraTag.Finisher, None),
        new(AuraMove.StareDown,   "Stare down",    AuraFamily.Stare, 4, 8,  1, AuraTag.Calm | AuraTag.Opener, None),
        new(AuraMove.Sunglasses,  "Sunglasses",    AuraFamily.Stare, 2, 9,  2, AuraTag.Cool,
            new[] { AuraMove.StareDown, AuraMove.PointToSky }),
        new(AuraMove.Yawn,        "Yawn",          AuraFamily.Stare, 2, 8,  1, AuraTag.Calm,
            new[] { AuraMove.Siuuu, AuraMove.Griddy }),
        new(AuraMove.SlowWalk,    "Slow walk",     AuraFamily.Walk,  4, 10, 2, AuraTag.Opener, None),
        new(AuraMove.Griddy,      "Griddy",        AuraFamily.Dance, 4, 11, 2, AuraTag.Hype, None),
        new(AuraMove.Siuuu,       "Siuuu",         AuraFamily.Meme,  4, 14, 3, AuraTag.Hype | AuraTag.Finisher, None),
        new(AuraMove.Mewing,      "Mewing",        AuraFamily.Meme,  2, 10, 2, AuraTag.Cool,
            new[] { AuraMove.StareDown, AuraMove.Sunglasses }),
        new(AuraMove.SixSeven,    "Six seven",     AuraFamily.Meme,  2, 10, 2, AuraTag.Hype, None),
        new(AuraMove.Lasso,       "Lasso",         AuraFamily.Mime,  4, 13, 3, AuraTag.Attack | AuraTag.Opener, None),
        new(AuraMove.CutTheRope,  "Cut the rope",  AuraFamily.Mime,  2, 11, 2, AuraTag.Surprise,
            new[] { AuraMove.Lasso }),
    };

    /// <summary>The moves a planner offers, in catalog order.</summary>
    public static readonly AuraMove[] Moves = BuildMoves();

    private static AuraMove[] BuildMoves()
    {
        var all = new AuraMove[Table.Length - 1];
        for (int i = 1; i < Table.Length; i++) all[i - 1] = Table[i].Move;
        return all;
    }

    public static bool IsMove(AuraMove m) => m > AuraMove.None && (int)m < Table.Length;

    public static AuraMoveDef Def(AuraMove m) => IsMove(m) ? Table[(int)m] : Table[0];

    public static string Name(AuraMove m) => Def(m).Name;

    /// <summary>The replicated pose of a move.</summary>
    public static int PoseOf(AuraMove m) => (int)AuraStance.MoveBase + (int)m;

    /// <summary>The move a replicated pose shows, or <see cref="AuraMove.None"/>.</summary>
    public static AuraMove MoveOf(int pose) =>
        pose >= (int)AuraStance.MoveBase && IsMove((AuraMove)(pose - (int)AuraStance.MoveBase))
            ? (AuraMove)(pose - (int)AuraStance.MoveBase) : AuraMove.None;

    /// <summary>Whether <paramref name="mine"/> answers <paramref name="theirs"/>.</summary>
    public static bool Counters(AuraMove mine, AuraMove theirs) => Array.IndexOf(Def(mine).Counters, theirs) >= 0;

    public static int Beats(IReadOnlyList<AuraMove> plan)
    {
        int b = 0;
        for (int i = 0; i < plan.Count; i++) b += Def(plan[i]).Beats;
        return b;
    }

    /// <summary>Whether a locked-in plan may be performed: 3-6 known moves, at most 16 beats.</summary>
    public static bool ValidatePlan(IReadOnlyList<AuraMove> plan)
    {
        if (plan == null || plan.Count < MinMoves || plan.Count > MaxMoves) return false;
        for (int i = 0; i < plan.Count; i++) if (!IsMove(plan[i])) return false;
        return Beats(plan) <= MaxBeats;
    }

    /// <summary>
    /// What the server performs from a submitted plan: refused (null) when it holds an unknown
    /// move, more than 6, or more than 16 beats; padded with Stand tall up to 3 when short (a plan
    /// that ran out of time).
    /// </summary>
    public static AuraMove[]? Accept(IReadOnlyList<AuraMove>? plan)
    {
        var moves = new List<AuraMove>(MaxMoves);
        if (plan != null)
        {
            if (plan.Count > MaxMoves) return null;
            for (int i = 0; i < plan.Count; i++)
            {
                if (!IsMove(plan[i])) return null;
                moves.Add(plan[i]);
            }
        }
        while (moves.Count < MinMoves) moves.Add(AuraMove.StandTall);
        return ValidatePlan(moves) ? moves.ToArray() : null;
    }

    /// <summary>The downbeat that ends move <paramref name="index"/>: when its Next press should land.</summary>
    public static double CutAt(IReadOnlyList<AuraMove> plan, int index, double t0)
    {
        int beats = 0;
        for (int i = 0; i <= index && i < plan.Count; i++) beats += Def(plan[i]).Beats;
        return t0 + beats * BeatSeconds;
    }

    /// <summary>When move <paramref name="index"/> starts.</summary>
    public static double StartAt(IReadOnlyList<AuraMove> plan, int index, double t0) =>
        index <= 0 ? t0 : CutAt(plan, index - 1, t0);

    /// <summary>
    /// Which move the schedule is in at <paramref name="now"/>: -1 before the first downbeat, the
    /// last one held once it has run out (its cut not yet pressed).
    /// </summary>
    public static int MoveAt(IReadOnlyList<AuraMove> plan, double t0, double now)
    {
        if (plan.Count == 0 || now < t0) return -1;
        double cut = t0;
        for (int i = 0; i < plan.Count; i++)
        {
            cut += Def(plan[i]).Beats * BeatSeconds;
            if (now < cut) return i;
        }
        return plan.Count - 1;
    }

    /// <summary>The first bar line at least <paramref name="lead"/> after <paramref name="now"/>, on the shared clock.</summary>
    public static double NextBar(double now, double lead)
    {
        double bar = BeatsPerBar * BeatSeconds;
        return Math.Ceiling((now + lead) / bar) * bar;
    }

    /// <summary>
    /// A Next press <paramref name="offset"/> seconds from its downbeat (negative = early) on a move
    /// of <paramref name="difficulty"/>. Beyond Ok either way is a Miss: rushed, or frozen.
    /// </summary>
    public static AuraGrade Grade(double offset, int difficulty)
    {
        double w = difficulty >= 3 ? HardNarrowing : 1.0, a = Math.Abs(offset);
        if (a <= PerfectWindow * w) return AuraGrade.Perfect;
        if (a <= GoodWindow * w) return AuraGrade.Good;
        if (a <= OkWindow * w) return AuraGrade.Ok;
        return AuraGrade.Miss;
    }

    public static float Factor(AuraGrade g) => g switch
    {
        AuraGrade.Perfect => 1f,
        AuraGrade.Good => 0.8f,
        AuraGrade.Ok => 0.5f,
        _ => 0f,
    };

    public static string GradeWord(AuraGrade g) => g switch
    {
        AuraGrade.Perfect => "PERFECT",
        AuraGrade.Good => "GOOD",
        AuraGrade.Ok => "OK",
        AuraGrade.Miss => "MISS",
        _ => "",
    };

    /// <summary>The bonus for moving from <paramref name="a"/> into <paramref name="b"/>, by family.</summary>
    public static int ChainBonus(AuraFamily a, AuraFamily b) => (a, b) switch
    {
        (AuraFamily.Walk, AuraFamily.Pose) => 4,   // the arrival
        (AuraFamily.Walk, AuraFamily.Stare) => 3,
        (AuraFamily.Stare, AuraFamily.Meme) => 3,  // deadpan into the meme
        (AuraFamily.Dance, AuraFamily.Pose) => 3,  // the freeze
        (AuraFamily.Mime, AuraFamily.Pose) => 3,
        (AuraFamily.Stare, AuraFamily.Pose) => 2,
        (AuraFamily.Meme, AuraFamily.Pose) => 2,
        (AuraFamily.Stare, AuraFamily.Walk) => 2,
        _ => 0,
    };

    public const int VarietyStep = 3, VarietyCap = 12;
    public const int OpenerBonus = 2, FinisherBonus = 5, FailedFinish = -6;
    public const int AnswerBonus = 8, AnswerCap = 18, MaxAnswers = 2;
    public const float FlipFactor = 1.25f;
    public const int StreakFrom = 3, StreakCap = 8;
    public const int TryHardFrom = 10, TryHardStep = -3, CringeFrom = 2, CringeStep = -5;
    public const int StumbleStep = -2, ThirdRepeat = -4;
    public const int CrowdStep = 2, CrowdCap = 8;
    public const float CrowdShare = 0.25f;

    /// <summary>
    /// A performed combo's score: base × timing (halved for a repeat, a stale move or a bitten
    /// one), variety, chains and the opener/finisher between moves that landed, answers to the
    /// rival's last combo, a Perfect streak; less try-hard, cringe and stumbles; plus a crowd capped
    /// at a quarter of the rest, so cheering never beats skill.
    /// </summary>
    public static AuraScore Score(IReadOnlyList<AuraMove> plan, IReadOnlyList<AuraGrade> grades, AuraContext? ctx = null)
    {
        ctx ??= AuraContext.Empty;
        var s = new AuraScore();
        int n = plan.Count;
        if (n == 0) return s;
        bool Landed(int i) => i < grades.Count && grades[i] is AuraGrade.Perfect or AuraGrade.Good or AuraGrade.Ok;
        AuraGrade G(int i) => i < grades.Count ? grades[i] : AuraGrade.Miss;

        int families = 0, difficulty = 0, memes = 0, run = 0, streak = 0;
        for (int i = 0; i < n; i++)
        {
            var def = Def(plan[i]);
            difficulty += def.Difficulty;
            if (def.Family == AuraFamily.Meme) memes++;

            int before = 0;
            for (int j = 0; j < i; j++) if (plan[j] == plan[i]) before++;
            float mul = before == 0 ? 1f : before == 1 ? 0.5f : 0f;
            if (before >= 2) s.Repeat += ThirdRepeat;
            if (Array.IndexOf(ctx.OwnPrevious, plan[i]) >= 0) mul *= 0.5f;
            else if (Array.IndexOf(ctx.OwnEarlier, plan[i]) >= 0) mul *= 0.7f;
            if (Array.IndexOf(ctx.RivalLast, plan[i]) >= 0) mul *= 0.5f;
            s.Base += def.Base * Factor(G(i)) * mul;

            if (!Landed(i)) s.Stumble += StumbleStep;
            else
            {
                bool seen = false;
                for (int j = 0; j < i; j++) if (Landed(j) && Def(plan[j]).Family == def.Family) seen = true;
                if (!seen) families++;
                if (i > 0 && Landed(i - 1)) s.Chain += ChainBonus(Def(plan[i - 1]).Family, def.Family);
            }

            if (G(i) == AuraGrade.Perfect) run++;
            else { streak += Math.Max(0, run - StreakFrom); run = 0; }
        }
        streak += Math.Max(0, run - StreakFrom);
        s.Streak = Math.Min(StreakCap, streak);
        s.Variety = Math.Min(VarietyCap, VarietyStep * Math.Max(0, families - 1));

        if (Def(plan[0]).Has(AuraTag.Opener) && Landed(0)) s.Opener = OpenerBonus;
        var last = G(n - 1);
        if (Def(plan[n - 1]).Has(AuraTag.Finisher) && last is AuraGrade.Perfect or AuraGrade.Good) s.Finisher = FinisherBonus;
        else if (last == AuraGrade.Miss) s.Finisher = FailedFinish;

        // answers: each distinct rival move countered by a landed move of mine
        int answers = 0;
        float answer = 0;
        for (int r = 0; r < ctx.RivalLast.Length && answers < MaxAnswers; r++)
        {
            var theirs = ctx.RivalLast[r];
            if (Array.IndexOf(ctx.RivalLast, theirs) < r) continue;
            for (int i = 0; i < n; i++)
            {
                if (!Landed(i) || !Counters(plan[i], theirs)) continue;
                answer += i == 0 ? AnswerBonus * FlipFactor : AnswerBonus;
                answers++;
                break;
            }
        }
        s.Answer = Math.Min(AnswerCap, answer);

        s.TryHard = TryHardStep * Math.Max(0, difficulty - TryHardFrom) + CringeStep * Math.Max(0, memes - CringeFrom);

        float rest = s.Raw;
        s.Crowd = Math.Max(0, Math.Min(Math.Min(CrowdStep * ctx.Crowd, CrowdCap), MathF.Floor(CrowdShare * Math.Max(0, rest))));
        return s;
    }

    private static readonly AuraGrade[] AllGood = { AuraGrade.Good, AuraGrade.Good, AuraGrade.Good, AuraGrade.Good, AuraGrade.Good, AuraGrade.Good };

    /// <summary>What a plan would score with every press Good: the planner's estimate.</summary>
    public static AuraScore Estimate(IReadOnlyList<AuraMove> plan, AuraContext? ctx = null) => Score(plan, AllGood, ctx);

    /// <summary>
    /// A strong plan for <paramref name="ctx"/>, greedily: add the move that raises the estimate
    /// most until no move does (at least 3, at most 6, within 16 beats). The planner's Suggest and bots.
    /// </summary>
    public static AuraMove[] SuggestPlan(AuraContext? ctx = null)
    {
        var plan = new List<AuraMove>(MaxMoves);
        float best = float.MinValue;
        while (plan.Count < MaxMoves)
        {
            AuraMove pick = AuraMove.None;
            float pickScore = float.MinValue;
            foreach (var m in Moves)
            {
                if (Beats(plan) + Def(m).Beats > MaxBeats) continue;
                plan.Add(m);
                float v = Estimate(plan, ctx).Raw;
                plan.RemoveAt(plan.Count - 1);
                if (v > pickScore) { pickScore = v; pick = m; }
            }
            if (pick == AuraMove.None) break;
            if (plan.Count >= MinMoves && pickScore <= best) break;
            plan.Add(pick);
            best = pickScore;
        }
        return plan.ToArray();
    }
}
