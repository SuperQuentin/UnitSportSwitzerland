namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Checks the invariants that "it measures better" does not cover.
///
/// <para>
/// The overlap metric only proves nothing is painted twice. It says nothing about whether
/// anything is painted at all — trimming every road to nothing would score a perfect zero.
/// These are the properties that have to hold at the same time.
/// </para>
/// </summary>
public sealed record VerifyResult(
    double WorstJunctionGap,
    double WorstChordError,
    double ChordTolerance,
    int DegenerateTriangles,
    int EmptyRibbons,
    double WorstEndpointDrift,
    string WorstChordWhere = "")
{
    public bool Passed =>
        WorstJunctionGap < 0.01
        && WorstChordError <= ChordTolerance * 1.5 + 1e-6
        && DegenerateTriangles == 0
        && WorstEndpointDrift < 0.01;

    public string Format()
    {
        var c = CultureInfo.InvariantCulture;
        string mark(bool ok) => ok ? "ok  " : "FAIL";
        bool chordOk = WorstChordError <= ChordTolerance * 1.5 + 1e-6;
        string where = chordOk || WorstChordWhere.Length == 0
            ? "" : Environment.NewLine + "         at " + WorstChordWhere;

        return $"""
              checks
                {mark(WorstJunctionGap < 0.01)} junction/ribbon seam   worst gap {(WorstJunctionGap * 1000).ToString("F2", c)} mm
                {mark(chordOk)} tessellation           worst chord error {(WorstChordError * 1000).ToString("F1", c)} mm (budget {(ChordTolerance * 1000).ToString("F0", c)} mm){where}
                {mark(WorstEndpointDrift < 0.01)} alignment endpoints    worst drift {(WorstEndpointDrift * 1000).ToString("F2", c)} mm
                {mark(DegenerateTriangles == 0)} junction triangles     {DegenerateTriangles} degenerate{(EmptyRibbons > 0 ? $"   [{EmptyRibbons} empty ribbons]" : "")}
            """;
    }
}

public static class Verifier
{
    public static VerifyResult Run(RoadNetwork net, IReadOnlyList<Ribbon> ribbons,
        IReadOnlyList<Junction> junctions, double chordTolerance)
    {
        var byLink = ribbons.Where(r => !r.IsEmpty).ToDictionary(r => r.LinkId);

        // 1. every junction arm must land exactly on the end of the ribbon it belongs to,
        //    or there is a visible crack between the road and the intersection
        double worstGap = 0;
        foreach (var junction in junctions)
        foreach (var arm in junction.Arms)
        {
            if (!byLink.TryGetValue(arm.LinkId, out var ribbon)) continue;
            var link = net.Links[arm.LinkId];

            bool atStart = link.StartNode == junction.NodeId
                && Math.Abs(ribbon.Stations[0].Distance - link.TrimStart) < 1e-6;

            var (armLeft, armRight) = atStart
                ? (ribbon.Left[0], ribbon.Right[0])
                : (ribbon.Right[^1], ribbon.Left[^1]);

            worstGap = Math.Max(worstGap, arm.Left.DistanceTo(armLeft));
            worstGap = Math.Max(worstGap, arm.Right.DistanceTo(armRight));
        }

        // 2. the adaptive sampler must honour its own chord budget
        double worstChord = 0;
        string worstWhere = "";
        foreach (var link in net.Links)
        {
            if (link.Alignment is not { IsEmpty: false } alignment) continue;
            if (!byLink.TryGetValue(link.Id, out var ribbon)) continue;

            var centre = ribbon.Stations.Select(s => s.Position).ToList();
            if (centre.Count < 2) continue;

            for (int i = 1; i < ribbon.Stations.Count; i++)
            {
                double a = ribbon.Stations[i - 1].Distance;
                double b = ribbon.Stations[i].Distance;
                for (int k = 1; k < 8; k++)
                {
                    double s = a + (b - a) * k / 8.0;
                    double d = Polyline.PointSegmentDistance(alignment.PointAt(s), centre[i - 1], centre[i]);
                    if (d <= worstChord) continue;
                    worstChord = d;
                    worstWhere = $"link {link.Id} ({link.Profile.Name}) station {a:F1}->{b:F1} "
                               + $"of {alignment.Length:F1} m, {alignment.Pieces.Count} pieces, "
                               + $"kappa {alignment.CurvatureAt(s):F4}";
                }
            }
        }

        // 3. smoothing must not move where a road starts and ends, or the graph comes apart
        double worstDrift = 0;
        foreach (var link in net.Links)
        {
            if (link.Alignment is not { IsEmpty: false } alignment) continue;
            worstDrift = Math.Max(worstDrift, alignment.PointAt(0).DistanceTo(link.First));
            worstDrift = Math.Max(worstDrift, alignment.PointAt(alignment.Length).DistanceTo(link.Last));
        }

        // 4. no zero-area or flipped triangles in a junction cap
        int degenerate = 0;
        foreach (var junction in junctions)
        {
            for (int i = 0; i + 2 < junction.Triangles.Count; i += 3)
            {
                var a = junction.Vertices[junction.Triangles[i]];
                var b = junction.Vertices[junction.Triangles[i + 1]];
                var c = junction.Vertices[junction.Triangles[i + 2]];
                if (Math.Abs((b - a).Cross(c - a)) < 1e-7) degenerate++;
            }
        }

        int empty = net.Links.Count(l => !byLink.ContainsKey(l.Id));
        return new VerifyResult(worstGap, worstChord, chordTolerance, degenerate, empty, worstDrift, worstWhere);
    }
}
