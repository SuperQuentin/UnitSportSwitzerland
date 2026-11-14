namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Layout;

/// <summary>
/// The car paths through the written junction cores, indexed by 20 m cell (#1929): a pole or a sign keeps a lane's half width
/// off every one of them. A core's outline does not cover the paths where they run on past a mouth, and the network's
/// ribbons may be narrower than the lane a path carries: a give-way sign and a main-road sign stood on paths there (Geneva
/// 2502195,1128169 and 2500596,1121787) and stopped the long drive.
/// </summary>
public sealed class CarPaths
{
    private const double Cell = 20.0;
    private readonly Dictionary<(int, int), List<(Vec2 A, Vec2 B, double Half)>> _cells = new();

    public CarPaths(IEnumerable<JunctionPlan> written)
    {
        foreach (var p in written)
            foreach (var c in p.Core?.Connectors ?? [])
            {
                if (c.Kind != ConnectorKind.Car) continue;
                double half = Math.Max(c.Width, c.ToWidth) * 0.5;
                for (int i = 1; i < c.Centre.Count; i++)
                {
                    var (a, b) = (c.Centre[i - 1], c.Centre[i]);
                    for (int cx = Key(Math.Min(a.X, b.X) - half); cx <= Key(Math.Max(a.X, b.X) + half); cx++)
                        for (int cy = Key(Math.Min(a.Y, b.Y) - half); cy <= Key(Math.Max(a.Y, b.Y) + half); cy++)
                        {
                            if (!_cells.TryGetValue((cx, cy), out var list)) _cells[(cx, cy)] = list = new();
                            list.Add((a, b, half));
                        }
                }
            }
    }

    /// <summary>Whether a pole of <paramref name="radius"/> at <paramref name="at"/> stands within a car path's lane.</summary>
    public bool Blocks(Vec2 at, double radius) =>
        _cells.TryGetValue((Key(at.X), Key(at.Y)), out var near)
        && near.Any(s => PriorityPlanner.DistanceToSegment(at, s.A, s.B) < s.Half + radius);

    private static int Key(double v) => (int)Math.Floor(v / Cell);
}
