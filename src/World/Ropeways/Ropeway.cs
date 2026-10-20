using System;
using System.Collections.Generic;

namespace UnitSport.World.Ropeways;

/// <summary>What carries the rider (#912). TLM does not tell a gondola from an aerial tramway: both are <see cref="Gondola"/>.</summary>
public enum RopewayKind : byte { Gondola, Chair, TBar, RopeTow }

/// <summary>
/// One lift (#912): its cable in LV95 (east, north) with the surveyed cable altitude, from the
/// bottom station to the top. Pure, so the joining and the motion are unit-tested.
/// </summary>
public sealed class RopewayLine
{
    public RopewayKind Kind { get; }
    public double[] E { get; }
    public double[] N { get; }
    public float[] Alt { get; }
    /// <summary>Distance along the cable, flat, from the bottom station.</summary>
    public float[] Arc { get; }
    public float Length => Arc[^1];
    /// <summary>The bottom station rounded to a metre: the same on every peer, the line's name in play.</summary>
    public long Key { get; }

    public RopewayLine(RopewayKind kind, double[] e, double[] n, float[] alt)
    {
        Kind = kind;
        // bottom first
        if (alt[^1] < alt[0])
        {
            Array.Reverse(e); Array.Reverse(n); Array.Reverse(alt);
        }
        E = e; N = n; Alt = alt;
        Arc = new float[e.Length];
        for (int i = 1; i < e.Length; i++)
            Arc[i] = Arc[i - 1] + (float)Math.Sqrt((e[i] - e[i - 1]) * (e[i] - e[i - 1]) + (n[i] - n[i - 1]) * (n[i] - n[i - 1]));
        Key = (long)Math.Round(e[0]) * 10_000_000L + (long)Math.Round(n[0]);
    }

    /// <summary>The cable at <paramref name="s"/> m from the bottom (clamped), and its flat direction up the line (unit east, north).</summary>
    public (double E, double N, float Alt, float DirE, float DirN) At(float s)
    {
        s = Math.Clamp(s, 0f, Length);
        int i = Array.BinarySearch(Arc, s);
        if (i < 0) i = ~i;
        i = Math.Clamp(i, 1, Arc.Length - 1);
        float seg = Arc[i] - Arc[i - 1];
        float t = seg < 1e-4f ? 0f : (s - Arc[i - 1]) / seg;
        double de = E[i] - E[i - 1], dn = N[i] - N[i - 1];
        float len = (float)Math.Sqrt(de * de + dn * dn);
        return (E[i - 1] + de * t, N[i - 1] + dn * t, Alt[i - 1] + (Alt[i] - Alt[i - 1]) * t,
            len < 1e-4f ? 0f : (float)(de / len), len < 1e-4f ? 1f : (float)(dn / len));
    }
}

/// <summary>Joins the per-tile pieces of the ropeways into whole lines (#912).</summary>
public static class RopewayJoin
{
    /// <summary>One clipped piece: (east, north, altitude) triplets in LV95.</summary>
    public readonly record struct Piece(RopewayKind Kind, double[] Points);

    private static (long, long) Snap(double e, double n) => ((long)Math.Round(e * 2), (long)Math.Round(n * 2));

    /// <summary>
    /// Chains pieces that share an end (to 0.5 m, as a tile seam leaves them) into lines, each once,
    /// bottom station first. Lines shorter than <paramref name="minLength"/> are dropped (a scrap at a tile corner).
    /// </summary>
    public static List<RopewayLine> Join(IReadOnlyList<Piece> pieces, float minLength = 30f)
    {
        var ends = new Dictionary<(long, long), List<int>>();
        void AddEnd(double e, double n, int piece)
        {
            var k = Snap(e, n);
            if (!ends.TryGetValue(k, out var list)) ends[k] = list = new List<int>();
            list.Add(piece);
        }
        for (int p = 0; p < pieces.Count; p++)
        {
            var pts = pieces[p].Points;
            if (pts.Length < 6) continue;
            AddEnd(pts[0], pts[1], p);
            AddEnd(pts[^3], pts[^2], p);
        }

        var used = new bool[pieces.Count];
        var lines = new List<RopewayLine>();
        // start from an end no other piece shares: the station, not a seam; then any left (a loop)
        for (int pass = 0; pass < 2; pass++)
            for (int p = 0; p < pieces.Count; p++)
            {
                if (used[p] || pieces[p].Points.Length < 6) continue;
                var pts = pieces[p].Points;
                bool startFree = ends[Snap(pts[0], pts[1])].Count == 1, endFree = ends[Snap(pts[^3], pts[^2])].Count == 1;
                if (pass == 0 && !startFree && !endFree) continue;
                var e = new List<double>();
                var n = new List<double>();
                var a = new List<float>();
                int cur = p;
                bool forward = pass == 1 || startFree;
                while (true)
                {
                    used[cur] = true;
                    var q = pieces[cur].Points;
                    int count = q.Length / 3;
                    for (int k = 0; k < count; k++)
                    {
                        int i = forward ? k : count - 1 - k;
                        if (e.Count > 0 && k == 0) continue;   // the shared end, once
                        e.Add(q[i * 3]); n.Add(q[i * 3 + 1]); a.Add((float)q[i * 3 + 2]);
                    }
                    var tail = Snap(e[^1], n[^1]);
                    int next = -1;
                    foreach (int o in ends[tail])
                        if (!used[o] && pieces[o].Kind == pieces[p].Kind) { next = o; break; }
                    if (next < 0) break;
                    var np = pieces[next].Points;
                    forward = Snap(np[0], np[1]) == tail;
                    cur = next;
                }
                var line = new RopewayLine(pieces[p].Kind, e.ToArray(), n.ToArray(), a.ToArray());
                if (line.Length >= minLength) lines.Add(line);
            }
        return lines;
    }
}

/// <summary>
/// The carriers of a lift as a pure function of the sim clock (#912): they loop up the line and
/// back down it, evenly spaced, so every peer sees the same chair at the same place with nothing
/// sent. Carrier i is <c>s = (sim·speed + i·2L/n) mod 2L</c> along the loop: up while s &lt; L, down after.
/// </summary>
public static class RopewayMotion
{
    /// <summary>Speed (m/s), spacing between carriers (m), the carrier's hang below the cable (m), and the down side's offset (m).</summary>
    public static (float Speed, float Spacing, float Hang, float Side) Of(RopewayKind k) => k switch
    {
        RopewayKind.Gondola => (5f, 45f, 3.2f, 3f),
        RopewayKind.Chair => (4.5f, 22f, 2.3f, 2.5f),
        RopewayKind.TBar => (3f, 14f, 0f, 1.5f),
        _ => (2f, 8f, 0f, 1f),
    };

    public static int Count(RopewayLine line)
    {
        var (_, spacing, _, _) = Of(line.Kind);
        return Math.Max(2, (int)Math.Round(2 * line.Length / spacing));
    }

    /// <summary>Carrier <paramref name="i"/>'s place on the loop, 0..2L.</summary>
    public static float Loop(RopewayLine line, int i, double sim)
    {
        var (speed, _, _, _) = Of(line.Kind);
        double loop = 2.0 * line.Length;
        double s = (sim * speed + i * loop / Count(line)) % loop;
        return (float)(s < 0 ? s + loop : s);
    }

    /// <summary>
    /// A place on the loop as a point on the line: up the line at <c>loop</c> while it is under L,
    /// back down it after, offset to the side; the flat direction it travels.
    /// </summary>
    public static (double E, double N, float Alt, float DirE, float DirN, bool Up) Place(RopewayLine line, float loop)
    {
        var (_, _, _, side) = Of(line.Kind);
        if (loop <= line.Length)
        {
            var (e, n, alt, de, dn) = line.At(loop);
            return (e, n, alt, de, dn, true);
        }
        var (e2, n2, alt2, de2, dn2) = line.At(2 * line.Length - loop);
        // the down side: to the right of the way down (left of the way up)
        return (e2 - dn2 * side, n2 + de2 * side, alt2, -de2, -dn2, false);
    }

    /// <summary>The carrier that reaches the bottom station next, and in how many seconds.</summary>
    public static (int Carrier, double Wait) Next(RopewayLine line, double sim)
    {
        var (speed, _, _, _) = Of(line.Kind);
        double loop = 2.0 * line.Length;
        int best = 0;
        double bestWait = double.MaxValue;
        for (int i = 0, n = Count(line); i < n; i++)
        {
            double to = (loop - Loop(line, i, sim)) % loop;
            double wait = to / speed;
            if (wait < bestWait) { bestWait = wait; best = i; }
        }
        return (best, bestWait);
    }
}
