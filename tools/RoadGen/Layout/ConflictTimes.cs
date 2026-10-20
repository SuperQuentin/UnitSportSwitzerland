namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// The conflict times between a lit junction's signal groups (#979, the user's rule; as SN 640 837 / RiLSA's intergreen times):
/// for two conflicting movements, the shortest time from the end of the ending one's green to the start of the starting one's
/// green that keeps their trails apart (the user): a green movement holds its path from when its first mover reaches a point to
/// when its last has left it, the last entering before red (it crosses its line as its amber ends). At each point the two share,
/// <c>amber + (clearing distance + length) / clearing speed − entering distance / entering speed</c>, the worst point binding
/// (<see cref="Meet"/>); the next group's red-amber not counted; negative where the geometry allows. Each movement is a line from
/// its own stop line: the cars' and the riders' junction paths, the riders' ways from path to path, the walkers along the zebra
/// both ways. A group pair's time is the most of its lines' pairs; NaN where their lines never meet.
/// </summary>
public static class ConflictTimes
{
    /// <summary>Clearing and entering speeds (m/s) and lengths (m) by who moves (the user's defaults, Oct 20 2026).</summary>
    /// <remarks>A cyclist enters at 30 km/h: it depends on the cyclist, the first at green may be a fast e-bike already rolling
    /// (the user, Oct 20 2026; clearing stays at 4 m/s).</remarks>
    public const double CarClear = 10, CarEnter = 40 / 3.6, BikeClear = 4, BikeEnter = 30 / 3.6, WalkClear = 1.2, WalkEnter = 1.5;
    public const double CarLength = 6, BikeLength = 2, WalkLength = 0;

    /// <summary>
    /// A turning car at most the speed its path's radius allows with this sideways acceleration (m/s²), and never over
    /// <see cref="TurnMost"/> (the user, Oct 20 2026: livelier than traffic's comfortable 2.5, nearer SN 640 838's flat 10 m/s in
    /// turns and 7 on tight radii: ~6.3 m/s at r = 10 m, ~9 at r = 20).
    /// </summary>
    public const double Lateral = 4, TurnMost = 10;

    /// <summary>Lines are walked in steps of this (m) to find where they meet.</summary>
    private const double Step = 0.5;

    /// <summary>Where two lines run on side by side (a merge), the conflict ends this far past where they first touch (m).</summary>
    private const double MergeLength = 8;

    /// <summary>Who moves along a line.</summary>
    public enum Mover : byte { Car, Bike, Walker }

    /// <summary>A walker's own width (m): one walker takes this much of the zebra, not all of it.</summary>
    public const double WalkBody = 0.5;

    /// <summary>
    /// A movement as a line from its stop line (LV95), with its group, who moves, its width (the band its movers may take: a lane,
    /// the whole zebra), speeds, length and amber.
    /// </summary>
    public sealed record Line(int Group, Mover Mover, List<Vec2> Points, double Width, double Clear, double Enter, double Length, double Amber)
    {
        /// <summary>
        /// The width one mover takes about its own line, for when it enters or leaves another's band: a vehicle its lane's (as laid,
        /// it may be anywhere on it), a walker only its own (#979, the user: walkers enter the middle of the crossing later and
        /// leave it sooner than its sides; the zebra's width is where the others may be, not where this one is).
        /// </summary>
        public double Body => Mover == Mover.Walker ? WalkBody : Width;
    }

    /// <summary>
    /// The table for <paramref name="plan"/> at <paramref name="jp"/>: its car and riders' connectors (<see cref="JunctionCore.Connectors"/>
    /// and <paramref name="rides"/>), each in the group <paramref name="groupOf"/> gives it and on the way <paramref name="laid"/>
    /// gives it where it is written other than its centre (riders carried onto an exit's path), as its junction path is written,
    /// and each arm's crossing in its pedestrian group. <c>[ending, starting]</c>, NaN where no lines of the two meet.
    /// </summary>
    public static float[,] Of(JunctionPlan jp, SignalPlan plan, IEnumerable<Connector> rides, Func<Connector, int> groupOf,
        Func<Line, bool>? keep = null, Func<Connector, List<Vec2>?>? laid = null)
    {
        var lines = Lines(jp, plan, rides, groupOf, laid).Where(l => keep?.Invoke(l) ?? true).ToList();
        int n = plan.Groups.Count;
        var table = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                table[i, j] = float.NaN;
        var byGroup = lines.GroupBy(l => l.Group).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (g, mine) in byGroup)
            foreach (var (h, theirs) in byGroup)
            {
                if (g == h) continue;
                double most = double.NaN;
                foreach (var a in mine)
                    foreach (var b in theirs)
                        if (Time(a, b) is { } t && !(t <= most)) most = t;
                table[g, h] = double.IsNaN(most) ? float.NaN : (float)Math.Round(most, 1);
            }
        return table;
    }

    /// <summary>The time for line <paramref name="ending"/>'s trail against <paramref name="starting"/>'s, at the point where they meet worst (<see cref="Meet"/>); null where they never meet.</summary>
    public static double? Time(Line ending, Line starting)
    {
        if (Meet(ending, starting) is not var (clear, enter)) return null;
        return ending.Amber + (clear + ending.Length) / ending.Clear - enter / starting.Enter;
    }

    /// <summary>
    /// Where two movements' trails meet worst (#979, the user): a green movement is a stream, its path held from when its first
    /// mover reaches a point to when its last has left it, however many enter between. For every point the two trails share
    /// (<paramref name="a"/>'s mover <c>s</c> m along its line in <paramref name="b"/>'s band while <paramref name="b"/>'s,
    /// <c>u</c> m along, is in <paramref name="a"/>'s, each with its own <see cref="Line.Body"/>: a walker holds where it walks, not
    /// the whole zebra), the ending trail closes at <c>amber + (s + length) / v_clear</c> after its green and the starting one opens
    /// at <c>u / v_enter</c> after its own: the point where the first is latest against the second binds. Its <c>s</c> (to past
    /// it) and <c>u</c>; null where the trails never share a point. A merge counts its first <see cref="MergeLength"/> m only.
    /// </summary>
    public static (double Clear, double Enter)? Meet(Line a, Line b)
    {
        double ra = (b.Width + a.Body) * 0.5, rb = (a.Width + b.Body) * 0.5, near = 2 * (ra + rb);
        var sa = Sample(a.Points);
        var sb = Sample(b.Points);
        var shared = new List<(double S, double U)>();
        double firstS = double.MaxValue, firstU = double.MaxValue;
        foreach (var (p, s, ta) in sa)
            foreach (var (q, u, tb) in sb)
            {
                var d = q - p;
                // b's mover in a's band and a's in b's, each across the other's line where they are
                if (d.LengthSquared > near * near || Math.Abs(ta.Cross(d)) >= rb || Math.Abs(tb.Cross(d)) >= ra) continue;
                shared.Add((s, u));
                firstS = Math.Min(firstS, s);
                firstU = Math.Min(firstU, u);
            }
        (double Clear, double Enter)? worst = null;
        double most = double.NegativeInfinity;
        foreach (var (s, u) in shared)
        {
            if (s > firstS + MergeLength || u > firstU + MergeLength) continue;
            // (cleared when past the next step: within a step, on the safe side)
            double t = (s + Step + a.Length) / a.Clear - u / b.Enter;
            if (t > most) (most, worst) = (t, (s + Step, u));
        }
        return worst;
    }

    /// <summary>A line every <see cref="Step"/> m: each point, how far along, and its direction there.</summary>
    private static List<(Vec2 Point, double Along, Vec2 Dir)> Sample(List<Vec2> line)
    {
        var list = new List<(Vec2, double, Vec2)>();
        double along = 0;
        for (int i = 1; i < line.Count; i++)
        {
            double len = line[i].DistanceTo(line[i - 1]);
            if (len < 1e-9) continue;
            var dir = (line[i] - line[i - 1]) * (1 / len);
            if (list.Count == 0) list.Add((line[i - 1], 0, dir));
            for (double s = Step; s < len; s += Step) list.Add((line[i - 1] + dir * s, along + s, dir));
            along += len;
            list.Add((line[i], along, dir));
        }
        return list;
    }

    /// <summary>The movements of a lit junction as lines from their stop lines (see <see cref="Of"/>).</summary>
    public static List<Line> Lines(JunctionPlan jp, SignalPlan plan, IEnumerable<Connector> rides, Func<Connector, int> groupOf,
        Func<Connector, List<Vec2>?>? laid = null)
    {
        var core = jp.Core!;
        var lines = new List<Line>();
        foreach (var c in core.Connectors.Concat(rides))
        {
            int g = groupOf(c);
            if (g < 0 || c.Centre.Count < 2 || jp.Arms.FirstOrDefault(x => x.Arm == c.FromArm) is not { Axis: { } axis } a) continue;
            var ex = jp.Exterior.FirstOrDefault(x => x.Arm == a);
            var mouth = core.Mouths.First(m => m.Arm == a.Arm);
            bool car = c.Kind == ConnectorKind.Car;
            double stop = (car ? ex?.CarStop : ex?.RiderStop ?? ex?.CarStop) ?? 0;
            var points = new List<Vec2> { axis.At(mouth.Station + stop, c.FromOffset) };
            var centre = laid?.Invoke(c) ?? c.Centre;
            points.AddRange(centre);
            double limit = plan.Arms[jp.Arms.IndexOf(a)].SpeedKmh / 3.6;
            if (car)
            {
                double turn = Math.Min(TurnMost, Math.Sqrt(Lateral * LeastRadius(centre)));
                lines.Add(new Line(g, Mover.Car, points, c.Width, Min(CarClear, limit, turn), Min(CarEnter, limit, turn), CarLength, plan.PassingTime(g)));
            }
            else lines.Add(new Line(g, Mover.Bike, points, c.Width, BikeClear, BikeEnter, BikeLength, plan.PassingTime(g)));
        }
        // the walkers: along the middle of each arm's zebra, from either side: past the kerb across a separated path beside it, as
        // the zebra is painted (the user: the riders on it cross the walkers too)
        for (int k = 0; k < jp.Arms.Count; k++)
        {
            var a = jp.Arms[k];
            int g = plan.Groups.FindIndex(x => x.Kind == SignalGroupKind.Pedestrian && x.Arm == k);
            if (g < 0 || a.Axis is null || jp.Exterior.FirstOrDefault(x => x.Arm == a)?.Bars.FirstOrDefault(b => b.Kind == BarKind.Crosswalk) is not { } bar)
                continue;
            var mouth = core.Mouths.First(m => m.Arm == a.Arm);
            var section = ExteriorPlanner.SectionAt(a, 0);
            double from = section.PathBand(right: false) is var (cl, wl) ? Math.Min(bar.Left, cl - wl * 0.5) : bar.Left;
            double to = section.PathBand(right: true) is var (cr, wr) ? Math.Max(bar.Right, cr + wr * 0.5) : bar.Right;
            var left = a.Axis.At(mouth.Station + bar.Middle, from);
            var right = a.Axis.At(mouth.Station + bar.Middle, to);
            lines.Add(new Line(g, Mover.Walker, [left, right], bar.To - bar.From, WalkClear, WalkEnter, WalkLength, plan.PassingTime(g)));
            lines.Add(new Line(g, Mover.Walker, [right, left], bar.To - bar.From, WalkClear, WalkEnter, WalkLength, plan.PassingTime(g)));
        }
        return lines;

        static double Min(double a, double b, double c) => Math.Max(0.5, Math.Min(a, Math.Min(b, c)));
    }

    /// <summary>The least radius along a line (m), from each three points in a row; a straight line's is infinite.</summary>
    public static double LeastRadius(List<Vec2> line)
    {
        double least = double.PositiveInfinity;
        for (int i = 1; i + 1 < line.Count; i++)
        {
            var (p, q, r) = (line[i - 1], line[i], line[i + 1]);
            double a = p.DistanceTo(q), b = q.DistanceTo(r), c = p.DistanceTo(r);
            double cross = Math.Abs((q - p).X * (r - p).Y - (q - p).Y * (r - p).X);
            if (cross < 1e-9 || a < 0.2 || b < 0.2) continue;
            least = Math.Min(least, a * b * c / (2 * cross));
        }
        return least;
    }
}
