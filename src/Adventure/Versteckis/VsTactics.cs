using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitSport.Adventure.Versteckis;

// Plain C#, no Godot: linked into the unit tests (VsTacticsTests in VersteckisTests; docs/notes/bots/versteckis.md).

/// <summary>
/// The Versteckis bots' choices (#1268, plan "Bots"), pure, as <c>BrTactics</c> is: where a hider bot hides, when it
/// flees, moos or goes for an objective, and what a seeker bot suspects. A seeker's suspicion is built only from what
/// a player could notice: a prop that moved, a prop where no village rule puts its kind, a sound heard near it, a
/// sniff's puff. The brain feeds it its senses; nothing here reads a role or a hider's position.
/// </summary>
public static class VsTactics
{
    public const float SpotFromDoor = 25f, SpotFromHider = 15f, SpotNearKind = 8f;
    public const float FleeWithin = 7f, MooBeyond = 40f, ObjectiveSeekerBeyond = 80f;
    /// <summary>A seeker facing within this cosine of a hider is "facing it".</summary>
    public const float FacingCos = 0.7f;

    /// <summary>
    /// A hider bot's spot: near a decor prop of its own kind (within 8 m), 25 m from the seeker's door and 15 m from
    /// the other hiders, scored by cover from the door's line of sight (<paramref name="hiddenFromDoor"/>, a ray on the
    /// server's collision) and distance. Easy bots choose among 10 candidates, hard ones among all. Null: none fits.
    /// </summary>
    public static (double E, double N, float Yaw)? HiderSpot(IReadOnlyList<VsDecor> decor, VsProp prop, double doorE, double doorN,
        IReadOnlyList<(double E, double N)> others, Func<double, double, bool>? hiddenFromDoor, int difficulty, Random rng, double ce, double cn, float radius)
    {
        var kin = decor.Where(d => d.Kind == prop).ToList();
        if (kin.Count == 0) return null;
        // shuffled, so two bots of one kind do not crowd the same bale
        var order = kin.OrderBy(_ => rng.Next()).ToList();
        int pool = difficulty <= 0 ? Math.Min(10, order.Count) : order.Count;
        (double E, double N, float Yaw)? best = null;
        double bestScore = double.MinValue;
        for (int i = 0; i < pool; i++)
        {
            var d = order[i];
            // beside it, as a copy would stand: 1.6 to 3 m off, any side
            double a = rng.NextDouble() * Math.Tau, r = 1.6 + rng.NextDouble() * 1.4;
            double e = d.E + Math.Cos(a) * r, n = d.N + Math.Sin(a) * r;
            if (!VsRules.Inside(ce, cn, radius - 4, e, n)) continue;
            double fromDoor = Math.Sqrt((e - doorE) * (e - doorE) + (n - doorN) * (n - doorN));
            if (fromDoor < SpotFromDoor) continue;
            if (others.Any(o => (o.E - e) * (o.E - e) + (o.N - n) * (o.N - n) < SpotFromHider * SpotFromHider)) continue;
            double score = Math.Min(fromDoor, 120) / 120.0 + (hiddenFromDoor?.Invoke(e, n) == true ? 1.0 : 0.0) + rng.NextDouble() * 0.3;
            if (score > bestScore) { bestScore = score; best = (e, n, d.Yaw); }
        }
        return best;
    }

    /// <summary>Whether a seeker at (<paramref name="se"/>, <paramref name="sn"/>) facing (<paramref name="fe"/>, <paramref name="fn"/>) faces a point.</summary>
    public static bool Facing(double se, double sn, double fe, double fn, double e, double n)
    {
        double de = e - se, dn = n - sn, l = Math.Sqrt(de * de + dn * dn), fl = Math.Sqrt(fe * fe + fn * fn);
        if (l < 0.5) return true;
        if (fl < 1e-6) return false;
        return (de * fe + dn * fn) / (l * fl) >= FacingCos;
    }

    /// <summary>A hider flees (a sprint, then walks away) when the seeker is within 7 m and facing it.</summary>
    public static bool Flee(double dist, bool facing) => dist < FleeWithin && facing;

    /// <summary>A hider moos when the seeker is past 40 m and not facing it, by its boldness (chance per think).</summary>
    public static bool Moo(double dist, bool facing, float boldness, double roll) => dist > MooBeyond && !facing && roll < 0.08 + 0.25 * boldness;

    /// <summary>A hider goes for an objective when the seeker is past 80 m from it, it is not done, and no other hider is on it.</summary>
    public static bool GoForObjective(double seekerToSite, bool done, bool taken) => !done && !taken && seekerToSite > ObjectiveSeekerBeyond;

    /// <summary>The base suspicion of any prop a seeker bot sees, by difficulty: the reason a bot makes real misses.</summary>
    public static float BaseSuspicion(int difficulty) => difficulty switch { <= 0 => 0.15f, 1 => 0.1f, _ => 0.05f };
}

/// <summary>
/// A seeker bot's memory of the props it has seen (#1268): each by the key its senses give it, where and what it was,
/// and how suspicious it is. Built from sight, hearing and sniffs only (<see cref="VsTactics"/>).
/// </summary>
public sealed class VsSuspicion
{
    public const float Moved = 0.6f, OutOfPlace = 0.45f, HeardNear = 0.3f, SniffNear = 0.5f, Missed = -1f;
    public const float HeardWithin = 10f, SniffWithin = 4.5f, MoveThreshold = 1f;

    public sealed class Seen
    {
        public string Key = "";
        public VsProp Kind;
        public double E, N;
        public double When;
        public float Suspicion;
    }

    private readonly Dictionary<string, Seen> _seen = new();
    private readonly float _base;

    public VsSuspicion(int difficulty) => _base = VsTactics.BaseSuspicion(difficulty);

    public IReadOnlyCollection<Seen> All => _seen.Values;

    /// <summary>A prop in sight: remembered; it gains suspicion when it moved since last seen, or stands where its kind never does.</summary>
    public void Look(string key, VsProp kind, double e, double n, double now, bool inPlace)
    {
        if (!_seen.TryGetValue(key, out var s))
        {
            _seen[key] = s = new Seen { Key = key, Kind = kind, E = e, N = n, When = now, Suspicion = _base + (inPlace ? 0 : OutOfPlace) };
            return;
        }
        double moved = Math.Sqrt((s.E - e) * (s.E - e) + (s.N - n) * (s.N - n));
        if (moved > MoveThreshold || s.Kind != kind) s.Suspicion += Moved;
        if (!inPlace && s.Kind != kind) s.Suspicion += OutOfPlace;
        s.Kind = kind;
        s.E = e;
        s.N = n;
        s.When = now;
    }

    /// <summary>A sound heard at a point (Moo, a sprint's thumps, the poof): the props within 10 m of it.</summary>
    public void Heard(double e, double n) => Near(e, n, HeardWithin, HeardNear);

    /// <summary>A sniff's puff (offset up to 4 m from its hider): the props within 4.5 m of it.</summary>
    public void Puff(double e, double n) => Near(e, n, SniffWithin, SniffNear);

    private void Near(double e, double n, float within, float add)
    {
        foreach (var s in _seen.Values)
            if ((s.E - e) * (s.E - e) + (s.N - n) * (s.N - n) <= within * within) s.Suspicion += add;
    }

    /// <summary>A tag on it missed: it was decor (or a decoy); never again.</summary>
    public void Miss(string key)
    {
        if (_seen.TryGetValue(key, out var s)) s.Suspicion = Missed;
    }

    /// <summary>Caught: forgotten (its body follows the seeker now).</summary>
    public void Forget(string key) => _seen.Remove(key);

    /// <summary>The most suspicious prop above <paramref name="atLeast"/>, nearest first among equals; null when none is.</summary>
    public Seen? Best(double e, double n, float atLeast)
    {
        Seen? best = null;
        double bestScore = double.MinValue;
        foreach (var s in _seen.Values)
        {
            if (s.Suspicion < atLeast) continue;
            double d = Math.Sqrt((s.E - e) * (s.E - e) + (s.N - n) * (s.N - n));
            double score = s.Suspicion - d / 200.0;
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return best;
    }
}
