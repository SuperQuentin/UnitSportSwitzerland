namespace UnitSport.Tools.RoadGen.Network;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// Road heights against the ground (#119): the extractor drapes every line
/// <see cref="Drape"/> over the terrain (plus a small per-class lift against z-fighting), which a
/// road blend that carves the ground under every road no longer needs. The network stage moves
/// each at-grade road point, before anything else reads the lines (junction caps, rails, paint,
/// walls, sidewalks), to where a road really lies:
/// <list type="bullet">
/// <item>outside towns, <see cref="Drain"/> plus the class lift over the ground (a crowned,
/// drained road); the change is the same for every class, so lines that met still meet;</item>
/// <item>in town, a kerb below the ground (plus the class lift): the sidewalk's top
/// (<see cref="StreetPlanner.KerbCm"/> up) then lies on the ground, as in a real street;</item>
/// <item>between the two, by the <see cref="UrbanField"/> weight, which depends only on the
/// position: seams, junction arms and split pieces agree, and the transition stays gentle.</item>
/// </list>
/// The change is the same for every class at a given place, so the extractor's class lift (1.2 cm
/// per class step) survives everywhere: overlapping ribbons of different classes (a path along a
/// road, a square, a junction approach) stay apart instead of z-fighting.
/// Motorways and expressways never take the town's height. Bridges and tunnels keep theirs, and
/// so does every road within <see cref="StructureTaper"/> of a structure's end (smoothstep), so the
/// extractor's approach ramps still land on the deck or the portal. Rails take the same change as
/// the roads around them (<see cref="Applies"/>); watercourses are left as they are.
/// </summary>
public static class RoadHeights
{
    /// <summary>Mirror of <c>RoadExtractor.DrapeOffset</c> (another project).</summary>
    public const double Drape = 0.35;

    /// <summary>A rural road's surface over the ground, before the class lift: crown and drainage.</summary>
    public const double Drain = 0.08;

    /// <summary>Within this distance of a bridge's or tunnel's end a road keeps its extracted height.</summary>
    public const double StructureTaper = 30.0;

    /// <summary>Mirror of <c>RoadExtractor.ClassLift</c> for road classes.</summary>
    public static double ClassLift(RoadClass c) => (12 - (int)c) * 0.012;

    /// <summary>
    /// At-grade roads and rails. Rails move with the roads: they used to lie a few centimetres under
    /// the road they cross or run in (the class lift), hidden where they are not embedded (#124); left
    /// at the old drape they would stand 0.4 m over a town's street, or fight it where they blend down.
    /// </summary>
    public static bool Applies(RoadSegment s) =>
        (s.Class <= RoadClass.Square || s.Class == RoadClass.Railway) && s.PointCount >= 1
        && (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) == 0;

    /// <summary>Region numbers, printed with the stage report.</summary>
    public sealed class Stats
    {
        public int Points, Urban, Rural, Tapered;
        public readonly List<double> Grades = new();

        public string Format()
        {
            var c = CultureInfo.InvariantCulture;
            Grades.Sort();
            string g = Grades.Count == 0 ? "-" : string.Create(c,
                $"p99 {Grades[(int)(Grades.Count * 0.99)] * 100:F2} %, worst {Grades[^1] * 100:F2} %");
            return string.Create(c, $"""
                  road heights (#119): {Points:N0} at-grade points; rural {Rural:N0} (drape -> +{Drain:F2} m), town {Urban:N0} (ground - kerb), between {Points - Rural - Urban:N0}; {Tapered:N0} held by a structure end
                    grade added by the transition between two points: {g}
                """);
        }
    }

    /// <summary>
    /// The offset for a point of a line of class <paramref name="c"/> at LV95 (e, n), given its
    /// town weight <paramref name="w"/> (0 rural, 1 town) and its structure factor <paramref name="hold"/>
    /// (0 at a structure end, 1 away from any).
    /// </summary>
    public static double Offset(RoadClass c, double w, double hold)
    {
        double rural = Drain - Drape;
        if (c is RoadClass.Motorway or RoadClass.Expressway) w = 0;
        double town = -(Drape + StreetPlanner.KerbCm / 100.0);
        return (rural + (town - rural) * w) * hold;
    }

    /// <summary>Moves every at-grade road point of the lines (block and halo) in place.</summary>
    public static void Apply(IReadOnlyList<CrossSectionPlanner.Line> lines, UrbanField field, Stats stats, bool count)
    {
        var ends = new StructureEnds(lines);
        foreach (var line in lines)
        {
            var seg = line.Segment;
            if (!Applies(seg)) continue;
            var id = line.Tile;
            var p = seg.Points;
            double prevOffset = 0, prevX = 0, prevZ = 0;
            for (int i = 0; i < seg.PointCount; i++)
            {
                double x = p[i * 3], z = p[i * 3 + 2];
                double e = id.MinE + x, n = id.MaxN - z;
                double w = field.Weight(e, n);
                double d = ends.Distance(e, n);
                double t = Math.Clamp(d / StructureTaper, 0, 1);
                double hold = t * t * (3 - 2 * t);
                double offset = Offset(seg.Class, w, hold);
                p[i * 3 + 1] = (float)(p[i * 3 + 1] + offset);
                if (!count || !line.Write) continue;
                stats.Points++;
                if (hold < 1) stats.Tapered++;
                if (w >= 0.999) stats.Urban++; else if (w <= 0.001) stats.Rural++;
                if (i > 0)
                {
                    double run = Math.Sqrt((x - prevX) * (x - prevX) + (z - prevZ) * (z - prevZ));
                    if (run > 1) stats.Grades.Add(Math.Abs(offset - prevOffset) / run);
                }
                prevOffset = offset; prevX = x; prevZ = z;
            }
        }
    }

    /// <summary>The ends of every bridge and tunnel road line, LV95, in 64 m buckets.</summary>
    private sealed class StructureEnds
    {
        private const double Cell = 64;
        private readonly Dictionary<(long, long), List<(double E, double N)>> _buckets = new();

        public StructureEnds(IReadOnlyList<CrossSectionPlanner.Line> lines)
        {
            foreach (var line in lines)
            {
                var s = line.Segment;
                if (s.Class > RoadClass.Square && s.Class != RoadClass.Railway) continue;
                if (s.PointCount < 1 || (s.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) == 0) continue;
                foreach (int i in new[] { 0, s.PointCount - 1 })
                {
                    double e = line.Tile.MinE + s.Points[i * 3], n = line.Tile.MaxN - s.Points[i * 3 + 2];
                    var key = ((long)Math.Floor(e / Cell), (long)Math.Floor(n / Cell));
                    if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = new();
                    list.Add((e, n));
                }
            }
        }

        public double Distance(double e, double n)
        {
            double best = double.PositiveInfinity;
            long ce = (long)Math.Floor(e / Cell), cn = (long)Math.Floor(n / Cell);
            for (long a = ce - 1; a <= ce + 1; a++)
                for (long b = cn - 1; b <= cn + 1; b++)
                    if (_buckets.TryGetValue((a, b), out var list))
                        foreach (var (pe, pn) in list)
                            best = Math.Min(best, Math.Sqrt((pe - e) * (pe - e) + (pn - n) * (pn - n)));
            return best;
        }
    }
}
