using System;
using System.Collections.Generic;

namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>Where an aura battle is. Append only: on the wire.</summary>
public enum AuraPhase : byte { Intro, Plan, Countdown, Perform, Verdict, Done }

/// <summary>A finished turn: who performed what, how each press landed, and its score.</summary>
public sealed record AuraTurn(long Performer, AuraMove[] Moves, AuraGrade[] Grades, AuraScore Score);

/// <summary>
/// One aura battle on the server (#907): three rounds of one turn each; a turn is plan (secret),
/// countdown to the next bar, perform (a Next press per move, graded on its downbeat), verdict.
/// Round 1 opens with A, round 2 with B; round 3's trailer goes second, so they get to answer.
/// </summary>
public sealed class AuraMatch
{
    public const float IntroSeconds = 3f;
    public const float PlanSeconds = 15f;
    /// <summary>A plan arriving this long after the planning time still counts (the timeout's own send).</summary>
    public const float PlanGrace = 0.75f;
    /// <summary>The first move starts on the first bar at least this long after the plan locks.</summary>
    public const float CountdownLead = 2f;
    public const float VerdictSeconds = 4f;
    public const float DoneSeconds = 6f;
    public const int Rounds = 3;
    public const int TurnCount = Rounds * 2;

    public readonly long A, B;
    public AuraPhase Phase = AuraPhase.Intro;
    /// <summary>0-5: the turn being planned, performed or judged.</summary>
    public int Turn;
    public int Round => Turn / 2 + 1;
    public long Performer { get; private set; }
    public int TotalA, TotalB;
    /// <summary>The match's winner, 0 for a draw or still going.</summary>
    public long Winner;
    public bool Forfeited;
    /// <summary>When the current phase ends, on the server clock (0 = it ends on an event).</summary>
    public double PhaseEnds;

    /// <summary>The plan being performed, and its start (the first move's downbeat).</summary>
    public AuraMove[] Plan = Array.Empty<AuraMove>();
    public double T0;
    public AuraGrade[] Grades = Array.Empty<AuraGrade>();
    /// <summary>The next move whose Next press is awaited.</summary>
    public int NextIndex;
    /// <summary>Spectators who cheered during this turn; the manager counts them.</summary>
    public int Crowd;

    public readonly List<AuraTurn> History = new(TurnCount);

    public AuraMatch(long a, long b, double now)
    {
        A = a;
        B = b;
        PhaseEnds = now + IntroSeconds;
        Performer = a;
    }

    public bool Has(long who) => who == A || who == B;
    public long Other(long who) => who == A ? B : A;
    public int Total(long who) => who == A ? TotalA : TotalB;

    /// <summary>Who performs turn <paramref name="turn"/>, given the totals before it.</summary>
    public long PerformerOf(int turn)
    {
        int round = turn / 2;
        long first = round switch
        {
            0 => A,
            1 => B,
            // the leader opens round 3 so the trailer gets to answer; a tie keeps A first
            _ => TotalB > TotalA ? B : A,
        };
        return turn % 2 == 0 ? first : Other(first);
    }

    /// <summary>The context a turn of <paramref name="who"/> is scored against.</summary>
    public AuraContext ContextFor(long who)
    {
        var ctx = new AuraContext();
        var earlier = new List<AuraMove>();
        AuraTurn? ownLast = null;
        for (int i = 0; i < History.Count; i++)
        {
            var t = History[i];
            if (t.Performer == who)
            {
                if (ownLast != null) earlier.AddRange(ownLast.Moves);
                ownLast = t;
            }
            else ctx.RivalLast = t.Moves;
        }
        if (ownLast != null) ctx.OwnPrevious = ownLast.Moves;
        ctx.OwnEarlier = earlier.ToArray();
        return ctx;
    }

    /// <summary>
    /// The performer locks in a plan. Refused unless it's theirs to plan now and it holds no unknown
    /// move, at most 6 and at most 16 beats; a short one is padded (ran out of time).
    /// </summary>
    public bool SubmitPlan(long who, int turn, IReadOnlyList<AuraMove>? plan, double now)
    {
        if (Phase != AuraPhase.Plan || who != Performer || turn != Turn) return false;
        if (now > PhaseEnds + PlanGrace) return false;
        var accepted = AuraRules.Accept(plan);
        if (accepted == null) return false;
        Lock(accepted, now);
        return true;
    }

    private void Lock(AuraMove[] accepted, double now)
    {
        Plan = accepted;
        Grades = new AuraGrade[accepted.Length];
        NextIndex = 0;
        Crowd = 0;
        T0 = AuraRules.NextBar(now, CountdownLead);
        Phase = AuraPhase.Countdown;
        PhaseEnds = T0;
    }

    /// <summary>
    /// The performer's Next press for move <paramref name="index"/> at <paramref name="ts"/> (the
    /// server clock). Ignored (null) when out of turn, out of order, or more than a beat before its
    /// downbeat (a double tap); otherwise graded: beyond Ok early is rushed, a Miss.
    /// </summary>
    public AuraGrade? Next(long who, int turn, int index, double ts)
    {
        if (who != Performer || turn != Turn || index != NextIndex) return null;
        if (Phase != AuraPhase.Perform && Phase != AuraPhase.Countdown) return null;
        if (index < 0 || index >= Plan.Length) return null;
        double cut = AuraRules.CutAt(Plan, index, T0);
        if (ts < cut - AuraRules.BeatSeconds) return null;
        if (ts > cut + AuraRules.LateLimit) return null; // Tick already called it frozen, or will
        var g = AuraRules.Grade(ts - cut, AuraRules.Def(Plan[index]).Difficulty);
        Grades[index] = g;
        NextIndex++;
        return g;
    }

    /// <summary>
    /// Advances the phases at <paramref name="now"/>. Returns the moves graded Miss because their
    /// press never came (frozen) through <paramref name="frozen"/>, and true when anything the
    /// battlers show changed.
    /// </summary>
    public bool Tick(double now, List<int>? frozen = null)
    {
        switch (Phase)
        {
            case AuraPhase.Intro when now >= PhaseEnds:
                StartPlan(now);
                return true;
            case AuraPhase.Plan when now > PhaseEnds + PlanGrace:
                // nothing arrived: stand tall three times
                Lock(AuraRules.Accept(null)!, now);
                return true;
            case AuraPhase.Countdown when now >= T0:
                Phase = AuraPhase.Perform;
                PhaseEnds = AuraRules.CutAt(Plan, Plan.Length - 1, T0) + AuraRules.LateLimit;
                return true;
            case AuraPhase.Perform:
            {
                bool changed = false;
                while (NextIndex < Plan.Length && now > AuraRules.CutAt(Plan, NextIndex, T0) + AuraRules.LateLimit)
                {
                    Grades[NextIndex] = AuraGrade.Miss;
                    frozen?.Add(NextIndex);
                    NextIndex++;
                    changed = true;
                }
                if (NextIndex >= Plan.Length) { EndTurn(now); return true; }
                return changed;
            }
            case AuraPhase.Verdict when now >= PhaseEnds:
                if (Turn + 1 >= TurnCount) Finish(now);
                else
                {
                    Turn++;
                    StartPlan(now);
                }
                return true;
        }
        return false;
    }

    /// <summary>The turn just scored (valid from the verdict on).</summary>
    public AuraTurn? LastTurn => History.Count > 0 ? History[^1] : null;

    /// <summary><paramref name="who"/>'s highest-scoring turn: their best combo.</summary>
    public AuraTurn? BestOf(long who)
    {
        AuraTurn? best = null;
        foreach (var t in History)
            if (t.Performer == who && (best == null || t.Score.Total > best.Score.Total)) best = t;
        return best;
    }

    /// <summary>Whether the match is over and its last banner has been shown long enough to drop it.</summary>
    public bool Expired(double now) => Phase == AuraPhase.Done && now >= PhaseEnds;

    /// <summary><paramref name="quitter"/> left or disconnected: the other wins.</summary>
    public void Forfeit(long quitter, double now)
    {
        if (!Has(quitter) || Phase == AuraPhase.Done) return;
        Winner = Other(quitter);
        Forfeited = true;
        Phase = AuraPhase.Done;
        PhaseEnds = now + DoneSeconds;
    }

    private void StartPlan(double now)
    {
        Performer = PerformerOf(Turn);
        Plan = Array.Empty<AuraMove>();
        Grades = Array.Empty<AuraGrade>();
        NextIndex = 0;
        Crowd = 0;
        Phase = AuraPhase.Plan;
        PhaseEnds = now + PlanSeconds;
    }

    private void EndTurn(double now)
    {
        var ctx = ContextFor(Performer);
        ctx.Crowd = Crowd;
        var score = AuraRules.Score(Plan, Grades, ctx);
        History.Add(new AuraTurn(Performer, Plan, (AuraGrade[])Grades.Clone(), score));
        if (Performer == A) TotalA += score.Total;
        else TotalB += score.Total;
        Phase = AuraPhase.Verdict;
        PhaseEnds = now + VerdictSeconds;
    }

    private void Finish(double now)
    {
        Winner = TotalA == TotalB ? 0 : TotalA > TotalB ? A : B;
        Phase = AuraPhase.Done;
        PhaseEnds = now + DoneSeconds;
    }
}
