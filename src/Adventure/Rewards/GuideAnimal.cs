using System;
using System.Collections.Generic;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>A guide's move, as the server tells its follower: the chest it leads to, the leg it runs now (1..n) and the server time it set off.</summary>
public readonly record struct GuideMove(string ChestId, int Leg, double Since, bool Done);

/// <summary>
/// The guides being followed (#1227, rewards plan 5), server-led: a guide woken at its burrow (or perch) runs to its
/// first waypoint; when its follower's body comes within <see cref="Seals.GuideReachM"/> of the waypoint the guide
/// stands at, it runs on to the next; the follower reaching it at the last (by the chest) completes the seal. Every
/// peer plays a leg from its start time, so nothing is sent per frame. One guide per player; a second player
/// waking the same burrow gets its own. The server measures bodies, never inputs: a bot follows the same way.
/// </summary>
public sealed class GuideBook
{
    private sealed class Run
    {
        public required string Chest;
        public required SealLayout Layout;
        public int Leg;
        public double Since, Last;
    }

    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    public int Count => _runs.Count;

    /// <summary>The leg's running time (s) at the guide's speed.</summary>
    public static double LegSeconds(SealLayout l, int leg)
    {
        if (leg <= 0 || leg >= l.Props.Length) return 0;
        var a = l.Props[leg - 1];
        var b = l.Props[leg];
        double d = Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));
        return d / (l.Guide == GuideKind.Vulture ? Seals.VultureSpeed : Seals.MarmotSpeed);
    }

    /// <summary>Where the guide is at <paramref name="now"/>: from its leg's start toward its end (LV95 and the coarse height).</summary>
    public static (double E, double N, float Alt) At(SealLayout l, int leg, double since, double now)
    {
        if (l.Props.Length == 0) return default;
        leg = Math.Clamp(leg, 0, l.Props.Length - 1);
        if (leg == 0) return (l.Props[0].E, l.Props[0].N, l.Props[0].Alt);
        double dur = LegSeconds(l, leg), t = dur <= 0 ? 1 : Math.Clamp((now - since) / dur, 0, 1);
        var a = l.Props[leg - 1];
        var b = l.Props[leg];
        return (a.E + (b.E - a.E) * t, a.N + (b.N - a.N) * t, (float)(a.Alt + (b.Alt - a.Alt) * t));
    }

    /// <summary>Wakes the guide of <paramref name="chestId"/> for a player: it sets off on leg 1 now.</summary>
    public GuideMove Wake(string key, string chestId, SealLayout layout, double now)
    {
        _runs[key] = new Run { Chest = chestId, Layout = layout, Leg = 1, Since = now, Last = now };
        return new GuideMove(chestId, 1, now, false);
    }

    /// <summary>The guide a player follows, if any.</summary>
    public bool Following(string key, out string chestId, out int leg, out double since)
    {
        if (_runs.TryGetValue(key, out var r)) { (chestId, leg, since) = (r.Chest, r.Leg, r.Since); return true; }
        (chestId, leg, since) = ("", 0, 0);
        return false;
    }

    /// <summary>
    /// The follower's body is at (<paramref name="e"/>, <paramref name="n"/>): when the guide has arrived at its
    /// waypoint and the body is near it, the guide moves on (or, at the last, the run is done). Null when nothing changed.
    /// </summary>
    public GuideMove? Follow(string key, double e, double n, double now)
    {
        if (!_runs.TryGetValue(key, out var r)) return null;
        var l = r.Layout;
        if (now - r.Since < LegSeconds(l, r.Leg)) return null;
        var w = l.Props[r.Leg];
        if ((w.E - e) * (w.E - e) + (w.N - n) * (w.N - n) > Seals.GuideReachM * Seals.GuideReachM) return null;
        r.Last = now;
        if (r.Leg >= l.Props.Length - 1)
        {
            _runs.Remove(key);
            return new GuideMove(r.Chest, r.Leg, now, true);
        }
        r.Leg++;
        r.Since = now;
        return new GuideMove(r.Chest, r.Leg, now, false);
    }

    /// <summary>Drops guides nobody followed for <see cref="Seals.ForgetAfter"/>.</summary>
    public void Prune(double now)
    {
        var old = new List<string>();
        foreach (var (k, r) in _runs)
            if (now - r.Last > Seals.ForgetAfter) old.Add(k);
        foreach (var k in old) _runs.Remove(k);
    }

    public void Forget(string key) => _runs.Remove(key);
}
