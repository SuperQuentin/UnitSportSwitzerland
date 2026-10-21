using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// A movement through a lit junction as the cars' yield rule sees it (#1046): its line (tile-local x, y, z, the signal's tile, as
/// <c>JPTH</c> and <c>CPAR</c> write them), its signal group, who moves on it, its move (none for walkers) and the half width of
/// the band its movers may take.
/// </summary>
public readonly record struct ConflictWay(float[] Points, int Group, ConflictMover Mover, SignalMoves Move, float HalfWidth);

/// <summary>
/// Where a car on a junction path gives way to another movement (#1046): <see cref="Other"/> is that movement's index in the
/// ways; <see cref="At"/> how far along the car's path its body first enters the other's band and <see cref="Out"/> where it has
/// left it again; <see cref="Enter"/> how far along the other's line its mover first enters the car's lane.
/// </summary>
public readonly record struct YieldPoint(int Other, float At, float Out, float Enter);

/// <summary>
/// The points where a car on a lit junction's path waits for a gap (#1046): a permissive conflict (<see cref="SignalPlan.Conflict"/>)
/// the car's movement is the one that yields in, by <see cref="SignalPlan.Conflicts"/>' rules: a left turn from a shared lane to
/// oncoming cars that are not turning left, a turn from a shared lane across a bike way or into an arm across its crossing.
/// Each found where the car's path first meets that movement's band, as <c>ConflictTimes.Meet</c> walks the lines (#979).
/// </summary>
public static class ConflictPoints
{
    /// <summary>A car's half width and a little (m): its body, not its lane, comes up to the other's band.</summary>
    public const float CarBody = 1f;

    /// <summary>The lines are walked in steps of this (m).</summary>
    private const float Step = 0.5f;

    /// <summary>Whether a car on <paramref name="mine"/> gives way to <paramref name="other"/> when both are green.</summary>
    public static bool Yields(SignalPlan plan, SignalPlan.Conflict[,] conflicts, ConflictWay mine, ConflictWay other)
    {
        if (mine.Mover != ConflictMover.Car || mine.Group < 0 || other.Group < 0 || mine.Group == other.Group) return false;
        if (mine.Group >= plan.Groups.Count || other.Group >= plan.Groups.Count) return false;
        if (conflicts[mine.Group, other.Group] != SignalPlan.Conflict.Permissive) return false;
        // only a shared lane's turns yield: a pocket's arrow is protected
        if (plan.Groups[mine.Group].Kind != SignalGroupKind.Car || mine.Move == SignalMoves.Through) return false;
        return other.Mover switch
        {
            // a left turn to oncoming cars; two lefts never yield to each other
            ConflictMover.Car => mine.Move == SignalMoves.Left && other.Move != SignalMoves.Left,
            // riders turning right keep to their own way round the corner (#971)
            ConflictMover.Bike => other.Move != SignalMoves.Right,
            _ => true,
        };
    }

    /// <summary>The points along way <paramref name="mine"/> where its cars give way, nearest first; one per other group and mover.</summary>
    public static List<YieldPoint> For(SignalPlan plan, SignalPlan.Conflict[,] conflicts, IReadOnlyList<ConflictWay> ways, int mine)
    {
        var list = new List<YieldPoint>();
        for (int k = 0; k < ways.Count; k++)
        {
            if (k == mine || !Yields(plan, conflicts, ways[mine], ways[k])) continue;
            if (Meet(ways[mine], CarBody, ways[k], OwnBody(ways[k])) is not var (at, outAt, enter)) continue;
            // the walkers' line is written both ways: once, the nearer (each car path stays: its cars are the ones that come)
            int same = ways[k].Mover == ConflictMover.Walker
                ? list.FindIndex(y => ways[y.Other].Group == ways[k].Group && ways[y.Other].Mover == ConflictMover.Walker) : -1;
            if (same < 0) list.Add(new YieldPoint(k, at, outAt, enter));
            else if (list[same].At > at) list[same] = new YieldPoint(k, at, outAt, enter);
        }
        list.Sort((a, b) => a.At.CompareTo(b.At));
        return list;
    }

    /// <summary>The width one mover takes about its own line (m): a car's or a rider's body, a walker's.</summary>
    private static float OwnBody(ConflictWay w) => w.Mover switch { ConflictMover.Car => CarBody, ConflictMover.Bike => 0.5f, _ => 0.3f };

    /// <summary>
    /// Where <paramref name="a"/>'s mover (<paramref name="aBody"/> m either side of its line) first comes into <paramref name="b"/>'s
    /// band and leaves it again, metres along <paramref name="a"/>, and where <paramref name="b"/>'s first comes into
    /// <paramref name="a"/>'s band, metres along <paramref name="b"/>; null where they never meet.
    /// </summary>
    public static (float At, float Out, float Enter)? Meet(ConflictWay a, float aBody, ConflictWay b, float bBody)
    {
        if (First(a.Points, b.Points, aBody + b.HalfWidth) is not var (at, outAt)) return null;
        var (enter, _) = First(b.Points, a.Points, bBody + a.HalfWidth) ?? (0f, 0f);
        return (at, outAt, enter);
    }

    /// <summary>The first stretch of <paramref name="line"/> within <paramref name="reach"/> m of <paramref name="other"/> (flat): from and to, m along it.</summary>
    private static (float From, float To)? First(float[] line, float[] other, float reach)
    {
        float along = 0f, from = -1f;
        int n = line.Length / 3;
        for (int i = 0; i + 1 < n; i++)
        {
            float x0 = line[i * 3], z0 = line[i * 3 + 2], x1 = line[i * 3 + 3], z1 = line[i * 3 + 5];
            float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            if (len < 1e-6f) continue;
            int steps = Math.Max(1, (int)MathF.Ceiling(len / Step));
            for (int s = i == 0 ? 0 : 1; s <= steps; s++)
            {
                float t = s / (float)steps, d = along + len * t;
                bool inside = Distance(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t, other) < reach;
                if (inside && from < 0f) from = d;
                else if (!inside && from >= 0f) return (from, d);
            }
            along += len;
        }
        return from >= 0f ? (from, along) : null;
    }

    /// <summary>Flat distance from (x, z) to a polyline.</summary>
    private static float Distance(float x, float z, float[] line)
    {
        float best = float.MaxValue;
        int n = line.Length / 3;
        for (int i = 0; i + 1 < n; i++)
        {
            float ax = line[i * 3], az = line[i * 3 + 2], dx = line[i * 3 + 3] - ax, dz = line[i * 3 + 5] - az;
            float l2 = dx * dx + dz * dz;
            float t = l2 > 1e-12f ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0f, 1f) : 0f;
            float ex = ax + dx * t - x, ez = az + dz * t - z;
            best = MathF.Min(best, ex * ex + ez * ez);
        }
        return n == 1 ? MathF.Sqrt((line[0] - x) * (line[0] - x) + (line[2] - z) * (line[2] - z)) : MathF.Sqrt(best);
    }
}
