using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Snow;

/// <summary>
/// A ski race down the slopes (#909): from the run nearest the start, down it, then on down the
/// run that continues from its bottom, until the race is long enough or nothing continues. Pure
/// (unit-tested): the caller gives the runs, read from the tiles' <c>.pst</c> records, and the
/// terrain's altitude.
/// </summary>
public static class PisteCourse
{
    /// <summary>One line slope in LV95: its centreline (east, north interleaved) and width.</summary>
    public readonly record struct Run(ulong Id, double[] Points, float Width)
    {
        public int Count => Points.Length / 2;
        public (double E, double N) At(int i) => (Points[i * 2], Points[i * 2 + 1]);
    }

    /// <summary>The spacing of the course's points, as a road route's.</summary>
    public const float Step = 2f;

    /// <summary>
    /// The line slopes a skier races (downhill, connection) from these tiles, each once: a run's
    /// path is kept whole in every tile it touches, so the same one comes from several.
    /// </summary>
    public static List<Run> Runs(IEnumerable<PisteTile> tiles)
    {
        var runs = new List<Run>();
        var seen = new HashSet<(ulong, long, long)>();
        foreach (var tile in tiles)
            foreach (var r in tile.Records)
            {
                if (r.IsArea || r.Type is not (PisteType.Downhill or PisteType.Connection) || r.Path.Length < 4) continue;
                double e0 = tile.Id.MinE + r.Path[0], n0 = tile.Id.MinN + r.Path[1];
                // a relation's member ways share its id: tell them apart by where they start
                if (!seen.Add((r.Id, (long)Math.Round(e0), (long)Math.Round(n0)))) continue;
                var pts = new double[r.Path.Length];
                for (int i = 0; i < pts.Length; i += 2) { pts[i] = tile.Id.MinE + r.Path[i]; pts[i + 1] = tile.Id.MinN + r.Path[i + 1]; }
                runs.Add(new Run(r.Id, pts, r.WidthM));
            }
        return runs;
    }

    /// <summary>
    /// The course from (e, n): points every <see cref="Step"/> m with the slope's width, or null when no
    /// run comes within <paramref name="reach"/> m. Down the nearest run from its nearest point; at
    /// each bottom, on to the run with a point within <paramref name="join"/> m that goes on down at
    /// least 5 m, the one turning least, never a run taken already.
    /// </summary>
    public static List<(double E, double N, float Width)>? Chain(IReadOnlyList<Run> runs, double e, double n,
        Func<double, double, float> altitude, float metres, float reach = 300f, float join = 40f)
    {
        int bestRun = -1, bestIndex = 0;
        double bestD = reach * reach;
        for (int r = 0; r < runs.Count; r++)
            for (int i = 0; i < runs[r].Count; i++)
            {
                var (pe, pn) = runs[r].At(i);
                double d = (pe - e) * (pe - e) + (pn - n) * (pn - n);
                if (d < bestD) { bestD = d; bestRun = r; bestIndex = i; }
            }
        if (bestRun < 0) return null;

        var path = new List<(double E, double N, float W)>();
        var taken = new HashSet<int>();
        int run = bestRun, from = bestIndex;
        double total = 0;
        while (true)
        {
            taken.Add(run);
            var r = runs[run];
            // down: toward whichever end is lower
            int dir = altitude(r.Points[^2], r.Points[^1]) <= altitude(r.Points[0], r.Points[1]) ? 1 : -1;
            for (int i = from; i >= 0 && i < r.Count; i += dir)
            {
                var (pe, pn) = r.At(i);
                if (path.Count > 0)
                {
                    double seg = Math.Sqrt(Sq(pe - path[^1].E) + Sq(pn - path[^1].N));
                    if (total + seg >= metres)
                    {
                        // cut on this segment, exactly at the race length
                        double t = seg < 1e-9 ? 0 : (metres - total) / seg;
                        path.Add((path[^1].E + (pe - path[^1].E) * t, path[^1].N + (pn - path[^1].N) * t, r.Width));
                        return Resample(path);
                    }
                    total += seg;
                }
                path.Add((pe, pn, r.Width));
            }
            if (path.Count < 2 || Next(runs, taken, path, altitude, join) is not { } next) break;
            (run, from) = next;
        }
        return path.Count >= 2 ? Resample(path) : null;
    }

    /// <summary>
    /// Up the start's run from (e, n), at most <paramref name="metres"/>, every <see cref="Step"/> m,
    /// the start first: the slope behind the grid, which race NPCs ski down to their slots.
    /// </summary>
    public static List<(double E, double N, float Width)> Above(IReadOnlyList<Run> runs, double e, double n,
        Func<double, double, float> altitude, float metres, float reach = 300f)
    {
        int bestRun = -1, bestIndex = 0;
        double bestD = reach * reach;
        for (int r = 0; r < runs.Count; r++)
            for (int i = 0; i < runs[r].Count; i++)
            {
                var (pe, pn) = runs[r].At(i);
                double d = (pe - e) * (pe - e) + (pn - n) * (pn - n);
                if (d < bestD) { bestD = d; bestRun = r; bestIndex = i; }
            }
        var path = new List<(double E, double N, float W)>();
        if (bestRun < 0) return path;
        var run = runs[bestRun];
        int up = altitude(run.Points[^2], run.Points[^1]) <= altitude(run.Points[0], run.Points[1]) ? -1 : 1;
        double total = 0;
        for (int i = bestIndex; i >= 0 && i < run.Count && total < metres; i += up)
        {
            var (pe, pn) = run.At(i);
            if (path.Count > 0) total += Math.Sqrt(Sq(pe - path[^1].E) + Sq(pn - path[^1].N));
            path.Add((pe, pn, run.Width));
        }
        return path.Count >= 2 ? Resample(path) : path;
    }

    /// <summary>The run that carries on down from the bottom of the course so far, and the point to join it at.</summary>
    private static (int Run, int From)? Next(IReadOnlyList<Run> runs, HashSet<int> taken, List<(double E, double N, float W)> path,
        Func<double, double, float> altitude, float join)
    {
        var (be, bn, _) = path[^1];
        var (ae, an, _) = path[Math.Max(0, path.Count - 6)];
        double ie = be - ae, inn = bn - an, il = Math.Sqrt(ie * ie + inn * inn);
        float bottom = altitude(be, bn);
        (int, int)? best = null;
        double bestTurn = double.MaxValue;
        for (int r = 0; r < runs.Count; r++)
        {
            if (taken.Contains(r)) continue;
            var run = runs[r];
            int near = -1;
            double nearD = join * join;
            for (int i = 0; i < run.Count; i++)
            {
                var (pe, pn) = run.At(i);
                double d = Sq(pe - be) + Sq(pn - bn);
                if (d < nearD) { nearD = d; near = i; }
            }
            if (near < 0) continue;
            int dir = altitude(run.Points[^2], run.Points[^1]) <= altitude(run.Points[0], run.Points[1]) ? 1 : -1;
            int end = dir > 0 ? run.Count - 1 : 0;
            var (ee, en) = run.At(end);
            if (altitude(ee, en) > bottom - 5f || near == end) continue;   // it does not go on down from here
            // how far it turns from the way the course came in
            var (oe, on) = run.At(Math.Clamp(near + dir * 5, 0, run.Count - 1));
            double ox = oe - run.At(near).E, oy = on - run.At(near).N, ol = Math.Sqrt(ox * ox + oy * oy);
            double turn = il < 1e-3 || ol < 1e-3 ? 0 : Math.Acos(Math.Clamp((ie * ox + inn * oy) / (il * ol), -1, 1));
            if (turn < bestTurn) { bestTurn = turn; best = (r, near); }
        }
        return best;
    }

    /// <summary>Every <see cref="Step"/> m along the polyline, each point with the width of the run it is on.</summary>
    private static List<(double E, double N, float Width)> Resample(List<(double E, double N, float W)> path)
    {
        var outp = new List<(double, double, float)> { path[0] };
        double carry = 0;
        for (int i = 1; i < path.Count; i++)
        {
            var (ae, an, _) = path[i - 1];
            var (be, bn, w) = path[i];
            double len = Math.Sqrt(Sq(be - ae) + Sq(bn - an));
            if (len < 1e-6) continue;
            double s = Step - carry;
            for (; s <= len; s += Step) outp.Add((ae + (be - ae) * s / len, an + (bn - an) * s / len, w));
            carry = len - (s - Step);
        }
        // the true end, where the course was cut or the last run ends
        if (carry > 0.1) outp.Add(path[^1]);
        return outp;
    }

    private static double Sq(double v) => v * v;
}
