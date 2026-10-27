using System;
using System.Collections.Generic;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>Which guide animal leads to a guide-sealed chest: a marmot on the ground, a bearded vulture along a cliff.</summary>
public enum GuideKind : byte { None = 0, Marmot = 1, Vulture = 2 }

/// <summary>A seal's prop: a wood stack, a ring, a plate, a cowbell, a guide's waypoint (LV95, the coarse height; a ring's centre is above it).</summary>
public readonly record struct SealProp(double E, double N, float Alt, float Radius);

/// <summary>
/// Where a sealed chest's challenge stands (#1227, rewards plan 3), computed from the chest alone: the same on
/// every peer. <see cref="Props"/>: Höhenfeuer the three stacks; rings in the order they are run; the two plates;
/// the four bells then the fifth that plays the tune; a guide's waypoints from its burrow (or perch) to the
/// chest. <see cref="Seconds"/> is the time a run or the bonfires allow.
/// </summary>
public sealed record SealLayout(SealKind Kind, SealProp[] Props, int[] Tune, float Seconds, GuideKind Guide)
{
    /// <summary>The steps that complete it.</summary>
    public int Steps => Kind switch
    {
        SealKind.Bonfire => Props.Length,
        SealKind.Rings => Props.Length,
        SealKind.Plates => 1,
        SealKind.Bells => Tune.Length,
        SealKind.Guide => Props.Length - 1,
        _ => 0,
    };

    public static readonly SealLayout None = new(SealKind.None, Array.Empty<SealProp>(), Array.Empty<int>(), 0, GuideKind.None);
}

/// <summary>The seals' shared numbers and the layout builder.</summary>
public static class Seals
{
    /// <summary>Höhenfeuer: the three stacks lit within this.</summary>
    public const float BonfireSeconds = 90;
    /// <summary>A step done by a party member counts for every member within this (the plan's co-op rule).</summary>
    public const float CoopRadiusM = 60;
    /// <summary>Seal progress is forgotten this long after its last step.</summary>
    public const double ForgetAfter = 600;
    /// <summary>A ring's radius: on foot, jumping or gliding through it.</summary>
    public const float RingRadius = 3f;
    /// <summary>A guide's walking speed (m/s): a marmot scurries, a vulture glides faster.</summary>
    public const float MarmotSpeed = 4.5f, VultureSpeed = 9f;
    /// <summary>How near the follower must come to the guide's waypoint for it to move on.</summary>
    public const float GuideReachM = 8f;

    /// <summary>The bit a seal state carries when it is complete (<c>SealState</c>'s mask).</summary>
    public const int DoneBit = 1 << 30;

    /// <summary>
    /// The layout of a sealed chest. <paramref name="ground"/> is the coarse height (the server's and the
    /// client's alike); props stay inside the chest's own tile so one tile's grid answers for all of them.
    /// <paramref name="slope"/> (degrees) picks the guide: a marmot on walkable ground, a vulture on a cliff.
    /// </summary>
    public static SealLayout Layout(RewardSpot chest, Func<double, double, float> ground, Func<double, double, float>? slope = null)
    {
        if (chest.Kind != SpotKind.Chest || chest.Seal == SealKind.None) return SealLayout.None;
        var rng = new SpotRng(chest.Hash ^ 0x5ea15UL);
        double minE = Math.Floor(chest.E / 1000) * 1000 + 6, minN = Math.Floor(chest.N / 1000) * 1000 + 6;
        double maxE = minE + 988, maxN = minN + 988;
        SealProp At(double e, double n, float up, float radius)
        {
            e = Math.Clamp(e, minE, maxE);
            n = Math.Clamp(n, minN, maxN);
            return new SealProp(e, n, ground(e, n) + up, radius);
        }
        double heading = rng.Range(0, Math.PI * 2);
        switch (chest.Seal)
        {
            case SealKind.Bonfire:
            {
                var props = new SealProp[3];
                for (int i = 0; i < 3; i++)
                {
                    double a = heading + i * Math.PI * 2 / 3 + rng.Range(-0.3, 0.3), r = rng.Range(14, 32);
                    props[i] = At(chest.E + Math.Sin(a) * r, chest.N + Math.Cos(a) * r, 0, 1.2f);
                }
                return new SealLayout(SealKind.Bonfire, props, Array.Empty<int>(), BonfireSeconds, GuideKind.None);
            }
            case SealKind.Rings:
            {
                // 4-6 rings on a gentle arc ending 10 m before the chest, run in order from the far end
                int n = 4 + rng.Below(3);
                var props = new SealProp[n];
                double bend = rng.Range(-0.25, 0.25), length = 0;
                for (int i = 0; i < n; i++)
                {
                    double d = 10 + (n - 1 - i) * 22, a = heading + bend * (n - 1 - i);
                    props[i] = At(chest.E + Math.Sin(a) * d, chest.N + Math.Cos(a) * d, 1.8f, RingRadius);
                    if (i > 0) length += Dist(props[i - 1], props[i]);
                }
                // a brisk run: 5 m/s on the flat, plus some for the climb
                float climb = Math.Max(0, props[n - 1].Alt - props[0].Alt);
                return new SealLayout(SealKind.Rings, props, Array.Empty<int>(), (float)(length / 5 + climb / 0.6 + 8), GuideKind.None);
            }
            case SealKind.Plates:
            {
                double a = heading, side = a + Math.PI / 2;
                double cx = chest.E + Math.Sin(a) * 6, cy = chest.N + Math.Cos(a) * 6;
                var props = new[]
                {
                    At(cx + Math.Sin(side) * 3.5, cy + Math.Cos(side) * 3.5, 0, 1.3f),
                    At(cx - Math.Sin(side) * 3.5, cy - Math.Cos(side) * 3.5, 0, 1.3f),
                };
                return new SealLayout(SealKind.Plates, props, Array.Empty<int>(), 0, GuideKind.None);
            }
            case SealKind.Bells:
            {
                // a rail of four bells, 0.9 m apart, and the fifth that plays the tune at its end
                double a = heading, side = a + Math.PI / 2;
                double cx = chest.E + Math.Sin(a) * 5, cy = chest.N + Math.Cos(a) * 5;
                var props = new SealProp[5];
                for (int i = 0; i < 5; i++)
                {
                    double off = (i - 2) * 0.9;
                    props[i] = At(cx + Math.Sin(side) * off, cy + Math.Cos(side) * off, 0, 0.6f);
                }
                var tune = new int[4];
                for (int i = 0; i < tune.Length; i++)
                {
                    int b;
                    do b = rng.Below(4); while (i > 0 && b == tune[i - 1]);
                    tune[i] = b;
                }
                return new SealLayout(SealKind.Bells, props, tune, 0, GuideKind.None);
            }
            case SealKind.Guide:
            {
                // the burrow (or perch) 80-130 m off, then 2-4 legs to the chest, each bent a little aside
                int legs = 2 + rng.Below(3);
                double far = rng.Range(80, 130);
                double be = chest.E + Math.Sin(heading) * far, bn = chest.N + Math.Cos(heading) * far;
                var props = new SealProp[legs + 1];
                for (int i = 0; i <= legs; i++)
                {
                    double t = (double)i / legs;
                    double e = be + (chest.E - be) * t, n = bn + (chest.N - bn) * t;
                    if (i > 0 && i < legs)
                    {
                        double side = rng.Range(-18, 18);
                        e += Math.Cos(heading) * side;
                        n -= Math.Sin(heading) * side;
                    }
                    if (i == legs)
                    {
                        // the guide stops beside the chest, not on it
                        e += Math.Sin(heading) * 2.5;
                        n += Math.Cos(heading) * 2.5;
                    }
                    props[i] = At(e, n, 0, GuideReachM);
                }
                var guide = (slope?.Invoke(props[0].E, props[0].N) ?? 0) > 32 ? GuideKind.Vulture : GuideKind.Marmot;
                return new SealLayout(SealKind.Guide, props, Array.Empty<int>(), 0, guide);
            }
        }
        return SealLayout.None;
    }

    private static double Dist(SealProp a, SealProp b) => Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));

    /// <summary>
    /// The party members a step counts for (the co-op rule): the doer, and every member whose body is within
    /// <see cref="CoopRadiusM"/> of where it was done. Strangers each do their own.
    /// </summary>
    public static List<string> Coop(string doer, IEnumerable<(string Key, double E, double N)> party, double e, double n)
    {
        var keys = new List<string> { doer };
        foreach (var (key, me, mn) in party)
        {
            if (key == doer || keys.Contains(key)) continue;
            double de = me - e, dn = mn - n;
            if (de * de + dn * dn <= CoopRadiusM * CoopRadiusM) keys.Add(key);
        }
        return keys;
    }

    /// <summary>The seal's name on a prompt and a card.</summary>
    public static string Name(SealKind k) => k switch
    {
        SealKind.Bonfire => "Höhenfeuer",
        SealKind.Rings => "Ring run",
        SealKind.Plates => "Pressure plates",
        SealKind.Bells => "Cowbell tune",
        SealKind.Guide => "Guide",
        _ => "",
    };

    /// <summary>What the seal asks, one line (the chest's prompt while sealed).</summary>
    public static string Hint(SealKind k) => k switch
    {
        SealKind.Bonfire => "Light the three wood stacks round it within 90 s.",
        SealKind.Rings => "Run through the rings in order, in time.",
        SealKind.Plates => "Hold both stone plates down at once: a friend, or something dropped on one.",
        SealKind.Bells => "Ring the four cowbells in the order the fifth plays.",
        SealKind.Guide => "Follow the guide to it.",
        _ => "",
    };
}

/// <summary>What a seal step did.</summary>
public enum SealStep : byte
{
    /// <summary>Nothing changed (a ring already passed, an unknown step).</summary>
    Ignored,
    /// <summary>A step counted.</summary>
    Progress,
    /// <summary>The seal is complete: the chest may open.</summary>
    Done,
    /// <summary>The window ran out or the tune was wrong: start again.</summary>
    Reset,
    /// <summary>It was complete already.</summary>
    AlreadyDone,
}

/// <summary>
/// Seal progress (#1227, rewards plan 3), per player and per chest, held by the server in memory only: forgotten
/// <see cref="Seals.ForgetAfter"/> after the last step (<see cref="Prune"/>) or when the player leaves
/// (<see cref="Forget"/>). The server checks where the body is before a step; this book checks the order and the
/// windows. Plain C#, tested.
/// </summary>
public sealed class SealBook
{
    private sealed class Entry
    {
        public int Mask, Next;
        public double Started, Last;
        public bool Done;
    }

    private readonly Dictionary<(string Key, string Chest), Entry> _entries = new();

    public int Count => _entries.Count;

    /// <summary>
    /// Applies step <paramref name="step"/> of <paramref name="chestId"/>'s seal for a player at server time
    /// <paramref name="now"/> (seconds). Bonfire: the stack lit (0-2), all three within the window. Rings: the
    /// ring passed, in order, within the time from the first. Plates: 0, both held (the server checked). Bells:
    /// the bell rung (0-3), against the tune; a wrong one starts again. Guide: the leg reached (1..n).
    /// </summary>
    public SealStep Step(string key, string chestId, SealLayout layout, int step, double now)
    {
        if (layout.Kind == SealKind.None) return SealStep.Ignored;
        var id = (key, chestId);
        if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry { Started = now };
        if (e.Done) return SealStep.AlreadyDone;
        var result = SealStep.Ignored;
        switch (layout.Kind)
        {
            case SealKind.Bonfire:
                if (step < 0 || step >= layout.Props.Length) break;
                if (e.Mask != 0 && now - e.Started > layout.Seconds)
                {
                    // the first fires burnt down: this one starts a new window
                    e.Mask = 0;
                    e.Started = now;
                    e.Last = now;
                    e.Mask |= 1 << step;
                    return SealStep.Reset;
                }
                if (e.Mask == 0) e.Started = now;
                if ((e.Mask & (1 << step)) != 0) break;
                e.Mask |= 1 << step;
                result = e.Mask == (1 << layout.Props.Length) - 1 ? SealStep.Done : SealStep.Progress;
                break;
            case SealKind.Rings:
                if (step == 0)
                {
                    // the first ring (re)starts the run
                    e.Started = now;
                    e.Next = 1;
                    e.Mask = 1;
                    result = layout.Props.Length == 1 ? SealStep.Done : SealStep.Progress;
                    break;
                }
                if (step != e.Next || e.Next == 0) break;
                if (now - e.Started > layout.Seconds)
                {
                    e.Next = 0;
                    e.Mask = 0;
                    e.Last = now;
                    return SealStep.Reset;
                }
                e.Next++;
                e.Mask |= 1 << step;
                result = e.Next >= layout.Props.Length ? SealStep.Done : SealStep.Progress;
                break;
            case SealKind.Plates:
                if (step != 0) break;
                e.Mask = 3;
                result = SealStep.Done;
                break;
            case SealKind.Bells:
                if (step < 0 || step > 3) break;
                if (layout.Tune[e.Next] != step)
                {
                    e.Next = 0;
                    e.Mask = 0;
                    e.Last = now;
                    return SealStep.Reset;
                }
                e.Next++;
                e.Mask = (1 << e.Next) - 1;
                result = e.Next >= layout.Tune.Length ? SealStep.Done : SealStep.Progress;
                break;
            case SealKind.Guide:
                if (step <= e.Next || step > layout.Steps) break;
                e.Next = step;
                e.Mask = (1 << step) - 1;
                result = step >= layout.Steps ? SealStep.Done : SealStep.Progress;
                break;
        }
        if (result == SealStep.Ignored) return result;
        e.Last = now;
        if (result == SealStep.Done) e.Done = true;
        return result;
    }

    /// <summary>Whether the player has completed this chest's seal (and it is still remembered).</summary>
    public bool IsDone(string key, string chestId) => _entries.TryGetValue((key, chestId), out var e) && e.Done;

    /// <summary>The state the owner is told: the steps done as bits, and <see cref="Seals.DoneBit"/> when complete.</summary>
    public int MaskOf(string key, string chestId) =>
        _entries.TryGetValue((key, chestId), out var e) ? e.Mask | (e.Done ? Seals.DoneBit : 0) : 0;

    /// <summary>The next ring, bell or leg expected (rings, bells, guide).</summary>
    public int NextOf(string key, string chestId) => _entries.TryGetValue((key, chestId), out var e) ? e.Next : 0;

    /// <summary>A chest was opened: its seal is spent.</summary>
    public void Spend(string key, string chestId) => _entries.Remove((key, chestId));

    /// <summary>Drops what nobody touched for <see cref="Seals.ForgetAfter"/>; returns how many.</summary>
    public int Prune(double now)
    {
        var old = new List<(string, string)>();
        foreach (var (id, e) in _entries)
            if (now - Math.Max(e.Last, e.Started) > Seals.ForgetAfter) old.Add(id);
        foreach (var id in old) _entries.Remove(id);
        return old.Count;
    }

    /// <summary>A player left: everything of theirs goes.</summary>
    public void Forget(string key)
    {
        var mine = new List<(string, string)>();
        foreach (var id in _entries.Keys)
            if (id.Key == key) mine.Add(id);
        foreach (var id in mine) _entries.Remove(id);
    }
}
