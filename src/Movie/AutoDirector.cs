namespace UnitSport.Movie;

/// <summary>
/// A first cut of a sequence from chosen shots (#778), to edit afterwards. Pure, deterministic:
/// <list type="bullet">
/// <item>The cuts fall on the music's bars (every 2, 4 or 8 beats, whichever gives shots of 2–4.5 s), or every
/// 3 s when there is no beat.</item>
/// <item>Each stretch gets the shot that scores best there: a following rig when its actor moves fast, a world
/// shot when the action is slow or turning, never the same shot twice in a row, and less of one used a moment
/// ago. A shot that cannot film there (its actor absent) is skipped.</item>
/// </list>
/// The result is ordinary cuts in the sequence, replacing the ones inside the stretch.
/// </summary>
public static class AutoDirector
{
    /// <summary>The cuts for <paramref name="shots"/> between <paramref name="from"/> and <paramref name="to"/>: (time, shot).</summary>
    public static List<(double T, int Shot)> Plan(MovieProject p, IReadOnlyList<int> shots, double from, double to)
    {
        var plan = new List<(double T, int Shot)>();
        if (shots.Count == 0 || to - from < MovieProject.MinLength) return plan;
        var cuts = CutTimes(p.CachedBeats(), from, to);
        int last = -1, before = -1;
        for (int k = 0; k < cuts.Count; k++)
        {
            double a = cuts[k], b = k + 1 < cuts.Count ? cuts[k + 1] : to, mid = (a + b) / 2;
            int best = -1;
            double bestScore = double.NegativeInfinity;
            foreach (int shot in shots)
            {
                if (shot < 0 || shot >= p.Cameras.Count || !p.ShotPose(shot, mid, null, out _, out _)) continue;
                double s = Score(p, shot, mid, k);
                if (shot == last) s -= 10;
                else if (shot == before) s -= 0.6;
                if (s > bestScore) { bestScore = s; best = shot; }
            }
            if (best < 0) continue;
            if (best == last) continue;   // only one shot could film here: it carries on
            plan.Add((a, best));
            before = last;
            last = best;
        }
        return plan;
    }

    /// <summary>
    /// Plans and writes the cuts into the active sequence: the old cuts inside the stretch go, the new ones
    /// come in with plain cuts between them. The number of cuts made.
    /// </summary>
    public static int Apply(MovieProject p, IReadOnlyList<int> shots, double from, double to)
    {
        var plan = Plan(p, shots, from, to);
        if (plan.Count == 0) return 0;
        p.Cuts.RemoveAll(c => c.T >= from - 1e-6 && c.T < to);
        foreach (var (t, shot) in plan) p.CutTo(t, shot, -1);
        return plan.Count;
    }

    /// <summary>Where the stretches start: bars of the beat, or every 3 s.</summary>
    internal static List<double> CutTimes(IReadOnlyList<(double T, bool Downbeat)> beats, double from, double to)
    {
        var inside = new List<double>();
        foreach (var (t, _) in beats)
            if (t >= from - 1e-6 && t < to) inside.Add(t);
        var cuts = new List<double> { from };
        if (inside.Count >= 4)
        {
            double period = (inside[^1] - inside[0]) / (inside.Count - 1);
            int every = 4;
            foreach (int n in new[] { 4, 8, 2 })
                if (n * period is >= 2.0 and <= 4.5) { every = n; break; }
            // from the first downbeat on, so the cuts land on the bar
            int start = 0;
            for (int i = 0; i < Math.Min(4, beats.Count); i++)
                if (beats[i].Downbeat) { start = i; break; }
            for (int i = 0; i < inside.Count; i++)
                if (i % every == start % every && inside[i] - cuts[^1] >= 1.5) cuts.Add(inside[i]);
        }
        else
        {
            for (double t = from + 3; t < to - 1.5; t += 3) cuts.Add(t);
        }
        return cuts;
    }

    /// <summary>How well shot <paramref name="shot"/> suits time <paramref name="t"/>: its rig against how fast its actor moves.</summary>
    private static double Score(MovieProject p, int shot, double t, int stretch)
    {
        var cam = p.Cameras[shot];
        int lane = cam.Target >= 0 ? cam.Target : FirstLane(p, t);
        double speed = 0, turn = 0;
        if (lane >= 0 && p.ActorFrame(lane, t - 0.5, out var e0, out var n0, out var a0, out var y0)
            && p.ActorFrame(lane, t + 0.5, out var e1, out var n1, out var a1, out var y1))
        {
            speed = Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0) + (a1 - a0) * (a1 - a0));
            double dy = (y1 - y0) % Math.Tau;
            if (dy > Math.PI) dy -= Math.Tau; else if (dy < -Math.PI) dy += Math.Tau;
            turn = Math.Abs(dy);
        }
        double s = 1;
        if (cam.Relative)
        {
            if (speed > 8) s += 1;
            else if (speed < 2) s -= 0.4;
            if (cam.Rig == ShotRig.Mount) s -= 0.2;   // a cockpit or bumper view, best not too often
        }
        else
        {
            if (speed < 3) s += 0.8;
            if (turn > 0.3) s += 0.5;
        }
        // a small fixed jitter, so equal shots take turns instead of the first always winning
        uint h = (uint)(shot * 73856093) ^ (uint)(stretch * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return s + (h & 0xffff) / 65535.0 * 0.3;
    }

    private static int FirstLane(MovieProject p, double t)
    {
        for (int lane = 0; lane < p.Lanes.Count; lane++)
            if (p.ActiveClip(lane, t) != null) return lane;
        return -1;
    }
}
