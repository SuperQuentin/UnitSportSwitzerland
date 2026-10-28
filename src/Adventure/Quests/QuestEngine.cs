using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/quests.md).

/// <summary>The <c>quests</c> section: the quests a player runs and has done, the tracked one, titles.</summary>
public sealed class QuestsProgress
{
    public static readonly SectionKey<QuestsProgress> Section = new("quests", Version: 1);

    /// <summary>Quest id -> its state.</summary>
    public Dictionary<string, QuestState> Active { get; set; } = new();
    /// <summary>Quest id -> when it was last done (Unix seconds).</summary>
    public Dictionary<string, long> Done { get; set; } = new();
    public string? Tracked { get; set; }
    public List<string> Titles { get; set; } = new();
    /// <summary>The title worn.</summary>
    public string? Title { get; set; }
}

/// <summary>Where a running quest stands: its step, the count on it, the distinct ids counted, a generator's seed.</summary>
public sealed class QuestState
{
    public int Step { get; set; }
    public int Count { get; set; }
    public long Started { get; set; }
    public List<string> Seen { get; set; } = new();
    public int Seed { get; set; }
}

public enum QuestChangeKind { Started = 0, Progressed = 1, StepDone = 2, Completed = 3, Abandoned = 4 }

/// <summary>What changed for the owner's toasts: <see cref="Step"/> the step now current (or the last one), <see cref="Count"/> on it.</summary>
public readonly record struct QuestChange(string QuestId, QuestChangeKind What, int Step, int Count);

/// <summary>
/// The quest rules (#1223), pure: given a <c>quests</c> section and an event (or a position), the
/// changed state and what to tell. The service applies rewards, touches the section and raises
/// <see cref="QuestCompleted"/>. At most <see cref="MaxActive"/> quests run at once, one is tracked.
/// </summary>
public sealed class QuestEngine
{
    public const int MaxActive = 8;

    private readonly List<IQuestSource> _sources = new();

    /// <summary>The plans whose systems run here (<c>places</c>, <c>rewards</c>, <c>navigation</c>, <c>stamina</c>): a quest with a step of a missing one is not offered.</summary>
    public HashSet<string> Plans { get; } = new(StringComparer.Ordinal);

    /// <summary>A POI's position and radius (places plan); null until the catalogue exists.</summary>
    public Func<string, (double E, double N, float RadiusM)?>? ResolvePoi { get; set; }

    /// <summary>
    /// A POI is reached only by being found (<see cref="PoiDiscovered"/>), never by the position check: set by
    /// the places service (#1224), so the travel rule (on foot or a light aircraft) holds for quests too.
    /// <see cref="ResolvePoi"/> still locates it for the tracked target.
    /// </summary>
    public bool PoisByDiscoveryOnly { get; set; }

    public QuestEngine(params IQuestSource[] sources) => _sources.AddRange(sources);

    public void AddSource(IQuestSource source)
    {
        if (!_sources.Any(s => s.Id == source.Id)) _sources.Add(source);
    }

    public IReadOnlyList<IQuestSource> Sources => _sources;

    public QuestDef? Resolve(string id)
    {
        foreach (var s in _sources)
            if (s.Resolve(id) is { } def) return def;
        return null;
    }

    /// <summary>Every step's system runs here.</summary>
    public bool Playable(QuestDef def) => def.Steps.All(s => s.Plan == null || Plans.Contains(s.Plan));

    /// <summary>Why <paramref name="def"/> cannot start now, or null when it can.</summary>
    /// <param name="given">The giver's own path (<c>ErrandService.Accept</c>) starts a <see cref="QuestStart.Given"/> quest; nothing else may (#1267).</param>
    public string? CanStart(QuestsProgress p, QuestDef def, bool given = false)
    {
        if (def.Start == QuestStart.Given && !given) return "Ask the giver.";
        if (p.Active.ContainsKey(def.Id)) return "Already running.";
        if (p.Done.ContainsKey(def.Id) && !def.Repeatable) return "Already done.";
        if (def.Requires.FirstOrDefault(r => !p.Done.ContainsKey(r)) is { } missing)
            return $"First: {Resolve(missing)?.Title ?? missing}.";
        if (!Playable(def)) return "Not in this world yet.";
        if (p.Active.Count >= MaxActive) return $"{MaxActive} quests at once at most.";
        return null;
    }

    /// <summary>Starts a quest; null when started, else why not. The first one started is tracked.</summary>
    public string? Start(QuestsProgress p, string id, long now, int seed = 0, bool given = false)
    {
        if (Resolve(id) is not { } def) return "No such quest.";
        if (CanStart(p, def, given) is { } why) return why;
        p.Active[id] = new QuestState { Started = now, Seed = seed };
        p.Tracked ??= id;
        return null;
    }

    public bool Abandon(QuestsProgress p, string id)
    {
        if (!p.Active.Remove(id)) return false;
        if (p.Tracked == id) p.Tracked = NextTracked(p);
        return true;
    }

    /// <summary>Tracks a running quest; null untracks.</summary>
    public bool Track(QuestsProgress p, string? id)
    {
        if (id != null && !p.Active.ContainsKey(id)) return false;
        if (p.Tracked == id) return false;
        p.Tracked = id;
        return true;
    }

    /// <summary>The quests the log offers: picked from the log, not running, not done (or repeatable), their requirements done.</summary>
    public IEnumerable<QuestDef> Available(QuestsProgress p, QuestContext? ctx = null)
    {
        ctx ??= new QuestContext(p);
        foreach (var s in _sources)
            foreach (var def in s.For(ctx))
                if (def.Start == QuestStart.FromLog && CanStart(p, def) == null) yield return def;
    }

    /// <summary>The current step of a running quest, or null.</summary>
    public Step? Current(string id, QuestState state) =>
        Resolve(id) is { } def && state.Step < def.Steps.Length ? def.Steps[state.Step] : null;

    /// <summary>An event: quests that start on it, then every running quest's current step.</summary>
    public List<QuestChange> OnEvent(QuestsProgress p, AdventureEvent e, long now)
    {
        var changes = new List<QuestChange>();
        var ctx = new QuestContext(p);
        foreach (var s in _sources)
            foreach (var def in s.For(ctx))
                if (def.Start == QuestStart.OnEvent && def.Steps.Length > 0 && def.Steps[0].Amount(e) > 0 && CanStart(p, def) == null)
                {
                    p.Active[def.Id] = new QuestState { Started = now };
                    p.Tracked ??= def.Id;
                    changes.Add(new QuestChange(def.Id, QuestChangeKind.Started, 0, 0));
                }
        foreach (string id in p.Active.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            if (!p.Active.TryGetValue(id, out var state) || Resolve(id) is not { } def || state.Step >= def.Steps.Length) continue;
            var step = def.Steps[state.Step];
            int amount = step.Amount(e);
            if (amount <= 0) continue;
            if (step.Distinct(e) is { } distinct)
            {
                if (state.Seen.Contains(distinct)) continue;
                state.Seen.Add(distinct);
                amount = 1;
            }
            Advance(p, def, state, amount, now, changes);
        }
        return changes;
    }

    /// <summary>The player is at LV95 (<paramref name="e"/>, <paramref name="n"/>): running quests whose current step is to reach a place there move on.</summary>
    public List<QuestChange> OnPosition(QuestsProgress p, double e, double n, long now)
    {
        var changes = new List<QuestChange>();
        // a quest that starts on its first step starts on getting there
        var ctx = new QuestContext(p, e, n);
        foreach (var s in _sources)
            foreach (var def in s.For(ctx))
                if (def.Start == QuestStart.OnEvent && def.Steps.Length > 0 && def.Steps[0] is ReachStep first
                    && Inside(first.At, e, n) && CanStart(p, def) == null)
                {
                    p.Active[def.Id] = new QuestState { Started = now };
                    p.Tracked ??= def.Id;
                    changes.Add(new QuestChange(def.Id, QuestChangeKind.Started, 0, 0));
                }
        foreach (string id in p.Active.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            if (!p.Active.TryGetValue(id, out var state) || Resolve(id) is not { } def || state.Step >= def.Steps.Length) continue;
            if (def.Steps[state.Step] is not ReachStep reach || !Inside(reach.At, e, n)) continue;
            Advance(p, def, state, reach.Count - state.Count, now, changes);
        }
        return changes;
    }

    private bool Inside(TargetRef target, double e, double n)
    {
        if (PoisByDiscoveryOnly && target is PoiTarget) return false;
        if (Locate(target) is not { } at) return false;
        double de = e - at.E, dn = n - at.N;
        return de * de + dn * dn <= (double)at.RadiusM * at.RadiusM;
    }

    /// <summary>Whether any running quest waits on a position, or a quest may start on one (the service's 2 Hz check skips the others).</summary>
    public bool WaitsOnPosition(QuestsProgress p)
    {
        foreach (var (id, state) in p.Active)
            if (Current(id, state) is ReachStep) return true;
        foreach (var s in _sources)
            foreach (var def in s.For(new QuestContext(p)))
                if (def.Start == QuestStart.OnEvent && def.Steps.Length > 0 && def.Steps[0] is ReachStep && CanStart(p, def) == null) return true;
        return false;
    }

    /// <summary>A target's position and radius: an area's own, a POI's from the places catalogue.</summary>
    public (double E, double N, float RadiusM)? Locate(TargetRef target) => target switch
    {
        AreaTarget a => (a.E, a.N, a.RadiusM),
        PoiTarget poi => ResolvePoi?.Invoke(poi.PoiId),
        _ => null,
    };

    private static void Advance(QuestsProgress p, QuestDef def, QuestState state, int amount, long now, List<QuestChange> changes)
    {
        var step = def.Steps[state.Step];
        state.Count = Math.Min(step.Count, state.Count + amount);
        if (state.Count < step.Count)
        {
            changes.Add(new QuestChange(def.Id, QuestChangeKind.Progressed, state.Step, state.Count));
            return;
        }
        state.Step++;
        state.Count = 0;
        state.Seen.Clear();
        if (state.Step < def.Steps.Length)
        {
            changes.Add(new QuestChange(def.Id, QuestChangeKind.StepDone, state.Step, 0));
            return;
        }
        p.Active.Remove(def.Id);
        p.Done[def.Id] = now;
        if (p.Tracked == def.Id) p.Tracked = NextTracked(p);
        changes.Add(new QuestChange(def.Id, QuestChangeKind.Completed, def.Steps.Length - 1, step.Count));
    }

    private static string? NextTracked(QuestsProgress p) => p.Active.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
}
