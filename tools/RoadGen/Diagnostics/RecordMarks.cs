namespace UnitSport.Tools.RoadGen.Diagnostics;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.TestRegion;

/// <summary>
/// A network build's records as marks to compare with another build's (#1647, #1660): each kind at a position with the
/// look that must be equal in both (a signal's plan size and canton look, a pole's flags, an approach's lanes, a junction
/// record's class) and, for a junction record, its plan area. Shared by the leaf compare (<c>CubeTestRegion</c>) and the
/// tiling check (<see cref="TilingCheck"/>).
/// </summary>
public static class RecordMarks
{
    /// <summary>A record at a position (E, N on the compared plane, its height), its look and its size (NaN: none).</summary>
    public readonly record struct Mark(double E, double N, double H, string Look, double Size = double.NaN);

    /// <summary>
    /// A tile's records by kind, at positions through <paramref name="place"/> (a record's x, y, z). Arm numbers are left out
    /// of the looks: a plan numbers its arms by heading, and two planes' headings differ by the grid's convergence.
    /// </summary>
    public static void Collect(RoadTile tile, Dictionary<string, List<Mark>> into, Func<double, double, double, (double E, double N, double H)> place)
    {
        void Add(string kind, double x, double y, double z, string look, double size = double.NaN)
        {
            var (e, n, h) = place(x, y, z);
            if (!into.TryGetValue(kind, out var list)) into[kind] = list = new();
            list.Add(new Mark(e, n, h, look, size));
        }
        foreach (var j in tile.Junctions)
        {
            double x = 0, y = 0, z = 0;
            int count = j.Vertices.Length / 3;
            if (count == 0) continue;
            for (int k = 0; k < j.Vertices.Length; k += 3) { x += j.Vertices[k]; y += j.Vertices[k + 1]; z += j.Vertices[k + 2]; }
            Add("junction", x / count, y / count, z / count, j.Class.ToString(), Area(j));
        }
        foreach (var s in tile.Signals)
        {
            var p = s.Plan;
            Add("signal", s.X, s.Y, s.Z, $"{p.Arms.Count} arms {p.Groups.Count} groups, peds {(p.PedestrianAmber ? "3" : "2")}-lens, "
                + $"plates {p.ArrowPlates}, tram T {p.TramTHead}, poles {p.PoleColour}, {s.Overheads.Count} overheads, {s.Gantries.Count} gantries");
            for (int k = 0; k + 2 < s.Stops.Length; k += 3)
                if (!float.IsNaN(s.Stops[k])) Add("stop line", s.Stops[k], s.Stops[k + 1], s.Stops[k + 2], "");
            foreach (var pole in s.Poles) Add("pole", pole.X, pole.Y, pole.Z, pole.Flags.ToString());
        }
        foreach (var a in tile.Approaches)
            Add("approach", a.X, a.Y, a.Z, string.Join(" | ", a.Lanes.Select(l => $"{l.Kind}:{l.Moves}")) + (a.Signal >= 0 ? " lit" : ""));
        foreach (var pp in tile.PointProps)
            if (pp.Type == PointPropType.CantonSign) Add("canton sign", pp.X, pp.Y, pp.Z, $"variant {pp.Variant}");
        foreach (var paint in tile.Paint)
            if (paint.Rgba == SignalTestRegion.BorderRgba && paint.Vertices.Length >= 6)
            {
                var v = paint.Vertices;
                Add("border stripe", (v[0] + v[^3]) / 2, (v[1] + v[^2]) / 2, (v[2] + v[^1]) / 2, "");
            }
    }

    /// <summary>A junction record's plan area (its triangles), m².</summary>
    public static double Area(RoadJunction j)
    {
        double a = 0;
        var v = j.Vertices;
        for (int t = 0; t + 2 < j.Indices.Length; t += 3)
        {
            int i0 = j.Indices[t] * 3, i1 = j.Indices[t + 1] * 3, i2 = j.Indices[t + 2] * 3;
            a += Math.Abs((v[i1] - v[i0]) * (v[i2 + 2] - v[i0 + 2]) - (v[i2] - v[i0]) * (v[i1 + 2] - v[i0 + 2])) * 0.5;
        }
        return a;
    }

    /// <summary>
    /// Each mark of <paramref name="a"/> paired with the nearest unpaired mark of <paramref name="b"/> within
    /// <paramref name="reach"/> m (the index into <paramref name="b"/>, -1 alone, and the distance); and which of
    /// <paramref name="b"/> stayed alone.
    /// </summary>
    public static (int[] Partner, double[] Distance, bool[] Used) Match(IReadOnlyList<Mark> a, IReadOnlyList<Mark> b, double reach)
    {
        var used = new bool[b.Count];
        var grid = new Dictionary<(long, long), List<int>>();
        double cell = Math.Max(reach, 1);
        for (int k = 0; k < b.Count; k++)
        {
            var c = ((long)Math.Floor(b[k].E / cell), (long)Math.Floor(b[k].N / cell));
            if (!grid.TryGetValue(c, out var list)) grid[c] = list = new();
            list.Add(k);
        }
        var partner = new int[a.Count];
        var dist = new double[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            var m = a[i];
            int best = -1;
            double bestD = reach;
            long ce = (long)Math.Floor(m.E / cell), cn = (long)Math.Floor(m.N / cell);
            for (long ie = ce - 1; ie <= ce + 1; ie++)
                for (long jn = cn - 1; jn <= cn + 1; jn++)
                    if (grid.TryGetValue((ie, jn), out var list))
                        foreach (int k in list)
                        {
                            if (used[k]) continue;
                            double d = Math.Sqrt((b[k].E - m.E) * (b[k].E - m.E) + (b[k].N - m.N) * (b[k].N - m.N));
                            if (d < bestD) (best, bestD) = (k, d);
                        }
            partner[i] = best;
            dist[i] = best < 0 ? double.NaN : bestD;
            if (best >= 0) used[best] = true;
        }
        return (partner, dist, used);
    }
}
