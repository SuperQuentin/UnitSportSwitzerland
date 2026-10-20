namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// The conflict times between a lit junction's signal groups (#979, the user's rule; as SN 640 837 / RiLSA's intergreen times):
/// for two conflicting movements, the shortest time from the end of the ending one's green to the start of the starting one's
/// green that lets the last vehicle entering before red (it crosses its line as its amber ends) clear the conflict before the
/// first one entering at green reaches it: <c>amber + (clearing distance + length) / clearing speed − entering distance /
/// entering speed</c>, the next group's red-amber not counted; negative where the geometry allows. Each movement is a line from
/// its own stop line: the cars' and the riders' junction paths, the riders' ways from path to path, the walkers along the zebra
/// both ways. A group pair's time is the most of its lines' pairs; NaN where their lines never meet.
/// </summary>
public static class ConflictTimes
{
    /// <summary>Clearing and entering speeds (m/s) and lengths (m) by who moves (the user's defaults, Oct 20 2026).</summary>
    public const double CarClear = 10, CarEnter = 40 / 3.6, BikeClear = 4, BikeEnter = 5, WalkClear = 1.2, WalkEnter = 1.5;
    public const double CarLength = 6, BikeLength = 2, WalkLength = 0;

    /// <summary>A turning car at most the speed its path's radius allows, with this lateral acceleration (m/s², as traffic drives).</summary>
    public const double Lateral = 2.5;

    /// <summary>Lines are walked in steps of this (m) to find where they meet.</summary>
    private const double Step = 0.5;

    /// <summary>Where two lines run on side by side (a merge), the conflict ends this far past where they first touch (m).</summary>
    private const double MergeLength = 8;

    /// <summary>Who moves along a line.</summary>
    public enum Mover : byte { Car, Bike, Walker }

    /// <summary>A movement as a line from its stop line (LV95), with its group, who moves, its width, speeds, length and amber.</summary>
    public sealed record Line(int Group, Mover Mover, List<Vec2> Points, double Width, double Clear, double Enter, double Length, double Amber);

    /// <summary>
    /// The table for <paramref name="plan"/> at <paramref name="jp"/>: its car and riders' connectors (<see cref="JunctionCore.Connectors"/>
    /// and <paramref name="rides"/>), each in the group <paramref name="groupOf"/> gives it (as its junction path is written), and
    /// each arm's crossing in its pedestrian group. <c>[ending, starting]</c>, NaN where no lines of the two meet.
    /// </summary>
    public static float[,] Of(JunctionPlan jp, SignalPlan plan, IEnumerable<Connector> rides, Func<Connector, int> groupOf,
        Func<Line, bool>? keep = null)
    {
        var lines = Lines(jp, plan, rides, groupOf).Where(l => keep?.Invoke(l) ?? true).ToList();
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

    /// <summary>The time for line <paramref name="ending"/>'s last vehicle against <paramref name="starting"/>'s first; null where they never meet.</summary>
    public static double? Time(Line ending, Line starting)
    {
        if (Meet(ending.Points, starting.Points, (ending.Width + starting.Width) * 0.5) is not var (clear, enter)) return null;
        return ending.Amber + (clear + ending.Length) / ending.Clear - enter / starting.Enter;
    }

    /// <summary>
    /// Where two lines meet, each within <paramref name="reach"/> of the other: how far along <paramref name="a"/> it has left the
    /// first stretch where they touch (a merge: <see cref="MergeLength"/> past its start at most), and how far along
    /// <paramref name="b"/> it first touches; null where they never do.
    /// </summary>
    public static (double Clear, double Enter)? Meet(List<Vec2> a, List<Vec2> b, double reach)
    {
        var sa = Sample(a);
        var sb = Sample(b);
        double? from = null, clear = null;
        foreach (var (p, s) in sa)
        {
            bool touch = Near(p, b, reach);
            if (touch && from is null) from = s;
            if (from is not null && (!touch || s - from >= MergeLength)) { clear = s; break; }
        }
        if (from is null) return null;
        clear ??= sa[^1].Along;
        foreach (var (p, s) in sb)
            if (Near(p, a, reach)) return (clear.Value, s);
        return null;
    }

    private static List<(Vec2 Point, double Along)> Sample(List<Vec2> line)
    {
        var list = new List<(Vec2, double)> { (line[0], 0) };
        double along = 0;
        for (int i = 1; i < line.Count; i++)
        {
            double len = line[i].DistanceTo(line[i - 1]);
            for (double s = Step; s < len; s += Step) list.Add((line[i - 1] + (line[i] - line[i - 1]) * (s / len), along + s));
            along += len;
            list.Add((line[i], along));
        }
        return list;
    }

    private static bool Near(Vec2 p, List<Vec2> line, double reach)
    {
        for (int i = 1; i < line.Count; i++)
        {
            var ab = line[i] - line[i - 1];
            double len2 = ab.Dot(ab);
            double t = len2 < 1e-12 ? 0 : Math.Clamp((p - line[i - 1]).Dot(ab) / len2, 0, 1);
            if ((line[i - 1] + ab * t).DistanceTo(p) < reach) return true;
        }
        return false;
    }

    /// <summary>The movements of a lit junction as lines from their stop lines (see <see cref="Of"/>).</summary>
    public static List<Line> Lines(JunctionPlan jp, SignalPlan plan, IEnumerable<Connector> rides, Func<Connector, int> groupOf)
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
            points.AddRange(c.Centre);
            double limit = plan.Arms[jp.Arms.IndexOf(a)].SpeedKmh / 3.6;
            if (car)
            {
                double turn = Math.Sqrt(Lateral * LeastRadius(c.Centre));
                lines.Add(new Line(g, Mover.Car, points, c.Width, Min(CarClear, limit, turn), Min(CarEnter, limit, turn), CarLength, plan.PassingTime(g)));
            }
            else lines.Add(new Line(g, Mover.Bike, points, c.Width, BikeClear, BikeEnter, BikeLength, plan.PassingTime(g)));
        }
        // the walkers: along the middle of each arm's zebra, from either kerb
        for (int k = 0; k < jp.Arms.Count; k++)
        {
            var a = jp.Arms[k];
            int g = plan.Groups.FindIndex(x => x.Kind == SignalGroupKind.Pedestrian && x.Arm == k);
            if (g < 0 || a.Axis is null || jp.Exterior.FirstOrDefault(x => x.Arm == a)?.Bars.FirstOrDefault(b => b.Kind == BarKind.Crosswalk) is not { } bar)
                continue;
            var mouth = core.Mouths.First(m => m.Arm == a.Arm);
            var left = a.Axis.At(mouth.Station + bar.Middle, bar.Left);
            var right = a.Axis.At(mouth.Station + bar.Middle, bar.Right);
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
