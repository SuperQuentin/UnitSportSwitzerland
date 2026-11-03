namespace UnitSport.Terrain.Format;

/// <summary>
/// Which way trams run on each tram track (#1057). swissTLM3D maps a tram line's tracks one by one (Geneva: every tram segment
/// single, none <see cref="RoadFlags.DoubleTrack"/>) and records no direction; Swiss trams keep right, so a track with its partner
/// on its left carries the trams along its drawing, one with the partner on its right against it (the divided carriageways' rule,
/// <c>LaneGraph.OrientDivided</c>); a track with no partner beside it (a single-track stretch, a loop, a siding) both ways. Plain
/// (no Godot): the game's tram graph and RoadGen's tram ways through the junctions (<c>TileRewriter.Trams</c>) decide alike.
/// </summary>
public static class TramTracks
{
    /// <summary>A partner track's centre lies this far to the side at least and at most (m): metre gauge track centres are ~3 m apart.</summary>
    public const double PartnerMin = 1.5, PartnerMax = 6.0;

    /// <summary>Samples along a track every this many metres vote on its direction.</summary>
    private const double SampleStep = 4.0;

    /// <summary>A partner runs this close to parallel (the cosine of the angle between them).</summary>
    private const double Parallel = 0.85;

    /// <summary>The grid cell the tracks' segments are binned in (m).</summary>
    private const double Cell = 16.0;

    /// <summary>
    /// The direction trams run on each of <paramref name="lines"/> (plan points, E and N in metres, any common origin): +1 along its
    /// drawing, -1 against it, 0 both ways (no partner track, or as many samples with it on either side). Lines flagged
    /// <see cref="RoadFlags.DoubleTrack"/> carry both directions themselves: give them out (their result is then 0).
    /// </summary>
    public static int[] Directions(IReadOnlyList<(double E, double N)[]> lines, IReadOnlyList<bool>? skip = null)
    {
        var grid = new Dictionary<(long, long), List<(int Line, int Seg)>>();
        for (int l = 0; l < lines.Count; l++)
        {
            if (skip?[l] == true) continue;
            var p = lines[l];
            for (int i = 0; i + 1 < p.Length; i++)
            {
                double minE = Math.Min(p[i].E, p[i + 1].E), maxE = Math.Max(p[i].E, p[i + 1].E);
                double minN = Math.Min(p[i].N, p[i + 1].N), maxN = Math.Max(p[i].N, p[i + 1].N);
                for (long cx = (long)Math.Floor((minE - PartnerMax) / Cell); cx <= (long)Math.Floor((maxE + PartnerMax) / Cell); cx++)
                    for (long cy = (long)Math.Floor((minN - PartnerMax) / Cell); cy <= (long)Math.Floor((maxN + PartnerMax) / Cell); cy++)
                    {
                        if (!grid.TryGetValue((cx, cy), out var list)) grid[(cx, cy)] = list = new();
                        list.Add((l, i));
                    }
            }
        }
        var result = new int[lines.Count];
        for (int l = 0; l < lines.Count; l++)
        {
            if (skip?[l] == true) continue;
            var p = lines[l];
            int votes = 0, samples = 0;
            for (int i = 0; i + 1 < p.Length; i++)
            {
                double dx = p[i + 1].E - p[i].E, dy = p[i + 1].N - p[i].N, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) continue;
                double tx = dx / len, ty = dy / len;
                // the driver's right of (tx, ty) with north up
                double rx = ty, ry = -tx;
                for (double s = Math.Min(SampleStep * 0.5, len * 0.5); s < len; s += SampleStep)
                {
                    double x = p[i].E + tx * s, y = p[i].N + ty * s;
                    samples++;
                    if (!grid.TryGetValue(((long)Math.Floor(x / Cell), (long)Math.Floor(y / Cell)), out var near)) continue;
                    double best = double.MaxValue, side = 0;
                    foreach (var (ol, os) in near)
                    {
                        if (ol == l) continue;
                        var q = lines[ol];
                        double ax = q[os].E, ay = q[os].N, bx = q[os + 1].E - ax, by = q[os + 1].N - ay, l2 = bx * bx + by * by;
                        if (l2 < 1e-12) continue;
                        double ol2 = Math.Sqrt(l2);
                        if (Math.Abs((bx * tx + by * ty) / ol2) < Parallel) continue;
                        double t = Math.Clamp(((x - ax) * bx + (y - ay) * by) / l2, 0, 1);
                        double ex = ax + bx * t - x, ey = ay + by * t - y;
                        // beside it, not ahead or behind on the same line
                        if (Math.Abs(ex * tx + ey * ty) > 1.0) continue;
                        double d = Math.Sqrt(ex * ex + ey * ey);
                        if (d < PartnerMin || d > PartnerMax || d >= best) continue;
                        best = d;
                        side = ex * rx + ey * ry;
                    }
                    if (best < double.MaxValue) votes += side > 0 ? -1 : 1;   // the partner on the right: against the drawing
                }
            }
            // most samples with a partner on one side, or none
            result[l] = Math.Abs(votes) * 3 >= Math.Max(1, samples) ? Math.Sign(votes) : 0;
        }
        return result;
    }
}
