namespace UnitSport.Tools.RoadGen;

using UnitSport.Tools.RoadGen.Diagnostics;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

public sealed record PipelineOptions(
    /// <summary>Off reproduces the current renderer: raw polylines, bisector offsets, no trims.</summary>
    bool Smooth = true,
    bool BuildJunctions = true,
    double SimplifyTolerance = 0.6,
    double ChordTolerance = 0.05,
    double MinStep = 0.5,
    double MaxStep = 25.0,
    double SnapTolerance = 0.75,
    bool StopLines = true,
    /// <summary>Off skips the overlap rasterisation, which dominates the cost on a large run.</summary>
    bool Analyze = true);

public sealed class RoadGenResult
{
    public required RoadNetwork Network { get; init; }
    public required List<Ribbon> Ribbons { get; init; }
    public required List<Junction> Junctions { get; init; }
    public required Dictionary<int, List<MarkingLine>> Markings { get; init; }
    public required QualityReport Report { get; init; }
    public required NetworkBuilder.Stats GraphStats { get; init; }
}

/// <summary>
/// The whole thing, in the order the steps have to happen.
///
/// <para>
/// The ordering is not arbitrary and getting it wrong is subtle: junction trims must be
/// computed from the <i>smoothed</i> headings, because rounding a corner changes the direction
/// a road leaves a node by exactly the amount that was rounded. Trim first and you cut the
/// road back along a heading it no longer has, which leaves a wedge of bare ground on one side
/// of every arm.
/// </para>
/// </summary>
public static class Pipeline
{
    public static RoadGenResult Run(RoadNetwork net, PipelineOptions? options = null)
    {
        var opts = options ?? new PipelineOptions();

        var graphStats = new NetworkBuilder
        {
            SnapTolerance = opts.SnapTolerance,
            SplitAtTJunctions = true,
        }.Build(net);

        if (opts.Smooth)
            foreach (var link in net.Links)
                link.Alignment = link.AllowSmoothing
                    ? AlignmentBuilder.FromPolyline(
                        link.Centreline, link.Profile.CurveStyle(opts.SimplifyTolerance))
                    // frozen shape, but still a real alignment so it can be trimmed like any other
                    : AlignmentBuilder.Polygonal(link.Centreline);

        NetworkBuilder.RefreshHeadings(net);

        var junctions = opts.BuildJunctions && opts.Smooth
            ? new JunctionBuilder().Build(net)
            : new List<Junction>();

        var ribbons = new List<Ribbon>();
        var markings = new Dictionary<int, List<MarkingLine>>();

        foreach (var link in net.Links)
        {
            var ribbon = opts.Smooth
                ? RibbonBuilder.Build(link, opts.ChordTolerance, opts.MinStep, opts.MaxStep)
                : BuildPolygonalRibbon(link);

            if (ribbon.IsEmpty) continue;
            ribbons.Add(ribbon);

            if (!opts.Smooth) continue;

            var lines = MarkingBuilder.Build(link,
                stopLineAtStart: opts.StopLines && NeedsStopLine(net, link, atStart: true),
                stopLineAtEnd: opts.StopLines && NeedsStopLine(net, link, atStart: false));
            if (lines.Count > 0) markings[link.Id] = lines;
        }

        var report = (opts.Analyze
                ? QualityAnalyzer.Analyze(ribbons, junctions, net.Links.Count, net.Nodes.Count,
                    nodePositions: net.Nodes.Select(n => n.Position).ToList())
                : QualityAnalyzer.Summarize(ribbons, junctions, net.Links.Count, net.Nodes.Count))
            with
        {
            WorstCurvatureJump = net.Links.Max(l => l.Alignment?.WorstCurvatureJump() ?? 0),
            MarkingRuns = markings.Values.Sum(v => v.Count),
        };

        return new RoadGenResult
        {
            Network = net,
            Ribbons = ribbons,
            Junctions = junctions,
            Markings = markings,
            Report = report,
            GraphStats = graphStats,
        };
    }

    /// <summary>
    /// The minor road stops, the major one does not. Priority comes from the road class, which
    /// is the only signal swissTLM3D gives — it records no priority, no signals and no signs.
    /// </summary>
    private static bool NeedsStopLine(RoadNetwork net, RoadLink link, bool atStart)
    {
        int nodeId = atStart ? link.StartNode : link.EndNode;
        if (nodeId < 0 || nodeId >= net.Nodes.Count) return false;

        var node = net.Nodes[nodeId];
        if (!node.IsJunction) return false;

        int best = node.Approaches.Max(a => net.Links[a.LinkId].Profile.Priority);
        return link.Profile.Priority < best;
    }

    /// <summary>
    /// Reproduces the existing runtime mesher: offset every vertex along the bisector of its
    /// neighbours, full length, no junction awareness. Kept so the report can put a real
    /// before-and-after number on the same input rather than asserting an improvement.
    /// </summary>
    private static Ribbon BuildPolygonalRibbon(RoadLink link)
    {
        var ribbon = new Ribbon { LinkId = link.Id, Profile = link.Profile, Layer = link.Layer };
        var pts = link.Centreline;
        if (pts.Count < 2) return ribbon;

        double half = link.Profile.HalfWidth;
        double travelled = 0;

        for (int i = 0; i < pts.Count; i++)
        {
            var forward = i == 0 ? pts[1] - pts[0]
                : i == pts.Count - 1 ? pts[^1] - pts[^2]
                : pts[i + 1] - pts[i - 1];
            if (forward.LengthSquared < 1e-12) forward = new Vec2(1, 0);
            forward = forward.Normalized();

            if (i > 0) travelled += pts[i].DistanceTo(pts[i - 1]);

            ribbon.Stations.Add(new AlignmentSample(pts[i], forward.Heading, 0, travelled));
            ribbon.Left.Add(pts[i] + forward.Perp * half);
            ribbon.Right.Add(pts[i] - forward.Perp * half);
        }

        return ribbon;
    }
}
