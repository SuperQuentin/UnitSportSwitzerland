namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// What the gantries of lane signs are decided from (#1110, <c>docs/notes/tools/direction-signs.md</c>): in canton Geneva SITG's
/// lane signs (<see cref="SitgGantries"/>), elsewhere the inference rule; the region's places for the signs' destinations; the
/// rule's facts of every lit approach, kept for <c>--gantry-benchmark</c>.
/// </summary>
public sealed class GantryContext
{
    public SitgGantries? Sitg { get; init; }
    public GantryRule Rule { get; init; } = GantryRule.Default;
    public IReadOnlyList<Place> Places { get; init; } = [];
    /// <summary>Every lit approach's facts, with what was built there (rows of <c>gantry_facts.tsv</c>).</summary>
    public ConcurrentQueue<string> Facts { get; } = new();

    public static GantryContext Load(string? sitgPath, string? placesPath) => new()
    {
        Sitg = SitgGantries.Load(sitgPath),
        Places = placesPath is not null && File.Exists(placesPath)
            ? PlaceIndex.FromJson(File.ReadAllText(placesPath)).Places.Where(p => p.Kind == PlaceKind.Town && p.Buildings >= 40).ToList()
            : [],
    };

    /// <summary>Writes the facts (<c>gantry_facts.tsv</c>), sorted so two runs give the same file.</summary>
    public void WriteFacts(string path)
    {
        var rows = Facts.ToList();
        rows.Sort(StringComparer.Ordinal);
        File.WriteAllLines(path, new[] { "# gantry_facts v1: every lit approach (#1110); built: none, rule, sitg",
            GantryFacts.Header + "\tcanton\tbuilt\truns\tsitg_signs\tcolours (rule, SITG)" }.Concat(rows));
    }
}

public static partial class TileRewriter
{
    /// <summary>A gantry's posts stand this far out from the signal poles' feet, and this far before them along the arm (m), tried in steps.</summary>
    private static readonly double[] GantryOut = [0.8, 1.2, 0.5], GantryBack = [2.0, 3.0, 4.5, 1.2];
    /// <summary>The signs' black band's lower edge over the road (m; SSV Art. 71: 4.50-5.50, Art. 103: 4.50 m clear over a carriageway).</summary>
    private const float GantryLowerEdge = 5.0f;
    /// <summary>A sign's destination is a town ahead within this angle of its lanes' way (deg), this near and far (m).</summary>
    private const double PlaceCone = 30, PlaceNear = 1500, PlaceFar = 30000;
    /// <summary>A sign is green where a motorway, an expressway or a ramp lies within this of its lanes' way (deg) and this near (m).</summary>
    private const double MotorwayCone = 45, MotorwayNear = 2000;

    /// <summary>The block's motorway, expressway and ramp points (LV95), for the rule's distance and the signs' colour.</summary>
    private static readonly ConditionalWeakTable<RoadGenResult, List<Vec2>> MotorwayPoints = new();

    private static List<Vec2> MotorwaysOf(RoadGenResult result) => MotorwayPoints.GetValue(result, r =>
    {
        var points = new List<Vec2>();
        foreach (var link in r.Network.Links)
            if (link.Tag is Source { Segment.Class: RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp } s)
                for (int i = 0; i < s.Plan.Length; i += 2) points.Add(s.Plan[i]);
        return points;
    });

    /// <summary>A bearing in degrees from north, clockwise, of a plan-view direction.</summary>
    private static double BearingOf(Vec2 d) => (Math.Atan2(d.X, d.Y) * 180 / Math.PI + 360) % 360;

    /// <summary>
    /// The approach's gantry of lane signs (#1110), or null (counted) where it gets none: in canton Geneva where SITG has lane signs
    /// over it, elsewhere where <see cref="GantryRule"/> wants one; then two posts out beside the signal poles a little before
    /// them, a sign over each run of lanes going the same way, a horizontal head in each lane's band (<see cref="SignGantry.Lit"/>).
    /// The approach's facts go to <see cref="GantryContext.Facts"/> either way.
    /// </summary>
    private static SignGantry? PlaceGantry(GantryContext ctx, JunctionPlan jp, ArmExterior ex, byte arm, double stop, Func<double, double, Vec2> P,
        Vec2 u, Vec2 right, (Vec2 Foot, float Y)? main, (Vec2 Foot, float Y)? far, string? canton, RoadNetwork net, TileId home,
        Func<Vec2, float> height, Func<Vec2, bool> clear, RoadGenResult result, LayoutWriteStats stats)
    {
        var a = ex.Arm;
        var sec = ExteriorPlanner.SectionAt(a, stop);
        var lanes = sec.CarLanes(StripWay.In).Select(l => sec.Span(l.Index) is var (f, t) ? (Mid: (f + t) * 0.5, Width: t - f, l.Strip.Moves) : default)
            .OrderBy(l => l.Mid).ToList();
        if (lanes.Count == 0) return null;
        var origin = P(0, 0);
        var at = P(stop, (lanes[0].Mid - lanes[0].Width * 0.5 + lanes[^1].Mid + lanes[^1].Width * 0.5) * 0.5);
        double bearing = BearingOf(u * -1);
        var info = InfoOf(net.Links[a.LinkId]);
        var motorways = MotorwaysOf(result);
        double motorwayM = motorways.Count == 0 ? double.MaxValue : Math.Sqrt(motorways.Min(m => m.DistanceSquaredTo(at)));
        var facts = new GantryFacts(at.X, at.Y, bearing, lanes.Count, a.Normal.CarLanes(StripWay.In).Count(),
            lanes.Select(l => l.Moves).Distinct().Count(), info is { } i1 ? i1.Attributes.Priority >> 4 : 0,
            info is { } i2 ? i2.Attributes.Has(RoadAttrFlags.OwnerFederal) ? 2 : i2.Attributes.Has(RoadAttrFlags.OwnerCanton) ? 1 : 0 : 0,
            info?.Class ?? RoadClass.Unknown, Math.Min(motorwayM, 99999));

        // Geneva: SITG's lane signs decide; elsewhere the rule
        bool geneva = canton == "GE" && ctx.Sitg is not null;
        var sitg = geneva ? ctx.Sitg!.At(at.X, at.Y, bearing) : null;
        bool wanted = geneva ? sitg is not null : ctx.Rule.Wants(facts);
        string Row(string built, string check = "\t\t") => facts.ToTsv() + "\t" + (canton ?? "") + "\t" + built + "\t" + check;
        if (!wanted) { ctx.Facts.Enqueue(Row("none")); return null; }

        // the posts: out beside the poles' feet, a little before them along the arm, on clear ground
        Vec2? Post((Vec2 Foot, float Y)? pole, Vec2 outward)
        {
            if (pole is not var (foot, _)) return null;
            foreach (double back in GantryBack)
                foreach (double off in GantryOut)
                    if (foot + u * back + outward * off is var q && clear(q)) return q;
            return null;
        }
        if (Post(main, right) is not { } pa || Post(far, right * -1) is not { } pb)
        {
            stats.GantriesRejected++;
            ctx.Facts.Enqueue(Row("none"));
            return null;
        }
        double Off(Vec2 q) => (q - origin).Dot(right);
        var dir = pb - pa;
        double length = Math.Sqrt(dir.Dot(dir));
        dir = dir * (1 / length);
        double across = dir.Dot(right);
        float Along(double offset) => (float)((offset - Off(pa)) / across);

        // a sign over each run of lanes going the same way, its text and colour from SITG's sign over it or from where it leads
        var runs = GantryLayout.Runs(lanes);
        var sitgSigns = sitg is null ? null : SitgGantries.LeftToRight(sitg);
        if (sitgSigns is not null && sitgSigns.Count > runs.Count && sitgSigns.Count(s => s.Osr == "4.41") >= runs.Count)
            sitgSigns = sitgSigns.Where(s => s.Osr == "4.41").ToList();   // the minor roads' signs go first where too many
        var assign = sitgSigns is null ? null : SitgGantries.Assign(runs.Count, sitgSigns.Count);
        var signs = new List<GantrySign>();
        var colours = new List<string>();
        for (int r = 0; r < runs.Count; r++)
        {
            var (mid, width) = GantryLayout.Sign(lanes, runs[r]);
            var moves = lanes[runs[r].From].Moves;
            SignColour colour;
            List<string> lines;
            if (sitgSigns is not null)
            {
                var s = sitgSigns[assign![r]];
                var (motorway, names) = SignText.Parse(s.Text);
                colour = motorway ? SignColour.Green : s.Osr == "4.42" ? SignColour.White : SignColour.Blue;
                lines = names.Count == 0 ? [] : names.Select((n, k) => k == 0 && motorway ? SignGantry.MotorwayMark + n : n).ToList();
            }
            else (colour, lines) = Destinations(ctx, at, bearing, moves, facts, motorways);
            // the benchmark (#1110): the colour the rule's inference would give beside SITG's
            if (sitgSigns is not null) colours.Add($"{"BGW"[(int)Destinations(ctx, at, bearing, moves, facts, motorways).Item1]}{"BGW"[(int)colour]}");
            signs.Add(new GantrySign
            {
                Along = Along(mid), Width = (float)width, Colour = colour, Lines = lines,
                Lanes = Enumerable.Range(runs[r].From, runs[r].To - runs[r].From + 1).Select(k => new GantryLane(Along(lanes[k].Mid), lanes[k].Moves)).ToList(),
            });
        }
        // the band's lower edge over the highest road under the lanes
        float road = lanes.Max(l => Local(home, [P(stop, l.Mid)], height, 0f)[1]);
        var fa = Local(home, [pa], height, 0f);
        var fb = Local(home, [pb], height, 0f);
        ctx.Facts.Enqueue(Row(geneva ? "sitg" : "rule", sitgSigns is null ? "\t\t" : $"{runs.Count}\t{sitgSigns.Count}\t{string.Join(',', colours)}"));
        if (geneva) stats.GantriesSitg++; else stats.GantriesRule++;
        stats.GantrySigns += signs.Count;
        return new SignGantry
        {
            Arm = arm, Lit = true, X = fa[0], Y = fa[1], Z = fa[2], EndX = fb[0], EndY = fb[1], EndZ = fb[2],
            Bottom = road + GantryLowerEdge, Heading = Heading(u), Signs = signs,
        };
    }

    /// <summary>
    /// A sign's colour and destinations where no data names them (#1110): its lanes' way (the turn, else straight on); green with the
    /// motorway symbol where a motorway lies that way within <see cref="MotorwayNear"/> m, else blue on a main road, white on others;
    /// the largest towns ahead that way (<see cref="PlaceCone"/>), two at most.
    /// </summary>
    private static (SignColour, List<string>) Destinations(GantryContext ctx, Vec2 at, double bearing, SignalMoves moves, GantryFacts facts, List<Vec2> motorways)
    {
        double way = (bearing + ((moves & SignalMoves.Through) != 0 ? 0 : (moves & SignalMoves.Left) != 0 ? -90 : (moves & SignalMoves.Right) != 0 ? 90 : 0) + 360) % 360;
        bool motorway = motorways.Any(m => m.DistanceSquaredTo(at) < MotorwayNear * MotorwayNear && SitgGantries.TurnBetween(BearingOf(m - at), way) <= MotorwayCone);
        var towns = ctx.Places
            .Select(p => (p, d: Math.Sqrt((p.E - at.X) * (p.E - at.X) + (p.N - at.Y) * (p.N - at.Y))))
            .Where(x => x.d is >= PlaceNear and <= PlaceFar && SitgGantries.TurnBetween(BearingOf(new Vec2(x.p.E - at.X, x.p.N - at.Y)), way) <= PlaceCone)
            .OrderByDescending(x => x.p.Buildings / Math.Sqrt(x.d)).ThenBy(x => x.p.Name, StringComparer.Ordinal)
            .Select(x => x.p.Name).Distinct().Take(2).ToList();
        if (motorway) return (SignColour.Green, towns.Count == 0 ? [SignGantry.MotorwayMark.ToString()] : towns.Select((n, k) => k == 0 ? SignGantry.MotorwayMark + n : n).ToList());
        return (GantryLayout.Colour(false, facts.MainRoad), towns);
    }
}
